using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Gateway.Tests;

/// <summary>
/// The real Account Service, hosted in-process for the integration test (SPEC 10.3) -
/// no WireMock, no stubs, the actual service behind the Gateway.
/// </summary>
/// <remarks>
/// Deliberately a copy of AccountService.Tests' factory rather than a reference to it:
/// the two test projects stay independently runnable, mirroring the services they cover.
/// </remarks>
internal sealed class AccountServiceHost : WebApplicationFactory<IAccountServiceApi>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"accounts-integration-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:AccountsDb", $"Data Source={_databasePath}");
        builder.UseEnvironment("Testing");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

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
