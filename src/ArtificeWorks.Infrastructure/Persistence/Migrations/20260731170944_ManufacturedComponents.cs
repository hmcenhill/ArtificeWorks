using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArtificeWorks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 13.2 — the whole multi-level BOM, in one nullable column. <c>components.make_product_id</c>
    /// names the product that builds a component; null means bought in, which is what every
    /// existing row is and what the large majority stay.
    /// <para>
    /// <strong>Nothing else moves.</strong> <c>bom_lines</c> is untouched — no polymorphic line, no
    /// discriminator, no second foreign key — so every read path that resolves a BOM keeps working
    /// and picking keeps drawing a made component off the shelf exactly as it draws a bought one.
    /// The tree is a walk over the same rows.
    /// </para>
    /// <para>
    /// The foreign key restricts rather than cascades: deleting a sub-assembly product would
    /// otherwise take the component it makes with it, and with that every <c>bom_lines</c> row
    /// across three product lines that calls for it. That deletion should fail loudly.
    /// </para>
    /// </summary>
    public partial class ManufacturedComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable with no default: existing components are bought parts, which is both the
            // true reading of them and the reading that changes nothing.
            migrationBuilder.AddColumn<string>(
                name: "make_product_id",
                table: "components",
                type: "text",
                nullable: true);

            // Filtered: sub-assemblies are the minority of components, and the explosion looks them
            // up by product. Nulls — the common row — stay out of the index entirely.
            migrationBuilder.CreateIndex(
                name: "IX_components_make_product_id",
                table: "components",
                column: "make_product_id",
                filter: "make_product_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_components_products_make_product_id",
                table: "components",
                column: "make_product_id",
                principalTable: "products",
                principalColumn: "ItemId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Clean, unlike 13.1's: dropping the column flattens the catalog back to bought parts,
        /// which is precisely what it meant before this migration. The seeded sub-assembly
        /// *products* survive as ordinary products with BOMs that nothing consumes — orphaned, but
        /// not wrong, and re-running the seeder restores the links.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_components_products_make_product_id",
                table: "components");

            migrationBuilder.DropIndex(
                name: "IX_components_make_product_id",
                table: "components");

            migrationBuilder.DropColumn(
                name: "make_product_id",
                table: "components");
        }
    }
}
