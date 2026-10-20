namespace ParagliderToolbox.Terrain.Features;

/// <summary>A rectangle of a terrain's frame to find features in.</summary>
/// <param name="X0">The x (east) of its west edge (m).</param>
/// <param name="Z0">The z (south) of its north edge (m).</param>
/// <param name="Width">Its east–west extent (m).</param>
/// <param name="Depth">Its north–south extent (m).</param>
public readonly record struct FeatureArea(double X0, double Z0, double Width, double Depth)
{
    /// <summary>Gets the square of side <paramref name="size"/> around (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public static FeatureArea Around(double x, double z, double size) => new(x - size / 2, z - size / 2, size, size);

    /// <summary>Gets the part of the area inside <paramref name="other"/>, or <c>null</c> when they don't overlap.</summary>
    public FeatureArea? Intersect(FeatureArea other)
    {
        double x0 = Math.Max(X0, other.X0), z0 = Math.Max(Z0, other.Z0);
        double x1 = Math.Min(X0 + Width, other.X0 + other.Width), z1 = Math.Min(Z0 + Depth, other.Z0 + other.Depth);
        return x1 > x0 && z1 > z0 ? new FeatureArea(x0, z0, x1 - x0, z1 - z0) : null;
    }
}

/// <summary>Where roads and paths come from.</summary>
public enum RoadSource
{
    /// <summary>OpenStreetMap (the images where it can't be reached): consistent classes and a real network.</summary>
    OpenStreetMap,

    /// <summary>Traced in the aerial images.</summary>
    Images,

    /// <summary>OpenStreetMap, and the images' roads where the map has none near.</summary>
    Both,
}

/// <summary>What <see cref="FeatureDetector"/> looks for, and how finely.</summary>
public sealed record FeatureSettings
{
    /// <summary>The pixel sizes offered (m): finer finds narrower paths and smaller trees, and reads four times as much.</summary>
    public static readonly IReadOnlyList<double> SpacingChoices = [0.25, 0.5, 1];

    /// <summary>Gets the pixel size the images and heights are read at (m).</summary>
    public double Spacing { get; init; } = 0.5;

    /// <summary>Gets whether trees are found.</summary>
    public bool Trees { get; init; } = true;

    /// <summary>Gets whether bushes (lower than <see cref="MinTreeHeight"/>, at least 1.2 m) are found with the trees.</summary>
    public bool Bushes { get; init; } = true;

    /// <summary>Gets the least height of a tree (m); lower ones are bushes.</summary>
    public double MinTreeHeight { get; init; } = 3;

    /// <summary>Gets whether roads and paths are found.</summary>
    public bool Roads { get; init; } = true;

    /// <summary>Gets where roads and paths come from.</summary>
    public RoadSource RoadsFrom { get; init; } = RoadSource.OpenStreetMap;

    /// <summary>
    /// Gets whether a map (OpenStreetMap) is read: buildings (everywhere, with their heights from a surface model where
    /// there is one), areas, single trees and tree rows, rocks, lines and landmarks; roads as <see cref="RoadsFrom"/> says.
    /// </summary>
    public bool Map { get; init; } = true;

    /// <summary>Gets whether the map's lines are read: waterways, power lines, aerialways, railways, fences and walls, cliffs.</summary>
    public bool Lines { get; init; } = true;

    /// <summary>Gets whether the map's landmarks are read: peaks, flying sites, masts, towers, crosses, huts, signposts ….</summary>
    public bool Landmarks { get; init; } = true;

    /// <summary>Gets whether buildings are found (where a surface model gives their height).</summary>
    public bool Buildings { get; init; } = true;

    /// <summary>Gets whether boulders are found.</summary>
    public bool Rocks { get; init; } = true;

    /// <summary>Gets whether the areas of forest, rock, water … are outlined.</summary>
    public bool Areas { get; init; } = true;

    /// <summary>Gets the cell size of the ground cover map (m; rounded to whole pixels, and coarser for large areas).</summary>
    public double CoverSpacing { get; init; } = 1;

    /// <summary>Gets whether a surface model is used where there is one (the heights of trees and buildings).</summary>
    public bool SurfaceModel { get; init; } = true;

    /// <summary>Gets the id of the imagery source asked first, or <c>null</c> for the finest that covers each point.</summary>
    public string? ImagerySource { get; init; }

    /// <summary>Gets the id of the elevation source asked first, or <c>null</c> for the finest that covers each point.</summary>
    public string? ElevationSource { get; init; }
}

/// <summary>How far a feature search is: reported after every block of the area, and in between while blocks are read.</summary>
/// <param name="BlocksDone">The blocks searched.</param>
/// <param name="BlockCount">The blocks of the area.</param>
/// <param name="BytesDownloaded">The bytes downloaded so far.</param>
public readonly record struct FeatureProgress(int BlocksDone, int BlockCount, long BytesDownloaded)
{
    /// <summary>Gets the fraction done (0–1).</summary>
    public double Fraction => BlockCount == 0 ? 1 : (double)BlocksDone / BlockCount;

    /// <summary>Gets the requests made so far.</summary>
    public int Requests { get; init; }

    /// <summary>Gets how long the search has run.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Gets the trees found so far.</summary>
    public int Trees { get; init; }

    /// <summary>Gets the buildings found so far.</summary>
    public int Buildings { get; init; }

    /// <summary>Gets the length of the road pieces found so far (m).</summary>
    public double RoadLength { get; init; }

    /// <summary>Gets what is being done, for people ("reading", "tracing the roads" …).</summary>
    public string Stage { get; init; } = "";
}
