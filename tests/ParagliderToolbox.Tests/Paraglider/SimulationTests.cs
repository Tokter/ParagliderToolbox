using System.Numerics;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Simulation;
using ParagliderToolbox.Paraglider.Proxy;

namespace ParagliderToolbox.Tests.Paraglider;

public class SimulationTests
{
    private static GliderSimulator Simulator(ProxyComplexity complexity = ProxyComplexity.Medium) =>
        new(GliderGenerator.Generate(new GliderDesign { ProxyComplexity = complexity, SpanwiseSegmentsPerCell = 2, ChordwiseSegments = 20, GenerateRibs = false },
            new GenerateOptions(-1)).Proxy.Model);

    private static (float Airspeed, float Sink) Average(GliderSimulator sim, float seconds)
    {
        float airspeed = 0, sink = 0;
        int steps = (int)(seconds * 60);
        for (int i = 0; i < steps; i++)
        {
            sim.Step(1 / 60f);
            airspeed += sim.Airspeed;
            sink += -sim.VerticalSpeed;
        }
        return (airspeed / steps, sink / steps);
    }

    [Theory]
    [InlineData(ProxyComplexity.Low)]
    [InlineData(ProxyComplexity.Medium)]
    [InlineData(ProxyComplexity.High)]
    public void HandsUp_SettlesIntoASteadyGlide(ProxyComplexity complexity)
    {
        var sim = Simulator(complexity);
        Average(sim, 10);
        var (airspeed, sink) = Average(sim, 8);

        Assert.InRange(airspeed * 3.6f, 32, 45); // km/h
        Assert.InRange(sink, 1.0f, 2.0f);
        Assert.InRange(airspeed / sink, 5.5f, 10f);
        Assert.InRange(sim.CenterAngleOfAttack, 4, 12);
        Assert.All(sim.CellPressure.ToArray(), p => Assert.True(p > 0.9f));
    }

    [Fact]
    public void Brakes_SlowTheGliderDown_AndFullBrakesStallIt()
    {
        var sim = Simulator();
        Average(sim, 10);
        var (trimSpeed, _) = Average(sim, 4);

        sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 0.4f;
        Average(sim, 6);
        var (brakedSpeed, brakedSink) = Average(sim, 4);
        Assert.True(brakedSpeed < trimSpeed - 1.5f, $"{brakedSpeed} vs {trimSpeed}");
        Assert.InRange(brakedSink, 0.5f, 2.0f);

        sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 1;
        Average(sim, 4);
        var (_, stallSink) = Average(sim, 3);
        Assert.True(stallSink > 4, $"sink {stallSink}");
        Assert.True(sim.CenterAngleOfAttack > 40, $"aoa {sim.CenterAngleOfAttack}");
    }

    [Fact]
    public void SpeedBar_MakesItFaster()
    {
        var sim = Simulator();
        Average(sim, 10);
        var (trimSpeed, _) = Average(sim, 4);
        sim.Inputs.SpeedBar = 1;
        Average(sim, 6);
        var (fast, _) = Average(sim, 4);
        // About 15 km/h more with 12 cm of speed bar travel, as on an EN-B wing.
        Assert.True(fast > trimSpeed + 10 / 3.6f, $"{fast * 3.6f} vs {trimSpeed * 3.6f} km/h");
    }

    [Fact]
    public void FrontalCollapse_DeflatesTheCells_AndTheWingRecovers()
    {
        var sim = Simulator();
        Average(sim, 10);
        float lowest = 1;
        sim.Inputs.Frontal = 1;
        for (int i = 0; i < 90; i++)
        {
            if (i == 36) sim.Inputs.Frontal = 0;
            sim.Step(1 / 60f);
            lowest = Math.Min(lowest, sim.CellPressure.ToArray().Average());
        }
        Assert.True(lowest < 0.6f, $"the cells deflate (lowest average pressure {lowest})");

        Average(sim, 12);
        var (airspeed, sink) = Average(sim, 10); // over a whole pitch oscillation
        Assert.True(sim.CellPressure.ToArray().Average() > 0.9f, "the cells refill");
        Assert.InRange(sink, 0.5f, 3.5f);
        Assert.InRange(airspeed * 3.6f, 20, 55);
    }


    [Fact]
    public void Simulation_StaysNearTheOrigin_AndTracksTheWorldPosition()
    {
        var sim = Simulator(ProxyComplexity.Arcade);
        Average(sim, 30);

        Assert.True(sim.PilotPosition.Length() < 40, $"local {sim.PilotPosition}");
        Assert.True(sim.Origin.Z > 200, $"flew {sim.Origin.Z} m forward");
        Assert.All(sim.Positions.ToArray(), p => Assert.False(float.IsNaN(p.X)));
    }

    // Pulls an input in over a third of a second, as the app does.
    private static void Ramp(GliderSimulator sim, Action<float> set)
    {
        for (int i = 1; i <= 20; i++)
        {
            set(i / 20f);
            sim.Step(1 / 60f);
        }
    }

    private static float CanopyWidth(GliderSimulator sim)
    {
        var sections = sim.Model.Sections;
        return Vector3.Distance(sim.Positions[sections[0].LeadingEdge], sim.Positions[sections[^1].LeadingEdge]);
    }

    [Fact]
    public void BrakeHandles_MoveDownWithTheBrakes()
    {
        var sim = Simulator();
        Average(sim, 6);
        int toggle = sim.Model.FindNode("Toggle_L")!.Id;
        float rest = sim.Positions[toggle].Y - sim.PilotPosition.Y;

        Ramp(sim, b => sim.Inputs.BrakeLeft = b * 0.6f);
        Average(sim, 2);
        float pulled = sim.Positions[toggle].Y - sim.PilotPosition.Y;

        // Slack plus 60 % of the travel, mostly downward.
        Assert.True(rest - pulled > 0.3f, $"the toggle moved {rest - pulled:F2} m down");
    }

    [Fact]
    public void BrakeLines_HangSlackHandsUp_AndBowBack()
    {
        var sim = Simulator();
        Average(sim, 8);
        var model = sim.Model;
        // Hands up, the brake lines from each trailing edge tab down to the pulley are longer than the straight distance:
        // they hang slack and bow back in the airflow.
        int pulley = model.FindNode("Pulley_L")!.Id;
        var brake = model.Constraints.Where(c => c.Kind == ConstraintKind.Line && c.Name is { } n && n.StartsWith("Brake")).ToList();
        var tabs = brake.SelectMany(c => new[] { c.A, c.B }).Where(i => model.Nodes[i].Section >= 0 && model.Nodes[i].Side > 0).Distinct().ToList();
        Assert.NotEmpty(tabs);
        foreach (int tab in tabs)
        {
            // Down the cascade: at each node the line toward the pulley (its other end is nearer the pulley at rest).
            float path = 0;
            int node = tab;
            for (int guard = 0; node != pulley && guard < 20; guard++)
            {
                float Rest(int i) => Vector3.Distance(model.Nodes[i].Position, model.Nodes[pulley].Position);
                var down = brake.Where(c => c.A == node || c.B == node).OrderBy(c => Rest(c.A == node ? c.B : c.A)).First();
                int next = down.A == node ? down.B : down.A;
                path += Vector3.Distance(sim.Positions[node], sim.Positions[next]);
                node = next;
            }
            float straight = Vector3.Distance(sim.Positions[tab], sim.Positions[pulley]);
            Assert.True(path > straight + 0.05f, $"{model.Nodes[tab].Name}: path {path:F2} m, straight {straight:F2} m");
        }
    }

    [Fact]
    public void AsymmetricCollapse_FoldsOneSide_AndTheWingRecovers()
    {
        var sim = Simulator();
        Average(sim, 10);
        float width = CanopyWidth(sim);

        Ramp(sim, c => sim.Inputs.CollapseLeft = c);
        Average(sim, 0.5f);
        var pressure = sim.CellPressure.ToArray();
        // The left (positive η) outer cells are empty, the right ones full, and the span is shorter.
        Assert.True(pressure[^1] < 0.3f, $"left tip {pressure[^1]}");
        Assert.True(pressure[0] > 0.8f, $"right tip {pressure[0]}");
        Assert.True(CanopyWidth(sim) < width - 1, $"width {CanopyWidth(sim)} vs {width}");

        sim.Inputs.CollapseLeft = 0;
        Average(sim, 10);
        Assert.True(sim.CellPressure.ToArray().Average() > 0.9f, "the cells refill");
        Assert.True(CanopyWidth(sim) > width - 0.5f, "the wing opens again");
    }

    [Fact]
    public void BigEars_FoldTheTips_AndSinkFaster()
    {
        var sim = Simulator();
        Average(sim, 10);
        var (_, trimSink) = Average(sim, 4);
        float width = CanopyWidth(sim);

        Ramp(sim, e => sim.Inputs.BigEars = e);
        Average(sim, 4);
        var (_, earsSink) = Average(sim, 4);
        var pressure = sim.CellPressure.ToArray();

        Assert.True(pressure[0] < 0.3f && pressure[^1] < 0.3f, "both tips are empty");
        Assert.True(pressure[pressure.Length / 2] > 0.9f, "the center flies on");
        Assert.True(CanopyWidth(sim) < width - 1, $"width {CanopyWidth(sim)} vs {width}");
        Assert.True(earsSink > trimSink + 0.3f, $"sink {earsSink} vs {trimSink}");
    }

    [Fact]
    public void FrontalCollapse_FoldsTheSpan()
    {
        var sim = Simulator();
        Average(sim, 10);
        float width = CanopyWidth(sim), narrowest = width;
        Ramp(sim, f => sim.Inputs.Frontal = f);
        for (int i = 0; i < 90; i++)
        {
            if (i == 20) sim.Inputs.Frontal = 0;
            sim.Step(1 / 60f);
            narrowest = Math.Min(narrowest, CanopyWidth(sim));
        }
        // A fabric wing folds along the span (the tips come forward and together), not just pitches like a rigid one.
        Assert.True(narrowest < width * 0.75f, $"narrowest {narrowest} of {width}");
    }

    [Fact]
    public void FullStall_SoftensTheCanopy_AndItRecovers()
    {
        var sim = Simulator();
        Average(sim, 10);
        Ramp(sim, b => sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = b);
        Average(sim, 5);
        Assert.True(sim.CellPressure.ToArray().Average() < 0.6f, "the stalled cells lose pressure");

        Ramp(sim, b => sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 1 - b);
        Average(sim, 10);
        var (airspeed, sink) = Average(sim, 6);
        Assert.InRange(sim.CenterAngleOfAttack, 3, 15);
        Assert.InRange(airspeed * 3.6f, 25, 50);
        Assert.InRange(sink, 0.5f, 3f);
    }
}
