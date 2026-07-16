using System.Net.Http.Json;
using System.Text;

namespace Gateway.Tests;

internal static class TestClientExtensions
{
    public const string DefaultTimestamp = "2026-05-15T14:02:11Z";

    public static Task<HttpResponseMessage> SubmitEventAsync(
        this HttpClient client,
        string eventId,
        string accountId,
        string type,
        decimal amount,
        string currency = "USD",
        string eventTimestamp = DefaultTimestamp) =>
        client.PostAsJsonAsync("/events", new { eventId, accountId, type, amount, currency, eventTimestamp });

    /// <summary>Posts a raw body, for cases a typed object cannot express - missing
    /// fields, wrong types, malformed JSON.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string body) =>
        client.PostAsync("/events", new StringContent(body, Encoding.UTF8, "application/json"));
}
