using System.Net;
using System.Text.Json;
using Vybe.Api.Gameplay;
using Vybe.Api.Guests;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>Reward formulas (spec §7, §8; test matrix §14 Unit).</summary>
public sealed class RunRewardsTests
{
    [Theory]
    [InlineData(0, 20, 10)]
    [InlineData(399, 20, 10)]
    [InlineData(499, 20, 15)]
    [InlineData(500, 30, 15)]
    [InlineData(800, 30, 20)]
    [InlineData(6_500, 150, 80)]
    [InlineData(22_900, 150, 80)]
    public void BaseRewards_FollowTheFormulasAndCaps(int score, long coins, int xp)
    {
        Assert.Equal(coins, RunRewards.BaseCoins(score));
        Assert.Equal(xp, RunRewards.BaseXp(score));
    }

    [Theory]
    [InlineData(1, 150)]
    [InlineData(10, 150)]
    [InlineData(11, 37)]
    [InlineData(20, 37)]
    [InlineData(21, 0)]
    public void CoinsForRun_UsesTheRunNumberTiers(int runNumber, long coins) =>
        Assert.Equal(coins, RunRewards.CoinsForRun(150, runNumber));

    [Theory]
    [InlineData(80, 0, 80)]
    [InlineData(80, 790, 10)]
    [InlineData(80, 800, 0)]
    [InlineData(80, 900, 0)]
    public void XpGrant_FillsExactlyToTheDailyCap(int xp, int xpFromGames, int grant) =>
        Assert.Equal(grant, RunRewards.XpGrant(xp, xpFromGames));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(399, 2)]
    [InlineData(400, 3)]
    [InlineData(240_099, 49)]
    [InlineData(240_100, 50)]
    [InlineData(10_000_000, 50)]
    public void LevelFor_UsesTheQuadraticThresholdsAndTheCap(long xpTotal, int level) =>
        Assert.Equal(level, RunRewards.LevelFor(xpTotal));
}

/// <summary>Raw-token <c>invalid_event</c> check (spec §5 submit).</summary>
public sealed class RawEventsTests
{
    [Fact]
    public void Parse_StrictIntegers_ReturnsTheEvents()
    {
        using var json = JsonDocument.Parse("""[{"power":500,"tMs":0,"aimX":-0},{"tMs":2147483647,"aimX":-2147483647,"power":1}]""");
        Assert.Equal(
            [new ShotEvent(0, 0, 500), new ShotEvent(2_147_483_647, -2_147_483_647, 1)],
            RawEvents.Parse(json.RootElement));
    }

    [Theory]
    [InlineData("""[{"tMs":01,"aimX":0,"power":0}]""")]
    [InlineData("""[{"tMs":-2147483648,"aimX":0,"power":0}]""")]
    [InlineData("""[{"tMs":99999999999999999999,"aimX":0,"power":0}]""")]
    [InlineData("""[{"tMs":1.0,"aimX":0,"power":0}]""")]
    [InlineData("""[{"tMs":1E2,"aimX":0,"power":0}]""")]
    [InlineData("""[{"TMs":1,"aimX":0,"power":0}]""")]
    [InlineData("""[null]""")]
    public void Parse_AnyInvalidToken_ReturnsNull(string events)
    {
        // JsonDocument rejects a leading zero itself; the parser must never see it as valid either.
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(events);
        }
        catch (JsonException)
        {
            return;
        }

        using (json)
        {
            Assert.Null(RawEvents.Parse(json.RootElement));
        }
    }
}

/// <summary>Guest-creation rate-limit partitions.</summary>
public sealed class RateLimitPartitionTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void RateLimitPartition_GroupsIpv6ByPrefix(string address, string partition) =>
        Assert.Equal(partition, GuestEndpoints.RateLimitPartition(IPAddress.Parse(address)));

    [Fact]
    public void RateLimitPartition_WithoutAddress_UsesOneSharedPartition() =>
        Assert.Equal("unknown", GuestEndpoints.RateLimitPartition(null));
}
