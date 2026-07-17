using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    ItemId = table.Column<string>(type: "text", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.ItemId);
                });

            migrationBuilder.CreateTable(
                name: "skus",
                columns: table => new
                {
                    SerialNumber = table.Column<Guid>(type: "uuid", nullable: false),
                    product_item_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skus", x => x.SerialNumber);
                    table.ForeignKey(
                        name: "FK_skus_products_product_item_id",
                        column: x => x.product_item_id,
                        principalTable: "products",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentStatus = table.Column<string>(type: "text", nullable: false),
                    PreviousStatus = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ordered_item_id = table.Column<string>(type: "text", nullable: false),
                    OrderItemQty = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_orders_products_ordered_item_id",
                        column: x => x.ordered_item_id,
                        principalTable: "products",
                        principalColumn: "ItemId",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "work_order_state_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_state_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_order_state_history_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skus_product_item_id",
                table: "skus",
                column: "product_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_order_skus_WorkOrderId",
                table: "work_order_skus",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_work_order_state_history_WorkOrderId_ChangedUtc",
                table: "work_order_state_history",
                columns: new[] { "WorkOrderId", "ChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_ordered_item_id",
                table: "work_orders",
                column: "ordered_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_skus");

            migrationBuilder.DropTable(
                name: "work_order_state_history");

            migrationBuilder.DropTable(
                name: "skus");

            migrationBuilder.DropTable(
                name: "work_orders");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
