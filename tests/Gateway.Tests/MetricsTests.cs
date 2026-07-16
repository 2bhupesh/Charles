using Xunit;

namespace Gateway.Tests;

/// <summary>
/// SPEC 8.3 / Requirement 4 - a custom metric, exposed for scraping. The counters are
/// asserted by outcome rather than merely present: a counter that never moves off zero
/// looks identical to one that is never recorded.
/// </summary>
public sealed class MetricsTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public MetricsTests()
    {
        _factory = new GatewayFactory(_accountService.Url, settings: new Dictionary<string, string>
        {
            ["AccountService:Resilience:Retry:MaxRetryAttempts"] = "1",
            ["AccountService:Resilience:Retry:Delay"] = "00:00:00"
        });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _accountService.Dispose();
    }

    [Fact]
    public async Task Metrics_AfterASuccessfulSubmission_CountsItAsCreated()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var metrics = await _client.GetStringAsync("/metrics");

        Assert.Contains("gateway_events_total", metrics);
        Assert.Contains("outcome=\"created\"", metrics);
    }

    [Fact]
    public async Task Metrics_DuplicateSubmission_IsCountedSeparatelyFromCreated()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var metrics = await _client.GetStringAsync("/metrics");

        // Duplicate delivery is expected traffic, not an error - counting it apart from
        // created is what makes "how much of our load is redelivery" answerable.
        Assert.Contains("outcome=\"created\"", metrics);
        Assert.Contains("outcome=\"duplicate\"", metrics);
    }

    [Fact]
    public async Task Metrics_RejectedAndUnavailable_AreCountedByOutcome()
    {
        await _client.PostJsonAsync("""
            {"eventId":"evt-1","accountId":"acct-1","type":"credit","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
            """);

        _accountService.Stop();
        await _client.SubmitEventAsync("evt-2", "acct-1", "CREDIT", 150.00m);

        var metrics = await _client.GetStringAsync("/metrics");

        Assert.Contains("outcome=\"rejected\"", metrics);
        Assert.Contains("outcome=\"downstream_unavailable\"", metrics);

        // The client-failure counter is what an alert would fire on.
        Assert.Contains("gateway_account_client_failures_total", metrics);
    }

    [Fact]
    public async Task Metrics_ExposesTheRequestDurationHistogram()
    {
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var metrics = await _client.GetStringAsync("/metrics");

        // Request count, latency and error rate all derive from this one histogram.
        Assert.Contains("http_server_request_duration_seconds", metrics);
        Assert.Contains("http_route=\"/events\"", metrics);
    }

    [Fact]
    public async Task Metrics_IsScrapableBeforeAnyTrafficHasArrived()
    {
        var response = await _client.GetAsync("/metrics");

        // A scraper polls from startup; the endpoint must answer, not 404.
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("text/plain", response.Content.Headers.ContentType?.MediaType);
    }
}
