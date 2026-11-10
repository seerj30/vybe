using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Features;
using Vybe.Api.Infrastructure;
using Vybe.Api.Jobs;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>
/// Spec §16 gate and decision 7: a second API machine before the social beta. Every background worker runs in
/// every API process, so each worker must be correct when two processes run it at the same time. Each test builds
/// a second, independent instance of the worker (its own object, its own scopes and connections, like a second
/// machine) and runs both instances in parallel against the same database. The idempotency cleanup has its own
/// test (<see cref="OpsIdempotencyCleanupTests"/>, <c>ConcurrentRuns_DeleteMoreThanOneBatch_EachRowOnce</c>).
/// Proof per worker: docs/runbooks/deploy-and-rollback.md, "Scaling before the social beta".
/// </summary>
public sealed class MultiInstanceWorkerTests(AccountPolicyApiFactory api) : IClassFixture<AccountPolicyApiFactory>
{
    [Fact]
    public async Task Outbox_TwoInstances_RunEveryJobOnce_AndPayEveryBonusOnce()
    {
        var challenges = new List<(Guid ChallengeId, Guid WinnerId)>();
        for (var i = 0; i < 4; i++)
        {
            var creator = await Player.CreateGuestAsync(api);
            var recipient = await Player.CreateGuestAsync(api);
            var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
            await ChallengeTests.AcceptAsync(recipient, token);
            await api.PlayRecipientRunAsync(recipient, challengeId, 0); // the creator wins
            challenges.Add((challengeId, creator.Id));
        }

        var (first, second) = Twice<OutboxProcessor>();
        var ran = await Task.WhenAll(first.RunOnceAsync(1_000, CancellationToken.None), second.RunOnceAsync(1_000, CancellationToken.None));

        await using var db = api.CreateDbContext();
        var keys = challenges.Select(c => $"challenge-resolved:{c.ChallengeId}:{c.WinnerId}").ToList();
        var jobs = await db.OutboxJobs.AsNoTracking().Where(j => keys.Contains(j.JobKey)).ToListAsync();
        Assert.Equal(keys.Count, jobs.Count);
        Assert.All(jobs, j =>
        {
            Assert.Equal(OutboxStatus.Done, j.Status);
            Assert.Equal(1, j.Attempts); // one run, by one instance
        });
        Assert.True(ran.Sum() >= keys.Count);
        foreach (var (challengeId, winnerId) in challenges)
        {
            Assert.Equal(1, await db.WalletTransactions.CountAsync(t => t.PlayerId == winnerId && t.LedgerKey == $"challenge:{challengeId}"));
        }
    }

    [Fact]
    public async Task Sweeper_TwoInstances_ExpireEverySessionOnce()
    {
        var sessions = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            sessions.Add((await (await Player.CreateGuestAsync(api)).StartRunAsync()).SessionId);
        }

        api.Clock.Advance(TimeSpan.FromMinutes(5)); // past every submit deadline (60 s run + grace)
        var now = api.Clock.UtcNowForDb();
        await using var db = api.CreateDbContext();
        var due = await db.GameSessions.CountAsync(s =>
            (s.Status == GameSessionStatus.Created && s.StartDeadline < now) || (s.Status == GameSessionStatus.Active && s.SubmitDeadline < now));

        var (first, second) = Twice<Sweeper>();
        var results = await Task.WhenAll(first.RunOnceAsync(CancellationToken.None), second.RunOnceAsync(CancellationToken.None));

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
        var guests = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            guests.Add((await Player.CreateGuestAsync(api)).Id);
        }

        api.Clock.Advance(TimeSpan.FromDays(61)); // the sessions ended 31 days ago
        await using var db = api.CreateDbContext();
        var before = await db.DeletionRequests.CountAsync();

        var (first, second) = Twice<GuestCleanup>();
        var queued = await Task.WhenAll(first.RunOnceAsync(CancellationToken.None), second.RunOnceAsync(CancellationToken.None));

        Assert.Equal(await db.DeletionRequests.CountAsync() - before, queued.Sum());
        foreach (var guest in guests)
        {
            Assert.Equal(1, await db.DeletionRequests.CountAsync(r => r.PlayerId == guest));
            Assert.Equal(PlayerStatus.Deleting, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == guest)).Status);
        }
    }

    [Fact]
    public async Task Privacy_TwoInstances_AnonymizeEveryPlayerOnce_AndDeleteEveryAuthUserOnce()
    {
        var subjects = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var players = new List<Guid>();
        foreach (var subject in subjects)
        {
            players.Add(await RegisteredAsync(subject));
            await RequestDeletionAsync(subject);
        }

        var (first, second) = Twice<PrivacyProcessor>();
        await Task.WhenAll(first.RunOnceAsync(100, CancellationToken.None), second.RunOnceAsync(100, CancellationToken.None));

        await using var db = api.CreateDbContext();
        foreach (var player in players)
        {
            Assert.Equal(PlayerStatus.Deleted, (await db.Players.AsNoTracking().SingleAsync(p => p.Id == player)).Status);
            Assert.Equal(DeletionStatus.Completed, (await db.DeletionRequests.AsNoTracking().SingleAsync(r => r.PlayerId == player)).Status);
        }

        foreach (var subject in subjects)
        {
            Assert.Single(api.Admin.Deleted, d => d == subject.ToString()); // one DELETE call per Auth user
        }

        Assert.Equal(0, await db.AuthUserDeletions.CountAsync(d => subjects.Select(s => s.ToString()).Contains(d.AuthSubject)));
    }

    [Fact]
    public async Task DeletionLog_TwoInstances_WriteEveryRequest_AndRewritesAreHarmless()
    {
        var guests = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            guests.Add((await Player.CreateGuestAsync(api)).Id);
        }

        api.Clock.Advance(TimeSpan.FromDays(61));
        await api.Services.GetRequiredService<GuestCleanup>().RunOnceAsync(CancellationToken.None);
        await using var db = api.CreateDbContext();
        var requests = await db.DeletionRequests.AsNoTracking().Where(r => guests.Contains(r.PlayerId)).ToListAsync();
        Assert.Equal(guests.Count, requests.Count);

        // The two machines share one log (the bucket). An entry is keyed by the request id and never changes, so
        // two instances that write the same entry write the same object.
        var log = new InMemoryDeletionLog();
        var scopes = api.Services.GetRequiredService<IServiceScopeFactory>();
        var first = new DeletionLogExporter(scopes, log, api.Clock);
        var second = new DeletionLogExporter(scopes, log, api.Clock);
        await Task.WhenAll(first.RunOnceAsync(CancellationToken.None), second.RunOnceAsync(CancellationToken.None));

        foreach (var request in requests)
        {
            Assert.Equal(new DeletionLogEntry(request.Id, request.PlayerId, request.CreatedAt), log.Entries[request.Id]);
        }

        Assert.Equal(0, await first.RunOnceAsync(CancellationToken.None));
    }

    /// <summary>Two independent instances of a worker, as on two machines: separate objects, separate scopes.</summary>
    private (T First, T Second) Twice<T>()
        where T : class =>
        (ActivatorUtilities.CreateInstance<T>(api.Services), ActivatorUtilities.CreateInstance<T>(api.Services));

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
