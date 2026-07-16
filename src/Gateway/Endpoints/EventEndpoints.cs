using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Clients;
using Gateway.Contracts;
using Gateway.Data;
using Gateway.Diagnostics;
using Gateway.Domain;
using Gateway.Http;
using Gateway.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Endpoints;

public static class EventEndpoints
{
    private const int SqliteConstraintViolation = 19;

    public static void MapEventEndpoints(this WebApplication app)
    {
        app.MapPost("/events", SubmitEvent);
        app.MapGet("/events/{id}", GetEvent);
        app.MapGet("/events", ListEvents);
        app.MapGet("/accounts/{accountId}/balance", GetBalance);
        app.MapGet("/health", GetHealth);
    }

    /// <summary>
    /// Proxies a balance read to the Account Service (SPEC 3.6). Unlike the event
    /// endpoints this cannot be answered locally - the Gateway does not compute balances,
    /// the Account Service owns them - so when the downstream is unreachable there is
    /// nothing to degrade to and the client is told exactly that.
    /// </summary>
    private static async Task<IResult> GetBalance(
        string accountId,
        AccountServiceClient accountService,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var lookup = await accountService.GetBalanceAsync(accountId, cancellationToken);

        return lookup.Outcome switch
        {
            BalanceOutcome.Found => Results.Ok(lookup.Balance),
            BalanceOutcome.NotFound => ProblemResults.NotFound($"Account '{accountId}' has no transactions."),
            _ => ProblemResults.BalanceUnavailable(context)
        };
    }

    /// <summary>
    /// Submits an event (SPEC 3.1). The event is recorded PENDING before the downstream
    /// call and promoted to APPLIED only once the Account Service has accepted it, so a
    /// failed call leaves a record rather than losing the event - and because both
    /// services deduplicate on eventId, resubmitting is always safe.
    /// </summary>
    private static async Task<IResult> SubmitEvent(
        SubmitEventRequest? request,
        GatewayDbContext db,
        AccountServiceClient accountService,
        GatewayMetrics metrics,
        HttpContext context,
        ILogger<Event> logger,
        CancellationToken cancellationToken)
    {
        if (!EventRequestValidator.TryValidate(request, out var eventTimestamp, out var errors))
        {
            metrics.EventRejected();
            logger.LogWarning("Rejected event {EventId}: {FieldCount} invalid field(s)",
                request?.EventId, errors.Count);
            return ProblemResults.Validation(errors);
        }

        var stored = await db.Events.SingleOrDefaultAsync(e => e.EventId == request!.EventId, cancellationToken);

        // Already applied: return the original, call nothing, change nothing.
        if (stored is { Status: EventStatus.Applied })
        {
            metrics.EventDuplicate();
            logger.LogInformation("Event {EventId} is a duplicate of an applied event; returning the original",
                stored.EventId);
            return Results.Ok(ToResponse(stored));
        }

        // A PENDING record means an earlier apply did not complete. Retry it rather than
        // rejecting the client (SPEC 3.1 step 2).
        var isRetryOfPending = stored is not null;
        var @event = stored;

        if (@event is null)
        {
            @event = new Event
            {
                EventId = request!.EventId!,
                AccountId = request.AccountId!,
                Type = request.Type!,
                Amount = request.Amount!.Value,
                Currency = request.Currency!,
                EventTimestamp = eventTimestamp,
                Metadata = request.Metadata is { ValueKind: JsonValueKind.Object } metadata
                    ? metadata.GetRawText()
                    : null,
                Status = EventStatus.Pending,
                ReceivedAt = DateTimeOffset.UtcNow
            };

            db.Events.Add(@event);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException
                                               { SqliteErrorCode: SqliteConstraintViolation })
            {
                // A concurrent submission of the same eventId won the primary key. The
                // loser reports the winner's record rather than applying it twice.
                db.Entry(@event).State = EntityState.Detached;

                var winner = await db.Events.SingleAsync(e => e.EventId == request.EventId, cancellationToken);

                metrics.EventDuplicate();
                logger.LogInformation("Event {EventId} lost an insert race; returning the stored event",
                    winner.EventId);
                return Results.Ok(ToResponse(winner));
            }
        }

        var outcome = await accountService.ApplyAsync(@event, cancellationToken);

        switch (outcome)
        {
            case AccountApplyOutcome.Applied:
                @event.Status = EventStatus.Applied;
                await db.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Event {EventId} applied to account {AccountId}",
                    @event.EventId, @event.AccountId);

                // A retry of a PENDING event is not a fresh creation, so it answers 200.
                if (isRetryOfPending)
                {
                    metrics.EventRetried();
                    return Results.Ok(ToResponse(@event));
                }

                metrics.EventCreated();
                return Results.Created($"/events/{@event.EventId}", ToResponse(@event));

            case AccountApplyOutcome.Rejected:
                // The Gateway validated this event, so a downstream 400 means the two
                // services disagree - a bug on our side, not the client's.
                metrics.EventDownstreamRejected();
                logger.LogError("Event {EventId} was rejected by the Account Service after passing Gateway validation",
                    @event.EventId);
                return ProblemResults.BadGateway(
                    "The Account Service rejected a validated transaction. The event was recorded but not applied.");

            default:
                metrics.EventDownstreamUnavailable();
                logger.LogWarning("Event {EventId} left PENDING: the Account Service is unavailable", @event.EventId);
                return ProblemResults.AccountServiceUnavailable(context);
        }
    }

    /// <summary>Local read: unaffected by the Account Service being down (SPEC 3.2).</summary>
    private static async Task<IResult> GetEvent(string id, GatewayDbContext db, CancellationToken cancellationToken)
    {
        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == id, cancellationToken);

        return @event is null
            ? ProblemResults.NotFound($"Event '{id}' was not found.")
            : Results.Ok(ToResponse(@event));
    }

    /// <summary>
    /// Lists an account's events in chronological order (SPEC 3.3). Ordering is applied at
    /// query time, so events that arrived out of order still read back in order. Also a
    /// local read - it keeps working while the Account Service is down.
    /// </summary>
    private static async Task<IResult> ListEvents(
        string? account,
        GatewayDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return ProblemResults.Validation(new Dictionary<string, string[]>
            {
                ["account"] = ["The account query parameter is required."]
            });
        }

        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.AccountId == account)
            .OrderBy(e => e.EventTimestamp)
            .ThenBy(e => e.ReceivedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(new EventListResponse(account, events.Select(ToResponse).ToList()));
    }

    /// <summary>
    /// SPEC 3.4: an unreachable Account Service degrades the Gateway but does not make it
    /// unhealthy - its own reads still work, so reporting 503 would invite an orchestrator
    /// to restart a service that is fine.
    /// </summary>
    private static async Task<IResult> GetHealth(
        GatewayDbContext db,
        AccountServiceAvailability availability,
        CancellationToken cancellationToken)
    {
        bool databaseReachable;

        try
        {
            databaseReachable = await db.Database.CanConnectAsync(cancellationToken);
        }
        catch (SqliteException)
        {
            databaseReachable = false;
        }

        var accountServiceStatus = availability.IsReachable ? "Healthy" : "Degraded";
        var status = databaseReachable
            ? availability.IsReachable ? "Healthy" : "Degraded"
            : "Unhealthy";

        var response = new HealthResponse(
            status,
            "gateway",
            new Dictionary<string, string>
            {
                ["database"] = databaseReachable ? "Healthy" : "Unhealthy",
                ["accountService"] = accountServiceStatus
            });

        return databaseReachable
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static EventResponse ToResponse(Event @event) =>
        new(@event.EventId,
            @event.AccountId,
            @event.Type,
            @event.Amount,
            @event.Currency,
            @event.EventTimestamp,
            @event.Metadata is null ? null : JsonNode.Parse(@event.Metadata),
            @event.Status,
            @event.ReceivedAt);
}
