using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homepage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageOptionInheritance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "NoIndex",
                table: "content_items",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<int>(
                name: "Layout",
                table: "content_items",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "InheritTheme",
                table: "content_items",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Settings made before inheritance existed: the defaults (header and footer, indexed) become "like the page
            // above", which is the same on a top-level page; a chosen theme stays chosen.
            migrationBuilder.Sql("UPDATE content_items SET \"Layout\" = NULL WHERE \"Layout\" = 0;");
            migrationBuilder.Sql("UPDATE content_items SET \"NoIndex\" = NULL WHERE \"NoIndex\" = false;");
            migrationBuilder.Sql("UPDATE content_items SET \"InheritTheme\" = false WHERE \"ThemeId\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE content_items SET \"Layout\" = 0 WHERE \"Layout\" IS NULL;");
            migrationBuilder.Sql("UPDATE content_items SET \"NoIndex\" = false WHERE \"NoIndex\" IS NULL;");
            migrationBuilder.DropColumn(
                name: "InheritTheme",
                table: "content_items");

            migrationBuilder.AlterColumn<bool>(
                name: "NoIndex",
                table: "content_items",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Layout",
                table: "content_items",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
