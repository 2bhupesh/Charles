using System.Net;
using System.Net.Http.Json;
using Gateway.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Gateway.Tests;

/// <summary>SPEC 10.2 - G3 (chronological listing) and G7 (reads survive a downstream outage).</summary>
public sealed class EventQueryTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public EventQueryTests()
    {
        _factory = new GatewayFactory(_accountService.Url);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _accountService.Dispose();
    }

    // G3 - arrival order (c, a, b) deliberately differs from chronological order (b, a, c).
    [Fact]
    public async Task ListEvents_SubmittedOutOfOrder_ReturnsChronologicalOrder()
    {
        await _client.SubmitEventAsync("evt-c", "acct-1", "CREDIT", 5.25m, eventTimestamp: "2026-05-15T12:00:00Z");
        await _client.SubmitEventAsync("evt-a", "acct-1", "CREDIT", 100.00m, eventTimestamp: "2026-05-15T10:00:00Z");
        await _client.SubmitEventAsync("evt-b", "acct-1", "DEBIT", 30.00m, eventTimestamp: "2026-05-15T08:00:00Z");

        var listing = await _client.GetFromJsonAsync<EventListResponse>("/events?account=acct-1");

        Assert.Equal(["evt-b", "evt-a", "evt-c"], listing!.Events.Select(e => e.EventId));
        Assert.Equal("acct-1", listing.AccountId);
    }

    [Fact]
    public async Task ListEvents_EqualTimestamps_FallsBackToArrivalOrder()
    {
        // Same eventTimestamp: order is otherwise undefined, so receivedAt breaks the tie
        // and keeps the listing stable (SPEC 3.3).
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 1.00m, eventTimestamp: "2026-05-15T10:00:00Z");
        await _client.SubmitEventAsync("evt-2", "acct-1", "CREDIT", 2.00m, eventTimestamp: "2026-05-15T10:00:00Z");

        var listing = await _client.GetFromJsonAsync<EventListResponse>("/events?account=acct-1");

        Assert.Equal(["evt-1", "evt-2"], listing!.Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task ListEvents_OtherAccountsHaveEvents_ReturnsOnlyTheRequestedAccount()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 100.00m);
        await _client.SubmitEventAsync("evt-2", "acct-2", "CREDIT", 500.00m);

        var listing = await _client.GetFromJsonAsync<EventListResponse>("/events?account=acct-1");

        Assert.Equal(["evt-1"], listing!.Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task ListEvents_AccountWithNoEvents_ReturnsEmptyList()
    {
        var listing = await _client.GetFromJsonAsync<EventListResponse>("/events?account=nobody");

        Assert.Empty(listing!.Events);
    }

    [Theory]
    [InlineData("/events")]
    [InlineData("/events?account=")]
    public async Task ListEvents_MissingAccountParameter_Returns400(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.Contains("account", problem!.Errors.Keys);
    }

    [Fact]
    public async Task GetEvent_UnknownId_Returns404ProblemDetails()
    {
        var response = await _client.GetAsync("/events/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("nope", problem!.Detail);
    }

    // G7 - the reads depend only on the Gateway's own data, so an Account Service outage
    // must not touch them (SPEC 7.1).
    [Fact]
    public async Task Reads_AccountServiceDown_StillSucceed()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 100.00m);

        _accountService.Stop();

        var single = await _client.GetAsync("/events/evt-1");
        var listing = await _client.GetAsync("/events?account=acct-1");

        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);

        var events = await listing.Content.ReadFromJsonAsync<EventListResponse>();
        Assert.Equal(["evt-1"], events!.Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task Reads_EventRecordedWhileDownstreamWasDown_AreStillReadable()
    {
        _accountService.Stop();

        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 100.00m);

        var listing = await _client.GetFromJsonAsync<EventListResponse>("/events?account=acct-1");

        // A PENDING event is still an event the client can see.
        var stored = Assert.Single(listing!.Events);
        Assert.Equal("PENDING", stored.Status);
    }
}
