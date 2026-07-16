using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Gateway.Tests;

/// <summary>
/// A stand-in Account Service (SPEC 10.2). Lets a test dictate what the downstream does -
/// applies, rejects, fails, or vanishes - and observe exactly what the Gateway sent it.
/// </summary>
internal sealed class FakeAccountService : IDisposable
{
    private const string TransactionsPath = "/accounts/*/transactions";

    private readonly WireMockServer _server = WireMockServer.Start();

    public FakeAccountService() => RespondApplied();

    public string Url => _server.Url!;

    /// <summary>Every transaction POST the Gateway has made, newest last.</summary>
    public IReadOnlyList<(string Path, string? Body)> ApplyCalls =>
        _server.LogEntries
            .Select(entry => entry.RequestMessage)
            .Where(request => request is { Method: "POST" }
                              && request.Path.EndsWith("/transactions", StringComparison.Ordinal))
            // The Where above already excluded nulls; the compiler cannot see that across it.
            .Select(request => (request!.Path, request.Body))
            .ToList();

    public int ApplyCallCount => ApplyCalls.Count;

    /// <summary>
    /// The W3C traceparent header on each transaction POST, which is how the Gateway's
    /// trace reaches the Account Service (SPEC 8.1).
    /// </summary>
    public IReadOnlyList<string?> ApplyTraceParents =>
        _server.LogEntries
            .Select(entry => entry.RequestMessage)
            .Where(request => request is { Method: "POST" }
                              && request.Path.EndsWith("/transactions", StringComparison.Ordinal))
            .Select(request => request!.Headers?
                .FirstOrDefault(header => string.Equals(header.Key, "traceparent", StringComparison.OrdinalIgnoreCase))
                .Value?
                .FirstOrDefault())
            .ToList();

    /// <summary>201 for a fresh apply, or 200 to imitate a replay of an already-applied event.</summary>
    public void RespondApplied(int statusCode = 201)
    {
        _server.ResetMappings();
        _server
            .Given(Request.Create().WithPath(new WildcardMatcher(TransactionsPath)).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z","appliedAt":"2026-07-16T09:00:01Z"}
                    """));
    }

    /// <summary>The downstream is up but failing.</summary>
    public void RespondWithStatus(int statusCode)
    {
        _server.ResetMappings();
        _server
            .Given(Request.Create().WithPath(new WildcardMatcher(TransactionsPath)).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(statusCode));
    }

    /// <summary>
    /// Fails the first call and applies every one after it - a transient blip, expressed as
    /// a state transition rather than a sleep, so the retry test cannot flake on timing.
    /// </summary>
    public void RespondFailingOnceThenApplied()
    {
        _server.ResetMappings();
        _server.ResetScenarios();

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher(TransactionsPath)).UsingPost())
            .InScenario("transient-blip")
            .WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(500));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher(TransactionsPath)).UsingPost())
            .InScenario("transient-blip")
            .WhenStateIs("recovered")
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {"eventId":"evt-1","accountId":"acct-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z","appliedAt":"2026-07-16T09:00:01Z"}
                    """));
    }

    /// <summary>Takes the downstream away entirely; its port then refuses connections.</summary>
    public void Stop() => _server.Stop();

    public void Dispose() => _server.Dispose();
}
