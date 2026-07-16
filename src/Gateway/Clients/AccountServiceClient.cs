using System.Globalization;
using System.Net.Http.Json;
using Gateway.Domain;

namespace Gateway.Clients;

/// <summary>What the Account Service did with a transaction, from the Gateway's point of view.</summary>
public enum AccountApplyOutcome
{
    /// <summary>Applied, or already applied - either way the balance now includes it.</summary>
    Applied,

    /// <summary>The Account Service rejected a transaction the Gateway had already
    /// validated, which means the two services disagree about the contract (SPEC 3.1).</summary>
    Rejected,

    /// <summary>Unreachable, timed out, or failing. The event stays PENDING and the
    /// client may safely resubmit it.</summary>
    Unavailable
}

/// <summary>
/// The Gateway's only route into the Account Service (SPEC 4.1). Resilience policies are
/// layered onto this client in Phase 3; retrying is safe because the downstream apply is
/// idempotent by eventId.
/// </summary>
public sealed class AccountServiceClient(HttpClient httpClient, AccountServiceAvailability availability, ILogger<AccountServiceClient> logger)
{
    /// <summary>Full precision, UTC, Z-form: the Account Service should store exactly the
    /// timestamp the Gateway stored, not a truncated copy of it.</summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public async Task<AccountApplyOutcome> ApplyAsync(Event @event, CancellationToken cancellationToken)
    {
        var payload = new
        {
            eventId = @event.EventId,
            type = @event.Type,
            amount = @event.Amount,
            currency = @event.Currency,
            eventTimestamp = @event.EventTimestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)
        };

        var path = $"/accounts/{Uri.EscapeDataString(@event.AccountId)}/transactions";

        try
        {
            using var response = await httpClient.PostAsJsonAsync(path, payload, cancellationToken);

            // 200 (replay) and 201 (fresh) both mean the balance includes this event.
            if (response.IsSuccessStatusCode)
            {
                availability.MarkReachable();
                return AccountApplyOutcome.Applied;
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                // It answered, so it is reachable - it just disagreed with us.
                availability.MarkReachable();
                logger.LogError(
                    "Account Service rejected event {EventId} with {StatusCode}; the services disagree about the contract",
                    @event.EventId, (int)response.StatusCode);
                return AccountApplyOutcome.Rejected;
            }

            availability.MarkUnreachable();
            logger.LogWarning("Account Service returned {StatusCode} applying event {EventId}",
                (int)response.StatusCode, @event.EventId);
            return AccountApplyOutcome.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service unreachable applying event {EventId}", @event.EventId);
            return AccountApplyOutcome.Unavailable;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient timed out. A cancellation from the caller is not our failure,
            // so that case is deliberately left to propagate.
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service timed out applying event {EventId}", @event.EventId);
            return AccountApplyOutcome.Unavailable;
        }
    }
}
