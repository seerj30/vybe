using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>The cleanup deletes expired records and keeps live leases and unexpired records.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class OpsIdempotencyCleanupTests(ApiFactory api)
{
    [Fact]
    public async Task RunOnce_DeletesExpiredRecords_KeepsHeldLeasesAndUnexpiredRecords()
    {
        var actor = "ops-cleanup-" + Guid.NewGuid();
        await using var db = api.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.idempotency_records (actor_subject, operation, key, request_hash, status, locked_until, response_status, response_body, expires_at)
            VALUES
              ({actor}, 'expired-completed', gen_random_uuid(), 'h', 'completed', NULL, 200, jsonb_build_object(), now() - interval '1 minute'),
              ({actor}, 'expired-elapsed-lease', gen_random_uuid(), 'h', 'in_progress', now() - interval '1 minute', NULL, NULL, now() - interval '1 minute'),
              ({actor}, 'expired-held-lease', gen_random_uuid(), 'h', 'in_progress', now() + interval '30 seconds', NULL, NULL, now() - interval '1 minute'),
              ({actor}, 'live', gen_random_uuid(), 'h', 'completed', NULL, 200, jsonb_build_object(), now() + interval '72 hours')
            """);

        var deleted = await api.Services.GetRequiredService<IdempotencyCleanup>().RunOnceAsync(CancellationToken.None);
        var left = await db.IdempotencyRecords.AsNoTracking().Where(r => r.ActorSubject == actor).Select(r => r.Operation).OrderBy(o => o).ToListAsync();

        Assert.True(deleted >= 2);
        Assert.Equal(["expired-held-lease", "live"], left);
    }

    [Fact]
    public async Task ConcurrentRuns_DeleteMoreThanOneBatch_EachRowOnce()
    {
        var actor = "ops-cleanup-bulk-" + Guid.NewGuid();
        await using var db = api.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.idempotency_records (actor_subject, operation, key, request_hash, status, locked_until, response_status, response_body, expires_at)
            SELECT {actor}, 'bulk', gen_random_uuid(), 'h', 'completed', NULL, 200, jsonb_build_object(), now() - interval '1 minute'
            FROM generate_series(1, 2500)
            """);
        var cleanup = api.Services.GetRequiredService<IdempotencyCleanup>();

        var runs = await Task.WhenAll(cleanup.RunOnceAsync(CancellationToken.None), cleanup.RunOnceAsync(CancellationToken.None));

        Assert.True(runs.Sum() >= 2500);
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync(r => r.ActorSubject == actor));
    }
}
