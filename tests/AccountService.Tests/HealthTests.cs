using System.Net;
using System.Net.Http.Json;
using AccountService.Contracts;
using Xunit;

namespace AccountService.Tests;

/// <summary>SPEC 10.1 - A6.</summary>
public sealed class HealthTests : IDisposable
{
    private readonly AccountServiceFactory _factory = new();
    private readonly HttpClient _client;

    public HealthTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_DatabaseReachable_Returns200WithDiagnostics()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", health!.Status);
        Assert.Equal("account-service", health.Service);
        Assert.Equal("Healthy", health.Checks["database"]);
    }
}
