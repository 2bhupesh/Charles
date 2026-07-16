using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace AccountService.Tests;

/// <summary>
/// Hosts the real service over a database file unique to each test, so tests are
/// isolated and can run in parallel. A file (not ":memory:") is used because an
/// in-memory SQLite database lives only as long as its connection.
/// </summary>
public sealed class AccountServiceFactory(string environment = "Testing") : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"accounts-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:AccountsDb", $"Data Source={_databasePath}");

        // Configurable because some request-handling behaviour is environment-dependent:
        // minimal APIs only throw on a bad request body in Development.
        builder.UseEnvironment(environment);
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
