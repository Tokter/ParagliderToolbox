using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Terrain.Features;

namespace ParagliderToolbox.Modules.Features;

/// <summary>Which features the view shows.</summary>
public sealed record FeatureViewOptions
{
    /// <summary>Gets whether trees and bushes show.</summary>
    public bool Trees { get; init; } = true;

    /// <summary>Gets whether roads and paths (and their junctions) show.</summary>
    public bool Roads { get; init; } = true;

    /// <summary>Gets whether buildings show.</summary>
    public bool Buildings { get; init; } = true;

    /// <summary>Gets whether boulders show.</summary>
    public bool Rocks { get; init; } = true;

    /// <summary>Gets whether the areas' outlines show.</summary>
    public bool Areas { get; init; } = true;

    /// <summary>Gets whether the ground cover map shows.</summary>
    public bool LandCover { get; init; }

    /// <summary>Gets whether the lines show: waterways, power lines, aerialways, railways, fences, cliffs.</summary>
    public bool Lines { get; init; } = true;

    /// <summary>Gets whether the landmarks show as pins.</summary>
    public bool Landmarks { get; init; } = true;
}

/// <summary>The geometry of one overlay: positions, indices, colors and optional normals.</summary>
internal sealed record FeatureMesh(Vector3[] Positions, uint[] Indices, Vector4[] Colors, Vector3[]? Normals = null)
{
    public static FeatureMesh Empty { get; } = new([], [], []);
}

/// <summary>Everything the overlays show of one feature set and set of options, built off the UI thread.</summary>
internal sealed record FeatureGeometry(
    FeatureMesh Bounds, FeatureMesh Trees, FeatureMesh Roads, FeatureMesh Junctions, FeatureMesh Buildings, FeatureMesh Rocks, FeatureMesh Areas,
    FeatureMesh LandCover, FeatureMesh Wires, FeatureMesh Strips, FeatureMesh Pins, FeatureMesh PinHeads);

/// <summary>
/// The features in a terrain's scene: the area searched (its outline), trees as cones (conifers) and diamonds, roads and
/// paths as ribbons colored by kind with their junctions, buildings as boxes with their roofs, boulders, the areas'
/// outlines colored by kind and the ground cover map. Everything sits on the terrain shown (its heights, not the finer
/// ones the features were found with).
/// </summary>
/// <remarks><see cref="Build"/> makes the geometry (on any thread); <see cref="Apply"/> puts it into the scene on the UI thread.</remarks>
public sealed class FeaturesScene
{
    /// <summary>The colors of the road kinds (sRGB 0–1, as the legend shows them).</summary>
    public static readonly IReadOnlyDictionary<RoadKind, Vector3> RoadColors = new Dictionary<RoadKind, Vector3>
    {
        [RoadKind.MainRoad] = new(0.90f, 0.18f, 0.15f),
        [RoadKind.Road] = new(0.98f, 0.60f, 0.10f),
        [RoadKind.Track] = new(0.98f, 0.88f, 0.30f),
        [RoadKind.Path] = new(0.95f, 0.45f, 0.85f),
    };

    /// <summary>The colors of the tree kinds (RGB 0–1).</summary>
    public static readonly IReadOnlyDictionary<TreeKind, Vector3> TreeColors = new Dictionary<TreeKind, Vector3>
    {
        [TreeKind.Conifer] = new(0.10f, 0.36f, 0.16f),
        [TreeKind.Broadleaf] = new(0.36f, 0.62f, 0.18f),
        [TreeKind.Unknown] = new(0.20f, 0.50f, 0.25f),
        [TreeKind.Bush] = new(0.55f, 0.75f, 0.30f),
    };

    /// <summary>The colors of the area kinds (RGB 0–1).</summary>
    public static readonly IReadOnlyDictionary<AreaKind, Vector3> AreaColors = new Dictionary<AreaKind, Vector3>
    {
        [AreaKind.Forest] = new(0.10f, 0.55f, 0.20f),
        [AreaKind.Rock] = new(0.75f, 0.75f, 0.75f),
        [AreaKind.Cliff] = new(0.30f, 0.30f, 0.32f),
        [AreaKind.Gravel] = new(0.92f, 0.82f, 0.60f),
        [AreaKind.Paved] = new(0.45f, 0.45f, 0.48f),
        [AreaKind.Water] = new(0.20f, 0.50f, 1.00f),
        [AreaKind.Snow] = new(1.00f, 1.00f, 1.00f),
        [AreaKind.Scree] = new(0.62f, 0.60f, 0.55f),
        [AreaKind.Scrub] = new(0.35f, 0.50f, 0.20f),
        [AreaKind.Grassland] = new(0.60f, 0.85f, 0.40f),
        [AreaKind.Farmland] = new(0.90f, 0.75f, 0.35f),
        [AreaKind.Orchard] = new(0.70f, 0.90f, 0.30f),
        [AreaKind.Vineyard] = new(0.65f, 0.30f, 0.55f),
        [AreaKind.Wetland] = new(0.30f, 0.70f, 0.70f),
        [AreaKind.Sand] = new(0.95f, 0.88f, 0.60f),
        [AreaKind.BuiltUp] = new(0.85f, 0.55f, 0.55f),
    };

    /// <summary>The colors of the line kinds (RGB 0–1).</summary>
    public static readonly IReadOnlyDictionary<LineKind, Vector3> LineColors = new Dictionary<LineKind, Vector3>
    {
        [LineKind.River] = new(0.15f, 0.45f, 1.00f),
        [LineKind.Stream] = new(0.30f, 0.60f, 1.00f),
        [LineKind.Canal] = new(0.15f, 0.45f, 1.00f),
        [LineKind.Ditch] = new(0.45f, 0.70f, 1.00f),
        [LineKind.PowerLine] = new(1.00f, 0.25f, 0.20f),
        [LineKind.MinorPowerLine] = new(1.00f, 0.55f, 0.45f),
        [LineKind.Aerialway] = new(0.75f, 0.30f, 1.00f),
        [LineKind.Railway] = new(0.45f, 0.30f, 0.20f),
        [LineKind.Fence] = new(0.85f, 0.85f, 0.80f),
        [LineKind.Wall] = new(0.70f, 0.68f, 0.65f),
        [LineKind.Hedge] = new(0.20f, 0.45f, 0.15f),
        [LineKind.Cliff] = new(0.35f, 0.20f, 0.10f),
    };

    /// <summary>The colors of the landmarks' pin heads (RGB 0–1): flying sites green and blue, obstacles red, peaks white, the rest yellow.</summary>
    public static Vector3 LandmarkColor(LandmarkKind kind) => kind switch
    {
        LandmarkKind.Takeoff or LandmarkKind.FlyingSite => new(0.20f, 0.95f, 0.35f),
        LandmarkKind.Landing => new(0.25f, 0.60f, 1.00f),
        LandmarkKind.Mast or LandmarkKind.Tower => new(1.00f, 0.20f, 0.20f),
        LandmarkKind.Peak or LandmarkKind.Saddle or LandmarkKind.Cross => new(1.00f, 1.00f, 1.00f),
        _ => new(1.00f, 0.85f, 0.25f),
    };

    // Lines and ribbons float a little above the terrain, so they aren't hidden in it between its samples.
    private const float Lift = 1.2f;

    // The palettes are sRGB, as the legends show them; the renderer takes vertex colors as linear.
    private static Vector4 Rgba(Vector3 srgb, float alpha = 1) =>
        new(MathF.Pow(srgb.X, 2.2f), MathF.Pow(srgb.Y, 2.2f), MathF.Pow(srgb.Z, 2.2f), alpha);

    private readonly Overlay _bounds, _trees, _roads, _junctions, _buildings, _rocks, _areas, _landCover, _wires, _strips, _pins, _pinHeads;

    /// <summary>Initializes the overlays in <paramref name="scene"/>, empty.</summary>
    public FeaturesScene(Scene3D scene)
    {
        var white = Color.FromRgb(255, 255, 255);
        var see = Color.FromArgb(254, 255, 255, 255);
        _landCover = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = see, Unlit = true, DoubleSided = true });
        _bounds = new Overlay(scene, PrimitiveTopology.Lines, new Material3D { BaseColor = white, Unlit = true });
        _areas = new Overlay(scene, PrimitiveTopology.Lines, new Material3D { BaseColor = white, Unlit = true });
        _roads = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = white, Unlit = true, DoubleSided = true });
        _junctions = new Overlay(scene, PrimitiveTopology.Points, new Material3D { BaseColor = white, Unlit = true, PointSize = 6 });
        _trees = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = white, Roughness = 0.9f, DoubleSided = true });
        _buildings = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = white, Roughness = 0.8f, DoubleSided = true });
        _rocks = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = white, Roughness = 0.9f, DoubleSided = true });
        _wires = new Overlay(scene, PrimitiveTopology.Lines, new Material3D { BaseColor = white, Unlit = true });
        _strips = new Overlay(scene, PrimitiveTopology.Triangles, new Material3D { BaseColor = white, Unlit = true, DoubleSided = true });
        _pins = new Overlay(scene, PrimitiveTopology.Lines, new Material3D { BaseColor = white, Unlit = true });
        _pinHeads = new Overlay(scene, PrimitiveTopology.Points, new Material3D { BaseColor = white, Unlit = true, PointSize = 10 });
    }

    /// <summary>Hides everything.</summary>
    public void Clear()
    {
        var empty = FeatureMesh.Empty;
        Apply(new FeatureGeometry(empty, empty, empty, empty, empty, empty, empty, empty, empty, empty, empty, empty));
    }

    /// <summary>Puts <paramref name="geometry"/> into the scene.</summary>
    internal void Apply(FeatureGeometry geometry)
    {
        _bounds.Set(geometry.Bounds);
        _trees.Set(geometry.Trees);
        _roads.Set(geometry.Roads);
        _junctions.Set(geometry.Junctions);
        _buildings.Set(geometry.Buildings);
        _rocks.Set(geometry.Rocks);
        _areas.Set(geometry.Areas);
        _landCover.Set(geometry.LandCover);
        _wires.Set(geometry.Wires);
        _strips.Set(geometry.Strips);
        _pins.Set(geometry.Pins);
        _pinHeads.Set(geometry.PinHeads);
    }

    /// <summary>Builds the overlays of <paramref name="features"/> (or only the area's outline without them).</summary>
    /// <param name="features">The features, or <c>null</c>.</param>
    /// <param name="area">The area searched (or to search).</param>
    /// <param name="options">What to show.</param>
    /// <param name="ground">The terrain's height at a point (NaN outside it).</param>
    internal static FeatureGeometry Build(FeatureSet? features, FeatureArea area, FeatureViewOptions options, Func<double, double, float> ground)
    {
        float fallback = 0;
        float Ground(double x, double z)
        {
            float h = ground(x, z);
            if (float.IsNaN(h)) return fallback;
            fallback = h;
            return h;
        }
        var empty = FeatureMesh.Empty;
        var bounds = BoundsGeometry(area, Ground);
        if (features is null) return new FeatureGeometry(bounds, empty, empty, empty, empty, empty, empty, empty, empty, empty, empty, empty);
        var (wires, strips) = options.Lines ? LineGeometry(features, Ground) : (empty, empty);
        var (pins, heads) = options.Landmarks ? LandmarkGeometry(features, Ground) : (empty, empty);
        return new FeatureGeometry(
            bounds,
            options.Trees ? TreeGeometry(features, Ground) : empty,
            options.Roads ? RoadGeometry(features, Ground) : empty,
            options.Roads ? JunctionGeometry(features, Ground) : empty,
            options.Buildings ? BuildingGeometry(features, Ground) : empty,
            options.Rocks ? RockGeometry(features, Ground) : empty,
            options.Areas ? AreaGeometry(features, Ground) : empty,
            options.LandCover && features.Covers is { } covers ? CoverGeometry(covers, Ground) : empty,
            wires, strips, pins, heads);
    }

    // Wires from tower to tower (sagging a little) with the towers as posts; waterways and railways as ribbons on the
    // ground; fences, walls and hedges standing up; cliffs' edges as lines.
    private static (FeatureMesh Wires, FeatureMesh Strips) LineGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        var stripPositions = new List<Vector3>();
        var stripColors = new List<Vector4>();
        var stripIndices = new List<uint>();
        void Segment(Vector3 a, Vector3 b, Vector4 color)
        {
            indices.Add((uint)positions.Count);
            indices.Add((uint)positions.Count + 1);
            positions.Add(a);
            positions.Add(b);
            colors.Add(color);
            colors.Add(color);
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 color)
        {
            uint first = (uint)stripPositions.Count;
            stripPositions.AddRange([a, b, c, d]);
            stripColors.AddRange([color, color, color, color]);
            stripIndices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        foreach (var line in features.Lines)
        {
            if (line.Points.Count < 2 || line.Structure == RoadStructure.Tunnel) continue;
            var color = Rgba(LineColors[line.Kind]);
            var points = line.Points.Select(p => new Vector3(p.X, Math.Max(ground(p.X, p.Z), line.Structure == RoadStructure.Bridge ? p.Y : float.MinValue), p.Z)).ToList();
            switch (line.Kind)
            {
                case LineKind.PowerLine or LineKind.MinorPowerLine or LineKind.Aerialway:
                    float height = Math.Max(2, line.Height);
                    foreach (var p in points) Segment(p, p + new Vector3(0, height, 0), color);
                    for (int i = 1; i < points.Count; i++)
                    {
                        var a = points[i - 1] + new Vector3(0, height, 0);
                        var b = points[i] + new Vector3(0, height, 0);
                        float sag = 0.03f * Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
                        var previous = a;
                        for (int k = 1; k <= 8; k++)
                        {
                            float t = k / 8f;
                            var next = Vector3.Lerp(a, b, t) - new Vector3(0, 4 * sag * t * (1 - t), 0);
                            Segment(previous, next, color);
                            previous = next;
                        }
                    }
                    break;
                case LineKind.Cliff:
                    for (int i = 1; i < points.Count; i++) Segment(points[i - 1] + new Vector3(0, Lift, 0), points[i] + new Vector3(0, Lift, 0), color);
                    break;
                case LineKind.Fence or LineKind.Wall or LineKind.Hedge:
                    float top = Math.Max(1, line.Height);
                    for (int i = 1; i < points.Count; i++)
                    {
                        var a = points[i - 1];
                        var b = points[i];
                        Quad(a, b, b + new Vector3(0, top, 0), a + new Vector3(0, top, 0), color);
                    }
                    break;
                default:
                    // Ribbons at least 2 m wide, so streams show.
                    float half = Math.Max(1, line.Width / 2);
                    for (int i = 1; i < points.Count; i++)
                    {
                        var a = points[i - 1];
                        var b = points[i];
                        var along = new Vector2(b.X - a.X, b.Z - a.Z);
                        if (along.LengthSquared() < 1e-8f) continue;
                        along = Vector2.Normalize(along) * half;
                        var across = new Vector3(-along.Y, 0, along.X);
                        var lift = new Vector3(0, Lift, 0);
                        Quad(a - across + lift, b - across + lift, b + across + lift, a + across + lift, color);
                    }
                    break;
            }
        }
        return (new FeatureMesh([.. positions], [.. indices], [.. colors]), new FeatureMesh([.. stripPositions], [.. stripIndices], [.. stripColors]));
    }

    // A pin per landmark: a post as high as the structure (at least 15 m) and a head colored by its kind.
    private static (FeatureMesh Pins, FeatureMesh Heads) LandmarkGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        var heads = new List<Vector3>();
        var headColors = new List<Vector4>();
        var post = Rgba(new(0.9f, 0.9f, 0.9f));
        foreach (var landmark in features.Landmarks)
        {
            var foot = new Vector3(landmark.Position.X, ground(landmark.Position.X, landmark.Position.Z), landmark.Position.Z);
            var top = foot + new Vector3(0, Math.Max(15, landmark.Height ?? 0), 0);
            indices.Add((uint)positions.Count);
            indices.Add((uint)positions.Count + 1);
            positions.Add(foot);
            positions.Add(top);
            colors.Add(post);
            colors.Add(post);
            heads.Add(top);
            headColors.Add(Rgba(LandmarkColor(landmark.Kind)));
        }
        return (new FeatureMesh([.. positions], [.. indices], [.. colors]),
            new FeatureMesh([.. heads], Enumerable.Range(0, heads.Count).Select(i => (uint)i).ToArray(), [.. headColors]));
    }

    // The area's outline, a few meters above the terrain.
    private static FeatureMesh BoundsGeometry(FeatureArea area, Func<double, double, float> ground)
    {
        var corners = new[]
        {
            new Vector2((float)area.X0, (float)area.Z0), new Vector2((float)(area.X0 + area.Width), (float)area.Z0),
            new Vector2((float)(area.X0 + area.Width), (float)(area.Z0 + area.Depth)), new Vector2((float)area.X0, (float)(area.Z0 + area.Depth)),
        };
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        var color = Rgba(new(1, 0.85f, 0.2f));
        Polyline(corners.Append(corners[0]).ToList(), Math.Max(4, area.Width / 200), 3, ground, color, positions, colors, indices);
        return new FeatureMesh([.. positions], [.. indices], [.. colors]);
    }

    // A line along the points, every `step` meters at most, `lift` above the terrain.
    private static void Polyline(IReadOnlyList<Vector2> points, double step, float lift, Func<double, double, float> ground, Vector4 color,
        List<Vector3> positions, List<Vector4> colors, List<uint> indices)
    {
        uint first = (uint)positions.Count;
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                var a = points[i - 1];
                var b = points[i];
                int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(a, b) / step));
                for (int k = 1; k < steps; k++)
                {
                    var p = Vector2.Lerp(a, b, (float)k / steps);
                    positions.Add(new Vector3(p.X, ground(p.X, p.Y) + lift, p.Y));
                    colors.Add(color);
                }
            }
            positions.Add(new Vector3(points[i].X, ground(points[i].X, points[i].Y) + lift, points[i].Y));
            colors.Add(color);
        }
        for (uint i = first; i + 1 < positions.Count; i++)
        {
            indices.Add(i);
            indices.Add(i + 1);
        }
    }

    // Conifers as cones from a fifth of their height, broadleaf trees and bushes as diamonds around their crown.
    private static FeatureMesh TreeGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        const int sides = 6;
        var trees = features.Trees;
        // A cone: tip, ring; a diamond: top, ring, bottom.
        var positions = new Vector3[trees.Count * (sides + 2)];
        var normals = new Vector3[positions.Length];
        var colors = new Vector4[positions.Length];
        var indices = new uint[trees.Count * sides * 6];
        Parallel.For(0, trees.Count, n =>
        {
            var tree = trees[n];
            float y = ground(tree.Position.X, tree.Position.Z), h = Math.Max(1, tree.Height), r = Math.Max(0.5f, tree.CrownRadius);
            bool cone = tree.Kind == TreeKind.Conifer;
            float bottom = y + (cone ? 0.2f * h : tree.Kind == TreeKind.Bush ? 0.1f * h : 0.3f * h);
            float ring = cone ? bottom : (bottom + y + h) / 2;
            var color = Rgba(TreeColors[tree.Kind]);
            int v = n * (sides + 2), t = n * sides * 6;
            var center = new Vector3(tree.Position.X, 0, tree.Position.Z);
            positions[v] = center with { Y = y + h };
            normals[v] = Vector3.UnitY;
            positions[v + sides + 1] = center with { Y = bottom };
            normals[v + sides + 1] = -Vector3.UnitY;
            for (int s = 0; s < sides; s++)
            {
                double a = 2 * Math.PI * s / sides;
                var outward = new Vector3((float)Math.Cos(a), 0, (float)Math.Sin(a));
                positions[v + 1 + s] = center with { Y = ring } + outward * r;
                normals[v + 1 + s] = Vector3.Normalize(outward + Vector3.UnitY * (cone ? r / h : 0));
            }
            for (int k = 0; k < sides + 2; k++) colors[v + k] = color;
            for (int s = 0; s < sides; s++)
            {
                uint a = (uint)(v + 1 + s), b = (uint)(v + 1 + (s + 1) % sides);
                // Up to the top, down to the bottom (a cone's bottom closes it).
                indices[t++] = (uint)v;
                indices[t++] = b;
                indices[t++] = a;
                indices[t++] = (uint)(v + sides + 1);
                indices[t++] = a;
                indices[t++] = b;
            }
        });
        return new FeatureMesh(positions, indices, colors, normals);
    }

    // A ribbon along each road as wide as the road (at least 2 m, so paths show), colored by its kind; bridges at their
    // own heights (where above the terrain), tunnels not at all.
    private static FeatureMesh RoadGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        foreach (var road in features.Roads)
        {
            if (road.Points.Count < 2 || road.Structure == RoadStructure.Tunnel) continue;
            var line = new List<Vector3>();
            for (int i = 0; i < road.Points.Count; i++)
            {
                var p = road.Points[i];
                if (i > 0)
                {
                    var a = line[^1];
                    int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(new(a.X, a.Z), new(p.X, p.Z)) / 3));
                    for (int k = 1; k < steps; k++) line.Add(Vector3.Lerp(a, p, (float)k / steps));
                }
                line.Add(p);
            }
            bool bridge = road.Structure == RoadStructure.Bridge;
            float half = Math.Max(1, road.Width / 2);
            var color = Rgba(RoadColors[road.Kind]);
            uint first = (uint)positions.Count;
            for (int i = 0; i < line.Count; i++)
            {
                var next = line[Math.Min(i + 1, line.Count - 1)];
                var previous = line[Math.Max(i - 1, 0)];
                var along = new Vector2(next.X - previous.X, next.Z - previous.Z);
                if (along.LengthSquared() < 1e-8f) along = Vector2.UnitX;
                along = Vector2.Normalize(along);
                var across = new Vector2(-along.Y, along.X) * half;
                var center = new Vector2(line[i].X, line[i].Z);
                foreach (var p in new[] { center - across, center + across })
                {
                    float y = ground(p.X, p.Y);
                    if (bridge) y = Math.Max(y, line[i].Y);
                    positions.Add(new Vector3(p.X, y + Lift, p.Y));
                    colors.Add(color);
                }
            }
            for (uint i = 0; i + 1 < line.Count; i++)
            {
                uint a = first + 2 * i;
                indices.AddRange([a, a + 2, a + 1, a + 1, a + 2, a + 3]);
            }
        }
        return new FeatureMesh([.. positions], [.. indices], [.. colors]);
    }

    private static FeatureMesh JunctionGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var positions = features.Junctions.Select(j => new Vector3(j.X, ground(j.X, j.Z) + Lift + 0.3f, j.Z)).ToArray();
        var colors = Enumerable.Repeat(new Vector4(1, 1, 1, 1), positions.Length).ToArray();
        return new FeatureMesh(positions, Enumerable.Range(0, positions.Length).Select(i => (uint)i).ToArray(), colors);
    }

    // A box per building (its rectangle, up to its eaves) with a gable roof along its long side, or a flat one.
    private static FeatureMesh BuildingGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        var wall = Rgba(new(0.86f, 0.84f, 0.80f));
        void Face(Vector4 color, params Vector3[] corners)
        {
            var normal = Vector3.Normalize(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]));
            uint first = (uint)positions.Count;
            foreach (var c in corners)
            {
                positions.Add(c);
                normals.Add(normal);
                colors.Add(color);
            }
            for (uint k = 1; k + 1 < corners.Length; k++) indices.AddRange([first, first + k, first + k + 1]);
        }
        foreach (var building in features.Buildings)
        {
            float angle = building.Angle * MathF.PI / 180;
            // The long side's direction (clockwise from north) and the short side's.
            var u = new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * building.Length / 2;
            var v = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * building.Width / 2;
            var c = new Vector2(building.Position.X, building.Position.Z);
            var corners = new[] { c - u - v, c + u - v, c + u + v, c - u + v };
            float y = corners.Min(p => ground(p.X, p.Y));
            float eaves = y + Math.Max(2, building.Roof == RoofShape.Pitched ? building.Eaves : building.Height), top = y + Math.Max(2.5f, building.Height);
            Vector3 At(Vector2 p, float height) => new(p.X, height, p.Y);
            // Walls, counterclockwise seen from outside whichever way the corners turn.
            bool turn = Polygons.SignedArea(corners) < 0;
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % 4];
                if (turn) Face(wall, At(a, y), At(a, eaves), At(b, eaves), At(b, y));
                else Face(wall, At(a, y), At(b, y), At(b, eaves), At(a, eaves));
            }
            if (building.Roof == RoofShape.Flat || top - eaves < 0.5f)
            {
                var roof = Rgba(new(0.55f, 0.55f, 0.58f));
                if (turn) Face(roof, At(corners[0], eaves), At(corners[3], eaves), At(corners[2], eaves), At(corners[1], eaves));
                else Face(roof, At(corners[0], eaves), At(corners[1], eaves), At(corners[2], eaves), At(corners[3], eaves));
                continue;
            }
            // The ridge runs along the long side through the middle.
            var red = Rgba(new(0.62f, 0.25f, 0.18f));
            Vector3 ridgeA = At(c - u, top), ridgeB = At(c + u, top);
            void Slope(Vector2 p, Vector2 q, Vector3 r1, Vector3 r2)
            {
                var face = new[] { At(p, eaves), At(q, eaves), r2, r1 };
                var normal = Vector3.Cross(face[1] - face[0], face[2] - face[0]);
                if (normal.Y < 0) Array.Reverse(face);
                Face(red, face);
            }
            Slope(corners[0], corners[1], ridgeA, ridgeB);
            Slope(corners[3], corners[2], ridgeA, ridgeB);
            void Gable(Vector2 p, Vector2 q, Vector3 r)
            {
                var face = new[] { At(p, eaves), At(q, eaves), r };
                var middle = (p + q) / 2;
                var normal = Vector3.Cross(face[1] - face[0], face[2] - face[0]);
                if (Vector2.Dot(new Vector2(normal.X, normal.Z), middle - c) < 0) Array.Reverse(face);
                Face(wall, face);
            }
            Gable(corners[0], corners[3], ridgeA);
            Gable(corners[1], corners[2], ridgeB);
        }
        return new FeatureMesh([.. positions], [.. indices], [.. colors], [.. normals]);
    }

    // A flattened diamond per boulder.
    private static FeatureMesh RockGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var rocks = features.Rocks;
        var positions = new Vector3[rocks.Count * 6];
        var normals = new Vector3[positions.Length];
        var colors = new Vector4[positions.Length];
        var indices = new uint[rocks.Count * 24];
        var color = Rgba(new(0.62f, 0.60f, 0.57f));
        Parallel.For(0, rocks.Count, n =>
        {
            var rock = rocks[n];
            float y = ground(rock.Position.X, rock.Position.Z);
            var center = new Vector3(rock.Position.X, y + rock.Height * 0.3f, rock.Position.Z);
            Vector3[] offsets = [new(0, rock.Height * 0.7f, 0), new(rock.Radius, 0, 0), new(0, 0, rock.Radius), new(-rock.Radius, 0, 0), new(0, 0, -rock.Radius), new(0, -rock.Height * 0.3f, 0)];
            int v = n * 6;
            for (int k = 0; k < 6; k++)
            {
                positions[v + k] = center + offsets[k];
                normals[v + k] = Vector3.Normalize(offsets[k] + new Vector3(0, 0.01f, 0));
                colors[v + k] = color;
            }
            int t = n * 24;
            for (int s = 0; s < 4; s++)
            {
                uint a = (uint)(v + 1 + s), b = (uint)(v + 1 + (s + 1) % 4);
                indices[t++] = (uint)v;
                indices[t++] = b;
                indices[t++] = a;
                indices[t++] = (uint)(v + 5);
                indices[t++] = a;
                indices[t++] = b;
            }
        });
        return new FeatureMesh(positions, indices, colors, normals);
    }

    // The outlines and holes of the areas, colored by kind.
    private static FeatureMesh AreaGeometry(FeatureSet features, Func<double, double, float> ground)
    {
        var positions = new List<Vector3>();
        var colors = new List<Vector4>();
        var indices = new List<uint>();
        foreach (var area in features.Areas)
        {
            var color = Rgba(AreaColors[area.Kind]);
            foreach (var ring in area.Holes.Prepend(area.Outline))
            {
                if (ring.Count < 2) continue;
                Polyline(ring.Append(ring[0]).ToList(), 4, Lift, ground, color, positions, colors, indices);
            }
        }
        return new FeatureMesh([.. positions], [.. indices], [.. colors]);
    }

    // The cover map on the terrain, at most 400 cells across.
    private static FeatureMesh CoverGeometry(CoverMap covers, Func<double, double, float> ground)
    {
        double width = covers.Columns * covers.Spacing, depth = covers.Rows * covers.Spacing;
        double step = Math.Max(covers.Spacing, Math.Max(width, depth) / 400);
        int nx = (int)Math.Ceiling(width / step) + 1, nz = (int)Math.Ceiling(depth / step) + 1;
        var positions = new Vector3[nx * nz];
        var colors = new Vector4[nx * nz];
        Parallel.For(0, nz, b =>
        {
            for (int a = 0; a < nx; a++)
            {
                double x = covers.X0 + Math.Min(a * step, width), z = covers.Z0 + Math.Min(b * step, depth);
                positions[b * nx + a] = new Vector3((float)x, ground(x, z) + Lift, (float)z);
                var cover = covers.At(Math.Min(x, covers.X0 + width - covers.Spacing / 2), Math.Min(z, covers.Z0 + depth - covers.Spacing / 2));
                uint rgba = GroundCovers.MapColors[(int)cover];
                colors[b * nx + a] = Rgba(new((rgba & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f), cover == GroundCover.None ? 0 : 0.55f);
            }
        });
        var indices = new uint[(nx - 1) * (nz - 1) * 6];
        int n = 0;
        for (int b = 0; b + 1 < nz; b++)
        {
            for (int a = 0; a + 1 < nx; a++)
            {
                uint i = (uint)(b * nx + a), j = i + 1, k = i + (uint)nx, l = k + 1;
                indices[n++] = i;
                indices[n++] = k;
                indices[n++] = j;
                indices[n++] = j;
                indices[n++] = k;
                indices[n++] = l;
            }
        }
        return new FeatureMesh(positions, indices, colors);
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

        public void Set(FeatureMesh geometry)
        {
            _instance.IsVisible = geometry.Indices.Length > 0;
            if (geometry.Indices.Length == 0) return;
            _mesh.SetGeometry(geometry.Positions, geometry.Indices, geometry.Normals, colors: geometry.Colors);
        }
    }
}
