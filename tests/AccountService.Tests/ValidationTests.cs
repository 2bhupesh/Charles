using System.Net;
using System.Net.Http.Json;
using System.Text;
using AccountService.Contracts;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AccountService.Tests;

/// <summary>SPEC 10.1 - A4. Validation rules from SPEC 4.1, errors per SPEC 6.</summary>
public sealed class ValidationTests : IDisposable
{
    private const string ValidBody = """
        {"eventId":"evt-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
        """;

    private readonly AccountServiceFactory _factory = new();
    private readonly HttpClient _client;

    public ValidationTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Theory]
    // Missing required fields.
    [InlineData("""{"type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "eventId")]
    [InlineData("""{"eventId":"evt-1","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "type")]
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "amount")]
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","amount":150.00,"eventTimestamp":"2026-05-15T14:02:11Z"}""", "currency")]
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","amount":150.00,"currency":"USD"}""", "eventTimestamp")]
    // Empty required fields.
    [InlineData("""{"eventId":"","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "eventId")]
    // Amount must be strictly greater than zero.
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","amount":0,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "amount")]
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","amount":-5.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "amount")]
    // Type is case-sensitive and closed (SPEC 1.2).
    [InlineData("""{"eventId":"evt-1","type":"credit","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "type")]
    [InlineData("""{"eventId":"evt-1","type":"TRANSFER","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}""", "type")]
    // Timestamp must parse as ISO 8601.
    [InlineData("""{"eventId":"evt-1","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"not-a-date"}""", "eventTimestamp")]
    public async Task ApplyTransaction_InvalidRequest_Returns400WithFieldError(string body, string expectedField)
    {
        var response = await PostAsync("acct-1", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.True(problem!.Errors.ContainsKey(expectedField),
            $"Expected an error for '{expectedField}' but got: {string.Join(", ", problem.Errors.Keys)}");
        Assert.NotEmpty(problem.Errors[expectedField]);
    }

    [Fact]
    public async Task ApplyTransaction_InvalidRequest_PersistsNothing()
    {
        await PostAsync("acct-1", """
            {"eventId":"evt-1","type":"CREDIT","amount":-5.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
            """);

        // The account must not exist: a rejected event may leave no trace (SPEC 3.1 step 1).
        var details = await _client.GetAsync("/accounts/acct-1");
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
    }

    [Fact]
    public async Task ApplyTransaction_MultipleInvalidFields_ReportsAllOfThem()
    {
        var response = await PostAsync("acct-1", """
            {"eventId":"evt-1","type":"credit","amount":0,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
            """);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("type", problem!.Errors.Keys);
        Assert.Contains("amount", problem.Errors.Keys);
    }

    [Fact]
    public async Task ApplyTransaction_ValidationFailure_IncludesTraceIdForLogCorrelation()
    {
        var response = await PostAsync("acct-1", """
            {"eventId":"evt-1","type":"credit","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
            """);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        // SPEC 6: every ProblemDetails carries a traceId. It must be the bare 32-hex
        // trace-id that logs correlate on, not a full "00-<trace>-<span>-00" traceparent.
        Assert.True(problem!.Extensions.TryGetValue("traceId", out var traceId));
        Assert.Matches("^[0-9a-f]{32}$", traceId?.ToString());
    }

    [Fact]
    public async Task ApplyTransaction_FrameworkGenerated400_AlsoCarriesTraceId()
    {
        // Malformed JSON is rejected by the framework, not our handler; SPEC 6 applies
        // to every problem response, so this path must be covered too.
        var response = await PostAsync("acct-1", "{ this is not json");

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem!.Extensions.TryGetValue("traceId", out var traceId));
        Assert.Matches("^[0-9a-f]{32}$", traceId?.ToString());
    }

    [Fact]
    public async Task ApplyTransaction_AccountIdLongerThanLimit_Returns400()
    {
        var response = await PostAsync(new string('a', 129), ValidBody);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("accountId", problem!.Errors.Keys);
    }

    [Fact]
    public async Task ApplyTransaction_EventIdLongerThanLimit_Returns400()
    {
        var response = await PostAsync("acct-1", $$"""
            {"eventId":"{{new string('e', 129)}}","type":"CREDIT","amount":150.00,"currency":"USD","eventTimestamp":"2026-05-15T14:02:11Z"}
            """);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("eventId", problem!.Errors.Keys);
    }

    // Runs in both environments: minimal APIs only throw on an unparseable body in
    // Development, so a Testing-only assertion would miss a 500 regression there.
    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task ApplyTransaction_MalformedJson_Returns400WithoutLeakingAnException(string environment)
    {
        using var factory = new AccountServiceFactory(environment);
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/accounts/acct-1/transactions",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    private Task<HttpResponseMessage> PostAsync(string accountId, string body) =>
        _client.PostAsync(
            $"/accounts/{accountId}/transactions",
            new StringContent(body, Encoding.UTF8, "application/json"));
}
