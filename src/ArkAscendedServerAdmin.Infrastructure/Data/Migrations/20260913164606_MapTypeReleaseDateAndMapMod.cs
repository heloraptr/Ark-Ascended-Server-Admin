using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkAscendedServerAdmin.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MapTypeReleaseDateAndMapMod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsStory",
                table: "Maps",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModId",
                table: "Maps",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReleaseDate",
                table: "Maps",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsStory",
                table: "Maps");

            migrationBuilder.DropColumn(
                name: "ModId",
                table: "Maps");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "Maps");
        }
    }
}
