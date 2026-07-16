using System.Diagnostics.Metrics;

namespace AccountService.Diagnostics;

/// <summary>
/// Custom metrics for the Account Service (SPEC 8.3). Applied and replayed are counted
/// separately because their ratio is the visible evidence that idempotency is doing its
/// job: replays should be common and must never move a balance.
/// </summary>
public sealed class AccountMetrics
{
    /// <summary>Registered with OpenTelemetry so the Prometheus exporter picks it up.</summary>
    public const string MeterName = "EventLedger.AccountService";

    private readonly Counter<long> _transactions;

    public AccountMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        // Exported as account_transactions_applied_total.
        _transactions = meter.CreateCounter<long>(
            "account.transactions.applied",
            unit: "{transaction}",
            description: "Transaction applies, by result.");
    }

    /// <summary>A new transaction changed the balance (201).</summary>
    public void Applied() => Count("applied");

    /// <summary>A known eventId was seen again and deliberately changed nothing (200).</summary>
    public void Replayed() => Count("replayed");

    private void Count(string result) =>
        _transactions.Add(1, new KeyValuePair<string, object?>("result", result));
}
