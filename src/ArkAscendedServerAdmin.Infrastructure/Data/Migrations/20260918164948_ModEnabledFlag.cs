using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkAscendedServerAdmin.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ModEnabledFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaunchFlags_PreventSpawnAnimations",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "LaunchFlags_PreventSpawnAnimations",
                table: "Clusters");

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "InstanceMods",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "ClusterMods",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "InstanceMods");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "ClusterMods");

            migrationBuilder.AddColumn<bool>(
                name: "LaunchFlags_PreventSpawnAnimations",
                table: "Instances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LaunchFlags_PreventSpawnAnimations",
                table: "Clusters",
                type: "INTEGER",
                nullable: true);
        }
    }
}
