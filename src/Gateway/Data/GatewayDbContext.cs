using System.Globalization;
using Gateway.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Data;

public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Fixed-width UTC format so that SQLite's lexicographic TEXT ordering is
    /// chronological ordering - which is what makes the ORDER BY in the event
    /// listing correct (SPEC 2.1, 3.3).
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var @event = modelBuilder.Entity<Event>();

        @event.ToTable("Events");

        // The client-supplied eventId is the primary key: a duplicate submission cannot
        // physically become a second row, whatever the application logic does.
        @event.HasKey(e => e.EventId);

        // Serves the listing query, which filters by account and orders by timestamp.
        @event.HasIndex(e => new { e.AccountId, e.EventTimestamp });

        @event.Property(e => e.AccountId).IsRequired();
        @event.Property(e => e.Type).IsRequired();
        @event.Property(e => e.Currency).IsRequired();
        @event.Property(e => e.Status).IsRequired();
        @event.Property(e => e.Metadata);

        // SQLite has no decimal type; the invariant string round-trips money exactly
        // and avoids the silent precision loss of REAL/double.
        @event.Property(e => e.Amount)
            .IsRequired()
            .HasConversion(
                amount => amount.ToString(CultureInfo.InvariantCulture),
                text => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture));

        @event.Property(e => e.EventTimestamp).IsRequired().HasConversion(UtcTextConverter());
        @event.Property(e => e.ReceivedAt).IsRequired().HasConversion(UtcTextConverter());
    }

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, string>
        UtcTextConverter() => new(
            value => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
