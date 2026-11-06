using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vybe.Data.Entities;

namespace Vybe.Data.Tests;

/// <summary>Legal state transitions through EF, and the guarantees around them (spec §4, §6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LifecycleTests(PostgresFixture db)
{
    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public async Task GameSession_CreatedToVerified_ThroughEf_IncrementsVersion()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        await using var context = db.CreateContext();

        var session = new GameSession
        {
            PlayerId = player,
            GameId = "street-hoops",
            RulesetId = ruleset,
            Seed = 4_294_967_295,
            Status = GameSessionStatus.Created,
            StartDeadline = Now.AddMinutes(10),
        };
        context.GameSessions.Add(session);
        await context.SaveChangesAsync();
        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(0, session.Version);

        session.Status = GameSessionStatus.Active;
        session.StartedAt = Now;
        session.SubmitDeadline = Now.AddSeconds(65);
        await context.SaveChangesAsync();

        session.Status = GameSessionStatus.Submitted;
        session.SubmittedAt = Now;
        await context.SaveChangesAsync();

        session.Status = GameSessionStatus.Verified;
        session.VerifiedAt = Now;
        session.Score = 1_950;
        session.EventCount = 14;
        session.RewardDay = DateOnly.FromDateTime(session.StartedAt!.Value);
        session.RewardCoins = 50;
        session.RewardXp = 30;
        await context.SaveChangesAsync();
        Assert.Equal(3, session.Version);

        await using var reader = db.CreateContext();
        var stored = await reader.GameSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Empty(stored.ReviewFlags);
        Assert.Null(stored.RejectionReason);
        Assert.Equal(1_950, stored.Score);

        // Finished: only review flags may change.
        var change = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.game_sessions SET score = 1 WHERE id = $1", session.Id);
        Assert.Equal(Sql.IntegrityViolation, change.SqlState);
        session.ReviewFlags = ["perfect_rate"];
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GameSession_CreatedToRejected_StoresReason_AndIsImmutable()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var id = await Sql.SessionAsync(db.DataSource, player, ruleset);
        await using var context = db.CreateContext();

        var session = await context.GameSessions.SingleAsync(s => s.Id == id);
        session.Status = GameSessionStatus.Rejected;
        session.RejectionReason = SessionRejectionReason.NotStarted;
        await context.SaveChangesAsync();

        Assert.Equal("not_started", await Sql.ScalarAsync<string>(
            db.DataSource, "SELECT rejection_reason FROM vybe.game_sessions WHERE id = $1", id));
        var change = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.game_sessions SET status = 'verified' WHERE id = $1", id);
        Assert.Equal(Sql.IntegrityViolation, change.SqlState);
    }

    [Fact]
    public async Task Challenge_OpenToCompleted_ThroughEf()
    {
        var creator = await Sql.PlayerAsync(db.DataSource);
        var recipient = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var creatorRun = await VerifiedSessionAsync(creator, ruleset);
        await using var context = db.CreateContext();

        var challenge = new Challenge
        {
            CreatorPlayerId = creator,
            CreatorSessionId = creatorRun,
            InviteTokenHash = SHA256.HashData(Guid.NewGuid().ToByteArray()),
            RulesetId = ruleset,
            Status = ChallengeStatus.Open,
            AcceptDeadline = Now.AddHours(24),
        };
        context.Challenges.Add(challenge);
        await context.SaveChangesAsync();
        Assert.False(challenge.Suppressed);
        Assert.Equal(0, challenge.Version);

        challenge.Status = ChallengeStatus.Accepted;
        challenge.RecipientPlayerId = recipient;
        challenge.AcceptedAt = Now;
        challenge.CompleteDeadline = Now.AddHours(24);
        await context.SaveChangesAsync();

        var recipientRun = await VerifiedSessionAsync(recipient, ruleset, challenge.Id);
        challenge.Status = ChallengeStatus.Completed;
        challenge.RecipientSessionId = recipientRun;
        challenge.ResolvedAt = Now;
        challenge.WinnerPlayerId = recipient;
        await context.SaveChangesAsync();

        Assert.Equal(2, challenge.Version);
    }

    [Fact]
    public async Task Challenge_RejectsWinnerBeforeCompletion_AndOneSessionForBothSides()
    {
        var creator = await Sql.PlayerAsync(db.DataSource);
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        var run = await VerifiedSessionAsync(creator, ruleset);
        const string insert = """
            INSERT INTO vybe.challenges (id, creator_player_id, creator_session_id, invite_token_hash, ruleset_id,
                                         status, accept_deadline, winner_player_id, recipient_session_id)
            VALUES (gen_random_uuid(), $1, $2, sha256(gen_random_uuid()::text::bytea), $3, 'open', now(), $4, $5)
            """;

        var winner = await Sql.FailsAsync(db.DataSource, insert, creator, run, ruleset, creator, DBNull.Value);
        var sameSession = await Sql.FailsAsync(db.DataSource, insert, creator, run, ruleset, DBNull.Value, run);

        Assert.Equal("ck_challenges_winner_only_when_completed", winner.ConstraintName);
        Assert.Equal("ck_challenges_distinct_sessions", sameSession.ConstraintName);
    }

    [Fact]
    public async Task Wallet_ConcurrentEfUpdates_SecondOneFails()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        await using (var setup = db.CreateContext())
        {
            setup.Wallets.Add(new Wallet { PlayerId = player, Balance = 100 });
            await setup.SaveChangesAsync();
        }

        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var a = await first.Wallets.SingleAsync(w => w.PlayerId == player);
        var b = await second.Wallets.SingleAsync(w => w.PlayerId == player);

        a.Balance += 10;
        await first.SaveChangesAsync();
        Assert.Equal(1, a.Revision);

        b.Balance += 20;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Equal(110, await Sql.ScalarAsync<long>(db.DataSource, "SELECT balance FROM vybe.wallets WHERE player_id = $1", player));
    }

    [Fact]
    public async Task RawCompareAndSwap_CannotPassTwice()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        await Sql.ExecuteAsync(db.DataSource, "INSERT INTO vybe.wallets (player_id, balance) VALUES ($1, 100)", player);
        const string cas = "UPDATE vybe.wallets SET balance = balance - 10 WHERE player_id = $1 AND revision = 0";

        await Sql.ExecuteAsync(db.DataSource, cas, player);
        await Sql.ExecuteAsync(db.DataSource, cas, player); // matches no row: the trigger bumped revision

        Assert.Equal(90, await Sql.ScalarAsync<long>(db.DataSource, "SELECT balance FROM vybe.wallets WHERE player_id = $1", player));
    }

    [Fact]
    public async Task Player_LevelZeroFromEf_IsRejected_NotSilentlyDefaulted()
    {
        await using var context = db.CreateContext();
        var handle = "lvl0_" + Guid.CreateVersion7().ToString("N")[^10..];
        context.Players.Add(new Player
        {
            Handle = handle,
            HandleNormalized = handle,
            Kind = PlayerKind.Guest,
            Status = PlayerStatus.Active,
            Level = 0,
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal("ck_players_level", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Player_HandleNormalizedMustMatchHandle()
    {
        var error = await Sql.FailsAsync(
            db.DataSource,
            "INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES (gen_random_uuid(), 'bob_x', 'BOB_X', 'guest', 'active')");

        Assert.Equal("ck_players_handle_normalized", error.ConstraintName);
    }

    [Fact]
    public async Task Ledger_KeyMustMatchReasonAndReference()
    {
        var player = await Sql.PlayerAsync(db.DataSource);
        const string insert = """
            INSERT INTO vybe.wallet_transactions (id, player_id, delta, balance_after, reason, reference_id, reference_type, ledger_key)
            VALUES (gen_random_uuid(), $1, 100, 100, $2, $3, $4, $5)
            """;

        var wrongReference = await Sql.FailsAsync(
            db.DataSource, insert, player, "game_reward", Guid.CreateVersion7(), "game_session", "game:" + Guid.CreateVersion7());
        var levelTooHigh = await Sql.FailsAsync(db.DataSource, insert, player, "level_reward", DBNull.Value, "level", "level:51");

        Assert.All([wrongReference, levelTooHigh], e => Assert.Equal("ck_wallet_transactions_ledger_key_shape", e.ConstraintName));
    }

    [Fact]
    public async Task Ledger_OneChallengeRewardPerChallenge_AcrossPlayers()
    {
        var first = await Sql.PlayerAsync(db.DataSource);
        var second = await Sql.PlayerAsync(db.DataSource);
        var challenge = Guid.CreateVersion7();
        const string insert = """
            INSERT INTO vybe.wallet_transactions (id, player_id, delta, balance_after, reason, reference_id, ledger_key)
            VALUES (gen_random_uuid(), $1, 50, 50, 'challenge_reward', $2, 'challenge:' || $2::text)
            """;

        await Sql.ExecuteAsync(db.DataSource, insert, first, challenge);
        var error = await Sql.FailsAsync(db.DataSource, insert, second, challenge);

        Assert.Equal("ux_wallet_transactions_one_challenge_reward", error.ConstraintName);
    }

    [Fact]
    public async Task AuditLog_RejectsUpdateAndTruncate_AllowsOnlyRetentionDelete()
    {
        var actor = "admin-" + Guid.CreateVersion7().ToString("N")[^8..];
        const string insert = """
            INSERT INTO vybe.admin_audit_logs (id, actor_subject, actor_role, action, entity_type, entity_id, reason, created_at)
            VALUES (gen_random_uuid(), $1, 'moderator', 'report.resolve', 'player_report', $2, 'retention test', now() - $3::interval)
            """;
        await Sql.ExecuteAsync(db.DataSource, insert, actor, "old", "91 days");
        await Sql.ExecuteAsync(db.DataSource, insert, actor, "recent", "1 day");

        var update = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.admin_audit_logs SET reason = 'edited' WHERE actor_subject = $1", actor);
        var truncate = await Sql.FailsAsync(db.DataSource, "TRUNCATE vybe.admin_audit_logs");
        Assert.All([update, truncate], e => Assert.Equal(Sql.IntegrityViolation, e.SqlState));

        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var enable = new NpgsqlCommand("SELECT set_config('vybe.retention_delete', 'on', true)", connection, transaction))
        {
            await enable.ExecuteNonQueryAsync();
        }

        await using (var delete = new NpgsqlCommand(
            "DELETE FROM vybe.admin_audit_logs WHERE actor_subject = $1 AND entity_id = 'old'", connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = actor });
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();

        // Even with the setting on, rows younger than 90 days stay.
        await using var second = await db.DataSource.OpenConnectionAsync();
        await using (var enable = new NpgsqlCommand("SET vybe.retention_delete = 'on'", second))
        {
            await enable.ExecuteNonQueryAsync();
        }

        await using var recent = new NpgsqlCommand("DELETE FROM vybe.admin_audit_logs WHERE actor_subject = $1", second);
        recent.Parameters.Add(new NpgsqlParameter { Value = actor });
        Assert.Equal(Sql.IntegrityViolation, (await Assert.ThrowsAsync<PostgresException>(() => recent.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Ruleset_CannotBeUnretired()
    {
        var ruleset = await Sql.RulesetAsync(db.DataSource);
        await Sql.ExecuteAsync(db.DataSource, "UPDATE vybe.game_rulesets SET retired_at = now() WHERE id = $1", ruleset);

        var error = await Sql.FailsAsync(db.DataSource, "UPDATE vybe.game_rulesets SET retired_at = NULL WHERE id = $1", ruleset);

        Assert.Equal(Sql.IntegrityViolation, error.SqlState);
    }

    [Fact]
    public async Task EquippedJson_MustHaveAllSlotsAndTwoAccessoryPoints()
    {
        var player = await Sql.PlayerAsync(db.DataSource);

        var error = await Sql.FailsAsync(
            db.DataSource,
            """
            INSERT INTO vybe.avatar_configs (player_id, body_preset_id, skin_tone_id, face_preset_id, hair_preset_id, hair_color_id, equipped_json)
            VALUES ($1, 'b', 's', 'f', 'h', 'c', '{"hair":null,"top":null,"bottom":null,"shoes":null,"emote":null,"accessories":[null]}')
            """,
            player);

        Assert.Equal("ck_avatar_configs_equipped_json_shape", error.ConstraintName);
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
}
