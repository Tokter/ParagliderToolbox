using System.Globalization;

namespace ParagliderToolbox.Terrain;

/// <summary>A tile of a terrain: its level of detail (0 the finest) and its column (west to east) and row (north to south) in that level.</summary>
/// <param name="Level">The level of detail.</param>
/// <param name="X">The column.</param>
/// <param name="Y">The row.</param>
public readonly record struct TileKey(int Level, int X, int Y)
{
    /// <summary>Formats the key as "L0_3_4".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"L{Level}_{X}_{Y}");
}

/// <summary>Where a tile lies in the terrain's frame and how many samples it has.</summary>
/// <param name="X0">The x (east) of its west edge (m).</param>
/// <param name="Z0">The z (south) of its north edge (m).</param>
/// <param name="Width">Its east–west extent (m).</param>
/// <param name="Depth">Its north–south extent (m).</param>
/// <param name="Columns">The height samples across (on both edges).</param>
/// <param name="Rows">The height samples down.</param>
/// <param name="Spacing">The distance between samples (m).</param>
public readonly record struct TileRect(double X0, double Z0, double Width, double Depth, int Columns, int Rows, double Spacing)
{
    /// <summary>Gets the x of the center.</summary>
    public double CenterX => X0 + Width / 2;

    /// <summary>Gets the z of the center.</summary>
    public double CenterZ => Z0 + Depth / 2;
}

/// <summary>
/// How a terrain is split into tiles and levels of detail: a quadtree whose tiles all have the same samples per side
/// (except where the area ends), each level half as fine as the one below and with a quarter as many tiles.
/// </summary>
/// <remarks>
/// <para>
/// The finest level (0) has tiles of (samples − 1) × resolution meters; the area is rounded to whole tiles of it, so
/// the size is a little more or less than asked for. A tile of level L covers 2 × 2 tiles of level L − 1 (its
/// children); where the area ends, tiles are cut back to it (fewer samples, the same spacing), so every level covers
/// exactly the area. The coarsest level has a single tile unless the levels are limited.
/// </para>
/// <para>
/// With a detail size, a tile is split into its children only where they reach into the square of that level around
/// the center: the finest level covers the detail size, each coarser one twice as much, so a large area stays
/// affordable. A split tile always has all of its children (four, or fewer where the area ends), so a renderer can draw
/// either a tile or its children.
/// </para>
/// </remarks>
public sealed class TerrainLayout
{
    private readonly HashSet<TileKey> _tiles = [];
    private readonly List<TileKey> _ordered = [];

    /// <summary>Lays out <paramref name="settings"/>.</summary>
    public TerrainLayout(TerrainSettings settings)
    {
        Samples = TerrainSettings.NearestTileSamples(settings.TileSamples);
        Quads = Samples - 1;
        Spacing = Math.Clamp(settings.Resolution, 0.1, 1000);
        TileSize = Quads * Spacing;
        TilesAcross = (int)Math.Clamp(Math.Round(settings.Size / TileSize), 1, 4096);
        Extent = TilesAcross * TileSize;

        int maxLevels = (int)Math.Log2(Quads) + 1;
        int toOneTile = (int)Math.Ceiling(Math.Log2(TilesAcross)) + 1;
        LevelCount = Math.Clamp(settings.LodLevels > 0 ? settings.LodLevels : toOneTile, 1, Math.Min(maxLevels, toOneTile));
        DetailSize = settings.DetailSize > 0 && settings.DetailSize < Extent ? settings.DetailSize : 0;
        TextureSize = TerrainSettings.NearestTextureSize(settings.TextureSize);

        // From the roots down, splitting where the detail reaches.
        int top = LevelCount - 1;
        var level = new List<TileKey>();
        for (int y = 0; y < TilesAcrossAt(top); y++)
        {
            for (int x = 0; x < TilesAcrossAt(top); x++) level.Add(new TileKey(top, x, y));
        }
        Roots = level.ToArray();
        while (level.Count > 0)
        {
            foreach (var key in level)
            {
                _tiles.Add(key);
                _ordered.Add(key);
            }
            var next = new List<TileKey>();
            foreach (var key in level)
            {
                if (key.Level > 0 && ShouldSplit(key)) next.AddRange(ChildKeys(key));
            }
            level = next;
        }
        Tiles = _ordered;
    }

    /// <summary>Gets the height samples per side of a whole tile.</summary>
    public int Samples { get; }

    /// <summary>Gets the quads per side of a whole tile (samples − 1).</summary>
    public int Quads { get; }

    /// <summary>Gets the distance between the finest level's samples (m).</summary>
    public double Spacing { get; }

    /// <summary>Gets the side of a finest level tile (m).</summary>
    public double TileSize { get; }

    /// <summary>Gets the finest level's tiles per side.</summary>
    public int TilesAcross { get; }

    /// <summary>Gets the side of the terrain (m): it runs from −extent/2 to +extent/2 in x and z.</summary>
    public double Extent { get; }

    /// <summary>Gets the number of levels of detail.</summary>
    public int LevelCount { get; }

    /// <summary>Gets the side of the square with the finest level (m), or 0 when it covers everything.</summary>
    public double DetailSize { get; }

    /// <summary>Gets the texture pixels per side of a whole tile.</summary>
    public int TextureSize { get; }

    /// <summary>Gets the coarsest level's tiles.</summary>
    public IReadOnlyList<TileKey> Roots { get; }

    /// <summary>Gets every tile, coarse levels first, each level row by row.</summary>
    public IReadOnlyList<TileKey> Tiles { get; }

    /// <summary>Gets the distance between a level's samples (m).</summary>
    public double SpacingAt(int level) => Spacing * (1 << level);

    /// <summary>Gets the side of a whole tile of a level (m).</summary>
    public double TileSizeAt(int level) => TileSize * (1 << level);

    /// <summary>Gets a level's tiles per side (if it were complete).</summary>
    public int TilesAcrossAt(int level) => (TilesAcross + (1 << level) - 1) >> level;

    /// <summary>Gets a level's texture pixel size (m).</summary>
    public double TexelSizeAt(int level) => TileSizeAt(level) / TextureSize;

    /// <summary>Gets the depth of the skirts hanging from a level's tile edges (m), which hide the cracks between levels.</summary>
    public double SkirtDepthAt(int level) => Math.Max(2, 4 * SpacingAt(level));

    /// <summary>Gets whether the terrain has <paramref name="key"/>.</summary>
    public bool Contains(TileKey key) => _tiles.Contains(key);

    /// <summary>Gets whether <paramref name="key"/> is split into children.</summary>
    public bool IsSplit(TileKey key) => key.Level > 0 && _tiles.Contains(new TileKey(key.Level - 1, key.X * 2, key.Y * 2));

    /// <summary>Gets the children of <paramref name="key"/> (none when it isn't split): up to four, fewer where the area ends.</summary>
    public IEnumerable<TileKey> Children(TileKey key) => IsSplit(key) ? ChildKeys(key) : [];

    /// <summary>Gets the parent of <paramref name="key"/>, or <c>null</c> for a root.</summary>
    public TileKey? Parent(TileKey key) => key.Level + 1 < LevelCount ? new TileKey(key.Level + 1, key.X / 2, key.Y / 2) : null;

    /// <summary>Gets where <paramref name="key"/> lies and its samples.</summary>
    public TileRect Rect(TileKey key)
    {
        int span = 1 << key.Level;
        int x0 = key.X * span, x1 = Math.Min(x0 + span, TilesAcross);
        int y0 = key.Y * span, y1 = Math.Min(y0 + span, TilesAcross);
        double half = Extent / 2;
        return new TileRect(-half + x0 * TileSize, -half + y0 * TileSize, (x1 - x0) * TileSize, (y1 - y0) * TileSize,
            (x1 - x0) * Quads / span + 1, (y1 - y0) * Quads / span + 1, SpacingAt(key.Level));
    }

    /// <summary>Gets the texture size of a tile (pixels across and down; smaller for tiles cut back where the area ends).</summary>
    public (int Width, int Height) TextureSizeOf(TileKey key)
    {
        var rect = Rect(key);
        double texel = TexelSizeAt(key.Level);
        return (Math.Max(1, (int)Math.Round(rect.Width / texel)), Math.Max(1, (int)Math.Round(rect.Depth / texel)));
    }

    /// <summary>Gets the total number of height samples of all tiles.</summary>
    public long SampleCount => _ordered.Sum(k => (long)Rect(k).Columns * Rect(k).Rows);

    /// <summary>Gets the number of triangles of the most detailed tiles everywhere (the leaves), without skirts.</summary>
    public long LeafTriangleCount => _ordered.Where(k => !IsSplit(k)).Sum(k => 2L * (Rect(k).Columns - 1) * (Rect(k).Rows - 1));

    private IEnumerable<TileKey> ChildKeys(TileKey key)
    {
        int across = TilesAcrossAt(key.Level - 1);
        for (int dy = 0; dy < 2; dy++)
        {
            for (int dx = 0; dx < 2; dx++)
            {
                int x = key.X * 2 + dx, y = key.Y * 2 + dy;
                if (x < across && y < across) yield return new TileKey(key.Level - 1, x, y);
            }
        }
    }

    // Split where any child reaches into the child level's detail square (all of the area without a detail size).
    private bool ShouldSplit(TileKey key)
    {
        if (DetailSize <= 0) return true;
        double half = DetailSize / 2 * (1 << (key.Level - 1));
        var rect = Rect(key);
        return rect.X0 < half && rect.X0 + rect.Width > -half && rect.Z0 < half && rect.Z0 + rect.Depth > -half;
    }
}
