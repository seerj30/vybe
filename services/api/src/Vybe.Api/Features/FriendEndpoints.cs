using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Infrastructure;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Features;

public sealed record FriendRequestCreate(string? Handle);

public sealed record FriendRequestResult(string Handle, FriendshipStatus Status);

public sealed record FriendEntry(string Handle, string? DisplayName, int Level, DateTime Since, bool ProfileVisible);

public sealed record FriendList(IReadOnlyList<FriendEntry> Items);

public sealed record FriendRequestEntry(string Handle, string? DisplayName, int? Level, DateTime RequestedAt);

public sealed record FriendRequestLists(IReadOnlyList<FriendRequestEntry> Incoming, IReadOnlyList<FriendRequestEntry> Outgoing);

/// <summary>The caller's relation to one handle, from the caller's view.</summary>
public enum FriendRelation { None, Friends, Outgoing, Incoming }

public sealed record FriendshipState(FriendRelation Relation);

/// <summary>
/// Spec §16 P1 full friends (docs/design/p1-friends-leaderboard.md). Every write to a pair's row takes the pair lock
/// (invariant 12) and re-checks <c>player_blocks</c> inside it, so a block and a request or accept cannot interleave.
/// </summary>
public static class FriendEndpoints
{
    /// <summary>Provisional abuse limits (design doc).</summary>
    public const int RequestsPerDay = 20;
    public const int MaxOutgoingPending = 50;
    public const int MaxFriends = 200;
    public const int MaxIncomingListed = 100;

    /// <summary>Open visible requests one player can receive; more would push real requests out of the list.</summary>
    public const int MaxIncomingPending = MaxIncomingListed;
    public static readonly TimeSpan DeclineCooldown = TimeSpan.FromDays(30);

    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/me/friends", ListFriendsAsync);
        api.MapDelete("/me/friends/{handle}", RemoveAsync);
        api.MapGet("/me/friend-requests", ListRequestsAsync);
        api.MapPost("/me/friend-requests", RequestAsync);
        api.MapPost("/me/friend-requests/{handle}/accept", AcceptAsync);
        api.MapPost("/me/friend-requests/{handle}/decline", DeclineAsync);
        api.MapDelete("/me/friend-requests/{handle}", CancelAsync);
        api.MapGet("/me/friendships/{handle}", RelationAsync);
    }

    /// <summary>The row of the pair, in any state and direction (the canonical pair is unique).</summary>
    private static IQueryable<Friendship> Pair(VybeDbContext db, Guid a, Guid b) =>
        db.Friendships.Where(f => (f.RequesterPlayerId == a && f.AddresseePlayerId == b)
            || (f.RequesterPlayerId == b && f.AddresseePlayerId == a));

    /// <summary>Accepted friends of a player (ids only).</summary>
    public static IQueryable<Guid> FriendIds(VybeDbContext db, Guid playerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterPlayerId == playerId || f.AddresseePlayerId == playerId))
            .Select(f => f.RequesterPlayerId == playerId ? f.AddresseePlayerId : f.RequesterPlayerId);
    }

    /// <summary>
    /// Sends a request. A visible reverse request makes the pair friends at once. The caller cannot tell a block by
    /// the target: the request is stored suppressed and answered as <c>pending</c> (spec §2 block privacy).
    /// </summary>
    private static async Task<IResult> RequestAsync(
        FriendRequestCreate request, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var playerId = await current.GetIdAsync(cancellationToken);
        if (!PlayerHandles.IsInput(request.Handle))
        {
            throw Problem.ValidationFailed("/handle", "invalid", "handle is not a valid handle");
        }

        current.EnsureNotSuspended(await db.Players.AsNoTracking().SingleAsync(p => p.Id == playerId, cancellationToken));
        var target = await PlayerHandles.FindAsync(db, request.Handle, cancellationToken) ?? throw Problem.Of(404, "not_found");
        if (target.Id == playerId)
        {
            throw Problem.Of(422, "self_friend_request");
        }

        var now = clock.UtcNowForDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PairLock.AcquireAsync(db, playerId, target.Id, cancellationToken);
        await EnsureStillListedAsync(db, target.Id, cancellationToken);
        var blockers = await ChallengeRuns.BlockersAsync(db, playerId, target.Id, cancellationToken);
        if (blockers.Contains(playerId))
        {
            throw Problem.Of(422, "recipient_blocked"); // the caller blocked the target and knows it
        }

        var row = await Pair(db, playerId, target.Id).SingleOrDefaultAsync(cancellationToken);
        if (row is { Status: FriendshipStatus.Accepted })
        {
            throw Problem.Of(409, "already_friends");
        }

        if (row is { Status: FriendshipStatus.Pending, Suppressed: false } && row.RequesterPlayerId == target.Id && blockers.Count == 0)
        {
            // The target asked first (also when the target is private: the target chose this pair).
            await EnsureBelowFriendLimitAsync(db, playerId, target.Id, cancellationToken);
            Accept(row, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return TypedResults.Ok(new FriendRequestResult(target.Handle, FriendshipStatus.Accepted));
        }

        if (row is { Status: FriendshipStatus.Pending } && row.RequesterPlayerId == playerId)
        {
            return TypedResults.Ok(new FriendRequestResult(target.Handle, FriendshipStatus.Pending)); // idempotent repeat
        }

        // A private profile receives no new requests: the 404 of an unknown handle (spec §17 decision 6).
        if (target.ProfileVisibility == ProfileVisibility.Private)
        {
            throw Problem.Of(404, "not_found");
        }

        if (row is { Status: FriendshipStatus.Declined, RespondedAt: { } declinedAt } && row.RequesterPlayerId == playerId
            && declinedAt > now - DeclineCooldown)
        {
            throw Problem.Of(409, "friend_request_declined");
        }

        // The counter row lock serializes this player's concurrent requests, so the pending count below is exact.
        var day = DateOnly.FromDateTime(now);
        var sent = (await db.Database.SqlQuery<int>($"""
            INSERT INTO vybe.friend_request_counters (player_id, day, sent) VALUES ({playerId}, {day}, 1)
            ON CONFLICT (player_id, day) DO UPDATE SET sent = friend_request_counters.sent + 1
            RETURNING sent AS "Value"
            """).ToListAsync(cancellationToken)).Single();
        if (sent > RequestsPerDay)
        {
            throw Problem.Of(429, "rate_limited"); // the rollback also undoes the counter increment
        }

        if (await db.Friendships.CountAsync(
                f => f.RequesterPlayerId == playerId && f.Status == FriendshipStatus.Pending, cancellationToken) >= MaxOutgoingPending)
        {
            throw Problem.Of(409, "friend_request_limit");
        }

        // Checked for suppressed requests too, so the answer never depends on a block.
        var suppressed = blockers.Contains(target.Id);
        if (await db.Friendships.CountAsync(
                f => f.AddresseePlayerId == target.Id && f.Status == FriendshipStatus.Pending && !f.Suppressed, cancellationToken)
            >= MaxIncomingPending)
        {
            throw Problem.Of(409, "recipient_request_limit");
        }

        if (row is null)
        {
            row = new Friendship { Id = Guid.CreateVersion7() };
            db.Friendships.Add(row);
        }

        row.RequesterPlayerId = playerId;
        row.AddresseePlayerId = target.Id;
        row.Status = FriendshipStatus.Pending;
        row.Suppressed = suppressed;
        row.CreatedAt = now;
        row.RespondedAt = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        // Older counter rows are no longer needed for the limit.
        await db.FriendRequestCounters.Where(c => c.PlayerId == playerId && c.Day < day).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok(new FriendRequestResult(target.Handle, FriendshipStatus.Pending));
    }

    /// <summary>Only the addressee of a visible pending request; anything else is the 404 of a missing request.</summary>
    private static async Task<FriendRequestResult> AcceptAsync(
        string handle, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var requester = await PlayerHandles.FindAsync(db, handle, cancellationToken) ?? throw Problem.Of(404, "not_found");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PairLock.AcquireAsync(db, playerId, requester.Id, cancellationToken);
        await EnsureStillListedAsync(db, requester.Id, cancellationToken);
        var row = await db.Friendships.SingleOrDefaultAsync(
                f => f.RequesterPlayerId == requester.Id && f.AddresseePlayerId == playerId
                    && f.Status == FriendshipStatus.Pending && !f.Suppressed,
                cancellationToken)
            ?? throw Problem.Of(404, "not_found");
        if ((await ChallengeRuns.BlockersAsync(db, playerId, requester.Id, cancellationToken)).Count > 0)
        {
            throw Problem.Of(404, "not_found");
        }

        await EnsureBelowFriendLimitAsync(db, playerId, requester.Id, cancellationToken);
        Accept(row, clock.UtcNowForDb());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FriendRequestResult(requester.Handle, FriendshipStatus.Accepted);
    }

    /// <summary>Idempotent. The row stays <c>declined</c>, so the requester waits <see cref="DeclineCooldown"/> to ask again.</summary>
    private static async Task<IResult> DeclineAsync(
        string handle, VybeDbContext db, CurrentPlayer current, TimeProvider clock, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        if (await PlayerHandles.FindAsync(db, handle, cancellationToken) is { } requester)
        {
            var now = clock.UtcNowForDb();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await PairLock.AcquireAsync(db, playerId, requester.Id, cancellationToken);
            await db.Friendships
                .Where(f => f.RequesterPlayerId == requester.Id && f.AddresseePlayerId == playerId
                    && f.Status == FriendshipStatus.Pending && !f.Suppressed)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(f => f.Status, FriendshipStatus.Declined)
                    .SetProperty(f => f.RespondedAt, now)
                    .SetProperty(f => f.UpdatedAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>Idempotent: deletes the caller's own pending request.</summary>
    private static Task<IResult> CancelAsync(string handle, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken) =>
        DeleteAsync(handle, db, current, (me, other) => f => f.RequesterPlayerId == me && f.AddresseePlayerId == other
            && f.Status == FriendshipStatus.Pending, cancellationToken);

    /// <summary>Idempotent: either friend ends the friendship.</summary>
    private static Task<IResult> RemoveAsync(string handle, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken) =>
        DeleteAsync(handle, db, current, (me, other) => f => f.Status == FriendshipStatus.Accepted
            && ((f.RequesterPlayerId == me && f.AddresseePlayerId == other) || (f.RequesterPlayerId == other && f.AddresseePlayerId == me)),
            cancellationToken);

    private static async Task<IResult> DeleteAsync(
        string handle, VybeDbContext db, CurrentPlayer current, Func<Guid, Guid, Expression<Func<Friendship, bool>>> match,
        CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        if (await PlayerHandles.FindAsync(db, handle, cancellationToken) is { } other)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await PairLock.AcquireAsync(db, playerId, other.Id, cancellationToken);
            await db.Friendships.Where(match(playerId, other.Id)).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// The caller's friends by handle. A private friend is listed (the friendship is a mutual choice) with
    /// <c>profileVisible = false</c>; nobody can list another player's friends (spec §17 decision 6).
    /// </summary>
    private static async Task<FriendList> ListFriendsAsync(VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var rows = await db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterPlayerId == playerId || f.AddresseePlayerId == playerId))
            .Join(Listable(db), f => f.RequesterPlayerId == playerId ? f.AddresseePlayerId : f.RequesterPlayerId, p => p.Id,
                (f, p) => new { p.Handle, p.DisplayName, p.Level, Since = f.RespondedAt!.Value, p.ProfileVisibility })
            .OrderBy(r => r.Handle)
            .Take(MaxFriends)
            .ToListAsync(cancellationToken);
        return new FriendList(rows
            .Select(r => new FriendEntry(r.Handle, r.DisplayName, r.Level, r.Since, r.ProfileVisibility == ProfileVisibility.Players))
            .ToList());
    }

    /// <summary>Incoming (never suppressed rows) and outgoing pending requests, newest first.</summary>
    private static async Task<FriendRequestLists> ListRequestsAsync(VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        var pending = db.Friendships.AsNoTracking().Where(f => f.Status == FriendshipStatus.Pending);
        var incoming = await pending.Where(f => f.AddresseePlayerId == playerId && !f.Suppressed)
            .Join(Listable(db), f => f.RequesterPlayerId, p => p.Id, (f, p) => new { p.Handle, p.DisplayName, p.Level, f.CreatedAt })
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Handle)
            .Take(MaxIncomingListed)
            .Select(r => new FriendRequestEntry(r.Handle, r.DisplayName, r.Level, r.CreatedAt))
            .ToListAsync(cancellationToken);
        // A player who turned private after the request shows only the handle the caller typed (spec §17 decision 6).
        var outgoing = await pending.Where(f => f.RequesterPlayerId == playerId)
            .Join(Listable(db), f => f.AddresseePlayerId, p => p.Id, (f, p) => new { p.Handle, p.DisplayName, p.Level, p.ProfileVisibility, f.CreatedAt })
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Handle)
            .Take(MaxOutgoingPending)
            .Select(r => r.ProfileVisibility == ProfileVisibility.Private
                ? new FriendRequestEntry(r.Handle, null, null, r.CreatedAt)
                : new FriendRequestEntry(r.Handle, r.DisplayName, r.Level, r.CreatedAt))
            .ToListAsync(cancellationToken);
        return new FriendRequestLists(incoming, outgoing);
    }

    /// <summary>The caller's relation to a handle. An unknown handle is <c>none</c>, so the answer is no existence check.</summary>
    private static async Task<FriendshipState> RelationAsync(
        string handle, VybeDbContext db, CurrentPlayer current, CancellationToken cancellationToken)
    {
        var playerId = await current.GetIdAsync(cancellationToken);
        if (await PlayerHandles.FindAsync(db, handle, cancellationToken) is not { } other || other.Id == playerId)
        {
            return new FriendshipState(FriendRelation.None);
        }

        var row = await Pair(db, playerId, other.Id).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return new FriendshipState(row switch
        {
            { Status: FriendshipStatus.Accepted } => FriendRelation.Friends,
            { Status: FriendshipStatus.Pending } when row.RequesterPlayerId == playerId => FriendRelation.Outgoing,
            { Status: FriendshipStatus.Pending, Suppressed: false } => FriendRelation.Incoming,
            _ => FriendRelation.None,
        });
    }

    /// <summary>
    /// Part of the block transaction (under the pair lock; extends invariant 8). The friendship and the blocker's own
    /// pending request end. The blocked player's pending request stays, suppressed, and a declined one stays declined, so the
    /// blocked player sees the same answers as without the block (spec §2 J2 block privacy).
    /// </summary>
    public static async Task ApplyBlockAsync(VybeDbContext db, Guid blockerId, Guid blockedId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await Pair(db, blockerId, blockedId)
            // Declined rows stay in both directions, so a block and unblock cannot skip the decline wait.
            .Where(f => f.Status == FriendshipStatus.Accepted || (f.RequesterPlayerId == blockerId && f.Status == FriendshipStatus.Pending))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Friendships
            .Where(f => f.RequesterPlayerId == blockedId && f.AddresseePlayerId == blockerId && f.Status == FriendshipStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.Suppressed, true), cancellationToken);
    }

    /// <summary>After an unblock, a suppressed request of the other player reaches the caller, unless a block remains.</summary>
    public static async Task ApplyUnblockAsync(VybeDbContext db, Guid playerId, string handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (await PlayerHandles.FindAsync(db, handle, cancellationToken) is not { } other || other.Id == playerId)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await PairLock.AcquireAsync(db, playerId, other.Id, cancellationToken);
        if ((await ChallengeRuns.BlockersAsync(db, playerId, other.Id, cancellationToken)).Count == 0)
        {
            await db.Friendships
                .Where(f => f.RequesterPlayerId == other.Id && f.AddresseePlayerId == playerId && f.Status == FriendshipStatus.Pending && f.Suppressed)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.Suppressed, false), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Inside the pair lock: the other player did not start a deletion meanwhile (J4 removes their rows).
    /// FOR SHARE waits for a running anonymization (FOR NO KEY UPDATE, PrivacyProcessor) and then reads its status;
    /// an anonymization that starts later waits for this transaction, so its delete sees the new friendship row.
    /// </summary>
    private static async Task EnsureStillListedAsync(VybeDbContext db, Guid otherId, CancellationToken cancellationToken)
    {
        var other = await db.Players.FromSql($"SELECT * FROM vybe.players WHERE id = {otherId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (other is null || other.Status is PlayerStatus.Deleting or PlayerStatus.Deleted)
        {
            throw Problem.Of(404, "not_found");
        }
    }

    private static IQueryable<Player> Listable(VybeDbContext db) =>
        db.Players.AsNoTracking().Where(p => p.Status != PlayerStatus.Deleting && p.Status != PlayerStatus.Deleted);

    private static void Accept(Friendship row, DateTime now)
    {
        row.Status = FriendshipStatus.Accepted;
        row.RespondedAt = now;
        row.UpdatedAt = now;
    }

    /// <summary>A soft cap: concurrent accepts of different pairs can pass it by a few.</summary>
    private static async Task EnsureBelowFriendLimitAsync(VybeDbContext db, Guid a, Guid b, CancellationToken cancellationToken)
    {
        foreach (var id in new[] { a, b })
        {
            if (await FriendIds(db, id).CountAsync(cancellationToken) >= MaxFriends)
            {
                throw Problem.Of(409, "friend_limit_reached");
            }
        }
    }
}
