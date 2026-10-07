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

        // Half brakes, well short of the stall (about 80 % of the travel).
        sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 0.5f;
        Average(sim, 12); // past the zoom: slowing down, it climbs for a while
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

        Assert.True(sim.PilotPosition.Length() < 10, $"local {sim.PilotPosition}");
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
        // A fabric wing folds along the span (the tips come forward and together), not just pitches like a rigid one. (The
        // outer sections, lifting up to their stall at about 18°, hold the tips out a little through the fold.)
        Assert.True(narrowest < width * 0.8f, $"narrowest {narrowest} of {width}");
    }

    [Fact]
    public void FullStall_SoftensTheCanopy_AndItRecovers()
    {
        var sim = Simulator();
        Average(sim, 10);
        Ramp(sim, b => sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = b);
        Average(sim, 5);
        // The wing is back and the flow comes from below and behind, across the inlets: the cells lose most of their air.
        Assert.True(sim.CellPressure.ToArray().Average() < 0.2f, "the stalled cells lose pressure");
        Assert.True(Enumerable.Range(0, sim.Model.Strips.Count).Where(s => sim.Model.Strips[s].HasInlet).Average(s => sim.StripInletFacing(s)) > 60);

        Ramp(sim, b => sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 1 - b);
        Average(sim, 10);
        var (airspeed, sink) = Average(sim, 6);
        Assert.InRange(sim.CenterAngleOfAttack, 3, 15);
        Assert.InRange(airspeed * 3.6f, 25, 50);
        Assert.InRange(sink, 0.5f, 3f);
    }

    [Theory]
    [InlineData(ProxyComplexity.Medium)]
    [InlineData(ProxyComplexity.High)]
    public void UpperAndLowerSkin_DontGetStuckThroughEachOther(ProxyComplexity complexity)
    {
        // A turn and its exit used to flip the tip section (upper skin under the lower one), and the pressure on the
        // inverted cell kept it there.
        var sim = Simulator(complexity);
        Average(sim, 10);
        int frames = 0, crossed = 0;
        void Fly(float seconds)
        {
            for (int i = 0; i < seconds * 60; i++)
            {
                sim.Step(1 / 60f);
                frames++;
                if (sim.SurfaceCrossings > 0) crossed++;
            }
        }
        Ramp(sim, b => sim.Inputs.BrakeLeft = 0.5f * b);
        Fly(12);
        sim.Inputs.BrakeLeft = 0;
        Fly(8);

        Assert.Equal(0, sim.SurfaceCrossings);
        Assert.True(crossed < frames / 50, $"crossed in {crossed} of {frames} frames");
    }

    [Fact]
    public void SuddenBrakeInput_PitchesTheWingBack_WithoutStallingIt()
    {
        // A step to 50 % brakes (a slider, a trigger) used to yank the light canopy back into a stall and a spin; pitch
        // damping and the hand speed keep it a pitch-back and a surge.
        var sim = Simulator();
        Average(sim, 12);
        float heading = MathF.Atan2(sim.PilotVelocity.X, sim.PilotVelocity.Z);
        float maxAoa = 0, maxSink = 0;
        sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 0.5f;
        for (int i = 0; i < 90; i++)
        {
            sim.Step(1 / 60f);
            maxAoa = MathF.Max(maxAoa, sim.CenterAngleOfAttack);
        }
        sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = 0;
        for (int i = 0; i < 360; i++)
        {
            sim.Step(1 / 60f);
            maxAoa = MathF.Max(maxAoa, sim.CenterAngleOfAttack);
            maxSink = MathF.Max(maxSink, -sim.VerticalSpeed);
        }
        float turned = MathF.Abs(MathF.Atan2(sim.PilotVelocity.X, sim.PilotVelocity.Z) - heading) * 180 / MathF.PI;

        Assert.True(maxAoa < 22, $"max angle of attack {maxAoa}");
        Assert.True(maxSink < 5, $"max sink {maxSink}");
        Assert.True(turned < 20, $"turned {turned}°");
    }

    public static TheoryData<WingClass> Classes() => [.. GliderPresets.Classes];

    [Theory]
    [MemberData(nameof(Classes))]
    public void EveryClass_FliesThroughMostOfItsBrakeTravel_AndStallsBeforeItsEnd(WingClass wingClass)
    {
        // Calibrated to stall at about 80 % of the travel (EN-A 64 cm … EN-D 48 cm), past the certification minimums. A wing
        // that stalled early (the brakes pitching a rigid profile up instead of curling the trailing edge) spun at 60 %.
        var design = GliderPresets.Create(wingClass, MeshDetail.LowPoly);
        design.ProxyComplexity = ProxyComplexity.Medium;
        var sim = new GliderSimulator(GliderGenerator.Generate(design, new GenerateOptions(-1)).Proxy.Model);
        Average(sim, 8);
        float maxAoa = 0, maxSink = 0;
        void Fly(float from, float to, float seconds)
        {
            for (int i = 0; i < seconds * 60; i++)
            {
                sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = from + (to - from) * Math.Min(1, i / (seconds * 30));
                sim.Step(1 / 60f);
                maxAoa = MathF.Max(maxAoa, sim.CenterAngleOfAttack);
                maxSink = MathF.Max(maxSink, -sim.VerticalSpeed);
            }
        }
        Fly(0, 0.5f, 12);
        Fly(0.5f, 0.7f, 16);
        Assert.True(maxAoa < 22 && maxSink < 3, $"{wingClass} stalled before 70 %: aoa {maxAoa:0}°, sink {maxSink:0.0} m/s");
        Fly(0.7f, 1, 12);
        Assert.True(maxAoa > 22 || maxSink > 3.5f, $"{wingClass} didn't stall: aoa {maxAoa:0}°, sink {maxSink:0.0} m/s");
    }

    [Fact]
    public void DefaultWing_FliesTheEnBReferencePolar()
    {
        // Progression EN-B reference (Flybubble): trim 36 km/h at 1.11 m/s sink (glide 9), top speed 48 km/h at 1.90 m/s
        // (glide 7), averaged over 30 s.
        var sim = Simulator();
        Average(sim, 15);
        var (trimSpeed, trimSink) = Average(sim, 30);
        sim.Inputs.SpeedBar = 1;
        Average(sim, 10);
        var (topSpeed, topSink) = Average(sim, 30);

        Assert.InRange(trimSpeed * 3.6f, 34, 39);
        Assert.InRange(trimSink, 1.0f, 1.3f);
        Assert.InRange(trimSpeed / trimSink, 7.8f, 10);
        Assert.InRange(topSpeed * 3.6f, 45, 52);
        Assert.InRange(topSink, 1.6f, 2.3f);
    }

    [Fact]
    public void HeldBrake_WindsIntoASpiralDive_AndThePilotLoadPointsOutOfTheTurn()
    {
        // At a fixed 16 substeps the loaded wing snapped its outer tip once the spiral pulled about 2 G, and it flipped
        // into a reversal or a spin. Into the turn with brake and weight shift, as a pilot enters a spiral (more brake alone
        // stalls the slow inner wing into a spinning spiral).
        var sim = Simulator();
        Average(sim, 10);
        Ramp(sim, b =>
        {
            sim.Inputs.BrakeLeft = 0.6f * b;
            sim.Inputs.WeightShift = 0.5f * b;
        });
        Average(sim, 16);
        float minLoad = float.MaxValue, minSink = float.MaxValue;
        for (int i = 0; i < 10 * 60; i++)
        {
            var before = sim.PilotVelocity;
            sim.Step(1 / 60f);
            var v = sim.PilotVelocity;
            // Turning left: the velocity keeps turning to the pilot's left (+X of the flight direction).
            var left = new Vector3(before.Z, 0, -before.X);
            Assert.True(Vector3.Dot(v - before, left) > 0, $"the turn reversed at {sim.Time:0.0} s");
            Assert.True(Vector3.Dot(sim.PilotLoad, left) < 0, "the load points out of the turn");
            minLoad = MathF.Min(minLoad, sim.LoadFactor);
            minSink = MathF.Min(minSink, -v.Y);
        }
        Assert.True(minLoad > 2.5f, $"{minLoad:0.0} G");
        Assert.True(minSink > 10, $"{minSink:0.0} m/s");
    }

    [Fact]
    public void InletPressure_FollowsHowTheInletFacesTheFlow()
    {
        var sim = Simulator();
        float closing = sim.Model.InletClosingAlpha;
        Assert.Equal(1, sim.InletPressure(8, 0));
        Assert.Equal(1, sim.InletPressure(8, 30)); // within the capture cone
        Assert.Equal(0.5f, sim.InletPressure(8, 60), 3);
        Assert.Equal(0, sim.InletPressure(8, 90), 3); // the flow passes across the inlet
        Assert.Equal(-0.25f, sim.InletPressure(8, 150)); // from behind: sucked empty
        Assert.Equal(-0.25f, sim.InletPressure(closing - 4, 10)); // the flow over the nose closes it however it faces

        // In trim every inlet takes in the flow (the tips, arced and twisted in flight, face it least squarely).
        Average(sim, 10);
        Assert.All(Enumerable.Range(0, sim.Model.Strips.Count).Where(s => sim.Model.Strips[s].HasInlet), s => Assert.InRange(sim.StripInletFacing(s), 0, 25));
    }

    [Fact]
    public void RecordedForces_CarryTheWeight_InASteadyGlide()
    {
        var sim = Simulator();
        sim.RecordForces = true;
        Average(sim, 10);

        Vector3 Sum(ReadOnlySpan<Vector3> forces)
        {
            var sum = Vector3.Zero;
            foreach (var force in forces) sum += force;
            return sum;
        }
        var total = Vector3.Zero;
        var stripLift = Vector3.Zero;
        var nodeLift = Vector3.Zero;
        const int steps = 120;
        for (int i = 0; i < steps; i++)
        {
            sim.Step(1 / 60f);
            total += (Sum(sim.NodeLift) + Sum(sim.NodeDrag) + Sum(sim.NodeParasiticDrag)) / steps;
            stripLift += Sum(sim.StripLift) / steps;
            nodeLift += Sum(sim.NodeLift) / steps;
        }

        float weight = sim.Model.Nodes.Sum(n => n.Mass) * 9.81f;
        Assert.InRange(total.Y / weight, 0.95f, 1.05f);
        Assert.True(MathF.Sqrt(total.X * total.X + total.Z * total.Z) < 0.05f * weight, $"{total}");
        Assert.True(Vector3.Distance(stripLift, nodeLift) < 1e-3f * weight, $"{stripLift} vs {nodeLift}");
        Assert.All(Enumerable.Range(0, sim.Model.Strips.Count), s => Assert.InRange(sim.StripCenterOfPressure(s), 0.05f, 0.9f));
    }

    [Fact]
    public void RecordingForces_DoesNotChangeTheFlight()
    {
        var plain = Simulator();
        var recording = Simulator();
        recording.RecordForces = true;
        for (int i = 0; i < 120; i++)
        {
            plain.Step(1 / 60f);
            recording.Step(1 / 60f);
        }
        Assert.Equal(plain.Positions.ToArray(), recording.Positions.ToArray());
        Assert.Equal(0, plain.NodeLift.Length);
    }

    [Fact]
    public void SteadyGlide_HasASteadySinkRate()
    {
        // Positions far from the origin (16 m was allowed) made the sink wander ±10% over tens of seconds through rounding
        // in the stiff constraints; re-centering every step keeps successive windows within a few percent.
        var sim = Simulator();
        Average(sim, 15);
        var sinks = Enumerable.Range(0, 3).Select(_ => Average(sim, 10).Sink).ToArray();
        float mean = sinks.Average();
        Assert.All(sinks, s => Assert.InRange(s, mean * 0.97f, mean * 1.03f));
    }
}
