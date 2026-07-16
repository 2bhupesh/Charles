using System.Net;
using System.Net.Http.Json;
using AccountService.Contracts;
using Xunit;

namespace AccountService.Tests;

/// <summary>SPEC 10.1 - A1, A2, A3.</summary>
public sealed class ApplyTransactionTests : IDisposable
{
    private readonly AccountServiceFactory _factory = new();
    private readonly HttpClient _client;

    public ApplyTransactionTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    // A1
    [Fact]
    public async Task Balance_CreditThenDebit_IsSumOfCreditsMinusDebits()
    {
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 150.00m);
        await _client.PostTransactionAsync("acct-1", "evt-2", "DEBIT", 24.50m);

        var balance = await _client.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");

        Assert.Equal(125.50m, balance!.Balance);
        Assert.Equal("acct-1", balance.AccountId);
        Assert.Equal("USD", balance.Currency);
    }

    [Fact]
    public async Task ApplyTransaction_FreshEvent_Returns201WithStoredTransaction()
    {
        var response = await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 150.00m);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TransactionResponse>();
        Assert.Equal("evt-1", body!.EventId);
        Assert.Equal("acct-1", body.AccountId);
        Assert.Equal(150.00m, body.Amount);
        Assert.Equal(DateTimeOffset.Parse("2026-05-15T14:02:11Z"), body.EventTimestamp);
    }

    // A2 - the idempotency contract that makes the Gateway's retries safe (SPEC 4.1).
    [Fact]
    public async Task ApplyTransaction_SameEventIdTwice_AppliesOnceAndReturnsOriginal()
    {
        var first = await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 100.00m);
        // Replayed with a different amount: the stored transaction must win, proving
        // the second call changed nothing.
        var replay = await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 999.00m);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var replayBody = await replay.Content.ReadFromJsonAsync<TransactionResponse>();
        Assert.Equal(100.00m, replayBody!.Amount);

        var details = await _client.GetFromJsonAsync<AccountDetailsResponse>("/accounts/acct-1");
        Assert.Equal(1, details!.TransactionCount);
        Assert.Equal(100.00m, details.Balance);
    }

    [Fact]
    public async Task ApplyTransaction_SameEventIdConcurrently_AppliesExactlyOnce()
    {
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 100.00m));

        var responses = await Task.WhenAll(attempts);

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));

        var details = await _client.GetFromJsonAsync<AccountDetailsResponse>("/accounts/acct-1");
        Assert.Equal(1, details!.TransactionCount);
        Assert.Equal(100.00m, details.Balance);
    }

    // A3 - balance is a commutative sum, so arrival order cannot affect it (SPEC 2.3).
    [Fact]
    public async Task Balance_EventsArrivingOutOfOrder_IsCorrect()
    {
        // Arrival order (c, a, b) deliberately differs from chronological order (b, a, c).
        await _client.PostTransactionAsync("acct-1", "evt-c", "CREDIT", 5.25m, eventTimestamp: "2026-05-15T12:00:00Z");
        await _client.PostTransactionAsync("acct-1", "evt-a", "CREDIT", 100.00m, eventTimestamp: "2026-05-15T10:00:00Z");
        await _client.PostTransactionAsync("acct-1", "evt-b", "DEBIT", 30.00m, eventTimestamp: "2026-05-15T08:00:00Z");

        var balance = await _client.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");

        Assert.Equal(75.25m, balance!.Balance);
    }

    // A3 - listings are chronological regardless of arrival order.
    [Fact]
    public async Task GetAccount_EventsArrivingOutOfOrder_ListsRecentTransactionsNewestFirst()
    {
        await _client.PostTransactionAsync("acct-1", "evt-c", "CREDIT", 5.25m, eventTimestamp: "2026-05-15T12:00:00Z");
        await _client.PostTransactionAsync("acct-1", "evt-a", "CREDIT", 100.00m, eventTimestamp: "2026-05-15T10:00:00Z");
        await _client.PostTransactionAsync("acct-1", "evt-b", "DEBIT", 30.00m, eventTimestamp: "2026-05-15T08:00:00Z");

        var details = await _client.GetFromJsonAsync<AccountDetailsResponse>("/accounts/acct-1");

        Assert.Equal(["evt-c", "evt-a", "evt-b"], details!.RecentTransactions.Select(t => t.EventId));
    }

    [Fact]
    public async Task GetAccount_MoreThanTenTransactions_ReturnsTenNewest()
    {
        for (var hour = 0; hour < 12; hour++)
        {
            await _client.PostTransactionAsync(
                "acct-1", $"evt-{hour:D2}", "CREDIT", 1.00m,
                eventTimestamp: $"2026-05-15T{hour:D2}:00:00Z");
        }

        var details = await _client.GetFromJsonAsync<AccountDetailsResponse>("/accounts/acct-1");

        Assert.Equal(12, details!.TransactionCount);
        Assert.Equal(12.00m, details.Balance);
        Assert.Equal(10, details.RecentTransactions.Count);
        Assert.Equal("evt-11", details.RecentTransactions[0].EventId);
        Assert.Equal("evt-02", details.RecentTransactions[^1].EventId);
    }

    [Fact]
    public async Task Balance_TransactionsForOtherAccounts_AreNotCounted()
    {
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 100.00m);
        await _client.PostTransactionAsync("acct-2", "evt-2", "CREDIT", 500.00m);

        var balance = await _client.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");

        Assert.Equal(100.00m, balance!.Balance);
    }

    [Fact]
    public async Task Balance_DebitsExceedingCredits_GoesNegative()
    {
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 10.00m);
        await _client.PostTransactionAsync("acct-1", "evt-2", "DEBIT", 25.00m);

        var balance = await _client.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");

        // No overdraft rule in v1 (SPEC 2.3).
        Assert.Equal(-15.00m, balance!.Balance);
    }

    [Fact]
    public async Task Balance_AmountsWithFractionalCents_StaysExact()
    {
        // Would drift if amounts round-tripped through a float.
        await _client.PostTransactionAsync("acct-1", "evt-1", "CREDIT", 0.10m);
        await _client.PostTransactionAsync("acct-1", "evt-2", "CREDIT", 0.20m);

        var balance = await _client.GetFromJsonAsync<BalanceResponse>("/accounts/acct-1/balance");

        Assert.Equal(0.30m, balance!.Balance);
    }
}
