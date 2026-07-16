using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// SPEC 10.2 - G9. A single client request must be traceable across both services, which
/// means the Gateway continues an inbound trace and passes it downstream.
/// </summary>
public sealed class TracePropagationTests : IDisposable
{
    private const string TraceParentPattern = "^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$";

    private readonly FakeAccountService _accountService = new();
    private readonly GatewayFactory _factory;
    private readonly HttpClient _client;

    public TracePropagationTests()
    {
        _factory = new GatewayFactory(_accountService.Url);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        _accountService.Dispose();
    }

    // G9 - the client's trace id must survive the hop to the Account Service.
    [Fact]
    public async Task SubmitEvent_InboundTraceParent_SendsTheSameTraceIdDownstream()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        await SubmitWithTraceParentAsync($"00-{traceId}-00f067aa0ba902b7-01");

        // Asserting over every downstream call rather than a single one: a timeout on a
        // loaded machine would retry, and how many attempts were made is the resilience
        // tests' business, not this test's.
        Assert.NotEmpty(_accountService.ApplyTraceParents);

        Assert.All(_accountService.ApplyTraceParents, traceParent =>
        {
            Assert.NotNull(traceParent);
            Assert.Matches(TraceParentPattern, traceParent);

            // Same trace, new span: the downstream call is a child of the inbound request,
            // not a separate trace.
            Assert.StartsWith($"00-{traceId}-", traceParent);
            Assert.DoesNotContain("00f067aa0ba902b7", traceParent);
        });
    }

    [Fact]
    public async Task SubmitEvent_NoInboundTraceParent_StillPropagatesAGeneratedTrace()
    {
        // Requirement 3: the Gateway generates a trace id when the client did not send one.
        await _client.SubmitEventAsync("evt-1", "acct-1", "CREDIT", 150.00m);

        Assert.NotEmpty(_accountService.ApplyTraceParents);

        Assert.All(_accountService.ApplyTraceParents, traceParent =>
        {
            Assert.NotNull(traceParent);
            Assert.Matches(TraceParentPattern, traceParent);
        });
    }

    [Fact]
    public async Task SubmitEvent_RetriedDownstreamCalls_AllCarryTheSameTraceId()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        _accountService.RespondWithStatus(500);

        await SubmitWithTraceParentAsync($"00-{traceId}-00f067aa0ba902b7-01");

        // Retries must not fragment the trace - every attempt belongs to the same request.
        Assert.NotEmpty(_accountService.ApplyTraceParents);
        Assert.All(_accountService.ApplyTraceParents, traceParent =>
            Assert.StartsWith($"00-{traceId}-", traceParent));
    }

    /// <summary>
    /// The trace id a client is handed on an error is the one that appears in the logs -
    /// otherwise SPEC 6's promise that a reported error is findable does not hold.
    /// </summary>
    [Fact]
    public async Task SubmitEvent_ValidationFailure_ReturnsTheInboundTraceIdInProblemDetails()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";

        var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(new
            {
                eventId = "evt-1",
                accountId = "acct-1",
                type = "credit",
                amount = 150.00m,
                currency = "USD",
                eventTimestamp = TestClientExtensions.DefaultTimestamp
            })
        };
        request.Headers.Add("traceparent", $"00-{traceId}-00f067aa0ba902b7-01");

        var response = await _client.SendAsync(request);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();

        Assert.True(problem!.Extensions.TryGetValue("traceId", out var reported));
        Assert.Equal(traceId, reported?.ToString());
    }

    private async Task SubmitWithTraceParentAsync(string traceParent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = JsonContent.Create(new
            {
                eventId = "evt-1",
                accountId = "acct-1",
                type = "CREDIT",
                amount = 150.00m,
                currency = "USD",
                eventTimestamp = TestClientExtensions.DefaultTimestamp
            })
        };

        request.Headers.Add("traceparent", traceParent);

        using var response = await _client.SendAsync(request);
    }
}
