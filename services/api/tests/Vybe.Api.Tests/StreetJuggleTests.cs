using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Api.Features;
using Vybe.GameRules;

namespace Vybe.Api.Tests;

/// <summary>
/// Street Juggle on the API (docs/design/p1-second-game.md §4, §5, §7): the generic session endpoints with the
/// second game's rules, the decision 4 reward floor, and challenges kept inside their game.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StreetJuggleTests(ApiFactory api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FullRun_IsVerified_PaysCoins_CountsForQuests_AndIsFlagged()
    {
        QuestTests.StartEarlyInADay(api);
        var player = await Player.CreateGuestAsync(api);
        await QuestTests.ListAsync(player);

        var run = await StartAsync(player);
        var trace = JuggleBot.PerfectTrace(run.Config, run.Seed);
        var expected = Replay(run, trace);
        api.Clock.Advance(TimeSpan.FromMilliseconds(run.Config.DurationMs + 100));
        var body = await Contract.AssertAsync(await player.SubmitAsync(run.SessionId, run.RulesetId, Events(trace)), HttpStatusCode.OK, "SubmitResult");

        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(expected.Score, body.GetProperty("score").GetInt32());
        Assert.True(expected.Score > 6_500, $"a perfect run scores {expected.Score}");
        var rewards = body.GetProperty("rewards");
        Assert.Equal(150, rewards.GetProperty("coins").GetInt64());
        Assert.Equal(80, rewards.GetProperty("xp").GetInt32());
        Assert.Equal(JsonValueKind.Null, rewards.GetProperty("noCoinsReason").ValueKind);

        var session = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/sessions/{run.SessionId}"), HttpStatusCode.OK, "GameSession");
        Assert.Equal("street-juggle", session.GetProperty("gameId").GetString());
        Assert.Equal(150, (await player.MeAsync()).GetProperty("walletBalance").GetInt64());

        // Daily quests count reward-eligible runs of every game (design §5).
        var quests = await QuestTests.ListAsync(player);
        Assert.Equal(1, QuestTests.Quest(quests, "play_3").GetProperty("progress").GetInt32());
        Assert.Equal("completed", QuestTests.Quest(quests, "score_1500").GetProperty("status").GetString());
        Assert.Equal("completed", QuestTests.Quest(quests, "challenge_or_streak").GetProperty("status").GetString());

        await using var db = api.CreateDbContext();
        var stored = await db.GameSessions.AsNoTracking().SingleAsync(s => s.Id == run.SessionId);
        Assert.Equal(trace.Count, stored.EventCount);
        Assert.Equal([ReviewFlags.PerfectRateHigh], stored.ReviewFlags);
    }

    public static TheoryData<string, string> TamperedTraces() => new()
    {
        { "lane_out_of_range", "out_of_range" },
        { "street_hoops_event", "invalid_event" },
        { "fractional_time", "invalid_event" },
        { "extra_field", "invalid_event" },
        { "equal_times", "non_monotonic" },
        { "too_fast", "cadence_violation" },
        { "time_travel", "submitted_too_early" },
    };

    [Theory]
    [MemberData(nameof(TamperedTraces))]
    public async Task TamperedTrace_IsRejected_WithoutRewards(string tampering, string reason)
    {
        var player = await Player.CreateGuestAsync(api);
        var run = await StartAsync(player);
        var trace = JuggleBot.PerfectTrace(run.Config, run.Seed);
        var first = trace[0];
        var second = trace[1];
        object[] events = tampering switch
        {
            "lane_out_of_range" => [new { tMs = first.TMs, lane = 3 }],
            "street_hoops_event" => [new { tMs = first.TMs, aimX = 0, power = 500 }],
            "fractional_time" => [JsonDocument.Parse($$"""{"tMs":{{first.TMs}}.0,"lane":{{first.Lane}}}""").RootElement],
            "extra_field" => [new { tMs = first.TMs, lane = first.Lane, points = 150 }],
            "equal_times" => [new { tMs = first.TMs, lane = first.Lane }, new { tMs = first.TMs, lane = second.Lane }],
            "too_fast" => [new { tMs = first.TMs, lane = first.Lane }, new { tMs = first.TMs + 100, lane = second.Lane }],
            _ => Events(trace), // the whole 60 s trace, 10 s after the start
        };
        api.Clock.Advance(TimeSpan.FromSeconds(tampering == "time_travel" ? 10 : 61));

        var body = await Contract.AssertAsync(await player.SubmitAsync(run.SessionId, run.RulesetId, events), HttpStatusCode.OK, "SubmitResult");

        Assert.Equal("rejected", body.GetProperty("status").GetString());
        Assert.Equal(reason, body.GetProperty("rejectionReason").GetString());
        Assert.Equal(0, (await player.MeAsync()).GetProperty("walletBalance").GetInt64());
    }

    [Fact]
    public async Task ShortRun_IsVerified_WithNoCoinsAndRunTooShort()
    {
        var player = await Player.CreateGuestAsync(api);
        var run = await StartAsync(player);
        var trace = JuggleBot.PerfectTrace(run.Config, run.Seed).Take(5).ToList();
        api.Clock.Advance(TimeSpan.FromMilliseconds(trace[^1].TMs + 1_000)); // a legal trace, but 50 s too early for rewards

        var body = await Contract.AssertAsync(await player.SubmitAsync(run.SessionId, run.RulesetId, Events(trace)), HttpStatusCode.OK, "SubmitResult");

        Assert.Equal("verified", body.GetProperty("status").GetString());
        Assert.Equal(Replay(run, trace, trace[^1].TMs + 1_000).Score, body.GetProperty("score").GetInt32());
        var rewards = body.GetProperty("rewards");
        Assert.Equal(0, rewards.GetProperty("coins").GetInt64());
        Assert.Equal(0, rewards.GetProperty("xp").GetInt32());
        Assert.Equal("run_too_short", rewards.GetProperty("noCoinsReason").GetString());
        await Contract.AssertProblemAsync(await ChallengeTests.CreateAsync(player, run.SessionId, null), HttpStatusCode.UnprocessableEntity, "session_not_eligible");
    }

    [Fact]
    public async Task FourTouches_FullLength_IsVerified_WithTooFewShots()
    {
        var player = await Player.CreateGuestAsync(api);
        var run = await StartAsync(player);
        var trace = JuggleBot.PerfectTrace(run.Config, run.Seed).Take(run.Config.MinEventsForRewards - 1).ToList();
        api.Clock.Advance(TimeSpan.FromMilliseconds(run.Config.DurationMs));

        var body = await Contract.AssertAsync(await player.SubmitAsync(run.SessionId, run.RulesetId, Events(trace)), HttpStatusCode.OK, "SubmitResult");

        Assert.Equal("too_few_shots", body.GetProperty("rewards").GetProperty("noCoinsReason").GetString());
    }

    [Fact]
    public async Task Challenge_IsPlayedInItsOwnGame()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var creatorRun = await StartAsync(creator);
        api.Clock.Advance(TimeSpan.FromMilliseconds(creatorRun.Config.DurationMs + 100));
        var creatorTrace = JuggleBot.PerfectTrace(creatorRun.Config, creatorRun.Seed);
        await Contract.AssertAsync(await creator.SubmitAsync(creatorRun.SessionId, creatorRun.RulesetId, Events(creatorTrace)), HttpStatusCode.OK, "SubmitResult");
        var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, creatorRun.SessionId);
        await Contract.AssertAsync(await ChallengeTests.AcceptAsync(recipient, token), HttpStatusCode.OK, "ChallengeAccepted");

        // The Street Hoops route does not open a Street Juggle challenge.
        await Contract.AssertProblemAsync(
            await recipient.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { challengeId }), HttpStatusCode.NotFound, "challenge_unavailable");

        var recipientRun = await StartAsync(recipient, challengeId);
        Assert.Equal(creatorRun.Seed, recipientRun.Seed);
        Assert.Equal(creatorRun.RulesetId, recipientRun.RulesetId);
        api.Clock.Advance(TimeSpan.FromMilliseconds(recipientRun.Config.DurationMs + 100));
        var recipientTrace = JuggleBot.PerfectTrace(recipientRun.Config, recipientRun.Seed).Take(10).ToList();
        var result = await Contract.AssertAsync(
            await recipient.SubmitAsync(recipientRun.SessionId, recipientRun.RulesetId, Events(recipientTrace)), HttpStatusCode.OK, "SubmitResult");
        Assert.Equal("verified", result.GetProperty("status").GetString());

        var challenge = await Contract.AssertAsync(await creator.Http.GetAsync($"/api/v1/challenges/{challengeId}"), HttpStatusCode.OK, "Challenge");
        Assert.Equal("completed", challenge.GetProperty("status").GetString());
        Assert.Equal("street-juggle", challenge.GetProperty("gameId").GetString());
    }

    [Fact]
    public async Task StreetHoopsChallenge_DoesNotOpenAStreetJuggleSession()
    {
        var creator = await Player.CreateGuestAsync(api);
        var recipient = await Player.CreateGuestAsync(api);
        var run = await creator.PlayPerfectRunAsync();
        var (challengeId, token) = await ChallengeTests.CreateOkAsync(creator, run.GetProperty("sessionId").GetGuid());
        await Contract.AssertAsync(await ChallengeTests.AcceptAsync(recipient, token), HttpStatusCode.OK, "ChallengeAccepted");

        await Contract.AssertProblemAsync(
            await recipient.Http.PostAsJsonAsync("/api/v1/games/street-juggle/sessions", new { challengeId }), HttpStatusCode.NotFound, "challenge_unavailable");
        // The challenge stays playable in its own game.
        await Contract.AssertAsync(
            await recipient.Http.PostAsJsonAsync("/api/v1/games/street-hoops/sessions", new { challengeId }), HttpStatusCode.Created, "SessionCreated");
    }

    [Fact]
    public void ForJuggle_FlagsOnlyAnAlmostPerfectRun()
    {
        static List<JuggleTouchResult> Touches(int count, int perfects) => Enumerable.Range(0, count)
            .Select(i => new JuggleTouchResult(i * 1000, 1, 1, 28_000, i < perfects ? JuggleTouchKind.Perfect : JuggleTouchKind.Good, 150, i + 1))
            .ToList();

        Assert.Empty(ReviewFlags.ForJuggle(Touches(29, 29)));
        Assert.Empty(ReviewFlags.ForJuggle(Touches(40, 36)));
        Assert.Equal([ReviewFlags.PerfectRateHigh], ReviewFlags.ForJuggle(Touches(40, 37)));
    }

    private sealed record JuggleRun(Guid SessionId, long Seed, StreetJuggleConfig Config, string RulesetId);

    private static async Task<JuggleRun> StartAsync(Player player, Guid? challengeId = null)
    {
        var created = await Contract.AssertAsync(
            await player.Http.PostAsJsonAsync("/api/v1/games/street-juggle/sessions", challengeId is null ? new { } : (object)new { challengeId }),
            HttpStatusCode.Created, "SessionCreated");
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var rulesetId = created.GetProperty("rulesetId").GetString()!;
        var ruleset = await Contract.AssertAsync(await player.Http.GetAsync($"/api/v1/games/rulesets/{rulesetId}"), HttpStatusCode.OK, "Ruleset");
        Assert.Equal("street-juggle", ruleset.GetProperty("gameId").GetString());
        var config = ruleset.GetProperty("config").Deserialize<StreetJuggleConfig>(Web)!;
        var started = await Contract.AssertAsync(
            await player.Http.PostAsync($"/api/v1/games/sessions/{sessionId}/start", null), HttpStatusCode.OK, "SessionStarted");
        return new JuggleRun(sessionId, started.GetProperty("seed").GetInt64(), config, rulesetId);
    }

    private static object[] Events(IEnumerable<JuggleTouch> trace) => trace.Select(t => (object)new { tMs = t.TMs, lane = t.Lane }).ToArray();

    private static JuggleEvaluateResult Replay(JuggleRun run, IReadOnlyList<JuggleTouch> trace, long submittedAtMs = 60_100) =>
        StreetJuggleSimulator.Evaluate(new JuggleEvaluateInput(
            run.Config, run.RulesetId, run.Seed, new SessionTiming(SessionStatus.Active, 0, 65_000),
            new JuggleSubmission(run.RulesetId, submittedAtMs, trace)));
}

/// <summary>A scripted Street Juggle player for test data (the C# twin of packages/game-rules/src/juggle-autoplay.ts, without errors).</summary>
internal static class JuggleBot
{
    /// <summary>Touches every ball at the tick where it is nearest to <c>KickY</c> while it falls.</summary>
    public static List<JuggleTouch> PerfectTrace(StreetJuggleConfig config, long seed)
    {
        var sim = new StreetJuggleSim(config, seed);
        var events = new List<JuggleTouch>();
        int? planned = null;
        for (var k = 0; k < sim.TotalTicks; k++)
        {
            var ball = sim.Ball;
            if (!ball.InPlay)
            {
                planned = null;
            }
            else if (planned is null && events.Count < config.MaxEvents)
            {
                planned = IdealTick(config, k, ball.Y, ball.Vy);
            }

            if (planned == k)
            {
                var touch = new JuggleTouch(k * config.TickMs, ball.Lane);
                sim.Touch(touch);
                events.Add(touch);
                planned = null;
            }
            else
            {
                sim.AdvanceTo(k + 1);
            }
        }

        return events;
    }

    private static int? IdealTick(StreetJuggleConfig config, int from, long y, long vy)
    {
        int? best = null;
        var bestDy = long.MaxValue;
        for (var j = from; y > 0; j++)
        {
            var dy = Math.Abs(y - config.KickY);
            if (vy <= 0 && dy <= config.GoodDy && dy < bestDy)
            {
                best = j;
                bestDy = dy;
            }

            vy -= StreetJuggleSimulator.GravityAt(config, j);
            y += vy;
        }

        return best;
    }
}
