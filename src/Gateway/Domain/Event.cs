namespace Gateway.Domain;

/// <summary>
/// A transaction event as received from a client (SPEC 2.1). The Gateway owns the
/// event record; the Account Service owns the resulting balance.
/// </summary>
public sealed class Event
{
    /// <summary>Client-supplied primary key - the idempotency key for submissions.</summary>
    public required string EventId { get; set; }

    public required string AccountId { get; set; }

    public required string Type { get; set; }

    public required decimal Amount { get; set; }

    public required string Currency { get; set; }

    /// <summary>When the event originally occurred upstream. Normalized to UTC.</summary>
    public required DateTimeOffset EventTimestamp { get; set; }

    /// <summary>Optional client context, stored verbatim as JSON and returned verbatim.</summary>
    public string? Metadata { get; set; }

    /// <summary>PENDING until the Account Service has applied it (SPEC 3.1).</summary>
    public required string Status { get; set; }

    /// <summary>Server-assigned arrival time. Breaks ties between equal event timestamps.</summary>
    public required DateTimeOffset ReceivedAt { get; set; }
}
