using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Infrastructure;

/// <summary>
/// Spec §4 invariant 12: block, challenge creation with a recipient, token accept, rematch and
/// recipient-session creation first take a transaction-scoped advisory lock on the player pair,
/// then re-check <c>player_blocks</c> inside the lock.
/// </summary>
public static class PairLock
{
    /// <summary>
    /// Takes the pair lock for the current transaction. The order of <paramref name="a"/> and
    /// <paramref name="b"/> does not matter: PostgreSQL orders the ids, exactly as the spec's SQL.
    /// Must run inside a transaction; the lock is released at commit or rollback.
    /// </summary>
    public static async Task AcquireAsync(VybeDbContext db, Guid a, Guid b, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("the pair lock is transaction-scoped; begin a transaction first");
        }

        await db.Database.ExecuteSqlAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended(
                LEAST({a}::uuid, {b}::uuid)::text || ':' || GREATEST({a}::uuid, {b}::uuid)::text, 0))
            """, cancellationToken);
    }
}
