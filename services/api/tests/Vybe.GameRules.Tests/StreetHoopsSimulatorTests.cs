using System.Text.Json;

namespace Vybe.GameRules.Tests;

/// <summary>Hand-checked rules, independent of the generated fixtures.</summary>
public sealed class StreetHoopsSimulatorTests
{
    private static readonly StreetHoopsConfig Config =
        FixtureFiles.Read<RulesetFile>("rulesets/street-hoops-v1.json").Config;

    private const long StartedAtMs = 1_000;

    private static EvaluateInput Input(IReadOnlyList<ShotEvent> events, long seed = 12345) =>
        new(
            Config,
            "street-hoops-v1",
            seed,
            new SessionTiming(SessionStatus.Active, StartedAtMs, StartedAtMs + Config.DurationMs + Config.GraceMs),
            new Submission("street-hoops-v1", StartedAtMs + Config.DurationMs + 500, events));

    private static List<ShotEvent> PerfectTrace(long seed, int count, int gapMs)
    {
        var waypoints = StreetHoopsSimulator.GenerateWaypoints(Config, seed);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var t = i * gapMs;
                var (x, p) = StreetHoopsSimulator.TargetAt(Config, waypoints, t);
                return new ShotEvent(t, x, p);
            })
            .ToList();
    }

    [Fact]
    public void Pcg32_MatchesOfficialDemoVector()
    {
        var rng = new Pcg32(42, 54);
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        Assert.Equal(expected, expected.Select(_ => rng.NextUInt32()).ToArray());
    }

    [Fact]
    public void TargetAt_RoundsTowardZero_ForNegativeDeltas()
    {
        Waypoint[] waypoints =
        [
            new(-700, 900),
            new(700, 300),
            .. Enumerable.Repeat(new Waypoint(0, 600), 14),
        ];

        Assert.Equal((-700, 900), StreetHoopsSimulator.TargetAt(Config, waypoints, 0));
        Assert.Equal((699, 301), StreetHoopsSimulator.TargetAt(Config, waypoints, 3999));
        Assert.Equal((700, 300), StreetHoopsSimulator.TargetAt(Config, waypoints, 4000));
        Assert.Throws<ArgumentOutOfRangeException>(() => StreetHoopsSimulator.TargetAt(Config, waypoints, 60_000));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 25)]
    [InlineData(4, 50)]
    [InlineData(5, 75)]
    [InlineData(6, 100)]
    [InlineData(50, 100)]
    public void StreakBonus_GrowsAndCaps(int streak, int expected) =>
        Assert.Equal(expected, StreetHoopsSimulator.StreakBonus(Config, streak));

    [Fact]
    public void Evaluate_AllPerfect_GivesMaximumScore()
    {
        var result = StreetHoopsSimulator.Evaluate(Input(PerfectTrace(12345, 93, 650)));

        Assert.Equal(Outcome.Verified, result.Outcome);
        Assert.Equal(22_900, result.Score);
        Assert.Equal(93, result.MaxStreak);
    }

    [Fact]
    public void Evaluate_EmptyTrace_IsVerifiedWithZero()
    {
        var result = StreetHoopsSimulator.Evaluate(Input([]));

        Assert.Equal(Outcome.Verified, result.Outcome);
        Assert.Equal(0, result.Score);
        Assert.Equal(0, result.EventCount);
    }

    [Fact]
    public void Evaluate_ChecksCategoriesOverWholeTrace()
    {
        ShotEvent[] events =
        [
            new(0, 0, 500),
            new(2000, 0, 500),
            new(1000, 0, 500),
            new(3000, 0, 500),
            new(4000, 1001, 500),
        ];

        var result = StreetHoopsSimulator.Evaluate(Input(events));

        Assert.Equal(Outcome.Rejected, result.Outcome);
        Assert.Equal(RejectionReason.OutOfRange, result.Reason);
    }

    [Fact]
    public void GenerateWaypoints_RejectsSeedOutsideUInt32()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StreetHoopsSimulator.GenerateWaypoints(Config, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StreetHoopsSimulator.GenerateWaypoints(Config, 1L << 32));
    }

    [Fact]
    public void ValidateConfig_RejectsDurationNotMultipleOfSegment() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreetHoopsSimulator.ValidateConfig(Config with { SegmentMs = 7000 }));

    [Fact]
    public void CanonicalJson_SortsKeysAndRejectsUnsupportedValues()
    {
        using var ok = JsonDocument.Parse("""{ "b": 1, "a": [2, { "d": -3, "c": "x" }] }""");
        Assert.Equal("""{"a":[2,{"c":"x","d":-3}],"b":1}""", CanonicalJson.Serialize(ok.RootElement));

        using var fraction = JsonDocument.Parse("""{ "a": 1.5 }""");
        using var nullValue = JsonDocument.Parse("""{ "a": null }""");
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(fraction.RootElement));
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(nullValue.RootElement));
    }

    [Theory]
    [InlineData("""{ "a": "x\n" }""")]
    [InlineData("""{ "a": 9007199254740992 }""")]
    [InlineData("""{ "a": 1, "a": 2 }""")]
    public void CanonicalJson_RejectsValuesTheTypeScriptReferenceRejects(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<NotSupportedException>(() => CanonicalJson.Serialize(document.RootElement));
    }

    public static TheoryData<string, StreetHoopsConfig> InvalidConfigs() => new()
    {
        { "streamId above uint32", Config with { StreamId = 1L << 32 } },
        { "negative streamId", Config with { StreamId = -1 } },
        { "empty x range", Config with { XMin = 1, XMax = 0 } },
        { "empty p range", Config with { PMin = 901, PMax = 900 } },
        { "zero segment", Config with { SegmentMs = 0 } },
        { "non-positive duration", Config with { DurationMs = -8000 } },
        { "zero gap", Config with { MinGapMs = 0 } },
        { "negative maxEvents", Config with { MaxEvents = -1 } },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void ValidateConfig_RejectsInvalidValues(string label, StreetHoopsConfig config)
    {
        Assert.NotEmpty(label);
        Assert.Throws<ArgumentOutOfRangeException>(() => StreetHoopsSimulator.ValidateConfig(config));
    }

    [Fact]
    public void GenerateWaypoints_MatchesLiteralsDerivedFromOfficialVector()
    {
        // Outputs 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e
        // -> x = -700 + out % 1401, p = 300 + out % 601, drawn interleaved.
        Waypoint[] expected = [new(377, 745), new(577, 522), new(-96, 535)];
        Assert.Equal(expected, StreetHoopsSimulator.GenerateWaypoints(Config, 42).Take(3));
    }

    [Fact]
    public void Checksum_MatchesKnownSha256ComputedOutsideThisCode()
    {
        // printf '%s' '{"a":2,"b":1}' | shasum -a 256
        using var document = JsonDocument.Parse("""{ "b": 1, "a": 2 }""");
        Assert.Equal(
            "d3626ac30a87e6f7a6428233b3c68299976865fa5508e4267c5415c76af7a772",
            CanonicalJson.Checksum(document.RootElement));
    }
}
