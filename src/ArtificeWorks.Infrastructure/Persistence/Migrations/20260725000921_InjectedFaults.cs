using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InjectedFaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "injected_faults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ArmedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArmedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_injected_faults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_injected_faults_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_injected_faults_WorkOrderId",
                table: "injected_faults",
                column: "WorkOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "injected_faults");
        }
    }
}
