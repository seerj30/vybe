using System.Globalization;
using System.Text;
using System.Text.Json;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>Builds submit bodies for the seeded <c>street-hoops-v1</c> ruleset.</summary>
internal static class Traces
{
    public const string RulesetId = "street-hoops-v1";

    public static readonly StreetHoopsConfig Config = new()
    {
        StreamId = 54,
        DurationMs = 60_000,
        GraceMs = 5_000,
        EarlyToleranceMs = 2_000,
        SegmentMs = 4_000,
        XMin = -700,
        XMax = 700,
        PMin = 300,
        PMax = 900,
        HitDx = 160,
        HitDp = 120,
        PerfectDx = 50,
        PerfectDp = 50,
        HitPoints = 100,
        PerfectPoints = 150,
        StreakStart = 3,
        StreakStep = 25,
        StreakMax = 100,
        MinGapMs = 650,
        MaxEvents = 93,
        MinShotsForRewards = 5,
    };

    /// <summary><paramref name="count"/> perfect shots at 0, 650, 1300, … ms.</summary>
    public static IReadOnlyList<ShotEvent> PerfectShots(long seed, int count)
    {
        var waypoints = StreetHoopsSimulator.GenerateWaypoints(Config, seed);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                var tMs = i * Config.MinGapMs;
                var (targetX, idealPower) = StreetHoopsSimulator.TargetAt(Config, waypoints, tMs);
                return new ShotEvent(tMs, targetX, idealPower);
            })
            .ToList();
    }

    public static string Body(IEnumerable<ShotEvent> events, string rulesetId = RulesetId) =>
        Body(string.Join(",", events.Select(e => string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"tMs":{{e.TMs}},"aimX":{{e.AimX}},"power":{{e.Power}}}"""))), rulesetId);

    /// <summary>A body with raw event JSON, for invalid tokens.</summary>
    public static string Body(string rawEvents, string rulesetId = RulesetId) =>
        $$"""{"rulesetId":{{JsonSerializer.Serialize(rulesetId)}},"events":[{{rawEvents}}],"clientReportedScore":0}""";

    /// <summary>Score the reference simulator gives the trace (the server must agree).</summary>
    public static int Score(long seed, IReadOnlyList<ShotEvent> events) =>
        StreetHoopsSimulator.Evaluate(new EvaluateInput(
                Config,
                RulesetId,
                seed,
                new SessionTiming(SessionStatus.Active, 0, long.MaxValue),
                new Submission(RulesetId, long.MaxValue - 1, events)))
            .Score!.Value;

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
