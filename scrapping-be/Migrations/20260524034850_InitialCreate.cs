using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace scrapping_be.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Listings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Bedrooms = table.Column<int>(type: "INTEGER", nullable: true),
                    PropertyType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AvailableFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ListingUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ScrapedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Listings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_ListingUrl",
                table: "Listings",
                column: "ListingUrl",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Listings_ScrapedAt",
                table: "Listings",
                column: "ScrapedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Listings");
        }
    }
}
