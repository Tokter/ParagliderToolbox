using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Convection;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// A moment of an airflow's film in a terrain's scene: the lift at the slice's height above the ground, every cloudy
/// column's cloud from its base to its top (the more water, the denser), the clouds' shadows on the ground, and the
/// followed thermals alive then.
/// </summary>
public sealed class FilmScene
{
    private readonly Mesh3D _slice = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _clouds = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _shadows = new(PrimitiveTopology.Triangles);
    private readonly Mesh3D _thermals = new(PrimitiveTopology.Triangles);
    private readonly MeshInstance3D _sliceInstance, _cloudInstance, _shadowInstance, _thermalInstance;

    /// <summary>Initializes the overlays in <paramref name="scene"/>, hidden.</summary>
    public FilmScene(Scene3D scene)
    {
        var see = Color.FromArgb(254, 255, 255, 255);
        _sliceInstance = new MeshInstance3D(_slice, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true }) { IsVisible = false };
        _shadowInstance = new MeshInstance3D(_shadows, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true }) { IsVisible = false };
        _cloudInstance = new MeshInstance3D(_clouds, new Material3D { BaseColor = see, Roughness = 1, DoubleSided = true }) { IsVisible = false };
        _thermalInstance = new MeshInstance3D(_thermals, new Material3D { BaseColor = see, Roughness = 0.9f, DoubleSided = true }) { IsVisible = false };
        scene.Instances.Add(_sliceInstance);
        scene.Instances.Add(_shadowInstance);
        scene.Instances.Add(_cloudInstance);
        scene.Instances.Add(_thermalInstance);
    }

    /// <summary>The geometry of a moment, built on any thread.</summary>
    internal sealed record Geometry(
        (Vector3[] Positions, uint[] Indices, Vector4[] Colors, Vector3[]? Normals) Slice,
        (Vector3[] Positions, uint[] Indices, Vector4[] Colors, Vector3[]? Normals) Clouds,
        (Vector3[] Positions, uint[] Indices, Vector4[] Colors, Vector3[]? Normals) Shadows,
        OverlayGeometry Thermals);

    /// <summary>Hides everything.</summary>
    public void Clear() => _sliceInstance.IsVisible = _cloudInstance.IsVisible = _shadowInstance.IsVisible = _thermalInstance.IsVisible = false;

    /// <summary>Puts <paramref name="geometry"/> into the scene (on the UI thread).</summary>
    internal void Apply(Geometry geometry)
    {
        Set(_sliceInstance, _slice, geometry.Slice);
        Set(_cloudInstance, _clouds, geometry.Clouds);
        Set(_shadowInstance, _shadows, geometry.Shadows);
        Set(_thermalInstance, _thermals, (geometry.Thermals.Positions, geometry.Thermals.Indices, geometry.Thermals.Colors, geometry.Thermals.Normals));
    }

    private static void Set(MeshInstance3D instance, Mesh3D mesh, (Vector3[] Positions, uint[] Indices, Vector4[] Colors, Vector3[]? Normals) geometry)
    {
        instance.IsVisible = geometry.Indices is { Length: > 0 };
        if (instance.IsVisible) mesh.SetGeometry(geometry.Positions, geometry.Indices, geometry.Normals, colors: geometry.Colors);
    }

    /// <summary>Builds a frame's geometry.</summary>
    /// <param name="film">The film.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="sliceHeight">The slice's height above the ground (m), or NaN for no slice.</param>
    /// <param name="clouds">Whether the clouds and their shadows show.</param>
    /// <param name="thermals">The run's followed thermals, to show those alive at the frame, or <c>null</c>.</param>
    /// <param name="ground">The terrain's height at a point (finer than the columns; NaN outside).</param>
    internal static Geometry Build(AirflowFilm film, AirflowFilmFrame frame, double sliceHeight, bool clouds, ConvectionClimate? thermals,
        Func<double, double, float> ground)
    {
        float Ground(double x, double z)
        {
            float h = ground(x, z);
            return float.IsNaN(h) ? film.GroundHeight[film.Column(x, z)] : h;
        }
        var empty = (Array.Empty<Vector3>(), Array.Empty<uint>(), Array.Empty<Vector4>(), (Vector3[]?)null);
        return new Geometry(
            double.IsNaN(sliceHeight) ? empty : SliceGeometry(film, frame, sliceHeight, Ground),
            clouds ? CloudGeometry(film, frame) : empty,
            clouds ? ShadowGeometry(film, frame, Ground) : empty,
            thermals is null ? OverlayGeometry.Empty : ConvectionGeometry.Alive(thermals, frame.Time, Ground));
    }

    // The lift at the height above the ground, on a mesh at half the columns' size draped over the terrain.
    private static (Vector3[], uint[], Vector4[], Vector3[]?) SliceGeometry(AirflowFilm film, AirflowFilmFrame frame, double height,
        Func<double, double, float> ground)
    {
        int nx = film.Nx * 2 + 1, nz = film.Nz * 2 + 1;
        double step = film.CellSize / 2;
        var positions = new Vector3[nx * nz];
        var colors = new Vector4[nx * nz];
        Parallel.For(0, nz, b =>
        {
            for (int a = 0; a < nx; a++)
            {
                double x = film.X0 + a * step, z = film.Z0 + b * step;
                positions[b * nx + a] = new Vector3((float)x, ground(x, z) + (float)height, (float)z);
                float w = frame.LiftAt(film.Column(x, z), height);
                colors[b * nx + a] = new Vector4(AirflowColors.Lift.At(w), 0.08f + 0.67f * Math.Min(1, Math.Abs(w) / 2.5f));
            }
        });
        var indices = new uint[(nx - 1) * (nz - 1) * 6];
        Parallel.For(0, nz - 1, b =>
        {
            for (int a = 0; a < nx - 1; a++)
            {
                uint v = (uint)(b * nx + a);
                int t = (b * (nx - 1) + a) * 6;
                indices[t] = v;
                indices[t + 1] = v + (uint)nx;
                indices[t + 2] = v + 1;
                indices[t + 3] = v + 1;
                indices[t + 4] = v + (uint)nx;
                indices[t + 5] = v + (uint)nx + 1;
            }
        });
        return (positions, indices, colors, null);
    }

    private static readonly Vector3[] s_faces = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    // A box per cloudy column from the cloud's base to its top, the denser the more water it holds.
    private static (Vector3[], uint[], Vector4[], Vector3[]?) CloudGeometry(AirflowFilm film, AirflowFilmFrame frame)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        float half = (float)film.CellSize / 2;
        for (int column = 0; column < frame.CloudBase.Length; column++)
        {
            if (frame.CloudBase[column] == 0) continue;
            float bottom = frame.CloudBase[column], top = Math.Max(frame.CloudTop[column], bottom + 10);
            float path = frame.CloudWater[column] * AirflowFilm.WaterStep;
            var color = new Vector4(1, 1, 1, 0.12f + 0.6f * Math.Min(1, path / 0.15f));
            var center = new Vector3((float)(film.X0 + (column % film.Nx + 0.5) * film.CellSize), (bottom + top) / 2,
                (float)(film.Z0 + (column / film.Nx + 0.5) * film.CellSize));
            var size = new Vector3(half, (top - bottom) / 2, half);
            foreach (var normal in s_faces)
            {
                var u = Math.Abs(normal.Y) > 0 ? Vector3.UnitX : Vector3.UnitY;
                var w = Vector3.Cross(normal, u);
                uint first = (uint)positions.Count;
                foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    positions.Add(center + (normal + u * a + w * b) * size);
                    normals.Add(normal);
                    colors.Add(color);
                }
                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
        }
        return ([.. positions], [.. indices], [.. colors], [.. normals]);
    }

    // A dark square draped over the ground of every shaded column, the darker the less sun gets through.
    private static (Vector3[], uint[], Vector4[], Vector3[]?) ShadowGeometry(AirflowFilm film, AirflowFilmFrame frame, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        double h = film.CellSize;
        for (int column = 0; column < frame.Sunshine.Length; column++)
        {
            float shade = 1 - frame.Sunshine[column] / 255f;
            if (shade < 0.1f) continue;
            double x0 = film.X0 + column % film.Nx * h, z0 = film.Z0 + column / film.Nx * h;
            uint first = (uint)positions.Count;
            foreach (var (a, b) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            {
                double x = x0 + a * h, z = z0 + b * h;
                positions.Add(new Vector3((float)x, ground(x, z) + 3, (float)z));
                colors.Add(new Vector4(0, 0, 0.05f, 0.45f * shade));
            }
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        return ([.. positions], [.. indices], [.. colors], null);
    }
}
