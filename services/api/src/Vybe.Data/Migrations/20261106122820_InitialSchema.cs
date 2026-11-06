using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vybe.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "vybe");

            migrationBuilder.CreateTable(
                name: "admin_audit_logs",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    actor_role = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit_logs", x => x.id);
                    table.CheckConstraint("ck_admin_audit_logs_actor_role", "actor_role IN ('administrator', 'moderator')");
                    table.CheckConstraint("ck_admin_audit_logs_reason_length", "length(reason) BETWEEN 3 AND 500");
                });

            migrationBuilder.CreateTable(
                name: "catalog_items",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    slot = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    price = table.Column<long>(type: "bigint", nullable: false),
                    min_level = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    asset_key = table.Column<string>(type: "text", nullable: false),
                    compatible_body_presets = table.Column<string[]>(type: "text[]", nullable: false),
                    rarity = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    catalog_version = table.Column<int>(type: "integer", nullable: false),
                    available_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    available_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_items", x => x.id);
                    table.CheckConstraint("ck_catalog_items_availability_window", "available_from IS NULL OR available_until IS NULL OR available_until > available_from");
                    table.CheckConstraint("ck_catalog_items_catalog_version", "catalog_version >= 1");
                    table.CheckConstraint("ck_catalog_items_compatible_body_presets", "cardinality(compatible_body_presets) >= 1");
                    table.CheckConstraint("ck_catalog_items_min_level", "min_level BETWEEN 1 AND 50");
                    table.CheckConstraint("ck_catalog_items_price", "price >= 0");
                    table.CheckConstraint("ck_catalog_items_rarity", "rarity IN ('common', 'epic', 'rare')");
                    table.CheckConstraint("ck_catalog_items_slot", "slot IN ('accessory', 'bottom', 'emote', 'hair', 'shoes', 'top')");
                });

            migrationBuilder.CreateTable(
                name: "deletion_requests",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deletion_requests", x => x.id);
                    table.CheckConstraint("ck_deletion_requests_status", "status IN ('completed', 'processing', 'queued')");
                });

            migrationBuilder.CreateTable(
                name: "game_rulesets",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    game_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    checksum = table.Column<string>(type: "text", nullable: false),
                    active_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_rulesets", x => x.id);
                    table.CheckConstraint("ck_game_rulesets_checksum_format", "checksum ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_game_rulesets_config_object", "jsonb_typeof(config) = 'object'");
                    table.CheckConstraint("ck_game_rulesets_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "vybe",
                columns: table => new
                {
                    actor_subject = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.actor_subject, x.operation, x.key });
                    table.CheckConstraint("ck_idempotency_records_completed_has_response", "status <> 'completed' OR response_status IS NOT NULL");
                    table.CheckConstraint("ck_idempotency_records_lease_only_in_progress", "(status = 'in_progress') = (locked_until IS NOT NULL)");
                    table.CheckConstraint("ck_idempotency_records_response_status", "response_status IS NULL OR response_status BETWEEN 100 AND 599");
                    table.CheckConstraint("ck_idempotency_records_status", "status IN ('completed', 'in_progress')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_jobs",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    job_key = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    run_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_jobs", x => x.id);
                    table.CheckConstraint("ck_outbox_jobs_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_jobs_payload_object", "jsonb_typeof(payload) = 'object'");
                    table.CheckConstraint("ck_outbox_jobs_status", "status IN ('done', 'failed', 'pending')");
                });

            migrationBuilder.CreateTable(
                name: "players",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    auth_subject = table.Column<string>(type: "text", nullable: true),
                    handle = table.Column<string>(type: "text", nullable: false),
                    handle_normalized = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    xp_total = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    suspended_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    suspension_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_players", x => x.id);
                    table.CheckConstraint("ck_players_auth_subject_by_kind", "status = 'deleted' OR (kind = 'guest') = (auth_subject IS NULL)");
                    table.CheckConstraint("ck_players_deleted_at", "(status = 'deleted') = (deleted_at IS NOT NULL)");
                    table.CheckConstraint("ck_players_deleted_pseudonym", "(status = 'deleted') = (handle ~ '^d_[a-z0-9]{18}$')");
                    table.CheckConstraint("ck_players_handle_format", "handle ~ '^[a-z0-9_]{3,20}$'");
                    table.CheckConstraint("ck_players_handle_normalized", "handle_normalized = handle");
                    table.CheckConstraint("ck_players_kind", "kind IN ('guest', 'registered')");
                    table.CheckConstraint("ck_players_level", "level BETWEEN 1 AND 50");
                    table.CheckConstraint("ck_players_status", "status IN ('active', 'deleted', 'deleting', 'suspended')");
                    table.CheckConstraint("ck_players_xp_total", "xp_total >= 0");
                });

            migrationBuilder.CreateTable(
                name: "quest_definitions",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    period = table.Column<string>(type: "text", nullable: false),
                    criteria = table.Column<string>(type: "jsonb", nullable: false),
                    target = table.Column<int>(type: "integer", nullable: false),
                    reward_coins = table.Column<long>(type: "bigint", nullable: false),
                    reward_xp = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest_definitions", x => x.id);
                    table.CheckConstraint("ck_quest_definitions_criteria_object", "jsonb_typeof(criteria) = 'object'");
                    table.CheckConstraint("ck_quest_definitions_period", "period IN ('daily', 'once')");
                    table.CheckConstraint("ck_quest_definitions_rewards", "reward_coins >= 0 AND reward_xp >= 0");
                    table.CheckConstraint("ck_quest_definitions_target", "target > 0");
                });

            migrationBuilder.CreateTable(
                name: "achievements",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    criteria = table.Column<string>(type: "jsonb", nullable: false),
                    reward_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_achievements", x => x.id);
                    table.CheckConstraint("ck_achievements_criteria_object", "jsonb_typeof(criteria) = 'object'");
                    table.ForeignKey(
                        name: "fk_achievements_catalog_items_reward_item_id",
                        column: x => x.reward_item_id,
                        principalSchema: "vybe",
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "avatar_configs",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body_preset_id = table.Column<string>(type: "text", nullable: false),
                    skin_tone_id = table.Column<string>(type: "text", nullable: false),
                    face_preset_id = table.Column<string>(type: "text", nullable: false),
                    hair_preset_id = table.Column<string>(type: "text", nullable: false),
                    hair_color_id = table.Column<string>(type: "text", nullable: false),
                    equipped_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<long>(type: "bigint", rowVersion: true, nullable: false, defaultValue: 0L),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_configs", x => x.player_id);
                    table.CheckConstraint("ck_avatar_configs_equipped_json_shape", "jsonb_typeof(equipped_json) = 'object' AND equipped_json ?& ARRAY['hair', 'top', 'bottom', 'shoes', 'emote', 'accessories'] AND CASE WHEN jsonb_typeof(equipped_json -> 'accessories') = 'array' THEN jsonb_array_length(equipped_json -> 'accessories') = 2 ELSE false END");
                    table.CheckConstraint("ck_avatar_configs_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_avatar_configs_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "data_export_requests",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: true),
                    download_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_export_requests", x => x.id);
                    table.CheckConstraint("ck_data_export_requests_failed_fields", "(status = 'failed') = (failure_code IS NOT NULL)");
                    table.CheckConstraint("ck_data_export_requests_ready_fields", "status <> 'ready' OR (storage_key IS NOT NULL AND download_expires_at IS NOT NULL)");
                    table.CheckConstraint("ck_data_export_requests_status", "status IN ('failed', 'queued', 'ready', 'running')");
                    table.ForeignKey(
                        name: "fk_data_export_requests_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guest_sessions",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guest_sessions", x => x.id);
                    table.CheckConstraint("ck_guest_sessions_token_hash_sha256", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_guest_sessions_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_blocks",
                schema: "vybe",
                columns: table => new
                {
                    blocker_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_blocks", x => new { x.blocker_player_id, x.blocked_player_id });
                    table.CheckConstraint("ck_player_blocks_not_self", "blocker_player_id <> blocked_player_id");
                    table.ForeignKey(
                        name: "fk_player_blocks_players_blocked_player_id",
                        column: x => x.blocked_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_blocks_players_blocker_player_id",
                        column: x => x.blocker_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_daily_counters",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    runs_rewarded = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    xp_from_games = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    challenge_bonuses = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_daily_counters", x => new { x.player_id, x.day });
                    table.CheckConstraint("ck_player_daily_counters_non_negative", "runs_rewarded >= 0 AND xp_from_games >= 0 AND challenge_bonuses >= 0");
                    table.ForeignKey(
                        name: "fk_player_daily_counters_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_reports",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_reports", x => x.id);
                    table.CheckConstraint("ck_player_reports_not_self", "reporter_player_id <> target_player_id");
                    table.CheckConstraint("ck_player_reports_reason", "reason IN ('cheating', 'harassment', 'offensive_name', 'other')");
                    table.CheckConstraint("ck_player_reports_status", "status IN ('actioned', 'dismissed', 'open')");
                    table.ForeignKey(
                        name: "fk_player_reports_players_reporter_player_id",
                        column: x => x.reporter_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_reports_players_target_player_id",
                        column: x => x.target_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "saved_outfits",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    equipped_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_outfits", x => x.id);
                    table.CheckConstraint("ck_saved_outfits_equipped_json_shape", "jsonb_typeof(equipped_json) = 'object' AND equipped_json ?& ARRAY['hair', 'top', 'bottom', 'shoes', 'emote', 'accessories'] AND CASE WHEN jsonb_typeof(equipped_json -> 'accessories') = 'array' THEN jsonb_array_length(equipped_json -> 'accessories') = 2 ELSE false END");
                    table.CheckConstraint("ck_saved_outfits_name_not_empty", "length(name) >= 1");
                    table.CheckConstraint("ck_saved_outfits_slot", "slot BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "fk_saved_outfits_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallet_transactions",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta = table.Column<long>(type: "bigint", nullable: false),
                    balance_after = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    reference_type = table.Column<string>(type: "text", nullable: true),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ledger_key = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_transactions", x => x.id);
                    table.CheckConstraint("ck_wallet_transactions_balance_after", "balance_after >= 0");
                    table.CheckConstraint("ck_wallet_transactions_delta", "delta <> 0");
                    table.CheckConstraint("ck_wallet_transactions_ledger_key_not_empty", "length(ledger_key) >= 1");
                    table.CheckConstraint("ck_wallet_transactions_ledger_key_shape", "CASE reason WHEN 'game_reward' THEN ledger_key = 'game:' || reference_id::text WHEN 'quest_reward' THEN ledger_key = 'quest:' || reference_id::text WHEN 'challenge_reward' THEN ledger_key = 'challenge:' || reference_id::text WHEN 'purchase' THEN ledger_key = 'purchase:' || reference_id::text WHEN 'level_reward' THEN ledger_key ~ '^level:([2-9]|[1-4][0-9]|50)$' WHEN 'admin_adjustment' THEN ledger_key ~ '^admin:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' ELSE false END");
                    table.CheckConstraint("ck_wallet_transactions_reason", "reason IN ('admin_adjustment', 'challenge_reward', 'game_reward', 'level_reward', 'purchase', 'quest_reward')");
                    table.CheckConstraint("ck_wallet_transactions_reference_required", "(reason = 'level_reward') = (reference_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_wallet_transactions_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    revision = table.Column<long>(type: "bigint", rowVersion: true, nullable: false, defaultValue: 0L),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallets", x => x.player_id);
                    table.CheckConstraint("ck_wallets_balance", "balance >= 0");
                    table.CheckConstraint("ck_wallets_revision", "revision >= 0");
                    table.ForeignKey(
                        name: "fk_wallets_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_quests",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    target = table.Column<int>(type: "integer", nullable: false),
                    progress = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_quests", x => x.id);
                    table.CheckConstraint("ck_player_quests_claimed_fields", "(status = 'claimed') = (claimed_at IS NOT NULL)");
                    table.CheckConstraint("ck_player_quests_completed_fields", "status = 'active' OR (progress = target AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_player_quests_progress", "progress BETWEEN 0 AND target");
                    table.CheckConstraint("ck_player_quests_status", "status IN ('active', 'claimed', 'completed')");
                    table.CheckConstraint("ck_player_quests_target", "target > 0");
                    table.ForeignKey(
                        name: "fk_player_quests_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_quests_quest_definitions_quest_id",
                        column: x => x.quest_id,
                        principalSchema: "vybe",
                        principalTable: "quest_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_achievements",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    achievement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unlocked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_achievements", x => new { x.player_id, x.achievement_id });
                    table.ForeignKey(
                        name: "fk_player_achievements_achievements_achievement_id",
                        column: x => x.achievement_id,
                        principalSchema: "vybe",
                        principalTable: "achievements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_achievements_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "player_inventory",
                schema: "vybe",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_reference_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_inventory", x => new { x.player_id, x.item_id });
                    table.CheckConstraint("ck_player_inventory_purchase_has_transaction", "(source = 'purchase') = (source_transaction_id IS NOT NULL)");
                    table.CheckConstraint("ck_player_inventory_source", "source IN ('achievement_grant', 'admin_grant', 'purchase', 'starter_grant')");
                    table.ForeignKey(
                        name: "fk_player_inventory_catalog_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "vybe",
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_inventory_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_player_inventory_wallet_transactions_source_transaction_id",
                        column: x => x.source_transaction_id,
                        principalSchema: "vybe",
                        principalTable: "wallet_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "challenges",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    creator_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_player_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invite_token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ruleset_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    accept_deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    complete_deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    recipient_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    winner_player_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rematch_of_challenge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suppressed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", rowVersion: true, nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_challenges", x => x.id);
                    table.CheckConstraint("ck_challenges_accepted_fields", "status NOT IN ('accepted', 'completed') OR (recipient_player_id IS NOT NULL AND accepted_at IS NOT NULL AND complete_deadline IS NOT NULL)");
                    table.CheckConstraint("ck_challenges_completed_fields", "status <> 'completed' OR (recipient_session_id IS NOT NULL AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_challenges_distinct_sessions", "recipient_session_id IS NULL OR recipient_session_id <> creator_session_id");
                    table.CheckConstraint("ck_challenges_not_self", "recipient_player_id IS NULL OR recipient_player_id <> creator_player_id");
                    table.CheckConstraint("ck_challenges_status", "status IN ('accepted', 'cancelled', 'completed', 'expired', 'open')");
                    table.CheckConstraint("ck_challenges_token_hash_sha256", "octet_length(invite_token_hash) = 32");
                    table.CheckConstraint("ck_challenges_winner_is_participant", "winner_player_id IS NULL OR winner_player_id = creator_player_id OR winner_player_id = recipient_player_id");
                    table.CheckConstraint("ck_challenges_winner_only_when_completed", "winner_player_id IS NULL OR status = 'completed'");
                    table.ForeignKey(
                        name: "fk_challenges_challenges_rematch_of_challenge_id",
                        column: x => x.rematch_of_challenge_id,
                        principalSchema: "vybe",
                        principalTable: "challenges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_challenges_game_rulesets_ruleset_id",
                        column: x => x.ruleset_id,
                        principalSchema: "vybe",
                        principalTable: "game_rulesets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_challenges_players_creator_player_id",
                        column: x => x.creator_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_challenges_players_recipient_player_id",
                        column: x => x.recipient_player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "game_sessions",
                schema: "vybe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    game_id = table.Column<string>(type: "text", nullable: false),
                    ruleset_id = table.Column<string>(type: "text", nullable: false),
                    seed = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    start_deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    submit_deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reward_day = table.Column<DateOnly>(type: "date", nullable: true),
                    event_count = table.Column<int>(type: "integer", nullable: true),
                    score = table.Column<int>(type: "integer", nullable: true),
                    reward_coins = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    reward_xp = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    input_trace_hash = table.Column<string>(type: "text", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    review_flags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    challenge_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", rowVersion: true, nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_sessions", x => x.id);
                    table.UniqueConstraint("ak_game_sessions_id_player_id_ruleset_id", x => new { x.id, x.player_id, x.ruleset_id });
                    table.CheckConstraint("ck_game_sessions_non_negative", "COALESCE(score, 0) >= 0 AND COALESCE(event_count, 0) >= 0 AND reward_coins >= 0 AND reward_xp >= 0");
                    table.CheckConstraint("ck_game_sessions_rejected_has_reason", "(status = 'rejected') = (rejection_reason IS NOT NULL)");
                    table.CheckConstraint("ck_game_sessions_rejection_reason", "rejection_reason IN ('cadence_violation', 'invalid_event', 'non_monotonic', 'not_started', 'out_of_range', 'ruleset_mismatch', 'submitted_too_early', 'too_many_events')");
                    table.CheckConstraint("ck_game_sessions_reward_day_utc", "reward_day IS NULL OR reward_day = (started_at AT TIME ZONE 'UTC')::date");
                    table.CheckConstraint("ck_game_sessions_seed_uint32", "seed BETWEEN 0 AND 4294967295");
                    table.CheckConstraint("ck_game_sessions_started_fields", "status NOT IN ('active', 'submitted', 'verified') OR (started_at IS NOT NULL AND submit_deadline IS NOT NULL)");
                    table.CheckConstraint("ck_game_sessions_status", "status IN ('active', 'created', 'expired', 'rejected', 'submitted', 'verified')");
                    table.CheckConstraint("ck_game_sessions_verified_fields", "status <> 'verified' OR (score IS NOT NULL AND verified_at IS NOT NULL AND reward_day IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_game_sessions_challenges_challenge_id",
                        column: x => x.challenge_id,
                        principalSchema: "vybe",
                        principalTable: "challenges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_game_sessions_game_rulesets_ruleset_id",
                        column: x => x.ruleset_id,
                        principalSchema: "vybe",
                        principalTable: "game_rulesets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_game_sessions_players_player_id",
                        column: x => x.player_id,
                        principalSchema: "vybe",
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "game_input_traces",
                schema: "vybe",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    compressed_trace = table.Column<byte[]>(type: "bytea", nullable: false),
                    encoding = table.Column<string>(type: "text", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_input_traces", x => x.session_id);
                    table.CheckConstraint("ck_game_input_traces_event_count", "event_count >= 0");
                    table.CheckConstraint("ck_game_input_traces_size_cap", "octet_length(compressed_trace) <= 16384");
                    table.ForeignKey(
                        name: "fk_game_input_traces_game_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "vybe",
                        principalTable: "game_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_achievements_code",
                schema: "vybe",
                table: "achievements",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_achievements_reward_item_id",
                schema: "vybe",
                table: "achievements",
                column: "reward_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_logs_actor_subject_created_at",
                schema: "vybe",
                table: "admin_audit_logs",
                columns: new[] { "actor_subject", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_logs_created_at",
                schema: "vybe",
                table: "admin_audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_logs_entity_type_entity_id_created_at",
                schema: "vybe",
                table: "admin_audit_logs",
                columns: new[] { "entity_type", "entity_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_sku",
                schema: "vybe",
                table: "catalog_items",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_challenges_accepted_complete_deadline",
                schema: "vybe",
                table: "challenges",
                column: "complete_deadline",
                filter: "status = 'accepted'");

            migrationBuilder.CreateIndex(
                name: "ix_challenges_creator_player_id_created_at",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "creator_player_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_challenges_creator_session_id",
                schema: "vybe",
                table: "challenges",
                column: "creator_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_challenges_creator_session_id_creator_player_id_ruleset_id",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "creator_session_id", "creator_player_id", "ruleset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_challenges_invite_token_hash",
                schema: "vybe",
                table: "challenges",
                column: "invite_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_challenges_recipient_player_id_status",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "recipient_player_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_challenges_recipient_session_id",
                schema: "vybe",
                table: "challenges",
                column: "recipient_session_id",
                unique: true,
                filter: "recipient_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_challenges_recipient_session_id_recipient_player_id_ruleset",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "recipient_session_id", "recipient_player_id", "ruleset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_challenges_rematch_of_challenge_id_creator_player_id",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "rematch_of_challenge_id", "creator_player_id" },
                unique: true,
                filter: "rematch_of_challenge_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_challenges_ruleset_id",
                schema: "vybe",
                table: "challenges",
                column: "ruleset_id");

            migrationBuilder.CreateIndex(
                name: "ix_challenges_status_accept_deadline",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "status", "accept_deadline" });

            migrationBuilder.CreateIndex(
                name: "ix_challenges_winner_player_id",
                schema: "vybe",
                table: "challenges",
                column: "winner_player_id");

            migrationBuilder.CreateIndex(
                name: "ix_data_export_requests_player_id_created_at",
                schema: "vybe",
                table: "data_export_requests",
                columns: new[] { "player_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_data_export_requests_one_pending_per_player",
                schema: "vybe",
                table: "data_export_requests",
                column: "player_id",
                unique: true,
                filter: "status IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_deletion_requests_player_id",
                schema: "vybe",
                table: "deletion_requests",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_game_input_traces_received_at",
                schema: "vybe",
                table: "game_input_traces",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_game_rulesets_game_id_version",
                schema: "vybe",
                table: "game_rulesets",
                columns: new[] { "game_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_game_sessions_active_submit_deadline",
                schema: "vybe",
                table: "game_sessions",
                column: "submit_deadline",
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_game_sessions_challenge_id",
                schema: "vybe",
                table: "game_sessions",
                column: "challenge_id",
                unique: true,
                filter: "challenge_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_game_sessions_created_start_deadline",
                schema: "vybe",
                table: "game_sessions",
                column: "start_deadline",
                filter: "status = 'created'");

            migrationBuilder.CreateIndex(
                name: "ix_game_sessions_player_id_created_at",
                schema: "vybe",
                table: "game_sessions",
                columns: new[] { "player_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_game_sessions_ruleset_id",
                schema: "vybe",
                table: "game_sessions",
                column: "ruleset_id");

            migrationBuilder.CreateIndex(
                name: "ux_game_sessions_one_open_per_player",
                schema: "vybe",
                table: "game_sessions",
                column: "player_id",
                unique: true,
                filter: "status IN ('created', 'active')");

            migrationBuilder.CreateIndex(
                name: "ix_guest_sessions_player_id",
                schema: "vybe",
                table: "guest_sessions",
                column: "player_id");

            migrationBuilder.CreateIndex(
                name: "ix_guest_sessions_token_hash",
                schema: "vybe",
                table: "guest_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "vybe",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_jobs_job_key",
                schema: "vybe",
                table: "outbox_jobs",
                column: "job_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_jobs_status_run_after",
                schema: "vybe",
                table: "outbox_jobs",
                columns: new[] { "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "ix_player_achievements_achievement_id",
                schema: "vybe",
                table: "player_achievements",
                column: "achievement_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_blocks_blocked_player_id",
                schema: "vybe",
                table: "player_blocks",
                column: "blocked_player_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_inventory_item_id",
                schema: "vybe",
                table: "player_inventory",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_inventory_source_transaction_id",
                schema: "vybe",
                table: "player_inventory",
                column: "source_transaction_id",
                unique: true,
                filter: "source_transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_player_quests_player_id_quest_id_period_start",
                schema: "vybe",
                table: "player_quests",
                columns: new[] { "player_id", "quest_id", "period_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_player_quests_quest_id",
                schema: "vybe",
                table: "player_quests",
                column: "quest_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_reports_reporter_player_id",
                schema: "vybe",
                table: "player_reports",
                column: "reporter_player_id");

            migrationBuilder.CreateIndex(
                name: "ix_player_reports_status_created_at",
                schema: "vybe",
                table: "player_reports",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_player_reports_target_player_id",
                schema: "vybe",
                table: "player_reports",
                column: "target_player_id");

            migrationBuilder.CreateIndex(
                name: "ix_players_auth_subject",
                schema: "vybe",
                table: "players",
                column: "auth_subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_players_handle_normalized",
                schema: "vybe",
                table: "players",
                column: "handle_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_players_suspended_until",
                schema: "vybe",
                table: "players",
                column: "suspended_until",
                filter: "status = 'suspended'");

            migrationBuilder.CreateIndex(
                name: "ix_quest_definitions_code_version",
                schema: "vybe",
                table: "quest_definitions",
                columns: new[] { "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_outfits_player_id_slot",
                schema: "vybe",
                table: "saved_outfits",
                columns: new[] { "player_id", "slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_player_id_created_at",
                schema: "vybe",
                table: "wallet_transactions",
                columns: new[] { "player_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_player_id_ledger_key",
                schema: "vybe",
                table: "wallet_transactions",
                columns: new[] { "player_id", "ledger_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_transactions_reason_reference_id",
                schema: "vybe",
                table: "wallet_transactions",
                columns: new[] { "reason", "reference_id" });

            migrationBuilder.CreateIndex(
                name: "ux_wallet_transactions_one_challenge_reward",
                schema: "vybe",
                table: "wallet_transactions",
                column: "reference_id",
                unique: true,
                filter: "reason = 'challenge_reward'");

            migrationBuilder.AddForeignKey(
                name: "fk_challenges_creator_session",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "creator_session_id", "creator_player_id", "ruleset_id" },
                principalSchema: "vybe",
                principalTable: "game_sessions",
                principalColumns: new[] { "id", "player_id", "ruleset_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_challenges_recipient_session",
                schema: "vybe",
                table: "challenges",
                columns: new[] { "recipient_session_id", "recipient_player_id", "ruleset_id" },
                principalSchema: "vybe",
                principalTable: "game_sessions",
                principalColumns: new[] { "id", "player_id", "ruleset_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_challenges_players_creator_player_id",
                schema: "vybe",
                table: "challenges");

            migrationBuilder.DropForeignKey(
                name: "fk_challenges_players_recipient_player_id",
                schema: "vybe",
                table: "challenges");

            migrationBuilder.DropForeignKey(
                name: "fk_game_sessions_players_player_id",
                schema: "vybe",
                table: "game_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_challenges_creator_session",
                schema: "vybe",
                table: "challenges");

            migrationBuilder.DropForeignKey(
                name: "fk_challenges_recipient_session",
                schema: "vybe",
                table: "challenges");

            migrationBuilder.DropTable(
                name: "admin_audit_logs",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "avatar_configs",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "data_export_requests",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "deletion_requests",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "game_input_traces",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "guest_sessions",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "outbox_jobs",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_achievements",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_blocks",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_daily_counters",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_inventory",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_quests",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "player_reports",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "saved_outfits",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "wallets",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "achievements",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "wallet_transactions",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "quest_definitions",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "catalog_items",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "players",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "game_sessions",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "challenges",
                schema: "vybe");

            migrationBuilder.DropTable(
                name: "game_rulesets",
                schema: "vybe");
        }
    }
}
