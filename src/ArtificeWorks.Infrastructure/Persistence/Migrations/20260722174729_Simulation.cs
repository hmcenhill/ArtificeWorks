using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Simulation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 'Visitor', not the scaffolder's empty string: every order that existed before the
            // simulation did was asked for by a person, and an empty string does not map back to
            // the enum at all — it would throw on the first read of an old row.
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "work_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Visitor");

            migrationBuilder.AddColumn<long>(
                name: "seed_on_hand",
                table: "components",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Backfilled from current stock rather than left at 0. A seed level of 0 would make
            // 10.4's restock a permanent no-op on an existing database — the sweep only ever raises
            // stock towards this number — and the demo would quietly go on emptying its shelves.
            // "What this factory has now is what it starts with" is the only answer available here;
            // a fresh database gets the real figures from CatalogSeeder.
            migrationBuilder.Sql("UPDATE components SET seed_on_hand = on_hand;");

            migrationBuilder.CreateTable(
                name: "simulation_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PacingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PaceSecondsScheduled = table.Column<double>(type: "double precision", nullable: false),
                    PaceSecondsMaterialsReserved = table.Column<double>(type: "double precision", nullable: false),
                    PaceSecondsProductionCompleted = table.Column<double>(type: "double precision", nullable: false),
                    PaceSecondsReworkRequired = table.Column<double>(type: "double precision", nullable: false),
                    PaceSecondsInspectionPassed = table.Column<double>(type: "double precision", nullable: false),
                    PaceSecondsShipmentScheduled = table.Column<double>(type: "double precision", nullable: false),
                    PaceJitter = table.Column<double>(type: "double precision", nullable: false),
                    FailureRate = table.Column<double>(type: "double precision", nullable: false),
                    AutoInspect = table.Column<bool>(type: "boolean", nullable: false),
                    RefusalRate = table.Column<double>(type: "double precision", nullable: false),
                    AutoBook = table.Column<bool>(type: "boolean", nullable: false),
                    MaxRebuildAttempts = table.Column<int>(type: "integer", nullable: false),
                    GenerationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    GenerationIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxInFlight = table.Column<int>(type: "integer", nullable: false),
                    WorldSweepIntervalHours = table.Column<int>(type: "integer", nullable: false),
                    RetireAfterHours = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_settings", x => x.Id);
                    table.CheckConstraint("ck_simulation_settings_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_Origin",
                table: "work_orders",
                column: "Origin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "simulation_settings");

            migrationBuilder.DropIndex(
                name: "IX_work_orders_Origin",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "seed_on_hand",
                table: "components");
        }
    }
}
