using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkAscendedServerAdmin.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OverridesClusterSchedule",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ScheduledActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: true),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Command = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    WarningMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledActions", x => x.Id);
                    table.CheckConstraint("CK_ScheduledActions_SingleOwner", "(ClusterId IS NULL) <> (InstanceId IS NULL)");
                    table.ForeignKey(
                        name: "FK_ScheduledActions_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduledActions_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledActionRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScheduledActionId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledActionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledActionRuns_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScheduledActionRuns_ScheduledActions_ScheduledActionId",
                        column: x => x.ScheduledActionId,
                        principalTable: "ScheduledActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActionRuns_InstanceId_StartedAt",
                table: "ScheduledActionRuns",
                columns: new[] { "InstanceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActionRuns_ScheduledActionId_InstanceId_ScheduledFor",
                table: "ScheduledActionRuns",
                columns: new[] { "ScheduledActionId", "InstanceId", "ScheduledFor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActions_ClusterId",
                table: "ScheduledActions",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActions_InstanceId",
                table: "ScheduledActions",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledActionRuns");

            migrationBuilder.DropTable(
                name: "ScheduledActions");

            migrationBuilder.DropColumn(
                name: "OverridesClusterSchedule",
                table: "Instances");
        }
    }
}
