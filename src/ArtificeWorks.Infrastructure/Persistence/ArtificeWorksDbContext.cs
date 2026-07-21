using Microsoft.EntityFrameworkCore;

using ArtificeWorks.Domain.Models;
using ArtificeWorks.Domain.Models.Materials;
using ArtificeWorks.Domain.Models.Production;

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
    public DbSet<Component> Components => Set<Component>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<MaterialReservation> MaterialReservations => Set<MaterialReservation>();
    public DbSet<MaterialReservationLine> MaterialReservationLines => Set<MaterialReservationLine>();
    public DbSet<ProductionRun> ProductionRuns => Set<ProductionRun>();
    public DbSet<InspectionRun> InspectionRuns => Set<InspectionRun>();

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

            entity.Property(x => x.BuildAttempt)
                .IsRequired();

            // One-to-many since 6.1, not many-to-many. A serialized unit belongs to exactly
            // one work order for its whole life — this factory builds to order rather than
            // allocating finished units off a shared shelf — and the owning FK is what lets
            // the rework loop count *this* order's passing units cheaply.
            entity.HasMany(x => x.AssignedStock)
                .WithOne()
                .HasForeignKey("work_order_id")
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
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

            // Silently unmapped since 4.2: read-only properties are only picked up here when
            // configured explicitly, so every transition set an author the database never
            // stored. Mapped in 6.1 while a migration was being written anyway; rows written
            // before that keep the empty-string default, since their author is unrecoverable.
            entity.Property(x => x.CompletedBy)
                .HasMaxLength(200)
                .IsRequired();

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

        modelBuilder.Entity<Component>(entity =>
        {
            // Belt and braces: the conditional decrement is what *prevents* overselling, but
            // the check constraint makes "a shelf can't hold negative stock" a database
            // invariant — any future write path that forgets the guard fails loudly instead
            // of quietly going negative.
            entity.ToTable("components", table =>
                table.HasCheckConstraint("ck_components_on_hand_not_negative", "on_hand >= 0"));

            entity.HasKey(x => x.ComponentId);

            entity.Property(x => x.ComponentName)
                .IsRequired();

            // On-hand is the number the reservation path decrements with an atomic
            // conditional UPDATE (see MaterialReservationRepository) — the column name is
            // therefore part of that SQL's contract.
            entity.Property(x => x.OnHand)
                .HasColumnName("on_hand")
                .IsRequired();
        });

        modelBuilder.Entity<BomLine>(entity =>
        {
            entity.ToTable("bom_lines");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.QtyPerUnit)
                .IsRequired();

            entity.HasOne<Product>()
                .WithMany(x => x.BillOfMaterials)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Component)
                .WithMany()
                .HasForeignKey("component_id")
                .IsRequired();

            // One line per component per product — the BOM is a set, not a bag.
            entity.HasIndex(nameof(BomLine.ProductId), "component_id")
                .IsUnique();
        });

        modelBuilder.Entity<MaterialReservation>(entity =>
        {
            entity.ToTable("material_reservations");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.ReservedUtc)
                .IsRequired();

            // THE idempotency key (5.4): one pick per work order. A redelivered
            // WorkOrderScheduled tries to insert a second reservation for the same order and
            // is rejected by the database, so duplicate consumption cannot double-decrement
            // inventory even if two workers race past the pre-check simultaneously.
            entity.HasIndex(x => x.WorkOrderId)
                .IsUnique();

            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(x => x.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MaterialReservationLine>(entity =>
        {
            entity.ToTable("material_reservation_lines");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.ComponentId)
                .IsRequired();

            entity.Property(x => x.Quantity)
                .IsRequired();

            entity.HasOne<MaterialReservation>()
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
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

            entity.Property(x => x.Status)
                .HasConversion<string>()
                .IsRequired();

            entity.Property(x => x.BuiltUtc)
                .IsRequired();

            entity.Property(x => x.InspectedUtc);

            entity.Property(x => x.ScrapReason)
                .HasMaxLength(500);

            entity.Property(x => x.BuildAttempt)
                .IsRequired();

            // The rework loop's hot read: "which units of this attempt still need a verdict?"
            entity.HasIndex("work_order_id", nameof(StockKeepingUnit.BuildAttempt));
        });

        modelBuilder.Entity<ProductionRun>(entity =>
        {
            entity.ToTable("production_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.AttemptNumber)
                .IsRequired();

            entity.Property(x => x.UnitsBuilt)
                .IsRequired();

            entity.Property(x => x.BuiltUtc)
                .IsRequired();

            // THE idempotency key for a stage that legitimately repeats (6.4). Epic 5 could
            // key on the work order alone because picking happens once per order; production
            // does not, so the thing that must happen exactly once is an *attempt*. The row is
            // written in the same SaveChanges as the units it built, so marker and work still
            // commit atomically — 5.4's best property, kept.
            entity.HasIndex(x => new { x.WorkOrderId, x.AttemptNumber })
                .IsUnique();

            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(x => x.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InspectionRun>(entity =>
        {
            entity.ToTable("inspection_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .ValueGeneratedNever();

            entity.Property(x => x.AttemptNumber)
                .IsRequired();

            entity.Property(x => x.UnitsPassed)
                .IsRequired();

            entity.Property(x => x.UnitsScrapped)
                .IsRequired();

            entity.Property(x => x.InspectedUtc)
                .IsRequired();

            // Same key, same reasoning: one inspection per build attempt. Without it a
            // redelivered ProductionCompleted could re-decide the order-level outcome even
            // though every individual unit refuses a second verdict.
            entity.HasIndex(x => new { x.WorkOrderId, x.AttemptNumber })
                .IsUnique();

            entity.HasOne<WorkOrder>()
                .WithMany()
                .HasForeignKey(x => x.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}