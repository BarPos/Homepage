using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Homepage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Blogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_content_items_Type_Slug",
                table: "content_items");

            migrationBuilder.AddColumn<int>(
                name: "BlogId",
                table: "content_items",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "blogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PostsPerPage = table.Column<int>(type: "integer", nullable: false),
                    PreviousSlugs = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blogs", x => x.Id);
                });

            // Every site starts with one blog at /blog, and the posts that existed before blogs belong to it.
            // Done here (not in the seeder), because upgrades run only --migrate-only.
            migrationBuilder.Sql("""
                INSERT INTO blogs ("Name", "Slug", "Description", "PostsPerPage", "PreviousSlugs", "CreatedAtUtc")
                VALUES ('Blog', 'blog', '', 10, '{}', now() at time zone 'utc');
                UPDATE content_items SET "BlogId" = (SELECT "Id" FROM blogs WHERE "Slug" = 'blog') WHERE "Type" = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_content_items_page_slug",
                table: "content_items",
                column: "Slug",
                unique: true,
                filter: "\"Type\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_content_items_post_slug",
                table: "content_items",
                columns: new[] { "BlogId", "Slug" },
                unique: true,
                filter: "\"Type\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_blogs_Slug",
                table: "blogs",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_content_items_blogs_BlogId",
                table: "content_items",
                column: "BlogId",
                principalTable: "blogs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_content_items_blogs_BlogId",
                table: "content_items");

            migrationBuilder.DropTable(
                name: "blogs");

            migrationBuilder.DropIndex(
                name: "IX_content_items_page_slug",
                table: "content_items");

            migrationBuilder.DropIndex(
                name: "IX_content_items_post_slug",
                table: "content_items");

            migrationBuilder.DropColumn(
                name: "BlogId",
                table: "content_items");

            migrationBuilder.CreateIndex(
                name: "IX_content_items_Type_Slug",
                table: "content_items",
                columns: new[] { "Type", "Slug" },
                unique: true);
        }
    }
}
