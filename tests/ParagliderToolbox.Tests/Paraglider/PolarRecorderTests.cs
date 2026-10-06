using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Polar;

namespace ParagliderToolbox.Tests.Paraglider;

public class PolarRecorderTests
{
    // Short timings keep the tests quick; the default settings settle and measure longer.
    private static readonly PolarRecorderSettings Quick = new()
    {
        SpeedBarSteps = [0.5f, 1f],
        BrakeSteps = [0.25f, 0.5f, 0.75f, 1f],
        StartSeconds = 8,
        SettleSeconds = 6,
        MeasureSeconds = 6,
    };

    private static readonly Lazy<PolarRecording> Recording = new(() =>
        PolarRecorder.Record(new GliderDesign()));   // the default settings: they settle long enough for steady points

    [Fact]
    public void FitPolynomial_RecoversACubic()
    {
        double[] x = [1, 2, 3, 4, 5, 6];
        double[] y = x.Select(v => 2 - 0.5 * v + 0.25 * v * v - 0.01 * v * v * v).ToArray();
        var c = PolarRecorder.FitPolynomial(x, y, 3);
        Assert.Equal(2, c[0], 6);
        Assert.Equal(-0.5, c[1], 6);
        Assert.Equal(0.25, c[2], 6);
        Assert.Equal(-0.01, c[3], 6);
    }

    [Fact]
    public void Summary_FindsMinSinkAndBestGlide_OnTheFittedCurve()
    {
        // A parabolic polar: sink 1 m/s at 8 m/s, rising away from it.
        var points = Enumerable.Range(0, 7).Select(i =>
        {
            float v = 7 + i;
            float s = 1 + 0.05f * (v - 8) * (v - 8);
            return new PolarPoint { Airspeed = v, HorizontalSpeed = v, SinkRate = s, GlideRatio = v / s, IsStable = true, Brake = i == 1 ? 0 : 0.1f };
        }).ToList();
        points.Add(new PolarPoint { Airspeed = 6, SinkRate = 3, Brake = 0.8f, IsStalled = true });

        var summary = PolarRecorder.Summarize(points);

        Assert.Equal(8, summary.MinSink.Airspeed, 1);
        Assert.Equal(1, summary.MinSink.SinkRate, 2);
        // Tangent from the origin to s = 1 + 0.05 (v − 8)²: v = √(8² + 1/0.05) ≈ 9.17.
        Assert.Equal(9.17, summary.BestGlide.Airspeed, 1);
        Assert.True(summary.BestGlide.GlideRatio > summary.MinSink.GlideRatio);
        Assert.Equal(7, summary.MinSpeed.Airspeed);
        Assert.Equal(0.8f, summary.StallBrake);
    }

    [Fact]
    public void Recording_FliesTheWholeRange_AndOrdersTheKeyFigures()
    {
        var recording = Recording.Value;
        var s = recording.Summary;

        Assert.True(recording.IsComplete);
        Assert.Equal("Trim", recording.Points[0].Label);
        Assert.True(recording.Samples.Count > 100);
        Assert.True(recording.Samples.Zip(recording.Samples.Skip(1)).All(p => p.Second.Time > p.First.Time));
        Assert.True(s.FullSpeed.Airspeed > s.Trim.Airspeed, $"{s.FullSpeed} vs {s.Trim}");
        Assert.True(s.MinSpeed.Airspeed < s.Trim.Airspeed);
        Assert.True(s.MinSink.SinkRate <= s.Trim.SinkRate + 0.05f);
        Assert.True(s.MaxSink.SinkRate >= s.Trim.SinkRate);
        Assert.InRange(s.BestGlide.GlideRatio, 4, 12);
        Assert.NotNull(recording.Design);
    }

    [Fact]
    public void BrakeSweep_StopsAtTheStall()
    {
        var points = Recording.Value.Points;
        var stall = points.FindIndex(p => p.IsStalled && p.Brake > 0);
        Assert.True(stall > 0, "the glider stalls before full brakes run out");
        Assert.Equal(points.Count - 1, stall);
        Assert.Equal(points[stall].Brake, Recording.Value.Summary.StallBrake);
    }

    [Fact]
    public void Recording_IsDeterministic()
    {
        var settings = Quick with { SpeedBarSteps = [1f], BrakeSteps = [0.3f], StartSeconds = 3, SettleSeconds = 2, MeasureSeconds = 2 };
        var design = new GliderDesign { ProxyComplexity = ProxyComplexity.Arcade };
        var a = PolarRecorder.Record(design, settings);
        var b = PolarRecorder.Record(design, settings);
        Assert.Equal(a.Points.Select(p => p.SinkRate), b.Points.Select(p => p.SinkRate));
    }

    [Fact]
    public void Cancelling_StopsEarly_WithAnIncompleteRecording()
    {
        using var cancellation = new CancellationTokenSource();
        int reports = 0;
        var progress = new Progress<PolarProgress>(_ => { if (++reports > 3) cancellation.Cancel(); });
        var recording = PolarRecorder.Record(new GliderDesign { ProxyComplexity = ProxyComplexity.Arcade }, Quick,
            new SynchronousProgress(p => { if (++reports > 20) cancellation.Cancel(); }), cancellation.Token);
        Assert.False(recording.IsComplete);
        Assert.True(recording.Points.Count < 2 + Quick.SpeedBarSteps.Length + Quick.BrakeSteps.Length);
        _ = progress;
    }

    private sealed class SynchronousProgress(Action<PolarProgress> report) : IProgress<PolarProgress>
    {
        public void Report(PolarProgress value) => report(value);
    }
}
