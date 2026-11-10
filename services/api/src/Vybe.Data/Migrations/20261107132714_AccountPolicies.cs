using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "age_band",
                schema: "vybe",
                table: "players",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "age_confirmed_at",
                schema: "vybe",
                table: "players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "age_policy_version",
                schema: "vybe",
                table: "players",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profile_visibility",
                schema: "vybe",
                table: "players",
                type: "text",
                nullable: false,
                defaultValueSql: "'players'");

            migrationBuilder.CreateTable(
                name: "auth_user_deletions",
                schema: "vybe",
                columns: table => new
                {
                    auth_subject = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_user_deletions", x => x.auth_subject);
                    table.CheckConstraint("ck_auth_user_deletions_attempts", "attempts >= 0");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_players_age_band",
                schema: "vybe",
                table: "players",
                sql: "age_band IN ('adult')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_players_age_confirmation",
                schema: "vybe",
                table: "players",
                sql: "(age_confirmed_at IS NULL) = (age_policy_version IS NULL) AND (age_confirmed_at IS NULL) = (age_band IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_players_profile_visibility",
                schema: "vybe",
                table: "players",
                sql: "profile_visibility IN ('players', 'private')");

            migrationBuilder.CreateIndex(
                name: "ix_auth_user_deletions_next_attempt_at",
                schema: "vybe",
                table: "auth_user_deletions",
                column: "next_attempt_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_user_deletions",
                schema: "vybe");

            migrationBuilder.DropCheckConstraint(
                name: "ck_players_age_band",
                schema: "vybe",
                table: "players");

            migrationBuilder.DropCheckConstraint(
                name: "ck_players_age_confirmation",
                schema: "vybe",
                table: "players");

            migrationBuilder.DropCheckConstraint(
                name: "ck_players_profile_visibility",
                schema: "vybe",
                table: "players");

            migrationBuilder.DropColumn(
                name: "age_band",
                schema: "vybe",
                table: "players");

            migrationBuilder.DropColumn(
                name: "age_confirmed_at",
                schema: "vybe",
                table: "players");

            migrationBuilder.DropColumn(
                name: "age_policy_version",
                schema: "vybe",
                table: "players");

            migrationBuilder.DropColumn(
                name: "profile_visibility",
                schema: "vybe",
                table: "players");
        }
    }
}
