using System.Net;
using System.Net.Http.Json;
using Gateway.Contracts;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// SPEC 10.2 - G8. The reason POST /events records the event before calling downstream:
/// a failed apply leaves a PENDING record that the client can drive to completion by
/// resubmitting the same eventId, with no risk of double-counting.
/// </summary>
public sealed class PendingRetryTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public PendingRetryTests()
    {
        _factory = new GatewayFactory(_accountService.Url);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _accountService.Dispose();
    }

    [Fact]
    public async Task SubmitEvent_ResubmittedAfterDownstreamRecovers_IsAppliedAndReturns200()
    {
        _accountService.RespondWithStatus(500);

        var whileDown = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, whileDown.StatusCode);

        var callsWhileDown = _accountService.ApplyCallCount;

        _accountService.RespondApplied();

        var afterRecovery = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        // 200, not 201: the event was created by the first submission, not this one.
        Assert.Equal(HttpStatusCode.OK, afterRecovery.StatusCode);

        var body = await afterRecovery.Content.ReadFromJsonAsync<EventResponse>();
        Assert.Equal("APPLIED", body!.Status);

        // A PENDING event is retried rather than treated as a duplicate, so the downstream
        // is called again - which is only safe because its apply is idempotent (SPEC 4.1).
        Assert.True(_accountService.ApplyCallCount > callsWhileDown,
            "the retry should have reached the Account Service");
    }

    [Fact]
    public async Task SubmitEvent_ResubmittedAfterApplied_IsTreatedAsDuplicateNotRetried()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);
        var callsAfterFirst = _accountService.ApplyCallCount;

        var duplicate = await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        // Once APPLIED, a resubmission is a duplicate: no downstream call at all.
        Assert.Equal(callsAfterFirst, _accountService.ApplyCallCount);
    }

    [Fact]
    public async Task SubmitEvent_PendingEventRetried_KeepsItsOriginalReceivedAt()
    {
        _accountService.RespondWithStatus(500);
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var pending = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");

        _accountService.RespondApplied();
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var applied = await _client.GetFromJsonAsync<EventResponse>("/events/evt-1");

        // receivedAt records when the event first arrived, not when it finally landed.
        Assert.Equal(pending!.ReceivedAt, applied!.ReceivedAt);
    }
}
