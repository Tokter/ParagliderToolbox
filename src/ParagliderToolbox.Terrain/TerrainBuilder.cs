using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;
using SkiaSharp;

namespace ParagliderToolbox.Terrain;

/// <summary>How far a terrain build is: reported when a tile is built, and every <see cref="TerrainBuilder.ProgressInterval"/> in between.</summary>
/// <param name="TilesDone">The tiles built.</param>
/// <param name="TileCount">The tiles to build.</param>
/// <param name="BytesDownloaded">The bytes downloaded so far.</param>
/// <param name="Tile">The tile just built, or <c>null</c> for a report in between.</param>
public readonly record struct TerrainProgress(int TilesDone, int TileCount, long BytesDownloaded, TerrainTile? Tile)
{
    /// <summary>Gets the fraction done (0–1).</summary>
    public double Fraction => TileCount == 0 ? 1 : (double)TilesDone / TileCount;

    /// <summary>Gets the requests made so far (each a file's part or a catalog page; cached data needs none).</summary>
    public int Requests { get; init; }

    /// <summary>Gets the level of detail being built (0 the finest).</summary>
    public int Level { get; init; }

    /// <summary>Gets the number of levels.</summary>
    public int LevelCount { get; init; }

    /// <summary>Gets the tiles of <see cref="Level"/> built.</summary>
    public int LevelTilesDone { get; init; }

    /// <summary>Gets the tiles of <see cref="Level"/>.</summary>
    public int LevelTileCount { get; init; }

    /// <summary>Gets the side of a whole tile of <see cref="Level"/> (m).</summary>
    public double LevelTileSize { get; init; }

    /// <summary>Gets how long the build has run.</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Builds a terrain: for every tile of the <see cref="TerrainLayout"/>, coarse levels first, the heights from the
/// elevation sources (each point from the best source that has it) and the texture from the imagery sources (elevation
/// colors where there is none).
/// </summary>
/// <remarks>
/// Each level is sampled from the sources at its own spacing (sources read their coarser overviews and average finer
/// data), so coarse levels are smooth rather than aliased. Tiles are built a few at a time, so downloads overlap.
/// Where a finer source's data ends, its heights ease into the next source's over <see cref="BlendDistance"/>, so there
/// is no step (Copernicus is a surface model: in forests it stands 15–25 m above swissALTI3D's bare ground). Points no
/// source has take their neighbors' height.
/// </remarks>
public sealed class TerrainBuilder(TerrainSources sources)
{
    /// <summary>Gets or sets the tiles built at once.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Gets or sets the JPEG quality of the textures (1–100).</summary>
    public int TextureQuality { get; init; } = 90;

    /// <summary>
    /// Gets or sets the distance (m) over which a finer elevation source eases into the next one where its data ends
    /// (at least three samples); 0 for a hard edge.
    /// </summary>
    public double BlendDistance { get; init; } = 200;

    /// <summary>
    /// Gets or sets how often progress is reported while tiles are still building (the first, coarsest tile of a large
    /// terrain reads a little of every file and can take a while with an empty cache).
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>Builds the terrain of <paramref name="settings"/>.</summary>
    /// <exception cref="HttpRequestException">Data couldn't be downloaded (and isn't cached).</exception>
    public async Task<TerrainModel> BuildAsync(TerrainSettings settings, IProgress<TerrainProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        long downloadedBefore = sources.Cache?.BytesDownloaded ?? 0;
        int requestsBefore = sources.Cache?.RequestCount ?? 0;
        var layout = new TerrainLayout(settings);
        var frame = new LocalFrame(settings.Center);
        var tiles = new ConcurrentDictionary<TileKey, TerrainTile>();
        var elevationCounts = new ConcurrentDictionary<string, long>();
        var imageryCounts = new ConcurrentDictionary<string, long>();
        long heightSamples = 0, missing = 0, texels = 0;
        int done = 0, levelDone = 0, levelCount = 0, currentLevel = layout.LevelCount - 1;

        TerrainProgress Snapshot(TerrainTile? tile) => new(Volatile.Read(ref done), layout.Tiles.Count,
            (sources.Cache?.BytesDownloaded ?? 0) - downloadedBefore, tile)
        {
            Requests = (sources.Cache?.RequestCount ?? 0) - requestsBefore,
            Level = Volatile.Read(ref currentLevel),
            LevelCount = layout.LevelCount,
            LevelTilesDone = Volatile.Read(ref levelDone),
            LevelTileCount = Volatile.Read(ref levelCount),
            LevelTileSize = layout.TileSizeAt(Volatile.Read(ref currentLevel)),
            Elapsed = clock.Elapsed,
        };

        // Reports in between, so a long tile doesn't look like a hang.
        using var heartbeat = progress is null ? null : new Timer(_ => progress.Report(Snapshot(null)), null, ProgressInterval, ProgressInterval);

        // Level by level from the coarsest, so a preview fills in from coarse to fine.
        foreach (var level in layout.Tiles.GroupBy(k => k.Level).OrderByDescending(g => g.Key))
        {
            Volatile.Write(ref levelDone, 0);
            Volatile.Write(ref levelCount, level.Count());
            Volatile.Write(ref currentLevel, level.Key);
            progress?.Report(Snapshot(null));
            await Parallel.ForEachAsync(level, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = cancellationToken },
                async (key, token) =>
                {
                    var (tile, counts) = await BuildTileAsync(settings, layout, frame, key, token);
                    tiles[key] = tile;
                    foreach (var (id, n) in counts.Elevation) elevationCounts.AddOrUpdate(id, n, (_, v) => v + n);
                    foreach (var (id, n) in counts.Imagery) imageryCounts.AddOrUpdate(id, n, (_, v) => v + n);
                    Interlocked.Add(ref heightSamples, counts.Samples);
                    Interlocked.Add(ref missing, counts.Missing);
                    Interlocked.Add(ref texels, counts.Texels);
                    Interlocked.Increment(ref done);
                    Interlocked.Increment(ref levelDone);
                    progress?.Report(Snapshot(tile));
                });
        }

        List<SourceShare> Shares(ConcurrentDictionary<string, long> counts, long total) => counts
            .Where(c => c.Value > 0 && sources.Find(c.Key) != null)
            .OrderByDescending(c => c.Value)
            .Select(c => new SourceShare(sources.Find(c.Key)!, total == 0 ? 0 : (double)c.Value / total))
            .ToList();

        return new TerrainModel(settings, layout, new Dictionary<TileKey, TerrainTile>(tiles),
            Shares(elevationCounts, heightSamples), Shares(imageryCounts, texels),
            heightSamples == 0 ? 0 : (double)missing / heightSamples, clock.Elapsed, (sources.Cache?.BytesDownloaded ?? 0) - downloadedBefore);
    }

    private sealed record TileCounts(Dictionary<string, long> Elevation, Dictionary<string, long> Imagery, long Samples, long Missing, long Texels);

    /// <summary>Builds one tile of <paramref name="layout"/>.</summary>
    public async Task<TerrainTile> BuildTileAsync(TerrainSettings settings, TerrainLayout layout, TileKey key, CancellationToken cancellationToken = default) =>
        (await BuildTileAsync(settings, layout, new LocalFrame(settings.Center), key, cancellationToken)).Tile;

    private async Task<(TerrainTile Tile, TileCounts Counts)> BuildTileAsync(TerrainSettings settings, TerrainLayout layout, LocalFrame frame,
        TileKey key, CancellationToken cancellationToken)
    {
        var rect = layout.Rect(key);
        var (textureWidth, textureHeight) = layout.TextureSizeOf(key);
        double texel = rect.Width / textureWidth;
        var colors = settings.Texture == TerrainTexture.None ? null : new uint[textureWidth * textureHeight];
        var imageryCounts = new Dictionary<string, long>();
        // The imagery downloads while the heights do (it doesn't depend on them).
        var imagery = colors != null && settings.Texture == TerrainTexture.Imagery
            ? FillImageryAsync(settings, frame, rect, texel, textureWidth, textureHeight, colors, imageryCounts, cancellationToken)
            : Task.CompletedTask;

        var heightsTask = FillHeightsAsync(settings, frame, rect, cancellationToken);
        await Task.WhenAll(heightsTask, imagery);
        var (heights, elevationCounts, missing) = heightsTask.Result;

        byte[]? texture = null;
        long texels = 0;
        if (colors != null)
        {
            texels = colors.Length;
            PaintElevationColors(colors, textureWidth, textureHeight, heights, rect);
            texture = EncodeJpeg(colors, textureWidth, textureHeight, TextureQuality);
        }

        var tile = new TerrainTile(key, rect, heights, texture, textureWidth, textureHeight);
        return (tile, new TileCounts(elevationCounts, imageryCounts, heights.Length, missing, texels));
    }

    // The tile's samples and a border of one, from the elevation sources in order, eased where one ends; holes filled.
    private async Task<(float[] Heights, Dictionary<string, long> Counts, int Missing)> FillHeightsAsync(TerrainSettings settings, LocalFrame frame,
        TileRect rect, CancellationToken cancellationToken)
    {
        double s = rect.Spacing;
        var request = new SampleRequest(frame, rect.X0 - s, rect.Z0 - s, s, rect.Columns + 2, rect.Rows + 2);
        var heights = new float[request.Count];
        Array.Fill(heights, float.NaN);
        var counts = new Dictionary<string, long>();
        var order = sources.ElevationFor(request.Bounds, settings.ElevationSource, request.Spacing);
        var filledBy = await FillInOrderAsync(order, request, heights, cancellationToken);
        for (int k = 0; k < order.Count; k++)
        {
            long n = filledBy.Count(f => f == k);
            if (n > 0) counts[order[k].Id] = n;
        }
        if (BlendDistance > 0 && order.Count > 1) await BlendSourceEdgesAsync(order, request, heights, filledBy, cancellationToken);
        int missing = FillHoles(heights, request.Columns, request.Rows);
        return (heights, counts, missing);
    }

    // Texel centers half a texel inside the tile's edges, from the imagery sources in order.
    private async Task FillImageryAsync(TerrainSettings settings, LocalFrame frame, TileRect rect, double texel, int width, int height,
        uint[] colors, Dictionary<string, long> counts, CancellationToken cancellationToken)
    {
        var request = new SampleRequest(frame, rect.X0 + texel / 2, rect.Z0 + texel / 2, texel, width, height);
        foreach (var source in sources.ImageryFor(request.Bounds, settings.ImagerySource, texel))
        {
            int filled = await source.FillAsync(request, colors, cancellationToken);
            if (filled > 0) counts[source.Id] = filled;
            if (!colors.Any(c => c >> 24 == 0)) break;
        }
    }

    // Fills the heights from the sources in order; returns which source filled each sample (−1: none).
    private static async Task<sbyte[]> FillInOrderAsync(IReadOnlyList<IElevationSource> order, SampleRequest request, float[] heights,
        CancellationToken cancellationToken)
    {
        var filledBy = new sbyte[heights.Length];
        Array.Fill(filledBy, (sbyte)-1);
        for (int k = 0; k < order.Count; k++)
        {
            if (await order[k].FillAsync(request, heights, cancellationToken) == 0) continue;
            for (int i = 0; i < heights.Length; i++)
            {
                if (filledBy[i] < 0 && !float.IsNaN(heights[i])) filledBy[i] = (sbyte)k;
            }
            if (!heights.Any(float.IsNaN)) break;
        }
        return filledBy;
    }

    // Where a finer source's data ends, its heights ease into the next source's over the blend distance (a surface model
    // stands meters above a terrain model in forests, which would be a step). Which source covers where comes from a
    // coarse lattice aligned to the terrain's frame and reaching the blend distance beyond the tile, so neighboring tiles
    // blend their shared edge the same way.
    private async Task BlendSourceEdgesAsync(IReadOnlyList<IElevationSource> order, SampleRequest request, float[] heights, sbyte[] filledBy,
        CancellationToken cancellationToken)
    {
        double blend = Math.Max(BlendDistance, 3 * request.Spacing);
        double step = Math.Max(blend / 8, request.Spacing);
        double x0 = Math.Floor((request.X0 - blend) / step) * step, z0 = Math.Floor((request.Z0 - blend) / step) * step;
        double x1 = request.X0 + (request.Columns - 1) * request.Spacing + blend, z1 = request.Z0 + (request.Rows - 1) * request.Spacing + blend;
        int columns = (int)Math.Ceiling((x1 - x0) / step) + 1, rows = (int)Math.Ceiling((z1 - z0) / step) + 1;
        var lattice = new SampleRequest(request.Frame, x0, z0, step, columns, rows);
        var latticeHeights = new float[lattice.Count];
        Array.Fill(latticeHeights, float.NaN);
        var latticeBy = await FillInOrderAsync(order, lattice, latticeHeights, cancellationToken);

        for (int k = 0; k < order.Count - 1; k++)
        {
            if (!filledBy.Any(f => f == k) || !latticeBy.Any(f => f > k)) continue;
            // The distance from every lattice point to the nearest point a coarser source covers.
            var distance = ChamferDistance(latticeBy, columns, rows, f => f > k, step);
            var weights = new float[heights.Length];
            var coarse = new float[heights.Length];
            bool any = false;
            for (int row = 0; row < request.Rows; row++)
            {
                for (int column = 0; column < request.Columns; column++)
                {
                    int i = row * request.Columns + column;
                    weights[i] = 1;
                    if (filledBy[i] != k) continue;
                    double fx = (request.X0 + column * request.Spacing - x0) / step, fz = (request.Z0 + row * request.Spacing - z0) / step;
                    double d = Bilinear(distance, columns, rows, fx, fz);
                    if (d >= blend) continue;
                    double t = Math.Clamp(d / blend, 0, 1);
                    weights[i] = (float)(t * t * (3 - 2 * t));
                    coarse[i] = float.NaN;
                    any = true;
                }
            }
            if (!any) continue;
            // The coarser sources' heights where the blend needs them.
            await FillInOrderAsync(order.Skip(k + 1).ToList(), request, coarse, cancellationToken);
            for (int i = 0; i < heights.Length; i++)
            {
                if (weights[i] >= 1 || float.IsNaN(coarse[i])) continue;
                heights[i] = coarse[i] + (heights[i] - coarse[i]) * weights[i];
            }
        }
    }

    // A two-pass chamfer distance transform (3-4 weights, within 8% of the Euclidean distance) to the lattice points
    // that match; infinity where none does.
    private static float[] ChamferDistance(sbyte[] values, int columns, int rows, Func<sbyte, bool> target, double step)
    {
        var d = new float[values.Length];
        for (int i = 0; i < d.Length; i++) d[i] = target(values[i]) ? 0 : float.PositiveInfinity;
        float straight = (float)step, diagonal = (float)(step * 4 / 3);
        void Relax(int i, int c, int r, float cost)
        {
            if (c < 0 || r < 0 || c >= columns || r >= rows) return;
            float candidate = d[r * columns + c] + cost;
            if (candidate < d[i]) d[i] = candidate;
        }
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c;
                Relax(i, c - 1, r, straight);
                Relax(i, c, r - 1, straight);
                Relax(i, c - 1, r - 1, diagonal);
                Relax(i, c + 1, r - 1, diagonal);
            }
        }
        for (int r = rows - 1; r >= 0; r--)
        {
            for (int c = columns - 1; c >= 0; c--)
            {
                int i = r * columns + c;
                Relax(i, c + 1, r, straight);
                Relax(i, c, r + 1, straight);
                Relax(i, c + 1, r + 1, diagonal);
                Relax(i, c - 1, r + 1, diagonal);
            }
        }
        return d;
    }

    private static double Bilinear(float[] grid, int columns, int rows, double fx, double fz)
    {
        int c = Math.Clamp((int)Math.Floor(fx), 0, columns - 2), r = Math.Clamp((int)Math.Floor(fz), 0, rows - 2);
        double tx = Math.Clamp(fx - c, 0, 1), tz = Math.Clamp(fz - r, 0, 1);
        double a = grid[r * columns + c], b = grid[r * columns + c + 1], e = grid[(r + 1) * columns + c], f = grid[(r + 1) * columns + c + 1];
        if (double.IsInfinity(a) || double.IsInfinity(b) || double.IsInfinity(e) || double.IsInfinity(f))
        {
            // Far from any edge: keep the nearest point's distance.
            return grid[(tz < 0.5 ? r : r + 1) * columns + (tx < 0.5 ? c : c + 1)];
        }
        return (a * (1 - tx) + b * tx) * (1 - tz) + (e * (1 - tx) + f * tx) * tz;
    }

    // Points no source had take the mean of their neighbors (spreading in from the edges of the hole); 0 if none has data.
    private static int FillHoles(float[] heights, int columns, int rows)
    {
        int missing = heights.Count(float.IsNaN);
        if (missing == 0) return 0;
        if (missing == heights.Length)
        {
            Array.Fill(heights, 0f);
            return missing;
        }
        var next = (float[])heights.Clone();
        for (int pass = 0; pass < columns + rows && next.Any(float.IsNaN); pass++)
        {
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int i = row * columns + column;
                    if (!float.IsNaN(heights[i])) continue;
                    float sum = 0;
                    int count = 0;
                    void Take(int c, int r)
                    {
                        if (c < 0 || r < 0 || c >= columns || r >= rows) return;
                        float h = heights[r * columns + c];
                        if (float.IsNaN(h)) return;
                        sum += h;
                        count++;
                    }
                    Take(column - 1, row);
                    Take(column + 1, row);
                    Take(column, row - 1);
                    Take(column, row + 1);
                    if (count > 0) next[i] = sum / count;
                }
            }
            Array.Copy(next, heights, heights.Length);
        }
        for (int i = 0; i < heights.Length; i++)
        {
            if (float.IsNaN(heights[i])) heights[i] = 0;
        }
        return missing;
    }

    // Texels without imagery get the elevation colors of their point (height and slope from the tile's samples).
    private static void PaintElevationColors(uint[] colors, int width, int height, float[] heights, TileRect rect)
    {
        int columns = rect.Columns + 2;
        double texelX = rect.Width / width, texelZ = rect.Depth / height;
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                int i = ty * width + tx;
                if (colors[i] >> 24 != 0) continue;
                // In samples of the bordered grid.
                double fc = ((tx + 0.5) * texelX) / rect.Spacing + 1, fr = ((ty + 0.5) * texelZ) / rect.Spacing + 1;
                int c = Math.Clamp((int)fc, 1, rect.Columns - 1), r = Math.Clamp((int)fr, 1, rect.Rows - 1);
                double u = Math.Clamp(fc - c, 0, 1), v = Math.Clamp(fr - r, 0, 1);
                float At(int cc, int rr) => heights[rr * columns + cc];
                float h = (float)((At(c, r) * (1 - u) + At(c + 1, r) * u) * (1 - v) + (At(c, r + 1) * (1 - u) + At(c + 1, r + 1) * u) * v);
                double dx = (At(c + 1, r) - At(c - 1, r) + At(c + 1, r + 1) - At(c - 1, r + 1)) / (4 * rect.Spacing);
                double dz = (At(c, r + 1) - At(c, r - 1) + At(c + 1, r + 1) - At(c + 1, r - 1)) / (4 * rect.Spacing);
                float slope = (float)(Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI);
                colors[i] = ElevationColors.Color(h, slope);
            }
        }
    }

    /// <summary>Encodes RGBA pixels (red in the lowest byte) as JPEG.</summary>
    public static byte[] EncodeJpeg(uint[] pixels, int width, int height, int quality)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), width * 4);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
