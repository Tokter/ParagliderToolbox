using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Simulation;

namespace ParagliderToolbox.Paraglider.Polar;

/// <summary>Progress of a recording: how far it is, what it flies, and the latest point and samples.</summary>
/// <param name="Fraction">From 0 to 1.</param>
/// <param name="Phase">What is being flown, e.g. "Brakes 40%".</param>
/// <param name="Point">The point just measured, or null.</param>
/// <param name="Sample">The latest sample.</param>
public readonly record struct PolarProgress(double Fraction, string Phase, PolarPoint? Point, PolarSample Sample);

/// <summary>
/// Flies a glider's proxy through its speed range and records its polar: starting in trim, it steps through the speed bar
/// settings and then the symmetric brake settings until the stall. At each setting the controls ramp over, the glider
/// settles, and the airspeed, horizontal speed and sink rate are averaged over a measuring window. All the while it
/// samples continuously.
/// </summary>
/// <remarks>
/// Runs headless and faster than real time, and is deterministic: the same design and settings give the same polar, so
/// an optimizer can compare parameter changes. The key figures come from a least-squares cubic fit of sink rate over
/// airspeed through the steady points.
/// </remarks>
public static class PolarRecorder
{
    /// <summary>Generates the proxy of <paramref name="design"/> and records its polar.</summary>
    public static PolarRecording Record(GliderDesign design, PolarRecorderSettings? settings = null,
        IProgress<PolarProgress>? progress = null, CancellationToken cancellation = default)
    {
        var model = GliderGenerator.Generate(design, new GenerateOptions(TextureSize: -1), cancellation);
        return Record(model.Proxy.Model, design, settings, progress, cancellation);
    }

    /// <summary>Records the polar of <paramref name="proxy"/> (generated from <paramref name="design"/>).</summary>
    public static PolarRecording Record(ProxyModel proxy, GliderDesign? design, PolarRecorderSettings? settings = null,
        IProgress<PolarProgress>? progress = null, CancellationToken cancellation = default)
    {
        settings ??= new PolarRecorderSettings();
        var sim = new GliderSimulator(proxy);
        var samples = new List<PolarSample>();
        var points = new List<PolarPoint>();

        var plan = new List<(string Label, float Bar, float Brake, bool IsBrake)> { ("Trim", 0, 0, false) };
        plan.AddRange(settings.SpeedBarSteps.Select(b => ($"Speed bar {b:P0}", b, 0f, false)));
        plan.AddRange(settings.BrakeSteps.Select(b => ($"Brakes {b:P0}", 0f, b, true)));
        float perSetting = settings.RampSeconds + settings.SettleSeconds + settings.MeasureSeconds;
        float total = settings.StartSeconds + settings.SettleSeconds + settings.MeasureSeconds + (plan.Count - 1) * perSetting;

        float bar = 0, brake = 0, nextSample = 0;
        string phase = "Trim";
        bool complete = true;
        for (int i = 0; i < plan.Count; i++)
        {
            var (label, targetBar, targetBrake, isBrake) = plan[i];
            phase = label;
            if (cancellation.IsCancellationRequested)
            {
                complete = false;
                break;
            }

            // Ramp the controls over, then let the glider settle.
            float ramp = i == 0 ? 0 : settings.RampSeconds;
            float settle = i == 0 ? settings.StartSeconds + settings.SettleSeconds : settings.SettleSeconds;
            float fromBar = bar, fromBrake = brake;
            Fly(ramp, t =>
            {
                float f = ramp > 0 ? Math.Clamp(t / ramp, 0, 1) : 1;
                bar = fromBar + (targetBar - fromBar) * f;
                brake = fromBrake + (targetBrake - fromBrake) * f;
            });
            bar = targetBar;
            brake = targetBrake;
            Fly(settle, null);

            // Measure.
            double airspeed = 0, horizontal = 0, vertical = 0, vertical2 = 0, aoa = 0;
            float minPressure = 1, maxAoa = float.MinValue;
            int count = 0;
            Fly(settings.MeasureSeconds, _ =>
            {
                var v = sim.PilotVelocity;
                airspeed += sim.Airspeed;
                horizontal += MathF.Sqrt(v.X * v.X + v.Z * v.Z);
                vertical += v.Y;
                vertical2 += v.Y * v.Y;
                aoa += sim.CenterAngleOfAttack;
                maxAoa = MathF.Max(maxAoa, sim.CenterAngleOfAttack);
                minPressure = MathF.Min(minPressure, MinPressure(sim));
                count++;
            });
            if (count == 0) break;
            double meanVertical = vertical / count;
            float spread = (float)Math.Sqrt(Math.Max(0, vertical2 / count - meanVertical * meanVertical));
            float sink = (float)-meanVertical;
            bool stalled = aoa / count > settings.StallAngleOfAttack || maxAoa > settings.StallAngleOfAttack + 15 || minPressure < 0.4f;
            var point = new PolarPoint
            {
                Label = label,
                SpeedBar = targetBar,
                Brake = targetBrake,
                Airspeed = (float)(airspeed / count),
                HorizontalSpeed = (float)(horizontal / count),
                SinkRate = sink,
                GlideRatio = sink > 0.01f ? (float)(horizontal / count) / sink : float.PositiveInfinity,
                AngleOfAttack = (float)(aoa / count),
                SinkSpread = spread,
                IsStalled = stalled,
                IsStable = !stalled && spread < settings.StableSpread,
            };
            points.Add(point);
            progress?.Report(new PolarProgress(Math.Min(1, sim.Time / total), label, point, samples.Count > 0 ? samples[^1] : default));

            // The brake sweep ends at the stall.
            if (isBrake && stalled) break;
        }

        return new PolarRecording
        {
            Recorded = DateTime.Now,
            Design = design?.Clone(),
            Settings = settings,
            ProxyNodes = proxy.Nodes.Count,
            Samples = samples,
            Points = points,
            Summary = Summarize(points),
            IsComplete = complete && !cancellation.IsCancellationRequested,
        };

        void Fly(float seconds, Action<float>? perStep)
        {
            int steps = (int)Math.Round(seconds / settings.StepTime);
            for (int s = 0; s < steps; s++)
            {
                if (cancellation.IsCancellationRequested) return;
                perStep?.Invoke(s * settings.StepTime);
                sim.Inputs.SpeedBar = bar;
                sim.Inputs.BrakeLeft = brake;
                sim.Inputs.BrakeRight = brake;
                sim.Step(settings.StepTime);
                if (sim.Time >= nextSample)
                {
                    var v = sim.PilotVelocity;
                    var sample = new PolarSample(sim.Time, bar, brake, sim.Airspeed, MathF.Sqrt(v.X * v.X + v.Z * v.Z), v.Y,
                        sim.CenterAngleOfAttack, MinPressure(sim));
                    samples.Add(sample);
                    nextSample = sim.Time + settings.SampleInterval;
                    progress?.Report(new PolarProgress(Math.Min(1, sim.Time / total), phase, null, sample));
                }
            }
        }
    }

    private static float MinPressure(GliderSimulator sim)
    {
        float min = 1;
        foreach (float p in sim.CellPressure) min = MathF.Min(min, p);
        return min;
    }

    /// <summary>Computes the key figures from the points: measured trim, full speed, slowest and max sink; min sink and best glide from the fitted curve.</summary>
    public static PolarSummary Summarize(IReadOnlyList<PolarPoint> points)
    {
        var stable = points.Where(p => p.IsStable).OrderBy(p => p.Airspeed).ToList();
        if (stable.Count == 0) return new PolarSummary { StallBrake = StallBrake(points) };

        static PolarValue Of(PolarPoint p) => new(p.Airspeed, p.SinkRate, p.GlideRatio);
        int degree = Math.Min(3, stable.Count - 1);
        var fit = degree >= 1 ? FitPolynomial(stable.Select(p => (double)p.Airspeed).ToArray(), stable.Select(p => (double)p.SinkRate).ToArray(), degree) : [stable[0].SinkRate];
        var summary = new PolarSummary { FitCoefficients = fit };

        // Min sink and best glide on the fitted curve between the slowest and the fastest steady point.
        double vMin = stable[0].Airspeed, vMax = stable[^1].Airspeed;
        PolarValue minSink = Of(stable.MinBy(p => p.SinkRate)!), bestGlide = Of(stable.MaxBy(p => p.GlideRatio)!);
        if (degree >= 2 && vMax > vMin)
        {
            double bestSink = double.MaxValue, bestRatio = 0;
            for (int i = 0; i <= 400; i++)
            {
                double v = vMin + (vMax - vMin) * i / 400;
                double s = summary.FittedSink(v);
                if (s <= 0.01) continue;
                double glide = Math.Sqrt(Math.Max(0, v * v - s * s)) / s;
                if (s < bestSink)
                {
                    bestSink = s;
                    minSink = new PolarValue((float)v, (float)s, (float)glide);
                }
                if (glide > bestRatio)
                {
                    bestRatio = glide;
                    bestGlide = new PolarValue((float)v, (float)s, (float)glide);
                }
            }
        }

        var trim = points.FirstOrDefault(p => p.SpeedBar == 0 && p.Brake == 0);
        var full = points.Where(p => p.Brake == 0).MaxBy(p => p.SpeedBar);
        return summary with
        {
            Trim = trim is null ? default : Of(trim),
            FullSpeed = full is null ? default : Of(full),
            MinSink = minSink,
            BestGlide = bestGlide,
            MinSpeed = Of(stable[0]),
            MaxSink = Of(stable.MaxBy(p => p.SinkRate)!),
            StallBrake = StallBrake(points),
        };
    }

    private static float? StallBrake(IReadOnlyList<PolarPoint> points) => points.FirstOrDefault(p => p.IsStalled && p.Brake > 0)?.Brake;

    /// <summary>Least-squares polynomial fit; returns the coefficients from the constant term up.</summary>
    internal static double[] FitPolynomial(double[] x, double[] y, int degree)
    {
        int n = degree + 1;
        var a = new double[n, n + 1];
        for (int i = 0; i < x.Length; i++)
        {
            var powers = new double[2 * n];
            powers[0] = 1;
            for (int k = 1; k < powers.Length; k++) powers[k] = powers[k - 1] * x[i];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++) a[r, c] += powers[r + c];
                a[r, n] += powers[r] * y[i];
            }
        }
        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            for (int c = 0; c <= n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            if (Math.Abs(a[col, col]) < 1e-12) continue;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / a[col, col];
                for (int c = col; c <= n; c++) a[r, c] -= f * a[col, c];
            }
        }
        var result = new double[n];
        for (int r = 0; r < n; r++) result[r] = Math.Abs(a[r, r]) < 1e-12 ? 0 : a[r, n] / a[r, r];
        return result;
    }
}
