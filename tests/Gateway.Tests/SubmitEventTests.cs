using System.Net;
using System.Net.Http.Json;
using System.Text;
using Gateway.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gateway.Tests;

/// <summary>SPEC 10.2 - G1, G2, G4, and the POST /events outcome mapping (SPEC 3.1).</summary>
public sealed class SubmitEventTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public SubmitEventTests()
    {
        _factory = new GatewayFactory(_accountService.Url);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _accountService.Dispose();
    }

    // G1
    [Fact]
    public async Task SubmitEvent_DownstreamApplies_Returns201AndMarksEventApplied()
    {
        var response = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EventResponse>();
        Assert.Equal("evt-1", body!.EventId);
        Assert.Equal("APPLIED", body.Status);
        Assert.Equal(150.00m, body.Amount);
        Assert.Equal(1, _accountService.ApplyCallCount);
    }

    [Fact]
    public async Task SubmitEvent_DownstreamApplies_SendsTransactionToTheAccountsRoute()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var (path, body) = Assert.Single(_accountService.ApplyCalls);

        // The contract between the services (SPEC 4.1): accountId in the route, and the
        // event's own timestamp - not the arrival time - in the body.
        Assert.Equal("/accounts/acct-1/transactions", path);
        Assert.Contains("\"eventId\":\"evt-1\"", body);
        Assert.Contains("\"amount\":150.00", body);
        Assert.Contains("2026-05-15T14:02:11", body);
    }

    // G2 - the duplicate must not reach the Account Service at all.
    [Fact]
    public async Task SubmitEvent_SameEventIdTwice_Returns200WithOriginalAndCallsDownstreamOnce()
    {
        var first = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);
        // Resubmitted with a different amount: the stored event must win, proving the
        // second submission changed nothing.
        var duplicate = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 999.00m);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        var body = await duplicate.Content.ReadFromJsonAsync<EventResponse>();
        Assert.Equal(150.00m, body!.Amount);

        // The point of Gateway-side dedupe: the balance is never touched twice.
        Assert.Equal(1, _accountService.ApplyCallCount);
    }

    [Fact]
    public async Task SubmitEvent_SameEventIdConcurrently_CreatesOneEventAndOnlyEverSendsThatEventId()
    {
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m));

        var responses = await Task.WhenAll(attempts);

        // Exactly one submission created the event; the rest saw it already existed.
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));

        // Note there is deliberately no assertion that the downstream was called once.
        // A request that finds the event already PENDING cannot tell "the previous apply
        // failed" from "the previous apply is still in flight", so it retries, and
        // concurrent duplicates can produce several applies of the same eventId. That is
        // safe by design rather than by luck: the Account Service deduplicates on eventId,
        // so the redundant calls collapse to one effect on the balance (SPEC 5.5). What
        // must hold here is that every call carries the same eventId, which is what makes
        // that collapse possible; the balance itself is asserted end-to-end against the
        // real Account Service in EndToEndIntegrationTests.
        Assert.NotEmpty(_accountService.ApplyCalls);
        Assert.All(_accountService.ApplyCalls, call =>
        {
            Assert.Equal("/accounts/acct-1/transactions", call.Path);
            Assert.Contains("\"eventId\":\"evt-1\"", call.Body);
        });
    }

    [Fact]
    public async Task SubmitEvent_DownstreamReportsReplay_IsStillApplied()
    {
        // 200 from the Account Service means "already applied" - the balance includes it,
        // so the event is APPLIED, not failed (SPEC 3.1 step 4).
        _accountService.RespondApplied(statusCode: 200);

        var response = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EventResponse>();
        Assert.Equal("APPLIED", body!.Status);
    }

    [Fact]
    public async Task SubmitEvent_MetadataSupplied_IsStoredAndReturnedVerbatim()
    {
        var response = await _client.PostJsonAsync("""
            {"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD",
             "eventTimestamp":"2026-05-15T14:02:11Z","metadata":{"source":"mainframe-batch","batchId":"B-9042"}}
            """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");
        Assert.Equal("mainframe-batch", stored!.Metadata!["source"]!.GetValue<string>());
        Assert.Equal("B-9042", stored.Metadata["batchId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SubmitEvent_NoMetadata_ReturnsNullMetadata()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var stored = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");

        Assert.Null(stored!.Metadata);
    }

    // Downstream unreachable: the event survives as PENDING and the client is told it can
    // safely retry (SPEC 3.1 step 4, SPEC 7.1).
    [Fact]
    public async Task SubmitEvent_DownstreamUnavailable_Returns503AndLeavesEventPending()
    {
        _accountService.Stop();

        var response = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("Account Service", problem!.Detail);

        // The event was recorded, so nothing is lost and a resubmission is safe.
        var stored = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");
        Assert.Equal("PENDING", stored!.Status);
    }

    [Fact]
    public async Task SubmitEvent_DownstreamFailing_Returns503RatherThan500()
    {
        _accountService.RespondWithStatus(500);

        var response = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        // A downstream failure is not the Gateway's own failure.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var stored = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");
        Assert.Equal("PENDING", stored!.Status);
    }

    [Fact]
    public async Task SubmitEvent_DownstreamRejectsValidatedEvent_Returns502()
    {
        // The Gateway already validated this event, so a downstream 400 means the two
        // services disagree about the contract - not that the client erred (SPEC 3.1).
        _accountService.RespondWithStatus(400);

        var response = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var stored = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");
        Assert.Equal("PENDING", stored!.Status);
    }

    // G4
    [Theory]
    [InlineData("""{"accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "eventId")]
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "accountId")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "type")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "amount")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"eventTimestamp":"2026-05-15T14:02:11Z"}""", "currency")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD"}""", "eventTimestamp")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"credit","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "type")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"TRANSFER","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "type")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":0,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "amount")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":-5.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "amount")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"not-a-date"}""", "eventTimestamp")]
    [InlineData("""{"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z","metadata":"not-an-object"}""", "metadata")]
    public async Task SubmitEvent_InvalidRequest_Returns400AndNeverCallsDownstream(string body, string expectedField)
    {
        var response = await _client.PostJsonAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.True(problem!.Errors.ContainsKey(expectedField),
            $"Expected an error for '{expectedField}' but got: {string.Join(", ", problem.Errors.Keys)}");

        // Invalid input must never reach the Account Service.
        Assert.Equal(0, _accountService.ApplyCallCount);
    }

    [Fact]
    public async Task SubmitEvent_InvalidRequest_PersistsNothing()
    {
        await _client.PostJsonAsync("""
            {"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":-5.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
            """);

        var stored = await _client.GetAsync("/events/evt-1");

        Assert.Equal(HttpStatusCode.NotFound, stored.StatusCode);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task SubmitEvent_MalformedJson_Returns400WithoutLeakingAnException(string environment)
    {
        using var factory = new GatewayFactory(_accountService.Url, environment);
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/events",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }
}
