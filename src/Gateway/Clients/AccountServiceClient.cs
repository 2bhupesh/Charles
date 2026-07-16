using System.Globalization;
using System.Net.Http.Json;
using Gateway.Domain;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Gateway.Clients;

/// <summary>What a balance read found.</summary>
public enum BalanceOutcome
{
    Found,

    /// <summary>The account has no transactions, so it does not exist yet (SPEC 2.2).</summary>
    NotFound,

    /// <summary>The Account Service could not be reached; no balance can be reported.</summary>
    Unavailable
}

/// <summary>A balance as the Account Service reports it (SPEC 4.2).</summary>
public sealed record AccountBalance(string AccountId, decimal Balance, string Currency);

public sealed record BalanceLookup(BalanceOutcome Outcome, AccountBalance? Balance);

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
        catch (BrokenCircuitException ex)
        {
            // The breaker is open: sustained failure, so this call never left the process.
            // Failing fast here is the point - it protects both a struggling downstream
            // and the Gateway's own threads (SPEC 7).
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Circuit is open; failing fast without calling the Account Service for event {EventId}",
                @event.EventId);
            return AccountApplyOutcome.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service unreachable applying event {EventId}", @event.EventId);
            return AccountApplyOutcome.Unavailable;
        }
        catch (TimeoutRejectedException ex)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service timed out applying event {EventId}", @event.EventId);
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

    /// <summary>
    /// Reads a balance on behalf of a client (SPEC 3.6). The Account Service is internal,
    /// so the Gateway is the only way a client can see a balance at all - which is why an
    /// unreachable downstream has to surface as a clear 503 rather than a bare failure.
    /// </summary>
    public async Task<BalanceLookup> GetBalanceAsync(string accountId, CancellationToken cancellationToken)
    {
        var path = $"/accounts/{Uri.EscapeDataString(accountId)}/balance";

        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                availability.MarkReachable();
                return new BalanceLookup(BalanceOutcome.NotFound, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                availability.MarkUnreachable();
                logger.LogWarning("Account Service returned {StatusCode} reading the balance for account {AccountId}",
                    (int)response.StatusCode, accountId);
                return new BalanceLookup(BalanceOutcome.Unavailable, null);
            }

            availability.MarkReachable();

            var balance = await response.Content.ReadFromJsonAsync<AccountBalance>(cancellationToken);
            return balance is null
                ? new BalanceLookup(BalanceOutcome.Unavailable, null)
                : new BalanceLookup(BalanceOutcome.Found, balance);
        }
        catch (BrokenCircuitException ex)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Circuit is open; failing fast without reading the balance for account {AccountId}",
                accountId);
            return new BalanceLookup(BalanceOutcome.Unavailable, null);
        }
        catch (HttpRequestException ex)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service unreachable reading the balance for account {AccountId}", accountId);
            return new BalanceLookup(BalanceOutcome.Unavailable, null);
        }
        catch (TimeoutRejectedException ex)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service timed out reading the balance for account {AccountId}", accountId);
            return new BalanceLookup(BalanceOutcome.Unavailable, null);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            availability.MarkUnreachable();
            logger.LogWarning(ex, "Account Service timed out reading the balance for account {AccountId}", accountId);
            return new BalanceLookup(BalanceOutcome.Unavailable, null);
        }
    }
}
