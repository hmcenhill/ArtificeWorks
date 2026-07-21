using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Epic 6. Gives serialized units a lifecycle (status, build attempt, verdict timestamps),
    /// re-homes them from a many-to-many join table onto an owning work-order FK, adds the
    /// attempt-keyed production and inspection run tables that carry the epic's idempotency
    /// guarantees, and finally maps <c>WorkOrderStateHistory.CompletedBy</c> — silently unmapped
    /// since 4.2, so every transition has been recording an author the database never stored.
    /// </summary>
    public partial class ProductionAndInspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_skus");

            // Existing serialized units cannot be re-homed: the many-to-many table that said
            // which order they belonged to is gone, and the new owning FK has no valid value to
            // backfill. In practice there are none — nothing in Epics 1–5 ever created a SKU,
            // AssignSku was only ever exercised by unit tests — so clear the table rather than
            // add a NOT NULL FK pointing at a work order that does not exist.
            migrationBuilder.Sql("DELETE FROM skus;");

            migrationBuilder.AddColumn<int>(
                name: "BuildAttempt",
                table: "work_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CompletedBy",
                table: "work_order_state_history",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BuildAttempt",
                table: "skus",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BuiltUtc",
                table: "skus",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "InspectedUtc",
                table: "skus",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScrapReason",
                table: "skus",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "skus",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "work_order_id",
                table: "skus",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "inspection_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    UnitsPassed = table.Column<long>(type: "bigint", nullable: false),
                    UnitsScrapped = table.Column<long>(type: "bigint", nullable: false),
                    InspectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_inspection_runs_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    UnitsBuilt = table.Column<long>(type: "bigint", nullable: false),
                    BuiltUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_runs_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skus_work_order_id_BuildAttempt",
                table: "skus",
                columns: new[] { "work_order_id", "BuildAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_runs_WorkOrderId_AttemptNumber",
                table: "inspection_runs",
                columns: new[] { "WorkOrderId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_runs_WorkOrderId_AttemptNumber",
                table: "production_runs",
                columns: new[] { "WorkOrderId", "AttemptNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_skus_work_orders_work_order_id",
                table: "skus",
                column: "work_order_id",
                principalTable: "work_orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_skus_work_orders_work_order_id",
                table: "skus");

            migrationBuilder.DropTable(
                name: "inspection_runs");

            migrationBuilder.DropTable(
                name: "production_runs");

            migrationBuilder.DropIndex(
                name: "IX_skus_work_order_id_BuildAttempt",
                table: "skus");

            migrationBuilder.DropColumn(
                name: "BuildAttempt",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                table: "work_order_state_history");

            migrationBuilder.DropColumn(
                name: "BuildAttempt",
                table: "skus");

            migrationBuilder.DropColumn(
                name: "BuiltUtc",
                table: "skus");

            migrationBuilder.DropColumn(
                name: "InspectedUtc",
                table: "skus");

            migrationBuilder.DropColumn(
                name: "ScrapReason",
                table: "skus");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "skus");

            migrationBuilder.DropColumn(
                name: "work_order_id",
                table: "skus");

            migrationBuilder.CreateTable(
                name: "work_order_skus",
                columns: table => new
                {
                    AssignedStockSerialNumber = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_skus", x => new { x.AssignedStockSerialNumber, x.WorkOrderId });
                    table.ForeignKey(
                        name: "FK_work_order_skus_skus_AssignedStockSerialNumber",
                        column: x => x.AssignedStockSerialNumber,
                        principalTable: "skus",
                        principalColumn: "SerialNumber",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_work_order_skus_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_skus_WorkOrderId",
                table: "work_order_skus",
                column: "WorkOrderId");
        }
    }
}
