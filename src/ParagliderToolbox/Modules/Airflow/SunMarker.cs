using System.Globalization;
using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Airflow.Sun;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// The sun in a terrain's scene: a disc in the sky where the sun stands, seen from a point on the ground, and parallel rays
/// from it down to that point, the way the sunlight falls on the slopes (and the clouds' shadows go).
/// </summary>
public sealed class SunMarker
{
    private const int Sides = 24;

    private readonly Mesh3D _disc = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _rays = new(PrimitiveTopology.Lines);
    private readonly MeshInstance3D _discInstance, _rayInstance;

    /// <summary>Initializes the marker in <paramref name="scene"/>, hidden.</summary>
    public SunMarker(Scene3D scene)
    {
        var see = Color.FromArgb(254, 255, 255, 255);
        _discInstance = new MeshInstance3D(_disc, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true }) { IsVisible = false };
        _rayInstance = new MeshInstance3D(_rays, new Material3D { BaseColor = see, Unlit = true }) { IsVisible = false };
        scene.Instances.Add(_rayInstance);
        scene.Instances.Add(_discInstance);
    }

    /// <summary>Shows the sun seen from <paramref name="target"/>, <paramref name="distance"/> meters away, or hides it (<c>null</c> or below the horizon).</summary>
    public void Show(SunPosition? sun, Vector3 target, float distance)
    {
        if (sun is not { IsUp: true } position)
        {
            _discInstance.IsVisible = _rayInstance.IsVisible = false;
            return;
        }
        var toSun = position.Direction;
        var center = target + toSun * distance;
        // Two axes across the sun's direction.
        var across = Vector3.Normalize(Vector3.Cross(toSun, Math.Abs(toSun.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
        var up = Vector3.Cross(across, toSun);
        float radius = distance * 0.045f;
        var warm = new Vector3(1f, 0.82f, 0.25f);

        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        // The disc, and a faint halo around it.
        void Disc(float outer, float inner, float alpha, float edgeAlpha)
        {
            uint first = (uint)positions.Count;
            for (int s = 0; s <= Sides; s++)
            {
                double a = 2 * Math.PI * s / Sides;
                var dir = across * (float)Math.Cos(a) + up * (float)Math.Sin(a);
                positions.Add(center + dir * inner);
                colors.Add(new Vector4(warm, alpha));
                positions.Add(center + dir * outer);
                colors.Add(new Vector4(warm, edgeAlpha));
            }
            for (uint s = 0; s < Sides; s++)
            {
                uint a = first + 2 * s;
                indices.AddRange([a, a + 1, a + 2, a + 2, a + 1, a + 3]);
            }
        }
        Disc(radius, 0, 1, 1);
        Disc(radius * 2.2f, radius, 0.45f, 0);
        _disc.SetGeometry([.. positions], [.. indices], null, colors: [.. colors]);
        _discInstance.IsVisible = true;

        // Parallel rays from around the disc to around the point.
        var rayPositions = new List<Vector3>();
        var rayColors = new List<Vector4>();
        foreach (var (a, b) in new[] { (0f, 0f), (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
        {
            var offset = (across * a + up * b) * radius * 0.6f;
            rayPositions.Add(center + offset);
            rayColors.Add(new Vector4(warm, 0.7f));
            rayPositions.Add(target + offset);
            rayColors.Add(new Vector4(warm, 0.15f));
        }
        _rays.SetGeometry([.. rayPositions], Enumerable.Range(0, rayPositions.Count).Select(n => (uint)n).ToArray(), null, colors: [.. rayColors]);
        _rayInstance.IsVisible = true;
    }

    /// <summary>Gets where the sun stands, for people: "from 194° (S), 64° up".</summary>
    public static string Describe(SunPosition sun)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        string point = points[(int)Math.Round(((sun.Azimuth % 360) + 360) % 360 / 45) % 8];
        return sun.IsUp
            ? string.Create(CultureInfo.CurrentCulture, $"from {sun.Azimuth:0}° ({point}), {sun.Elevation:0}° up")
            : string.Create(CultureInfo.CurrentCulture, $"below the horizon ({sun.Elevation:0}°)");
    }
}
