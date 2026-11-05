using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Payments.Infra;

public class FudisDbContext : DbContext
{
    public FudisDbContext(DbContextOptions<FudisDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<DriverEarning> DriverEarnings => Set<DriverEarning>();

    public DbSet<PaymentInbox> PaymentInbox => Set<PaymentInbox>();

    public DbSet<RestaurantNotificationOutbox> RestaurantNotificationOutbox => Set<RestaurantNotificationOutbox>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(o => o.DeliveryFee).HasColumnType("decimal(18,2)");
            entity.Property(o => o.TotalAmount).HasColumnType("decimal(18,2)");
            entity.Property(o => o.Currency).HasMaxLength(3).IsRequired();
            entity.Property(o => o.RowVersion).IsRowVersion();
            entity.HasIndex(o => o.DriverId);
        });

        modelBuilder.Entity<DriverEarning>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();
            // Money guarantee: at most one earning per order, enforced by the engine, not by application code.
            entity.HasIndex(e => e.OrderId).IsUnique();
            entity.HasOne<Order>()
                  .WithMany()
                  .HasForeignKey(e => e.OrderId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentInbox>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Provider).HasMaxLength(32).IsRequired();
            entity.Property(i => i.ProviderEventId).HasMaxLength(128).IsRequired();
            entity.Property(i => i.Amount).HasColumnType("decimal(18,2)");
            entity.Property(i => i.Currency).HasMaxLength(3).IsRequired();
            // Idempotency guarantee: one gateway event is applied at most once.
            entity.HasIndex(i => new { i.Provider, i.ProviderEventId }).IsUnique();
        });

        modelBuilder.Entity<RestaurantNotificationOutbox>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Type).HasMaxLength(64).IsRequired();
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(o => o.Payload).IsRequired();
            entity.HasIndex(o => new { o.OrderId, o.Type }).IsUnique();
            entity.HasIndex(o => new { o.Status, o.NextAttemptAt });
        });
    }
}
