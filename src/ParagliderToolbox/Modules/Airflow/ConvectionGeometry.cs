using System.Numerics;
using ParagliderToolbox.Airflow.Convection;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// The geometry of a run's followed thermals (<see cref="ConvectionClimate"/>): the sites they rise from, and the
/// thermals alive at a moment, each a leaning tube as wide as its core, colored by its climb.
/// </summary>
internal static class ConvectionGeometry
{
    private const int Sides = 12;

    /// <summary>A tube per site from the ground to its thermals' mean top, leaning as they do; the busier, the denser.</summary>
    public static OverlayGeometry Sites(ConvectionClimate climate, Func<double, double, float> ground)
    {
        var mesh = new Mesh();
        foreach (var site in climate.Sites)
        {
            var bottom = new Vector3(site.X, ground(site.X, site.Z), site.Z);
            float height = Math.Max(50, site.Top - bottom.Y);
            var top = bottom + new Vector3(site.LeanX * height, height, site.LeanZ * height);
            float alpha = 0.2f + 0.5f * (float)Math.Min(1, site.Rate / 12);
            mesh.Tube(bottom, top, site.Diameter / 2, new Vector4(AirflowColors.Lift.At(site.Climb), alpha));
        }
        return mesh.Geometry();
    }

    /// <summary>
    /// The thermals alive at <paramref name="time"/>: where their root has drifted by then, from their bottom to their top,
    /// fading in as they start and out as they end.
    /// </summary>
    public static OverlayGeometry Alive(ConvectionClimate climate, double time, Func<double, double, float> ground)
    {
        var mesh = new Mesh();
        foreach (var thermal in climate.Thermals)
        {
            double age = time - thermal.Start;
            if (age < 0 || age > thermal.Duration) continue;
            float x = thermal.X + (float)(thermal.DriftX * age), z = thermal.Z + (float)(thermal.DriftZ * age);
            float floor = ground(x, z);
            if (float.IsNaN(floor)) continue;
            var bottom = new Vector3(x, floor + Math.Max(0, thermal.Bottom), z);
            float height = Math.Max(50, thermal.Top - bottom.Y);
            var top = bottom + new Vector3(thermal.LeanX * height, height, thermal.LeanZ * height);
            float fade = (float)Math.Clamp(Math.Min(age, thermal.Duration - age) / 60, 0.25, 1);
            mesh.Tube(bottom, top, thermal.Diameter / 2, new Vector4(AirflowColors.Lift.At(thermal.Climb), 0.6f * fade));
        }
        return mesh.Geometry();
    }

    // Tubes as lists of vertices.
    private sealed class Mesh
    {
        private readonly List<Vector3> _positions = [], _normals = [];
        private readonly List<Vector4> _colors = [];
        private readonly List<uint> _indices = [];

        // A tube from bottom to top (its rings level), fainter at the bottom.
        public void Tube(Vector3 bottom, Vector3 top, float radius, Vector4 color)
        {
            uint first = (uint)_positions.Count;
            foreach (var (center, alpha) in new[] { (bottom, color.W * 0.3f), (top, color.W) })
            {
                for (int s = 0; s <= Sides; s++)
                {
                    double a = 2 * Math.PI * s / Sides;
                    var normal = new Vector3((float)Math.Cos(a), 0, (float)Math.Sin(a));
                    _positions.Add(center + normal * radius);
                    _normals.Add(normal);
                    _colors.Add(color with { W = alpha });
                }
            }
            for (uint s = 0; s < Sides; s++)
            {
                uint a = first + s, b = a + Sides + 1;
                _indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        }

        public OverlayGeometry Geometry() => new([.. _positions], [.. _indices], [.. _colors], [.. _normals]);
    }
}
