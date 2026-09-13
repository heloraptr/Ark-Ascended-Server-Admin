using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkAscendedServerAdmin.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropServerGameLogFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaunchFlags_ServerGameLog",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "LaunchFlags_ServerGameLog",
                table: "Clusters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LaunchFlags_ServerGameLog",
                table: "Instances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LaunchFlags_ServerGameLog",
                table: "Clusters",
                type: "INTEGER",
                nullable: true);
        }
    }
}
