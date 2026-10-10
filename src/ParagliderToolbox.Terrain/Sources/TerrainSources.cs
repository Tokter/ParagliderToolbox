using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>
/// The elevation and imagery sources a terrain can be built from, and the order they are asked in for an area: the
/// preferred source first (if any), then the others that may cover it, finest first.
/// </summary>
/// <example>
/// <code>
/// var sources = TerrainSources.CreateDefault(new DataCache(cacheFolder));
/// sources.Elevation.Add(new MyLidarSource(...)); // a new dataset takes part by its resolution
/// </code>
/// </example>
public sealed class TerrainSources
{
    /// <summary>Gets the elevation sources.</summary>
    public List<IElevationSource> Elevation { get; } = [];

    /// <summary>Gets the imagery sources.</summary>
    public List<IImagerySource> Imagery { get; } = [];

    /// <summary>Gets the cache the built-in sources download through, or <c>null</c>.</summary>
    public DataCache? Cache { get; init; }

    /// <summary>
    /// Creates the built-in sources: swissALTI3D and SWISSIMAGE (swisstopo, Switzerland), the Copernicus DEM GLO-30 and
    /// EOX's Sentinel-2 cloudless 2016 (worldwide), downloading through <paramref name="cache"/>.
    /// </summary>
    public static TerrainSources CreateDefault(DataCache cache)
    {
        var files = new CogFiles(cache);
        var swiss = new SwissTopoCatalog(cache);
        var sources = new TerrainSources { Cache = cache };
        sources.Elevation.Add(new SwissAlti3DSource(files, swiss));
        sources.Elevation.Add(new CopernicusDemSource(files));
        sources.Imagery.Add(new SwissImageSource(files, swiss));
        sources.Imagery.Add(new Sentinel2CloudlessSource(cache));
        return sources;
    }

    /// <summary>Gets the source with <paramref name="id"/>, or <c>null</c>.</summary>
    public ITerrainSource? Find(string? id) =>
        id is null ? null : (ITerrainSource?)Elevation.FirstOrDefault(s => s.Id == id) ?? Imagery.FirstOrDefault(s => s.Id == id);

    /// <summary>Gets the elevation sources to ask for <paramref name="bounds"/>, in order.</summary>
    public IReadOnlyList<IElevationSource> ElevationFor(GeoBounds bounds, string? preferred) => Order(Elevation, bounds, preferred);

    /// <summary>Gets the imagery sources to ask for <paramref name="bounds"/>, in order.</summary>
    public IReadOnlyList<IImagerySource> ImageryFor(GeoBounds bounds, string? preferred) => Order(Imagery, bounds, preferred);

    private static List<T> Order<T>(List<T> sources, GeoBounds bounds, string? preferred) where T : ITerrainSource =>
        sources.Where(s => s.Covers(bounds)).OrderBy(s => s.Id == preferred ? 0 : 1).ThenBy(s => s.Resolution).ToList();
}
