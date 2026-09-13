using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArkAscendedServerAdmin.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Clusters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClusterKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AdminWhitelist = table.Column<string>(type: "TEXT", nullable: false),
                    LaunchFlags_NoBattlEye = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ServerPlatform = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LaunchFlags_ExclusiveJoin = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_NoWildBabies = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_PreventSpawnAnimations = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_UseStore = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ConvertToStore = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ServerGameLogIncludeTribeLogs = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ServerRconOutputTribeLogs = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ActiveEvent = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LaunchFlags_AdditionalArgs = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clusters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Entries = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Maps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsOfficial = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsStory = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ModId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModLibrary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DateModified = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModLibrary", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: true),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GamePort = table.Column<int>(type: "INTEGER", nullable: false),
                    RconPort = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxPlayers = table.Column<int>(type: "INTEGER", nullable: false),
                    AdminWhitelist = table.Column<string>(type: "TEXT", nullable: false),
                    LaunchFlags_NoBattlEye = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ServerPlatform = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LaunchFlags_ExclusiveJoin = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_NoWildBabies = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_PreventSpawnAnimations = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_UseStore = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ConvertToStore = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ServerGameLogIncludeTribeLogs = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ServerRconOutputTribeLogs = table.Column<bool>(type: "INTEGER", nullable: true),
                    LaunchFlags_ActiveEvent = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LaunchFlags_AdditionalArgs = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    BackupIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    BackupRetention = table.Column<int>(type: "INTEGER", nullable: true),
                    LastPid = table.Column<int>(type: "INTEGER", nullable: true),
                    LastLaunchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastProcessStartTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Instances_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Instances_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClusterMods",
                columns: table => new
                {
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterMods", x => new { x.ClusterId, x.ModId });
                    table.ForeignKey(
                        name: "FK_ClusterMods_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClusterMods_ModLibrary_ModId",
                        column: x => x.ModId,
                        principalTable: "ModLibrary",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackupRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsManual = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupRecords_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExtraOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    File = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Section = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtraOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtraOverrides_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IniDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClusterId = table.Column<int>(type: "INTEGER", nullable: true),
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: true),
                    File = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IniDocuments", x => x.Id);
                    table.CheckConstraint("CK_IniDocuments_SingleOwner", "(ClusterId IS NULL) <> (InstanceId IS NULL)");
                    table.ForeignKey(
                        name: "FK_IniDocuments_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IniDocuments_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "InstanceMods",
                columns: table => new
                {
                    InstanceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanceMods", x => new { x.InstanceId, x.ModId });
                    table.ForeignKey(
                        name: "FK_InstanceMods_Instances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InstanceMods_ModLibrary_ModId",
                        column: x => x.ModId,
                        principalTable: "ModLibrary",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnownPlayers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EosId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastJoinedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastLeftAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastInstanceId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownPlayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnownPlayers_Instances_LastInstanceId",
                        column: x => x.LastInstanceId,
                        principalTable: "Instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRecords_InstanceId_CreatedAt",
                table: "BackupRecords",
                columns: new[] { "InstanceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClusterMods_ModId",
                table: "ClusterMods",
                column: "ModId");

            migrationBuilder.CreateIndex(
                name: "IX_Clusters_Name",
                table: "Clusters",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clusters_Slug",
                table: "Clusters",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExtraOverrides_InstanceId",
                table: "ExtraOverrides",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_IniDocuments_ClusterId_File",
                table: "IniDocuments",
                columns: new[] { "ClusterId", "File" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IniDocuments_InstanceId_File",
                table: "IniDocuments",
                columns: new[] { "InstanceId", "File" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstanceMods_ModId",
                table: "InstanceMods",
                column: "ModId");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_ClusterId",
                table: "Instances",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_MapId",
                table: "Instances",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Name",
                table: "Instances",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instances_Slug",
                table: "Instances",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnownPlayers_EosId",
                table: "KnownPlayers",
                column: "EosId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnownPlayers_LastInstanceId",
                table: "KnownPlayers",
                column: "LastInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Maps_Key",
                table: "Maps",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "BackupRecords");

            migrationBuilder.DropTable(
                name: "ClusterMods");

            migrationBuilder.DropTable(
                name: "ExtraOverrides");

            migrationBuilder.DropTable(
                name: "IniDocuments");

            migrationBuilder.DropTable(
                name: "InstanceMods");

            migrationBuilder.DropTable(
                name: "KnownPlayers");

            migrationBuilder.DropTable(
                name: "MaintenanceState");

            migrationBuilder.DropTable(
                name: "ModLibrary");

            migrationBuilder.DropTable(
                name: "Instances");

            migrationBuilder.DropTable(
                name: "Clusters");

            migrationBuilder.DropTable(
                name: "Maps");
        }
    }
}
