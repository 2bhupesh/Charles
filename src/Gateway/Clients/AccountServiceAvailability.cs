namespace Gateway.Clients;

/// <summary>
/// Last-known reachability of the Account Service, reported by GET /health (SPEC 3.4).
/// Updated as a side effect of real calls rather than by polling, so it reflects what
/// clients actually experienced. Phase 3 will fold circuit state into this.
/// </summary>
/// <remarks>
/// Starts reachable: before any call has been made there is no known failure, and the
/// Gateway should not accuse a downstream it has never contacted.
/// </remarks>
public sealed class AccountServiceAvailability
{
    private volatile bool _reachable = true;

    public bool IsReachable => _reachable;

    public void MarkReachable() => _reachable = true;

    public void MarkUnreachable() => _reachable = false;
}
