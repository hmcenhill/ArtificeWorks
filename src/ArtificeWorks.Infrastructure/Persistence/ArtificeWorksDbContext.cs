using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;

namespace ArtificeWorks.Infrastructure.Persistence;

public class ArtificeWorksDbContext : DbContext
{
    public ArtificeWorksDbContext(DbContextOptions<ArtificeWorksDbContext> options)
        : base(options)
    {
    }

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderStateHistory> OrderStateHistory => Set<WorkOrderStateHistory>();
    public DbSet<StockKeepingUnit> StockKeepingUnits => Set<StockKeepingUnit>();
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WorkOrder>(entity =>
        {
            entity.ToTable("work_orders");

            entity.HasKey(x => x.Id);

            // The domain generates aggregate ids (Guid.NewGuid() in the constructor),
            // so the store must never generate them. Without this, EF treats a
            // client-set Guid key on a graph child as an existing row and issues an
            // UPDATE instead of an INSERT when new state history is appended.
            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.CurrentStatus)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(x => x.PreviousStatus)
                .HasConversion<string>();

            entity.Property(x => x.CreatedUtc)
                .IsRequired();

            entity.Property(x => x.UpdatedUtc)
                .IsRequired();

            entity.Property<string>("ordered_item_id");

            entity.HasOne(x => x.OrderedItem)
                .WithMany()
                .HasForeignKey("ordered_item_id")
                .IsRequired();

            entity.Property(x => x.OrderItemQty)
                .IsRequired();

            entity.HasMany(x => x.AssignedStock)
                .WithMany()
                .UsingEntity("work_order_skus");
        });

        modelBuilder.Entity<WorkOrderStateHistory>(entity =>
        {
            entity.ToTable("work_order_state_history");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.Status)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(x => x.ChangedUtc)
                .IsRequired();

            entity.Property(x => x.Notes)
                .HasMaxLength(500);

            entity.HasOne(x => x.WorkOrder)
                .WithMany(x => x.StateHistory)
                .HasForeignKey(x => x.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.WorkOrderId, x.ChangedUtc });
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");

            entity.HasKey(x => x.ItemId);

            entity.Property(x => x.ItemName)
                .IsRequired();
        });

        modelBuilder.Entity<StockKeepingUnit>(entity =>
        {
            entity.ToTable("skus");

            entity.HasKey(x => x.SerialNumber);

            entity.Property(x => x.SerialNumber)
                .ValueGeneratedNever();

            entity.Property<string>("product_item_id");

            entity.HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey("product_item_id")
                .IsRequired();
        });
    }
}