using System.Net;
using System.Net.Http.Json;
using Gateway.Contracts;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// SPEC 10.2 - G5 (retry) and G6 (circuit breaker). Each test drives the pipeline through
/// configuration rather than trusting the defaults, which also proves the SPEC 7 settings
/// are actually bound from configuration and not silently ignored.
/// </summary>
public sealed class ResilienceTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly List<GatewayFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();

        _accountService.Dispose();
    }

    /// <summary>
    /// Retries are on, the breaker is effectively off (a throughput no test will reach),
    /// so failures exercise the retry strategy in isolation.
    /// </summary>
    /// <remarks>
    /// The timeouts are deliberately far longer than production's. These tests count
    /// downstream attempts, and the real 2s attempt timeout makes that count depend on how
    /// quickly a loaded CI machine can answer: an attempt that times out before reaching
    /// WireMock is a retry that never shows up in the count. Taking timeouts out of the
    /// picture leaves the test measuring only what it is about. Timeout behaviour is
    /// covered separately, by the tests that assert 503s rather than counts.
    /// </remarks>
    private HttpClient CreateClientWithRetries(int maxRetryAttempts) =>
        CreateClient(new Dictionary<string, string>
        {
            ["AccountService:Resilience:Retry:MaxRetryAttempts"] = maxRetryAttempts.ToString(),
            ["AccountService:Resilience:Retry:Delay"] = "00:00:00",
            ["AccountService:Resilience:Retry:UseJitter"] = "false",
            ["AccountService:Resilience:AttemptTimeout:Timeout"] = "00:00:30",
            ["AccountService:Resilience:TotalRequestTimeout:Timeout"] = "00:01:00",
            ["AccountService:Resilience:CircuitBreaker:SamplingDuration"] = "00:02:00",
            ["AccountService:Resilience:CircuitBreaker:MinimumThroughput"] = "1000"
        });

    /// <summary>
    /// A breaker that opens after two failed attempts. One retry per submission (the
    /// minimum the options allow) means the first failing submission makes exactly two
    /// attempts and trips the breaker, so any call after that is unambiguously the
    /// breaker's doing.
    /// </summary>
    private HttpClient CreateClientWithBreaker(string breakDuration = "00:00:30") =>
        CreateClient(new Dictionary<string, string>
        {
            ["AccountService:Resilience:Retry:MaxRetryAttempts"] = "1",
            ["AccountService:Resilience:Retry:Delay"] = "00:00:00",
            ["AccountService:Resilience:Retry:UseJitter"] = "false",
            // Long, for the same reason as above: the failures that trip the breaker here
            // should be WireMock's 500s, not incidental timeouts on a busy machine.
            ["AccountService:Resilience:AttemptTimeout:Timeout"] = "00:00:30",
            ["AccountService:Resilience:TotalRequestTimeout:Timeout"] = "00:01:00",
            ["AccountService:Resilience:CircuitBreaker:MinimumThroughput"] = "2",
            ["AccountService:Resilience:CircuitBreaker:FailureRatio"] = "0.5",
            ["AccountService:Resilience:CircuitBreaker:SamplingDuration"] = "00:02:00",
            ["AccountService:Resilience:CircuitBreaker:BreakDuration"] = breakDuration
        });

    private HttpClient CreateClient(Dictionary<string, string> settings)
    {
        var factory = new GatewayFactory(_accountService.Url, settings: settings);
        _factories.Add(factory);
        return factory.CreateClient();
    }

    // G5 - a failing downstream is retried, and the retries are bounded.
    [Fact]
    public async Task SubmitEvent_DownstreamFailing_RetriesThenReturns503WithEventPending()
    {
        _accountService.RespondWithStatus(500);
        var client = CreateClientWithRetries(maxRetryAttempts: 3);

        var response = await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // 1 initial attempt + 3 retries. The exact number matters: retrying forever would
        // hang the client, and not retrying at all would fail on the first transient blip.
        Assert.Equal(4, _accountService.ApplyCallCount);

        var stored = await client.GetFromJsonAsync<EventResponse>("/events/evt-1");
        Assert.Equal("PENDING", stored!.Status);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(4, 5)]
    public async Task SubmitEvent_DownstreamFailing_MakesExactlyOneAttemptPlusConfiguredRetries(
        int maxRetryAttempts, int expectedCalls)
    {
        _accountService.RespondWithStatus(500);
        var client = CreateClientWithRetries(maxRetryAttempts);

        await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        // Proves the SPEC 7 values are read from configuration rather than defaulted.
        Assert.Equal(expectedCalls, _accountService.ApplyCallCount);
    }

    [Fact]
    public async Task SubmitEvent_TransientFailureThenSuccess_IsRetriedIntoSuccess()
    {
        // The case retry exists for: the downstream blips once, the retry lands, and the
        // client never learns anything went wrong.
        _accountService.RespondFailingOnceThenApplied();
        var client = CreateClientWithRetries(maxRetryAttempts: 3);

        var response = await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, _accountService.ApplyCallCount);

        var stored = await client.GetFromJsonAsync<EventResponse>("/events/evt-1");
        Assert.Equal("APPLIED", stored!.Status);
    }

    [Fact]
    public async Task SubmitEvent_DownstreamRejectsWith400_IsNotRetried()
    {
        // SPEC 7: never retry 4xx. A rejected transaction will be rejected again, so
        // retrying only wastes the downstream's capacity.
        _accountService.RespondWithStatus(400);
        var client = CreateClientWithRetries(maxRetryAttempts: 3);

        var response = await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, _accountService.ApplyCallCount);
    }

    // G6 - sustained failure opens the circuit, and the open circuit fails fast.
    [Fact]
    public async Task SubmitEvent_FailuresExceedBreakerThreshold_OpensCircuitAndFailsFast()
    {
        _accountService.RespondWithStatus(500);
        var client = CreateClientWithBreaker();

        // One submission = one attempt + one retry = two failures, which reaches the
        // threshold (MinimumThroughput 2, FailureRatio 0.5) and opens the circuit.
        await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var callsBeforeOpen = _accountService.ApplyCallCount;
        Assert.Equal(2, callsBeforeOpen);

        var whileOpen = await client.SubmitEventAsync("evt-2", "acct-1", "CREDIT", 150.00m);

        // Still a clean 503 for the client...
        Assert.Equal(HttpStatusCode.ServiceUnavailable, whileOpen.StatusCode);

        // ...but the request never left the Gateway. That is the whole point of the
        // breaker: stop hammering a downstream that is already failing.
        Assert.Equal(callsBeforeOpen, _accountService.ApplyCallCount);

        // Failing fast must not cost the event - it is still recorded and resubmittable.
        var stored = await client.GetFromJsonAsync<EventResponse>("/events/evt-2");
        Assert.Equal("PENDING", stored!.Status);
    }

    [Fact]
    public async Task Reads_CircuitOpen_AreUnaffected()
    {
        _accountService.RespondApplied();
        var client = CreateClientWithBreaker();
        await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        _accountService.RespondWithStatus(500);
        await client.SubmitEventAsync("evt-2", "acct-1", "CREDIT", 150.00m);

        // Local reads never touch the downstream, so an open circuit is irrelevant to them.
        var listing = await client.GetAsync("/events?account=acct-1");
        var single = await client.GetAsync("/events/evt-1");

        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
    }

    [Fact]
    public async Task SubmitEvent_AfterBreakDurationAndRecovery_IsAppliedAgain()
    {
        _accountService.RespondWithStatus(500);
        var client = CreateClientWithBreaker(breakDuration: "00:00:01");

        await client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var openCircuit = await client.SubmitEventAsync("evt-2", "acct-1", "CREDIT", 150.00m);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, openCircuit.StatusCode);

        _accountService.RespondApplied();

        // The breaker must let traffic back through once the downstream has had a moment
        // to recover, otherwise a blip would take the Gateway down with it.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var afterRecovery = await client.SubmitEventAsync("evt-3", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.Created, afterRecovery.StatusCode);
    }
}
