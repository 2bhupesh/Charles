namespace AccountService.Domain;

/// <summary>
/// Transaction types. Comparison is case-sensitive: exactly "CREDIT" or "DEBIT" (SPEC 1.2).
/// </summary>
public static class TransactionType
{
    public const string Credit = "CREDIT";
    public const string Debit = "DEBIT";

    public static bool IsValid(string? value) => value is Credit or Debit;
}
