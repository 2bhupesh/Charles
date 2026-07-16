using System.Diagnostics;
using System.Text.Json;
using AccountService.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AccountService.Tests;

/// <summary>
/// SPEC 8.2 - the shape of a log line is itself a requirement. The formatter is duplicated
/// per service by design, so each copy carries its own tests rather than trusting the two
/// to stay in step.
/// </summary>
public sealed class JsonLogFormatterTests
{
    [Fact]
    public void Write_InsideAnActivity_PutsTraceIdSpanIdAndServiceAtTopLevel()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Information, "Applied {Type} for event {EventId}",
            [new("Type", "CREDIT"), new("EventId", "evt-001")]);

        // This trace id is what ties the line to the Gateway's log line for the same
        // request - the whole point of propagating traceparent (SPEC 8.1).
        Assert.Equal("account-service", log.GetProperty("service").GetString());
        Assert.Equal(activity.TraceId.ToString(), log.GetProperty("traceId").GetString());
        Assert.Equal(activity.SpanId.ToString(), log.GetProperty("spanId").GetString());
        Assert.Equal("Information", log.GetProperty("level").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", log.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void Write_StructuredMessage_PromotesItsFieldsSoLogsCanBeFilteredNotGrepped()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Information, "Applied {Type} for event {EventId}",
            [new("Type", "CREDIT"), new("EventId", "evt-001")]);

        Assert.Equal("evt-001", log.GetProperty("eventId").GetString());
        Assert.Equal("CREDIT", log.GetProperty("type").GetString());
        Assert.False(log.TryGetProperty("{OriginalFormat}", out _));
    }

    [Fact]
    public void Write_StateFieldNamedLikeAFormatterField_DoesNotOverwriteIt()
    {
        using var activity = new Activity("test").Start();

        var log = Format(LogLevel.Warning, "Something about {Service}", [new("Service", "impostor")]);

        Assert.Equal("account-service", log.GetProperty("service").GetString());
    }

    private static JsonElement Format(LogLevel level, string template, KeyValuePair<string, object?>[] fields)
    {
        var formatter = new JsonLogFormatter(
            new StaticOptionsMonitor<JsonLogFormatterOptions>(
                new JsonLogFormatterOptions { ServiceName = "account-service" }));

        var state = new List<KeyValuePair<string, object?>>(fields)
        {
            new("{OriginalFormat}", template)
        };

        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            level,
            "AccountService.Tests",
            new EventId(1),
            state,
            exception: null,
            (_, _) => Render(template, fields));

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
