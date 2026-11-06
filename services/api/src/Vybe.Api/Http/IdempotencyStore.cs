using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Http;

internal abstract record IdempotencyStart
{
    /// <summary>The caller holds the lease and must run the operation.</summary>
    public sealed record Acquired(DateTime Lease) : IdempotencyStart;

    public sealed record Replay(int Status, string Body) : IdempotencyStart;

    public sealed record Mismatch : IdempotencyStart;

    public sealed record InProgress : IdempotencyStart;
}

/// <summary>
/// The <c>idempotency_records</c> protocol of spec §4: a short insert with a 30-second lease, takeover of
/// an elapsed lease, and completion as a compare-and-swap on the caller's own lease inside the
/// operation's transaction. A 5xx or a crash stores nothing; the lease elapses and a retry takes over.
/// </summary>
internal sealed class IdempotencyStore(VybeDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RecordLifetime = TimeSpan.FromHours(72);

    /// <summary>Step 1 and 2. Must run outside the operation's transaction, so the lease is visible to other requests.</summary>
    public async Task<IdempotencyStart> StartAsync(
        string actor,
        string operation,
        Guid key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNowMicros();
        var lease = now + LeaseDuration;
        var expiresAt = now + RecordLifetime;
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO vybe.idempotency_records
                 (actor_subject, operation, key, request_hash, status, locked_until, created_at, expires_at)
             VALUES ({actor}, {operation}, {key}, {requestHash}, 'in_progress', {lease}, {now}, {expiresAt})
             ON CONFLICT DO NOTHING
             """,
            cancellationToken);
        if (inserted == 1)
        {
            return new IdempotencyStart.Acquired(lease);
        }

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ActorSubject == actor && r.Operation == operation && r.Key == key, cancellationToken);
        if (existing is null)
        {
            return new IdempotencyStart.InProgress();
        }

        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyStart.Mismatch();
        }

        if (existing.Status == IdempotencyStatus.Completed)
        {
            return new IdempotencyStart.Replay(existing.ResponseStatus!.Value, existing.ResponseBody!);
        }

        if (existing.LockedUntil > now)
        {
            return new IdempotencyStart.InProgress();
        }

        var takenOver = await db.IdempotencyRecords
            .Where(r => r.ActorSubject == actor && r.Operation == operation && r.Key == key
                        && r.Status == IdempotencyStatus.InProgress && r.LockedUntil < now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LockedUntil, lease), cancellationToken);
        return takenOver == 1 ? new IdempotencyStart.Acquired(lease) : new IdempotencyStart.InProgress();
    }

    /// <summary>
    /// Step 3. Call inside the operation's transaction. Returns false when another request took over the
    /// lease; the caller must then roll back.
    /// </summary>
    public async Task<bool> CompleteAsync(
        string actor,
        string operation,
        Guid key,
        DateTime lease,
        int responseStatus,
        string responseBody,
        CancellationToken cancellationToken)
    {
        var updated = await db.IdempotencyRecords
            .Where(r => r.ActorSubject == actor && r.Operation == operation && r.Key == key
                        && r.Status == IdempotencyStatus.InProgress && r.LockedUntil == lease)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, IdempotencyStatus.Completed)
                    .SetProperty(r => r.LockedUntil, (DateTime?)null)
                    .SetProperty(r => r.ResponseStatus, responseStatus)
                    .SetProperty(r => r.ResponseBody, responseBody),
                cancellationToken);
        return updated == 1;
    }
}
