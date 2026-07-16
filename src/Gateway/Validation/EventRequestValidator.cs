using System.Globalization;
using System.Text.Json;
using Gateway.Contracts;
using Gateway.Domain;

namespace Gateway.Validation;

/// <summary>
/// Validation rules from SPEC 3.1. Collects every violation rather than failing on the
/// first, so a client sees all problems in one response.
/// </summary>
public static class EventRequestValidator
{
    public const int MaxIdentifierLength = 128;

    public static bool TryValidate(
        SubmitEventRequest? request,
        out DateTimeOffset eventTimestamp,
        out Dictionary<string, string[]> errors)
    {
        var found = new Dictionary<string, List<string>>();
        eventTimestamp = default;

        if (request is null)
        {
            errors = new Dictionary<string, string[]> { ["body"] = ["A JSON request body is required."] };
            return false;
        }

        ValidateIdentifier(found, "eventId", request.EventId);
        ValidateIdentifier(found, "accountId", request.AccountId);

        if (string.IsNullOrWhiteSpace(request.Type))
            Add(found, "type", "Type is required.");
        else if (!TransactionType.IsValid(request.Type))
            Add(found, "type", $"Type must be exactly {TransactionType.Credit} or {TransactionType.Debit}.");

        if (request.Amount is null)
            Add(found, "amount", "Amount is required.");
        else if (request.Amount <= 0)
            Add(found, "amount", "Amount must be greater than 0.");

        if (string.IsNullOrWhiteSpace(request.Currency))
            Add(found, "currency", "Currency is required.");

        if (string.IsNullOrWhiteSpace(request.EventTimestamp))
        {
            Add(found, "eventTimestamp", "EventTimestamp is required.");
        }
        else if (!DateTimeOffset.TryParse(
                     request.EventTimestamp,
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.RoundtripKind,
                     out eventTimestamp))
        {
            Add(found, "eventTimestamp", "EventTimestamp must be a valid ISO 8601 timestamp.");
        }
        else
        {
            eventTimestamp = eventTimestamp.ToUniversalTime();
        }

        // Optional, but if supplied it must be an object - metadata is round-tripped
        // verbatim, so a scalar or array would break the documented shape (SPEC 3.1).
        if (request.Metadata is { } metadata && metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            Add(found, "metadata", "Metadata must be a JSON object.");

        errors = found.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        return errors.Count == 0;
    }

    private static void ValidateIdentifier(Dictionary<string, List<string>> found, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(found, field, $"{char.ToUpperInvariant(field[0])}{field[1..]} is required.");
        else if (value.Length > MaxIdentifierLength)
            Add(found, field, $"{char.ToUpperInvariant(field[0])}{field[1..]} must be at most {MaxIdentifierLength} characters.");
    }

    private static void Add(Dictionary<string, List<string>> found, string field, string message)
    {
        if (!found.TryGetValue(field, out var messages))
        {
            messages = [];
            found[field] = messages;
        }

        messages.Add(message);
    }
}
