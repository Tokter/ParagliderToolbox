using System.Numerics;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Simulation;

namespace ParagliderToolbox.Tests.Paraglider;

public class SelfCollisionTests
{
    private static readonly ProxyModel s_model =
        GliderGenerator.Generate(new GliderDesign { SpanwiseSegmentsPerCell = 2, ChordwiseSegments = 20, GenerateRibs = false }, new GenerateOptions(-1)).Proxy.Model;

    // The mean number of pairs of canopy surface triangles of strips at least two apart that pass through each other,
    // through a full stall (both brakes pulled in over two seconds, held for four, released).
    private static double FullStallCrossings(SimulatorSettings settings)
    {
        var sim = new GliderSimulator(s_model, settings);
        var triangles = new List<(int A, int B, int C, int Strip)>();
        for (int s = 0; s < s_model.Strips.Count; s++)
        {
            var surface = s_model.Strips[s].Surface;
            for (int t = 0; t < surface.Count; t += 3) triangles.Add((surface[t], surface[t + 1], surface[t + 2], s));
        }
        long crossings = 0;
        int samples = 0;
        for (int i = 0; i < 16 * 60; i++)
        {
            float t = sim.Time;
            sim.Inputs.BrakeLeft = sim.Inputs.BrakeRight = t < 4 ? 0 : t < 6 ? (t - 4) / 2 : t < 10 ? 1 : 0;
            sim.Step(1 / 60f);
            if (t < 6 || i % 6 != 0) continue;
            crossings += Crossings(sim.Positions, triangles);
            samples++;
        }
        return crossings / (double)samples;
    }

    private static int Crossings(ReadOnlySpan<Vector3> p, List<(int A, int B, int C, int Strip)> triangles)
    {
        var min = new Vector3[triangles.Count];
        var max = new Vector3[triangles.Count];
        for (int i = 0; i < triangles.Count; i++)
        {
            var (a, b, c, _) = triangles[i];
            min[i] = Vector3.Min(p[a], Vector3.Min(p[b], p[c]));
            max[i] = Vector3.Max(p[a], Vector3.Max(p[b], p[c]));
        }
        int count = 0;
        for (int i = 0; i < triangles.Count; i++)
        {
            var x = triangles[i];
            for (int j = i + 1; j < triangles.Count; j++)
            {
                var y = triangles[j];
                if (Math.Abs(x.Strip - y.Strip) < 2) continue;
                var low = Vector3.Max(min[i], min[j]);
                var high = Vector3.Min(max[i], max[j]);
                if (low.X > high.X || low.Y > high.Y || low.Z > high.Z) continue;
                if (Pierces(p[x.A], p[x.B], p[y.A], p[y.B], p[y.C]) || Pierces(p[x.B], p[x.C], p[y.A], p[y.B], p[y.C]) || Pierces(p[x.C], p[x.A], p[y.A], p[y.B], p[y.C]) ||
                    Pierces(p[y.A], p[y.B], p[x.A], p[x.B], p[x.C]) || Pierces(p[y.B], p[y.C], p[x.A], p[x.B], p[x.C]) || Pierces(p[y.C], p[y.A], p[x.A], p[x.B], p[x.C]))
                    count++;
            }
        }
        return count;
    }

    // Whether segment pq passes through triangle abc (Möller–Trumbore).
    private static bool Pierces(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
    {
        var direction = q - p;
        var ab = b - a;
        var ac = c - a;
        var h = Vector3.Cross(direction, ac);
        float determinant = Vector3.Dot(ab, h);
        if (MathF.Abs(determinant) < 1e-10f) return false;
        var s = p - a;
        float u = Vector3.Dot(s, h) / determinant;
        if (u < 0 || u > 1) return false;
        var r = Vector3.Cross(s, ab);
        float v = Vector3.Dot(direction, r) / determinant;
        if (v < 0 || u + v > 1) return false;
        float t = Vector3.Dot(ac, r) / determinant;
        return t >= 0 && t <= 1;
    }

    [Fact]
    public void FullStall_CrumplesTheCanopy_WithoutFoldingItThroughItself()
    {
        double without = FullStallCrossings(new SimulatorSettings { SelfCollisionDistance = 0 });
        double with = FullStallCrossings(new SimulatorSettings());

        Assert.True(without > 20, $"without self-collision the stalled canopy passes through itself ({without:0.0} crossings)");
        Assert.True(with < without / 10, $"{with:0.0} crossings with self-collision, {without:0.0} without");
    }
}
