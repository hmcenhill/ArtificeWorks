using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReliabilityAndRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: the scaffolder emitted an `AddColumn<uint>("xmin", "work_orders")` here and it
            // has been deleted by hand. `xmin` is a Postgres *system* column that every table
            // already has — trying to add it fails with "column name xmin conflicts with a system
            // column name". Mapping it in the model is the whole trick: the work order gets an
            // optimistic-concurrency token (8.1) with no schema change at all. If a future
            // scaffold puts this line back, delete it again.

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
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

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
                name: "ix_outbox_messages_unsent",
                table: "outbox_messages",
                column: "Id",
                filter: "\"SentUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letters");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            // No DropColumn for xmin, for the same reason there is no AddColumn: it was never ours.
        }
    }
}
