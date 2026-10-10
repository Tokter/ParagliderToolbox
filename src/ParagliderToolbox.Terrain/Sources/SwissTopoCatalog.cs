using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>A 1 km tile of a swisstopo dataset: its south-west corner in LV95 kilometers, the date of its data and its files.</summary>
/// <param name="East">The easting of the west edge (km).</param>
/// <param name="North">The northing of the south edge (km).</param>
/// <param name="Date">The date of the data (the newest version is kept).</param>
/// <param name="Assets">The files: one per resolution.</param>
public sealed record SwissTile(int East, int North, DateTime Date, IReadOnlyList<SwissAsset> Assets);

/// <summary>A file of a <see cref="SwissTile"/>.</summary>
/// <param name="Href">The file's URL.</param>
/// <param name="Gsd">The ground sample distance (m).</param>
/// <param name="Updated">When the file was last changed (its version in the cache).</param>
public sealed record SwissAsset(string Href, double Gsd, string? Updated);

/// <summary>
/// Finds the files of swisstopo's datasets (swissALTI3D, SWISSIMAGE) through their STAC catalog
/// (data.geo.admin.ch): the 1 km tiles covering an area, the newest version of each.
/// </summary>
/// <remarks>
/// The catalog is queried in 8 × 8 km cells of the Swiss grid, so the same queries recur and are answered from the
/// cache (for 30 days, and when offline).
/// </remarks>
public sealed class SwissTopoCatalog(DataCache cache)
{
    /// <summary>The STAC API.</summary>
    public const string Api = "https://data.geo.admin.ch/api/stac/v1";

    private const int CellKilometers = 8;
    private static readonly TimeSpan s_maxAge = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<(string, int, int), Lazy<Task<Dictionary<(int, int), SwissTile>>>> _cells = new();

    /// <summary>Gets the tiles of <paramref name="collection"/> that overlap an LV95 rectangle (m).</summary>
    public async Task<List<SwissTile>> FindAsync(string collection, double minE, double minN, double maxE, double maxN, CancellationToken cancellationToken)
    {
        int e0 = (int)Math.Floor(minE / 1000), e1 = (int)Math.Floor(maxE / 1000);
        int n0 = (int)Math.Floor(minN / 1000), n1 = (int)Math.Floor(maxN / 1000);
        var cells = new List<Task<Dictionary<(int, int), SwissTile>>>();
        for (int ce = FloorDiv(e0, CellKilometers); ce <= FloorDiv(e1, CellKilometers); ce++)
        {
            for (int cn = FloorDiv(n0, CellKilometers); cn <= FloorDiv(n1, CellKilometers); cn++)
            {
                cells.Add(CellAsync(collection, ce, cn, cancellationToken));
            }
        }
        var tiles = new List<SwissTile>();
        foreach (var cell in await Task.WhenAll(cells))
        {
            foreach (var tile in cell.Values)
            {
                if (tile.East >= e0 && tile.East <= e1 && tile.North >= n0 && tile.North <= n1) tiles.Add(tile);
            }
        }
        return tiles;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    private async Task<Dictionary<(int, int), SwissTile>> CellAsync(string collection, int cellE, int cellN, CancellationToken cancellationToken)
    {
        var key = (collection, cellE, cellN);
        var lazy = _cells.GetOrAdd(key, _ => new Lazy<Task<Dictionary<(int, int), SwissTile>>>(() => Task.Run(() => QueryCellAsync(collection, cellE, cellN))));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _cells.TryRemove(new KeyValuePair<(string, int, int), Lazy<Task<Dictionary<(int, int), SwissTile>>>>(key, lazy));
            throw;
        }
    }

    private async Task<Dictionary<(int, int), SwissTile>> QueryCellAsync(string collection, int cellE, int cellN)
    {
        // The cell's box in WGS84, a little inside it (the neighbors' tiles would come along otherwise).
        double e0 = cellE * CellKilometers * 1000.0 + 20, e1 = (cellE + 1) * CellKilometers * 1000.0 - 20;
        double n0 = cellN * CellKilometers * 1000.0 + 20, n1 = (cellN + 1) * CellKilometers * 1000.0 - 20;
        var box = GeoBounds.Around([SwissGrid.ToWgs84(e0, n0), SwissGrid.ToWgs84(e1, n0), SwissGrid.ToWgs84(e1, n1), SwissGrid.ToWgs84(e0, n1)]);
        string? url = string.Create(CultureInfo.InvariantCulture,
            $"{Api}/collections/{collection}/items?bbox={box.West:0.######},{box.South:0.######},{box.East:0.######},{box.North:0.######}&limit=100");

        var tiles = new Dictionary<(int, int), SwissTile>();
        for (int page = 0; url != null && page < 50; page++)
        {
            var json = await cache.GetAsync(url, s_maxAge) ?? throw new HttpRequestException($"The swisstopo catalog has no {collection}.");
            url = null;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (ParseTile(feature) is not { } tile) continue;
                    if (FloorDiv(tile.East, CellKilometers) != cellE || FloorDiv(tile.North, CellKilometers) != cellN) continue;
                    if (!tiles.TryGetValue((tile.East, tile.North), out var existing) || tile.Date > existing.Date) tiles[(tile.East, tile.North)] = tile;
                }
            }
            if (root.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var rel) && rel.GetString() == "next" && link.TryGetProperty("href", out var href)) url = href.GetString();
                }
            }
        }
        return tiles;
    }

    // "swissalti3d_2019_2632-1169": the tile's south-west corner is 2632 / 1169 km.
    private static SwissTile? ParseTile(JsonElement feature)
    {
        if (!feature.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id) return null;
        int dash = id.LastIndexOf('-'), underscore = id.LastIndexOf('_');
        if (dash < 0 || underscore < 0 || underscore > dash) return null;
        if (!int.TryParse(id.AsSpan(underscore + 1, dash - underscore - 1), CultureInfo.InvariantCulture, out int east) ||
            !int.TryParse(id.AsSpan(dash + 1), CultureInfo.InvariantCulture, out int north))
        {
            return null;
        }
        var date = DateTime.MinValue;
        if (feature.TryGetProperty("properties", out var properties) && properties.TryGetProperty("datetime", out var datetime) &&
            datetime.ValueKind == JsonValueKind.String)
        {
            DateTime.TryParse(datetime.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);
        }
        var assets = ParseAssets(feature);
        return assets.Count == 0 ? null : new SwissTile(east, north, date, assets);
    }

    // The item's GeoTIFF files.
    private static List<SwissAsset> ParseAssets(JsonElement feature)
    {
        var assets = new List<SwissAsset>();
        if (!feature.TryGetProperty("assets", out var assetsElement)) return assets;
        foreach (var asset in assetsElement.EnumerateObject())
        {
            var value = asset.Value;
            if (!value.TryGetProperty("href", out var href) || href.GetString() is not { } link) continue;
            if (!link.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) continue;
            double gsd = value.TryGetProperty("gsd", out var g) && g.TryGetDouble(out double v) ? v
                : value.TryGetProperty("eo:gsd", out var eo) && eo.TryGetDouble(out double w) ? w : 0;
            string? updated = value.TryGetProperty("updated", out var u) ? u.GetString() : null;
            assets.Add(new SwissAsset(link, gsd, updated));
        }
        return assets;
    }

    /// <summary>Gets the GeoTIFF files of one item of <paramref name="collection"/> (such as a national mosaic); none when there is no such item.</summary>
    public async Task<IReadOnlyList<SwissAsset>> FindItemAsync(string collection, string item, CancellationToken cancellationToken)
    {
        var json = await cache.GetAsync($"{Api}/collections/{collection}/items/{item}", s_maxAge, cancellationToken);
        if (json is null) return [];
        using var document = JsonDocument.Parse(json);
        return ParseAssets(document.RootElement);
    }

    /// <summary>Gets the file of <paramref name="tile"/> best for sampling at <paramref name="spacing"/>: the coarsest at most that fine, else the finest.</summary>
    public static SwissAsset? ChooseAsset(SwissTile tile, double spacing)
    {
        var bySize = tile.Assets.Where(a => a.Gsd > 0).OrderBy(a => a.Gsd).ToList();
        if (bySize.Count == 0) return tile.Assets.FirstOrDefault();
        return bySize.LastOrDefault(a => a.Gsd <= spacing * 1.01) ?? bySize[0];
    }
}
