using Gateway.Clients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

/// <summary>
/// Hosts the real Gateway against a database file unique to each test and an
/// Account Service base URL of the test's choosing - normally a WireMock server
/// standing in for the downstream, or a real Account Service host for SPEC 10.3.
/// </summary>
/// <param name="accountServiceHandler">
/// Sends the Gateway's downstream calls through this handler instead of the network.
/// Used by the integration test to reach a real in-process Account Service, which has no
/// socket to dial.
/// </param>
/// <param name="settings">
/// Extra configuration, chiefly the AccountService:Resilience:* keys - the reason those
/// live in configuration is so a test can trip the circuit breaker in milliseconds
/// instead of waiting out the production sampling window.
/// </param>
public sealed class GatewayFactory(
    string accountServiceBaseUrl,
    string environment = "Testing",
    HttpMessageHandler? accountServiceHandler = null,
    IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<IGatewayApi>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"events-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:EventsDb", $"Data Source={_databasePath}");
        builder.UseSetting("AccountService:BaseUrl", accountServiceBaseUrl);
        builder.UseEnvironment(environment);

        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            builder.UseSetting(key, value);

        if (accountServiceHandler is null)
            return;

        // Runs after the app's own registration, so this primary handler wins.
        builder.ConfigureServices(services =>
            services
                .AddHttpClient<AccountServiceClient>(client => client.BaseAddress = new Uri(accountServiceBaseUrl))
                .ConfigurePrimaryHttpMessageHandler(() => accountServiceHandler));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        // Pooled connections keep a handle on the file and would block the delete.
        SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
