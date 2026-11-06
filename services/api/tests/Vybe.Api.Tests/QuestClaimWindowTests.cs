using System.Net;
using static Vybe.Api.Tests.QuestTests;

namespace Vybe.Api.Tests;

/// <summary>
/// The 7-day claim window. It moves the clock a week forward, so it has its own API, database and
/// clock instead of the shared <see cref="ApiTestGroup"/> factory.
/// </summary>
public sealed class QuestClaimWindowTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task CompletedQuest_IsClaimableForSevenDays()
    {
        StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await player.PlayPerfectRunAsync();
        var quest = Quest(await ListAsync(player), "score_1500");
        var until = quest.GetProperty("claimableUntil").GetDateTime().ToUniversalTime();

        api.Clock.Advance(until - api.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(1));
        var listed = (await ListAsync(player)).GetProperty("quests").EnumerateArray();
        Assert.Contains(listed, q => Id(q) == Id(quest)); // an earlier day's completed quest, still claimable

        api.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain((await ListAsync(player)).GetProperty("quests").EnumerateArray(), q => Id(q) == Id(quest));
        await Contract.AssertProblemAsync(await ClaimAsync(player, Id(quest)), HttpStatusCode.Gone, "quest_claim_expired");
    }
}
