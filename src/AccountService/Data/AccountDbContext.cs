using System.Globalization;
using AccountService.Domain;
using Microsoft.EntityFrameworkCore;

namespace AccountService.Data;

public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Fixed-width UTC format so that SQLite's lexicographic TEXT ordering is
    /// chronological ordering (SPEC 2.2).
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public DbSet<AccountTransaction> Transactions => Set<AccountTransaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var transaction = modelBuilder.Entity<AccountTransaction>();

        transaction.ToTable("Transactions");
        transaction.HasKey(t => t.Id);

        // The idempotency contract (SPEC 4.1): the database, not application logic,
        // is the authority on "this event was already applied".
        transaction.HasIndex(t => t.EventId).IsUnique();
        transaction.HasIndex(t => t.AccountId);

        transaction.Property(t => t.EventId).IsRequired();
        transaction.Property(t => t.AccountId).IsRequired();
        transaction.Property(t => t.Type).IsRequired();
        transaction.Property(t => t.Currency).IsRequired();

        // SQLite has no decimal type; storing the invariant string round-trips money
        // exactly and avoids the silent precision loss of REAL/double.
        transaction.Property(t => t.Amount)
            .IsRequired()
            .HasConversion(
                amount => amount.ToString(CultureInfo.InvariantCulture),
                text => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture));

        transaction.Property(t => t.EventTimestamp).IsRequired().HasConversion(UtcTextConverter());
        transaction.Property(t => t.AppliedAt).IsRequired().HasConversion(UtcTextConverter());
    }

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, string>
        UtcTextConverter() => new(
            value => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
