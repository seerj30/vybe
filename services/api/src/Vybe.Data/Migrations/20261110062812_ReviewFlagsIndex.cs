using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewFlagsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_game_sessions_flagged_verified_at",
                schema: "vybe",
                table: "game_sessions",
                columns: new[] { "verified_at", "id" },
                descending: new bool[0],
                filter: "cardinality(review_flags) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_game_sessions_flagged_verified_at",
                schema: "vybe",
                table: "game_sessions");
        }
    }
}
