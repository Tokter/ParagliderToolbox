using System.Globalization;
using System.Text.Json;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>A node of a map with tags of its own: a tree, a peak, a signpost ….</summary>
/// <param name="Id">The node's id.</param>
/// <param name="Point">Its position.</param>
/// <param name="Tags">Its tags ("natural" = "peak" …).</param>
public sealed record MapNode(long Id, GeoPoint Point, IReadOnlyDictionary<string, string> Tags);

/// <summary>A way of a map: its nodes (ids; a node shared by two ways joins them) and their positions, and its tags.</summary>
/// <param name="Id">The way's id.</param>
/// <param name="Nodes">The ids of its nodes, in order.</param>
/// <param name="Points">The positions of its nodes.</param>
/// <param name="Tags">Its tags ("highway" = "track" …).</param>
public sealed record MapWay(long Id, IReadOnlyList<long> Nodes, IReadOnlyList<GeoPoint> Points, IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>Gets whether the way closes on itself (an area's outline, unless its tags make it a line).</summary>
    public bool IsClosed => Nodes.Count >= 4 && Nodes[0] == Nodes[^1];
}

/// <summary>A way of a relation: its role ("outer", "inner") and its positions.</summary>
/// <param name="Role">Its role in the relation.</param>
/// <param name="Points">Its positions.</param>
public sealed record MapMember(string Role, IReadOnlyList<GeoPoint> Points);

/// <summary>A relation of a map (a multipolygon: an area of several outlines with holes), its ways and tags.</summary>
/// <param name="Id">The relation's id.</param>
/// <param name="Members">Its ways.</param>
/// <param name="Tags">Its tags ("landuse" = "forest" …).</param>
public sealed record MapRelation(long Id, IReadOnlyList<MapMember> Members, IReadOnlyDictionary<string, string> Tags);

/// <summary>What a map has in an area: nodes with tags, ways and multipolygon relations.</summary>
/// <param name="Nodes">The tagged nodes.</param>
/// <param name="Ways">The ways.</param>
/// <param name="Relations">The multipolygons.</param>
public sealed record MapData(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapWay> Ways, IReadOnlyList<MapRelation> Relations)
{
    /// <summary>Gets a map without anything.</summary>
    public static MapData Empty { get; } = new([], [], []);
}

/// <summary>A source of map data (OpenStreetMap) for the Feature Detector.</summary>
public interface IMapSource : ITerrainSource
{
    /// <summary>
    /// Gets what the Feature Detector reads of the map in <paramref name="bounds"/>: roads and paths, buildings, land
    /// use and natural areas, waterways, power lines, aerialways, railways, fences and walls, cliffs, and the nodes of
    /// trees, rocks, peaks, paragliding sites, masts, huts, signposts …; each once and whole.
    /// </summary>
    /// <exception cref="HttpRequestException">The data couldn't be downloaded (and isn't cached).</exception>
    Task<MapData> DataAsync(GeoBounds bounds, CancellationToken cancellationToken);
}

/// <summary>
/// OpenStreetMap through the Overpass API: what the Feature Detector reads (<see cref="Query"/>) in an area, its bounds
/// rounded out to <see cref="GridDegrees"/> (so the same area asks the same query, and the answer comes from the cache
/// for <see cref="MaxAge"/>), in one query up to <see cref="MaxQueryDegrees"/> on a side, else in several.
/// </summary>
/// <remarks>
/// Measured: overpass-api.de answers a query in about 30 s whatever its size (a few hundred meters or 5 km of roads),
/// so an area is asked in as few queries as it can; the public mirrors tried timed out (504 after minutes) or refused.
/// The queries go one after another (Overpass allows a couple at once per user). An answer whose remark reports an
/// error (a query that timed out) is incomplete: it is forgotten and the next server asked.
/// </remarks>
public sealed class OpenStreetMapSource(DataCache cache) : IMapSource
{
    /// <summary>The Overpass servers asked, in order.</summary>
    public static readonly IReadOnlyList<string> Endpoints = ["https://overpass-api.de/api/interpreter"];

    /// <summary>The Overpass statements of what is read, <c>{0}</c> standing for the bounds.</summary>
    public static readonly IReadOnlyList<string> Query =
    [
        "way[\"highway\"]({0})",
        "way[\"building\"]({0})", "relation[\"building\"][\"type\"=\"multipolygon\"]({0})",
        "way[\"landuse\"]({0})", "relation[\"landuse\"][\"type\"=\"multipolygon\"]({0})",
        "way[\"natural\"]({0})", "relation[\"natural\"][\"type\"=\"multipolygon\"]({0})",
        "way[\"waterway\"]({0})", "way[\"amenity\"=\"parking\"]({0})",
        "way[\"power\"~\"^(line|minor_line)$\"]({0})", "way[\"aerialway\"]({0})",
        "way[\"railway\"~\"^(rail|narrow_gauge|funicular|light_rail|tram|monorail)$\"]({0})",
        "way[\"barrier\"~\"^(fence|wall|city_wall|retaining_wall|hedge)$\"]({0})",
        "node[\"natural\"~\"^(tree|peak|saddle|stone|rock|spring|cave_entrance)$\"]({0})",
        "node[\"man_made\"~\"^(mast|tower|cross|summit_cross)$\"]({0})",
        "node[\"tourism\"~\"^(alpine_hut|wilderness_hut|viewpoint)$\"]({0})",
        "node[\"amenity\"~\"^(shelter|bench)$\"]({0})", "node[\"information\"=\"guidepost\"]({0})",
        "node[\"aerialway\"=\"station\"]({0})", "nw[\"sport\"=\"free_flying\"]({0})",
    ];

    /// <summary>Gets or sets the grid the bounds are rounded out to (degrees of latitude and longitude).</summary>
    public double GridDegrees { get; init; } = 0.01;

    /// <summary>Gets or sets the largest side of one query (degrees).</summary>
    public double MaxQueryDegrees { get; init; } = 0.1;

    /// <summary>Gets or sets how long an answer is used before it is asked for again.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <inheritdoc/>
    public string Id => "openstreetmap";

    /// <inheritdoc/>
    public string Name => "OpenStreetMap";

    /// <inheritdoc/>
    public string Description =>
        "The map made by its contributors, worldwide: roads and paths, buildings, land use, waterways, power lines, lifts, trees, peaks and paragliding sites, through the Overpass API.";

    /// <inheritdoc/>
    public string Attribution => "© OpenStreetMap contributors";

    /// <inheritdoc/>
    public string License =>
        "Open Database License (ODbL) 1.0: free, including commercial use; credit \"© OpenStreetMap contributors\"; data derived from it stays under the ODbL when shared.";

    /// <inheritdoc/>
    public double Resolution => 1;

    /// <inheritdoc/>
    public SourceScope Scope => SourceScope.Global;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => true;

    /// <inheritdoc/>
    public async Task<MapData> DataAsync(GeoBounds bounds, CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<long, MapNode>();
        var ways = new Dictionary<long, MapWay>();
        var relations = new Dictionary<long, MapRelation>();
        int south = (int)Math.Floor(bounds.South / GridDegrees), north = (int)Math.Ceiling(bounds.North / GridDegrees);
        int west = (int)Math.Floor(bounds.West / GridDegrees), east = (int)Math.Ceiling(bounds.East / GridDegrees);
        int step = Math.Max(1, (int)Math.Round(MaxQueryDegrees / GridDegrees));
        for (int y = south; y < north; y += step)
        {
            for (int x = west; x < east; x += step)
            {
                var part = new GeoBounds(y * GridDegrees, x * GridDegrees, Math.Min(y + step, north) * GridDegrees, Math.Min(x + step, east) * GridDegrees);
                var data = await QueryAsync(part, cancellationToken);
                foreach (var node in data.Nodes) nodes.TryAdd(node.Id, node);
                foreach (var way in data.Ways) ways.TryAdd(way.Id, way);
                foreach (var relation in data.Relations) relations.TryAdd(relation.Id, relation);
            }
        }
        return new MapData([.. nodes.Values], [.. ways.Values], [.. relations.Values]);
    }

    // One query, from the first server that answers completely (the server gives up before the client's 90 s).
    private async Task<MapData> QueryAsync(GeoBounds part, CancellationToken cancellationToken)
    {
        string box = string.Create(CultureInfo.InvariantCulture, $"{part.South:0.0####},{part.West:0.0####},{part.North:0.0####},{part.East:0.0####}");
        string query = "[out:json][timeout:80];(" + string.Concat(Query.Select(q => string.Format(CultureInfo.InvariantCulture, q, box) + ";")) + ");out geom;";
        HttpRequestException? failure = null;
        foreach (string endpoint in Endpoints)
        {
            string url = endpoint + "?data=" + Uri.EscapeDataString(query);
            try
            {
                byte[] bytes = await cache.GetAsync(url, MaxAge, cancellationToken) ?? [];
                return Parse(bytes);
            }
            catch (HttpRequestException e)
            {
                failure = e;
            }
            catch (InvalidDataException e)
            {
                cache.Remove(url);
                failure = new HttpRequestException($"OpenStreetMap's answer was incomplete: {e.Message}", e);
            }
        }
        throw failure ?? new HttpRequestException("No OpenStreetMap server answered.");
    }

    /// <summary>Reads an Overpass answer (JSON with <c>out geom</c>): its tagged nodes, its ways and its multipolygons.</summary>
    /// <exception cref="InvalidDataException">The answer reports an error or isn't JSON.</exception>
    public static MapData Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("remark", out var remark) && remark.GetString() is { } text && text.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(text);
            }
            var nodes = new List<MapNode>();
            var ways = new List<MapWay>();
            var relations = new List<MapRelation>();
            foreach (var element in root.GetProperty("elements").EnumerateArray())
            {
                var tags = Tags(element);
                switch (element.GetProperty("type").GetString())
                {
                    case "node" when tags.Count > 0:
                        nodes.Add(new MapNode(element.GetProperty("id").GetInt64(),
                            new GeoPoint(element.GetProperty("lat").GetDouble(), element.GetProperty("lon").GetDouble()), tags));
                        break;
                    case "way" when element.TryGetProperty("geometry", out var geometry):
                        var ids = element.GetProperty("nodes").EnumerateArray().Select(n => n.GetInt64()).ToList();
                        var points = Points(geometry);
                        if (points.Count == ids.Count && points.Count >= 2) ways.Add(new MapWay(element.GetProperty("id").GetInt64(), ids, points, tags));
                        break;
                    case "relation" when element.TryGetProperty("members", out var members):
                        var ringWays = new List<MapMember>();
                        foreach (var member in members.EnumerateArray())
                        {
                            if (member.GetProperty("type").GetString() != "way" || !member.TryGetProperty("geometry", out var memberGeometry)) continue;
                            var memberPoints = Points(memberGeometry);
                            if (memberPoints.Count >= 2) ringWays.Add(new MapMember(member.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "", memberPoints));
                        }
                        if (ringWays.Count > 0) relations.Add(new MapRelation(element.GetProperty("id").GetInt64(), ringWays, tags));
                        break;
                }
            }
            return new MapData(nodes, ways, relations);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException($"Not an Overpass answer: {e.Message}", e);
        }

        static Dictionary<string, string> Tags(JsonElement element)
        {
            var tags = new Dictionary<string, string>();
            if (!element.TryGetProperty("tags", out var tagElement)) return tags;
            foreach (var tag in tagElement.EnumerateObject()) tags[tag.Name] = tag.Value.GetString() ?? "";
            return tags;
        }

        // Nodes the server didn't have are null: the positions stop there.
        static List<GeoPoint> Points(JsonElement geometry)
        {
            var points = new List<GeoPoint>();
            foreach (var point in geometry.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Object) break;
                points.Add(new GeoPoint(point.GetProperty("lat").GetDouble(), point.GetProperty("lon").GetDouble()));
            }
            return points;
        }
    }
}
