using System.Diagnostics;
using System.Text.Json;
using Gateway.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

/// <summary>SPEC 8.2 - the shape of a log line is itself a requirement.</summary>
public sealed class JsonLogFormatterTests
{
    [Fact]
    public void Write_InsideAnActivity_PutsTraceIdSpanIdAndServiceAtTopLevel()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Information, "Event {EventId} applied to account {AccountId}",
            [new("EventId", "evt-001"), new("AccountId", "acct-123")]);

        // Requirement 4: JSON logs with trace ID, timestamp, level and service name - as
        // fields, not buried in a nested scopes array.
        Assert.Equal("gateway", log.GetProperty("service").GetString());
        Assert.Equal(activity.TraceId.ToString(), log.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToString(), log.GetProperty("spanId").GetString());
        Assert.Equal("Information", log.GetProperty("level").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", log.GetProperty("timestamp").GetString());
        Assert.Equal("Event evt-001 applied to account acct-123", log.GetProperty("message").GetString());
    }

    [Fact]
    public void Write_StructuredMessage_PromotesItsFieldsSoLogsCanBeFilteredNotGrepped()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Information, "Event {EventId} applied to account {AccountId}",
            [new("EventId", "evt-001"), new("AccountId", "acct-123")]);

        Assert.Equal("evt-001", log.GetProperty("eventId").GetString());
        Assert.Equal("acct-123", log.GetProperty("accountId").GetString());

        // The raw template is noise once the holes are filled in.
        Assert.False(log.TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public void Write_StateFieldNamedLikeAFormatterField_DoesNotOverwriteIt()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Warning, "Something about {Service}", [new("Service", "impostor")]);

        // A log message must not be able to forge which service it came from.
        Assert.Equal("gateway", log.GetProperty("service").GetString());
    }

    [Fact]
    public void Write_WithException_IncludesItWithoutLosingTheMessage()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Error, "Downstream failed", [], new InvalidOperationException("boom"));

        Assert.Equal("Downstream failed", log.GetProperty("message").GetString());
        Assert.Contains("boom", log.GetProperty("exception").GetString());
    }

    [Fact]
    public void Write_OutsideAnyActivity_StillLogsRatherThanThrowing()
    {
        Activity.Current = null;

        var log = Format(LogLevel.Information, "Starting up", []);

        Assert.Equal("Starting up", log.GetProperty("message").GetString());
        Assert.False(log.TryGetProperty("traceId", out _));
    }

    private static JsonElement Format(
        LogLevel level,
        string template,
        KeyValuePair<string, object?>[] fields,
        Exception? exception = null)
    {
        var formatter = new JsonLogFormatter(
            new StaticOptionsMonitor<JsonLogFormatterOptions>(new JsonLogFormatterOptions { ServiceName = "gateway" }));

        var state = new List<KeyValuePair<string, object?>>(fields)
        {
            new("{OriginalFormat}", template)
        };

        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            level,
            "Gateway.Tests",
            new EventId(1),
            state,
            exception,
            (_, _) => Render(template, fields) + (exception is null ? string.Empty : string.Empty));

        var writer = new StringWriter();
        formatter.Write(entry, null, writer);

        return JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }

    private static string Render(string template, IEnumerable<KeyValuePair<string, object?>> fields) =>
        fields.Aggregate(template, (message, field) =>
            message.Replace($"{{{field.Key}}}", field.Value?.ToString()));
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
