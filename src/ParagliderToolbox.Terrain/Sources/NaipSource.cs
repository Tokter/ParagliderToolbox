using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sampling;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>A NAIP quarter-quad image: its file, the year and resolution it was flown at, its box.</summary>
/// <param name="Url">The Cloud Optimized GeoTIFF's URL.</param>
/// <param name="QuarterQuad">The quarter quadrangle (3.75′ × 3.75′) it covers, e.g. <c>m_3812237_sw</c>: one image per year.</param>
/// <param name="Year">The year it was flown.</param>
/// <param name="Gsd">Its ground sample distance (m).</param>
/// <param name="Bounds">Its box (WGS84).</param>
public sealed record NaipImage(string Url, string QuarterQuad, int Year, double Gsd, GeoBounds Bounds);

/// <summary>
/// Finds NAIP's images through Microsoft's Planetary Computer STAC API: the newest image of every quarter quadrangle
/// covering an area.
/// </summary>
/// <remarks>The API is asked in cells of 0.1°, so the same queries recur and are answered from the cache (for 30 days, and when offline).</remarks>
public sealed class NaipCatalog(DataCache cache)
{
    /// <summary>The STAC search endpoint.</summary>
    public const string Api = "https://planetarycomputer.microsoft.com/api/stac/v1/search";

    private const double Cell = 0.1;
    private static readonly TimeSpan s_maxAge = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<(int, int), Lazy<Task<List<NaipImage>>>> _cells = new();

    /// <summary>Gets the newest image of every quarter quadrangle that overlaps <paramref name="bounds"/>.</summary>
    public async Task<List<NaipImage>> FindAsync(GeoBounds bounds, CancellationToken cancellationToken)
    {
        var cells = new List<Task<List<NaipImage>>>();
        for (int y = (int)Math.Floor(bounds.South / Cell); y <= (int)Math.Floor(bounds.North / Cell); y++)
        {
            for (int x = (int)Math.Floor(bounds.West / Cell); x <= (int)Math.Floor(bounds.East / Cell); x++) cells.Add(CellAsync(x, y, cancellationToken));
        }
        var newest = new Dictionary<string, NaipImage>();
        foreach (var cell in await Task.WhenAll(cells))
        {
            foreach (var image in cell)
            {
                if (!image.Bounds.Intersects(bounds)) continue;
                if (!newest.TryGetValue(image.QuarterQuad, out var other) || image.Year > other.Year || image.Year == other.Year && image.Gsd < other.Gsd)
                {
                    newest[image.QuarterQuad] = image;
                }
            }
        }
        return newest.Values.ToList();
    }

    private async Task<List<NaipImage>> CellAsync(int x, int y, CancellationToken cancellationToken)
    {
        var lazy = _cells.GetOrAdd((x, y), _ => new Lazy<Task<List<NaipImage>>>(() => Task.Run(() => QueryCellAsync(x, y))));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _cells.TryRemove(new KeyValuePair<(int, int), Lazy<Task<List<NaipImage>>>>((x, y), lazy));
            throw;
        }
    }

    private async Task<List<NaipImage>> QueryCellAsync(int x, int y)
    {
        var images = new List<NaipImage>();
        string? url = CellUrl(x, y);
        for (int page = 0; url != null && page < 40; page++)
        {
            var json = await cache.GetAsync(url, s_maxAge) ?? throw new HttpRequestException("The Planetary Computer's catalog didn't answer.");
            url = null;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    if (Parse(feature) is { } image) images.Add(image);
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
        return images;
    }

    // The search of a cell (x and y in tenths of a degree).
    internal static string CellUrl(int x, int y) => string.Create(CultureInfo.InvariantCulture,
        $"{Api}?collections=naip&bbox={x * Cell:0.###},{y * Cell:0.###},{(x + 1) * Cell:0.###},{(y + 1) * Cell:0.###}&limit=250");

    // A feature: its image asset (…/m_3812237_sw_10_060_20220524.tif), year, resolution and box.
    internal static NaipImage? Parse(JsonElement feature)
    {
        if (!feature.TryGetProperty("assets", out var assets) || !assets.TryGetProperty("image", out var image) ||
            !image.TryGetProperty("href", out var href) || href.GetString() is not { } url)
        {
            return null;
        }
        if (!feature.TryGetProperty("bbox", out var bbox) || bbox.GetArrayLength() < 4) return null;
        var box = new GeoBounds(bbox[1].GetDouble(), bbox[0].GetDouble(), bbox[3].GetDouble(), bbox[2].GetDouble());
        string name = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
        var parts = name.Split('_');
        string quarterQuad = parts.Length >= 3 ? string.Join('_', parts[..3]) : name;
        int year = 0;
        double gsd = 1;
        if (feature.TryGetProperty("properties", out var properties))
        {
            if (properties.TryGetProperty("naip:year", out var y) && int.TryParse(y.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) year = value;
            if (properties.TryGetProperty("gsd", out var g) && g.TryGetDouble(out double d)) gsd = d;
        }
        return new NaipImage(url, quarterQuad, year, gsd, box);
    }
}

/// <summary>
/// NAIP, the USDA's National Agriculture Imagery Program: aerial images of the conterminous United States flown every
/// two or three years in the growing season, 60 cm in recent years (1 m before), as quarter-quadrangle Cloud Optimized
/// GeoTIFFs in UTM (red, green, blue and near-infrared) on Microsoft's Planetary Computer. The newest image of each
/// quarter quadrangle is used; their corners outside it are black, which counts as no image.
/// </summary>
public sealed class NaipSource(CogFiles files, NaipCatalog catalog) : IImagerySource
{
    // The quarter quadrangles' size (°): the images of neighbors overlap (each covers its own with black around it).
    private const double QuarterQuad = 3.75 / 60;

    private readonly BlockCache<uint> _blocks = new();

    /// <inheritdoc/>
    public string Id => "naip";

    /// <inheritdoc/>
    public string Name => "NAIP (USDA)";

    /// <inheritdoc/>
    public string Description => "Aerial photos of the conterminous United States, 60 cm (recent years), summer.";

    /// <inheritdoc/>
    public string Attribution => "USDA Farm Production and Conservation Business Center, National Agriculture Imagery Program; via Microsoft Planetary Computer";

    /// <inheritdoc/>
    public string License => "Public domain (a U.S. government work): free, including commercial use; credit to the USDA is asked.";

    /// <inheritdoc/>
    public double Resolution => 0.6;

    /// <inheritdoc/>
    public SourceScope Scope => SourceScope.Regional;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => bounds.Intersects(UnitedStates.Areas[0]);

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, uint[] colors, CancellationToken cancellationToken)
    {
        if (!Covers(request.Bounds) || Usgs3Dep1MeterSource.EmptyBounds(request, i => colors[i] >> 24 == 0) is not { } empty) return 0;
        var images = await catalog.FindAsync(empty, cancellationToken);
        if (images.Count == 0) return 0;
        var opened = await Task.WhenAll(images.Select(async i => (Image: i, Tiff: await files.OpenAsync(i.Url, i.Year.ToString(CultureInfo.InvariantCulture), cancellationToken))));
        int filled = 0;
        // Neighbors overlap: a pass per UTM zone and checkerboard square of the quarter quadrangles, so a pass's images
        // don't, and each fills where the ones before had black.
        foreach (var group in opened.Where(o => o.Tiff != null).GroupBy(o => (o.Tiff!.Epsg, Square(o.Image.Bounds))))
        {
            if (EpsgSystems.Find(group.Key.Epsg) is not { } system) continue;
            var levels = new List<RasterLevel<uint>>();
            double pixel = double.MaxValue;
            foreach (var (_, tiff) in group)
            {
                int image = CogFiles.ChooseImage(tiff!, request.Spacing);
                levels.Add(new NaipLevel(tiff!, image));
                pixel = Math.Min(pixel, tiff!.PixelSize(image).Width);
            }
            filled += await RasterFill.FillColorsAsync(request, colors, system, new RasterMosaic<uint>(levels), pixel, _blocks, cancellationToken);
        }
        return filled;
    }

    // Which of four checkerboard squares a quarter quadrangle is (by its center).
    private static int Square(GeoBounds bounds)
    {
        int column = (int)Math.Floor((bounds.West + bounds.East) / 2 / QuarterQuad), row = (int)Math.Floor((bounds.South + bounds.North) / 2 / QuarterQuad);
        return (column & 1) | (row & 1) << 1;
    }
}

/// <summary>An image of a NAIP file read as colors: black (its corners outside its quarter quadrangle) is no image.</summary>
internal sealed class NaipLevel(GeoTiff tiff, int image) : RasterLevel<uint>(
    tiff.FirstPixelCenter(image).X, tiff.FirstPixelCenter(image).Y, tiff.PixelSize(image).Width, tiff.PixelSize(image).Height,
    tiff.Images[image].Width, tiff.Images[image].Height, tiff.Images[image].BlockWidth, tiff.Images[image].BlockHeight)
{
    /// <inheritdoc/>
    public override string Key { get; } = tiff.Name + "#" + image;

    /// <inheritdoc/>
    public override async Task<uint[]?> LoadBlockAsync(int blockX, int blockY, CancellationToken cancellationToken)
    {
        var colors = await tiff.ReadColorBlockAsync(image, blockX, blockY, cancellationToken);
        for (int i = 0; i < colors.Length; i++)
        {
            if ((colors[i] & 0xFFFFFF) == 0) colors[i] = 0;
        }
        return colors;
    }
}
