using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Gateway.Logging;

public sealed class JsonLogFormatterOptions : ConsoleFormatterOptions
{
    public string ServiceName { get; set; } = "unknown";
}

/// <summary>
/// One JSON object per line, in the shape SPEC 8.2 requires: timestamp, level, service,
/// traceId, spanId, message.
/// </summary>
/// <remarks>
/// The built-in JSON console formatter buries the trace ID inside a nested "Scopes" array
/// and has no place for a service name, which makes the central question - "show me every
/// line from both services for this one request" - awkward to answer. Promoting traceId,
/// spanId and service to top-level fields is the whole point of the exercise.
///
/// Deliberately duplicated in each service rather than shared. It is small, stable
/// infrastructure, and a shared library would tie the two services' builds together for
/// the sake of a log format - the same reasoning as Gateway.Domain.TransactionType.
/// </remarks>
public sealed class JsonLogFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterName = "event-ledger-json";

    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>Field names owned by the formatter; a log's own state may not overwrite them.</summary>
    private static readonly HashSet<string> ReservedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "level", "service", "traceId", "spanId", "category", "message", "exception"
    };

    private readonly IDisposable? _optionsReloadToken;
    private JsonLogFormatterOptions _options;

    public JsonLogFormatter(IOptionsMonitor<JsonLogFormatterOptions> options) : base(FormatterName)
    {
        _options = options.CurrentValue;
        _optionsReloadToken = options.OnChange(updated => _options = updated);
    }

    public void Dispose() => _optionsReloadToken?.Dispose();

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            return;

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteString("level", logEntry.LogLevel.ToString());
            writer.WriteString("service", _options.ServiceName);

            // The ambient activity is what ties this line to the same request in the other
            // service. ASP.NET Core starts it from the inbound traceparent, and .NET
            // propagates it onward over HttpClient (SPEC 8.1).
            if (Activity.Current is { } activity)
            {
                writer.WriteString("traceId", activity.TraceId.ToString());
                writer.WriteString("spanId", activity.SpanId.ToString());
            }

            writer.WriteString("category", logEntry.Category);
            writer.WriteString("message", message);

            if (logEntry.Exception is not null)
                writer.WriteString("exception", logEntry.Exception.ToString());

            WriteStateFields(writer, logEntry.State);

            writer.WriteEndObject();
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// Promotes the named holes of a structured message ("Event {EventId} applied...") to
    /// their own fields, so logs can be filtered by eventId rather than by substring.
    /// </summary>
    private static void WriteStateFields<TState>(Utf8JsonWriter writer, TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
            return;

        foreach (var field in fields)
        {
            if (field.Key == "{OriginalFormat}")
                continue;

            var name = ToCamelCase(field.Key);

            if (ReservedFields.Contains(name))
                continue;

            writer.WriteString(name, field.Value?.ToString());
        }
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : string.Create(name.Length, name, (span, source) =>
            {
                source.AsSpan().CopyTo(span);
                span[0] = char.ToLowerInvariant(span[0]);
            });
}
