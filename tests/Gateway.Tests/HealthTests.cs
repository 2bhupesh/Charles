using System.Net;
using System.Net.Http.Json;
using Gateway.Contracts;
using Xunit;

namespace Gateway.Tests;

/// <summary>SPEC 3.4 - the Gateway's own health is separate from its downstream's.</summary>
public sealed class HealthTests : IDisposable
{
    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public HealthTests()
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
    public async Task Health_EverythingReachable_Returns200Healthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", health!.Status);
        Assert.Equal("gateway", health.Service);
        Assert.Equal("Healthy", health.Checks["database"]);
        Assert.Equal("Healthy", health.Checks["accountService"]);
    }

    [Fact]
    public async Task Health_AccountServiceDown_Returns200DegradedNotUnhealthy()
    {
        _accountService.Stop();

        // Provoke a failed call so the Gateway learns the downstream is unreachable.
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var response = await _client.GetAsync("/health");

        // 200: the Gateway itself is fine and its reads still work. Reporting 503 would
        // invite an orchestrator to restart a healthy service (SPEC 3.4).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Degraded", health!.Status);
        Assert.Equal("Healthy", health.Checks["database"]);
        Assert.Equal("Degraded", health.Checks["accountService"]);
    }

    [Fact]
    public async Task Health_AccountServiceRecovers_ReportsHealthyAgain()
    {
        _accountService.RespondWithStatus(500);
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        var degraded = await _client.GetFromJsonAsync<HealthResponse>("/health");
        Assert.Equal("Degraded", degraded!.Checks["accountService"]);

        _accountService.RespondApplied();
        await _client.SubmitEventAsync("evt-2", "acct-1", "CREDIT", 150.00m);

        var recovered = await _client.GetFromJsonAsync<HealthResponse>("/health");
        Assert.Equal("Healthy", recovered!.Checks["accountService"]);
    }
}
