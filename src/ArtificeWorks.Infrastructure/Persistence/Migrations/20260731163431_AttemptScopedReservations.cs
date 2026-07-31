using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 13.1 — the picking stage's idempotency key widens from the work order to the build attempt,
    /// so a rebuild can draw its own parts. This is the index Epic 5 built its concurrency argument
    /// on: one reservation per order was what made a redelivered scheduling event collide instead of
    /// double-picking. The argument is unchanged, only its scope — one reservation per
    /// <c>(order, attempt)</c> — because the thing that must happen once is now the pick for an
    /// attempt, not the pick for an order.
    /// </summary>
    public partial class AttemptScopedReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_material_reservations_WorkOrderId",
                table: "material_reservations");

            // Existing rows backfill to attempt 1 — the only reading that is true of them, since the
            // index dropped above made a second pick per order impossible. That is also why the
            // backfill cannot violate the new unique index below.
            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                table: "material_reservations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_material_reservations_WorkOrderId_AttemptNumber",
                table: "material_reservations",
                columns: new[] { "WorkOrderId", "AttemptNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_material_reservations_WorkOrderId_AttemptNumber",
                table: "material_reservations");

            // Lossy, and it fails loudly rather than quietly discarding picks: any order that went
            // round the rework loop has more than one reservation, and re-creating the order-scoped
            // unique index rejects it. There is no correct way to collapse two real draws into one
            // row, so the honest down migration is one that refuses.
            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                table: "material_reservations");

            migrationBuilder.CreateIndex(
                name: "IX_material_reservations_WorkOrderId",
                table: "material_reservations",
                column: "WorkOrderId",
                unique: true);
        }
    }
}
