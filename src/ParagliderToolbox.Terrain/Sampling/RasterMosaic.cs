namespace ParagliderToolbox.Terrain.Sampling;

/// <summary>
/// Rasters side by side in one coordinate system (a source's files, or one map service level), sampled as one: a point
/// near a raster's edge is interpolated with the neighboring raster's pixels.
/// </summary>
public sealed class RasterMosaic<T>
{
    private readonly RasterLevel<T>[] _levels;
    private readonly Dictionary<(long, long), List<int>> _buckets = [];
    private readonly double _bucketSize;

    /// <summary>Initializes a mosaic of <paramref name="levels"/> (which shouldn't overlap much).</summary>
    public RasterMosaic(IEnumerable<RasterLevel<T>> levels)
    {
        _levels = levels.ToArray();
        _bucketSize = _levels.Length == 0 ? 1 : _levels.Max(l => Math.Max(l.MaxX - l.MinX, l.MaxY - l.MinY));
        for (int i = 0; i < _levels.Length; i++)
        {
            var level = _levels[i];
            for (long bx = Bucket(level.MinX); bx <= Bucket(level.MaxX); bx++)
            {
                for (long by = Bucket(level.MinY); by <= Bucket(level.MaxY); by++)
                {
                    if (!_buckets.TryGetValue((bx, by), out var list)) _buckets[(bx, by)] = list = [];
                    list.Add(i);
                }
            }
        }
    }

    /// <summary>Gets the rasters.</summary>
    public IReadOnlyList<RasterLevel<T>> Levels => _levels;

    private long Bucket(double v) => (long)Math.Floor(v / _bucketSize);

    /// <summary>Gets the index of the raster whose pixels cover (x, y), or −1; <paramref name="hint"/> is tried first (and updated).</summary>
    public int Find(double x, double y, ref int hint)
    {
        if (hint >= 0 && hint < _levels.Length && Contains(_levels[hint], x, y)) return hint;
        if (_buckets.TryGetValue((Bucket(x), Bucket(y)), out var list))
        {
            foreach (int i in list)
            {
                if (Contains(_levels[i], x, y)) return hint = i;
            }
        }
        return -1;
    }

    private static bool Contains(RasterLevel<T> level, double x, double y) =>
        x >= level.MinX && x < level.MaxX && y > level.MinY && y <= level.MaxY;

    /// <summary>
    /// Loads the blocks of every raster that cover the box (with a pixel around it, for interpolation), for sampling
    /// with the returned view.
    /// </summary>
    public async Task<MosaicView<T>> LoadAsync(double minX, double minY, double maxX, double maxY, BlockCache<T> cache, CancellationToken cancellationToken)
    {
        var regions = new MosaicView<T>.Region?[_levels.Length];
        var loads = new List<Task>();
        for (int i = 0; i < _levels.Length; i++)
        {
            var level = _levels[i];
            if (maxX < level.MinX - level.Dx || minX > level.MaxX + level.Dx || maxY < level.MinY - level.Dy || minY > level.MaxY + level.Dy) continue;
            int c0 = Math.Clamp((int)Math.Floor((minX - level.X0) / level.Dx) - 1, 0, level.Width - 1);
            int c1 = Math.Clamp((int)Math.Ceiling((maxX - level.X0) / level.Dx) + 1, 0, level.Width - 1);
            int r0 = Math.Clamp((int)Math.Floor((level.Y0 - maxY) / level.Dy) - 1, 0, level.Height - 1);
            int r1 = Math.Clamp((int)Math.Ceiling((level.Y0 - minY) / level.Dy) + 1, 0, level.Height - 1);
            int bx0 = c0 / level.BlockWidth, bx1 = c1 / level.BlockWidth, by0 = r0 / level.BlockHeight, by1 = r1 / level.BlockHeight;
            var region = new MosaicView<T>.Region(bx0, by0, bx1 - bx0 + 1, by1 - by0 + 1);
            regions[i] = region;
            for (int by = by0; by <= by1; by++)
            {
                for (int bx = bx0; bx <= bx1; bx++)
                {
                    int index = (by - by0) * region.Across + (bx - bx0);
                    int x = bx, y = by;
                    loads.Add(LoadInto(region, index, level, x, y));
                }
            }
        }
        await Task.WhenAll(loads);
        return new MosaicView<T>(this, regions);

        async Task LoadInto(MosaicView<T>.Region region, int index, RasterLevel<T> level, int x, int y) =>
            region.Blocks[index] = await cache.GetAsync(level, x, y, cancellationToken);
    }
}

/// <summary>The loaded blocks of a <see cref="RasterMosaic{T}"/> around a box, to read pixels from.</summary>
public sealed class MosaicView<T>
{
    internal sealed class Region(int firstX, int firstY, int across, int down)
    {
        public int FirstX { get; } = firstX;
        public int FirstY { get; } = firstY;
        public int Across { get; } = across;
        public int Down { get; } = down;
        public T[]?[] Blocks { get; } = new T[]?[across * down];
    }

    private readonly RasterMosaic<T> _mosaic;
    private readonly Region?[] _regions;

    internal MosaicView(RasterMosaic<T> mosaic, Region?[] regions)
    {
        _mosaic = mosaic;
        _regions = regions;
    }

    /// <summary>Gets the mosaic.</summary>
    public RasterMosaic<T> Mosaic => _mosaic;

    /// <summary>Gets a pixel of raster <paramref name="level"/>; <c>false</c> outside it, or where its block has no data or wasn't loaded.</summary>
    public bool TryGetPixel(int level, int column, int row, out T value)
    {
        var raster = _mosaic.Levels[level];
        value = default!;
        if (column < 0 || row < 0 || column >= raster.Width || row >= raster.Height || _regions[level] is not { } region) return false;
        int bx = column / raster.BlockWidth - region.FirstX, by = row / raster.BlockHeight - region.FirstY;
        if (bx < 0 || by < 0 || bx >= region.Across || by >= region.Down) return false;
        if (region.Blocks[by * region.Across + bx] is not { } block) return false;
        value = block[(row % raster.BlockHeight) * raster.BlockWidth + column % raster.BlockWidth];
        return true;
    }

    /// <summary>
    /// Gets the pixel at column, row of <paramref name="level"/>'s lattice, which may lie beyond it: then the pixel of
    /// the raster covering that point is taken.
    /// </summary>
    public bool TryGetLatticePixel(int level, int column, int row, out T value)
    {
        if (TryGetPixel(level, column, row, out value)) return true;
        var raster = _mosaic.Levels[level];
        if (column >= 0 && row >= 0 && column < raster.Width && row < raster.Height) return false;
        double x = raster.X0 + column * raster.Dx, y = raster.Y0 - row * raster.Dy;
        int hint = -1;
        int other = _mosaic.Find(x, y, ref hint);
        if (other < 0 || other == level) return false;
        var neighbor = _mosaic.Levels[other];
        int c = (int)Math.Round((x - neighbor.X0) / neighbor.Dx), r = (int)Math.Round((neighbor.Y0 - y) / neighbor.Dy);
        return TryGetPixel(other, c, r, out value);
    }
}

/// <summary>Bilinear sampling of mosaics: heights (no-data pixels left out) and colors (transparent pixels left out).</summary>
public static class MosaicSampling
{
    /// <summary>
    /// Gets the height at (x, y), interpolated between the four nearest pixels; <see cref="float.NaN"/> where the
    /// pixels with data carry less than half the weight.
    /// </summary>
    public static float Height(MosaicView<float> view, double x, double y, ref int hint)
    {
        int level = view.Mosaic.Find(x, y, ref hint);
        if (level < 0) return float.NaN;
        var raster = view.Mosaic.Levels[level];
        double fc = (x - raster.X0) / raster.Dx, fr = (raster.Y0 - y) / raster.Dy;
        int c0 = (int)Math.Floor(fc), r0 = (int)Math.Floor(fr);
        double tx = fc - c0, ty = fr - r0;
        double sum = 0, weights = 0;
        for (int b = 0; b < 2; b++)
        {
            for (int a = 0; a < 2; a++)
            {
                double w = (a == 0 ? 1 - tx : tx) * (b == 0 ? 1 - ty : ty);
                if (w <= 0) continue;
                if (view.TryGetLatticePixel(level, c0 + a, r0 + b, out float v) && !float.IsNaN(v))
                {
                    sum += w * v;
                    weights += w;
                }
            }
        }
        return weights >= 0.5 ? (float)(sum / weights) : float.NaN;
    }

    /// <summary>Gets the color at (x, y) (RGBA, red in the lowest byte), interpolated like <see cref="Height"/>; 0 where there is none.</summary>
    public static uint Color(MosaicView<uint> view, double x, double y, ref int hint)
    {
        int level = view.Mosaic.Find(x, y, ref hint);
        if (level < 0) return 0;
        var raster = view.Mosaic.Levels[level];
        double fc = (x - raster.X0) / raster.Dx, fr = (raster.Y0 - y) / raster.Dy;
        int c0 = (int)Math.Floor(fc), r0 = (int)Math.Floor(fr);
        double tx = fc - c0, ty = fr - r0;
        double r = 0, g = 0, bl = 0, weights = 0;
        for (int b = 0; b < 2; b++)
        {
            for (int a = 0; a < 2; a++)
            {
                double w = (a == 0 ? 1 - tx : tx) * (b == 0 ? 1 - ty : ty);
                if (w <= 0) continue;
                if (view.TryGetLatticePixel(level, c0 + a, r0 + b, out uint v) && v >> 24 != 0)
                {
                    r += w * (v & 0xFF);
                    g += w * ((v >> 8) & 0xFF);
                    bl += w * ((v >> 16) & 0xFF);
                    weights += w;
                }
            }
        }
        if (weights < 0.5) return 0;
        return Pack(r / weights, g / weights, bl / weights);
    }

    /// <summary>Packs an opaque color (channels 0–255) into RGBA.</summary>
    public static uint Pack(double r, double g, double b) =>
        (uint)Math.Clamp((int)(r + 0.5), 0, 255) | (uint)Math.Clamp((int)(g + 0.5), 0, 255) << 8 | (uint)Math.Clamp((int)(b + 0.5), 0, 255) << 16 | 0xFF000000u;
}
