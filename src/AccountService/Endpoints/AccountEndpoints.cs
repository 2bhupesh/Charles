using AccountService.Contracts;
using AccountService.Data;
using AccountService.Diagnostics;
using AccountService.Domain;
using AccountService.Http;
using AccountService.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AccountService.Endpoints;

public static class AccountEndpoints
{
    private const int RecentTransactionLimit = 10;
    private const int SqliteConstraintViolation = 19;

    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/accounts/{accountId}/transactions", ApplyTransaction);
        app.MapGet("/accounts/{accountId}/balance", GetBalance);
        app.MapGet("/accounts/{accountId}", GetAccount);
        app.MapGet("/health", GetHealth);
    }

    /// <summary>
    /// Applies a transaction, idempotently by eventId (SPEC 4.1). A replay returns 200
    /// with the originally stored transaction and changes no state; a fresh apply
    /// returns 201. This is what makes the Gateway's retries safe to perform.
    /// </summary>
    private static async Task<IResult> ApplyTransaction(
        string accountId,
        ApplyTransactionRequest? request,
        AccountDbContext db,
        AccountMetrics metrics,
        ILogger<AccountTransaction> logger,
        CancellationToken cancellationToken)
    {
        if (!TransactionRequestValidator.TryValidate(accountId, request, out var eventTimestamp, out var errors))
        {
            logger.LogWarning("Rejected transaction for account {AccountId}: {FieldCount} invalid field(s)",
                accountId, errors.Count);
            return ProblemResults.Validation(errors);
        }

        var existing = await db.Transactions
            .SingleOrDefaultAsync(t => t.EventId == request!.EventId, cancellationToken);

        if (existing is not null)
        {
            metrics.Replayed();
            logger.LogInformation("Event {EventId} already applied to account {AccountId}; replay ignored",
                existing.EventId, existing.AccountId);
            return Results.Ok(ToResponse(existing));
        }

        var transaction = new AccountTransaction
        {
            EventId = request!.EventId!,
            AccountId = accountId,
            Type = request.Type!,
            Amount = request.Amount!.Value,
            Currency = request.Currency!,
            EventTimestamp = eventTimestamp,
            AppliedAt = DateTimeOffset.UtcNow
        };

        db.Transactions.Add(transaction);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException
                                           { SqliteErrorCode: SqliteConstraintViolation })
        {
            // A concurrent request inserted the same eventId between our read and write.
            // The unique index is the arbiter; the loser reports the winner's row.
            db.Entry(transaction).State = EntityState.Detached;

            var winner = await db.Transactions
                .SingleAsync(t => t.EventId == request.EventId, cancellationToken);

            // A lost race is a replay by another name: the event is applied exactly once.
            metrics.Replayed();
            logger.LogInformation("Event {EventId} lost an insert race; returning the stored transaction",
                winner.EventId);
            return Results.Ok(ToResponse(winner));
        }

        metrics.Applied();
        logger.LogInformation("Applied {Type} of {Amount} {Currency} to account {AccountId} for event {EventId}",
            transaction.Type, transaction.Amount, transaction.Currency, transaction.AccountId, transaction.EventId);

        return Results.Created($"/accounts/{accountId}/transactions/{transaction.EventId}", ToResponse(transaction));
    }

    /// <summary>Balance = sum(CREDIT) - sum(DEBIT) (SPEC 2.3). Summation is commutative,
    /// so the result does not depend on the order events arrived in.</summary>
    private static async Task<IResult> GetBalance(
        string accountId,
        AccountDbContext db,
        CancellationToken cancellationToken)
    {
        var transactions = await LoadAccountAsync(db, accountId, cancellationToken);

        if (transactions.Count == 0)
            return ProblemResults.NotFound($"Account '{accountId}' has no transactions.");

        return Results.Ok(new BalanceResponse(accountId, Balance(transactions), Currency(transactions)));
    }

    private static async Task<IResult> GetAccount(
        string accountId,
        AccountDbContext db,
        CancellationToken cancellationToken)
    {
        var transactions = await LoadAccountAsync(db, accountId, cancellationToken);

        if (transactions.Count == 0)
            return ProblemResults.NotFound($"Account '{accountId}' has no transactions.");

        var recent = transactions
            .OrderByDescending(t => t.EventTimestamp)
            .Take(RecentTransactionLimit)
            .Select(ToResponse)
            .ToList();

        return Results.Ok(new AccountDetailsResponse(
            accountId,
            Balance(transactions),
            Currency(transactions),
            transactions.Count,
            recent));
    }

    private static async Task<IResult> GetHealth(AccountDbContext db, CancellationToken cancellationToken)
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

        var response = new HealthResponse(
            databaseReachable ? "Healthy" : "Unhealthy",
            "account-service",
            new Dictionary<string, string> { ["database"] = databaseReachable ? "Healthy" : "Unhealthy" });

        return databaseReachable
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Amounts are stored as TEXT to keep decimals exact, so SQLite cannot SUM them.
    /// An account's transactions are loaded and summed in memory as decimal - correct
    /// by construction, and bounded by the transaction count of a single account.
    /// </summary>
    private static Task<List<AccountTransaction>> LoadAccountAsync(
        AccountDbContext db,
        string accountId,
        CancellationToken cancellationToken) =>
        db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .ToListAsync(cancellationToken);

    private static decimal Balance(IEnumerable<AccountTransaction> transactions) =>
        transactions.Sum(t => t.Type == TransactionType.Credit ? t.Amount : -t.Amount);

    // v1 assumes a single currency per account and does not enforce it (SPEC 4.2).
    private static string Currency(IEnumerable<AccountTransaction> transactions) =>
        transactions.First().Currency;

    private static TransactionResponse ToResponse(AccountTransaction t) =>
        new(t.EventId, t.AccountId, t.Type, t.Amount, t.Currency, t.EventTimestamp, t.AppliedAt);
}
