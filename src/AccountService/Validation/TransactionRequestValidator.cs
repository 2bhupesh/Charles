using System.Globalization;
using AccountService.Contracts;
using AccountService.Domain;

namespace AccountService.Validation;

/// <summary>
/// Validation rules from SPEC 4.1 (identical to SPEC 3.1 for the fields present here).
/// Collects every violation rather than failing on the first, so a client sees all
/// problems in one response.
/// </summary>
public static class TransactionRequestValidator
{
    public const int MaxIdentifierLength = 128;

    public static bool TryValidate(
        string accountId,
        ApplyTransactionRequest? request,
        out DateTimeOffset eventTimestamp,
        out Dictionary<string, string[]> errors)
    {
        var found = new Dictionary<string, List<string>>();
        eventTimestamp = default;

        if (request is null)
        {
            errors = new Dictionary<string, string[]>
            {
                ["body"] = ["A JSON request body is required."]
            };
            return false;
        }

        ValidateIdentifier(found, "accountId", accountId);
        ValidateIdentifier(found, "eventId", request.EventId);

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
