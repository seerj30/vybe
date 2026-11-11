using System.Text.Json;
using Vybe.Api.Infrastructure;
using Vybe.GameRules;

namespace Vybe.Api.Features;

/// <summary>Timing and ruleset facts of one submit, the same for every game (spec §7 simulator contract).</summary>
public sealed record RunContext(string RulesetId, long Seed, SessionTiming Session, string SubmittedRulesetId, long SubmittedAtMs);

/// <summary>
/// The game-independent verdict of one submit. <paramref name="InvalidEvent"/> is the API check 4 (raw JSON
/// tokens); otherwise <paramref name="Outcome"/> and <paramref name="Reason"/> come from the game's simulator.
/// <paramref name="EventCount"/> is null for <c>invalid_event</c>. Score, max streak and review flags apply to verified runs.
/// </summary>
public sealed record RunVerdict(
    IRewardConfig Config, bool InvalidEvent, Outcome Outcome, RejectionReason? Reason, int? EventCount, int Score, int MaxStreak,
    IReadOnlyList<string> Flags);

/// <summary>The rules of one game: config type, event parser, simulator and review flags (design p1-second-game §5).</summary>
public interface IGameRules
{
    string GameId { get; }

    /// <summary>Parses and validates a stored ruleset config; throws on an invalid config.</summary>
    IRewardConfig ParseConfig(string configJson);

    RunVerdict Verify(string configJson, RunContext run, JsonElement events);
}

/// <summary>The games the API serves, by <c>gameId</c>.</summary>
public static class Games
{
    public const string StreetHoops = "street-hoops";
    public const string StreetJuggle = "street-juggle";

    private static readonly Dictionary<string, IGameRules> All = new(StringComparer.Ordinal)
    {
        [StreetHoops] = new StreetHoopsRules(),
        [StreetJuggle] = new StreetJuggleRules(),
    };

    public static IGameRules For(string gameId) =>
        All.TryGetValue(gameId, out var rules) ? rules : throw new InvalidDataException($"unknown game {gameId}");

    public static bool IsKnown(string gameId) => All.ContainsKey(gameId);

    internal static T Parse<T>(string configJson) =>
        JsonSerializer.Deserialize<T>(configJson, ApiJson.Options) ?? throw new InvalidDataException("ruleset config is empty");

    /// <summary>
    /// Spec §7 check order: not_started, expired and ruleset_mismatch (checks 1–3) come before invalid_event
    /// (check 4). An empty trace runs exactly those checks; null means the run is <c>invalid_event</c>.
    /// </summary>
    internal static (Outcome Outcome, RejectionReason? Reason)? DecidedBeforeInvalidEvent(Outcome outcome, RejectionReason? reason) =>
        outcome == Outcome.Expired || reason is RejectionReason.NotStarted or RejectionReason.RulesetMismatch
            ? (outcome, reason)
            : null;

    private sealed class StreetHoopsRules : IGameRules
    {
        private static readonly string[] Fields = ["aimX", "power", "tMs"];

        public string GameId => StreetHoops;

        public IRewardConfig ParseConfig(string configJson)
        {
            var config = Parse<StreetHoopsConfig>(configJson);
            StreetHoopsSimulator.ValidateConfig(config);
            return config;
        }

        public RunVerdict Verify(string configJson, RunContext run, JsonElement events)
        {
            var config = (StreetHoopsConfig)ParseConfig(configJson);
            EvaluateInput Input(IReadOnlyList<ShotEvent> trace) => new(
                config, run.RulesetId, run.Seed, run.Session, new Submission(run.SubmittedRulesetId, run.SubmittedAtMs, trace));

            if (!SubmitParser.TryParseEvents(events, Fields, out var rows))
            {
                var precheck = StreetHoopsSimulator.Evaluate(Input([]));
                return DecidedBeforeInvalidEvent(precheck.Outcome, precheck.Reason) is { } early
                    ? new RunVerdict(config, false, early.Outcome, early.Reason, 0, 0, 0, [])
                    : new RunVerdict(config, true, Outcome.Rejected, null, null, 0, 0, []);
            }

            // Fields are in ordinal order: aimX, power, tMs.
            var shots = rows.Select(r => new ShotEvent(r[2], r[0], r[1])).ToList();
            var result = StreetHoopsSimulator.Evaluate(Input(shots));
            return result.Outcome == Outcome.Verified
                ? new RunVerdict(config, false, Outcome.Verified, null, shots.Count, result.Score!.Value, result.MaxStreak!.Value,
                    ReviewFlags.ForRun(result.Shots!))
                : new RunVerdict(config, false, result.Outcome, result.Reason, shots.Count, 0, 0, []);
        }
    }

    private sealed class StreetJuggleRules : IGameRules
    {
        private static readonly string[] Fields = ["lane", "tMs"];

        public string GameId => StreetJuggle;

        public IRewardConfig ParseConfig(string configJson)
        {
            var config = Parse<StreetJuggleConfig>(configJson);
            StreetJuggleSimulator.ValidateConfig(config);
            return config;
        }

        public RunVerdict Verify(string configJson, RunContext run, JsonElement events)
        {
            var config = (StreetJuggleConfig)ParseConfig(configJson);
            JuggleEvaluateInput Input(IReadOnlyList<JuggleTouch> trace) => new(
                config, run.RulesetId, run.Seed, run.Session, new JuggleSubmission(run.SubmittedRulesetId, run.SubmittedAtMs, trace));

            if (!SubmitParser.TryParseEvents(events, Fields, out var rows))
            {
                var precheck = StreetJuggleSimulator.Evaluate(Input([]));
                return DecidedBeforeInvalidEvent(precheck.Outcome, precheck.Reason) is { } early
                    ? new RunVerdict(config, false, early.Outcome, early.Reason, 0, 0, 0, [])
                    : new RunVerdict(config, true, Outcome.Rejected, null, null, 0, 0, []);
            }

            // Fields are in ordinal order: lane, tMs.
            var touches = rows.Select(r => new JuggleTouch(r[1], r[0])).ToList();
            var result = StreetJuggleSimulator.Evaluate(Input(touches));
            return result.Outcome == Outcome.Verified
                ? new RunVerdict(config, false, Outcome.Verified, null, touches.Count, result.Score!.Value, result.MaxStreak!.Value,
                    ReviewFlags.ForJuggle(result.Touches!))
                : new RunVerdict(config, false, result.Outcome, result.Reason, touches.Count, 0, 0, []);
        }
    }
}
