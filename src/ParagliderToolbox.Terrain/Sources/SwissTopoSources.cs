using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sampling;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>
/// swissALTI3D, swisstopo's terrain model of Switzerland and Liechtenstein: bare ground (no trees or buildings) from
/// airborne lidar, 0.5 m and 2 m, as 1 km Cloud Optimized GeoTIFFs in LV95.
/// </summary>
public sealed class SwissAlti3DSource(CogFiles files, SwissTopoCatalog catalog) : IElevationSource
{
    /// <summary>The STAC collection.</summary>
    public const string Collection = "ch.swisstopo.swissalti3d";

    private readonly BlockCache<float> _blocks = new();

    /// <summary>The area the Swiss grid datasets may cover (Switzerland and Liechtenstein with a margin).</summary>
    public static readonly GeoBounds SwissBounds = new(45.80, 5.94, 47.83, 10.51);

    /// <inheritdoc/>
    public string Id => "swissalti3d";

    /// <inheritdoc/>
    public string Name => "swissALTI3D (swisstopo)";

    /// <inheritdoc/>
    public string Description => "Lidar terrain model of Switzerland and Liechtenstein, bare ground, 0.5 m and 2 m.";

    /// <inheritdoc/>
    public string Attribution => "© swisstopo";

    /// <inheritdoc/>
    public string License => "swisstopo open government data: free, including commercial use; the source must be credited.";

    /// <inheritdoc/>
    public double Resolution => 0.5;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => bounds.Intersects(SwissBounds);

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken)
    {
        if (!Covers(request.Bounds)) return 0;
        var levels = await SwissLevels.FindAsync(files, catalog, Collection, request, i => float.IsNaN(heights[i]),
            (tiff, image) => new GeoTiffElevationLevel(tiff, image), cancellationToken);
        if (levels is null) return 0;
        return await RasterFill.FillHeightsAsync(request, heights, SwissGrid.Instance, new RasterMosaic<float>(levels.Value.Levels),
            levels.Value.PixelMeters, _blocks, cancellationToken);
    }
}

/// <summary>
/// SWISSIMAGE, swisstopo's orthophoto mosaic of Switzerland: 10 cm (25 cm over the Alps) and 2 m, as 1 km Cloud
/// Optimized GeoTIFFs (JPEG) in LV95.
/// </summary>
public sealed class SwissImageSource(CogFiles files, SwissTopoCatalog catalog) : IImagerySource
{
    /// <summary>The STAC collection.</summary>
    public const string Collection = "ch.swisstopo.swissimage-dop10";

    private readonly BlockCache<uint> _blocks = new();

    /// <inheritdoc/>
    public string Id => "swissimage";

    /// <inheritdoc/>
    public string Name => "SWISSIMAGE (swisstopo)";

    /// <inheritdoc/>
    public string Description => "Aerial orthophotos of Switzerland, 10 cm (25 cm over the Alps).";

    /// <inheritdoc/>
    public string Attribution => "© swisstopo";

    /// <inheritdoc/>
    public string License => "swisstopo open government data: free, including commercial use; the source must be credited.";

    /// <inheritdoc/>
    public double Resolution => 0.1;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => bounds.Intersects(SwissAlti3DSource.SwissBounds);

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, uint[] colors, CancellationToken cancellationToken)
    {
        if (!Covers(request.Bounds)) return 0;
        var levels = await SwissLevels.FindAsync(files, catalog, Collection, request, i => colors[i] >> 24 == 0,
            (tiff, image) => new GeoTiffColorLevel(tiff, image), cancellationToken);
        if (levels is null) return 0;
        return await RasterFill.FillColorsAsync(request, colors, SwissGrid.Instance, new RasterMosaic<uint>(levels.Value.Levels),
            levels.Value.PixelMeters, _blocks, cancellationToken);
    }
}

/// <summary>Finds and opens the swisstopo files covering a request, at the resolution that suits it.</summary>
internal static class SwissLevels
{
    public static async Task<(List<RasterLevel<T>> Levels, double PixelMeters)?> FindAsync<T>(CogFiles files, SwissTopoCatalog catalog,
        string collection, SampleRequest request, Func<int, bool> isEmpty, Func<Data.GeoTiff, int, RasterLevel<T>> level,
        CancellationToken cancellationToken)
    {
        var (es, ns) = request.Project(SwissGrid.Instance);
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        for (int i = 0; i < es.Length; i++)
        {
            if (!isEmpty(i)) continue;
            minE = Math.Min(minE, es[i]);
            maxE = Math.Max(maxE, es[i]);
            minN = Math.Min(minN, ns[i]);
            maxN = Math.Max(maxN, ns[i]);
        }
        if (minE > maxE) return null;
        double margin = request.Spacing * 2 + 2;
        var tiles = await catalog.FindAsync(collection, minE - margin, minN - margin, maxE + margin, maxN + margin, cancellationToken);
        if (tiles.Count == 0) return null;

        var assets = tiles.Select(t => SwissTopoCatalog.ChooseAsset(t, request.Spacing)).OfType<SwissAsset>().ToList();
        var tiffs = await files.OpenAllAsync(assets.Select(a => (a.Href, a.Updated)), cancellationToken);
        if (tiffs.Count == 0) return null;
        var levels = new List<RasterLevel<T>>(tiffs.Count);
        double pixel = double.MaxValue;
        foreach (var tiff in tiffs)
        {
            int image = CogFiles.ChooseImage(tiff, request.Spacing);
            levels.Add(level(tiff, image));
            pixel = Math.Min(pixel, tiff.PixelSize(image).Width);
        }
        return (levels, pixel);
    }
}
