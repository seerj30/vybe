using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vybe.Api.Features;
using Vybe.Api.Jobs;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>P1 achievements (docs/design/p1-achievements.md).</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AchievementTests(ApiFactory api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private AchievementCatalog Catalog => api.Services.GetRequiredService<AchievementCatalog>();

    [Fact]
    public void Manifest_IsValid_WithBadgeAndXpOnly()
    {
        var all = Catalog.All;
        Assert.InRange(all.Count, 20, 30);
        Assert.Equal(all.Count, all.Select(a => a.Code).Distinct().Count());
        Assert.All(all, a => Assert.True(AchievementStats.IsKnown(a.Stat), a.Stat));
        Assert.All(all, a => Assert.Equal(a.Tier switch { AchievementTier.Bronze => 25, AchievementTier.Silver => 50, _ => 100 }, a.RewardXp));
        Assert.Contains(all, a => a.Stat == AchievementStats.BestOf(Games.StreetJuggle));
    }

    [Fact]
    public async Task VerifiedRun_UnlocksAchievements_GrantsXpOnce_AndReplaysTheSameList()
    {
        var player = await Player.CreateGuestAsync(api);
        var (sessionId, seed, config, rulesetId) = await player.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));
        var key = Guid.NewGuid();
        var events = Player.PerfectEvents(config, seed, 12); // 12 shots: no review flags, score above 1,500
        var body = await Contract.AssertAsync(await player.SubmitAsync(sessionId, rulesetId, events, key), HttpStatusCode.OK, "SubmitResult");
        var unlocked = Codes(body.GetProperty("achievementsUnlocked"));
        Assert.Contains("hoops_first_run", unlocked);
        Assert.Contains("hoops_score_1500", unlocked);
        Assert.DoesNotContain("hoops_score_4000", unlocked);

        // Same key: the stored answer. New key: the canonical answer of the finished session, with the same list.
        var replay = await Contract.AssertAsync(await player.SubmitAsync(sessionId, rulesetId, events, key), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal(unlocked, Codes(replay.GetProperty("achievementsUnlocked")));
        var duplicate = await Contract.AssertAsync(await player.SubmitAsync(sessionId, rulesetId, events), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal(unlocked, Codes(duplicate.GetProperty("achievementsUnlocked")));

        // XP: the run's XP plus each unlock's XP, once.
        var unlockXp = body.GetProperty("achievementsUnlocked").EnumerateArray().Sum(u => u.GetProperty("rewardXp").GetInt32());
        await using (var db = api.CreateDbContext())
        {
            var xp = await db.Players.Where(p => p.Id == player.Id).Select(p => p.XpTotal).SingleAsync();
            Assert.Equal(body.GetProperty("rewards").GetProperty("xp").GetInt32() + unlockXp, xp);
            Assert.Equal(Rewards.LevelFor(xp), body.GetProperty("rewards").GetProperty("levelAfter").GetInt32());
        }

        var list = await ListAsync(player);
        Assert.Equal(Catalog.All.Count, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(unlocked.Count, list.GetProperty("unlockedCount").GetInt32());
        Assert.Equal(unlockXp, list.GetProperty("earnedXp").GetInt32());
        var first = Find(list, "hoops_first_run");
        Assert.Equal(1, first.GetProperty("progress").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, first.GetProperty("unlockedAt").ValueKind);
        var runs10 = Find(list, "runs_10");
        Assert.Equal((1, 10, JsonValueKind.Null), (runs10.GetProperty("progress").GetInt32(), runs10.GetProperty("target").GetInt32(),
            runs10.GetProperty("unlockedAt").ValueKind));
        Assert.Equal("private, no-store", (await player.Http.GetAsync("/api/v1/me/achievements")).Headers.NonValidated["Cache-Control"].ToString());
    }

    [Fact]
    public async Task FlaggedShortAndRejectedRuns_DoNotUnlockScoreAchievements()
    {
        var flagged = await Player.CreateGuestAsync(api);
        var body = await flagged.PlayPerfectRunAsync(); // 93 perfect shots at a fixed rhythm: review flags, a very high score
        var unlocked = Codes(body.GetProperty("achievementsUnlocked"));
        Assert.Contains("hoops_first_run", unlocked); // a flagged run is still a finished run
        Assert.DoesNotContain(unlocked, c => c.StartsWith("hoops_score_", StringComparison.Ordinal) || c.StartsWith("weekly_", StringComparison.Ordinal));
        Assert.Null(await StatAsync(flagged, AchievementStats.BestOf(Games.StreetHoops)));

        var shortRun = await Player.CreateGuestAsync(api);
        var (sessionId, seed, config, rulesetId) = await shortRun.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromSeconds(10));
        body = await Contract.AssertAsync(await shortRun.SubmitAsync(sessionId, rulesetId, Player.PerfectEvents(config, seed, 12)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("achievementsUnlocked").EnumerateArray());

        var rejected = await Player.CreateGuestAsync(api);
        (sessionId, _, config, rulesetId) = await rejected.StartRunAsync();
        api.Clock.Advance(TimeSpan.FromMilliseconds(config.DurationMs + 100));
        body = await Contract.AssertAsync(
            await rejected.SubmitAsync(sessionId, rulesetId, new[] { new { tMs = 0, aimX = 5000, power = 5000 } }), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("rejected", body.GetProperty("status").GetString());

        foreach (var player in new[] { shortRun, rejected })
        {
            Assert.Equal(0, (await ListAsync(player)).GetProperty("unlockedCount").GetInt32());
            Assert.Null(await StatAsync(player, AchievementStats.Runs));
        }
    }

    [Fact]
    public async Task Challenge_RecipientRunHasNoScoreAchievement_AndTheResultUnlocksThroughTheJob()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, await api.PlayRunAsync(creator, 6));
        Assert.Equal(HttpStatusCode.OK, (await ChallengeTests.AcceptAsync(recipient, token)).StatusCode);
        var body = await api.PlayRecipientRunAsync(recipient, challengeId, 19); // above 4,000 points, but a known seed
        var unlocked = Codes(body.GetProperty("achievementsUnlocked"));
        Assert.Contains("hoops_first_run", unlocked);
        Assert.DoesNotContain(unlocked, c => c.StartsWith("hoops_score_", StringComparison.Ordinal));

        await api.Services.GetRequiredService<OutboxProcessor>().RunOnceAsync(1_000, CancellationToken.None);
        Assert.Equal(["challenge_first", "challenge_win_1"], UnlockedCodes(await ListAsync(recipient)).Where(c => c.StartsWith("challenge_", StringComparison.Ordinal)));
        Assert.Equal(["challenge_first"], UnlockedCodes(await ListAsync(creator)).Where(c => c.StartsWith("challenge_", StringComparison.Ordinal)));

        // A second run of the job (for example a retry) changes nothing.
        var before = await StatAsync(recipient, AchievementStats.ChallengesWon);
        await using (var db = api.CreateDbContext())
        {
            await ChallengeResolvedJob.RunAsync(db, Catalog, new ChallengeResolvedJob.Payload(challengeId, recipient.Id), api.Clock.GetUtcNow().UtcDateTime,
                CancellationToken.None);
        }

        Assert.Equal(before, await StatAsync(recipient, AchievementStats.ChallengesWon));
    }

    [Fact]
    public async Task FriendsPurchasesOutfitsAndQuests_UnlockInTheirTransactionsOrThroughTheSyncJob()
    {
        var a = await Player.CreateGuestAsync(api);
        var b = await Player.CreateGuestAsync(api);
        var aHandle = (await a.MeAsync()).GetProperty("handle").GetString()!;
        var bHandle = (await b.MeAsync()).GetProperty("handle").GetString()!;
        await FriendTests.RequestOkAsync(a, bHandle);
        await Contract.AssertAsync(await FriendTests.AcceptAsync(b, aHandle), HttpStatusCode.OK, "FriendRequestResult");

        await a.PlayPerfectRunAsync();
        await a.PlayPerfectRunAsync(); // coins for one item
        await Contract.AssertAsync(await a.PurchaseAsync(await a.CatalogItemIdAsync("acc_sweatband")), HttpStatusCode.OK, "Purchase");
        Assert.DoesNotContain("friends_1", UnlockedCodes(await ListAsync(a)));
        Assert.DoesNotContain("items_1", UnlockedCodes(await ListAsync(a)));

        await api.Services.GetRequiredService<OutboxProcessor>().RunOnceAsync(1_000, CancellationToken.None);
        Assert.Contains("friends_1", UnlockedCodes(await ListAsync(a)));
        Assert.Contains("friends_1", UnlockedCodes(await ListAsync(b)));
        Assert.Contains("items_1", UnlockedCodes(await ListAsync(a)));

        // A removed friend does not take the achievement away.
        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.DeleteAsync($"/api/v1/me/friends/{bHandle}")).StatusCode);
        await using (var db = api.CreateDbContext())
        {
            await AchievementProgress.SyncCountsAsync(db, Catalog, a.Id, api.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        }

        Assert.Equal(1, await StatAsync(a, AchievementStats.Friends));
        Assert.Contains("friends_1", UnlockedCodes(await ListAsync(a)));

        // Outfit save: in its own transaction, at once.
        Assert.Equal(HttpStatusCode.Created, (await a.Http.PostAsJsonAsync("/api/v1/me/outfits", new { name = "Fit" })).StatusCode);
        Assert.Contains("outfit_1", UnlockedCodes(await ListAsync(a)));

        // Quest claim: counted at once (play_3 is complete after two runs plus one more).
        await a.PlayPerfectRunAsync();
        var quests = await Contract.AssertAsync(await a.Http.GetAsync("/api/v1/me/quests"), HttpStatusCode.OK, "QuestList");
        var play3 = quests.GetProperty("quests").EnumerateArray().Single(q => q.GetProperty("code").GetString() == "play_3");
        using var claim = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/me/quests/{play3.GetProperty("playerQuestId").GetGuid()}/claim");
        claim.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await Contract.AssertAsync(await a.Http.SendAsync(claim), HttpStatusCode.OK, "QuestClaimed");
        Assert.Equal(1, await StatAsync(a, AchievementStats.QuestsClaimed));
    }

    [Fact]
    public async Task PlayStreak_CountsConsecutiveDays()
    {
        var player = await Player.CreateGuestAsync(api);
        for (var day = 0; day < 3; day++)
        {
            await api.PlayRunAsync(player, 6);
            api.Clock.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(3, await StatAsync(player, AchievementStats.PlayStreak));
        Assert.Contains("streak_3", UnlockedCodes(await ListAsync(player)));
        Assert.DoesNotContain("streak_7", UnlockedCodes(await ListAsync(player)));
    }

    [Fact]
    public async Task Profile_ShowsBadges_UnderTheProfileVisibilityRule()
    {
        var owner = await Player.CreateGuestAsync(api);
        var stranger = await Player.CreateGuestAsync(api);
        var handle = (await owner.MeAsync()).GetProperty("handle").GetString()!;
        await api.PlayRunAsync(owner, 12);

        var seen = await Contract.AssertAsync(await stranger.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.OK, "PublicProfile");
        var badges = seen.GetProperty("badges").EnumerateArray().ToList();
        Assert.Contains(badges, b => b.GetProperty("code").GetString() == "hoops_first_run");
        Assert.All(badges, b => Assert.False(b.TryGetProperty("unlockedAt", out _))); // no unlock times on the profile

        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PatchAsJsonAsync("/api/v1/me/privacy", new { profileVisibility = "private" })).StatusCode);
        await Contract.AssertProblemAsync(await stranger.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.NotFound, "not_found");
        var own = await Contract.AssertAsync(await owner.Http.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.OK, "PublicProfile");
        Assert.Equal(badges.Count, own.GetProperty("badges").GetArrayLength());
        var anonymous = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await Contract.AssertProblemAsync(await anonymous.GetAsync($"/api/v1/profiles/{handle}"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Deletion_RemovesTheRows_AndTheExportListsThem()
    {
        var player = await Player.CreateGuestAsync(api);
        await api.PlayRunAsync(player, 12);
        await using (var db = api.CreateDbContext())
        {
            var export = JsonSerializer.SerializeToElement(
                await PlayerDataExport.BuildAsync(db, player.Id, api.Clock.GetUtcNow().UtcDateTime, CancellationToken.None),
                Web);
            Assert.Contains(export.GetProperty("achievements").EnumerateArray(), a => a.GetProperty("code").GetString() == "hoops_first_run");
            Assert.Contains(export.GetProperty("stats").EnumerateArray(), s => s.GetProperty("stat").GetString() == AchievementStats.Runs);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, (await player.Http.SendAsync(request)).StatusCode);
        await api.Services.GetRequiredService<PrivacyProcessor>().RunOnceAsync(1_000, CancellationToken.None);

        await using (var db = api.CreateDbContext())
        {
            Assert.Equal(PlayerStatus.Deleted, await db.Players.Where(p => p.Id == player.Id).Select(p => p.Status).SingleAsync());
            Assert.False(await db.PlayerAchievements.AnyAsync(a => a.PlayerId == player.Id));
            Assert.False(await db.PlayerStats.AnyAsync(s => s.PlayerId == player.Id));
        }
    }

    [Fact]
    public async Task Backfill_GivesTheLiveResult_AndASecondRunChangesNothing()
    {
        var player = await Player.CreateGuestAsync(api);
        var other = await Player.CreateGuestAsync(api);
        var otherHandle = (await other.MeAsync()).GetProperty("handle").GetString()!;
        await api.PlayRunAsync(player, 12);
        await api.PlayRunAsync(player, 19);
        await player.PlayPerfectRunAsync(); // flagged: counts as a run, not for the best score
        Assert.Equal(HttpStatusCode.Created, (await player.Http.PostAsJsonAsync("/api/v1/me/outfits", new { name = "Fit" })).StatusCode);
        await FriendTests.RequestOkAsync(other, (await player.MeAsync()).GetProperty("handle").GetString()!);
        await Contract.AssertAsync(await FriendTests.AcceptAsync(player, otherHandle), HttpStatusCode.OK, "FriendRequestResult");
        await api.Services.GetRequiredService<OutboxProcessor>().RunOnceAsync(1_000, CancellationToken.None);

        var live = await StatsAsync(player);
        var liveCodes = UnlockedCodes(await ListAsync(player));
        Assert.Contains("hoops_score_4000", liveCodes);

        // A player from before the achievements: no stats, no unlocks.
        await using (var db = api.CreateDbContext())
        {
            await db.PlayerAchievements.Where(a => a.PlayerId == player.Id).ExecuteDeleteAsync();
            await db.PlayerStats.Where(s => s.PlayerId == player.Id).ExecuteDeleteAsync();
            await AchievementBackfill.RunAsync(db, Catalog, api.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        }

        var backfilled = await StatsAsync(player);
        foreach (var stat in new[]
                 {
                     AchievementStats.Runs, AchievementStats.RunsOf(Games.StreetHoops), AchievementStats.BestOf(Games.StreetHoops),
                     AchievementStats.GamesPlayed, AchievementStats.PlayStreak, AchievementStats.OutfitsSaved, AchievementStats.Friends,
                 })
        {
            Assert.Equal(live[stat], backfilled[stat]);
        }

        Assert.Equal(liveCodes.Where(c => !c.StartsWith("weekly_", StringComparison.Ordinal)),
            UnlockedCodes(await ListAsync(player)).Where(c => !c.StartsWith("weekly_", StringComparison.Ordinal)));

        long xp;
        await using (var db = api.CreateDbContext())
        {
            xp = await db.Players.Where(p => p.Id == player.Id).Select(p => p.XpTotal).SingleAsync();
            await AchievementBackfill.RunAsync(db, Catalog, api.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        }

        await using (var db = api.CreateDbContext())
        {
            Assert.Equal(xp, await db.Players.Where(p => p.Id == player.Id).Select(p => p.XpTotal).SingleAsync());
        }

        Assert.Equal(backfilled, await StatsAsync(player));
    }

    [Fact]
    public async Task Seeder_IsIdempotent_AndDeactivatesARemovedCode()
    {
        await using var db = api.CreateDbContext();
        Assert.False(await AchievementSeeder.SeedAsync(db, Catalog, CancellationToken.None));
        Assert.Equal(Catalog.All.Count, await db.Achievements.CountAsync(a => a.IsActive));
    }

    [Fact]
    public async Task Seed_BackfillsOncePerRulesVersion_AndAgainAfterAStoppedBackfill()
    {
        await Player.CreateGuestAsync(api); // at least one player to backfill
        await using var db = api.CreateDbContext();
        // The test host's startup seed already backfilled these rules: an unchanged manifest does no backfill work.
        Assert.Null(await AchievementSeeding.SeedAndBackfillAsync(db, Catalog, api.Clock, CancellationToken.None));
        Assert.Equal(AchievementSeeding.Fingerprint(Catalog),
            await db.ContentMarkers.Where(m => m.Name == AchievementSeeding.BackfillMarker).Select(m => m.Fingerprint).SingleAsync());

        // No marker (a backfill that stopped half way, or new rules): the next seed backfills again, then marks it done.
        await db.ContentMarkers.Where(m => m.Name == AchievementSeeding.BackfillMarker).ExecuteDeleteAsync();
        Assert.True(await AchievementSeeding.SeedAndBackfillAsync(db, Catalog, api.Clock, CancellationToken.None) > 0);
        Assert.Null(await AchievementSeeding.SeedAndBackfillAsync(db, Catalog, api.Clock, CancellationToken.None));
    }

    private static async Task<JsonElement> ListAsync(Player player) =>
        await Contract.AssertAsync(await player.Http.GetAsync("/api/v1/me/achievements"), HttpStatusCode.OK, "AchievementList");

    private static JsonElement Find(JsonElement list, string code) =>
        list.GetProperty("achievements").EnumerateArray().Single(a => a.GetProperty("code").GetString() == code);

    private static List<string> Codes(JsonElement array) => array.EnumerateArray().Select(u => u.GetProperty("code").GetString()!).ToList();

    private static List<string> UnlockedCodes(JsonElement list) =>
        list.GetProperty("achievements").EnumerateArray()
            .Where(a => a.GetProperty("unlockedAt").ValueKind != JsonValueKind.Null)
            .Select(a => a.GetProperty("code").GetString()!)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

    private async Task<long?> StatAsync(Player player, string stat)
    {
        await using var db = api.CreateDbContext();
        return await db.PlayerStats.Where(s => s.PlayerId == player.Id && s.Stat == stat).Select(s => (long?)s.Value).SingleOrDefaultAsync();
    }

    private async Task<Dictionary<string, long>> StatsAsync(Player player)
    {
        await using var db = api.CreateDbContext();
        return await db.PlayerStats.Where(s => s.PlayerId == player.Id).ToDictionaryAsync(s => s.Stat, s => s.Value);
    }
}
