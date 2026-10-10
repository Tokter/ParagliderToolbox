using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain.Sources;

/// <summary>What kind of data a source has, which ranks it before its resolution.</summary>
public enum SourceScope
{
    /// <summary>Measured national or regional data (lidar terrain models, aerial images): always preferred.</summary>
    Regional,

    /// <summary>Worldwide data (satellite surface models and images): where there is nothing better.</summary>
    Global,
}

/// <summary>
/// Where terrain data comes from: a dataset with its coverage, resolution and the terms it may be used under.
/// </summary>
/// <remarks>
/// The builder asks the sources that may cover an area in order (see <see cref="TerrainSources"/>), each filling the
/// samples the ones before left empty, so every point gets the best data there is: a terrain across the Swiss border
/// takes swissALTI3D in Switzerland and swissALTIRegio or Copernicus beyond. To add a dataset, implement
/// <see cref="IElevationSource"/> or <see cref="IImagerySource"/> and add it to <see cref="TerrainSources"/>.
/// </remarks>
public interface ITerrainSource
{
    /// <summary>Gets the stable id saved in project files, e.g. <c>"swissalti3d"</c>.</summary>
    string Id { get; }

    /// <summary>Gets the name shown to users, e.g. "swissALTI3D (swisstopo)".</summary>
    string Name { get; }

    /// <summary>Gets what the data is and where it covers.</summary>
    string Description { get; }

    /// <summary>Gets the credit to show with the terrain and in exports.</summary>
    string Attribution { get; }

    /// <summary>Gets the terms of use, in short.</summary>
    string License { get; }

    /// <summary>Gets the finest ground resolution of the data (m), which ranks the sources of a scope.</summary>
    double Resolution { get; }

    /// <summary>Gets whether the data is regional (preferred) or worldwide.</summary>
    SourceScope Scope { get; }

    /// <summary>Gets whether the source may have data in <paramref name="bounds"/> (a quick check: it may still have gaps).</summary>
    bool Covers(GeoBounds bounds);
}

/// <summary>A source of heights above sea level.</summary>
public interface IElevationSource : ITerrainSource
{
    /// <summary>
    /// Fills the samples of <paramref name="request"/> whose height is <see cref="float.NaN"/> where the source has
    /// data, leaving the others as they are.
    /// </summary>
    /// <returns>The number of samples filled.</returns>
    /// <exception cref="HttpRequestException">The data couldn't be downloaded.</exception>
    Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken);
}

/// <summary>A source of aerial or satellite images.</summary>
public interface IImagerySource : ITerrainSource
{
    /// <summary>
    /// Fills the samples of <paramref name="request"/> whose color is 0 (transparent) with opaque RGBA colors (red in
    /// the lowest byte) where the source has data, leaving the others as they are.
    /// </summary>
    /// <returns>The number of samples filled.</returns>
    /// <exception cref="HttpRequestException">The data couldn't be downloaded.</exception>
    Task<int> FillAsync(SampleRequest request, uint[] colors, CancellationToken cancellationToken);
}
