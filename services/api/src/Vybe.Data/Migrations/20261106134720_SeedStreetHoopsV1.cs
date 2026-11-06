using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <summary>
    /// Inserts the <c>street-hoops-v1</c> ruleset (spec v1.3 §7). The config and checksum are a copy of
    /// <c>packages/game-rules/fixtures/rulesets/street-hoops-v1.json</c>; the API tests check that the
    /// stored row, the fixture and <c>CanonicalJson.Checksum</c> agree.
    /// </summary>
    public partial class SeedStreetHoopsV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO vybe.game_rulesets (id, game_id, version, config, checksum, active_from)
                VALUES (
                    'street-hoops-v1',
                    'street-hoops',
                    1,
                    '{"streamId": 54, "durationMs": 60000, "graceMs": 5000, "earlyToleranceMs": 2000,
                      "segmentMs": 4000, "xMin": -700, "xMax": 700, "pMin": 300, "pMax": 900,
                      "hitDx": 160, "hitDp": 120, "perfectDx": 50, "perfectDp": 50,
                      "hitPoints": 100, "perfectPoints": 150, "streakStart": 3, "streakStep": 25,
                      "streakMax": 100, "minGapMs": 650, "maxEvents": 93, "minShotsForRewards": 5}',
                    '9b3bd19baa803520ab49a48f96f415f477fdfedbfc5a842dbacf00cfc8f2948d',
                    TIMESTAMPTZ '2026-11-06 00:00:00+00');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rulesets are immutable (guard trigger). Rolling back this migration is only valid
            // before any session references the row; the foreign keys still enforce that.
            migrationBuilder.Sql("""
                ALTER TABLE vybe.game_rulesets DISABLE TRIGGER game_rulesets_immutable;
                DELETE FROM vybe.game_rulesets WHERE id = 'street-hoops-v1';
                ALTER TABLE vybe.game_rulesets ENABLE TRIGGER game_rulesets_immutable;
                """);
        }
    }
}
