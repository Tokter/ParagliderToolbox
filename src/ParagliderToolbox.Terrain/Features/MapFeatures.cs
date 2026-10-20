using System.Globalization;
using System.Numerics;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>A building of a map on its way into a <see cref="BuildingFeature"/>: the block that holds its center measures it.</summary>
internal sealed class MapBuilding
{
    public required List<Vector2> Footprint { get; init; }

    public required List<List<Vector2>> Holes { get; init; }

    public required string Class { get; init; }

    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    public Vector2 Center => Polygons.Centroid(Footprint);

    /// <summary>Gets or sets the lowest ground under it (m), NaN until a block measured it.</summary>
    public float Ground { get; set; } = float.NaN;

    /// <summary>Gets or sets its measured top and eaves above the ground (m), NaN without a surface model.</summary>
    public float Top { get; set; } = float.NaN;

    public float Eaves { get; set; } = float.NaN;
}

/// <summary>A point of a map (a tree, a rock, a landmark): its position, the ground's height to come (NaN).</summary>
internal sealed class MapPoint
{
    public required List<Vector3> Point { get; init; }

    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    public required string Class { get; init; }
}

/// <summary>A line of a map on its way into a <see cref="LineFeature"/>.</summary>
internal sealed class MapLine
{
    public required List<Vector3> Points { get; init; }

    public required LineKind Kind { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    public required RoadStructure Structure { get; init; }

    public required string Class { get; init; }

    public required IReadOnlyDictionary<string, string> Tags { get; init; }
}

/// <summary>What a map has in an area, in the terrain's frame, before the blocks give it the ground's heights.</summary>
internal sealed class MapContent
{
    public List<MapRoad> Roads { get; } = [];

    public List<MapBuilding> Buildings { get; } = [];

    public List<AreaFeature> Areas { get; } = [];

    public List<MapLine> Lines { get; } = [];

    public List<MapPoint> Trees { get; } = [];

    public List<MapPoint> Rocks { get; } = [];

    public List<MapPoint> Landmarks { get; } = [];

    /// <summary>Gets every point that needs the ground's height (and, for roads, the images' cover along it).</summary>
    public IEnumerable<(List<Vector3> Points, int[]? Tally)> Probes =>
        Roads.Select(r => (r.Points, (int[]?)r.Tally))
            .Concat(Lines.Select(l => (l.Points, (int[]?)null)))
            .Concat(Trees.Concat(Rocks).Concat(Landmarks).Select(p => (p.Point, (int[]?)null)));
}

/// <summary>
/// Reads what the Feature Detector uses of a map (OpenStreetMap) in an area: buildings (outlines and holes, of closed
/// ways and multipolygons), areas by their land use and natural cover, lines (waterways, power lines, aerialways,
/// railways, fences and walls, cliffs), single trees and tree rows, rocks and landmarks; roads through
/// <see cref="MapRoads"/>. Areas and lines are cut at the area's edges; buildings and points belong to it by their center.
/// </summary>
public static class MapFeatures
{
    // Tags of no use to a game.
    private static bool Kept(string key) =>
        !(key.StartsWith("source", StringComparison.Ordinal) || key.StartsWith("note", StringComparison.Ordinal) || key.StartsWith("fixme", StringComparison.OrdinalIgnoreCase)
          || key.StartsWith("check_date", StringComparison.Ordinal) || key.StartsWith("survey", StringComparison.Ordinal) || key.StartsWith("created_by", StringComparison.Ordinal)
          || key.StartsWith("wikidata", StringComparison.Ordinal) || key.StartsWith("ref:", StringComparison.Ordinal) || key.StartsWith("addr:", StringComparison.Ordinal));

    private static Dictionary<string, string> KeptTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Where(t => Kept(t.Key)).ToDictionary(t => t.Key, t => t.Value);

    /// <summary>Gets the kind of area of a closed way's or multipolygon's tags, and its class ("landuse=forest"), or <c>null</c>.</summary>
    public static AreaKind? AreaKindOf(IReadOnlyDictionary<string, string> tags, out string kind)
    {
        foreach (string key in new[] { "natural", "landuse", "waterway", "amenity" })
        {
            if (!tags.TryGetValue(key, out var value)) continue;
            kind = key + "=" + value;
            AreaKind? area = (key, value) switch
            {
                ("natural", "wood") or ("landuse", "forest") => AreaKind.Forest,
                ("natural", "scrub") or ("natural", "heath") => AreaKind.Scrub,
                ("natural", "grassland") or ("landuse", "meadow") or ("landuse", "grass") or ("landuse", "village_green") or ("landuse", "recreation_ground") => AreaKind.Grassland,
                ("landuse", "farmland") or ("landuse", "allotments") or ("landuse", "plant_nursery") => AreaKind.Farmland,
                ("landuse", "orchard") => AreaKind.Orchard,
                ("landuse", "vineyard") => AreaKind.Vineyard,
                ("natural", "wetland") => AreaKind.Wetland,
                ("natural", "bare_rock") => AreaKind.Rock,
                ("natural", "scree") or ("natural", "shingle") => AreaKind.Scree,
                ("natural", "sand") or ("natural", "beach") => AreaKind.Sand,
                ("natural", "glacier") => AreaKind.Snow,
                ("natural", "water") or ("landuse", "reservoir") or ("landuse", "basin") or ("waterway", "riverbank") or ("waterway", "dock") => AreaKind.Water,
                ("landuse", "quarry") => AreaKind.Gravel,
                ("landuse", "residential") or ("landuse", "commercial") or ("landuse", "industrial") or ("landuse", "retail") or ("landuse", "farmyard")
                    or ("landuse", "garages") or ("landuse", "construction") => AreaKind.BuiltUp,
                ("amenity", "parking") => AreaKind.Paved,
                _ => null,
            };
            if (area != null) return area;
        }
        kind = "";
        return null;
    }

    /// <summary>Gets the kind of line of a way's tags, and its class ("waterway=stream"), or <c>null</c>.</summary>
    public static LineKind? LineKindOf(IReadOnlyDictionary<string, string> tags, out string kind)
    {
        foreach (string key in new[] { "waterway", "power", "aerialway", "railway", "barrier", "natural" })
        {
            if (!tags.TryGetValue(key, out var value)) continue;
            kind = key + "=" + value;
            LineKind? line = (key, value) switch
            {
                ("waterway", "river") => LineKind.River,
                ("waterway", "stream") or ("waterway", "brook") => LineKind.Stream,
                ("waterway", "canal") => LineKind.Canal,
                ("waterway", "ditch") or ("waterway", "drain") => LineKind.Ditch,
                ("power", "line") => LineKind.PowerLine,
                ("power", "minor_line") => LineKind.MinorPowerLine,
                ("aerialway", "cable_car" or "gondola" or "mixed_lift" or "chair_lift" or "drag_lift" or "t-bar" or "j-bar" or "platter" or "rope_tow"
                    or "magic_carpet" or "zip_line" or "goods") => LineKind.Aerialway,
                ("railway", "rail" or "narrow_gauge" or "funicular" or "light_rail" or "tram" or "monorail") => LineKind.Railway,
                ("barrier", "fence") => LineKind.Fence,
                ("barrier", "wall" or "city_wall" or "retaining_wall") => LineKind.Wall,
                ("barrier", "hedge") => LineKind.Hedge,
                ("natural", "cliff") => LineKind.Cliff,
                _ => null,
            };
            if (line != null) return line;
        }
        kind = "";
        return null;
    }

    /// <summary>Gets the kind of landmark of a node's tags, and its class ("natural=peak"), or <c>null</c>.</summary>
    public static LandmarkKind? LandmarkKindOf(IReadOnlyDictionary<string, string> tags, out string kind)
    {
        if (tags.GetValueOrDefault("sport") == "free_flying")
        {
            kind = "sport=free_flying";
            return tags.GetValueOrDefault("free_flying:site") switch
            {
                "takeoff" => LandmarkKind.Takeoff,
                "landing" or "toplanding" => LandmarkKind.Landing,
                _ => LandmarkKind.FlyingSite,
            };
        }
        foreach (string key in new[] { "natural", "man_made", "tourism", "amenity", "information", "aerialway" })
        {
            if (!tags.TryGetValue(key, out var value)) continue;
            kind = key + "=" + value;
            LandmarkKind? landmark = (key, value) switch
            {
                ("natural", "peak") => LandmarkKind.Peak,
                ("natural", "saddle") => LandmarkKind.Saddle,
                ("natural", "spring") => LandmarkKind.Spring,
                ("natural", "cave_entrance") => LandmarkKind.Cave,
                ("man_made", "mast") => LandmarkKind.Mast,
                ("man_made", "tower") => LandmarkKind.Tower,
                ("man_made", "cross" or "summit_cross") => LandmarkKind.Cross,
                ("tourism", "alpine_hut" or "wilderness_hut") => LandmarkKind.Hut,
                ("tourism", "viewpoint") => LandmarkKind.Viewpoint,
                ("amenity", "shelter") => LandmarkKind.Shelter,
                ("amenity", "bench") => LandmarkKind.Bench,
                ("information", "guidepost") => LandmarkKind.Guidepost,
                ("aerialway", "station") => LandmarkKind.Station,
                _ => null,
            };
            if (landmark != null) return landmark;
        }
        kind = "";
        return null;
    }

    /// <summary>Gets a number of meters from a tag's value ("12", "12.5 m", "12,5", "40'", "40 ft"), or <c>null</c>.</summary>
    public static float? Meters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().Replace(',', '.');
        float factor = 1;
        if (text.EndsWith("ft", StringComparison.OrdinalIgnoreCase) || text.EndsWith('\''))
        {
            factor = 0.3048f;
            text = text.TrimEnd('\'').Replace("ft", "", StringComparison.OrdinalIgnoreCase);
        }
        text = text.Replace("m", "", StringComparison.OrdinalIgnoreCase).Trim();
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value * factor : null;
    }

    /// <summary>
    /// Gets a building's top and eaves above the ground (m) and roof from the map's tags: <c>height</c>,
    /// <c>roof:height</c>, <c>building:levels</c> (3 m each, and a roof), <c>roof:shape</c>; else its class's usual
    /// height and roof (houses 7 m pitched, garages 3 m flat, industrial 9 m flat …).
    /// </summary>
    public static (float Top, float Eaves, RoofShape Roof) BuildingHeights(IReadOnlyDictionary<string, string> tags)
    {
        string building = tags.GetValueOrDefault("building", "yes");
        var (usualTop, usualRoof) = building switch
        {
            "house" or "detached" or "semidetached_house" or "terrace" or "residential" or "farm" => (7f, RoofShape.Pitched),
            "bungalow" or "cabin" or "hut" or "chalet" => (4.5f, RoofShape.Pitched),
            "garage" or "garages" or "carport" => (3f, RoofShape.Flat),
            "shed" => (3f, RoofShape.Pitched),
            "barn" or "farm_auxiliary" or "stable" or "cowshed" or "sty" => (8f, RoofShape.Pitched),
            "apartments" or "dormitory" => (13f, RoofShape.Pitched),
            "church" or "cathedral" => (14f, RoofShape.Pitched),
            "chapel" or "shrine" => (6f, RoofShape.Pitched),
            "industrial" or "warehouse" or "hangar" or "manufacture" => (9f, RoofShape.Flat),
            "commercial" or "retail" or "office" or "supermarket" or "school" or "hospital" or "public" or "civic" or "hotel" or "university" or "college"
                or "train_station" or "transportation" or "sports_hall" => (10f, RoofShape.Flat),
            "roof" => (4f, RoofShape.Flat),
            "service" or "transformer_tower" or "kiosk" or "toilets" => (3f, RoofShape.Flat),
            _ => (6f, RoofShape.Pitched),
        };
        var roof = tags.GetValueOrDefault("roof:shape") switch
        {
            null => usualRoof,
            "flat" => RoofShape.Flat,
            _ => RoofShape.Pitched,
        };
        float? roofHeight = Meters(tags.GetValueOrDefault("roof:height")) is { } r and > 0 and < 100 ? r : null;
        if (Meters(tags.GetValueOrDefault("height")) is not { } height || height is <= 0 or > 1000)
        {
            // Floors of 3 m, the roof on top of them.
            if (float.TryParse(tags.GetValueOrDefault("building:levels"), NumberStyles.Float, CultureInfo.InvariantCulture, out float levels) && levels is > 0 and < 200)
            {
                float walls = levels * 3;
                return (walls + (roof == RoofShape.Pitched ? roofHeight ?? 3 : 0), walls, roof);
            }
            height = usualTop;
        }
        float top = Math.Max(2, height);
        float eaves = roof == RoofShape.Flat ? top : top - Math.Clamp(roofHeight ?? top * 0.35f, 0.5f, top - 1);
        return (top, eaves, roof);
    }

    /// <summary>Gets a line's width and height (m) from its tags (<c>width</c>, <c>height</c>, a power line's voltage), else its kind's usual ones.</summary>
    public static (float Width, float Height) LineSize(LineKind kind, IReadOnlyDictionary<string, string> tags)
    {
        float usualWidth = kind switch
        {
            LineKind.River => 15,
            LineKind.Stream => 2,
            LineKind.Canal => 8,
            LineKind.Ditch => 1,
            LineKind.Railway => 3,
            LineKind.Fence => 0.1f,
            LineKind.Wall => 0.5f,
            LineKind.Hedge => 1.2f,
            _ => 0,
        };
        float voltage = float.TryParse(tags.GetValueOrDefault("voltage")?.Split(';')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;
        float usualHeight = kind switch
        {
            LineKind.PowerLine => voltage >= 220_000 ? 50 : voltage >= 110_000 ? 35 : 25,
            LineKind.MinorPowerLine => 10,
            LineKind.Aerialway => tags.GetValueOrDefault("aerialway") switch
            {
                "cable_car" => 25,
                "gondola" or "goods" => 15,
                "mixed_lift" => 12,
                "chair_lift" or "zip_line" => 10,
                "drag_lift" or "t-bar" or "j-bar" or "platter" => 5,
                _ => 1,
            },
            LineKind.Fence => 1.2f,
            LineKind.Wall => 1.5f,
            LineKind.Hedge => 1.8f,
            _ => 0,
        };
        return (Meters(tags.GetValueOrDefault("width")) is { } width and > 0 and < 500 ? width : usualWidth,
            Meters(tags.GetValueOrDefault("height")) is { } height and > 0 and < 500 ? height : usualHeight);
    }

    private static readonly HashSet<string> s_conifers =
    [
        "abies", "picea", "pinus", "larix", "pseudotsuga", "cedrus", "taxus", "thuja", "juniperus", "sequoia", "sequoiadendron", "tsuga", "cupressus",
        "chamaecyparis", "metasequoia", "taxodium", "araucaria", "calocedrus", "cryptomeria", "ginkgo",
    ];

    /// <summary>
    /// Gets a tree's height, crown radius and kind from its tags (<c>height</c>, <c>diameter_crown</c>, <c>leaf_type</c>,
    /// else the <c>genus</c> or <c>species</c>: firs, spruces, pines, larches, cedars, yews, cypresses … are conifers, other
    /// genera broadleaf); the height <c>null</c> when the map gives none.
    /// </summary>
    public static (float? Height, float CrownRadius, TreeKind Kind) TreeOf(IReadOnlyDictionary<string, string> tags)
    {
        float? height = Meters(tags.GetValueOrDefault("height")) is { } h and > 0.5f and < 120 ? h : null;
        float crown = Meters(tags.GetValueOrDefault("diameter_crown")) is { } d and > 0.5f and < 60 ? d / 2 : 3;
        var kind = KindOfLeaves(tags.GetValueOrDefault("leaf_type"));
        string? genus = tags.GetValueOrDefault("genus") ?? tags.GetValueOrDefault("species")?.Split(' ')[0];
        if (kind == TreeKind.Unknown && !string.IsNullOrWhiteSpace(genus))
        {
            kind = s_conifers.Contains(genus.Trim().ToLowerInvariant()) ? TreeKind.Conifer : TreeKind.Broadleaf;
        }
        return (height, crown, kind);
    }

    /// <summary>Gets the tree kind of an OpenStreetMap <c>leaf_type</c>: needleleaved conifers, broadleaved broadleaf, else unknown.</summary>
    public static TreeKind KindOfLeaves(string? leafType) => leafType switch
    {
        "needleleaved" => TreeKind.Conifer,
        "broadleaved" => TreeKind.Broadleaf,
        _ => TreeKind.Unknown,
    };

    /// <summary>Reads the map's features in <paramref name="area"/> of <paramref name="frame"/>.</summary>
    internal static MapContent Build(MapData data, LocalFrame frame, FeatureArea area, bool roads)
    {
        var content = new MapContent();
        if (roads) content.Roads.AddRange(MapRoads.Build(data.Ways, frame, area));
        float x0 = (float)area.X0, z0 = (float)area.Z0, x1 = (float)(area.X0 + area.Width), z1 = (float)(area.Z0 + area.Depth);
        bool Inside(Vector2 p) => p.X >= x0 && p.X < x1 && p.Y >= z0 && p.Y < z1;
        Vector2 Local(GeoPoint point)
        {
            var (x, z) = frame.FromGeo(point);
            return new Vector2((float)x, (float)z);
        }
        // A closed way's ring without its repeated last point.
        List<Vector2> Ring(IReadOnlyList<GeoPoint> points)
        {
            var ring = points.Select(Local).ToList();
            if (ring.Count > 1 && ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);
            return ring;
        }

        // Polygons: closed ways, and the multipolygons' rings with their holes.
        var polygons = new List<(List<Vector2> Outline, List<List<Vector2>> Holes, IReadOnlyDictionary<string, string> Tags)>();
        foreach (var way in data.Ways)
        {
            if (!way.IsClosed || way.Tags.ContainsKey("highway") && !way.Tags.ContainsKey("building")) continue;
            polygons.Add((Ring(way.Points), [], way.Tags));
        }
        foreach (var relation in data.Relations)
        {
            var outers = Assemble(relation.Members.Where(m => m.Role != "inner").Select(m => m.Points.Select(Local).ToList()));
            var inners = Assemble(relation.Members.Where(m => m.Role == "inner").Select(m => m.Points.Select(Local).ToList()));
            foreach (var outer in outers)
            {
                var holes = inners.Where(h => h.Count > 0 && Polygons.Contains(outer, Polygons.Centroid(h))).ToList();
                polygons.Add((outer, holes, relation.Tags));
            }
        }

        foreach (var (outline, holes, tags) in polygons)
        {
            if (outline.Count < 3) continue;
            if (tags.TryGetValue("building", out var building) && building != "no")
            {
                var footprint = Oriented(outline, outer: true);
                if (!Inside(Polygons.Centroid(footprint))) continue;
                content.Buildings.Add(new MapBuilding
                {
                    Footprint = footprint,
                    Holes = holes.Select(h => Oriented(h, outer: false)).ToList(),
                    Class = building,
                    Tags = KeptTags(tags),
                });
                continue;
            }
            if (AreaKindOf(tags, out string areaClass) is { } kind)
            {
                var clipped = Polygons.ClipRing(outline, x0, z0, x1, z1);
                if (clipped.Count < 3) continue;
                var clippedHoles = holes.Select(h => Polygons.ClipRing(h, x0, z0, x1, z1)).Where(h => h.Count >= 3).Select(h => Oriented(h, outer: false)).ToList();
                content.Areas.Add(new AreaFeature(kind, Oriented(clipped, outer: true), clippedHoles)
                {
                    Origin = FeatureOrigin.Map,
                    Class = areaClass,
                    Tags = KeptTags(tags),
                });
            }
        }

        // Lines: waterways, power lines, aerialways, railways, barriers, cliffs; tree rows as trees every 8 m.
        foreach (var way in data.Ways)
        {
            if (way.Tags.ContainsKey("building")) continue;
            if (way.Tags.GetValueOrDefault("natural") == "tree_row")
            {
                var (height, crown, treeKind) = TreeOf(way.Tags);
                foreach (var part in Clip(way.Points.Select(Local).ToList(), x0, z0, x1, z1))
                {
                    foreach (var p in Densify(part, 8)) AddPoint(content.Trees, p, way.Tags, "natural=tree_row");
                }
                continue;
            }
            if (LineKindOf(way.Tags, out string lineClass) is not { } lineKind) continue;
            var (width, lineHeight) = LineSize(lineKind, way.Tags);
            // Wires hang from their towers and pylons: those stay the points; what lies on the ground follows it.
            bool wires = lineKind is LineKind.PowerLine or LineKind.MinorPowerLine or LineKind.Aerialway;
            foreach (var part in Clip(way.Points.Select(Local).ToList(), x0, z0, x1, z1))
            {
                var points = wires ? part : Densify(part, 5);
                content.Lines.Add(new MapLine
                {
                    Points = points.Select(p => new Vector3(p.X, float.NaN, p.Y)).ToList(),
                    Kind = lineKind,
                    Width = width,
                    Height = lineHeight,
                    Structure = MapRoads.StructureOf(way.Tags),
                    Class = lineClass,
                    Tags = KeptTags(way.Tags),
                });
            }
        }

        // Points: trees, rocks and landmarks (flying sites mapped as areas at their center).
        foreach (var node in data.Nodes)
        {
            var p = Local(node.Point);
            if (!Inside(p)) continue;
            string natural = node.Tags.GetValueOrDefault("natural", "");
            if (natural == "tree") AddPoint(content.Trees, p, node.Tags, "natural=tree");
            else if (natural is "stone" or "rock") AddPoint(content.Rocks, p, node.Tags, "natural=" + natural);
            else if (LandmarkKindOf(node.Tags, out string landmarkClass) is not null) AddPoint(content.Landmarks, p, node.Tags, landmarkClass);
        }
        foreach (var way in data.Ways)
        {
            if (way.Tags.GetValueOrDefault("sport") != "free_flying") continue;
            var center = Polygons.Centroid(Ring(way.Points));
            if (Inside(center) && LandmarkKindOf(way.Tags, out string siteClass) is not null) AddPoint(content.Landmarks, center, way.Tags, siteClass);
        }
        return content;
    }

    private static void AddPoint(List<MapPoint> points, Vector2 p, IReadOnlyDictionary<string, string> tags, string kind) =>
        points.Add(new MapPoint { Point = [new Vector3(p.X, float.NaN, p.Y)], Tags = KeptTags(tags), Class = kind });

    // Outlines counterclockwise seen from above (negative shoelace in x, z), holes clockwise.
    private static List<Vector2> Oriented(List<Vector2> ring, bool outer)
    {
        if (Polygons.SignedArea(ring) < 0 != outer) ring.Reverse();
        return ring;
    }

    // Joins the ways of a multipolygon into closed rings where their ends meet; rings that don't close are dropped.
    private static List<List<Vector2>> Assemble(IEnumerable<List<Vector2>> ways)
    {
        var open = ways.Where(w => w.Count >= 2).ToList();
        var rings = new List<List<Vector2>>();
        static bool Same(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) < 1e-4f;
        while (open.Count > 0)
        {
            var ring = new List<Vector2>(open[^1]);
            open.RemoveAt(open.Count - 1);
            while (!Same(ring[0], ring[^1]))
            {
                int next = open.FindIndex(w => Same(w[0], ring[^1]) || Same(w[^1], ring[^1]));
                if (next < 0) break;
                var way = open[next];
                open.RemoveAt(next);
                if (!Same(way[0], ring[^1])) way.Reverse();
                ring.AddRange(way.Skip(1));
            }
            if (ring.Count >= 4 && Same(ring[0], ring[^1]))
            {
                ring.RemoveAt(ring.Count - 1);
                rings.Add(ring);
            }
        }
        return rings;
    }

    // The parts of a polyline inside the rectangle.
    private static List<List<Vector2>> Clip(List<Vector2> points, float x0, float z0, float x1, float z1)
    {
        var parts = new List<List<Vector2>>();
        List<Vector2>? part = null;
        for (int k = 0; k + 1 < points.Count; k++)
        {
            var a = points[k];
            var b = points[k + 1];
            double t0 = 0, t1 = 1, dx = b.X - a.X, dz = b.Y - a.Y;
            Span<double> p = [-dx, dx, -dz, dz];
            Span<double> q = [a.X - x0, x1 - a.X, a.Y - z0, z1 - a.Y];
            bool outside = false;
            for (int i = 0; i < 4 && !outside; i++)
            {
                if (p[i] == 0)
                {
                    outside = q[i] < 0;
                    continue;
                }
                double r = q[i] / p[i];
                if (p[i] < 0) t0 = Math.Max(t0, r);
                else t1 = Math.Min(t1, r);
            }
            if (outside || t0 >= t1)
            {
                if (part is { Count: >= 2 }) parts.Add(part);
                part = null;
                continue;
            }
            part ??= [Vector2.Lerp(a, b, (float)t0)];
            part.Add(Vector2.Lerp(a, b, (float)t1));
            if (t1 < 1)
            {
                if (part.Count >= 2) parts.Add(part);
                part = null;
            }
        }
        if (part is { Count: >= 2 }) parts.Add(part);
        return parts;
    }

    // Points at most `step` apart along a polyline (its own points kept).
    private static List<Vector2> Densify(List<Vector2> points, float step)
    {
        var result = new List<Vector2> { points[0] };
        for (int i = 1; i < points.Count; i++)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(points[i - 1], points[i]) / step));
            for (int s = 1; s <= steps; s++) result.Add(Vector2.Lerp(points[i - 1], points[i], (float)s / steps));
        }
        return result;
    }
}
