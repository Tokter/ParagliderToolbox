using System.Globalization;
using System.Numerics;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>
/// A road from a map on its way into a <see cref="RoadFeature"/>: its center line (the ground's heights still to come,
/// NaN), its attributes from the map's tags, and the images' ground cover along it, which decides the surface where the
/// map doesn't tell.
/// </summary>
internal sealed class MapRoad
{
    public required List<Vector3> Points { get; init; }

    public required string Class { get; init; }

    public required float Width { get; init; }

    public required RoadStructure Structure { get; init; }

    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    /// <summary>Gets the surface the map gives, or <c>null</c>.</summary>
    public RoadSurface? Surface { get; init; }

    /// <summary>Gets the surface without the map's or the images' word.</summary>
    public RoadSurface DefaultSurface { get; init; }

    /// <summary>Gets the surfaces the images may choose from (none where the map's class is clear).</summary>
    public RoadSurface[] Choices { get; init; } = [];

    /// <summary>Gets the node at the first point (a map node's id), or −1 where the road was cut at the area's edge.</summary>
    public long Start { get; init; }

    /// <summary>Gets the node at the last point, or −1.</summary>
    public long End { get; init; }

    /// <summary>Gets how many points of the line the images saw as each <see cref="GroundCover"/>.</summary>
    public int[] Tally { get; } = new int[16];

    /// <summary>Decides the surface: the map's, else the images' where enough of the line shows a road surface, else the default.</summary>
    public RoadSurface ResolveSurface()
    {
        if (Surface is { } surface) return surface;
        int paved = Tally[(int)GroundCover.Paved], gravel = Tally[(int)GroundCover.Gravel], dirt = Tally[(int)GroundCover.Soil];
        int seen = Tally.Sum();
        if (Choices.Length == 0 || seen == 0 || paved + gravel + dirt < 0.4 * seen) return DefaultSurface;
        return Choices.MaxBy(c => c switch { RoadSurface.Paved => paved, RoadSurface.Gravel => gravel, _ => dirt });
    }
}

/// <summary>
/// Roads and paths from map ways (OpenStreetMap's <c>highway</c>s): the ways split where they share a node and joined
/// again where two of the same tags meet end to end, so every road runs between junctions (three or more roads, or a
/// change of kind) and ends; cut at the area's edge; with their kind, width, surface and structure from the tags.
/// </summary>
public static class MapRoads
{
    private static readonly HashSet<string> s_main =
        ["motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link", "secondary", "secondary_link"];

    private static readonly HashSet<string> s_roads =
        ["tertiary", "tertiary_link", "unclassified", "residential", "living_street", "service", "road", "pedestrian", "busway"];

    private static readonly HashSet<string> s_paths = ["path", "footway", "cycleway", "bridleway", "steps", "via_ferrata"];

    private static readonly Dictionary<string, float> s_widths = new()
    {
        ["motorway"] = 11, ["motorway_link"] = 6, ["trunk"] = 9, ["trunk_link"] = 6, ["primary"] = 7.5f, ["primary_link"] = 6,
        ["secondary"] = 7, ["secondary_link"] = 5.5f, ["tertiary"] = 6, ["tertiary_link"] = 5, ["unclassified"] = 5, ["residential"] = 5.5f,
        ["living_street"] = 4.5f, ["service"] = 3.5f, ["road"] = 5, ["pedestrian"] = 5, ["busway"] = 3.5f, ["track"] = 3,
        ["path"] = 1, ["footway"] = 1.5f, ["cycleway"] = 2.5f, ["bridleway"] = 1.5f, ["steps"] = 1.5f, ["via_ferrata"] = 0.5f,
    };

    private static readonly HashSet<string> s_paved =
    [
        "asphalt", "concrete", "concrete:plates", "concrete:lanes", "paved", "paving_stones", "paving_stones:lanes", "sett", "cobblestone",
        "unhewn_cobblestone", "bricks", "chipseal", "metal", "wood", "rubber",
    ];

    private static readonly HashSet<string> s_gravel = ["gravel", "fine_gravel", "compacted", "pebblestone", "rock", "stone", "shells"];

    private static readonly HashSet<string> s_dirt = ["dirt", "ground", "earth", "grass", "mud", "sand", "woodchips", "soil", "clay", "snow", "ice", "grass_paver"];

    /// <summary>The tags kept with a road (what a game may want beyond its kind, width and surface).</summary>
    public static readonly IReadOnlyList<string> KeptTags =
    [
        "highway", "name", "ref", "surface", "tracktype", "smoothness", "width", "lanes", "oneway", "bridge", "tunnel", "layer", "sac_scale",
        "trail_visibility", "access", "motor_vehicle", "foot", "bicycle", "mtb:scale", "incline", "service",
    ];

    /// <summary>Gets whether a <c>highway</c> value is a road or path (not a platform, a road under construction …).</summary>
    public static bool IsRoad(string highway) => s_main.Contains(highway) || s_roads.Contains(highway) || s_paths.Contains(highway) || highway == "track";

    /// <summary>
    /// Gets a road's kind from its class and surface: motorways to secondary roads are main roads; tertiary, residential,
    /// service and similar roads are roads, or tracks where unpaved; tracks are tracks; paths, footways, cycleways,
    /// bridleways and steps are paths.
    /// </summary>
    public static RoadKind KindOf(string highway, RoadSurface surface) =>
        s_main.Contains(highway) ? RoadKind.MainRoad
        : s_roads.Contains(highway) ? surface == RoadSurface.Paved ? RoadKind.Road : RoadKind.Track
        : highway == "track" ? RoadKind.Track
        : RoadKind.Path;

    /// <summary>Gets a road's width (m): its <c>width</c> (or <c>est_width</c>) tag, else 3.25 m per lane, else the usual width of its class.</summary>
    public static float WidthOf(IReadOnlyDictionary<string, string> tags)
    {
        foreach (string key in new[] { "width", "est_width" })
        {
            if (tags.TryGetValue(key, out var text) && ParseMeters(text) is { } meters && meters is > 0.2f and < 60) return meters;
        }
        string highway = tags.GetValueOrDefault("highway", "");
        if ((s_main.Contains(highway) || s_roads.Contains(highway)) && tags.TryGetValue("lanes", out var lanes) &&
            int.TryParse(lanes, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count is > 0 and < 12)
        {
            return 3.25f * count;
        }
        return s_widths.GetValueOrDefault(highway, 3);
    }

    // "3", "3.5", "3 m", "3,5" (meters); "10'" or "10 ft" (feet).
    private static float? ParseMeters(string text)
    {
        text = text.Trim().Replace(',', '.');
        float factor = 1;
        if (text.EndsWith("ft", StringComparison.OrdinalIgnoreCase) || text.EndsWith('\''))
        {
            factor = 0.3048f;
            text = text.TrimEnd('\'').Replace("ft", "", StringComparison.OrdinalIgnoreCase);
        }
        text = text.Replace("m", "", StringComparison.OrdinalIgnoreCase).Trim();
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value * factor : null;
    }

    /// <summary>
    /// Gets the surface the tags give: <c>surface</c> (asphalt, concrete, paving stones … paved; gravel, compacted …
    /// gravel; dirt, ground, grass … dirt), else <c>tracktype</c> (grade 2–3 gravel, 4–5 dirt); <c>null</c> where they don't
    /// tell (the images decide).
    /// </summary>
    public static RoadSurface? SurfaceOf(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.TryGetValue("surface", out var surface))
        {
            if (s_paved.Contains(surface)) return RoadSurface.Paved;
            if (s_gravel.Contains(surface)) return RoadSurface.Gravel;
            if (s_dirt.Contains(surface)) return RoadSurface.Dirt;
        }
        return tags.GetValueOrDefault("tracktype") switch
        {
            "grade2" or "grade3" => RoadSurface.Gravel,
            "grade4" or "grade5" => RoadSurface.Dirt,
            _ => null,
        };
    }

    // Where the tags don't tell: the class's usual surface, and what the images may choose.
    private static (RoadSurface Default, RoadSurface[] Choices) Unknown(IReadOnlyDictionary<string, string> tags)
    {
        string highway = tags.GetValueOrDefault("highway", "");
        RoadSurface[] all = [RoadSurface.Paved, RoadSurface.Gravel, RoadSurface.Dirt];
        if (tags.GetValueOrDefault("surface") == "unpaved") return (highway == "track" ? RoadSurface.Gravel : RoadSurface.Dirt, [RoadSurface.Gravel, RoadSurface.Dirt]);
        if (tags.GetValueOrDefault("tracktype") == "grade1") return (RoadSurface.Gravel, [RoadSurface.Paved, RoadSurface.Gravel]);
        return highway switch
        {
            "service" or "unclassified" or "footway" or "cycleway" or "steps" => (RoadSurface.Paved, all),
            "track" => (RoadSurface.Gravel, all),
            "path" or "bridleway" or "via_ferrata" => (RoadSurface.Dirt, all),
            _ => (RoadSurface.Paved, []),
        };
    }

    /// <summary>Gets a road's structure from its tags: on a bridge, in a tunnel (also through a building), or on the ground.</summary>
    public static RoadStructure StructureOf(IReadOnlyDictionary<string, string> tags) =>
        tags.TryGetValue("bridge", out var bridge) && bridge != "no" ? RoadStructure.Bridge
        : tags.TryGetValue("tunnel", out var tunnel) && tunnel != "no" ? RoadStructure.Tunnel
        : RoadStructure.Ground;

    /// <summary>Makes the roads of the ways in <paramref name="area"/>, points at most <paramref name="step"/> apart.</summary>
    internal static List<MapRoad> Build(IReadOnlyList<MapWay> ways, LocalFrame frame, FeatureArea area, double step = 5)
    {
        var roads = ways.Where(w => w.Tags.TryGetValue("highway", out var h) && IsRoad(h) && w.Tags.GetValueOrDefault("area") != "yes").ToList();

        // Split every way where another way (or itself again) shares a node.
        var uses = new Dictionary<long, int>();
        foreach (var way in roads)
        {
            foreach (long node in way.Nodes) uses[node] = uses.GetValueOrDefault(node) + 1;
        }
        var pieces = new List<(List<long> Nodes, List<Vector2> Points, IReadOnlyDictionary<string, string> Tags, string Signature)>();
        foreach (var way in roads)
        {
            var tags = KeptTags.Where(way.Tags.ContainsKey).ToDictionary(k => k, k => way.Tags[k]);
            string signature = string.Join(";", tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "=" + t.Value));
            var nodes = new List<long> { way.Nodes[0] };
            var points = new List<Vector2> { Local(way.Points[0]) };
            for (int i = 1; i < way.Nodes.Count; i++)
            {
                nodes.Add(way.Nodes[i]);
                points.Add(Local(way.Points[i]));
                if (i < way.Nodes.Count - 1 && uses[way.Nodes[i]] >= 2)
                {
                    pieces.Add((nodes, points, tags, signature));
                    nodes = [way.Nodes[i]];
                    points = [Local(way.Points[i])];
                }
            }
            pieces.Add((nodes, points, tags, signature));
        }
        Vector2 Local(GeoPoint point)
        {
            var (x, z) = frame.FromGeo(point);
            return new Vector2((float)x, (float)z);
        }

        // Join pieces of the same tags that meet in twos.
        var ends = new Dictionary<long, List<int>>();
        for (int i = 0; i < pieces.Count; i++)
        {
            foreach (long node in new[] { pieces[i].Nodes[0], pieces[i].Nodes[^1] })
            {
                if (!ends.TryGetValue(node, out var list)) ends[node] = list = [];
                list.Add(i);
            }
        }
        bool Joins(long node) => ends[node] is [var a, var b] && a != b && pieces[a].Signature == pieces[b].Signature;
        var used = new bool[pieces.Count];
        var result = new List<MapRoad>();
        void Walk(int first, long from)
        {
            var points = new List<Vector2>();
            int current = first;
            long node = from;
            while (true)
            {
                used[current] = true;
                var piece = pieces[current];
                bool forward = piece.Nodes[0] == node;
                var line = forward ? piece.Points : Enumerable.Reverse(piece.Points).ToList();
                points.AddRange(points.Count == 0 ? line : line.Skip(1));
                long other = forward ? piece.Nodes[^1] : piece.Nodes[0];
                if (!Joins(other) || other == from)
                {
                    Add(points, from, other, pieces[first].Tags);
                    return;
                }
                int next = ends[other][0] == current ? ends[other][1] : ends[other][0];
                if (used[next])
                {
                    Add(points, from, other, pieces[first].Tags);
                    return;
                }
                current = next;
                node = other;
            }
        }
        for (int i = 0; i < pieces.Count; i++)
        {
            if (used[i]) continue;
            if (!Joins(pieces[i].Nodes[0])) Walk(i, pieces[i].Nodes[0]);
            else if (!Joins(pieces[i].Nodes[^1])) Walk(i, pieces[i].Nodes[^1]);
        }
        // Rings of pieces joined all around.
        for (int i = 0; i < pieces.Count; i++)
        {
            if (!used[i]) Walk(i, pieces[i].Nodes[0]);
        }
        return result;

        // The road's parts inside the area, cut where it leaves it, points at most `step` apart.
        void Add(List<Vector2> points, long start, long end, IReadOnlyDictionary<string, string> tags)
        {
            var surface = SurfaceOf(tags);
            var (fallback, choices) = Unknown(tags);
            List<Vector2>? part = null;
            long partStart = -1;
            for (int k = 0; k + 1 < points.Count; k++)
            {
                if (!Clip(area, points[k], points[k + 1], out double t0, out double t1)) continue;
                var a = Vector2.Lerp(points[k], points[k + 1], (float)t0);
                var b = Vector2.Lerp(points[k], points[k + 1], (float)t1);
                if (part is null)
                {
                    part = [a];
                    partStart = k == 0 && t0 == 0 ? start : -1;
                }
                int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(a, b) / step));
                for (int s = 1; s <= steps; s++) part.Add(Vector2.Lerp(a, b, (float)s / steps));
                if (t1 < 1 || k + 2 == points.Count)
                {
                    long partEnd = t1 >= 1 && k + 2 == points.Count ? end : -1;
                    if (part.Count >= 2)
                    {
                        result.Add(new MapRoad
                        {
                            Points = part.Select(p => new Vector3(p.X, float.NaN, p.Y)).ToList(),
                            Class = tags.GetValueOrDefault("highway", ""),
                            Width = WidthOf(tags),
                            Structure = StructureOf(tags),
                            Tags = tags,
                            Surface = surface,
                            DefaultSurface = fallback,
                            Choices = surface is null ? choices : [],
                            Start = partStart,
                            End = partEnd,
                        });
                    }
                    part = null;
                }
            }
        }
    }

    // The part t0…t1 of segment a–b inside the area (Liang–Barsky); false when none is.
    private static bool Clip(FeatureArea area, Vector2 a, Vector2 b, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 1;
        double dx = b.X - a.X, dz = b.Y - a.Y;
        Span<double> p = [-dx, dx, -dz, dz];
        Span<double> q = [a.X - area.X0, area.X0 + area.Width - a.X, a.Y - area.Z0, area.Z0 + area.Depth - a.Y];
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0) return false;
                continue;
            }
            double r = q[i] / p[i];
            if (p[i] < 0) t0 = Math.Max(t0, r);
            else t1 = Math.Min(t1, r);
        }
        return t0 < t1;
    }
}
