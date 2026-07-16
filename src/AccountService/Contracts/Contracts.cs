namespace AccountService.Contracts;

/// <summary>
/// Body of POST /accounts/{accountId}/transactions (SPEC 4.1). Every field is nullable
/// and the timestamp arrives as a string so that validation - not the JSON
/// deserializer - decides what a bad request looks like, which lets us return
/// per-field errors (SPEC 6).
/// </summary>
public sealed record ApplyTransactionRequest(
    string? EventId,
    string? Type,
    decimal? Amount,
    string? Currency,
    string? EventTimestamp);

public sealed record TransactionResponse(
    string EventId,
    string AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset EventTimestamp,
    DateTimeOffset AppliedAt);

public sealed record BalanceResponse(
    string AccountId,
    decimal Balance,
    string Currency);

public sealed record AccountDetailsResponse(
    string AccountId,
    decimal Balance,
    string Currency,
    int TransactionCount,
    IReadOnlyList<TransactionResponse> RecentTransactions);

public sealed record HealthResponse(
    string Status,
    string Service,
    IReadOnlyDictionary<string, string> Checks);
