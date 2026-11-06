using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Infrastructure;
using Vybe.Api.Jobs;
using Vybe.Data;
using Vybe.Data.Entities;

namespace Vybe.Api.Tests;

/// <summary>Outbox retries, the failed state, and suspended or deleted winners (ADR 0004 item 6).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class OutboxTests(ApiFactory api)
{
    [Fact]
    public async Task PoisonJob_RetriesWithBackoff_ThenFails()
    {
        var key = $"poison:{Guid.NewGuid()}";
        var now = api.Clock.UtcNowForDb();
        await using (var db = api.CreateDbContext())
        {
            db.OutboxJobs.Add(new OutboxJob
            {
                Id = Guid.CreateVersion7(),
                Kind = "no-such-kind",
                JobKey = key,
                Payload = "{}",
                Status = OutboxStatus.Pending,
                RunAfter = now,
            });
            await db.SaveChangesAsync();
        }

        await RunOutboxAsync();
        var job = await JobAsync(key);
        Assert.Equal(OutboxStatus.Pending, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(now.AddSeconds(2), job.RunAfter); // 2^attempts seconds
        Assert.StartsWith("InvalidOperationException: unknown outbox job kind", job.LastError, StringComparison.Ordinal);

        await RunOutboxAsync(); // not due yet
        Assert.Equal(1, (await JobAsync(key)).Attempts);

        await using (var db = api.CreateDbContext())
        {
            await db.OutboxJobs.Where(j => j.JobKey == key).ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Attempts, OutboxProcessor.MaxAttempts - 1)
                .SetProperty(j => j.RunAfter, now));
        }

        await RunOutboxAsync();
        job = await JobAsync(key);
        Assert.Equal(OutboxStatus.Failed, job.Status);
        Assert.Equal(OutboxProcessor.MaxAttempts, job.Attempts);
        Assert.NotNull(job.LastError);

        await RunOutboxAsync(); // a failed job never runs again
        Assert.Equal(OutboxProcessor.MaxAttempts, (await JobAsync(key)).Attempts);
    }

    [Fact]
    public async Task SuspendedWinner_GetsTheBonusAndQuestProgress_WhenTheSuspensionEnds()
    {
        QuestTests.StartEarlyInADay(api);
        var (challengeId, winner) = await ChallengeWonByCreatorAsync();
        var until = api.Clock.UtcNowForDb().AddHours(1);
        await SetStatusAsync(winner.Id, PlayerStatus.Suspended, until);

        await RunOutboxAsync();
        Assert.Equal(0, await LedgerCountAsync(winner.Id, challengeId));
        var job = await JobAsync(ChallengeResolvedKey(challengeId, winner.Id));
        Assert.Equal(OutboxStatus.Pending, job.Status);
        Assert.Equal(until, job.RunAfter);
        Assert.Equal(0, job.Attempts);
        Assert.Equal("active", await ChallengeQuestStatusAsync(winner.Id, challengeId));

        api.Clock.Advance(TimeSpan.FromHours(1));
        await RunOutboxAsync();
        Assert.Equal(1, await LedgerCountAsync(winner.Id, challengeId));
        Assert.Equal(OutboxStatus.Done, (await JobAsync(ChallengeResolvedKey(challengeId, winner.Id))).Status);
        Assert.Equal("completed", await ChallengeQuestStatusAsync(winner.Id, challengeId));
    }

    [Fact]
    public async Task IndefinitelySuspendedWinner_StaysPending_DeletingWinner_GetsNothing()
    {
        var (indefinite, suspended) = await ChallengeWonByCreatorAsync();
        await SetStatusAsync(suspended.Id, PlayerStatus.Suspended, null);
        var (deleted, deleting) = await ChallengeWonByCreatorAsync();
        await SetStatusAsync(deleting.Id, PlayerStatus.Deleting, null);

        await RunOutboxAsync();

        var waiting = await JobAsync(ChallengeResolvedKey(indefinite, suspended.Id));
        Assert.Equal(OutboxStatus.Pending, waiting.Status);
        Assert.Equal(api.Clock.UtcNowForDb() + ChallengeResolvedJob.IndefiniteSuspensionRetry, waiting.RunAfter);
        Assert.Equal(0, await LedgerCountAsync(suspended.Id, indefinite));

        Assert.Equal(OutboxStatus.Done, (await JobAsync(ChallengeResolvedKey(deleted, deleting.Id))).Status);
        Assert.Equal(0, await LedgerCountAsync(deleting.Id, deleted));
    }

    private static string ChallengeResolvedKey(Guid challengeId, Guid playerId) => $"challenge-resolved:{challengeId}:{playerId}";

    /// <summary>The creator wins: a short-streak run (no streak quest progress) against an empty recipient run.</summary>
    private async Task<(Guid ChallengeId, Player Creator)> ChallengeWonByCreatorAsync()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, await api.PlayShortStreakRunAsync(creator));
        await ChallengeTests.AcceptAsync(recipient, token);
        await api.PlayRecipientRunAsync(recipient, challengeId, 0);
        return (challengeId, creator);
    }

    private async Task SetStatusAsync(Guid playerId, PlayerStatus status, DateTime? until)
    {
        await using var db = api.CreateDbContext();
        await db.Players.Where(p => p.Id == playerId).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Status, status)
            .SetProperty(p => p.SuspendedUntil, until)
            .SetProperty(p => p.SuspensionReason, status == PlayerStatus.Suspended ? "test" : null));
    }

    private async Task RunOutboxAsync() =>
        await api.Services.GetRequiredService<OutboxProcessor>().RunOnceAsync(1_000, CancellationToken.None);

    private async Task<OutboxJob> JobAsync(string key)
    {
        await using var db = api.CreateDbContext();
        return await db.OutboxJobs.AsNoTracking().SingleAsync(j => j.JobKey == key);
    }

    private async Task<int> LedgerCountAsync(Guid playerId, Guid challengeId)
    {
        await using var db = api.CreateDbContext();
        return await db.WalletTransactions.CountAsync(t => t.PlayerId == playerId && t.LedgerKey == $"challenge:{challengeId}");
    }

    /// <summary>The player's challenge_or_streak row on the UTC date of the challenge's resolved_at ("active" if absent).</summary>
    private async Task<string> ChallengeQuestStatusAsync(Guid playerId, Guid challengeId)
    {
        await using var db = api.CreateDbContext();
        var day = DateOnly.FromDateTime((await db.Challenges.SingleAsync(c => c.Id == challengeId)).ResolvedAt!.Value);
        var status = await db.PlayerQuests
            .Where(q => q.PlayerId == playerId && q.PeriodStart == day)
            .Join(db.QuestDefinitions.Where(d => d.Code == "challenge_or_streak"), q => q.QuestId, d => d.Id, (q, _) => (QuestStatus?)q.Status)
            .SingleOrDefaultAsync();
        return status is null ? "active" : TextEnums.ToDatabase(status.Value);
    }
}
