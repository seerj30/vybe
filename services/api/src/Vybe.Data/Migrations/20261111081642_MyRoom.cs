using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <inheritdoc />
    public partial class MyRoom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_catalog_items_slot",
                schema: "vybe",
                table: "catalog_items");

            migrationBuilder.CreateTable(
                name: "player_rooms",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wall_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    floor_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    layout_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    revision = table.Column<long>(type: "bigint", rowVersion: true, nullable: false, defaultValue: 0L),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_rooms", x => x.player_id);
                    table.CheckConstraint("ck_player_rooms_layout_json_shape", "jsonb_typeof(layout_json) = 'array' AND jsonb_array_length(layout_json) <= 40");
                    table.CheckConstraint("ck_player_rooms_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_player_rooms_catalog_items_floor_item_id",
                        column: x => x.floor_item_id,
                        principalSchema: "vybe",
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_rooms_catalog_items_wall_item_id",
                        column: x => x.wall_item_id,
                        principalSchema: "vybe",
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_rooms_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_catalog_items_slot",
                schema: "vybe",
                table: "catalog_items",
                sql: "slot IN ('accessory', 'bottom', 'emote', 'furniture', 'hair', 'room_floor', 'room_wall', 'shoes', 'top', 'wall_decor')");

            migrationBuilder.CreateIndex(
                name: "ix_player_rooms_floor_item_id",
                schema: "vybe",
                table: "player_rooms",
                column: "floor_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_rooms_wall_item_id",
                schema: "vybe",
                table: "player_rooms",
                column: "wall_item_id");

            // Spec §4 invariant 5: every UPDATE increments the revision (the compare-and-swap of PUT /me/room).
            // The slot CHECK above only accepts more values (expand change); no existing row changes.
            migrationBuilder.Sql("""
                CREATE TRIGGER player_rooms_a_increment_revision
                    BEFORE UPDATE ON vybe.player_rooms
                    FOR EACH ROW EXECUTE FUNCTION vybe.increment_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER player_rooms_a_increment_revision ON vybe.player_rooms;");

            migrationBuilder.DropTable(
                name: "player_rooms",
                schema: "vybe");

            migrationBuilder.DropCheckConstraint(
                name: "ck_catalog_items_slot",
                schema: "vybe",
                table: "catalog_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_catalog_items_slot",
                schema: "vybe",
                table: "catalog_items",
                sql: "slot IN ('accessory', 'bottom', 'emote', 'hair', 'shoes', 'top')");
        }
    }
}
