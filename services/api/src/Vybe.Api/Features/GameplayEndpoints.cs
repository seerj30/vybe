using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Features;

public sealed record RulesetResponse(string Id, string GameId, int Version, JsonElement Config, string Checksum);

public sealed record SessionCreateRequest(Guid? ChallengeId);

public sealed record SessionCreated(Guid SessionId, string Status, string RulesetId, string RulesetChecksum, DateTime StartDeadline);

public sealed record SessionStarted(long Seed, DateTime StartedAt, DateTime SubmitDeadline);

/// <summary>Run rewards; <paramref name="NoCoinsReason"/> says why <paramref name="Coins"/> is 0 (null when coins were earned).</summary>
public sealed record RewardsResponse(long Coins, int Xp, int LevelAfter, NoCoinsReason? NoCoinsReason);

public sealed record GameSessionResponse(
    Guid SessionId, string GameId, string RulesetId, GameSessionStatus Status, Guid? ChallengeId, DateTime CreatedAt,
    DateTime StartDeadline, DateTime? StartedAt, DateTime? SubmitDeadline, int? Score, RewardsResponse? Rewards,
    SessionRejectionReason? RejectionReason);

public static class GameplayEndpoints
{
    private static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(10); // provisional, spec §17 item 4

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/games/rulesets/{rulesetId}", GetRulesetAsync).AllowAnonymous();
        api.MapPost("/games/street-hoops/sessions", (SessionCreateRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken ct) =>
            CreateSessionAsync(Games.StreetHoops, request, db, current, clock, ct));
        api.MapPost("/games/street-juggle/sessions", (SessionCreateRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken ct) =>
            CreateSessionAsync(Games.StreetJuggle, request, db, current, clock, ct));
        api.MapPost("/games/sessions/{sessionId:guid}/start", StartSessionAsync);
        api.MapPost("/games/sessions/{sessionId:guid}/submit", SubmitAsync);
        api.MapGet("/games/sessions/{sessionId:guid}", GetSessionAsync);
    }

    private static async Task<IResult> GetRulesetAsync(string rulesetId, HttpContext context, VybeDbContext db, CancellationToken cancellationToken)
    {
        var ruleset = await db.GameRulesets.AsNoTracking().SingleOrDefaultAsync(r => r.Id == rulesetId, cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        using var config = JsonDocument.Parse(ruleset.Config);
        return TypedResults.Ok(new RulesetResponse(ruleset.Id, ruleset.GameId, ruleset.Version, config.RootElement.Clone(), ruleset.Checksum));
    }

    private static async Task<IResult> CreateSessionAsync(
        string gameId, SessionCreateRequest request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        var player = await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, cancellationToken);
        current.EnsureNotSuspended(player);

        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Challenge? challenge = null;
        var seed = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); // uniform uint32 (spec §4)
        string rulesetId;
        if (request.ChallengeId is { } challengeId)
        {
            // A challenge is played in its own game: another game's route does not see it, before any status check.
            var challengeGame = await db.Challenges.AsNoTracking()
                .Where(c => c.Id == challengeId)
                .Join(db.GameRulesets, c => c.RulesetId, r => r.Id, (c, r) => r.GameId)
                .SingleOrDefaultAsync(cancellationToken);
            if (challengeGame is not null && challengeGame != gameId)
            {
                throw Problem.Of(404, "challenge_unavailable");
            }

            // Recipient run: the creator run's ruleset and seed (spec §7 challenge rules).
            (challenge, var creatorSeed) = await ChallengeRuns.LockForRecipientAsync(db, playerId, challengeId, now, cancellationToken);
            seed = (uint)creatorSeed;
            rulesetId = challenge.RulesetId;
        }
        else
        {
            rulesetId = await db.GameRulesets.AsNoTracking()
                .Where(r => r.GameId == gameId && r.RetiredAt == null && r.ActiveFrom <= now)
                .OrderByDescending(r => r.Version)
                .Select(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"no active {gameId} ruleset");
        }

        // Serialize this player's session creations (double tap, two tabs); otherwise both inserts
        // race on ux_game_sessions_one_open_per_player and one fails.
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE", cancellationToken);
        await ExpireOpenSessionsAsync(db, playerId, cancellationToken);

        var session = new GameSession
        {
            PlayerId = playerId,
            GameId = gameId,
            RulesetId = rulesetId,
            Seed = seed,
            Status = GameSessionStatus.Created,
            StartDeadline = now + StartWindow,
            ChallengeId = challenge?.Id,
        };
        db.GameSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        if (challenge is not null)
        {
            await ChallengeRuns.BindSessionAsync(db, challenge.Id, session.Id, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var checksum = await db.GameRulesets.Where(r => r.Id == rulesetId).Select(r => r.Checksum).SingleAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/games/sessions/{session.Id}",
            new SessionCreated(session.Id, "created", rulesetId, checksum, session.StartDeadline));
    }

    /// <summary>
    /// One open session per player (spec §4): a new session replaces the old one. A replaced recipient
    /// session ends <c>expired</c>, so its challenge moves <c>accepted -> expired</c> in the same transaction.
    /// </summary>
    private static async Task ExpireOpenSessionsAsync(VybeDbContext db, Guid playerId, CancellationToken cancellationToken)
    {
        var replaced = await db.GameSessions
            .Where(s => s.PlayerId == playerId && (s.Status == GameSessionStatus.Created || s.Status == GameSessionStatus.Active))
            .Select(s => new { s.Id, s.ChallengeId })
            .ToListAsync(cancellationToken);
        await db.GameSessions
            .Where(s => s.PlayerId == playerId && (s.Status == GameSessionStatus.Created || s.Status == GameSessionStatus.Active))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, GameSessionStatus.Expired), cancellationToken);
        foreach (var old in replaced.Where(s => s.ChallengeId is not null))
        {
            await ChallengeRuns.ExpireForRecipientSessionAsync(db, old.Id, cancellationToken);
        }
    }

    private static async Task<SessionStarted> StartSessionAsync(
        Guid sessionId, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        // Row lock: a repeated start waits and then sees `active` (idempotent 200); a replacing
        // create-session waits and then expires the started session.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var session = await db.GameSessions
            .FromSql($"SELECT * FROM vybe.game_sessions WHERE id = {sessionId} AND player_id = {playerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        var now = clock.UtcNowForDb();

        if (session.Status == GameSessionStatus.Active)
        {
            return new SessionStarted(session.Seed, session.StartedAt!.Value, session.SubmitDeadline!.Value); // idempotent repeat
        }

        if (session.Status != GameSessionStatus.Created)
        {
            throw Problem.Of(410, "session_expired");
        }

        if (now > session.StartDeadline)
        {
            session.Status = GameSessionStatus.Expired;
            await db.SaveChangesAsync(cancellationToken);
            await ChallengeRuns.ExpireForRecipientSessionAsync(db, session.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Problem.Of(410, "session_expired");
        }

        if (session.ChallengeId is { } challengeId)
        {
            // Spec §5/§6: start also requires now < complete_deadline and a challenge still accepted.
            var challenge = await db.Challenges.AsNoTracking()
                .Where(c => c.Id == challengeId).Select(c => new { c.Status, c.CompleteDeadline }).SingleAsync(cancellationToken);
            if (now >= challenge.CompleteDeadline)
            {
                throw Problem.Of(410, "challenge_expired");
            }

            if (challenge.Status != ChallengeStatus.Accepted)
            {
                throw Problem.Of(404, "challenge_unavailable");
            }
        }

        var config = RulesetConfig.Parse(session.GameId, await RulesetJsonAsync(db, session.RulesetId, cancellationToken));
        session.Status = GameSessionStatus.Active;
        session.StartedAt = now;
        session.SubmitDeadline = now.AddMilliseconds(config.DurationMs + config.GraceMs);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SessionStarted(session.Seed, session.StartedAt.Value, session.SubmitDeadline.Value);
    }

    private static async Task<IResult> SubmitAsync(
        Guid sessionId, HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency,
        TimeProvider clock, AchievementCatalog achievements, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        SubmitParser.Shape shape;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw Problem.MalformedRequest();
        }

        using (document)
        {
            shape = SubmitParser.ParseShape(document);
            var hash = Idempotency.Hash("games.submit", sessionId.ToString(), body);
            var events = shape.Events.Clone();
            return await idempotency.RunAsync(context, playerId.ToString(), "games.submit", hash,
                ct => VerifyAsync(db, clock, achievements, playerId, sessionId, shape.RulesetId, events, ct), cancellationToken);
        }
    }

    private static async Task<OperationResult> VerifyAsync(
        VybeDbContext db, TimeProvider clock, AchievementCatalog achievements, Guid playerId, Guid sessionId, string submittedRulesetId,
        JsonElement events, CancellationToken cancellationToken)
    {
        // Lock order player row, then session row: the same order as create-session, which locks the
        // player and then expires the open session. The opposite order deadlocks the two.
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM vybe.players WHERE id = {playerId} FOR NO KEY UPDATE", cancellationToken);
        var session = await db.GameSessions
            .FromSql($"SELECT * FROM vybe.game_sessions WHERE id = {sessionId} AND player_id = {playerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw Problem.Of(404, "not_found");

        // A finished session always answers with its canonical result (spec §6).
        if (session.Status is GameSessionStatus.Verified or GameSessionStatus.Rejected or GameSessionStatus.Expired)
        {
            return await CanonicalAsync(db, achievements, session, cancellationToken);
        }

        var now = clock.UtcNowForDb();
        var eventsJson = events.GetRawText();
        var run = new RunContext(
            session.RulesetId,
            session.Seed,
            new SessionTiming(
                session.Status == GameSessionStatus.Created ? SessionStatus.Created : SessionStatus.Active,
                ToMs(session.StartedAt ?? now),
                ToMs(session.SubmitDeadline ?? now)),
            submittedRulesetId,
            ToMs(now));
        var verdict = Games.For(session.GameId).Verify(await RulesetJsonAsync(db, session.RulesetId, cancellationToken), run, events);

        if (verdict.InvalidEvent)
        {
            return await FinishRejectedAsync(db, session, now, SessionRejectionReason.InvalidEvent, eventsJson, null, cancellationToken);
        }

        switch (verdict.Outcome)
        {
            case Outcome.Expired:
                session.Status = GameSessionStatus.Expired;
                await db.SaveChangesAsync(cancellationToken);
                await ChallengeRuns.ExpireForRecipientSessionAsync(db, session.Id, cancellationToken);
                return new OperationResult(200, new { sessionId = session.Id, status = "expired" });

            case Outcome.Rejected:
                return await FinishRejectedAsync(db, session, now, Map(verdict.Reason!.Value), eventsJson, verdict.EventCount, cancellationToken);
        }

        var eventCount = verdict.EventCount!.Value;
        var (rewards, runNumber) = await RewardService.GrantForRunAsync(
            db, session, verdict.Config, verdict.Score, eventCount, verdict.MaxStreak, now, cancellationToken);
        var flags = verdict.Flags.ToList();
        if (runNumber is { } n
            && await ReviewFlags.IsDailyCapStreakAsync(db, playerId, DateOnly.FromDateTime(session.StartedAt!.Value), n, cancellationToken))
        {
            flags.Add(ReviewFlags.DailyCapStreak);
        }

        session.ReviewFlags = [.. flags];
        session.Status = GameSessionStatus.Verified;
        session.SubmittedAt = now;
        session.VerifiedAt = now;
        session.Score = verdict.Score;
        session.EventCount = eventCount;
        session.RewardDay = DateOnly.FromDateTime(session.StartedAt!.Value);
        session.RewardCoins = rewards.Coins;
        session.RewardXp = rewards.Xp;
        AddTrace(db, session, eventsJson, eventCount, now);
        await db.SaveChangesAsync(cancellationToken);
        var counts = LeaderboardEndpoints.Counts(session, runNumber is not null);
        if (counts)
        {
            await LeaderboardEndpoints.RecordAsync(db, session, verdict.Score, now, cancellationToken);
        }

        // P1 achievements: same transaction, after the leaderboard row (the weekly rank reads it). Reward-eligible runs only.
        IReadOnlyList<AchievementUnlocked> unlocked = runNumber is { } number
            ? await AchievementProgress.RecordRunAsync(db, achievements, session, counts, number, now, cancellationToken)
            : [];
        if (unlocked.Count > 0)
        {
            rewards = rewards with { LevelAfter = await db.Players.Where(p => p.Id == playerId).Select(p => p.Level).SingleAsync(cancellationToken) };
        }

        if (session.ChallengeId is not null)
        {
            await ChallengeRuns.ResolveAsync(db, session, verdict.Score, now, cancellationToken);
        }

        return new OperationResult(200, new
        {
            sessionId = session.Id,
            status = "verified",
            score = verdict.Score,
            rewards,
            achievementsUnlocked = unlocked,
        });
    }

    private static async Task<OperationResult> FinishRejectedAsync(
        VybeDbContext db, GameSession session, DateTime now, SessionRejectionReason reason, string eventsJson, int? eventCount,
        CancellationToken cancellationToken)
    {
        session.Status = GameSessionStatus.Rejected;
        session.RejectionReason = reason;
        session.SubmittedAt = now;
        session.EventCount = eventCount;
        AddTrace(db, session, eventsJson, eventCount ?? 0, now);
        await db.SaveChangesAsync(cancellationToken);
        await ChallengeRuns.ExpireForRecipientSessionAsync(db, session.Id, cancellationToken);
        return new OperationResult(200, new { sessionId = session.Id, status = "rejected", rejectionReason = reason });
    }

    private static async Task<OperationResult> CanonicalAsync(
        VybeDbContext db, AchievementCatalog achievements, GameSession session, CancellationToken cancellationToken) =>
        session.Status switch
        {
            GameSessionStatus.Verified => new OperationResult(200, new
            {
                sessionId = session.Id,
                status = "verified",
                score = session.Score,
                rewards = await StoredRewardsAsync(db, session,
                    await db.Players.Where(p => p.Id == session.PlayerId).Select(p => p.Level).SingleAsync(cancellationToken),
                    cancellationToken),
                achievementsUnlocked = await AchievementProgress.UnlockedByRunAsync(db, achievements, session, cancellationToken),
            }),
            GameSessionStatus.Rejected => new OperationResult(200, new
            {
                sessionId = session.Id,
                status = "rejected",
                rejectionReason = session.RejectionReason,
            }),
            _ => new OperationResult(200, new { sessionId = session.Id, status = "expired" }),
        };

    private static async Task<GameSessionResponse> GetSessionAsync(
        Guid sessionId, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var s = await db.GameSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId && x.PlayerId == playerId, cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        var level = await db.Players.Where(p => p.Id == playerId).Select(p => p.Level).SingleAsync(cancellationToken);
        var rewards = s.Status == GameSessionStatus.Verified ? await StoredRewardsAsync(db, s, level, cancellationToken) : null;
        return new GameSessionResponse(s.Id, s.GameId, s.RulesetId, s.Status, s.ChallengeId, s.CreatedAt, s.StartDeadline,
            s.StartedAt, s.SubmitDeadline, s.Score, rewards, s.RejectionReason);
    }

    /// <summary>Rewards of a stored verified session; the no-coins reason is derived from the stored run (no extra column).</summary>
    private static async Task<RewardsResponse> StoredRewardsAsync(VybeDbContext db, GameSession session, int level, CancellationToken cancellationToken)
    {
        NoCoinsReason? noCoins = null;
        if (session.RewardCoins == 0)
        {
            var config = RulesetConfig.Parse(session.GameId, await RulesetJsonAsync(db, session.RulesetId, cancellationToken));
            noCoins = Rewards.NoCoins(config, session.RewardCoins, session.Score ?? 0, session.EventCount ?? 0,
                session.StartedAt!.Value, session.SubmittedAt!.Value);
        }

        return new RewardsResponse(session.RewardCoins, session.RewardXp, level, noCoins);
    }

    private static async Task<string> RulesetJsonAsync(VybeDbContext db, string rulesetId, CancellationToken cancellationToken) =>
        await db.GameRulesets.Where(r => r.Id == rulesetId).Select(r => r.Config).SingleAsync(cancellationToken);

    private static void AddTrace(VybeDbContext db, GameSession session, string eventsJson, int eventCount, DateTime now)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(eventsJson);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize))
        {
            gzip.Write(raw);
        }

        session.InputTraceHash = Convert.ToHexStringLower(SHA256.HashData(raw));
        db.GameInputTraces.Add(new GameInputTrace
        {
            SessionId = session.Id,
            CompressedTrace = compressed.ToArray(),
            Encoding = "gzip+json",
            EventCount = eventCount,
            ReceivedAt = now,
        });
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static SessionRejectionReason Map(RejectionReason reason) => reason switch
    {
        RejectionReason.NotStarted => SessionRejectionReason.NotStarted,
        RejectionReason.RulesetMismatch => SessionRejectionReason.RulesetMismatch,
        RejectionReason.OutOfRange => SessionRejectionReason.OutOfRange,
        RejectionReason.TooManyEvents => SessionRejectionReason.TooManyEvents,
        RejectionReason.NonMonotonic => SessionRejectionReason.NonMonotonic,
        RejectionReason.CadenceViolation => SessionRejectionReason.CadenceViolation,
        RejectionReason.SubmittedTooEarly => SessionRejectionReason.SubmittedTooEarly,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
