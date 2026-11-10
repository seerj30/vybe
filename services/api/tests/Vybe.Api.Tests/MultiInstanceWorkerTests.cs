using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Api.Jobs;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>
/// Every background worker runs in every API process: two machines, or an old and a new process during a rolling
/// restart. So each worker must be correct when two processes run it at the same time. Each test builds a second,
/// independent instance of the worker (its own object, scopes and connections, like a second process), seeds 50 or
/// more rows, and releases both instances from one barrier, so they compete for the same rows. The idempotency
/// cleanup has its own test (<see cref="OpsIdempotencyCleanupTests"/>, <c>ConcurrentRuns_DeleteMoreThanOneBatch_EachRowOnce</c>).
/// Proof per worker: docs/runbooks/deploy-and-rollback.md, "Scaling before the social beta".
/// </summary>
public sealed class MultiInstanceWorkerTests(AccountPolicyApiFactory api) : IClassFixture<AccountPolicyApiFactory>
{
    private const int Rows = 60;

    [Fact]
    public async Task Outbox_TwoInstances_RunEveryJobOnce_AndPayEveryBonusOnce()
    {
        var challenges = new List<(Guid ChallengeId, Guid WinnerId)>();
        for (var i = 0; i < Rows / 2; i++)
        {
            var creator = await Player.CreateGuestAsync(api);
            var recipient = await Player.CreateGuestAsync(api);
            var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
            await ChallengeTests.AcceptAsync(recipient, token);
            await api.PlayRecipientRunAsync(recipient, challengeId, 0); // the creator wins
            challenges.Add((challengeId, creator.Id));
        }

        await using var db = api.CreateDbContext();
        var now = api.Clock.UtcNowForDb();
        var due = await db.OutboxJobs.CountAsync(j => j.Status == OutboxStatus.Pending && j.RunAfter <= now);
        Assert.True(due >= Rows / 2);

        var (first, second) = Twice<OutboxProcessor>();
        var ran = await RaceAsync(() => first.RunOnceAsync(1_000, CancellationToken.None), () => second.RunOnceAsync(1_000, CancellationToken.None));

        Assert.Equal(due, ran.Sum()); // every due job ran exactly once, by one of the instances
        Assert.All(ran, count => Assert.True(count > 0, "both instances took jobs (contention)"));
        var keys = challenges.Select(c => $"challenge-resolved:{c.ChallengeId}:{c.WinnerId}").ToList();
        var jobs = await db.OutboxJobs.AsNoTracking().Where(j => keys.Contains(j.JobKey)).ToListAsync();
        Assert.Equal(keys.Count, jobs.Count);
        Assert.All(jobs, j =>
        {
            Assert.Equal(OutboxStatus.Done, j.Status);
            Assert.Equal(1, j.Attempts);
        });
        foreach (var (challengeId, winnerId) in challenges)
        {
            Assert.Equal(1, await db.WalletTransactions.CountAsync(t => t.PlayerId == winnerId && t.LedgerKey == $"challenge:{challengeId}"));
        }
    }

    [Fact]
    public async Task Sweeper_TwoInstances_ExpireEverySessionOnce()
    {
        var sessions = new List<Guid>();
        for (var i = 0; i < Rows; i++)
        {
            sessions.Add((await (await Player.CreateGuestAsync(api)).StartRunAsync()).SessionId);
        }

        api.Clock.Advance(TimeSpan.FromMinutes(5)); // past every submit deadline (60 s run + grace)
        var now = api.Clock.UtcNowForDb();
        await using var db = api.CreateDbContext();
        var due = await db.GameSessions.CountAsync(s =>
            (s.Status == GameSessionStatus.Created && s.StartDeadline < now) || (s.Status == GameSessionStatus.Active && s.SubmitDeadline < now));

        var (first, second) = Twice<Sweeper>();
        var results = await RaceAsync(() => first.RunOnceAsync(CancellationToken.None), () => second.RunOnceAsync(CancellationToken.None));

        Assert.True(due >= sessions.Count);
        Assert.Equal(due, results.Sum(r => r.Sessions)); // every due session counted by exactly one instance
        Assert.All(
            await db.GameSessions.AsNoTracking().Where(s => sessions.Contains(s.Id)).Select(s => s.Status).ToListAsync(),
            status => Assert.Equal(GameSessionStatus.Expired, status));
        Assert.Equal(0, (await first.RunOnceAsync(CancellationToken.None)).Sessions);
    }

    [Fact]
    public async Task GuestCleanup_TwoInstances_QueueOneDeletionPerGuest()
    {
        var now = api.Clock.UtcNowForDb();
        var guests = new List<Guid>();
        await using var db = api.CreateDbContext();
        for (var i = 0; i < Rows; i++)
        {
            guests.Add(await InsertExpiredGuestAsync(db, now - GuestCleanup.Grace - TimeSpan.FromDays(1)));
        }

        var before = await db.DeletionRequests.CountAsync();
        var (first, second) = Twice<GuestCleanup>();
        var queued = await RaceAsync(() => first.RunOnceAsync(CancellationToken.None), () => second.RunOnceAsync(CancellationToken.None));

        Assert.Equal(await db.DeletionRequests.CountAsync() - before, queued.Sum());
        var perGuest = await db.DeletionRequests.Where(r => guests.Contains(r.PlayerId)).GroupBy(r => r.PlayerId)
            .Select(g => g.Count()).ToListAsync();
        Assert.Equal(guests.Count, perGuest.Count);
        Assert.All(perGuest, count => Assert.Equal(1, count));
        Assert.Equal(guests.Count, await db.Players.CountAsync(p => guests.Contains(p.Id) && p.Status == PlayerStatus.Deleting));
    }

    [Fact]
    public async Task Privacy_TwoInstances_AnonymizeEveryPlayerOnce_AndDeleteEveryAuthUserOnce()
    {
        var subjects = Enumerable.Range(0, Rows).Select(_ => Guid.NewGuid()).ToList();
        var players = new List<Guid>();
        foreach (var subject in subjects)
        {
            players.Add(await RegisteredAsync(subject));
            await RequestDeletionAsync(subject);
        }

        // Repeated raced passes: each pass of each instance takes up to 10 requests, so the rows are shared over
        // several rounds, and later rounds race over the Auth deletions that the earlier rounds queued. Other tests of
        // this class can queue more deletions, so the rounds go on until nothing is left.
        var (first, second) = Twice<PrivacyProcessor>();
        await using var db = api.CreateDbContext();
        var rounds = 0;
        do
        {
            await RaceAsync(() => first.RunOnceAsync(10, CancellationToken.None), () => second.RunOnceAsync(10, CancellationToken.None));
            rounds++;
        }
        while (rounds < 50 && (await db.DeletionRequests.AnyAsync(r => r.Status == DeletionStatus.Queued) || await db.AuthUserDeletions.AnyAsync()));

        Assert.True(rounds >= Rows / 20, "the work was shared over several raced rounds (10 per pass and instance)");
        Assert.Equal(players.Count, await db.Players.CountAsync(p => players.Contains(p.Id) && p.Status == PlayerStatus.Deleted));
        Assert.Equal(players.Count, await db.DeletionRequests.CountAsync(r => players.Contains(r.PlayerId) && r.Status == DeletionStatus.Completed));
        var deleted = api.Admin.Deleted.ToList();
        foreach (var subject in subjects)
        {
            Assert.Single(deleted, d => d == subject.ToString()); // one DELETE call per Auth user
        }

        Assert.Equal(0, await db.AuthUserDeletions.CountAsync(d => subjects.Select(s => s.ToString()).Contains(d.AuthSubject)));
    }

    [Fact]
    public async Task DeletionLog_TwoInstances_WriteEveryRequest_AndRewritesAreHarmless()
    {
        var now = api.Clock.UtcNowForDb();
        var guests = new List<Guid>();
        await using var db = api.CreateDbContext();
        for (var i = 0; i < Rows; i++)
        {
            guests.Add(await InsertExpiredGuestAsync(db, now - GuestCleanup.Grace - TimeSpan.FromDays(1)));
        }

        await api.Services.GetRequiredService<GuestCleanup>().RunOnceAsync(CancellationToken.None);
        var requests = await db.DeletionRequests.AsNoTracking().Where(r => guests.Contains(r.PlayerId)).ToListAsync();
        Assert.Equal(guests.Count, requests.Count);

        // The machines share one log (the bucket). An entry is keyed by the request id and never changes, so two
        // instances that write the same entry write the same object. Here the log is in memory, not the bucket.
        var log = new InMemoryDeletionLog();
        var scopes = api.Services.GetRequiredService<IServiceScopeFactory>();
        var first = new DeletionLogExporter(scopes, log, api.Clock);
        var second = new DeletionLogExporter(scopes, log, api.Clock);
        await RaceAsync(() => first.RunOnceAsync(CancellationToken.None), () => second.RunOnceAsync(CancellationToken.None));

        foreach (var request in requests)
        {
            Assert.Equal(new DeletionLogEntry(request.Id, request.PlayerId, request.CreatedAt), log.Entries[request.Id]);
        }

        Assert.Equal(0, await first.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>Two independent instances of a worker, as in two processes: separate objects, separate scopes.</summary>
    private (T First, T Second) Twice<T>()
        where T : class =>
        (ActivatorUtilities.CreateInstance<T>(api.Services), ActivatorUtilities.CreateInstance<T>(api.Services));

    /// <summary>Starts both passes on pool threads and releases them together from one barrier.</summary>
    private static async Task<T[]> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(async () =>
        {
            await barrier.Task;
            return await first();
        });
        var b = Task.Run(async () =>
        {
            await barrier.Task;
            return await second();
        });
        barrier.SetResult();
        return await Task.WhenAll(a, b);
    }

    private static Task<bool[]> RaceAsync(Func<Task> first, Func<Task> second) =>
        RaceAsync(async () =>
        {
            await first();
            return true;
        }, async () =>
        {
            await second();
            return true;
        });

    /// <summary>A guest written directly (no API) whose only session ended at <paramref name="sessionEnded"/>.</summary>
    private static async Task<Guid> InsertExpiredGuestAsync(VybeDbContext db, DateTime sessionEnded)
    {
        var id = Guid.CreateVersion7();
        var handle = "multi_" + Guid.NewGuid().ToString("N")[..12];
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vybe.players (id, handle, handle_normalized, kind, status) VALUES ({id}, {handle}, {handle}, 'guest', 'active');
            INSERT INTO vybe.avatar_configs (player_id, body_preset_id, skin_tone_id, face_preset_id, hair_preset_id, hair_color_id, equipped_json)
            SELECT {id}, body_preset_id, skin_tone_id, face_preset_id, hair_preset_id, hair_color_id, equipped_json FROM vybe.avatar_configs LIMIT 1;
            INSERT INTO vybe.guest_sessions (id, player_id, token_hash, expires_at) VALUES ({Guid.NewGuid()}, {id}, {RandomNumberGenerator.GetBytes(32)}, {sessionEnded});
            """);
        return id;
    }

    private async Task<Guid> RegisteredAsync(Guid subject)
    {
        var guest = await Player.CreateGuestAsync(api);
        await Contract.AssertAsync(
            await guest.Http.PostAsJsonAsync("/api/v1/accounts/link", new { linkProof = ApiFactory.RegisteredToken(subject.ToString()) }),
            HttpStatusCode.OK, "AccountLinked");
        return guest.Id;
    }

    private async Task RequestDeletionAsync(Guid subject)
    {
        using var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFactory.RegisteredToken(subject.ToString(), authenticatedAt: api.Clock.GetUtcNow()));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await Contract.AssertAsync(await client.SendAsync(request), HttpStatusCode.Accepted, "DeletionRequested");
    }
}
