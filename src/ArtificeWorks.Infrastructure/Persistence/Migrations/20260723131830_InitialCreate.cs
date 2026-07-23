using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

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
                name: "components",
                columns: table => new
                {
                    ComponentId = table.Column<string>(type: "text", nullable: false),
                    ComponentName = table.Column<string>(type: "text", nullable: false),
                    on_hand = table.Column<long>(type: "bigint", nullable: false),
                    seed_on_hand = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_components", x => x.ComponentId);
                    table.CheckConstraint("ck_components_on_hand_not_negative", "on_hand >= 0");
                });

            migrationBuilder.CreateTable(
                name: "dead_letters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ParkedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReplayedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplayCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    ResponseLocation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TraceParent = table.Column<string>(type: "character varying(55)", maxLength: 55, nullable: true),
                    TraceState = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

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
                name: "work_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentStatus = table.Column<string>(type: "text", nullable: false),
                    PreviousStatus = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ordered_item_id = table.Column<string>(type: "text", nullable: false),
                    OrderItemQty = table.Column<long>(type: "bigint", nullable: false),
                    Origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BuildAttempt = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Carrier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BookedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstimatedArrivalUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shipments_work_orders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "work_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "skus",
                columns: table => new
                {
                    SerialNumber = table.Column<Guid>(type: "uuid", nullable: false),
                    product_item_id = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BuiltUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InspectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScrapReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BuildAttempt = table.Column<int>(type: "integer", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                    table.ForeignKey(
                        name: "FK_skus_work_orders_work_order_id",
                        column: x => x.work_order_id,
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
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CompletedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "shipment_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SerialNumber = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipment_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shipment_lines_shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "shipments",
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
                name: "IX_dead_letters_ParkedUtc",
                table: "dead_letters",
                column: "ParkedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_dead_letters_WorkOrderId",
                table: "dead_letters",
                column: "WorkOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_keys_Key",
                table: "idempotency_keys",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inspection_runs_WorkOrderId_AttemptNumber",
                table: "inspection_runs",
                columns: new[] { "WorkOrderId", "AttemptNumber" },
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

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unsent",
                table: "outbox_messages",
                column: "Id",
                filter: "\"SentUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_runs_WorkOrderId_AttemptNumber",
                table: "production_runs",
                columns: new[] { "WorkOrderId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shipment_lines_ShipmentId",
                table: "shipment_lines",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_shipments_WorkOrderId",
                table: "shipments",
                column: "WorkOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skus_product_item_id",
                table: "skus",
                column: "product_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_skus_work_order_id_BuildAttempt",
                table: "skus",
                columns: new[] { "work_order_id", "BuildAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_state_history_WorkOrderId_ChangedUtc",
                table: "work_order_state_history",
                columns: new[] { "WorkOrderId", "ChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_ordered_item_id",
                table: "work_orders",
                column: "ordered_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_Origin",
                table: "work_orders",
                column: "Origin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bom_lines");

            migrationBuilder.DropTable(
                name: "dead_letters");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "inspection_runs");

            migrationBuilder.DropTable(
                name: "material_reservation_lines");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "production_runs");

            migrationBuilder.DropTable(
                name: "shipment_lines");

            migrationBuilder.DropTable(
                name: "simulation_settings");

            migrationBuilder.DropTable(
                name: "skus");

            migrationBuilder.DropTable(
                name: "work_order_state_history");

            migrationBuilder.DropTable(
                name: "components");

            migrationBuilder.DropTable(
                name: "material_reservations");

            migrationBuilder.DropTable(
                name: "shipments");

            migrationBuilder.DropTable(
                name: "work_orders");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
