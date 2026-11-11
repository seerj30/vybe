using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <inheritdoc />
    public partial class FriendsAndLeaderboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "friend_request_counters",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    sent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friend_request_counters", x => new { x.player_id, x.day });
                    table.CheckConstraint("ck_friend_request_counters_sent", "sent >= 0");
                    table.ForeignKey(
                        name: "fk_friend_request_counters_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "friendships",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    addressee_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    suppressed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    responded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_friendships", x => x.id);
                    table.CheckConstraint("ck_friendships_not_self", "requester_player_id <> addressee_player_id");
                    table.CheckConstraint("ck_friendships_responded_at", "(status = 'pending') = (responded_at IS NULL)");
                    table.CheckConstraint("ck_friendships_status", "status IN ('accepted', 'declined', 'pending')");
                    table.CheckConstraint("ck_friendships_suppressed_pending", "NOT suppressed OR status = 'pending'");
                    table.ForeignKey(
                        name: "fk_friendships_players_addressee_player_id",
                        column: x => x.addressee_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_friendships_players_requester_player_id",
                        column: x => x.requester_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "weekly_leaderboard_scores",
                schema: "vybe",
                columns: table => new
                {
                    game_id = table.Column<string>(type: "text", nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    best_score = table.Column<int>(type: "integer", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    achieved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_weekly_leaderboard_scores", x => new { x.game_id, x.week_start, x.player_id });
                    table.CheckConstraint("ck_weekly_leaderboard_scores_best_score", "best_score >= 0");
                    table.CheckConstraint("ck_weekly_leaderboard_scores_week_start_monday", "EXTRACT(ISODOW FROM week_start) = 1");
                    table.ForeignKey(
                        name: "fk_weekly_leaderboard_scores_game_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "vybe",
                        principalTable: "game_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_weekly_leaderboard_scores_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_friendships_addressee_player_id_status",
                schema: "vybe",
                table: "friendships",
                columns: new[] { "addressee_player_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_friendships_requester_player_id_status",
                schema: "vybe",
                table: "friendships",
                columns: new[] { "requester_player_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_weekly_leaderboard_scores_player_id",
                schema: "vybe",
                table: "weekly_leaderboard_scores",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_weekly_leaderboard_scores_rank",
                schema: "vybe",
                table: "weekly_leaderboard_scores",
                columns: new[] { "game_id", "week_start", "best_score", "achieved_at" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "ix_weekly_leaderboard_scores_session_id",
                schema: "vybe",
                table: "weekly_leaderboard_scores",
                column: "session_id");

            // Spec §4 friendships: "canonical pair unique". One row per pair in every state; EF Core cannot model an
            // expression index, so it lives here (and in db/schema.sql).
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_friendships_pair ON vybe.friendships
                    (LEAST(requester_player_id, addressee_player_id), GREATEST(requester_player_id, addressee_player_id));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS vybe.ux_friendships_pair;");

            migrationBuilder.DropTable(
                name: "friend_request_counters",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "friendships",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "weekly_leaderboard_scores",
                schema: "vybe");
        }
    }
}
