using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Content;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record ChallengeCreateRequest(Guid? CreatorSessionId, string? RecipientHandle);

public sealed record RematchRequest(Guid? CreatorSessionId);

public sealed record PlayerSummary(string Handle, string? DisplayName, int Level);

public sealed record ChallengePreview(
    Guid ChallengeId, string GameId, string RulesetId, PlayerSummary Creator, int CreatorScore, string Status, DateTime AcceptDeadline);

public sealed record ChallengeAccepted(Guid ChallengeId, string Status, DateTime CompleteDeadline);

public sealed record ChallengeResponse(
    Guid ChallengeId, string Status, string GameId, string RulesetId, PlayerSummary Creator, PlayerSummary? Recipient,
    int CreatorScore, int? RecipientScore, string? Result, DateTime CreatedAt, DateTime AcceptDeadline, DateTime? AcceptedAt,
    DateTime? CompleteDeadline, DateTime? ResolvedAt, Guid? RematchOfChallengeId, string? ShareUrl);

/// <summary>Spec §2 J2, §5 Challenges, §6 Challenge; invariants 5, 8, 12.</summary>
public static class ChallengeEndpoints
{
    public static readonly TimeSpan AcceptWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan CompleteWindow = TimeSpan.FromHours(24);

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/challenges", CreateAsync);
        api.MapGet("/invites/{token}", PreviewAsync).AllowAnonymous();
        api.MapPost("/invites/{token}/accept", AcceptAsync);
        api.MapGet("/challenges/{challengeId:guid}", GetAsync);
        api.MapPost("/challenges/{challengeId:guid}/rematch", RematchAsync);
        api.MapGet("/me/challenges", ListAsync);
    }

    private static ApiProblemException Unavailable() => Problem.Of(404, "challenge_unavailable");

    private static async Task<IResult> CreateAsync(
        HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency, ChallengeInvites invites,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        var request = RequestBody.Deserialize<ChallengeCreateRequest>(body);
        var sessionId = request.CreatorSessionId
            ?? throw Problem.ValidationFailed("/creatorSessionId", "required", "creatorSessionId is required");
        if (request.RecipientHandle is { } handle && !IsHandleInput(handle))
        {
            throw Problem.ValidationFailed("/recipientHandle", "invalid", "recipientHandle is not a valid handle");
        }

        var hash = Idempotency.Hash("challenges.create", string.Empty, body);
        return await idempotency.RunAsync(context, playerId.ToString(), "challenges.create", hash, async ct =>
        {
            current.EnsureNotSuspended(await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct));
            Guid? recipientId = null;
            if (request.RecipientHandle is { } input)
            {
                var normalized = input.ToLowerInvariant();
                recipientId = await db.Players.AsNoTracking()
                    .Where(p => p.HandleNormalized == normalized && p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted)
                    .Select(p => (Guid?)p.Id)
                    .SingleOrDefaultAsync(ct)
                    ?? throw Problem.Of(404, "not_found");
                if (recipientId == playerId)
                {
                    throw Problem.Of(422, "self_challenge");
                }
            }

            return await InsertAsync(db, invites, clock, playerId, sessionId, recipientId, null, ct);
        }, cancellationToken, invites.AddShareUrl);
    }

    private static async Task<IResult> RematchAsync(
        Guid challengeId, HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency,
        ChallengeInvites invites, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        var request = RequestBody.Deserialize<RematchRequest>(body);
        var sessionId = request.CreatorSessionId
            ?? throw Problem.ValidationFailed("/creatorSessionId", "required", "creatorSessionId is required");

        var hash = Idempotency.Hash("challenges.rematch", challengeId.ToString(), body);
        return await idempotency.RunAsync(context, playerId.ToString(), "challenges.rematch", hash, async ct =>
        {
            current.EnsureNotSuspended(await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, ct));
            var original = await db.Challenges.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == challengeId
                    && (c.CreatorPlayerId == playerId || (c.RecipientPlayerId == playerId && !c.Suppressed)), ct)
                ?? throw Problem.Of(404, "not_found");
            if (original.Status != ChallengeStatus.Completed)
            {
                throw Problem.Of(409, "challenge_not_completed");
            }

            var other = original.CreatorPlayerId == playerId ? original.RecipientPlayerId!.Value : original.CreatorPlayerId;
            return await InsertAsync(db, invites, clock, playerId, sessionId, other, original.Id, ct);
        }, cancellationToken, invites.AddShareUrl);
    }

    /// <summary>
    /// Shared by createChallenge and createRematch. With a recipient: pair lock first, then the block
    /// re-check (invariant 12). The creator session row lock serializes concurrent use of one run.
    /// </summary>
    private static async Task<OperationResult> InsertAsync(
        VybeDbContext db, ChallengeInvites invites, TimeProvider clock, Guid playerId, Guid sessionId, Guid? recipientId,
        Guid? rematchOf, CancellationToken cancellationToken)
    {
        if (recipientId is { } locked)
        {
            await PairLock.AcquireAsync(db, playerId, locked, cancellationToken);
        }

        if (rematchOf is not null
            && await db.Challenges.AnyAsync(c => c.RematchOfChallengeId == rematchOf && c.CreatorPlayerId == playerId, cancellationToken))
        {
            throw Problem.Of(409, "rematch_already_created");
        }

        var session = await db.GameSessions
            .FromSql($"SELECT * FROM vybe.game_sessions WHERE id = {sessionId} AND player_id = {playerId} FOR NO KEY UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (session is not { Status: GameSessionStatus.Verified, ChallengeId: null })
        {
            throw Problem.Of(422, "session_not_eligible");
        }

        var config = RulesetConfig.Parse(
            await db.GameRulesets.Where(r => r.Id == session.RulesetId).Select(r => r.Config).SingleAsync(cancellationToken));
        // Same minimum-play rule as rewards (spec v1.4 §7): enough shots and a full-length run.
        if (Rewards.Ineligibility(config, session.EventCount ?? 0, session.StartedAt!.Value, session.SubmittedAt!.Value) is not null)
        {
            throw Problem.Of(422, "session_not_eligible");
        }

        if (await db.Challenges.AnyAsync(c => c.CreatorSessionId == sessionId, cancellationToken))
        {
            throw Problem.Of(409, "session_already_used");
        }

        var suppressed = false;
        if (recipientId is { } recipient)
        {
            var blockers = await ChallengeRuns.BlockersAsync(db, playerId, recipient, cancellationToken);
            if (blockers.Contains(playerId))
            {
                throw Problem.Of(422, "recipient_blocked");
            }

            // The recipient blocked the caller: store it suppressed, answer as usual (spec §5 block privacy).
            suppressed = blockers.Contains(recipient);
        }

        var now = clock.UtcNowForDb();
        var id = Guid.CreateVersion7();
        var challenge = new Challenge
        {
            Id = id,
            CreatorPlayerId = playerId,
            CreatorSessionId = sessionId,
            RecipientPlayerId = recipientId,
            InviteTokenHash = ChallengeInvites.Hash(invites.Token(id)),
            RulesetId = session.RulesetId,
            Status = ChallengeStatus.Open,
            CreatedAt = now,
            AcceptDeadline = now + AcceptWindow,
            RematchOfChallengeId = rematchOf,
            Suppressed = suppressed,
        };
        db.Challenges.Add(challenge);
        await db.SaveChangesAsync(cancellationToken);

        // Stored without shareUrl; the presenter adds it (spec §4 idempotency step 3 exception).
        return new OperationResult(201, new { challengeId = id, acceptDeadline = challenge.AcceptDeadline });
    }

    private static async Task<ChallengePreview> PreviewAsync(
        string token, HttpContext context, VybeDbContext db, CurrentPlayer current, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.CacheControl = "private, no-store";
        var callerId = await current.TryGetIdAsync(cancellationToken);
        if (!ChallengeInvites.IsWellFormed(token))
        {
            throw Unavailable();
        }

        var hash = ChallengeInvites.Hash(token);
        var row = await Rows(db).SingleOrDefaultAsync(r => r.C.InviteTokenHash == hash, cancellationToken) ?? throw Unavailable();
        var c = row.C;
        // Same rules as accept for an identified caller (the creator may preview their own challenge).
        if (callerId is { } me && me != c.CreatorPlayerId
            && (!MayRedeem(c, me) || (await ChallengeRuns.BlockersAsync(db, me, c.CreatorPlayerId, cancellationToken)).Count > 0))
        {
            throw Unavailable();
        }

        return new ChallengePreview(c.Id, row.GameId, c.RulesetId, Summary(row.Creator), row.CreatorScore,
            VisibleStatus(row, clock.UtcNowForDb()), c.AcceptDeadline);
    }

    private static async Task<IResult> AcceptAsync(
        string token, HttpContext context, VybeDbContext db, CurrentPlayer current, Idempotency idempotency, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var playerId = await current.GetIdAsync(cancellationToken);
        var body = await RequestBody.ReadAsync(context, cancellationToken);
        if (!ChallengeInvites.IsWellFormed(token))
        {
            throw Unavailable();
        }

        var tokenHash = ChallengeInvites.Hash(token);
        // The idempotency record never holds the token: its target is the token's SHA-256 (spec §11).
        var hash = Idempotency.Hash("challenges.accept", Convert.ToHexStringLower(tokenHash), body);
        return await idempotency.RunAsync(context, playerId.ToString(), "challenges.accept", hash, async ct =>
        {
            // Token, suppression and block checks come before the status checks (spec §5).
            var c = await db.Challenges.AsNoTracking().SingleOrDefaultAsync(x => x.InviteTokenHash == tokenHash, ct)
                ?? throw Unavailable();
            if (c.CreatorPlayerId == playerId)
            {
                throw Problem.Of(403, "self_accept");
            }

            if (!MayRedeem(c, playerId))
            {
                throw Unavailable();
            }

            await PairLock.AcquireAsync(db, playerId, c.CreatorPlayerId, ct);
            if ((await ChallengeRuns.BlockersAsync(db, playerId, c.CreatorPlayerId, ct)).Count > 0)
            {
                throw Unavailable();
            }

            // Compare-and-swap on the status (invariant 5): one accept wins, whatever the interleaving.
            var now = clock.UtcNowForDb();
            var completeDeadline = now + CompleteWindow;
            var accepted = await db.Challenges
                .Where(x => x.Id == c.Id && x.Status == ChallengeStatus.Open && x.AcceptDeadline > now
                    && (x.RecipientPlayerId == null || x.RecipientPlayerId == playerId))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.Status, ChallengeStatus.Accepted)
                    .SetProperty(x => x.RecipientPlayerId, playerId)
                    .SetProperty(x => x.AcceptedAt, now)
                    .SetProperty(x => x.CompleteDeadline, completeDeadline), ct);
            if (accepted == 0)
            {
                var status = await db.Challenges.Where(x => x.Id == c.Id).Select(x => x.Status).SingleAsync(ct);
                throw status is ChallengeStatus.Accepted or ChallengeStatus.Completed
                    ? Problem.Of(409, "challenge_already_accepted")
                    : Problem.Of(410, "challenge_expired");
            }

            return new OperationResult(200, new ChallengeAccepted(c.Id, "accepted", completeDeadline));
        }, cancellationToken);
    }

    private static async Task<ChallengeResponse> GetAsync(
        Guid challengeId, VybeDbContext db, CurrentPlayer current, ChallengeInvites invites, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var row = await Rows(db)
            .SingleOrDefaultAsync(r => r.C.Id == challengeId
                && (r.C.CreatorPlayerId == playerId || (r.C.RecipientPlayerId == playerId && !r.C.Suppressed)), cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        return ToResponse(row, playerId, invites, clock.UtcNowForDb());
    }

    private static async Task<Page<ChallengeResponse>> ListAsync(
        string? role, string? status, string? cursor, int? limit, VybeDbContext db, CurrentPlayer current, ChallengeInvites invites,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var take = Cursor.Limit(limit, 20);
        var now = clock.UtcNowForDb();

        var query = role switch
        {
            null => Rows(db).Where(r => r.C.CreatorPlayerId == playerId || (r.C.RecipientPlayerId == playerId && !r.C.Suppressed)),
            "created" => Rows(db).Where(r => r.C.CreatorPlayerId == playerId),
            "received" => Rows(db).Where(r => r.C.RecipientPlayerId == playerId && !r.C.Suppressed),
            _ => throw Problem.ValidationFailed("/role", "invalid", "role must be created or received"),
        };

        // Filters use the visible status (spec §5, §6): cancelled and stale rows count as expired.
        query = status switch
        {
            null => query,
            "open" => query.Where(r => r.C.Status == ChallengeStatus.Open && r.C.AcceptDeadline > now),
            "accepted" => query.Where(r => r.C.Status == ChallengeStatus.Accepted && !(r.C.CompleteDeadline <= now
                && (r.RecipientStatus == null || r.RecipientStatus == GameSessionStatus.Created
                    || r.RecipientStatus == GameSessionStatus.Rejected || r.RecipientStatus == GameSessionStatus.Expired))),
            "completed" => query.Where(r => r.C.Status == ChallengeStatus.Completed),
            "expired" => query.Where(r => r.C.Status == ChallengeStatus.Expired || r.C.Status == ChallengeStatus.Cancelled
                || (r.C.Status == ChallengeStatus.Open && r.C.AcceptDeadline <= now)
                || (r.C.Status == ChallengeStatus.Accepted && r.C.CompleteDeadline <= now
                    && (r.RecipientStatus == null || r.RecipientStatus == GameSessionStatus.Created
                        || r.RecipientStatus == GameSessionStatus.Rejected || r.RecipientStatus == GameSessionStatus.Expired))),
            _ => throw Problem.ValidationFailed("/status", "invalid", "unknown challenge status"),
        };

        if (Cursor.Decode(cursor) is { } decoded)
        {
            var (createdAt, id) = ParseCursor(decoded);
            query = query.Where(r => r.C.CreatedAt < createdAt || (r.C.CreatedAt == createdAt && r.C.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(r => r.C.CreatedAt).ThenByDescending(r => r.C.Id)
            .Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(r => ToResponse(r, playerId, invites, now)).ToList();
        var next = rows.Count > take
            ? Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{rows[take - 1].C.CreatedAt.Ticks}:{rows[take - 1].C.Id}"))
            : null;
        return new Page<ChallengeResponse>(page, next);
    }

    private static (DateTime CreatedAt, Guid Id) ParseCursor(string decoded)
    {
        var parts = decoded.Split(':');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTime.MaxValue.Ticks && Guid.TryParse(parts[1], out var id)
            ? (new DateTime(ticks, DateTimeKind.Utc), id)
            : throw Problem.ValidationFailed("/cursor", "invalid_cursor", "cursor is not valid");
    }

    /// <summary>Who may redeem the token: not suppressed, and the preset recipient (if any) while open.</summary>
    private static bool MayRedeem(Challenge c, Guid playerId) =>
        !c.Suppressed && !(c.Status == ChallengeStatus.Open && c.RecipientPlayerId is { } preset && preset != playerId);

    private static bool IsHandleInput(string handle) =>
        handle.Length is >= 3 and <= 20 && handle.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static PlayerSummary Summary(Player p) => new(p.Handle, p.DisplayName, p.Level);

    /// <summary>
    /// Spec §5/§6 visible status: <c>cancelled</c> shows as <c>expired</c>; reads apply the sweeper rules,
    /// so a challenge past its deadline shows <c>expired</c> before the sweeper runs.
    /// </summary>
    private static string VisibleStatus(Row row, DateTime now) => row.C.Status switch
    {
        ChallengeStatus.Open => now < row.C.AcceptDeadline ? "open" : "expired",
        ChallengeStatus.Accepted => now >= row.C.CompleteDeadline
            && row.RecipientStatus is null or GameSessionStatus.Created or GameSessionStatus.Rejected or GameSessionStatus.Expired
                ? "expired"
                : "accepted",
        ChallengeStatus.Completed => "completed",
        _ => "expired",
    };

    private static ChallengeResponse ToResponse(Row row, Guid playerId, ChallengeInvites invites, DateTime now)
    {
        var c = row.C;
        var visible = VisibleStatus(row, now);
        var result = c.Status != ChallengeStatus.Completed ? null
            : c.WinnerPlayerId is null ? "tie"
            : c.WinnerPlayerId == c.CreatorPlayerId ? "creator_win"
            : "recipient_win";
        // Only the designated recipient of an open challenge gets the link (rematch or profile challenge).
        var shareUrl = c.RecipientPlayerId == playerId && visible == "open" ? invites.ShareUrl(c.Id) : null;
        return new ChallengeResponse(c.Id, visible, row.GameId, c.RulesetId, Summary(row.Creator),
            row.Recipient is null ? null : Summary(row.Recipient), row.CreatorScore,
            row.RecipientStatus == GameSessionStatus.Verified ? row.RecipientScore : null, result, c.CreatedAt, c.AcceptDeadline,
            c.AcceptedAt, c.CompleteDeadline, c.ResolvedAt, c.RematchOfChallengeId, shareUrl);
    }

    private static IQueryable<Row> Rows(VybeDbContext db) =>
        from c in db.Challenges.AsNoTracking()
        join creator in db.Players on c.CreatorPlayerId equals creator.Id
        join creatorSession in db.GameSessions on c.CreatorSessionId equals creatorSession.Id
        join recipient in db.Players on c.RecipientPlayerId equals (Guid?)recipient.Id into recipients
        from recipient in recipients.DefaultIfEmpty()
        join recipientSession in db.GameSessions on c.RecipientSessionId equals (Guid?)recipientSession.Id into recipientSessions
        from recipientSession in recipientSessions.DefaultIfEmpty()
        select new Row
        {
            C = c,
            GameId = creatorSession.GameId,
            Creator = creator,
            Recipient = recipient,
            CreatorScore = creatorSession.Score ?? 0,
            RecipientStatus = recipientSession == null ? null : (GameSessionStatus?)recipientSession.Status,
            RecipientScore = recipientSession == null ? null : recipientSession.Score,
        };

    private sealed class Row
    {
        public required Challenge C { get; init; }

        public required string GameId { get; init; }

        public required Player Creator { get; init; }

        public Player? Recipient { get; init; }

        public int CreatorScore { get; init; }

        public GameSessionStatus? RecipientStatus { get; init; }

        public int? RecipientScore { get; init; }
    }
}
