using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Observability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "outbox_messages",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                table: "outbox_messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "TraceState",
                table: "outbox_messages");
        }
    }
}
