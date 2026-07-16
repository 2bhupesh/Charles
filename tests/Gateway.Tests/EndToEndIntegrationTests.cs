using System.Net;
using System.Net.Http.Json;
using AccountService.Contracts;
using Gateway.Contracts;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// SPEC 10.3 - I1. Both real services, no stub in between: the Gateway's own HTTP client
/// calling the Account Service's real pipeline. This is what proves the two idempotency
/// mechanisms compose into an exactly-once effect on the balance.
/// </summary>
public sealed class EndToEndIntegrationTests : IDisposable
{
    private readonly AccountServiceHost _accountService = new();
    private readonly GatewayFactory _gatewayFactory;
    private readonly HttpClient _gateway;
    private readonly HttpClient _accounts;

    public EndToEndIntegrationTests()
    {
        // Touching Server starts the host and gives us a handler that reaches it.
        _accounts = _accountService.CreateClient();

        _gatewayFactory = new GatewayFactory(
            _accountService.Server.BaseAddress.ToString(),
            accountServiceHandler: _accountService.Server.CreateHandler());

        _gateway = _gatewayFactory.CreateClient();
    }

    public void Dispose()
    {
        _gatewayFactory.Dispose();
        _accountService.Dispose();
    }

    // I1
    [Fact]
    public async Task SubmitEvent_ThroughBothRealServices_AppliesOnceAndDuplicateLeavesBalanceUnchanged()
    {
        var created = await _gateway.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var balance = await _accounts.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");
        Assert.Equal(150.00m, balance!.Balance);

        var duplicate = await _gateway.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        // The whole point: submitting twice does not pay twice.
        var afterDuplicate = await _accounts.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");
        Assert.Equal(150.00m, afterDuplicate!.Balance);
    }

    /// <summary>
    /// The composition that matters: under concurrency the Gateway can send the same
    /// eventId downstream more than once (a request cannot distinguish an apply that
    /// failed from one still in flight), and the Account Service's unique index is what
    /// turns those redundant applies into a single effect on the balance. Neither
    /// service's idempotency is sufficient alone - this test fails if either is removed.
    /// </summary>
    [Fact]
    public async Task SubmitEvent_SameEventIdConcurrentlyThroughBothRealServices_CountsTowardsBalanceOnce()
    {
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => _gateway.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m));

        var responses = await Task.WhenAll(attempts);

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));

        var balance = await _accounts.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");
        Assert.Equal(150.00m, balance!.Balance);

        var account = await _accounts.GetFromJsonAsync<AccountDetailsResponse>("/accounts/acct-1");
        Assert.Equal(1, account!.TransactionCount);
    }

    [Fact]
    public async Task SubmitEvents_OutOfOrderThroughBothRealServices_ListsChronologicallyAndBalancesCorrectly()
    {
        await _gateway.SubmitEventAsync("evt-c", "acct-1", "CREDIT", 5.25m, eventTimestamp: "2026-05-15T12:00:00Z");
        await _gateway.SubmitEventAsync("evt-a", "acct-1", "CREDIT", 100.00m, eventTimestamp: "2026-05-15T10:00:00Z");
        await _gateway.SubmitEventAsync("evt-b", "acct-1", "DEBIT", 30.00m, eventTimestamp: "2026-05-15T08:00:00Z");

        var listing = await _gateway.GetFromJsonAsync<EventListResponse>("/events?account=acct-1");
        Assert.Equal(["evt-b", "evt-a", "evt-c"], listing!.Events.Select(e => e.EventId));

        var balance = await _accounts.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");
        Assert.Equal(75.25m, balance!.Balance);
    }

    [Fact]
    public async Task SubmitEvent_ThroughBothRealServices_StoresTheSameTimestampInBothServices()
    {
        await _gateway.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m,
            eventTimestamp: "2026-05-15T14:02:11Z");

        var gatewayEvent = await _gateway.GetFromJsonAsync<EventResponse>("/events/evt-1");
        var account = await _accounts.GetFromJsonAsync<AccountDetailsResponse>("/accounts/acct-1");

        // The services must not disagree about when the event happened.
        var transaction = Assert.Single(account!.RecentTransactions);
        Assert.Equal(gatewayEvent!.EventTimestamp, transaction.EventTimestamp);
    }
}
