using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Auth;
using Vybe.Api.Http;
using Vybe.Data;
using Vybe.Data.Entities;
using Vybe.GameRules;

namespace Vybe.Api.Gameplay;

/// <summary>
/// <c>POST /games/sessions/{id}/submit</c> (spec §5, §6, §7). Verifies synchronously: the outcome, the
/// daily counters, the reward ledger rows and the XP/level update commit in one transaction that locks
/// rows of the caller only (invariant 10). Quest progress and anti-automation flags are not written yet.
/// </summary>
internal sealed partial class SubmitGameSession(
    VybeDbContext db,
    TimeProvider clock,
    RulesetStore rulesets,
    IdempotencyStore idempotency,
    ILogger<SubmitGameSession> logger)
{
    public const string Operation = "submitGameSession";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string TraceEncoding = "gzip+json";

    public async Task<IResult> HandleAsync(HttpContext http, Guid sessionId, CancellationToken cancellationToken)
    {
        var keyHeader = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrEmpty(keyHeader))
        {
            return ApiProblem.Result(http, StatusCodes.Status428PreconditionRequired, ProblemCodes.IdempotencyKeyRequired);
        }

        if (!Guid.TryParseExact(keyHeader, "D", out var key))
        {
            return ApiProblem.Result(http, StatusCodes.Status422UnprocessableEntity, ProblemCodes.InvalidIdempotencyKey);
        }

        // 400 and 413 leave the session untouched (it stays active until submit_deadline).
        var body = await JsonBody.ReadAsync(http.Request, cancellationToken);
        using var document = body.Document;
        if (body.Status == BodyStatus.TooLarge)
        {
            return ApiProblem.Result(http, StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge);
        }

        if (document is null || !TryReadShape(document.RootElement, out var submittedRulesetId, out var rawEvents))
        {
            return ApiProblem.Result(http, StatusCodes.Status400BadRequest, ProblemCodes.MalformedRequest);
        }

        var playerId = http.User.PlayerId();
        if (!await db.GameSessions.AnyAsync(s => s.Id == sessionId && s.PlayerId == playerId, cancellationToken))
        {
            return ApiProblem.Result(http, StatusCodes.Status404NotFound, ProblemCodes.NotFound);
        }

        var actor = playerId.ToString();
        var start = await idempotency.StartAsync(actor, Operation, key, RequestHash(sessionId, body.Bytes), cancellationToken);
        DateTime lease;
        switch (start)
        {
            case IdempotencyStart.Acquired acquired:
                lease = acquired.Lease;
                break;
            case IdempotencyStart.Replay replay:
                return Results.Content(replay.Body, "application/json", Encoding.UTF8, replay.Status);
            case IdempotencyStart.Mismatch:
                return ApiProblem.Result(http, StatusCodes.Status422UnprocessableEntity, ProblemCodes.IdempotencyKeyMismatch);
            default:
                return ApiProblem.Result(http, StatusCodes.Status409Conflict, ProblemCodes.RequestInProgress);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (status, response) = await EvaluateAsync(sessionId, playerId, submittedRulesetId, rawEvents, cancellationToken);
        var json = JsonSerializer.Serialize(response, response.GetType(), ApiProblem.JsonOptions(http));
        if (!await idempotency.CompleteAsync(actor, Operation, key, lease, status, json, cancellationToken))
        {
            // Our lease elapsed and another request took it over; that request owns the outcome.
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Result(http, StatusCodes.Status409Conflict, ProblemCodes.RequestInProgress);
        }

        await transaction.CommitAsync(cancellationToken);
        return Results.Content(json, "application/json", Encoding.UTF8, status);
    }

    public static RewardsResponse Rewards(GameSession session, int levelAfter) =>
        new(session.RewardCoins, session.RewardXp, levelAfter);

    /// <summary>400 only when the body is not an object with <c>rulesetId</c> (string) and <c>events</c> (array).</summary>
    private static bool TryReadShape(JsonElement root, out string rulesetId, out JsonElement events)
    {
        rulesetId = string.Empty;
        events = default;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rulesetId", out var rulesetElement) || rulesetElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("events", out events) || events.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        rulesetId = rulesetElement.GetString()!;
        return true;
    }

    private static string RequestHash(Guid sessionId, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(sessionId.ToString("D") + "\n");
        return Convert.ToHexStringLower(SHA256.HashData([.. prefix, .. body]));
    }

    private async Task<(int Status, object Body)> EvaluateAsync(
        Guid sessionId,
        Guid playerId,
        string submittedRulesetId,
        JsonElement rawEvents,
        CancellationToken cancellationToken)
    {
        var player = await RowLocks.PlayerAsync(db, playerId, cancellationToken);
        var session = await RowLocks.SessionAsync(db, sessionId, playerId, cancellationToken)
                      ?? throw new InvalidOperationException($"session {sessionId} disappeared");

        // A repeated submission (any key) returns the canonical result and grants nothing.
        switch (session.Status)
        {
            case GameSessionStatus.Verified:
                return (StatusCodes.Status200OK, new SubmitVerifiedResponse(session.Id, "verified", session.Score!.Value, Rewards(session, player.Level)));
            case GameSessionStatus.Rejected:
                return (StatusCodes.Status200OK, new SubmitRejectedResponse(session.Id, "rejected", TextEnums.ToDatabase(session.RejectionReason!.Value)));
            case GameSessionStatus.Expired:
                return (StatusCodes.Status200OK, new SubmitStatusResponse(session.Id, "expired"));
            case GameSessionStatus.Submitted:
                return (StatusCodes.Status202Accepted, new SubmitStatusResponse(session.Id, "submitted"));
        }

        var ruleset = await rulesets.GetAsync(session.RulesetId, cancellationToken);
        var now = clock.UtcNowMicros();
        var timing = new SessionTiming(
            session.Status == GameSessionStatus.Created ? SessionStatus.Created : SessionStatus.Active,
            session.StartedAt?.ToUnixMs() ?? 0,
            session.SubmitDeadline?.ToUnixMs() ?? 0);
        EvaluateResult Evaluate(IReadOnlyList<ShotEvent> events) => StreetHoopsSimulator.Evaluate(new EvaluateInput(
            ruleset.Config,
            ruleset.Id,
            session.Seed,
            timing,
            new Submission(submittedRulesetId, now.ToUnixMs(), events)));

        // Steps 1-3 of the §7 check order do not read the events, so an empty trace decides them;
        // they must win over step 4 (invalid_event), which the simulator does not know.
        var result = Evaluate([]);
        if (result.Outcome == Outcome.Verified)
        {
            var events = RawEvents.Parse(rawEvents);
            if (events is null)
            {
                return await RejectAsync(session, SessionRejectionReason.InvalidEvent, rawEvents, now, cancellationToken);
            }

            result = Evaluate(events);
        }

        switch (result.Outcome)
        {
            case Outcome.Expired:
                // Late submit: the same state the sweeper writes (status only).
                session.Status = GameSessionStatus.Expired;
                await db.SaveChangesAsync(cancellationToken);
                LogOutcome(logger, session.Id, playerId, "expired", null, null);
                return (StatusCodes.Status200OK, new SubmitStatusResponse(session.Id, "expired"));
            case Outcome.Rejected:
                return await RejectAsync(session, ToSessionReason(result.Reason!.Value), rawEvents, now, cancellationToken);
            default:
                return await VerifyAsync(player, session, ruleset.Config, result, rawEvents, now, cancellationToken);
        }
    }

    private async Task<(int, object)> RejectAsync(
        GameSession session,
        SessionRejectionReason reason,
        JsonElement rawEvents,
        DateTime now,
        CancellationToken cancellationToken)
    {
        session.Status = GameSessionStatus.Rejected;
        session.RejectionReason = reason;
        session.SubmittedAt = now;
        session.EventCount = rawEvents.GetArrayLength();
        StoreTrace(session, rawEvents, now);
        await db.SaveChangesAsync(cancellationToken);

        var code = TextEnums.ToDatabase(reason);
        LogOutcome(logger, session.Id, session.PlayerId, "rejected", code, null);
        return (StatusCodes.Status200OK, new SubmitRejectedResponse(session.Id, "rejected", code));
    }

    private async Task<(int, object)> VerifyAsync(
        Player player,
        GameSession session,
        StreetHoopsConfig config,
        EvaluateResult result,
        JsonElement rawEvents,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var score = result.Score!.Value;
        var eventCount = result.EventCount!.Value;
        var rewardDay = DateOnly.FromDateTime(session.StartedAt!.Value);
        long coins = 0;
        var xp = 0;

        // Minimum play (spec §7): below minShotsForRewards the run is verified but grants nothing.
        if (eventCount >= config.MinShotsForRewards)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO vybe.player_daily_counters AS c (player_id, day, runs_rewarded)
                 VALUES ({player.Id}, {rewardDay}, 1)
                 ON CONFLICT (player_id, day) DO UPDATE SET runs_rewarded = c.runs_rewarded + 1
                 """,
                cancellationToken);
            // The upsert holds the row lock until commit, so this read is this run's slot.
            var counter = await db.PlayerDailyCounters
                .SingleAsync(c => c.PlayerId == player.Id && c.Day == rewardDay, cancellationToken);
            coins = RunRewards.CoinsForRun(RunRewards.BaseCoins(score), counter.RunsRewarded);
            xp = RunRewards.XpGrant(RunRewards.BaseXp(score), counter.XpFromGames);
            counter.XpFromGames += xp;
        }

        var levelBefore = player.Level;
        if (xp > 0)
        {
            player.XpTotal += xp;
            player.Level = RunRewards.LevelFor(player.XpTotal);
            player.UpdatedAt = now;
        }

        if (coins > 0 || player.Level > levelBefore)
        {
            var wallet = await RowLocks.WalletAsync(db, player.Id, cancellationToken);
            if (coins > 0)
            {
                wallet.Balance += coins;
                db.WalletTransactions.Add(Ledger(player.Id, coins, wallet.Balance, LedgerReason.GameReward, "game_session", session.Id, $"game:{session.Id:D}", now));
            }

            // One level reward per new level (spec §8), exactly once by ledger key level:{L}.
            for (var level = levelBefore + 1; level <= player.Level; level++)
            {
                wallet.Balance += RunRewards.LevelUpCoins;
                db.WalletTransactions.Add(Ledger(player.Id, RunRewards.LevelUpCoins, wallet.Balance, LedgerReason.LevelReward, "level", null, $"level:{level}", now));
            }

            wallet.UpdatedAt = now;
        }

        session.Status = GameSessionStatus.Verified;
        session.SubmittedAt = now;
        session.VerifiedAt = now;
        session.RewardDay = rewardDay;
        session.EventCount = eventCount;
        session.Score = score;
        session.RewardCoins = coins;
        session.RewardXp = xp;
        StoreTrace(session, rawEvents, now);
        await db.SaveChangesAsync(cancellationToken);

        LogOutcome(logger, session.Id, player.Id, "verified", null, score);
        return (StatusCodes.Status200OK, new SubmitVerifiedResponse(session.Id, "verified", score, Rewards(session, player.Level)));
    }

    private static WalletTransaction Ledger(
        Guid playerId,
        long delta,
        long balanceAfter,
        LedgerReason reason,
        string referenceType,
        Guid? referenceId,
        string ledgerKey,
        DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            PlayerId = playerId,
            Delta = delta,
            BalanceAfter = balanceAfter,
            Reason = reason,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            LedgerKey = ledgerKey,
            CreatedAt = now,
        };

    /// <summary>Keeps the submitted trace (spec §4 <c>game_input_traces</c>, short retention) and its hash.</summary>
    private void StoreTrace(GameSession session, JsonElement rawEvents, DateTime now)
    {
        var raw = Encoding.UTF8.GetBytes(rawEvents.GetRawText());
        session.InputTraceHash = Convert.ToHexStringLower(SHA256.HashData(raw));

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        if (compressed.Length > JsonBody.MaxBytes)
        {
            LogTraceTooLarge(logger, session.Id, compressed.Length);
            return;
        }

        db.GameInputTraces.Add(new GameInputTrace
        {
            SessionId = session.Id,
            CompressedTrace = compressed.ToArray(),
            Encoding = TraceEncoding,
            EventCount = rawEvents.GetArrayLength(),
            ReceivedAt = now,
        });
    }

    private static SessionRejectionReason ToSessionReason(RejectionReason reason) => reason switch
    {
        RejectionReason.NotStarted => SessionRejectionReason.NotStarted,
        RejectionReason.RulesetMismatch => SessionRejectionReason.RulesetMismatch,
        RejectionReason.OutOfRange => SessionRejectionReason.OutOfRange,
        RejectionReason.TooManyEvents => SessionRejectionReason.TooManyEvents,
        RejectionReason.NonMonotonic => SessionRejectionReason.NonMonotonic,
        RejectionReason.CadenceViolation => SessionRejectionReason.CadenceViolation,
        RejectionReason.SubmittedTooEarly => SessionRejectionReason.SubmittedTooEarly,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown rejection reason"),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Game session {SessionId} of player {PlayerId}: {Outcome} {Reason} score {Score}")]
    private static partial void LogOutcome(ILogger logger, Guid sessionId, Guid playerId, string outcome, string? reason, int? score);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Input trace of game session {SessionId} not stored: {Bytes} bytes compressed")]
    private static partial void LogTraceTooLarge(ILogger logger, Guid sessionId, long bytes);
}
