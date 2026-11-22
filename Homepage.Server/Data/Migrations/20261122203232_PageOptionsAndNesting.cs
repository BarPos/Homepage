using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homepage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageOptionsAndNesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_content_items_page_slug",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_content_items_ParentId",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_blogs_Slug",
                table: "blogs");

            migrationBuilder.AddColumn<string>(
                name: "CustomCss",
                table: "content_items",
                type: "character varying(100000)",
                maxLength: 100000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Layout",
                table: "content_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "NoIndex",
                table: "content_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ThemeId",
                table: "content_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentPageId",
                table: "blogs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_items_page_slug",
                table: "content_items",
                column: "Slug",
                unique: true,
                filter: "\"Type\" = 0 AND \"ParentId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_content_items_subpage_slug",
                table: "content_items",
                columns: new[] { "ParentId", "Slug" },
                unique: true,
                filter: "\"Type\" = 0 AND \"ParentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_content_items_ThemeId",
                table: "content_items",
                column: "ThemeId");

            migrationBuilder.CreateIndex(
                name: "IX_blogs_ParentPageId_Slug",
                table: "blogs",
                columns: new[] { "ParentPageId", "Slug" },
                unique: true,
                filter: "\"ParentPageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_blogs_Slug",
                table: "blogs",
                column: "Slug",
                unique: true,
                filter: "\"ParentPageId\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_blogs_content_items_ParentPageId",
                table: "blogs",
                column: "ParentPageId",
                principalTable: "content_items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_content_items_themes_ThemeId",
                table: "content_items",
                column: "ThemeId",
                principalTable: "themes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_blogs_content_items_ParentPageId",
                table: "blogs");

            migrationBuilder.DropForeignKey(
                name: "FK_content_items_themes_ThemeId",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_content_items_page_slug",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_content_items_subpage_slug",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_content_items_ThemeId",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_blogs_ParentPageId_Slug",
                table: "blogs");

            migrationBuilder.DropIndex(
                name: "IX_blogs_Slug",
                table: "blogs");

            migrationBuilder.DropColumn(
                name: "CustomCss",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "Layout",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "NoIndex",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "ThemeId",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "ParentPageId",
                table: "blogs");

            migrationBuilder.CreateIndex(
                name: "IX_content_items_page_slug",
                table: "content_items",
                column: "Slug",
                unique: true,
                filter: "\"Type\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_content_items_ParentId",
                table: "content_items",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_blogs_Slug",
                table: "blogs",
                column: "Slug",
                unique: true);
        }
    }
}
