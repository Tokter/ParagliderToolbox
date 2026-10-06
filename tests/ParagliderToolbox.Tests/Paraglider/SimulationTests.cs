using System.Numerics;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Simulation;

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
        Assert.True(fast > trimSpeed + 1.0f, $"{fast} vs {trimSpeed}");
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
}
