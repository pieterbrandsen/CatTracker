using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    RaisedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RawSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CapturedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: false),
                    FindMyName = table.Column<string>(type: "TEXT", nullable: false),
                    PetName = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Zones",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CenterLat = table.Column<double>(type: "REAL", nullable: false),
                    CenterLon = table.Column<double>(type: "REAL", nullable: false),
                    RadiusM = table.Column<double>(type: "REAL", nullable: false),
                    ExitBufferM = table.Column<double>(type: "REAL", nullable: false),
                    NotifyOnExit = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnEnter = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Zones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Excursions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TagId = table.Column<long>(type: "INTEGER", nullable: false),
                    DepartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ReturnedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxDistanceM = table.Column<double>(type: "REAL", nullable: false),
                    FixCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverageRatio = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Excursions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Excursions_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Fixes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TagId = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    HorizontalAccuracy = table.Column<double>(type: "REAL", nullable: true),
                    Altitude = table.Column<double>(type: "REAL", nullable: true),
                    PositionType = table.Column<string>(type: "TEXT", nullable: true),
                    IsOld = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsInaccurate = table.Column<bool>(type: "INTEGER", nullable: false),
                    BatteryStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    IngestedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fixes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Fixes_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ZoneEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TagId = table.Column<long>(type: "INTEGER", nullable: false),
                    ZoneId = table.Column<long>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    FixId = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZoneEvents_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ZoneEvents_Zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ZoneStates",
                columns: table => new
                {
                    TagId = table.Column<long>(type: "INTEGER", nullable: false),
                    ZoneId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZoneStates", x => new { x.TagId, x.ZoneId });
                    table.ForeignKey(
                        name: "FK_ZoneStates_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ZoneStates_Zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_RaisedUtc",
                table: "Alerts",
                column: "RaisedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Excursions_TagId_DepartedUtc",
                table: "Excursions",
                columns: new[] { "TagId", "DepartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Fixes_TagId_TimestampUtc",
                table: "Fixes",
                columns: new[] { "TagId", "TimestampUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawSnapshots_CapturedUtc",
                table: "RawSnapshots",
                column: "CapturedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_SerialNumber",
                table: "Tags",
                column: "SerialNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZoneEvents_TagId_OccurredUtc",
                table: "ZoneEvents",
                columns: new[] { "TagId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ZoneEvents_ZoneId",
                table: "ZoneEvents",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_ZoneStates_ZoneId",
                table: "ZoneStates",
                column: "ZoneId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "Excursions");

            migrationBuilder.DropTable(
                name: "Fixes");

            migrationBuilder.DropTable(
                name: "RawSnapshots");

            migrationBuilder.DropTable(
                name: "ZoneEvents");

            migrationBuilder.DropTable(
                name: "ZoneStates");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Zones");
        }
    }
}
