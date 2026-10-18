using System.Numerics;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Simulation;

namespace ParagliderToolbox.Tests.Paraglider;

public class LaunchTests
{
    private static GliderSimulator OnGround()
    {
        var sim = new GliderSimulator(GliderGenerator.Generate(new GliderDesign { SpanwiseSegmentsPerCell = 2, ChordwiseSegments = 20, GenerateRibs = false },
            new GenerateOptions(-1)).Proxy.Model);
        sim.ResetOnGround();
        return sim;
    }

    // The world height of a node.
    private static float Height(GliderSimulator sim, int node) => (float)(sim.Origin.Y + sim.Positions[node].Y);

    private static Vector3 CanopyMiddle(GliderSimulator sim)
    {
        var section = sim.Model.Sections[sim.Model.Sections.Count / 2];
        return (sim.Positions[section.LeadingEdge] + sim.Positions[section.TrailingEdge]) * 0.5f;
    }

    // Flies with the launch wind picking up at 0.4 m/s per second to `wind`, calling `pilot` before every step.
    private static void Fly(GliderSimulator sim, float seconds, float wind, Action<GliderSimulator>? pilot = null)
    {
        for (int i = 0; i < (int)(seconds * 60); i++)
        {
            sim.Inputs.Wind = new Vector3(0, 0, -Math.Min(wind, 0.4f * sim.Time));
            pilot?.Invoke(sim);
            sim.Step(1 / 60f);
            Assert.All(sim.Positions.ToArray(), p => Assert.True(p.Y + sim.Origin.Y > -1e-3, $"below the ground at {sim.Time:0.00} s"));
        }
    }

    [Fact]
    public void LayingOut_PutsTheEmptyWingOnTheGround_BehindTheStandingPilot()
    {
        var sim = OnGround();
        var model = sim.Model;

        Assert.Equal(SimulationStart.Ground, sim.Start);
        Assert.Equal(0, sim.Ground);
        Assert.Equal(0, sim.Time);
        Assert.True(sim.PilotOnGround);
        Assert.Equal(sim.StandingHeight, Height(sim, model.Pilot), 2);
        Assert.True(sim.PilotForward.Z > 0.98f, $"facing {sim.PilotForward}");
        Assert.InRange(sim.CanopyElevation, -15, 0);
        Assert.True(sim.CanopyBehindPilot);
        Assert.True(sim.CellPressure.ToArray().Average() < 0.1f, "the cells are empty");

        // Flat on the ground, behind the pilot, its leading edge toward them; the lines slack.
        var middle = model.Sections[model.Sections.Count / 2];
        Assert.True(sim.Positions[middle.LeadingEdge].Z > sim.Positions[middle.TrailingEdge].Z);
        Assert.True(CanopyMiddle(sim).Z < sim.PilotPosition.Z - 3);
        foreach (var section in model.Sections.Skip(2).SkipLast(2))
            Assert.True(ProxyBuilder.Chain(section, true).Max(n => Height(sim, n)) < 1, $"section at {section.Eta:0.00} lies low");
        foreach (var c in model.Constraints.Where(c => c.Kind is ConstraintKind.Line or ConstraintKind.Riser))
            Assert.True(Vector3.Distance(sim.Positions[c.A], sim.Positions[c.B]) < c.RestLength * 1.005f, c.Name);
        Assert.All(sim.Velocities.ToArray(), v => Assert.Equal(Vector3.Zero, v));
    }

    [Fact]
    public void Wind_FillsTheWing_AndLiftsItOffTheGround_WhileThePilotStands()
    {
        var sim = OnGround();
        var start = sim.PilotPosition + new Vector3((float)sim.Origin.X, 0, (float)sim.Origin.Z);
        Fly(sim, 10.5f, 4);

        Assert.True(sim.CellPressure.ToArray().Average() > 0.5f, $"pressure {sim.CellPressure.ToArray().Average():0.00}");
        Assert.InRange(sim.CanopyElevation, 10, 50);
        Assert.True(sim.PilotOnGround);
        var now = sim.PilotPosition + new Vector3((float)sim.Origin.X, 0, (float)sim.Origin.Z);
        Assert.True(Vector3.Distance(now with { Y = 0 }, start with { Y = 0 }) < 0.3f, "the feet hold");
    }

    [Fact]
    public void PullingTheARisers_BringsTheWingUp()
    {
        // The wind picking up (3.2 m/s after 8 s) has half filled the wing, which lies low.
        var pulled = OnGround();
        var left = OnGround();
        Fly(pulled, 8, 4);
        Fly(left, 8, 4);
        Assert.InRange(pulled.CanopyElevation, -5, 15);

        Fly(pulled, 2, 4, sim => sim.Inputs.ARiserLeft = sim.Inputs.ARiserRight = 1);
        Fly(left, 2, 4);

        Assert.True(pulled.CanopyElevation > 40, $"pulled up to {pulled.CanopyElevation:0}°");
        Assert.True(left.CanopyElevation < 30, $"left alone up to {left.CanopyElevation:0}°");
        Assert.True(pulled.CellPressure.ToArray().Average() > 0.6f, "the cells fill as it rises");
    }

    [Fact]
    public void ThePilot_WalksWhereTheyWant_AndTurnsToFaceTheWing()
    {
        var sim = OnGround();
        var start = new Vector3((float)sim.Origin.X, 0, (float)sim.Origin.Z) + sim.PilotPosition;
        Fly(sim, 2, 0, s => s.Inputs.Walk = new Vector3(1.5f, 0, 0));
        var walked = new Vector3((float)sim.Origin.X, 0, (float)sim.Origin.Z) + sim.PilotPosition - start;

        // Speeding up at 3 m/s² to 1.5 m/s: 2.6 m in 2 s.
        Assert.InRange(walked.X, 2.2f, 2.9f);
        Assert.InRange(MathF.Abs(walked.Z), 0, 0.3f);
        Assert.True(sim.PilotOnGround);
        Assert.Equal(sim.StandingHeight, Height(sim, sim.Model.Pilot), 2);

        // Standing still again, they turn to face away from the wing behind them.
        Fly(sim, 4, 0, s => s.Inputs.Walk = Vector3.Zero);
        Assert.True(sim.PilotVelocity.Length() < 0.1f);
        var away = Vector3.Normalize((sim.PilotPosition - CanopyMiddle(sim)) with { Y = 0 });
        Assert.True(away.X > 0.15f, $"the wing is off to the side behind them ({away})");
        Assert.True(Vector3.Dot(sim.PilotForward, away) > 0.98f, $"facing {sim.PilotForward}, away from the wing {away}");
    }

    [Fact]
    public void ResettingForFlight_TakesTheGroundAway()
    {
        var sim = OnGround();
        sim.Reset(SimulationStart.Flight);

        Assert.Equal(SimulationStart.Flight, sim.Start);
        Assert.Null(sim.Ground);
        Assert.False(sim.PilotOnGround);
        sim.Step(1 / 60f);
        Assert.True(sim.Airspeed > 8);
    }
}
