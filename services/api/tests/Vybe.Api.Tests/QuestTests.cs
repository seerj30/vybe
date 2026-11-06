using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Features;
using Vybe.Api.Jobs;
using Vybe.Data;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>Spec §8 daily quests, §6 Quest and invariant 11; every response is checked against the contract.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class QuestTests(ApiFactory api)
{
    [Fact]
    public async Task ListQuests_CreatesTodaysRows_Once()
    {
        var player = await Player.CreateGuestAsync(api);

        var first = await ListAsync(player);
        var second = await ListAsync(player);

        var quests = first.GetProperty("quests").EnumerateArray().ToList();
        Assert.Equal(["challenge_or_streak", "play_3", "score_1500"], quests.Select(q => q.GetProperty("code").GetString()));
        Assert.All(quests, q =>
        {
            Assert.Equal(0, q.GetProperty("progress").GetInt32());
            Assert.Equal("active", q.GetProperty("status").GetString());
            Assert.Equal(100, q.GetProperty("rewardCoins").GetInt64());
            Assert.Equal(60, q.GetProperty("rewardXp").GetInt32());
        });
        Assert.Equal(3, quests.Single(q => q.GetProperty("code").GetString() == "play_3").GetProperty("target").GetInt32());
        Assert.Equal(quests.Select(Id), second.GetProperty("quests").EnumerateArray().Select(Id));
    }

    [Fact]
    public async Task VerifiedRuns_CompleteQuests_AndClaimPaysOnce()
    {
        StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await ListAsync(player); // rows created by GET, then completed by the upsert
        await api.PlayRunAsync(player, 4); // below minShotsForRewards: no progress
        Assert.Equal(0, Quest(await ListAsync(player), "play_3").GetProperty("progress").GetInt32());

        for (var run = 0; run < 3; run++)
        {
            await player.PlayPerfectRunAsync(); // 22,900 points, max streak 93
        }

        var list = await ListAsync(player);
        Assert.All(list.GetProperty("quests").EnumerateArray(), q => Assert.Equal("completed", q.GetProperty("status").GetString()));
        var balance = (await player.MeAsync()).GetProperty("walletBalance").GetInt64(); // 3 × 150 + level 2 bonus

        var play3 = Id(Quest(list, "play_3"));
        var key = Guid.NewGuid();
        var claimed = await Contract.AssertAsync(await ClaimAsync(player, play3, key), HttpStatusCode.OK, "QuestClaimed");
        Assert.Equal(100, claimed.GetProperty("coinsGranted").GetInt64());
        Assert.Equal(60, claimed.GetProperty("xpGranted").GetInt32());
        Assert.Equal(2, claimed.GetProperty("level").GetInt32()); // 240 + 60 XP

        var replay = await Contract.AssertAsync(await ClaimAsync(player, play3, key), HttpStatusCode.OK, "QuestClaimed");
        Assert.True(JsonElement.DeepEquals(claimed, replay));
        await Contract.AssertProblemAsync(await ClaimAsync(player, play3), HttpStatusCode.Conflict, "quest_already_claimed");
        Assert.Equal("claimed", Quest(await ListAsync(player), "play_3").GetProperty("status").GetString());

        // Quest XP crosses a level: 360 + 60 = 420 XP → level 3 and its level reward.
        await Contract.AssertAsync(await ClaimAsync(player, Id(Quest(list, "score_1500"))), HttpStatusCode.OK, "QuestClaimed");
        var third = await Contract.AssertAsync(
            await ClaimAsync(player, Id(Quest(list, "challenge_or_streak"))), HttpStatusCode.OK, "QuestClaimed");
        Assert.Equal(3, third.GetProperty("level").GetInt32());
        Assert.Equal(balance + 300 + Rewards.LevelUpCoins, (await player.MeAsync()).GetProperty("walletBalance").GetInt64());
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.WalletTransactions.CountAsync(t => t.PlayerId == player.Id && t.LedgerKey == $"quest:{play3}"));
    }

    [Fact]
    public async Task Claim_Errors()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var list = await ListAsync(player);

        await Contract.AssertProblemAsync(await ClaimAsync(player, Id(Quest(list, "play_3"))), HttpStatusCode.Conflict, "quest_not_completed");
        await Contract.AssertProblemAsync(await ClaimAsync(other, Id(Quest(list, "play_3"))), HttpStatusCode.NotFound, "not_found");

        await player.PlayPerfectRunAsync();
        await using (var db = api.CreateDbContext())
        {
            await db.Players.Where(p => p.Id == player.Id).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, PlayerStatus.Suspended)
                .SetProperty(p => p.SuspensionReason, "test"));
        }

        await Contract.AssertProblemAsync(
            await ClaimAsync(player, Id(Quest(list, "score_1500"))), HttpStatusCode.Forbidden, "account_suspended");
    }

    [Fact]
    public async Task CompletedChallenge_CountsForBothParticipants_ThroughTheOutbox()
    {
        StartEarlyInADay(api);
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var run = await api.PlayShortStreakRunAsync(creator); // eligible, max streak 4: no streak progress
        var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, run);
        await ChallengeTests.AcceptAsync(recipient, token);
        await api.PlayRecipientRunAsync(recipient, challengeId, 0);

        Assert.Equal(0, Quest(await ListAsync(creator), "challenge_or_streak").GetProperty("progress").GetInt32());
        await api.Services.GetRequiredService<OutboxProcessor>().RunOnceAsync(1_000, CancellationToken.None);

        foreach (var participant in new[] { creator, recipient })
        {
            Assert.Equal("completed", Quest(await ListAsync(participant), "challenge_or_streak").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task ConcurrentClaims_WithTwoKeys_PayOnce()
    {
        StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await player.PlayPerfectRunAsync();
        var quest = Id(Quest(await ListAsync(player), "score_1500"));

        var responses = await Task.WhenAll(ClaimAsync(player, quest), ClaimAsync(player, quest));

        await Contract.AssertAsync(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK, "QuestClaimed");
        await Contract.AssertProblemAsync(
            Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK), HttpStatusCode.Conflict, "quest_already_claimed");
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.WalletTransactions.CountAsync(t => t.PlayerId == player.Id && t.LedgerKey == $"quest:{quest}"));
    }

    [Fact]
    public async Task ConcurrentProgress_LosesNoIncrement()
    {
        var player = await Player.CreateGuestAsync(api);
        var day = DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime);
        var now = api.Clock.GetUtcNow().UtcDateTime;

        async Task AddAsync()
        {
            await using var db = api.CreateDbContext();
            await QuestProgress.AddAsync(db, player.Id, QuestProgress.Play3, day, 1, now, CancellationToken.None);
        }

        await Task.WhenAll(AddAsync(), AddAsync());

        Assert.Equal(2, Quest(await ListAsync(player), "play_3").GetProperty("progress").GetInt32());
    }

    /// <summary>Quest days are UTC dates: move the shared clock to 01:00 UTC so a test never crosses midnight.</summary>
    internal static void StartEarlyInADay(ApiFactory api)
    {
        var now = api.Clock.GetUtcNow();
        api.Clock.Advance(new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddHours(1), TimeSpan.Zero) - now);
    }

    internal static Guid Id(JsonElement quest) => quest.GetProperty("playerQuestId").GetGuid();

    internal static JsonElement Quest(JsonElement list, string code) =>
        list.GetProperty("quests").EnumerateArray().First(q => q.GetProperty("code").GetString() == code);

    internal static async Task<JsonElement> ListAsync(Player player) =>
        await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/quests"), HttpStatusCode.OK, "QuestList");

    internal static async Task<HttpResponseMessage> ClaimAsync(Player player, Guid playerQuestId, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/quests/{playerQuestId}/claim");
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await player.Http.SendAsync(request);
    }
}
