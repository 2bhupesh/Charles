namespace AccountService.Domain;

/// <summary>
/// A single transaction applied to an account. Accounts are implicit: an account
/// exists iff it has at least one transaction (SPEC 2.2).
/// </summary>
public sealed class AccountTransaction
{
    public int Id { get; set; }

    /// <summary>Idempotency key. Unique-indexed, which is what makes replays safe.</summary>
    public required string EventId { get; set; }

    public required string AccountId { get; set; }

    /// <summary>Either <see cref="TransactionType.Credit"/> or <see cref="TransactionType.Debit"/>.</summary>
    public required string Type { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    /// <summary>When the event originally occurred upstream. Normalized to UTC.</summary>
    public required DateTimeOffset EventTimestamp { get; set; }

    /// <summary>Server-assigned arrival time.</summary>
    public required DateTimeOffset AppliedAt { get; set; }
}
