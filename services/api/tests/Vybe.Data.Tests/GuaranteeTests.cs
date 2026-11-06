using Npgsql;

namespace Vybe.Data.Tests;

/// <summary>Guarantees added after the second schema review (ADR 0002).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class GuaranteeTests(PostgresFixture db)
{
    private const string ForeignKeyViolation = "23503";

    [Fact]
    public async Task CrossedChallenges_VerifiedConcurrently_DoNotDeadlock()
    {
        // Spec invariant 10: A→B and B→A resolve at the same time, each transaction holding its own
        // player row lock. A winner FK would make each wait for the other's player row.
        var a = await Sql.PlayerAsync(db.DataSource);
        var b = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var aToB = await AcceptedChallengeWithRecipientRunAsync(a, b, ruleset);
        var bToA = await AcceptedChallengeWithRecipientRunAsync(b, a, ruleset);

        await using var first = await db.DataSource.OpenConnectionAsync();
        await using var second = await db.DataSource.OpenConnectionAsync();
        await using var t1 = await first.BeginTransactionAsync();
        await using var t2 = await second.BeginTransactionAsync();
        await LockedAsync(first, t1, b);
        await LockedAsync(second, t2, a);

        await Task.WhenAll(
            CompleteAsync(first, t1, aToB, winner: a),
            CompleteAsync(second, t2, bToA, winner: b));
        await t1.CommitAsync();
        await t2.CommitAsync();

        Assert.Equal("completed", await Sql.ScalarAsync<string>(db.DataSource, "SELECT status FROM vybe.challenges WHERE id = $1", aToB));
        Assert.Equal("completed", await Sql.ScalarAsync<string>(db.DataSource, "SELECT status FROM vybe.challenges WHERE id = $1", bToA));
    }

    [Fact]
    public async Task Idempotency_LeaseExistsOnlyWhileInProgress()
    {
        const string insert = """
            INSERT INTO vybe.idempotency_records (actor_subject, operation, key, request_hash, status, locked_until, response_status, expires_at)
            VALUES ($1, 'purchase', gen_random_uuid(), 'h', $2, $3, $4, now() + interval '72 hours')
            """;
        var actor = Guid.CreateVersion7().ToString();

        var completedWithLease = await Sql.FailsAsync(db.DataSource, insert, actor, "completed", DateTime.UtcNow, 200);
        var inProgressWithoutLease = await Sql.FailsAsync(db.DataSource, insert, actor, "in_progress", DBNull.Value, DBNull.Value);

        Assert.All([completedWithLease, inProgressWithoutLease],
            e => Assert.Equal("ck_idempotency_records_lease_only_in_progress", e.ConstraintName));
    }

    [Fact]
    public async Task Challenge_CreatorRunMustBeTheCreatorsOwnNonRecipientRun()
    {
        var creator = await Sql.PlayerAsync(db.DataSource);
        var other = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var othersRun = await VerifiedSessionAsync(other, ruleset);
        var challenge = await AcceptedChallengeWithRecipientRunAsync(creator, other, ruleset);
        var recipientRun = await Sql.ScalarAsync<Guid>(
            db.DataSource, "SELECT recipient_session_id FROM vybe.challenges WHERE id = $1", challenge);
        const string insert = """
            INSERT INTO vybe.challenges (id, creator_player_id, creator_session_id, invite_token_hash, ruleset_id, status, accept_deadline)
            VALUES (gen_random_uuid(), $1, $2, sha256(gen_random_uuid()::text::bytea), $3, 'open', now() + interval '1 day')
            """;

        var notOwnRun = await Sql.FailsAsync(db.DataSource, insert, creator, othersRun, ruleset);
        var recipientRunAsCreator = await Sql.FailsAsync(db.DataSource, insert, other, recipientRun, ruleset);

        Assert.Equal(ForeignKeyViolation, notOwnRun.SqlState);
        Assert.Equal(Sql.IntegrityViolation, recipientRunAsCreator.SqlState);
    }

    [Fact]
    public async Task Challenge_RecipientRunMustCarryThisChallengesId()
    {
        var creator = await Sql.PlayerAsync(db.DataSource);
        var recipient = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var first = await AcceptedChallengeWithRecipientRunAsync(creator, recipient, ruleset, attachRun: false);
        var second = await AcceptedChallengeWithRecipientRunAsync(creator, recipient, ruleset, attachRun: false);
        var runForFirst = await VerifiedSessionAsync(recipient, ruleset, first);

        var error = await Sql.FailsAsync(
            db.DataSource, "UPDATE vybe.challenges SET recipient_session_id = $1 WHERE id = $2", runForFirst, second);

        Assert.Equal("fk_challenges_recipient_session_challenge", error.ConstraintName);
    }

    [Fact]
    public async Task Inventory_PurchaseMustUseTheBuyersLedgerRowForThatItem()
    {
        var buyer = await Sql.PlayerAsync(db.DataSource);
        var other = await Sql.PlayerAsync(db.DataSource);
        var item = await ItemAsync();
        var othersPurchase = Guid.CreateVersion7();
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.wallet_transactions (id, player_id, delta, balance_after, reason, reference_id, ledger_key)
            VALUES ($1, $2, -200, 0, 'purchase', $3, 'purchase:' || $3::text)
            """,
            othersPurchase,
            other,
            item);

        var error = await Sql.FailsAsync(
            db.DataSource,
            "INSERT INTO vybe.player_inventory (player_id, item_id, source, source_transaction_id) VALUES ($1, $2, 'purchase', $3)",
            buyer,
            item,
            othersPurchase);

        Assert.Equal("fk_player_inventory_purchase_transaction", error.ConstraintName);
    }

    [Fact]
    public async Task Player_AnonymizationWithPseudonym_Succeeds_AndIsRequiredForDeleted()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var pseudonym = "d_" + Guid.CreateVersion7().ToString("N")[^18..];

        var keepsHandle = await Sql.FailsAsync(
            db.DataSource, "UPDATE vybe.players SET status = 'deleted', deleted_at = now() WHERE id = $1", player);
        Assert.Equal("ck_players_deleted_pseudonym", keepsHandle.ConstraintName);

        await Sql.ExecuteAsync(
            db.DataSource,
            """
            UPDATE vybe.players
            SET status = 'deleted', deleted_at = now(), handle = $2, handle_normalized = $2,
                display_name = NULL, auth_subject = NULL, suspension_reason = NULL
            WHERE id = $1
            """,
            player,
            pseudonym);

        var normalUser = await Sql.FailsAsync(
            db.DataSource,
            "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES (gen_random_uuid(), $1, $1, 'guest', 'active')",
            "d_" + Guid.CreateVersion7().ToString("N")[^18..]);
        Assert.Equal("ck_players_deleted_pseudonym", normalUser.ConstraintName);
    }

    [Theory]
    [InlineData("ck_players_auth_subject_by_kind",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES (gen_random_uuid(), 'reg_noauth', 'reg_noauth', 'registered', 'active')")]
    [InlineData("ck_players_auth_subject_by_kind",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, auth_subject) VALUES (gen_random_uuid(), 'guest_auth', 'guest_auth', 'guest', 'active', 'sub-1')")]
    [InlineData("ck_players_deleted_at",
        "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status, deleted_at) VALUES (gen_random_uuid(), 'del_at', 'del_at', 'guest', 'active', now())")]
    public async Task Player_DerivedFieldsAreConsistent(string constraint, string sql)
    {
        var error = await Sql.FailsAsync(db.DataSource, sql);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task GameSession_RewardDayIsUtcDateOfStart()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);

        var error = await Sql.FailsAsync(
            db.DataSource,
            """
            INSERT INTO vybe.game_sessions (id, player_id, game_id, ruleset_id, seed, status, start_deadline, started_at,
                                            submit_deadline, reward_day)
            VALUES (gen_random_uuid(), $1, 'street-hoops', $2, 1, 'active', now(), TIMESTAMPTZ '2026-11-06 23:59:00+00',
                    now(), DATE '2026-11-07')
            """,
            player,
            ruleset);

        Assert.Equal("ck_game_sessions_reward_day_utc", error.ConstraintName);
    }

    [Fact]
    public async Task DataExport_StatusFieldsAreConsistent()
    {
        var player = await Sql.PlayerAsync(db.DataSource);

        var ready = await Sql.FailsAsync(
            db.DataSource, "INSERT INTO vybe.data_export_requests (id, player_id, status) VALUES (gen_random_uuid(), $1, 'ready')", player);
        var failed = await Sql.FailsAsync(
            db.DataSource, "INSERT INTO vybe.data_export_requests (id, player_id, status) VALUES (gen_random_uuid(), $1, 'failed')", player);

        Assert.Equal("ck_data_export_requests_ready_fields", ready.ConstraintName);
        Assert.Equal("ck_data_export_requests_failed_fields", failed.ConstraintName);
    }

    [Fact]
    public async Task FinishedSessions_AllowBackfillOfANewColumn()
    {
        // Expand/contract: a later migration adds a column and backfills it on finished sessions.
        var player = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        await VerifiedSessionAsync(player, ruleset);

        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var alter = new NpgsqlCommand("ALTER TABLE vybe.game_sessions ADD COLUMN backfill_probe int", connection, transaction))
        {
            await alter.ExecuteNonQueryAsync();
        }

        await using (var backfill = new NpgsqlCommand(
            "UPDATE vybe.game_sessions SET backfill_probe = 1 WHERE player_id = $1", connection, transaction))
        {
            backfill.Parameters.Add(new NpgsqlParameter { Value = player });
            Assert.Equal(1, await backfill.ExecuteNonQueryAsync());
        }

        await transaction.RollbackAsync();
    }

    private static async Task LockedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid player)
    {
        // lock_timeout turns a regression into a fast failure instead of a hung test.
        await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction))
        {
            await timeout.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM vybe.players WHERE id = $1 FOR UPDATE", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = player });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CompleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid challenge, Guid winner)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE vybe.challenges SET status = 'completed', resolved_at = now(), winner_player_id = $2
            WHERE id = $1 AND status = 'accepted'
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = challenge });
        command.Parameters.Add(new NpgsqlParameter { Value = winner });
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<Guid> ItemAsync()
    {
        var item = Guid.CreateVersion7();
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.catalog_items (id, sku, slot, name, price, asset_key, compatible_body_presets, rarity, is_active, catalog_version)
            VALUES ($1, $2, 'top', 'Tee', 200, 'tee', '{body_a}', 'common', true, 1)
            """,
            item,
            "sku_" + item.ToString("N")[^10..]);
        return item;
    }

    private async Task<Guid> VerifiedSessionAsync(Guid player, string ruleset, Guid? challengeId = null)
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.game_sessions (id, player_id, game_id, ruleset_id, seed, status, start_deadline, started_at,
                                            submit_deadline, submitted_at, verified_at, reward_day, score, event_count, challenge_id)
            VALUES ($1, $2, 'street-hoops', $3, 7, 'verified', now(), now(), now() + interval '65 seconds', now(), now(),
                    (now() AT TIME ZONE 'UTC')::date, 1200, 9, $4)
            """,
            id,
            player,
            ruleset,
            challengeId.HasValue ? challengeId.Value : DBNull.Value);
        return id;
    }

    /// <summary>An accepted challenge creator → recipient; optionally with the recipient's verified run attached.</summary>
    private async Task<Guid> AcceptedChallengeWithRecipientRunAsync(Guid creator, Guid recipient, string ruleset, bool attachRun = true)
    {
        var creatorRun = await VerifiedSessionAsync(creator, ruleset);
        var challenge = Guid.CreateVersion7();
        await Sql.ExecuteAsync(
            db.DataSource,
            """
            INSERT INTO vybe.challenges (id, creator_player_id, creator_session_id, recipient_player_id, invite_token_hash,
                                         ruleset_id, status, accept_deadline, accepted_at, complete_deadline)
            VALUES ($1, $2, $3, $4, sha256($1::text::bytea), $5, 'accepted', now() + interval '1 day', now(), now() + interval '1 day')
            """,
            challenge,
            creator,
            creatorRun,
            recipient,
            ruleset);

        if (attachRun)
        {
            var recipientRun = await VerifiedSessionAsync(recipient, ruleset, challenge);
            await Sql.ExecuteAsync(
                db.DataSource, "UPDATE vybe.challenges SET recipient_session_id = $1 WHERE id = $2", recipientRun, challenge);
        }

        return challenge;
    }
}
