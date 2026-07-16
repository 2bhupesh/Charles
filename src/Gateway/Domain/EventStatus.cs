namespace Gateway.Domain;

/// <summary>
/// Write-path state (SPEC 3.1). An event is recorded PENDING before the downstream call
/// and only becomes APPLIED once the Account Service has accepted it, so a failed call
/// leaves a record the client can safely resubmit under the same eventId.
/// </summary>
public static class EventStatus
{
    public const string Pending = "PENDING";
    public const string Applied = "APPLIED";
}
