using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Homepage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_salts",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_salts", x => x.Day);
                });

            migrationBuilder.CreateTable(
                name: "page_views",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VisitorId = table.Column<long>(type: "bigint", nullable: false),
                    SessionId = table.Column<long>(type: "bigint", nullable: false),
                    IsEntry = table.Column<bool>(type: "boolean", nullable: false),
                    Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ReferrerHost = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UtmSource = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    Browser = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Os = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Device = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EngagedSeconds = table.Column<int>(type: "integer", nullable: false),
                    PageKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_views", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_page_views_AtUtc",
                table: "page_views",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_page_views_VisitorId_AtUtc",
                table: "page_views",
                columns: new[] { "VisitorId", "AtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_salts");

            migrationBuilder.DropTable(
                name: "page_views");
        }
    }
}
