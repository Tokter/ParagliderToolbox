using System.Globalization;
using System.Runtime.InteropServices;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sampling;
using SkiaSharp;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>
/// The Copernicus DEM GLO-30: a worldwide surface model (it includes forests and buildings) of about 30 m, as 1° × 1°
/// Cloud Optimized GeoTIFFs on AWS's open data registry. Tiles that don't exist are sea, at height 0.
/// </summary>
public sealed class CopernicusDemSource(CogFiles files) : IElevationSource
{
    /// <summary>The bucket's address.</summary>
    public const string Bucket = "https://copernicus-dem-30m.s3.amazonaws.com";

    private const double MetersPerDegree = 111_320;
    private readonly BlockCache<float> _blocks = new();

    /// <inheritdoc/>
    public string Id => "copernicus-glo30";

    /// <inheritdoc/>
    public string Name => "Copernicus DEM GLO-30";

    /// <inheritdoc/>
    public string Description => "Worldwide surface model, about 30 m; includes forests and buildings.";

    /// <inheritdoc/>
    public string Attribution =>
        "Produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved";

    /// <inheritdoc/>
    public string License =>
        "Copernicus DEM licence: free, including commercial use; credit and the disclaimer that the Copernicus programme bodies are not liable for its use are required.";

    /// <inheritdoc/>
    public double Resolution => 30;

    /// <inheritdoc/>
    public SourceScope Scope => SourceScope.Global;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => true;

    /// <summary>Gets the URL of the tile whose south-west corner is at <paramref name="latitude"/>, <paramref name="longitude"/> (whole degrees).</summary>
    public static string TileUrl(int latitude, int longitude)
    {
        string name = string.Create(CultureInfo.InvariantCulture,
            $"Copernicus_DSM_COG_10_{(latitude >= 0 ? 'N' : 'S')}{Math.Abs(latitude):00}_00_{(longitude >= 0 ? 'E' : 'W')}{Math.Abs(longitude):000}_00_DEM");
        return $"{Bucket}/{name}/{name}.tif";
    }

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken)
    {
        var (lons, lats) = request.Project(GeographicCoordinates.Instance);
        var cells = new HashSet<(int Lat, int Lon)>();
        for (int i = 0; i < heights.Length; i++)
        {
            if (!float.IsNaN(heights[i])) continue;
            // With a pixel of margin: interpolation near a tile's edge reads its neighbor.
            for (int dy = -1; dy <= 1; dy += 2)
            {
                for (int dx = -1; dx <= 1; dx += 2)
                {
                    cells.Add(((int)Math.Floor(lats[i] + dy * 0.0006), (int)Math.Floor(lons[i] + dx * 0.0006)));
                }
            }
        }
        if (cells.Count == 0) return 0;

        var opened = await Task.WhenAll(cells.Select(async c => (Cell: c, Tiff: await files.OpenAsync(TileUrl(c.Lat, c.Lon), null, cancellationToken))));
        var levels = new List<RasterLevel<float>>();
        double pixel = double.MaxValue;
        double metersPerDegree = MetersPerDegree;
        foreach (var (_, tiff) in opened)
        {
            if (tiff is null) continue;
            int image = CogFiles.ChooseImage(tiff, request.Spacing, metersPerDegree);
            levels.Add(new GeoTiffElevationLevel(tiff, image));
            pixel = Math.Min(pixel, tiff.PixelSize(image).Height * metersPerDegree);
        }
        int filled = levels.Count == 0 ? 0 : await RasterFill.FillHeightsAsync(request, heights, GeographicCoordinates.Instance,
            new RasterMosaic<float>(levels), pixel, _blocks, cancellationToken);

        // The sea has no tiles.
        var sea = opened.Where(o => o.Tiff is null).Select(o => o.Cell).ToHashSet();
        if (sea.Count == 0) return filled;
        for (int i = 0; i < heights.Length; i++)
        {
            if (float.IsNaN(heights[i]) && sea.Contains(((int)Math.Floor(lats[i]), (int)Math.Floor(lons[i]))))
            {
                heights[i] = 0;
                filled++;
            }
        }
        return filled;
    }
}

/// <summary>
/// EOX's Sentinel-2 cloudless mosaic of 2016: a worldwide cloud-free satellite image of about 10 m from Copernicus
/// Sentinel-2 data, served as web map tiles. The 2016 edition is free under CC BY 4.0 (the later ones only for
/// non-commercial use).
/// </summary>
public sealed class Sentinel2CloudlessSource(DataCache cache) : IImagerySource
{
    /// <summary>The tiles' URL: zoom, row, column.</summary>
    public const string TileTemplate = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{0}/{1}/{2}.jpg";

    /// <summary>The finest zoom level used (about 6.5 m per pixel at 46°; the data is 10 m).</summary>
    public const int MaxZoom = 14;

    private readonly BlockCache<uint> _blocks = new();

    /// <inheritdoc/>
    public string Id => "s2cloudless-2016";

    /// <inheritdoc/>
    public string Name => "Sentinel-2 cloudless 2016 (EOX)";

    /// <inheritdoc/>
    public string Description => "Worldwide cloud-free satellite mosaic, about 10 m.";

    /// <inheritdoc/>
    public string Attribution => "Sentinel-2 cloudless 2016 by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016 & 2017)";

    /// <inheritdoc/>
    public string License => "CC BY 4.0: free, including commercial use, with attribution.";

    /// <inheritdoc/>
    public double Resolution => 10;

    /// <inheritdoc/>
    public SourceScope Scope => SourceScope.Global;

    /// <inheritdoc/>
    public bool Covers(GeoBounds bounds) => bounds.South < WebMercator.MaxLatitude && bounds.North > -WebMercator.MaxLatitude;

    /// <inheritdoc/>
    public async Task<int> FillAsync(SampleRequest request, uint[] colors, CancellationToken cancellationToken)
    {
        if (!Covers(request.Bounds)) return 0;
        double latitude = (request.Bounds.South + request.Bounds.North) / 2;
        double groundPerTile = 2 * WebMercator.HalfWorld * Math.Cos(latitude * Math.PI / 180) / 256;
        int zoom = Math.Clamp((int)Math.Ceiling(Math.Log2(groundPerTile / request.Spacing)), 0, MaxZoom);
        var level = new WebTileLevel(cache, TileTemplate, zoom);
        return await RasterFill.FillColorsAsync(request, colors, WebMercator.Instance, new RasterMosaic<uint>([level]),
            groundPerTile / (1 << zoom), _blocks, cancellationToken);
    }
}

/// <summary>A zoom level of a web map tile service (Web Mercator, 256 px JPEG or PNG tiles) as a raster.</summary>
public sealed class WebTileLevel(DataCache cache, string template, int zoom) : RasterLevel<uint>(
    -WebMercator.HalfWorld + WebMercator.HalfWorld / (256 << zoom), WebMercator.HalfWorld - WebMercator.HalfWorld / (256 << zoom),
    WebMercator.HalfWorld / (128 << zoom), WebMercator.HalfWorld / (128 << zoom), 256 << zoom, 256 << zoom, 256, 256)
{
    private static readonly TimeSpan s_maxAge = TimeSpan.FromDays(365);

    /// <inheritdoc/>
    public override string Key { get; } = template + "#" + zoom;

    /// <inheritdoc/>
    public override async Task<uint[]?> LoadBlockAsync(int blockX, int blockY, CancellationToken cancellationToken)
    {
        string url = string.Format(CultureInfo.InvariantCulture, template, zoom, blockY, blockX);
        var data = await cache.GetAsync(url, s_maxAge, cancellationToken);
        if (data is null || data.Length == 0) return null;
        using var bitmap = SKBitmap.Decode(data, new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (bitmap is null) return null;
        var pixels = new uint[256 * 256];
        MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()).CopyTo(pixels);
        return pixels;
    }
}
