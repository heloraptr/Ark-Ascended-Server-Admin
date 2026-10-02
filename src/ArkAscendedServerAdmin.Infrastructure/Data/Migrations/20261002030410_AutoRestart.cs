using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkAscendedServerAdmin.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoRestart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRestart",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoRestart",
                table: "Instances");
        }
    }
}
