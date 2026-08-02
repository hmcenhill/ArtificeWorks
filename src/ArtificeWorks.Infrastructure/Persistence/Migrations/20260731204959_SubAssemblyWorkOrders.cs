using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubAssemblyWorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "for_component_id",
                table: "work_orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parent_attempt_number",
                table: "work_orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_work_order_id",
                table: "work_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tree_depth",
                table: "work_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_for_component_id",
                table: "work_orders",
                column: "for_component_id");

            migrationBuilder.CreateIndex(
                name: "ux_work_orders_open_sub_assembly_request",
                table: "work_orders",
                columns: new[] { "parent_work_order_id", "parent_attempt_number", "for_component_id" },
                unique: true,
                filter: "parent_work_order_id IS NOT NULL AND \"CurrentStatus\" NOT IN ('Completed', 'Cancelled')");

            migrationBuilder.AddForeignKey(
                name: "FK_work_orders_components_for_component_id",
                table: "work_orders",
                column: "for_component_id",
                principalTable: "components",
                principalColumn: "ComponentId",
                onDelete: ReferentialAction.Restrict);

            // Note the absence of an ON DELETE clause: this self-reference is deliberately NO ACTION
            // rather than CASCADE or RESTRICT. CASCADE would let 10.4's world sweep retire a held
            // parent and silently take a running child with it. RESTRICT is checked row by row, so
            // the sweep's single DELETE would fail even when it removes a whole terminal tree at
            // once. NO ACTION is checked at the end of the statement, which allows exactly that and
            // refuses exactly the case that matters — a parent whose child is still live.
            migrationBuilder.AddForeignKey(
                name: "FK_work_orders_work_orders_parent_work_order_id",
                table: "work_orders",
                column: "parent_work_order_id",
                principalTable: "work_orders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_work_orders_components_for_component_id",
                table: "work_orders");

            migrationBuilder.DropForeignKey(
                name: "FK_work_orders_work_orders_parent_work_order_id",
                table: "work_orders");

            migrationBuilder.DropIndex(
                name: "IX_work_orders_for_component_id",
                table: "work_orders");

            migrationBuilder.DropIndex(
                name: "ux_work_orders_open_sub_assembly_request",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "for_component_id",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "parent_attempt_number",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "parent_work_order_id",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "tree_depth",
                table: "work_orders");
        }
    }
}
