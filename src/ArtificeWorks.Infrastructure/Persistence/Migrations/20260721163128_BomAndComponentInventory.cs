using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BomAndComponentInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "components",
                columns: table => new
                {
                    ComponentId = table.Column<string>(type: "text", nullable: false),
                    ComponentName = table.Column<string>(type: "text", nullable: false),
                    on_hand = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_components", x => x.ComponentId);
                    table.CheckConstraint("ck_components_on_hand_not_negative", "on_hand >= 0");
                });

            migrationBuilder.CreateTable(
                name: "material_reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_reservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_material_reservations_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bom_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    component_id = table.Column<string>(type: "text", nullable: false),
                    QtyPerUnit = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bom_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bom_lines_components_component_id",
                        column: x => x.component_id,
                        principalTable: "components",
                        principalColumn: "ComponentId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_bom_lines_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "material_reservation_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComponentId = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_reservation_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_material_reservation_lines_material_reservations_Reservatio~",
                        column: x => x.ReservationId,
                        principalTable: "material_reservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bom_lines_component_id",
                table: "bom_lines",
                column: "component_id");

            migrationBuilder.CreateIndex(
                name: "IX_bom_lines_ProductId_component_id",
                table: "bom_lines",
                columns: new[] { "ProductId", "component_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_reservation_lines_ReservationId",
                table: "material_reservation_lines",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_material_reservations_WorkOrderId",
                table: "material_reservations",
                column: "WorkOrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bom_lines");

            migrationBuilder.DropTable(
                name: "material_reservation_lines");

            migrationBuilder.DropTable(
                name: "components");

            migrationBuilder.DropTable(
                name: "material_reservations");
        }
    }
}
