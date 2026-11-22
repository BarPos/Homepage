using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homepage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ThemeDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftCustomCss",
                table: "themes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftFooterBlocks",
                table: "themes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftHeaderBlocks",
                table: "themes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftTokens",
                table: "themes",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftCustomCss",
                table: "themes");

            migrationBuilder.DropColumn(
                name: "DraftFooterBlocks",
                table: "themes");

            migrationBuilder.DropColumn(
                name: "DraftHeaderBlocks",
                table: "themes");

            migrationBuilder.DropColumn(
                name: "DraftTokens",
                table: "themes");
        }
    }
}
