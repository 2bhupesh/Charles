using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gateway.Serialization;

/// <summary>
/// Writes timestamps as UTC "yyyy-MM-ddTHH:mm:ssZ" (SPEC 1.2) rather than the
/// default local-offset form. Reads any ISO 8601 value.
/// </summary>
public sealed class Iso8601UtcConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ssZ";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
