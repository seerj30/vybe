using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <inheritdoc />
    public partial class Achievements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "vybe",
                table: "achievements",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "reward_xp",
                schema: "vybe",
                table: "achievements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "vybe",
                table: "achievements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "content_markers",
                schema: "vybe",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_markers", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "player_stats",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stat = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_stats", x => new { x.player_id, x.stat });
                    table.CheckConstraint("ck_player_stats_stat", "stat ~ '^[a-z0-9_:-]{1,64}$'");
                    table.CheckConstraint("ck_player_stats_value", "value >= 0");
                    table.ForeignKey(
                        name: "fk_player_stats_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_achievements_reward_xp",
                schema: "vybe",
                table: "achievements",
                sql: "reward_xp >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_markers",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_stats",
                schema: "vybe");

            migrationBuilder.DropCheckConstraint(
                name: "ck_achievements_reward_xp",
                schema: "vybe",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "vybe",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "reward_xp",
                schema: "vybe",
                table: "achievements");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "vybe",
                table: "achievements");
        }
    }
}
