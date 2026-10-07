using System.Numerics;
using System.Text.Json;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Simulation;

namespace ParagliderToolbox.Tests.Paraglider;

public class FlightRecordingTests
{
    private static GliderDesign Design() => new() { SpanwiseSegmentsPerCell = 2, ChordwiseSegments = 20, GenerateRibs = false };

    private static GliderSimulator Simulator(GliderDesign design) =>
        new(GliderGenerator.Generate(design, new GenerateOptions(-1)).Proxy.Model);

    // Flies a while, records a left brake turn with a collapse in it, and returns the recording and the positions seen at
    // every frame (relative to where the recording started).
    private static (FlightRecording Recording, List<Vector3[]> Seen) Fly(GliderDesign design)
    {
        var sim = Simulator(design);
        var recorder = new FlightRecorder(sim, design);
        var seen = new List<Vector3[]>();
        var start = default((double X, double Y, double Z));
        int sinceStart = 0;
        void Step()
        {
            recorder.BeforeStep();
            sim.Step(1 / 60f);
            bool frame = recorder.IsRecording && sinceStart % recorder.StepsPerFrame == 0;
            recorder.AfterStep();
            if (frame)
            {
                var shift = new Vector3((float)(sim.Origin.X - start.X), (float)(sim.Origin.Y - start.Y), (float)(sim.Origin.Z - start.Z));
                seen.Add(sim.Positions.ToArray().Select(p => p + shift).ToArray());
            }
            if (recorder.IsRecording) sinceStart++;
        }
        for (int i = 0; i < 120; i++) Step();
        sim.Inputs.BrakeLeft = 0.4f;
        for (int i = 0; i < 60; i++) Step();
        start = sim.Origin;
        recorder.Start();
        for (int i = 0; i < 90; i++)
        {
            sim.Inputs.CollapseRight = i is > 20 and < 50 ? 1 : 0;
            Step();
        }
        return (recorder.Stop(), seen);
    }

    [Fact]
    public void Recording_KeepsEveryFrame_ToAMillimeter()
    {
        var design = Design();
        var (recording, seen) = Fly(design);
        var frames = recording.Decode();

        Assert.Equal(45, recording.FrameCount);
        Assert.Equal(seen.Count, frames.Positions.Length);
        Assert.Equal(1.5f, recording.Duration + recording.FrameTime, 3);
        for (int f = 0; f < seen.Count; f++)
        {
            for (int n = 0; n < seen[f].Length; n++) Assert.True(Vector3.Distance(seen[f][n], frames.Positions[f][n]) < 1.5e-3f, $"frame {f} node {n}");
        }
        Assert.Contains(frames.Telemetry, t => t.Inputs.CollapseRight == 1);
        Assert.All(frames.Telemetry, t => Assert.Equal(0.4f, t.Inputs.BrakeLeft));
        Assert.True(frames.Telemetry[^1].Time > frames.Telemetry[0].Time);
    }

    [Fact]
    public void Recording_FliesAgain_FromItsInputs()
    {
        // The inputs from the start of the flight and the design are enough to fly it again exactly (the simulator is
        // deterministic), which is how a shared recording is examined.
        var design = Design();
        var (recording, _) = Fly(design);
        var frames = recording.Decode();

        var sim = Simulator(recording.Design!);
        var start = default((double X, double Y, double Z));
        int lastStep = recording.FirstStep + (recording.FrameCount - 1) * recording.StepsPerFrame;
        for (int step = 0; step <= lastStep; step++)
        {
            if (step == recording.FirstStep) start = sim.Origin;
            recording.InputsAt(step).ApplyTo(sim.Inputs);
            sim.Step(recording.StepTime);
            int sinceFirst = step - recording.FirstStep;
            if (sinceFirst < 0 || sinceFirst % recording.StepsPerFrame != 0) continue;
            var shift = new Vector3((float)(sim.Origin.X - start.X), (float)(sim.Origin.Y - start.Y), (float)(sim.Origin.Z - start.Z));
            var recorded = frames.Positions[sinceFirst / recording.StepsPerFrame];
            for (int n = 0; n < recorded.Length; n++) Assert.True(Vector3.Distance(sim.Positions[n] + shift, recorded[n]) < 1.5e-3f, $"step {step} node {n}");
        }
    }

    [Fact]
    public void Recording_SurvivesJson()
    {
        var (recording, _) = Fly(Design());
        string json = JsonSerializer.Serialize(recording);
        var copy = JsonSerializer.Deserialize<FlightRecording>(json)!;

        Assert.Equal(recording.FrameCount, copy.FrameCount);
        Assert.Equal(recording.Inputs, copy.Inputs);
        Assert.Equal(recording.Design!.CellCount, copy.Design!.CellCount);
        Assert.Equal(recording.Decode().Positions[^1][7], copy.Decode().Positions[^1][7]);
        Assert.DoesNotContain("\"Duration\"", json);
    }
}
