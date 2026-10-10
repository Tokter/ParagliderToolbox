using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>
/// The elevation and imagery sources a terrain can be built from, and the order they are asked in for an area: the
/// preferred source first (if any), then the regional sources before the global ones; within each, those fine enough
/// for the samples' spacing first, the coarsest of them first (it has the same detail at that spacing and much less to
/// download), then the others finest first.
/// </summary>
/// <remarks>
/// So a coarse level of detail reads swissALTIRegio's 10 m terrain model (one file) instead of hundreds of
/// swissALTI3D tiles, the finest levels read swissALTI3D, and swissALTIRegio still fills where swissALTI3D ends,
/// before Copernicus.
/// </remarks>
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
    /// Creates the built-in sources: swissALTI3D, swissALTIRegio and SWISSIMAGE (swisstopo, Switzerland and around it),
    /// the Copernicus DEM GLO-30 and EOX's Sentinel-2 cloudless 2016 (worldwide), downloading through
    /// <paramref name="cache"/>.
    /// </summary>
    public static TerrainSources CreateDefault(DataCache cache)
    {
        var files = new CogFiles(cache);
        var swiss = new SwissTopoCatalog(cache);
        var sources = new TerrainSources { Cache = cache };
        sources.Elevation.Add(new SwissAlti3DSource(files, swiss));
        sources.Elevation.Add(new SwissAltiRegioSource(files, swiss));
        sources.Elevation.Add(new CopernicusDemSource(files));
        sources.Imagery.Add(new SwissImageSource(files, swiss));
        sources.Imagery.Add(new Sentinel2CloudlessSource(cache));
        return sources;
    }

    /// <summary>Gets the source with <paramref name="id"/>, or <c>null</c>.</summary>
    public ITerrainSource? Find(string? id) =>
        id is null ? null : (ITerrainSource?)Elevation.FirstOrDefault(s => s.Id == id) ?? Imagery.FirstOrDefault(s => s.Id == id);

    /// <summary>Gets the elevation sources to ask for samples <paramref name="spacing"/> meters apart in <paramref name="bounds"/>, in order.</summary>
    public IReadOnlyList<IElevationSource> ElevationFor(GeoBounds bounds, string? preferred, double spacing) => Order(Elevation, bounds, preferred, spacing);

    /// <summary>Gets the imagery sources to ask for texels <paramref name="spacing"/> meters apart in <paramref name="bounds"/>, in order.</summary>
    public IReadOnlyList<IImagerySource> ImageryFor(GeoBounds bounds, string? preferred, double spacing) => Order(Imagery, bounds, preferred, spacing);

    private static List<T> Order<T>(List<T> sources, GeoBounds bounds, string? preferred, double spacing) where T : ITerrainSource =>
        sources.Where(s => s.Covers(bounds))
            .OrderBy(s => s.Id == preferred ? 0 : 1)
            .ThenBy(s => s.Scope)
            .ThenBy(s => FineEnough(s, spacing) ? 0 : 1)
            .ThenBy(s => FineEnough(s, spacing) ? -s.Resolution : s.Resolution)
            .ToList();

    private static bool FineEnough(ITerrainSource source, double spacing) => source.Resolution <= spacing * 1.01;
}
