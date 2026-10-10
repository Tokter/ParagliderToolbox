using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Terrain;

/// <summary>What colors a terrain's surface.</summary>
public enum TerrainTexture
{
    /// <summary>Aerial or satellite images of the best source there is; elevation colors where there is none.</summary>
    Imagery,

    /// <summary>Colors from the height and slope: meadows, forest, rock and snow.</summary>
    ElevationColors,

    /// <summary>No texture (a plain surface).</summary>
    None,
}

/// <summary>How a terrain's surface is shaded.</summary>
public enum TerrainShading
{
    /// <summary>Smooth: shared vertices with interpolated normals, textured.</summary>
    Smooth,

    /// <summary>Faceted (low poly): every triangle flat, in one color (the texture's average over it).</summary>
    Faceted,
}

/// <summary>
/// The parameters of a terrain: where (the center), how big, how fine, how it is split into tiles and levels of detail,
/// and how it is colored. Everything else follows from them (see <see cref="TerrainLayout"/>).
/// </summary>
public sealed record TerrainSettings
{
    /// <summary>The samples per tile side offered: 2^k + 1, as game engines' heightmaps need.</summary>
    public static readonly IReadOnlyList<int> TileSampleChoices = [17, 33, 65, 129, 257, 513, 1025];

    /// <summary>Gets the terrain's center (the origin of its frame).</summary>
    public GeoPoint Center { get; init; } = new(46.6863, 7.8632);

    /// <summary>Gets the side of the square area (m); rounded to whole finest tiles.</summary>
    public double Size { get; init; } = 5000;

    /// <summary>Gets the distance between the finest level's height samples (m).</summary>
    public double Resolution { get; init; } = 4;

    /// <summary>Gets the height samples per tile side (one of <see cref="TileSampleChoices"/>).</summary>
    public int TileSamples { get; init; } = 129;

    /// <summary>Gets the number of levels of detail, each half as fine as the one before; 0 for as many as reach a single tile.</summary>
    public int LodLevels { get; init; }

    /// <summary>
    /// Gets the side of the square around the center with the finest level (m); each coarser level covers twice as much.
    /// 0 for the finest level everywhere.
    /// </summary>
    public double DetailSize { get; init; }

    /// <summary>Gets what colors the surface.</summary>
    public TerrainTexture Texture { get; init; } = TerrainTexture.Imagery;

    /// <summary>Gets the texture pixels per tile side (a power of two).</summary>
    public int TextureSize { get; init; } = 512;

    /// <summary>Gets how the surface is shaded.</summary>
    public TerrainShading Shading { get; init; } = TerrainShading.Smooth;

    /// <summary>Gets the id of the elevation source asked first, or <c>null</c> for the finest that covers each point.</summary>
    public string? ElevationSource { get; init; }

    /// <summary>Gets the id of the imagery source asked first, or <c>null</c> for the finest that covers each point.</summary>
    public string? ImagerySource { get; init; }

    /// <summary>Gets the nearest offered tile sample count to <paramref name="samples"/>.</summary>
    public static int NearestTileSamples(int samples) => TileSampleChoices.MinBy(c => Math.Abs(Math.Log((double)c / Math.Max(2, samples))));

    /// <summary>Gets the nearest power of two (16–4096) to <paramref name="size"/>.</summary>
    public static int NearestTextureSize(int size) =>
        1 << Math.Clamp((int)Math.Round(Math.Log2(Math.Max(1, size))), 4, 12);
}
