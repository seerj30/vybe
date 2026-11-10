using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vybe.Data.Entities;

namespace Vybe.Data.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class SchemaTests(PostgresFixture db)
{
    [Fact]
    public async Task Migrations_ApplyToCleanDatabase_AndMatchTheModel()
    {
        await using var context = db.CreateContext();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(context.Database.HasPendingModelChanges(), "the model changed without a migration");
    }

    [Fact]
    public async Task GuardsMigration_RollsBackAndReappliesCleanly()
    {
        await using var context = db.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var initial = context.Database.GetMigrations().First();

        await migrator.MigrateAsync(initial);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(
            db.DataSource, "SELECT count(*) FROM pg_trigger WHERE tgrelid::regclass::text LIKE 'vybe.%' AND NOT tgisinternal"));

        await migrator.MigrateAsync();
        Assert.Equal(11L, await Sql.ScalarAsync<long>(
            db.DataSource, "SELECT count(*) FROM pg_trigger WHERE tgrelid::regclass::text LIKE 'vybe.%' AND NOT tgisinternal"));
    }

    [Fact]
    public async Task EveryForeignKey_HasAnIndexStartingWithItsColumns()
    {
        // schema-foreign-key-indexes: an unindexed FK makes joins and parent deletes scan the child table.
        // An index whose first column is the FK's first column serves the lookup (for composite FKs
        // that column is already unique).
        var unindexed = await Sql.ScalarAsync<string>(
            db.DataSource,
            """
            SELECT coalesce(string_agg(c.conrelid::regclass::text || '.' || c.conname, ', '), '')
            FROM pg_constraint c
            WHERE c.contype = 'f'
              AND c.connamespace = 'vybe'::regnamespace
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_index i
                  WHERE i.indrelid = c.conrelid
                    AND (string_to_array(i.indkey::text, ' ')::int2[])[1] = c.conkey[1])
            """);

        Assert.Equal(string.Empty, unindexed);

        // Guard against a vacuous pass: the schema really has foreign keys to check.
        var foreignKeys = await Sql.ScalarAsync<long>(
            db.DataSource,
            "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND connamespace = 'vybe'::regnamespace");
        Assert.True(foreignKeys >= 25, $"expected at least 25 foreign keys, found {foreignKeys}");
    }

    [Fact]
    public async Task EnumColumns_StoreSnakeCaseText()
    {
        var id = Guid.CreateVersion7();
        await using (var context = db.CreateContext())
        {
            context.Players.Add(new Player
            {
                Id = id,
                Handle = "enum_" + id.ToString("N")[^10..],
                HandleNormalized = "enum_" + id.ToString("N")[^10..],
                Kind = PlayerKind.Registered,
                Status = PlayerStatus.Active,
                AuthSubject = "supabase|" + id,
            });
            await context.SaveChangesAsync();
        }

        Assert.Equal("registered", await Sql.ScalarAsync<string>(db.DataSource, "SELECT kind FROM vybe.players WHERE id = $1", id));

        await using var reader = db.CreateContext();
        var player = await reader.Players.SingleAsync(p => p.Id == id);
        Assert.Equal(PlayerKind.Registered, player.Kind);
        Assert.Equal(0, player.XpTotal);
        Assert.Equal(1, player.Level);
        Assert.Equal(ProfileVisibility.Players, player.ProfileVisibility); // database default
        Assert.Equal("players", await Sql.ScalarAsync<string>(db.DataSource, "SELECT profile_visibility FROM vybe.players WHERE id = $1", id));
    }

    [Theory]
    [InlineData("ck_players_handle_format",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES (gen_random_uuid(), 'Bad Handle', 'x_bad', 'guest', 'active')")]
    [InlineData("ck_players_kind",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, auth_subject) VALUES (gen_random_uuid(), 'kind_bad', 'kind_bad', 'robot', 'active', 'sub-robot')")]
    [InlineData("ck_players_level",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, level) VALUES (gen_random_uuid(), 'lvl_bad', 'lvl_bad', 'guest', 'active', 51)")]
    [InlineData("ck_players_profile_visibility",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, profile_visibility) VALUES (gen_random_uuid(), 'vis_bad', 'vis_bad', 'guest', 'active', 'public')")]
    [InlineData("ck_players_age_confirmation",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, age_confirmed_at) VALUES (gen_random_uuid(), 'age_part', 'age_part', 'guest', 'active', now())")]
    [InlineData("ck_players_age_band",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, age_confirmed_at, age_policy_version, age_band) VALUES (gen_random_uuid(), 'age_band', 'age_band', 'guest', 'active', now(), 'v', 'teen')")]
    [InlineData("ck_auth_user_deletions_attempts",
        "INSERT INTO vybe.auth_user_deletions (auth_subject, attempts) VALUES ('sub-negative', -1)")]
    [InlineData("ck_catalog_items_compatible_body_presets",
        "INSERT INTO vybe.catalog_items (id, sku, slot, name, price, asset_key, compatible_body_presets, rarity, is_active, catalog_version) VALUES (gen_random_uuid(), 'sku_none', 'top', 'x', 10, 'a', '{}', 'common', true, 1)")]
    [InlineData("ck_catalog_items_min_level",
        "INSERT INTO vybe.catalog_items (id, sku, slot, name, price, min_level, asset_key, compatible_body_presets, rarity, is_active, catalog_version) VALUES (gen_random_uuid(), 'sku_bad', 'top', 'x', 10, 0, 'a', '{body_a}', 'common', true, 1)")]
    public async Task CheckConstraints_RejectInvalidRows(string constraint, string sql)
    {
        var error = await Sql.FailsAsync(db.DataSource, sql);
        Assert.Equal(Sql.CheckViolation, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task Wallet_CannotGoNegative()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        await Sql.ExecuteAsync(db.DataSource, "INSERT INTO vybe.wallets (player_id) VALUES ($1)", player);

        var error = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.wallets SET balance = balance - 1 WHERE player_id = $1", player);

        Assert.Equal("ck_wallets_balance", error.ConstraintName);
    }

    [Fact]
    public async Task Ledger_RejectsZeroDelta_AndDuplicateLedgerKey()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var session = Guid.CreateVersion7();
        const string insert = """
            INSERT INTO vybe.wallet_transactions (id, player_id, delta, balance_after, reason, reference_id, ledger_key)
            VALUES (gen_random_uuid(), $1, $2, 100, 'game_reward', $3, 'game:' || $3::text)
            """;

        var zero = await Sql.FailsAsync(db.DataSource, insert, player, 0L, session);
        Assert.Equal("ck_wallet_transactions_delta", zero.ConstraintName);

        await Sql.ExecuteAsync(db.DataSource, insert, player, 100L, session);
        var duplicate = await Sql.FailsAsync(db.DataSource, insert, player, 100L, session);
        Assert.Equal(Sql.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Ledger_IsAppendOnly()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.wallet_transactions (id, player_id, delta, balance_after, reason, reference_type, ledger_key)
            VALUES (gen_random_uuid(), $1, 100, 100, 'level_reward', 'level', 'level:2')
            """,
            player);

        var update = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.wallet_transactions SET delta = 999 WHERE player_id = $1", player);
        var delete = await Sql.FailsAsync(db.DataSource, "DELETE FROM vybe.wallet_transactions WHERE player_id = $1", player);
        var truncate = await Sql.FailsAsync(db.DataSource, "TRUNCATE vybe.wallet_transactions CASCADE");

        Assert.All([update, delete, truncate], e => Assert.Equal(Sql.IntegrityViolation, e.SqlState));
    }

    [Fact]
    public async Task AuditLog_IsAppendOnly()
    {
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.admin_audit_logs (id, actor_subject, actor_role, action, entity_type, entity_id, reason)
            VALUES (gen_random_uuid(), 'admin-1', 'administrator', 'catalog.toggle', 'catalog_item', 'x', 'test reason')
            """);

        var error = await Sql.FailsAsync(db.DataSource, "DELETE FROM vybe.admin_audit_logs WHERE actor_subject = 'admin-1'");

        Assert.Equal(Sql.IntegrityViolation, error.SqlState);
    }

    [Fact]
    public async Task Inventory_PurchaseRequiresTransaction_GrantForbidsIt()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var item = Guid.CreateVersion7();
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.catalog_items (id, sku, slot, name, price, asset_key, compatible_body_presets, rarity, is_active, catalog_version)
            VALUES ($1, $2, 'top', 'Tee', 0, 'tee', '{body_a,body_b}', 'common', true, 1)
            """,
            item,
            "sku_" + item.ToString("N")[^10..]);

        var error = await Sql.FailsAsync(
            db.DataSource,
            "INSERT INTO vybe.player_inventory (player_id, item_id, source) VALUES ($1, $2, 'purchase')",
            player,
            item);
        Assert.Equal("ck_player_inventory_purchase_has_transaction", error.ConstraintName);

        await Sql.ExecuteAsync(
            db.DataSource,
            "INSERT INTO vybe.player_inventory (player_id, item_id, source) VALUES ($1, $2, 'starter_grant')",
            player,
            item);
    }

    [Fact]
    public async Task SavedOutfits_AreLimitedToThreeSlots()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        const string insert = """
            INSERT INTO vybe.saved_outfits (id, player_id, slot, name, equipped_json)
            VALUES (gen_random_uuid(), $1, $2, 'Look',
                    '{"hair":null,"top":null,"bottom":null,"shoes":null,"emote":null,"accessories":[null,null]}')
            """;
        for (short slot = 1; slot <= 3; slot++)
        {
            await Sql.ExecuteAsync(db.DataSource, insert, player, slot);
        }

        Assert.Equal("ck_saved_outfits_slot", (await Sql.FailsAsync(db.DataSource, insert, player, (short)4)).ConstraintName);
        Assert.Equal(Sql.UniqueViolation, (await Sql.FailsAsync(db.DataSource, insert, player, (short)2)).SqlState);
    }

    [Fact]
    public async Task GameSessions_OneOpenSessionPerPlayer_SeedIsUInt32()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        await Sql.SessionAsync(db.DataSource, player, ruleset);

        var second = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Sql.SessionAsync(db.DataSource, player, ruleset));
        Assert.Equal("ux_game_sessions_one_open_per_player", second.ConstraintName);

        await Sql.SessionAsync(db.DataSource, player, ruleset, "expired");
        await Sql.SessionAsync(db.DataSource, player, ruleset, "expired");

        var seed = await Sql.FailsAsync(
            db.DataSource,
            """
            INSERT INTO vybe.game_sessions (id, player_id, game_id, ruleset_id, seed, status, start_deadline)
            VALUES (gen_random_uuid(), $1, 'street-hoops', $2, 4294967296, 'expired', now())
            """,
            player,
            ruleset);
        Assert.Equal("ck_game_sessions_seed_uint32", seed.ConstraintName);
    }

    [Fact]
    public async Task FinishedSession_IsImmutable_ExceptReviewFlags()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var session = await Sql.SessionAsync(db.DataSource, player, ruleset, "expired");

        await Sql.ExecuteAsync(db.DataSource, "UPDATE vybe.game_sessions SET review_flags = '{perfect_rate}' WHERE id = $1", session);
        var error = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.game_sessions SET score = 999 WHERE id = $1", session);

        Assert.Equal(Sql.IntegrityViolation, error.SqlState);
    }

    [Fact]
    public async Task Ruleset_IsImmutable_ExceptRetirement()
    {
        var ruleset = await Sql.RulesetAsync(db.DataSource);

        await Sql.ExecuteAsync(db.DataSource, "UPDATE vybe.game_rulesets SET retired_at = now() WHERE id = $1", ruleset);
        var change = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.game_rulesets SET config = '{}' WHERE id = $1", ruleset);
        var delete = await Sql.FailsAsync(db.DataSource, "DELETE FROM vybe.game_rulesets WHERE id = $1", ruleset);

        Assert.All([change, delete], e => Assert.Equal(Sql.IntegrityViolation, e.SqlState));
    }

    [Fact]
    public async Task QuestProgressUpsert_FromSpecInvariant11_CompletesOnceAndCaps()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var quest = Guid.CreateVersion7();
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.quest_definitions (id, code, version, period, criteria, target, reward_coins, reward_xp, is_active)
            VALUES ($1, $2, 1, 'daily', '{}', 2, 100, 60, true)
            """,
            quest,
            "q_" + quest.ToString("N")[^10..]);

        // Spec v1.3 §4 invariant 11, verbatim apart from parameter syntax.
        const string upsert = """
            INSERT INTO vybe.player_quests (id, player_id, quest_id, period_start, target, progress, status, completed_at)
            VALUES (gen_random_uuid(), $1, $2, DATE '2026-11-06', 2, LEAST(2, $3),
                    CASE WHEN $3 >= 2 THEN 'completed' ELSE 'active' END,
                    CASE WHEN $3 >= 2 THEN now() END)
            ON CONFLICT (player_id, quest_id, period_start) DO UPDATE SET
              progress     = LEAST(player_quests.target, player_quests.progress + $3),
              status       = CASE WHEN player_quests.status = 'active'
                                   AND player_quests.progress + $3 >= player_quests.target
                                  THEN 'completed' ELSE player_quests.status END,
              completed_at = CASE WHEN player_quests.status = 'active'
                                   AND player_quests.progress + $3 >= player_quests.target
                                  THEN now() ELSE player_quests.completed_at END
            """;

        await Sql.ExecuteAsync(db.DataSource, upsert, player, quest, 1);
        Assert.Equal("active", await Sql.ScalarAsync<string>(db.DataSource, "SELECT status FROM vybe.player_quests WHERE player_id = $1", player));

        await Sql.ExecuteAsync(db.DataSource, upsert, player, quest, 1);
        var completedAt = await Sql.ScalarAsync<DateTime>(db.DataSource, "SELECT completed_at FROM vybe.player_quests WHERE player_id = $1", player);

        await Sql.ExecuteAsync(db.DataSource, upsert, player, quest, 1);
        Assert.Equal(2, await Sql.ScalarAsync<int>(db.DataSource, "SELECT progress FROM vybe.player_quests WHERE player_id = $1", player));
        Assert.Equal("completed", await Sql.ScalarAsync<string>(db.DataSource, "SELECT status FROM vybe.player_quests WHERE player_id = $1", player));
        Assert.Equal(completedAt, await Sql.ScalarAsync<DateTime>(db.DataSource, "SELECT completed_at FROM vybe.player_quests WHERE player_id = $1", player));
    }

    [Fact]
    public async Task DailyCounterUpsert_ReturnsDistinctRunNumbers()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        const string upsert = """
            INSERT INTO vybe.player_daily_counters (player_id, day, runs_rewarded) VALUES ($1, DATE '2026-11-06', 1)
            ON CONFLICT (player_id, day) DO UPDATE SET runs_rewarded = player_daily_counters.runs_rewarded + 1
            RETURNING runs_rewarded
            """;

        var runs = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Sql.ScalarAsync<int>(db.DataSource, upsert, player)));

        Assert.Equal(Enumerable.Range(1, 12), runs.Order());
    }

    [Fact]
    public async Task Schema_IsNotAccessibleToPublic()
    {
        var publicCanUse = await Sql.ScalarAsync<bool>(db.DataSource, "SELECT has_schema_privilege('public', 'vybe', 'USAGE')");

        Assert.False(publicCanUse);
    }
}
