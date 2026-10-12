using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sampling;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>The areas USGS and USDA data cover: the conterminous United States, Alaska, Hawaii and Puerto Rico.</summary>
public static class UnitedStates
{
    /// <summary>The boxes around the areas (with a margin).</summary>
    public static readonly GeoBounds[] Areas =
    [
        new(24.0, -125.5, 49.6, -66.5), // conterminous
        new(51.0, -180.0, 71.6, -129.5), // Alaska
        new(18.5, -160.6, 22.4, -154.6), // Hawaii
        new(17.8, -67.4, 18.6, -65.1), // Puerto Rico
    ];

    /// <summary>Gets whether <paramref name="bounds"/> touches one of the areas.</summary>
    public static bool Covers(GeoBounds bounds) => Areas.Any(bounds.Intersects);
}

/// <summary>A 10 km tile of the USGS 3DEP one-meter DEM: its file, the lidar project it is of, when published, its box.</summary>
/// <param name="Url">The Cloud Optimized GeoTIFF's URL.</param>
/// <param name="Project">The lidar project (tiles of one project are seamless; projects overlap).</param>
/// <param name="Published">When it was published (the newest project wins where they overlap).</param>
/// <param name="Updated">When it was last changed (its version in the cache).</param>
/// <param name="Bounds">Its box (WGS84).</param>
public sealed record UsgsTile(string Url, string Project, DateTime Published, string? Updated, GeoBounds Bounds);

/// <summary>
/// Finds the USGS 3DEP one-meter DEM's tiles through The National Map's products API (tnmaccess.nationalmap.gov): the
/// 10 km tiles of every lidar project covering an area.
/// </summary>
/// <remarks>The API is asked in cells of 0.1°, so the same queries recur and are answered from the cache (for 30 days, and when offline).</remarks>
public sealed class NationalMapCatalog(DataCache cache)
{
    /// <summary>The products API.</summary>
    public const string Api = "https://tnmaccess.nationalmap.gov/api/v1/products";

    /// <summary>The dataset of the one-meter DEM.</summary>
    public const string OneMeter = "Digital Elevation Model (DEM) 1 meter";

    private const double Cell = 0.1;
    private const int PageSize = 200;
    private static readonly TimeSpan s_maxAge = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<(int, int), Lazy<Task<List<UsgsTile>>>> _cells = new();

    /// <summary>Gets the tiles that overlap <paramref name="bounds"/>, the newest first.</summary>
    public async Task<List<UsgsTile>> FindAsync(GeoBounds bounds, CancellationToken cancellationToken)
    {
        var cells = new List<Task<List<UsgsTile>>>();
        for (int y = (int)Math.Floor(bounds.South / Cell); y <= (int)Math.Floor(bounds.North / Cell); y++)
        {
            for (int x = (int)Math.Floor(bounds.West / Cell); x <= (int)Math.Floor(bounds.East / Cell); x++) cells.Add(CellAsync(x, y, cancellationToken));
        }
        var tiles = new Dictionary<string, UsgsTile>();
        foreach (var cell in await Task.WhenAll(cells))
        {
            foreach (var tile in cell)
            {
                if (tile.Bounds.Intersects(bounds)) tiles.TryAdd(tile.Url, tile);
            }
        }
        return tiles.Values.OrderByDescending(t => t.Published).ThenBy(t => t.Project, StringComparer.Ordinal).ToList();
    }

    private async Task<List<UsgsTile>> CellAsync(int x, int y, CancellationToken cancellationToken)
    {
        var lazy = _cells.GetOrAdd((x, y), _ => new Lazy<Task<List<UsgsTile>>>(() => Task.Run(() => QueryCellAsync(x, y))));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _cells.TryRemove(new KeyValuePair<(int, int), Lazy<Task<List<UsgsTile>>>>((x, y), lazy));
            throw;
        }
    }

    private async Task<List<UsgsTile>> QueryCellAsync(int x, int y)
    {
        var tiles = new List<UsgsTile>();
        for (int offset = 0, total = int.MaxValue; offset < total && offset < 5000; offset += PageSize)
        {
            string url = CellUrl(x, y, offset);
            var json = await cache.GetAsync(url, s_maxAge) ?? throw new HttpRequestException("The National Map's products API didn't answer.");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out int n) ? n : 0;
            if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) break;
            foreach (var item in items.EnumerateArray())
            {
                if (Parse(item) is { } tile) tiles.Add(tile);
            }
        }
        return tiles;
    }

    // The query of a cell (x and y in tenths of a degree) from an offset.
    internal static string CellUrl(int x, int y, int offset) => string.Create(CultureInfo.InvariantCulture,
        $"{Api}?datasets={Uri.EscapeDataString(OneMeter)}&bbox={x * Cell:0.###},{y * Cell:0.###},{(x + 1) * Cell:0.###},{(y + 1) * Cell:0.###}&prodFormats=GeoTIFF&max={PageSize}&offset={offset}");

    // An item: its download URL (…/1m/Projects/<project>/TIFF/USGS_1M_….tif), dates and box.
    internal static UsgsTile? Parse(JsonElement item)
    {
        if (!item.TryGetProperty("downloadURL", out var download) || download.GetString() is not { } url) return null;
        if (!url.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) return null;
        if (!item.TryGetProperty("boundingBox", out var box)) return null;
        double Read(string name) => box.TryGetProperty(name, out var v) && v.TryGetDouble(out double d) ? d : double.NaN;
        double west = Read("minX"), east = Read("maxX"), south = Read("minY"), north = Read("maxY");
        if (double.IsNaN(west + east + south + north)) return null;
        string project = "";
        int at = url.IndexOf("/Projects/", StringComparison.Ordinal);
        if (at >= 0)
        {
            int end = url.IndexOf('/', at + 10);
            project = end > at ? url[(at + 10)..end] : "";
        }
        var published = DateTime.MinValue;
        if (item.TryGetProperty("publicationDate", out var date) && date.GetString() is { } text)
        {
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out published);
        }
        string? updated = item.TryGetProperty("lastUpdated", out var u) ? u.GetString() : null;
        return new UsgsTile(url, project, published, updated, new GeoBounds(south, west, north, east));
    }
}

/// <summary>
/// The USGS 3DEP one-meter DEM: bare ground from airborne lidar wherever the 3D Elevation Program has flown it (most
/// of the conterminous United States), as 10 km Cloud Optimized GeoTIFFs in UTM (NAD83, heights NAVD88) with overviews
/// to 32 m. Lidar projects overlap: the newest is read first, the others fill its gaps.
/// </summary>
public sealed class Usgs3Dep1MeterSource(CogFiles files, NationalMapCatalog catalog) : IElevationSource
{
    private readonly BlockCache<float> _blocks = new();

    /// <inheritdoc/>
    public string Id => "usgs-3dep-1m";

    /// <inheritdoc/>
    public string Name => "USGS 3DEP 1 m (lidar)";

    /// <inheritdoc/>
    public string Description => "Lidar terrain model of most of the United States, bare ground, 1 m.";

    /// <inheritdoc/>
    public string Attribution => "U.S. Geological Survey, 3D Elevation Program";

    /// <inheritdoc/>
    public string License => "Public domain (a U.S. government work): free, including commercial use; credit to the USGS is asked.";

    /// <inheritdoc/>
    public double Resolution => 1;

    /// <inheritdoc/>
    public SourceScope Scope => SourceScope.Regional;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => UnitedStates.Covers(bounds);

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken)
    {
        if (!Covers(request.Bounds) || EmptyBounds(request, i => float.IsNaN(heights[i])) is not { } empty) return 0;
        var tiles = await catalog.FindAsync(empty, cancellationToken);
        if (tiles.Count == 0) return 0;
        var opened = await Task.WhenAll(tiles.Select(async t => (Tile: t, Tiff: await files.OpenAsync(t.Url, t.Updated, cancellationToken))));
        int filled = 0;
        // A pass per project and UTM zone, the newest project first: each fills what the ones before left.
        foreach (var group in opened.Where(o => o.Tiff != null).GroupBy(o => (o.Tile.Project, o.Tiff!.Epsg)).OrderByDescending(g => g.Max(o => o.Tile.Published)))
        {
            if (EpsgSystems.Find(group.Key.Epsg) is not { } system) continue;
            var levels = new List<RasterLevel<float>>();
            double pixel = double.MaxValue;
            foreach (var (_, tiff) in group)
            {
                int image = CogFiles.ChooseImage(tiff!, request.Spacing);
                levels.Add(new GeoTiffElevationLevel(tiff!, image));
                pixel = Math.Min(pixel, tiff!.PixelSize(image).Width);
            }
            filled += await RasterFill.FillHeightsAsync(request, heights, system, new RasterMosaic<float>(levels), pixel, _blocks, cancellationToken);
        }
        return filled;
    }

    // The box around the samples still to fill (and a pixel or two), or null when none are.
    internal static GeoBounds? EmptyBounds(SampleRequest request, Func<int, bool> isEmpty)
    {
        var (lons, lats) = request.Project(GeographicCoordinates.Instance);
        double south = double.MaxValue, north = double.MinValue, west = double.MaxValue, east = double.MinValue;
        for (int i = 0; i < lons.Length; i++)
        {
            if (!isEmpty(i)) continue;
            south = Math.Min(south, lats[i]);
            north = Math.Max(north, lats[i]);
            west = Math.Min(west, lons[i]);
            east = Math.Max(east, lons[i]);
        }
        if (south > north) return null;
        double margin = (request.Spacing * 2 + 2) / 111_320;
        double lonMargin = margin / Math.Max(0.1, Math.Cos((south + north) / 2 * Math.PI / 180));
        return new GeoBounds(south - margin, west - lonMargin, north + margin, east + lonMargin);
    }
}

/// <summary>
/// The USGS 3DEP 1/3 arc-second DEM: the seamless terrain model of the United States (about 10 m; lidar and older
/// sources), as 1° × 1° Cloud Optimized GeoTIFFs (NAD83, heights NAVD88) on The National Map's bucket. It fills the coarse
/// levels of a terrain with few files and wherever there is no lidar.
/// </summary>
public sealed class Usgs3Dep13Source(CogFiles files) : IElevationSource
{
    /// <summary>The tiles' folder.</summary>
    public const string Folder = "https://prd-tnm.s3.amazonaws.com/StagedProducts/Elevation/13/TIFF/current";

    private const double MetersPerDegree = 111_320;
    private readonly BlockCache<float> _blocks = new();

    /// <inheritdoc/>
    public string Id => "usgs-3dep-13";

    /// <inheritdoc/>
    public string Name => "USGS 3DEP 1/3 arc-second";

    /// <inheritdoc/>
    public string Description => "Seamless terrain model of the United States, bare ground, about 10 m.";

    /// <inheritdoc/>
    public string Attribution => "U.S. Geological Survey, 3D Elevation Program";

    /// <inheritdoc/>
    public string License => "Public domain (a U.S. government work): free, including commercial use; credit to the USGS is asked.";

    /// <inheritdoc/>
    public double Resolution => 10;

    /// <inheritdoc/>
    public SourceScope Scope => SourceScope.Regional;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => UnitedStates.Covers(bounds);

    /// <summary>Gets the URL of the tile whose north-west corner is at <paramref name="north"/>° N, <paramref name="west"/>° W (whole degrees).</summary>
    public static string TileUrl(int north, int west)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"{(north >= 0 ? 'n' : 's')}{Math.Abs(north):00}{(west >= 0 ? 'w' : 'e')}{Math.Abs(west):000}");
        return $"{Folder}/{name}/USGS_13_{name}.tif";
    }

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken)
    {
        if (!Covers(request.Bounds)) return 0;
        var (lons, lats) = request.Project(GeographicCoordinates.Instance);
        var cells = new HashSet<(int North, int West)>();
        for (int i = 0; i < heights.Length; i++)
        {
            if (!float.IsNaN(heights[i])) continue;
            // With a pixel of margin: interpolation near a tile's edge reads its neighbor.
            for (int dy = -1; dy <= 1; dy += 2)
            {
                for (int dx = -1; dx <= 1; dx += 2) cells.Add(((int)Math.Floor(lats[i] + dy * 0.0002) + 1, -(int)Math.Floor(lons[i] + dx * 0.0002)));
            }
        }
        if (cells.Count == 0) return 0;
        var tiffs = await files.OpenAllAsync(cells.Select(c => (TileUrl(c.North, c.West), (string?)null)), cancellationToken);
        if (tiffs.Count == 0) return 0;
        var levels = new List<RasterLevel<float>>();
        double pixel = double.MaxValue;
        foreach (var tiff in tiffs)
        {
            int image = CogFiles.ChooseImage(tiff, request.Spacing, MetersPerDegree);
            levels.Add(new GeoTiffElevationLevel(tiff, image));
            pixel = Math.Min(pixel, tiff.PixelSize(image).Height * MetersPerDegree);
        }
        return await RasterFill.FillHeightsAsync(request, heights, GeographicCoordinates.Instance, new RasterMosaic<float>(levels), pixel, _blocks, cancellationToken);
    }
}
