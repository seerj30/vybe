using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Jobs;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Spec §4/§6 sweepers: sessions, challenges and elapsed suspensions.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SweeperTests(ApiFactory api)
{
    [Fact]
    public async Task Sessions_PastTheirDeadlines_Expire_AndASecondSweepChangesNothing()
    {
        var notStarted = await Player.CreateGuestAsync(api);
        var abandoned = await Player.CreateGuestAsync(api);
        var created = await Contract.AssertAsync(
            await notStarted.Http.PostAsync("/api/v1/games/street-hoops/sessions", Json("{}")), HttpStatusCode.Created, "SessionCreated");
        var (active, _, config, _) = await abandoned.StartRunAsync();

        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + config.GraceMs + 1));
        await SweepAsync();
        Assert.Equal(GameSessionStatus.Expired, await StatusAsync(active));
        Assert.Equal(GameSessionStatus.Created, await StatusAsync(created.GetProperty("sessionId").GetGuid())); // 10 min to start

        api.Clock.Advance(TimeSpan.FromMinutes(10));
        await SweepAsync();
        Assert.Equal(GameSessionStatus.Expired, await StatusAsync(created.GetProperty("sessionId").GetGuid()));
        var again = await SweepAsync();
        Assert.Equal(0, again.Sessions);
    }

    [Fact]
    public async Task LateSubmit_RacingTheSweeper_GivesOneExpiredState()
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + config.GraceMs + 1));

        var submit = player.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 10));
        await Task.WhenAll(submit, SweepAsync());

        var body = await Contract.AssertAsync(await submit, HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("expired", body.GetProperty("status").GetString());
        Assert.Equal(GameSessionStatus.Expired, await StatusAsync(sessionId));
    }

    [Fact]
    public async Task ExpiredRecipientSession_ExpiresItsChallenge()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await ChallengeTests.AcceptAsync(recipient, token);
        var (sessionId, _, config, _) = await recipient.StartRecipientRunAsync(challengeId);

        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + config.GraceMs + 1));
        await SweepAsync();

        Assert.Equal(GameSessionStatus.Expired, await StatusAsync(sessionId));
        Assert.Equal(ChallengeStatus.Expired, await ChallengeStatusAsync(challengeId));
    }

    [Fact]
    public async Task Challenges_ExpireAfterTheirDeadlines_ButNeverWithAnActiveRecipientRun()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (open, _) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        var (unplayed, unplayedToken) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        var (playing, playingToken) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 10));
        await ChallengeTests.AcceptAsync(recipient, unplayedToken);
        await ChallengeTests.AcceptAsync(recipient, playingToken);

        // The recipient starts just before complete_deadline; the run may finish after it.
        api.Clock.Advance(Features.ChallengeEndpoints.CompleteWindow - TimeSpan.FromSeconds(5));
        var (sessionId, seed, config, rulesetId) = await recipient.StartRecipientRunAsync(playing);
        api.Clock.Advance(TimeSpan.FromSeconds(10));
        await SweepAsync();

        Assert.Equal(ChallengeStatus.Expired, await ChallengeStatusAsync(open));
        Assert.Equal(ChallengeStatus.Expired, await ChallengeStatusAsync(unplayed));
        Assert.Equal(ChallengeStatus.Accepted, await ChallengeStatusAsync(playing));

        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100) - TimeSpan.FromSeconds(10)); // inside submit_deadline
        var body = await Contract.AssertAsync(
            await recipient.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 10)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(ChallengeStatus.Completed, await ChallengeStatusAsync(playing));
    }

    [Fact]
    public async Task ElapsedSuspension_IsReset_IndefiniteOneStays()
    {
        var elapsed = await Player.CreateGuestAsync(api);
        var indefinite = await Player.CreateGuestAsync(api);
        var now = api.Clock.GetUtcNow().UtcDateTime;
        await using (var db = api.CreateDbContext())
        {
            await db.Players.Where(p => p.Id == elapsed.Id).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, PlayerStatus.Suspended)
                .SetProperty(p => p.SuspendedUntil, now.AddMinutes(-1))
                .SetProperty(p => p.SuspensionReason, "test"));
            await db.Players.Where(p => p.Id == indefinite.Id).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, PlayerStatus.Suspended)
                .SetProperty(p => p.SuspensionReason, "test"));
        }

        await SweepAsync();

        await using var check = api.CreateDbContext();
        var reset = await check.Players.SingleAsync(p => p.Id == elapsed.Id);
        Assert.Equal(PlayerStatus.Active, reset.Status);
        Assert.Null(reset.SuspendedUntil);
        Assert.Null(reset.SuspensionReason);
        Assert.Equal(PlayerStatus.Suspended, (await check.Players.SingleAsync(p => p.Id == indefinite.Id)).Status);
    }

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private Task<Sweeper.Result> SweepAsync() => api.Services.GetRequiredService<Sweeper>().RunOnceAsync(CancellationToken.None);

    private async Task<GameSessionStatus> StatusAsync(Guid sessionId)
    {
        await using var db = api.CreateDbContext();
        return await db.GameSessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync();
    }

    private async Task<ChallengeStatus> ChallengeStatusAsync(Guid challengeId)
    {
        await using var db = api.CreateDbContext();
        return await db.Challenges.Where(c => c.Id == challengeId).Select(c => c.Status).SingleAsync();
    }
}
