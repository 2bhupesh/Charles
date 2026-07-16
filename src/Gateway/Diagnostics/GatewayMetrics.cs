using System.Diagnostics.Metrics;

namespace Gateway.Diagnostics;

/// <summary>
/// Custom metrics for the Gateway (SPEC 8.3). Counted by outcome rather than as a single
/// total, because the interesting questions are ratios: how much traffic is duplicate
/// delivery, and how much is being turned away while the Account Service is down.
/// </summary>
public sealed class GatewayMetrics
{
    /// <summary>Registered with OpenTelemetry so the Prometheus exporter picks it up.</summary>
    public const string MeterName = "EventLedger.Gateway";

    private readonly Counter<long> _events;
    private readonly Counter<long> _accountClientFailures;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        // Exported as gateway_events_total; the unit is an annotation, so Prometheus drops it.
        _events = meter.CreateCounter<long>(
            "gateway.events",
            unit: "{event}",
            description: "Events submitted to POST /events, by outcome.");

        _accountClientFailures = meter.CreateCounter<long>(
            "gateway.account_client.failures",
            unit: "{failure}",
            description: "Calls to the Account Service that did not succeed, by reason.");
    }

    /// <summary>A new event was accepted and applied (201).</summary>
    public void EventCreated() => Count("created");

    /// <summary>An already-applied eventId was resubmitted (200), costing no downstream call.</summary>
    public void EventDuplicate() => Count("duplicate");

    /// <summary>A PENDING event was retried into APPLIED (200).</summary>
    public void EventRetried() => Count("retried");

    /// <summary>Validation turned the event away (400).</summary>
    public void EventRejected() => Count("rejected");

    /// <summary>The Account Service was unreachable, so the event stayed PENDING (503).</summary>
    public void EventDownstreamUnavailable() => Count("downstream_unavailable");

    /// <summary>The Account Service rejected an event the Gateway had validated (502).</summary>
    public void EventDownstreamRejected() => Count("downstream_rejected");

    public void AccountClientUnavailable() => CountFailure("unavailable");

    public void AccountClientRejected() => CountFailure("rejected");

    private void Count(string outcome) =>
        _events.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private void CountFailure(string reason) =>
        _accountClientFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
