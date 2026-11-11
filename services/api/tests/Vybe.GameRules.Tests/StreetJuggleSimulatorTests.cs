using System.Text.Json;

namespace Vybe.GameRules.Tests;

/// <summary>Hand-checked Street Juggle cases (the same as packages/game-rules/test/juggle.test.ts).</summary>
public sealed class StreetJuggleSimulatorTests
{
    private static readonly StreetJuggleConfig Config = LoadConfig();

    /// <summary>Stream 54 with seed 42 gives the official pcg32-demo outputs, so the draws are checked by hand.</summary>
    private static readonly StreetJuggleConfig Demo = Config with { StreamId = 54 };

    private static StreetJuggleConfig LoadConfig()
    {
        using var document = FixtureFiles.ReadDocument("rulesets/street-juggle-v1.json");
        return document.RootElement.GetProperty("config").Deserialize<StreetJuggleConfig>(FixtureFiles.JsonOptions)!;
    }

    private static JuggleEvaluateInput Input(IReadOnlyList<JuggleTouch> events, SessionStatus status = SessionStatus.Active, long submittedAtMs = 61_500) =>
        new(Config, "street-juggle-v1", 12345, new SessionTiming(status, 1000, 66_000),
            new JuggleSubmission("street-juggle-v1", submittedAtMs, events));

    [Fact]
    public void Serve_UsesTwoDrawsAndFalls()
    {
        var sim = new StreetJuggleSim(Demo, 42);
        sim.AdvanceTo(1);
        Assert.Equal(new JuggleBall(true, 0, 89_982, -18, false), sim.Ball);
        sim.AdvanceTo(80);
        Assert.Equal(90_000 - (9 * 80 * 81), sim.Ball.Y);
    }

    [Fact]
    public void PerfectKick_LaunchesWithThreeDraws()
    {
        var sim = new StreetJuggleSim(Demo, 42);
        Assert.Equal(new JuggleTouchResult(805, 0, 0, 31_680, JuggleTouchKind.Perfect, 150, 1), sim.Touch(new JuggleTouch(805, 0)));
        Assert.Equal(new JuggleBall(true, 1, 33_264, 1584, false), sim.Ball);
    }

    [Theory]
    [InlineData(69, JuggleTouchKind.Whiff)]
    [InlineData(70, JuggleTouchKind.Good)]
    [InlineData(79, JuggleTouchKind.Good)]
    [InlineData(80, JuggleTouchKind.Perfect)]
    [InlineData(85, JuggleTouchKind.Perfect)]
    [InlineData(86, JuggleTouchKind.Good)]
    public void Touch_JudgesTheWindow(int tick, JuggleTouchKind expected) =>
        Assert.Equal(expected, new StreetJuggleSim(Demo, 42).Touch(new JuggleTouch(tick * 10, 0)).Result);

    [Fact]
    public void WrongSpot_WhiffsAndLocksWithoutExtending()
    {
        var sim = new StreetJuggleSim(Demo, 42);
        Assert.Equal(JuggleTouchKind.Whiff, sim.Touch(new JuggleTouch(800, 2)).Result);
        Assert.True(sim.IsLocked(124));
        Assert.False(sim.IsLocked(125));
        Assert.Equal(JuggleTouchKind.Whiff, sim.Touch(new JuggleTouch(850, 0)).Result);
        Assert.False(sim.IsLocked(125));
    }

    [Fact]
    public void Drop_WaitsRespawnAndServesAgain()
    {
        var sim = new StreetJuggleSim(Demo, 42);
        sim.AdvanceTo(100);
        Assert.Equal(1, sim.DropCount);
        Assert.Equal(new JuggleTouchResult(1500, 1, -1, 0, JuggleTouchKind.Whiff, 0, 0), sim.Touch(new JuggleTouch(1500, 1)));
        sim.AdvanceTo(200);
        Assert.False(sim.Ball.InPlay);
        sim.AdvanceTo(201);
        Assert.Equal(new JuggleBall(true, 2, 90_000 - 18, -18, false), sim.Ball);
    }

    [Theory]
    [InlineData(0, 18)]
    [InlineData(1199, 18)]
    [InlineData(1200, 21)]
    [InlineData(2399, 21)]
    [InlineData(2400, 24)]
    [InlineData(5999, 30)]
    public void Gravity_RisesEveryLevel(int tick, int expected) => Assert.Equal(expected, StreetJuggleSimulator.GravityAt(Config, tick));

    [Fact]
    public void Touch_BeforeSimulationTime_Throws()
    {
        var sim = new StreetJuggleSim(Config, 1);
        sim.AdvanceTo(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Touch(new JuggleTouch(990, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Touch(new JuggleTouch(60_000, 0)));
    }

    [Theory]
    [InlineData("streamId")]
    [InlineData("tickMs")]
    [InlineData("duration")]
    [InlineData("perfectDy")]
    [InlineData("serveY")]
    [InlineData("launch")]
    [InlineData("star")]
    public void ValidateConfig_RejectsBadValues(string which)
    {
        var bad = which switch
        {
            "streamId" => Config with { StreamId = 1L << 32 },
            "tickMs" => Config with { TickMs = 0 },
            "duration" => Config with { DurationMs = 60_005 },
            "perfectDy" => Config with { PerfectDy = Config.GoodDy + 1 },
            "serveY" => Config with { ServeY = 46_000 },
            "launch" => Config with { LaunchMin = Config.LaunchMax + 1 },
            _ => Config with { StarPercent = 101 },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => StreetJuggleSimulator.ValidateConfig(bad));
    }

    [Fact]
    public void Evaluate_AppliesTheCheckOrder()
    {
        Assert.Equal(RejectionReason.NotStarted, StreetJuggleSimulator.Evaluate(Input([], SessionStatus.Created)).Reason);
        Assert.Equal(Outcome.Expired, StreetJuggleSimulator.Evaluate(Input([], submittedAtMs: 66_001)).Outcome);
        Assert.Equal(RejectionReason.OutOfRange, StreetJuggleSimulator.Evaluate(Input([new(0, 3)])).Reason);
        Assert.Equal(RejectionReason.NonMonotonic, StreetJuggleSimulator.Evaluate(Input([new(5, 0), new(5, 1)])).Reason);
        Assert.Equal(RejectionReason.CadenceViolation, StreetJuggleSimulator.Evaluate(Input([new(0, 0), new(149, 1)])).Reason);
        Assert.Equal(3, StreetJuggleSimulator.Evaluate(Input([new(0, 0), new(150, 1), new(59_999, 2)])).EventCount);
    }
}
