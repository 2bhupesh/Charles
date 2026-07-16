using System.Net.Http.Json;

namespace AccountService.Tests;

internal static class TestClientExtensions
{
    public const string DefaultTimestamp = "2026-05-15T14:02:11Z";

    public static Task<HttpResponseMessage> PostTransactionAsync(
        this HttpClient client,
        string accountId,
        string eventId,
        string type,
        decimal amount,
        string currency = "USD",
        string eventTimestamp = DefaultTimestamp) =>
        client.PostAsJsonAsync(
            $"/accounts/{accountId}/transactions",
            new { eventId, type, amount, currency, eventTimestamp });
}
