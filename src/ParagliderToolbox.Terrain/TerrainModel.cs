using System.Runtime.InteropServices;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;
using SkiaSharp;

namespace ParagliderToolbox.Terrain;

/// <summary>
/// A built tile: its heights (with a border of one sample around it, from the neighbors, so normals match across
/// edges) and its texture.
/// </summary>
public sealed class TerrainTile
{
    /// <summary>Initializes a tile.</summary>
    public TerrainTile(TileKey key, TileRect rect, float[] heights, byte[]? texture, int textureWidth, int textureHeight)
    {
        if (heights.Length != (rect.Columns + 2) * (rect.Rows + 2)) throw new ArgumentException("The heights need a border of one sample.", nameof(heights));
        Key = key;
        Rect = rect;
        Heights = heights;
        Texture = texture;
        TextureWidth = textureWidth;
        TextureHeight = textureHeight;
        float min = float.MaxValue, max = float.MinValue;
        for (int row = 0; row < rect.Rows; row++)
        {
            for (int column = 0; column < rect.Columns; column++)
            {
                float h = Height(column, row);
                min = Math.Min(min, h);
                max = Math.Max(max, h);
            }
        }
        MinHeight = min;
        MaxHeight = max;
    }

    /// <summary>Gets the tile's key.</summary>
    public TileKey Key { get; }

    /// <summary>Gets where the tile lies.</summary>
    public TileRect Rect { get; }

    /// <summary>Gets the heights: (columns + 2) × (rows + 2), row by row from the north-west, with a border sample on every side.</summary>
    public float[] Heights { get; }

    /// <summary>Gets the lowest height inside the tile.</summary>
    public float MinHeight { get; }

    /// <summary>Gets the highest height inside the tile.</summary>
    public float MaxHeight { get; }

    /// <summary>Gets the texture as JPEG (north up; texel centers half a texel inside the edges), or <c>null</c>.</summary>
    public byte[]? Texture { get; }

    /// <summary>Gets the texture's width in pixels.</summary>
    public int TextureWidth { get; }

    /// <summary>Gets the texture's height in pixels.</summary>
    public int TextureHeight { get; }

    /// <summary>Gets the height of sample (column, row); −1 and columns/rows reach into the border.</summary>
    public float Height(int column, int row) => Heights[(row + 1) * (Rect.Columns + 2) + column + 1];

    /// <summary>Gets the heights inside the tile (without the border), row by row.</summary>
    public float[] InnerHeights()
    {
        var inner = new float[Rect.Columns * Rect.Rows];
        for (int row = 0; row < Rect.Rows; row++)
        {
            Array.Copy(Heights, (row + 1) * (Rect.Columns + 2) + 1, inner, row * Rect.Columns, Rect.Columns);
        }
        return inner;
    }

    /// <summary>Gets the height at local (x, z) inside the tile, interpolated between the samples.</summary>
    public float HeightAt(double x, double z)
    {
        double fc = Math.Clamp((x - Rect.X0) / Rect.Spacing, 0, Rect.Columns - 1);
        double fr = Math.Clamp((z - Rect.Z0) / Rect.Spacing, 0, Rect.Rows - 1);
        int c = Math.Min((int)fc, Rect.Columns - 2), r = Math.Min((int)fr, Rect.Rows - 2);
        c = Math.Max(c, 0);
        r = Math.Max(r, 0);
        double tx = fc - c, ty = fr - r;
        float a = Height(c, r), b = Height(Math.Min(c + 1, Rect.Columns - 1), r);
        float d = Height(c, Math.Min(r + 1, Rect.Rows - 1)), e = Height(Math.Min(c + 1, Rect.Columns - 1), Math.Min(r + 1, Rect.Rows - 1));
        return (float)((a * (1 - tx) + b * tx) * (1 - ty) + (d * (1 - tx) + e * tx) * ty);
    }

    /// <summary>Decodes the texture into RGBA pixels (red in the lowest byte), or <c>null</c> without one.</summary>
    public uint[]? DecodeTexture()
    {
        if (Texture is null) return null;
        using var bitmap = SKBitmap.Decode(Texture, new SKImageInfo(TextureWidth, TextureHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (bitmap is null) return null;
        var pixels = new uint[TextureWidth * TextureHeight];
        MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()).CopyTo(pixels);
        return pixels;
    }
}

/// <summary>A source's part in a terrain.</summary>
/// <param name="Source">The source.</param>
/// <param name="Share">The fraction of the samples it gave (0–1).</param>
public sealed record SourceShare(ITerrainSource Source, double Share);

/// <summary>A built terrain: its layout, frame, tiles and where the data came from.</summary>
public sealed class TerrainModel
{
    internal TerrainModel(TerrainSettings settings, TerrainLayout layout, IReadOnlyDictionary<TileKey, TerrainTile> tiles,
        IReadOnlyList<SourceShare> elevation, IReadOnlyList<SourceShare> imagery, double missingShare, TimeSpan elapsed, long downloaded)
    {
        Settings = settings;
        Layout = layout;
        Frame = new LocalFrame(settings.Center);
        Tiles = tiles;
        ElevationSources = elevation;
        ImagerySources = imagery;
        MissingShare = missingShare;
        Elapsed = elapsed;
        BytesDownloaded = downloaded;
        MinHeight = tiles.Count == 0 ? 0 : tiles.Values.Min(t => t.MinHeight);
        MaxHeight = tiles.Count == 0 ? 0 : tiles.Values.Max(t => t.MaxHeight);
    }

    /// <summary>Gets the settings it was built from.</summary>
    public TerrainSettings Settings { get; }

    /// <summary>Gets the tiles and levels.</summary>
    public TerrainLayout Layout { get; }

    /// <summary>Gets the frame (the origin is the settings' center).</summary>
    public LocalFrame Frame { get; }

    /// <summary>Gets the tiles.</summary>
    public IReadOnlyDictionary<TileKey, TerrainTile> Tiles { get; }

    /// <summary>Gets the lowest height of the terrain.</summary>
    public float MinHeight { get; }

    /// <summary>Gets the highest height of the terrain.</summary>
    public float MaxHeight { get; }

    /// <summary>Gets the elevation sources used, most used first.</summary>
    public IReadOnlyList<SourceShare> ElevationSources { get; }

    /// <summary>Gets the imagery sources used, most used first.</summary>
    public IReadOnlyList<SourceShare> ImagerySources { get; }

    /// <summary>Gets the fraction of height samples no source had (filled from their neighbors).</summary>
    public double MissingShare { get; }

    /// <summary>Gets how long the build took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Gets the bytes downloaded for the build.</summary>
    public long BytesDownloaded { get; }

    /// <summary>Gets the credits of the sources used, one per line.</summary>
    public string Attribution => string.Join("\n", ElevationSources.Concat(ImagerySources).Select(s => s.Source.Attribution).Distinct());

    /// <summary>Gets the height at local (x, z), from the finest tile there; <see cref="float.NaN"/> outside the terrain.</summary>
    public float HeightAt(double x, double z)
    {
        double half = Layout.Extent / 2;
        if (x < -half || x > half || z < -half || z > half) return float.NaN;
        TerrainTile? best = null;
        foreach (var root in Layout.Roots)
        {
            if (!Inside(root, x, z)) continue;
            TileKey? key = root;
            while (key is { } current)
            {
                if (Tiles.TryGetValue(current, out var tile)) best = tile;
                key = null;
                foreach (var child in Layout.Children(current))
                {
                    if (!Inside(child, x, z)) continue;
                    key = child;
                    break;
                }
            }
            break;
        }
        return best?.HeightAt(x, z) ?? float.NaN;
    }

    /// <summary>
    /// Samples the textures on a grid of points <paramref name="spacing"/> apart from (<paramref name="x0"/>,
    /// <paramref name="z0"/>), row by row: RGBA (red in the lowest byte), alpha 0 where there is no texture. Each point
    /// takes the tile at the coarsest level whose texture is as fine as the spacing (or the finest there is), so only
    /// the textures needed are decoded.
    /// </summary>
    public uint[] SampleColors(double x0, double z0, double spacing, int columns, int rows)
    {
        var colors = new uint[columns * rows];
        var decoded = new System.Collections.Concurrent.ConcurrentDictionary<TileKey, Lazy<uint[]?>>();
        double half = Layout.Extent / 2;
        Parallel.For(0, rows, row =>
        {
            for (int column = 0; column < columns; column++)
            {
                double x = x0 + column * spacing, z = z0 + row * spacing;
                if (x < -half || x > half || z < -half || z > half) continue;
                TerrainTile? best = null;
                foreach (var root in Layout.Roots)
                {
                    if (!Inside(root, x, z)) continue;
                    TileKey? key = root;
                    while (key is { } current)
                    {
                        if (Tiles.TryGetValue(current, out var tile) && tile.Texture != null) best = tile;
                        if (Layout.TexelSizeAt(current.Level) <= spacing) break;
                        key = null;
                        foreach (var child in Layout.Children(current))
                        {
                            if (!Inside(child, x, z)) continue;
                            key = child;
                            break;
                        }
                    }
                    break;
                }
                if (best is null) continue;
                var pixels = decoded.GetOrAdd(best.Key, _ => new Lazy<uint[]?>(best.DecodeTexture)).Value;
                if (pixels is null) continue;
                colors[row * columns + column] = SampleTexture(best, pixels, x, z);
            }
        });
        return colors;
    }

    // Bilinear in a tile's texture (pixel centers half a pixel inside the tile's edges).
    private static uint SampleTexture(TerrainTile tile, uint[] pixels, double x, double z)
    {
        var rect = tile.Rect;
        int width = tile.TextureWidth, height = tile.TextureHeight;
        double u = Math.Clamp((x - rect.X0) / rect.Width * width - 0.5, 0, width - 1), v = Math.Clamp((z - rect.Z0) / rect.Depth * height - 0.5, 0, height - 1);
        int a = Math.Min((int)u, width - 2), b = Math.Min((int)v, height - 2);
        a = Math.Max(a, 0);
        b = Math.Max(b, 0);
        double tu = u - a, tv = v - b;
        uint p00 = pixels[b * width + a], p10 = pixels[b * width + Math.Min(a + 1, width - 1)];
        uint p01 = pixels[Math.Min(b + 1, height - 1) * width + a], p11 = pixels[Math.Min(b + 1, height - 1) * width + Math.Min(a + 1, width - 1)];
        uint result = 0xFF000000u;
        for (int shift = 0; shift < 24; shift += 8)
        {
            double top = ((p00 >> shift) & 0xFF) * (1 - tu) + ((p10 >> shift) & 0xFF) * tu;
            double bottom = ((p01 >> shift) & 0xFF) * (1 - tu) + ((p11 >> shift) & 0xFF) * tu;
            result |= (uint)Math.Clamp((int)(top * (1 - tv) + bottom * tv + 0.5), 0, 255) << shift;
        }
        return result;
    }

    private bool Inside(TileKey key, double px, double pz)
    {
        var rect = Layout.Rect(key);
        return px >= rect.X0 && px <= rect.X0 + rect.Width && pz >= rect.Z0 && pz <= rect.Z0 + rect.Depth;
    }
}
