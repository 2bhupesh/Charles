namespace Gateway.Domain;

/// <summary>
/// Transaction types. Comparison is case-sensitive: exactly "CREDIT" or "DEBIT" (SPEC 1.2).
/// </summary>
/// <remarks>
/// Deliberately duplicated rather than shared with the Account Service. The two services
/// are independently deployable and must not share in-process state; a common library
/// would couple their release cycles for the sake of two constants. The HTTP contract
/// (SPEC 4.1) is the seam between them, and the tests pin it.
/// </remarks>
public static class TransactionType
{
    public const string Credit = "CREDIT";
    public const string Debit = "DEBIT";

    public static bool IsValid(string? value) => value is Credit or Debit;
}
