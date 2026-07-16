using Xunit;

namespace AccountService.Tests;

/// <summary>SPEC 8.3 / Requirement 4 - a custom metric, exposed for scraping.</summary>
public sealed class MetricsTests : IDisposable
{
    private readonly AccountServiceFactory _factory = new();
    private readonly HttpClient _client;

    public MetricsTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Metrics_AppliedAndReplayed_AreCountedSeparately()
    {
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 150.00m);
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 150.00m);

        var metrics = await _client.GetStringAsync("/metrics");

        Assert.Contains("account_transactions_applied_total", metrics);

        // The ratio of these two is the visible evidence that idempotency is working:
        // replays should be common and must never move a balance.
        Assert.Contains("result=\"applied\"", metrics);
        Assert.Contains("result=\"replayed\"", metrics);
    }

    [Fact]
    public async Task Metrics_ExposesTheRequestDurationHistogram()
    {
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 150.00m);

        var metrics = await _client.GetStringAsync("/metrics");

        Assert.Contains("http_server_request_duration_seconds", metrics);
    }

    [Fact]
    public async Task Metrics_IsScrapableBeforeAnyTrafficHasArrived()
    {
        var response = await _client.GetAsync("/metrics");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("text/plain", response.Content.Headers.ContentType?.MediaType);
    }
}
