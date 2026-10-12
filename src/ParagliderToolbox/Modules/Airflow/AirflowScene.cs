using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>Which parts of an airflow the view shows, and how.</summary>
public sealed record AirflowViewOptions
{
    /// <summary>Gets whether streamlines show the mean wind's paths.</summary>
    public bool Streamlines { get; init; }

    /// <summary>Gets whether particles stream with the wind.</summary>
    public bool Particles { get; init; }

    /// <summary>Gets whether a slice at <see cref="SliceHeight"/> above the ground shows <see cref="Quantity"/>.</summary>
    public bool Slice { get; init; } = true;

    /// <summary>Gets whether a vertical section along the wind through <see cref="SectionCenter"/> shows <see cref="Quantity"/>.</summary>
    public bool Section { get; init; }

    /// <summary>Gets what the slice and the section color.</summary>
    public AirflowQuantity Quantity { get; init; } = AirflowQuantity.Lift;

    /// <summary>Gets the slice's height above the ground (m), where the streamlines start.</summary>
    public double SliceHeight { get; init; } = 300;

    /// <summary>Gets the point (X, Z) the section goes through.</summary>
    public Vector2 SectionCenter { get; init; }

    /// <summary>Gets whether the thermals show as columns (with their cumulus), or the sites of a run's followed thermals.</summary>
    public bool Thermals { get; init; } = true;

    /// <summary>Gets whether the clouds show.</summary>
    public bool Clouds { get; init; } = true;

    /// <summary>Gets whether a map on the ground shows <see cref="GroundMap"/> and the thermal triggers.</summary>
    public bool Ground { get; init; }

    /// <summary>Gets what the ground map shows.</summary>
    public GroundMap GroundMap { get; init; }
}

/// <summary>The geometry of one overlay: positions, indices, colors (linear RGBA) and optional normals.</summary>
internal sealed record OverlayGeometry(Vector3[] Positions, uint[] Indices, Vector4[] Colors, Vector3[]? Normals = null)
{
    public static OverlayGeometry Empty { get; } = new([], [], []);
}

/// <summary>Everything the overlays show of one field and set of options, built off the UI thread.</summary>
internal sealed record AirflowGeometry(
    OverlayGeometry Streamlines, OverlayGeometry Slice, OverlayGeometry Section, OverlayGeometry Thermals, OverlayGeometry Clouds,
    OverlayGeometry Ground, OverlayGeometry Triggers, OverlayGeometry TriggerTops);

/// <summary>
/// The airflow's overlays in a terrain's scene: streamlines of the mean wind, a slice at a height above the ground and a
/// vertical section along the wind (colored by lift, turbulence, wind …), the thermals as columns with their cumulus,
/// the clouds, a map of the ground's heating or land cover with the thermal triggers, and streaming particles.
/// </summary>
/// <remarks>
/// <see cref="Build"/> makes the geometry (on any thread, from the field and the terrain's heights); <see cref="Apply"/>
/// puts it into the scene on the UI thread.
/// </remarks>
public sealed class AirflowScene
{
    private readonly Overlay _streamlines, _slice, _section, _thermals, _clouds, _ground, _triggers, _triggerTops;

    /// <summary>Initializes the overlays in <paramref name="scene"/>, empty.</summary>
    public AirflowScene(Scene3D scene)
    {
        var see = Color.FromArgb(254, 255, 255, 255);
        _streamlines = new Overlay(scene, PrimitiveTopology.Lines, new Material3D { BaseColor = Color.FromRgb(255, 255, 255), Unlit = true });
        _slice = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true });
        _section = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true });
        _thermals = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = see, DoubleSided = true, Roughness = 0.9f });
        _clouds = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = see, Roughness = 1, DoubleSided = true });
        _ground = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true });
        _triggers = new Overlay(scene, PrimitiveTopology.Lines, new Material3D { BaseColor = Color.FromRgb(255, 255, 255), Unlit = true });
        _triggerTops = new Overlay(scene, PrimitiveTopology.Points, new Material3D { BaseColor = Color.FromRgb(255, 255, 255), Unlit = true, PointSize = 9 });
        Particles = new AirflowParticles(scene);
    }

    /// <summary>Gets the particles.</summary>
    public AirflowParticles Particles { get; }

    /// <summary>Hides everything.</summary>
    public void Clear()
    {
        Apply(new AirflowGeometry(OverlayGeometry.Empty, OverlayGeometry.Empty, OverlayGeometry.Empty, OverlayGeometry.Empty, OverlayGeometry.Empty,
            OverlayGeometry.Empty, OverlayGeometry.Empty, OverlayGeometry.Empty));
        Particles.Show(null, null);
    }

    /// <summary>Puts <paramref name="geometry"/> into the scene.</summary>
    internal void Apply(AirflowGeometry geometry)
    {
        _streamlines.Set(geometry.Streamlines);
        _slice.Set(geometry.Slice);
        _section.Set(geometry.Section);
        _thermals.Set(geometry.Thermals);
        _clouds.Set(geometry.Clouds);
        _ground.Set(geometry.Ground);
        _triggers.Set(geometry.Triggers);
        _triggerTops.Set(geometry.TriggerTops);
    }

    /// <summary>Builds the overlays of <paramref name="field"/> as <paramref name="options"/> want them.</summary>
    /// <param name="field">The airflow.</param>
    /// <param name="options">What to show.</param>
    /// <param name="ground">The terrain's height at a point (finer than the field's columns).</param>
    internal static AirflowGeometry Build(AirflowField field, AirflowViewOptions options, Func<double, double, float> ground)
    {
        float Ground(double x, double z)
        {
            float h = ground(x, z);
            return float.IsNaN(h) ? field.GroundAt(x, z) : h;
        }
        var empty = OverlayGeometry.Empty;
        return new AirflowGeometry(
            options.Streamlines ? StreamlineGeometry(field, options, Ground) : empty,
            options.Slice ? SliceGeometry(field, options, Ground) : empty,
            options.Section ? SectionGeometry(field, options, Ground) : empty,
            // A run that followed its thermals shows the sites they rose from; else the mean's thermals.
            !options.Thermals ? empty : field.Convection is { } climate ? ConvectionGeometry.Sites(climate, Ground) : ThermalGeometry(field, Ground),
            options.Clouds ? CloudGeometry(field) : empty,
            options.Ground ? GroundGeometry(field, options, Ground) : empty,
            options.Ground ? TriggerGeometry(field, Ground, tops: false) : empty,
            options.Ground ? TriggerGeometry(field, Ground, tops: true) : empty);
    }

    /// <summary>
    /// Gets the ground the air starts at: the terrain's (<paramref name="ground"/>, NaN where it has none), or the field's
    /// where that is higher (its columns' mean, above the terrain at the foot of a cliff), where the wind is still.
    /// </summary>
    internal static float AirGround(AirflowField field, Func<double, double, float>? ground, double x, double z)
    {
        float terrain = ground?.Invoke(x, z) ?? float.NaN, columns = field.GroundAt(x, z);
        return float.IsNaN(terrain) ? columns : Math.Max(terrain, columns);
    }

    /// <summary>The least height a slice is drawn above the terrain (m), so it doesn't flicker in it at 0.</summary>
    internal const float SliceClearance = 1;

    private static Vector4 Rgba(Vector3 color, float alpha) => new(color, alpha);

    // Seeds on a grid at the slice's height, traced both ways; colored by the vertical wind along them.
    private static OverlayGeometry StreamlineGeometry(AirflowField field, AirflowViewOptions options, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        double width = field.Nx * field.CellSize, depth = field.Nz * field.CellSize;
        double spacing = Math.Max(field.CellSize * 4, Math.Max(width, depth) / 22);
        double step = Math.Max(10, field.CellSize / 3), length = Math.Max(width, depth) / 3;
        for (double z = field.Z0 + spacing / 2; z < field.Z0 + depth; z += spacing)
        {
            for (double x = field.X0 + spacing / 2; x < field.X0 + width; x += spacing)
            {
                var start = new Vector3((float)x, AirGround(field, ground, x, z) + (float)options.SliceHeight, (float)z);
                foreach (bool backward in new[] { false, true })
                {
                    var line = Streamlines.Trace(field, start, length, step, backward);
                    if (line.Count < 2) continue;
                    uint first = (uint)positions.Count;
                    foreach (var p in line)
                    {
                        positions.Add(p);
                        colors.Add(Rgba(AirflowColors.Lift.At(field.WindAt(p.X, p.Y, p.Z).Y), 1));
                    }
                    for (uint i = 0; i + 1 < line.Count; i++)
                    {
                        indices.Add(first + i);
                        indices.Add(first + i + 1);
                    }
                }
            }
        }
        return new OverlayGeometry([.. positions], [.. indices], [.. colors]);
    }

    // A surface at the slice's height above the terrain, at half the cells' spacing.
    private static OverlayGeometry SliceGeometry(AirflowField field, AirflowViewOptions options, Func<double, double, float> ground)
    {
        int nx = field.Nx * 2 + 1, nz = field.Nz * 2 + 1;
        double step = field.CellSize / 2;
        var positions = new Vector3[nx * nz];
        var colors = new Vector4[nx * nz];
        var scale = AirflowColors.Of(options.Quantity);
        Parallel.For(0, nz, b =>
        {
            for (int a = 0; a < nx; a++)
            {
                double x = field.X0 + a * step, z = field.Z0 + b * step;
                float y = ground(x, z) + Math.Max(SliceClearance, (float)options.SliceHeight);
                positions[b * nx + a] = new Vector3((float)x, y, (float)z);
                colors[b * nx + a] = SampleColor(field, options.Quantity, scale, x, AirGround(field, ground, x, z) + options.SliceHeight, z);
            }
        });
        return new OverlayGeometry(positions, Grid(nx, nz), colors);
    }

    // A vertical plane along the wind through the section's center, from the grid's floor to its top.
    private static OverlayGeometry SectionGeometry(AirflowField field, AirflowViewOptions options, Func<double, double, float> ground)
    {
        double angle = (field.Info.WindDirection + 180) * Math.PI / 180;
        var along = new Vector2((float)Math.Sin(angle), (float)-Math.Cos(angle));
        if (field.Info.WindSpeed < 0.3) along = new Vector2(1, 0);
        double half = Math.Max(field.Nx, field.Nz) * field.CellSize * 0.75;
        double step = field.CellSize / 2, vertical = field.LayerHeight / 2;
        int nu = (int)(2 * half / step) + 1, nv = (int)((field.Top - field.Y0) / vertical) + 1;
        var positions = new Vector3[nu * nv];
        var colors = new Vector4[nu * nv];
        var scale = AirflowColors.Of(options.Quantity);
        double x1 = field.X0 + field.Nx * field.CellSize, z1 = field.Z0 + field.Nz * field.CellSize;
        Parallel.For(0, nu, u =>
        {
            double s = -half + u * step;
            double x = options.SectionCenter.X + along.X * s, z = options.SectionCenter.Y + along.Y * s;
            bool inside = x >= field.X0 && x <= x1 && z >= field.Z0 && z <= z1;
            float floor = inside ? ground(x, z) : float.MaxValue;
            for (int v = 0; v < nv; v++)
            {
                double y = field.Y0 + v * vertical;
                positions[v * nu + u] = new Vector3((float)x, (float)y, (float)z);
                colors[v * nu + u] = y < floor ? default : SampleColor(field, options.Quantity, scale, x, y, z, section: true);
            }
        });
        return new OverlayGeometry(positions, Grid(nu, nv), colors);
    }

    private static Vector4 SampleColor(AirflowField field, AirflowQuantity quantity, AirflowColors.Scale scale, double x, double y, double z, bool section = false)
    {
        var sample = field.Sample(x, y, z);
        float value = AirflowColors.Value(quantity, sample);
        // Lift and sink stand out, still air is faint on the slice and clear in the section (a wall across the sky
        // would hide the terrain); the other quantities fade where they are small in the section.
        float alpha = quantity == AirflowQuantity.Lift
            ? (section ? 0.75f * Math.Clamp((Math.Abs(value) - 0.2f) / 2.3f, 0, 1) : 0.08f + 0.67f * Math.Min(1, Math.Abs(value) / 2.5f))
            : section ? 0.6f * Math.Clamp((value - scale.Stops[0].Value) / (scale.Stops[^1].Value - scale.Stops[0].Value) * 2, 0, 1) : 0.65f;
        return Rgba(scale.At(value), alpha);
    }

    // Two triangles per quad of a grid of nu × nv vertices (row by row).
    private static uint[] Grid(int nu, int nv)
    {
        var indices = new uint[(nu - 1) * (nv - 1) * 6];
        int n = 0;
        for (int v = 0; v + 1 < nv; v++)
        {
            for (int u = 0; u + 1 < nu; u++)
            {
                uint a = (uint)(v * nu + u), b = a + 1, c = a + (uint)nu, d = c + 1;
                indices[n++] = a;
                indices[n++] = c;
                indices[n++] = b;
                indices[n++] = b;
                indices[n++] = c;
                indices[n++] = d;
            }
        }
        return indices;
    }

    // A tube from the ground to the top per thermal (thicker for stronger ones), with a disc at its cloud base.
    private static OverlayGeometry ThermalGeometry(AirflowField field, Func<double, double, float> ground)
    {
        const int sides = 12;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        foreach (var thermal in field.Thermals)
        {
            float radius = (float)(25 + 10 * thermal.Climb);
            var color = Rgba(AirflowColors.Lift.At((float)thermal.Climb), 0.55f);
            // Along its core, which leans with the wind, from the ground (straight up without one).
            var core = new List<Vector3>();
            float bottom = ground(thermal.X, thermal.Z);
            core.Add(new Vector3((float)thermal.X, bottom, (float)thermal.Z));
            foreach (var point in thermal.Core)
            {
                if (point.Y > core[^1].Y + 20) core.Add(point);
            }
            if (core.Count == 1) core.Add(new Vector3((float)thermal.X, (float)Math.Max(thermal.Top, bottom + 50), (float)thermal.Z));
            uint first = (uint)positions.Count;
            for (int n = 0; n < core.Count; n++)
            {
                float along = (float)n / (core.Count - 1);
                for (int s = 0; s <= sides; s++)
                {
                    double a = 2 * Math.PI * s / sides;
                    var normal = new Vector3((float)Math.Cos(a), 0, (float)Math.Sin(a));
                    positions.Add(core[n] + normal * radius * (1 + 0.3f * along));
                    normals.Add(normal);
                    colors.Add(color with { W = 0.1f + 0.45f * along });
                }
            }
            for (uint n = 0; n + 1 < core.Count; n++)
            {
                for (uint s = 0; s < sides; s++)
                {
                    uint a = first + n * (sides + 1) + s, b = a + sides + 1;
                    indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
                }
            }
            if (double.IsNaN(thermal.CloudBase)) continue;
            // The cumulus' base: a disc where the core reaches it.
            float baseHeight = (float)thermal.CloudBase;
            var under = core.LastOrDefault(p => p.Y <= baseHeight, core[^1]);
            uint middle = (uint)positions.Count;
            positions.Add(new Vector3(under.X, baseHeight, under.Z));
            normals.Add(-Vector3.UnitY);
            colors.Add(new Vector4(1, 1, 1, 0.75f));
            for (int s = 0; s <= sides; s++)
            {
                double a = 2 * Math.PI * s / sides;
                positions.Add(new Vector3((float)(under.X + Math.Cos(a) * radius * 3), baseHeight, (float)(under.Z + Math.Sin(a) * radius * 3)));
                normals.Add(-Vector3.UnitY);
                colors.Add(new Vector4(1, 1, 1, 0.45f));
            }
            for (uint s = 0; s < sides; s++) indices.AddRange([middle, middle + 1 + s, middle + 2 + s]);
        }
        return new OverlayGeometry([.. positions], [.. indices], [.. colors], [.. normals]);
    }

    // A box per cloudy cell (the most cloudy first when there are many).
    private static OverlayGeometry CloudGeometry(AirflowField field)
    {
        const int limit = 40_000;
        var cells = new List<(int I, int J, int K, float Cloud)>();
        for (int j = 0; j < field.Ny; j++)
        {
            for (int k = 0; k < field.Nz; k++)
            {
                for (int i = 0; i < field.Nx; i++)
                {
                    float cloud = field.Cloud[field.Index(i, j, k)];
                    if (cloud >= 0.15f) cells.Add((i, j, k, cloud));
                }
            }
        }
        if (cells.Count > limit) cells = cells.OrderByDescending(c => c.Cloud).Take(limit).ToList();
        var positions = new Vector3[cells.Count * 24];
        var normals = new Vector3[positions.Length];
        var colors = new Vector4[positions.Length];
        var indices = new uint[cells.Count * 36];
        var half = new Vector3((float)field.CellSize, (float)field.LayerHeight, (float)field.CellSize) * 0.5f;
        Parallel.For(0, cells.Count, n =>
        {
            var (i, j, k, cloud) = cells[n];
            var center = new Vector3((float)(field.X0 + (i + 0.5) * field.CellSize), (float)(field.Y0 + (j + 0.5) * field.LayerHeight), (float)(field.Z0 + (k + 0.5) * field.CellSize));
            // Faint per box: a cloud is many boxes deep.
            var color = new Vector4(1, 1, 1, 0.04f + 0.14f * cloud);
            Box(center, half, color, positions, normals, colors, indices, n);
        });
        return new OverlayGeometry(positions, indices, colors, normals);
    }

    private static readonly Vector3[] s_faceNormals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    private static void Box(Vector3 center, Vector3 half, Vector4 color, Vector3[] positions, Vector3[] normals, Vector4[] colors, uint[] indices, int n)
    {
        int v = n * 24, t = n * 36;
        foreach (var normal in s_faceNormals)
        {
            // Two axes across the face.
            var u = Math.Abs(normal.Y) > 0 ? Vector3.UnitX : Vector3.UnitY;
            var w = Vector3.Cross(normal, u);
            uint first = (uint)v;
            foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                positions[v] = center + (normal + u * a + w * b) * half;
                normals[v] = normal;
                colors[v] = color;
                v++;
            }
            indices[t++] = first;
            indices[t++] = first + 1;
            indices[t++] = first + 2;
            indices[t++] = first;
            indices[t++] = first + 2;
            indices[t++] = first + 3;
        }
    }

    // The ground map, a few meters above the terrain at half the cells' spacing.
    private static OverlayGeometry GroundGeometry(AirflowField field, AirflowViewOptions options, Func<double, double, float> ground)
    {
        int nx = field.Nx * 2 + 1, nz = field.Nz * 2 + 1;
        double step = field.CellSize / 2;
        var positions = new Vector3[nx * nz];
        var colors = new Vector4[nx * nz];
        float lift = (float)Math.Max(3, field.CellSize / 25);
        Parallel.For(0, nz, b =>
        {
            for (int a = 0; a < nx; a++)
            {
                double x = field.X0 + a * step, z = field.Z0 + b * step;
                positions[b * nx + a] = new Vector3((float)x, ground(x, z) + lift, (float)z);
                var color = options.GroundMap == GroundMap.LandCover
                    ? AirflowColors.Of((LandCover)field.LandCover[Column(field, x, z)])
                    : AirflowColors.Heating.At(field.MapAt(field.SurfaceHeat, x, z));
                colors[b * nx + a] = Rgba(color, 0.6f);
            }
        });
        return new OverlayGeometry(positions, Grid(nx, nz), colors);
    }

    private static int Column(AirflowField field, double x, double z) =>
        Math.Clamp((int)((z - field.Z0) / field.CellSize), 0, field.Nz - 1) * field.Nx + Math.Clamp((int)((x - field.X0) / field.CellSize), 0, field.Nx - 1);

    // A pin per trigger, taller for likelier ones, and a dot on top.
    private static OverlayGeometry TriggerGeometry(AirflowField field, Func<double, double, float> ground, bool tops)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        float height = (float)Math.Max(60, field.CellSize);
        foreach (var trigger in field.Triggers)
        {
            float y = ground(trigger.X, trigger.Z), top = y + height * (float)(1 + Math.Min(trigger.Score, 3));
            var color = Rgba(AirflowColors.Heating.At((float)trigger.Heat), 1);
            if (tops)
            {
                indices.Add((uint)positions.Count);
                positions.Add(new Vector3((float)trigger.X, top, (float)trigger.Z));
                colors.Add(color);
                continue;
            }
            indices.Add((uint)positions.Count);
            indices.Add((uint)positions.Count + 1);
            positions.Add(new Vector3((float)trigger.X, y, (float)trigger.Z));
            positions.Add(new Vector3((float)trigger.X, top, (float)trigger.Z));
            colors.Add(color);
            colors.Add(color);
        }
        return new OverlayGeometry([.. positions], [.. indices], [.. colors]);
    }

    /// <summary>One overlay's mesh and instance in the scene.</summary>
    private sealed class Overlay
    {
        private readonly Mesh3D _mesh;
        private readonly MeshInstance3D _instance;

        public Overlay(Scene3D scene, PrimitiveTopology topology, Material3D material)
        {
            _mesh = new Mesh3D(topology);
            _instance = new MeshInstance3D(_mesh, material) { IsVisible = false };
            scene.Instances.Add(_instance);
        }

        public void Set(OverlayGeometry geometry)
        {
            _instance.IsVisible = geometry.Indices.Length > 0;
            if (geometry.Indices.Length == 0) return;
            _mesh.SetGeometry(geometry.Positions, geometry.Indices, geometry.Normals, colors: geometry.Colors);
        }
    }
}
