using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Terrain.Features;

/// <summary>
/// Finds features in an area of a terrain for a game to place its models by: trees and bushes, roads and paths as a
/// network, buildings, boulders, areas of forest, rock, gravel, water and snow, and a ground cover map. It reads the
/// aerial images, the terrain model and, where there is one, the surface model (the top of the trees and buildings) of
/// the terrain's sources, and classifies every pixel (<see cref="GroundCovers.Classify"/>).
/// </summary>
/// <remarks>
/// <para>
/// The area is searched in blocks of <see cref="BlockPixels"/> pixels with a <see cref="Margin"/> of the neighbors around
/// them, a few at a time: a feature belongs to the block its center (a tree's trunk, a building's middle) is in, and the
/// roads are cut at the blocks' edges and joined again across them.
/// </para>
/// <para>
/// With a surface model (swissSURFACE3D in Switzerland), trees are found in the canopy height model (surface less
/// terrain, <see cref="TreeFinder.Find"/>) with their measured heights, and buildings as smooth, tall, not green
/// objects with their heights and roof shape. Without one, crowns are found in the image alone
/// (<see cref="TreeFinder.FindInImage"/>, heights estimated) and buildings aren't found. Roads are traced in the
/// classified image (<see cref="RoadFinder"/>), areas in the ground cover map.
/// </para>
/// </remarks>
public sealed class FeatureDetector(TerrainSources sources)
{
    /// <summary>Gets or sets the blocks searched at once.</summary>
    public int Parallelism { get; init; } = 3;

    /// <summary>Gets or sets the pixels per side of a block's own part.</summary>
    public int BlockPixels { get; init; } = 1024;

    /// <summary>Gets or sets how far a block reads beyond its own part (m): the roads and buildings near its edges see their surroundings.</summary>
    public double Margin { get; init; } = 32;

    /// <summary>Gets or sets the most cells of the ground cover map (it gets coarser for large areas).</summary>
    public int MaxCoverCells { get; init; } = 4096 * 4096;

    /// <summary>Gets or sets the coarsest imagery features are looked for in (m): satellite images show neither trees nor paths.</summary>
    public double CoarsestImagery { get; init; } = 2;

    /// <summary>Gets or sets how often progress is reported while blocks are read.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>The smallest building (m² of footprint).</summary>
    public const double MinBuildingArea = 12;

    /// <summary>
    /// The most a found building's roof may bend (m): the median of |h(x ± 0.5 m) + h(z ± 0.5 m) − 4 h| over it. Measured
    /// in Interlaken: roofs (houses, row houses, greenhouses, garages) 0.03–0.11, dark red and purple trees (copper
    /// beeches, which aren't green and smooth enough to pass as roofs) 0.25–0.7.
    /// </summary>
    public const float MaxRoofCurvature = 0.2f;

    /// <summary>Finds the features of <paramref name="area"/> in the frame centered on <paramref name="origin"/>.</summary>
    /// <exception cref="HttpRequestException">Data couldn't be downloaded (and isn't cached).</exception>
    public async Task<FeatureSet> DetectAsync(GeoPoint origin, FeatureArea area, FeatureSettings settings, IProgress<FeatureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        long downloadedBefore = sources.Cache?.BytesDownloaded ?? 0;
        int requestsBefore = sources.Cache?.RequestCount ?? 0;
        var frame = new LocalFrame(origin);
        double spacing = Math.Clamp(settings.Spacing, 0.1, 2);

        // The cover map's cells are whole pixels, and blocks whole cells, so they line up.
        int cell = Math.Max(1, (int)Math.Round(settings.CoverSpacing / spacing));
        while (area.Width / (cell * spacing) * (area.Depth / (cell * spacing)) > MaxCoverCells) cell *= 2;
        double cellSize = cell * spacing;
        int columns = Math.Max(1, (int)Math.Round(area.Width / cellSize)), rows = Math.Max(1, (int)Math.Round(area.Depth / cellSize));
        var map = new CoverMap(area.X0, area.Z0, cellSize, columns, rows, new byte[columns * rows]);
        var heights = new float[columns * rows];
        Array.Fill(heights, float.NaN);

        int blockPixels = Math.Max(cell, BlockPixels / cell * cell);
        double blockSize = blockPixels * spacing, width = columns * cellSize, depth = rows * cellSize;
        int marginPixels = (int)Math.Ceiling(Margin / spacing);
        var blocks = new List<Block>();
        int across = (int)Math.Ceiling((width - 1e-6) / blockSize);
        for (int by = 0; by * blockSize < depth - 1e-6; by++)
        {
            for (int bx = 0; bx * blockSize < width - 1e-6; bx++)
            {
                double x0 = area.X0 + bx * blockSize, z0 = area.Z0 + by * blockSize;
                double x1 = Math.Min(x0 + blockSize, area.X0 + width), z1 = Math.Min(z0 + blockSize, area.Z0 + depth);
                int coreColumns = (int)Math.Round((x1 - x0) / spacing), coreRows = (int)Math.Round((z1 - z0) / spacing);
                blocks.Add(new Block(blocks.Count, x0, z0, x1, z1, x0 - marginPixels * spacing, z0 - marginPixels * spacing, spacing,
                    coreColumns + 2 * marginPixels, coreRows + 2 * marginPixels));
            }
        }

        var credits = new ConcurrentDictionary<string, ITerrainSource>();
        var results = new ConcurrentBag<BlockResult>();
        int done = 0, treeCount = 0, buildingCount = 0;
        double roadLength = 0;
        var roadLock = new object();
        string stage = "reading";
        FeatureProgress Snapshot() => new(Volatile.Read(ref done), blocks.Count, (sources.Cache?.BytesDownloaded ?? 0) - downloadedBefore)
        {
            Requests = (sources.Cache?.RequestCount ?? 0) - requestsBefore,
            Elapsed = clock.Elapsed,
            Trees = Volatile.Read(ref treeCount),
            Buildings = Volatile.Read(ref buildingCount),
            RoadLength = roadLength,
            Stage = stage,
        };
        using var heartbeat = progress is null ? null : new Timer(_ => progress.Report(Snapshot()), null, ProgressInterval, ProgressInterval);
        progress?.Report(Snapshot());

        // The map (OpenStreetMap): its roads, buildings, areas, lines, trees, rocks and landmarks. The blocks give their
        // points the ground's height (and roads the images' cover along them) and measure the buildings whose centers
        // they hold.
        var notes = new List<string>();
        var whole = new FeatureArea(area.X0, area.Z0, width, depth);
        MapContent? mapContent = null;
        string mapName = "";
        if (settings.Map)
        {
            stage = "reading OpenStreetMap";
            progress?.Report(Snapshot());
            var bounds = frame.Bounds(area.X0, area.Z0, area.X0 + width, area.Z0 + depth);
            var maps = sources.Maps.Where(m => m.Covers(bounds)).ToList();
            if (maps.Count == 0) notes.Add("No map covers the area: everything is found in the images.");
            foreach (var source in maps)
            {
                try
                {
                    var mapData = await source.DataAsync(bounds, cancellationToken);
                    credits.TryAdd(source.Id, source);
                    mapContent = MapFeatures.Build(mapData, frame, whole, settings.Roads && settings.RoadsFrom != RoadSource.Images);
                    mapName = source.Name;
                    break;
                }
                catch (HttpRequestException e)
                {
                    notes.Add($"{source.Name} couldn't be reached ({e.Message}): everything is found in the images.");
                }
            }
            stage = "reading";
        }
        bool imageRoads = settings.Roads && (settings.RoadsFrom != RoadSource.OpenStreetMap || mapContent is null);
        int blockRows = blocks.Count / across;
        int BlockOf(float x, float z) =>
            Math.Clamp((int)Math.Floor((z - area.Z0) / blockSize), 0, blockRows - 1) * across + Math.Clamp((int)Math.Floor((x - area.X0) / blockSize), 0, across - 1);
        var probes = new List<(List<Vector3> Points, int Index, int[]? Tally)>?[blocks.Count];
        var mapBuildings = new List<MapBuilding>?[blocks.Count];
        if (mapContent is not null)
        {
            foreach (var (points, tally) in mapContent.Probes)
            {
                for (int i = 0; i < points.Count; i++) (probes[BlockOf(points[i].X, points[i].Z)] ??= []).Add((points, i, tally));
            }
            foreach (var building in mapContent.Buildings)
            {
                var center = building.Center;
                (mapBuildings[BlockOf(center.X, center.Y)] ??= []).Add(building);
            }
        }

        // The blocks nearest the middle first, so a preview fills in from where one looks.
        double middleX = area.X0 + width / 2, middleZ = area.Z0 + depth / 2;
        var ordered = blocks.OrderBy(b => Math.Pow((b.X0 + b.X1) / 2 - middleX, 2) + Math.Pow((b.Z0 + b.Z1) / 2 - middleZ, 2)).ToList();
        await Parallel.ForEachAsync(ordered, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = cancellationToken },
            async (block, token) =>
            {
                var data = await ReadAsync(frame, block, settings, credits, token);
                token.ThrowIfCancellationRequested();
                var result = Analyze(block, data, settings, imageRoads, probes[block.Index], mapBuildings[block.Index], map, heights, cell, token);
                results.Add(result);
                Interlocked.Add(ref treeCount, result.Trees.Count);
                Interlocked.Add(ref buildingCount, result.Buildings.Count);
                lock (roadLock) roadLength += result.Roads.Sum(r => Length(r.Points));
                Interlocked.Increment(ref done);
                progress?.Report(Snapshot());
            });

        stage = "joining the roads and outlining the areas";
        progress?.Report(Snapshot());
        var all = results.OrderBy(r => r.Block).ToList();
        var (roads, junctions) = imageRoads ? Join(all.SelectMany(r => r.Roads).ToList(), Math.Max(2, 4 * spacing), spacing) : ([], []);
        var trees = settings.Trees ? all.SelectMany(r => r.Trees).ToList() : [];
        var buildings = settings.Buildings ? all.SelectMany(r => r.Buildings).ToList() : [];
        var rocks = settings.Rocks ? all.SelectMany(r => r.Rocks).ToList() : [];
        List<AreaFeature> mapAreas = [];
        List<LineFeature> lines = [];
        List<LandmarkFeature> landmarks = [];
        if (mapContent is not null)
        {
            if (mapContent.Roads.Count > 0 || !imageRoads)
            {
                var (mapped, crossings) = Finish(mapContent.Roads);
                // Both: the images' roads where the map has none near (unmapped tracks and trails), as a network of their own.
                var unmapped = settings.RoadsFrom == RoadSource.Both ? Unmapped(roads, mapped) : [];
                (roads, junctions) = Combine(mapped, crossings, unmapped, junctions);
            }
            var mapMask = new MapMask(mapContent);
            // The map's buildings (measured where a surface model is), and those the surface model found that it lacks.
            if (settings.Buildings) buildings = Finish(mapContent.Buildings).Concat(buildings.Where(b => Unmapped(b, mapMask))).ToList();
            // What the images took for crowns on the map's roofs, or for boulders on its roofs, roads and parking (cars,
            // roof parts: without a surface model the images can't tell), isn't.
            if (settings.Trees) trees = Merge(trees.Where(t => !mapMask.InBuilding(new(t.Position.X, t.Position.Z), 0)).ToList(), mapContent.Trees, mapContent.Areas);
            if (settings.Rocks)
            {
                rocks = Merge(rocks.Where(r => new Vector2(r.Position.X, r.Position.Z) is var p && !mapMask.InBuilding(p, 2) && !mapMask.OnPaved(p) && !mapMask.OnRoad(p, 2))
                    .ToList(), mapContent.Rocks);
            }
            if (settings.Areas) mapAreas = mapContent.Areas;
            if (settings.Lines) lines = mapContent.Lines.Select(Finish).ToList();
            if (settings.Landmarks) landmarks = mapContent.Landmarks.Select(FinishLandmark).ToList();
            notes.Add($"From {mapName}: {roads.Count(r => r.Origin == FeatureOrigin.Map)} roads, {buildings.Count(b => b.Origin == FeatureOrigin.Map)} buildings, " +
                $"{mapAreas.Count} areas, {lines.Count} lines, {landmarks.Count} landmarks and {trees.Count(t => t.Origin == FeatureOrigin.Map)} trees: " +
                "© OpenStreetMap contributors, under the Open Database License (data derived from it stays under it when shared).");
        }
        cancellationToken.ThrowIfCancellationRequested();
        ResolveShadows(map);
        if (mapContent is not null) Burn(map, mapContent, roads, buildings);
        var areas = settings.Areas ? mapAreas.Concat(Outline(map, heights, mapAreas)).ToList() : [];

        long pixels = all.Sum(r => r.Pixels), surfaced = all.Sum(r => r.SurfacePixels);
        int noImage = all.Count(r => r.NoImagery), coarse = all.Count(r => r.CoarseImagery);
        if (noImage > 0) notes.Add($"{noImage} of {blocks.Count} blocks have no aerial images: nothing is found there.");
        if (coarse > 0) notes.Add($"{coarse} of {blocks.Count} blocks have only satellite images coarser than {CoarsestImagery:0.#} m: only their ground cover is mapped there.");
        if (settings.SurfaceModel && pixels > 0 && surfaced < pixels * 0.99)
        {
            string buildingNote = mapContent is null ? "buildings aren't found" : "only the map's buildings are found, their heights from its tags or their kind";
            notes.Add(surfaced == 0
                ? $"No surface model covers the area: the trees' heights are estimated from their crowns, and {buildingNote}."
                : $"A surface model covers {100.0 * surfaced / pixels:0}% of the area: elsewhere the trees' heights are estimated and {buildingNote}.");
        }

        return new FeatureSet
        {
            Origin = origin,
            X0 = area.X0,
            Z0 = area.Z0,
            Width = width,
            Depth = depth,
            Spacing = spacing,
            SurfaceShare = pixels == 0 ? 0 : (double)surfaced / pixels,
            Trees = trees,
            Roads = roads,
            Junctions = junctions,
            Buildings = buildings,
            Rocks = rocks,
            Areas = areas,
            Lines = lines,
            Landmarks = landmarks,
            Covers = map,
            Attribution = string.Join("\n", credits.Values.OrderBy(s => s.Id).Select(s => s.Attribution).Distinct()),
            Notes = notes,
            Elapsed = clock.Elapsed,
        };
    }

    /// <summary>A block: its own part (x0…x1, z0…z1) and the pixels read (with the margin), from (<see cref="ReadX0"/>, <see cref="ReadZ0"/>).</summary>
    private sealed record Block(int Index, double X0, double Z0, double X1, double Z1, double ReadX0, double ReadZ0, double Spacing, int Columns, int Rows)
    {
        public bool Owns(double x, double z) => x >= X0 && x < X1 && z >= Z0 && z < Z1;
    }

    private sealed record BlockData(uint[] Colors, float[] Ground, float[]? Surface, bool NoImagery, bool CoarseImagery, int SurfacePixels);

    private sealed record RoadPiece(List<Vector3> Points, float Width, RoadSurface Surface, int Block, long Start, long End);

    private sealed record BlockResult(int Block, List<TreeFeature> Trees, List<RoadPiece> Roads, List<BuildingFeature> Buildings, List<RockFeature> Rocks,
        long Pixels, long SurfacePixels, bool NoImagery, bool CoarseImagery);

    // The block's images, terrain and surface heights, each point from the best source that has it.
    private async Task<BlockData> ReadAsync(LocalFrame frame, Block block, FeatureSettings settings, ConcurrentDictionary<string, ITerrainSource> credits,
        CancellationToken cancellationToken)
    {
        double spacing = block.Spacing;
        var request = new SampleRequest(frame, block.ReadX0 + spacing / 2, block.ReadZ0 + spacing / 2, spacing, block.Columns, block.Rows);
        int count = request.Count;

        var colors = new uint[count];
        int filled = 0, coarse = 0;
        foreach (var source in sources.ImageryFor(request.Bounds, settings.ImagerySource, spacing))
        {
            int n = await source.FillAsync(request, colors, cancellationToken);
            if (n == 0) continue;
            credits.TryAdd(source.Id, source);
            filled += n;
            if (source.Resolution > CoarsestImagery) coarse += n;
            if (filled >= count) break;
        }

        var ground = new float[count];
        Array.Fill(ground, float.NaN);
        int heights = 0;
        foreach (var source in sources.ElevationFor(request.Bounds, settings.ElevationSource, spacing))
        {
            int n = await source.FillAsync(request, ground, cancellationToken);
            if (n == 0) continue;
            credits.TryAdd(source.Id, source);
            heights += n;
            if (heights >= count) break;
        }
        // Points without heights take the mean of the others (the slope there is 0).
        if (heights < count)
        {
            double sum = 0;
            int valid = 0;
            foreach (float h in ground)
            {
                if (float.IsNaN(h)) continue;
                sum += h;
                valid++;
            }
            float mean = valid > 0 ? (float)(sum / valid) : 0;
            for (int i = 0; i < count; i++) if (float.IsNaN(ground[i])) ground[i] = mean;
        }

        float[]? surface = null;
        int surfaced = 0;
        if (settings.SurfaceModel)
        {
            var top = new float[count];
            Array.Fill(top, float.NaN);
            foreach (var source in sources.SurfaceFor(request.Bounds, spacing))
            {
                int n = await source.FillAsync(request, top, cancellationToken);
                if (n == 0) continue;
                credits.TryAdd(source.Id, source);
                surfaced += n;
                if (surfaced >= count) break;
            }
            if (surfaced > 0) surface = top;
        }
        return new BlockData(colors, ground, surface, filled < count / 2, coarse > count / 2, surfaced);
    }

    private BlockResult Analyze(Block block, BlockData data, FeatureSettings settings, bool imageRoads, List<(List<Vector3> Points, int Index, int[]? Tally)>? probes,
        List<MapBuilding>? mapBuildings, CoverMap map, float[] mapHeights, int cell, CancellationToken cancellationToken)
    {
        int w = block.Columns, h = block.Rows, n = w * h;
        double s = block.Spacing;
        var luminance = Rasters.Luminance(data.Colors);
        var texture = Rasters.LocalDeviation(luminance, w, h, Math.Max(1, (int)Math.Round(0.6 / s)));
        var slope = Rasters.Slope(data.Ground, w, h, s);
        float[]? objectHeight = null, roughness = null;
        if (data.Surface is { } surface)
        {
            objectHeight = new float[n];
            var known = new float[n];
            for (int i = 0; i < n; i++)
            {
                objectHeight[i] = surface[i] - data.Ground[i];
                known[i] = float.IsNaN(objectHeight[i]) ? 0 : objectHeight[i];
            }
            roughness = Rasters.LocalDeviation(known, w, h, Math.Max(1, (int)Math.Round(0.75 / s)));
        }
        var covers = new GroundCover[n];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                covers[i] = GroundCovers.Classify(new CoverSample(data.Colors[i], texture[i], slope[i], data.Ground[i],
                    objectHeight?[i] ?? float.NaN, roughness?[i] ?? float.NaN));
            }
        });
        // The buildings' parts (smooth, tall, not green), thinner bits taken off, grown out to their walls: the roof's
        // edges are as rough as crowns (the step down to the ground), but they are tall and not green.
        bool[]? built = null;
        if (objectHeight is not null)
        {
            built = new bool[n];
            for (int i = 0; i < n; i++) built[i] = covers[i] == GroundCover.Building;
            float open = Math.Max(1, (float)(0.5 / s));
            built = Skeletons.Dilate(Skeletons.Erode(built, w, h, open), w, h, open);
            var grown = Skeletons.Dilate(built, w, h, (float)Math.Ceiling(1 / s));
            for (int i = 0; i < n; i++)
            {
                if (!grown[i] || built[i] || !(objectHeight[i] > GroundCovers.TallObject) || GroundCovers.IsGreen(data.Colors[i])) continue;
                built[i] = true;
                covers[i] = GroundCover.Building;
            }
        }
        WriteCovers(block, covers, data.Ground, map, mapHeights, cell);

        // The block's own pixels, and how many of them a surface model measures.
        long pixels = 0, surfacePixels = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!block.Owns(block.ReadX0 + (x + 0.5) * s, block.ReadZ0 + (y + 0.5) * s)) continue;
                pixels++;
                if (objectHeight is not null && !float.IsNaN(objectHeight[y * w + x])) surfacePixels++;
            }
        }
        var result = new BlockResult(block.Index, [], [], [], [], pixels, surfacePixels, data.NoImagery, data.CoarseImagery);
        // The map's points here: the ground's height (and for roads what the images see under them).
        foreach (var (points, index, tally) in probes ?? [])
        {
            var p = points[index];
            double u = (p.X - block.ReadX0) / s, v = (p.Z - block.ReadZ0) / s;
            points[index] = p with { Y = GroundAt(data.Ground, w, h, u, v) };
            if (tally is null || data.NoImagery || data.CoarseImagery) continue;
            int px = Math.Clamp((int)u, 0, w - 1), py = Math.Clamp((int)v, 0, h - 1);
            Interlocked.Increment(ref tally[(int)covers[py * w + px]]);
        }
        // The map's buildings whose centers are here: the lowest ground under them, and their measured heights.
        foreach (var building in mapBuildings ?? [])
        {
            Vector2 Pixel(Vector2 p) => new((float)((p.X - block.ReadX0) / s), (float)((p.Y - block.ReadZ0) / s));
            var rings = new List<IReadOnlyList<Vector2>> { building.Footprint.Select(Pixel).ToList() };
            rings.AddRange(building.Holes.Select(hole => (IReadOnlyList<Vector2>)hole.Select(Pixel).ToList()));
            float ground = float.MaxValue;
            int inside = 0;
            var tops = new List<float>();
            Polygons.Scan(rings, w, h, (x, y) =>
            {
                int i = y * w + x;
                inside++;
                ground = Math.Min(ground, data.Ground[i]);
                if (objectHeight is not null && !float.IsNaN(objectHeight[i])) tops.Add(objectHeight[i]);
            });
            var center = Pixel(building.Center);
            building.Ground = inside > 0 ? ground : GroundAt(data.Ground, w, h, center.X, center.Y);
            if (tops.Count < Math.Max(3, 0.3 * inside)) continue;
            tops.Sort();
            building.Top = tops[(int)(tops.Count * 0.95)];
            building.Eaves = tops[(int)(tops.Count * 0.15)];
        }
        if (data.NoImagery || data.CoarseImagery) return result;

        Vector3 At(double u, double v)
        {
            double x = block.ReadX0 + u * s, z = block.ReadZ0 + v * s;
            return new Vector3((float)x, GroundAt(data.Ground, w, h, u, v), (float)z);
        }

        // Buildings first: a roof's edges are rough like crowns, so no trees within two meters of one.
        bool[]? buildingMask = null;
        if (objectHeight is not null && built is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buildingMask = FindBuildings(block, data, built, objectHeight, settings.Buildings ? result.Buildings : []);
        }
        if (settings.Trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nearBuilding = buildingMask is null ? null : Skeletons.Dilate(buildingMask, w, h, (float)(2 / s));
            FindTrees(block, data, settings, covers, texture, objectHeight, nearBuilding, At, result.Trees);
        }
        if (imageRoads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var traces = RoadFinder.Find(covers, slope, texture, w, h, s);
            foreach (var trace in traces.Roads) Clip(block, trace, At, result.Roads);
        }
        if (settings.Rocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindRocks(block, data, covers, objectHeight, result.Rocks);
        }
        return result;
    }

    // The ground's height at image coordinates (u, v), between the pixels' centers.
    private static float GroundAt(float[] ground, int w, int h, double u, double v)
    {
        double fx = Math.Clamp(u - 0.5, 0, w - 1), fy = Math.Clamp(v - 0.5, 0, h - 1);
        int x = Math.Min((int)fx, w - 2), y = Math.Min((int)fy, h - 2);
        x = Math.Max(x, 0);
        y = Math.Max(y, 0);
        double tx = fx - x, ty = fy - y;
        int x1 = Math.Min(x + 1, w - 1), y1 = Math.Min(y + 1, h - 1);
        double top = ground[y * w + x] * (1 - tx) + ground[y * w + x1] * tx;
        double bottom = ground[y1 * w + x] * (1 - tx) + ground[y1 * w + x1] * tx;
        return (float)(top * (1 - ty) + bottom * ty);
    }

    // The block's cells of the cover map: the most common cover of their pixels, and their mean ground height.
    private static void WriteCovers(Block block, GroundCover[] covers, float[] ground, CoverMap map, float[] mapHeights, int cell)
    {
        int w = block.Columns;
        double s = block.Spacing;
        int c0 = (int)Math.Round((block.X0 - map.X0) / map.Spacing), r0 = (int)Math.Round((block.Z0 - map.Z0) / map.Spacing);
        int c1 = Math.Min(map.Columns, (int)Math.Round((block.X1 - map.X0) / map.Spacing)), r1 = Math.Min(map.Rows, (int)Math.Round((block.Z1 - map.Z0) / map.Spacing));
        Parallel.For(r0, r1, row =>
        {
            Span<int> tally = stackalloc int[16];
            int y0 = (int)Math.Round((map.Z0 + row * map.Spacing - block.ReadZ0) / s);
            for (int column = c0; column < c1; column++)
            {
                int x0 = (int)Math.Round((map.X0 + column * map.Spacing - block.ReadX0) / s);
                tally.Clear();
                double sum = 0;
                for (int dy = 0; dy < cell; dy++)
                {
                    for (int dx = 0; dx < cell; dx++)
                    {
                        int i = (y0 + dy) * w + x0 + dx;
                        tally[(int)covers[i]]++;
                        sum += ground[i];
                    }
                }
                // No data only where nothing else is.
                int best = 0;
                for (int k = 1; k < tally.Length; k++) if (tally[k] > tally[best] || best == 0 && tally[k] > 0) best = k;
                map.Cells[row * map.Columns + column] = (byte)best;
                mapHeights[row * map.Columns + column] = (float)(sum / (cell * cell));
            }
        });
    }

    private static void FindTrees(Block block, BlockData data, FeatureSettings settings, GroundCover[] covers, float[] texture, float[]? objectHeight,
        bool[]? nearBuilding, Func<double, double, Vector3> at, List<TreeFeature> trees)
    {
        int w = block.Columns, h = block.Rows, n = w * h;
        double s = block.Spacing;
        float minTree = (float)settings.MinTreeHeight;
        int unmeasured = n;
        if (objectHeight is not null)
        {
            // The canopy height model: the vegetation's height above the ground.
            var canopy = new float[n];
            unmeasured = 0;
            for (int i = 0; i < n; i++)
            {
                if (float.IsNaN(objectHeight[i])) unmeasured++;
                else if (covers[i] is GroundCover.Tree or GroundCover.Shrub && nearBuilding?[i] != true) canopy[i] = Math.Max(0, objectHeight[i]);
            }
            foreach (var crown in TreeFinder.Find(canopy, w, h, s, settings.Bushes ? 1.2f : minTree))
            {
                var position = at(crown.X + 0.5, crown.Y + 0.5);
                if (!block.Owns(position.X, position.Z)) continue;
                var kind = crown.Height < minTree ? TreeKind.Bush : crown.Peakedness > 0.75f ? TreeKind.Conifer : TreeKind.Broadleaf;
                trees.Add(new TreeFeature(position, crown.Height, crown.CrownRadius, kind, Estimated: false));
            }
        }
        if (unmeasured < n / 100) return;
        // Where no surface model measures them: the crowns in the image.
        foreach (var crown in TreeFinder.FindInImage(data.Colors, covers, texture, w, h, s))
        {
            if (objectHeight is not null && !float.IsNaN(objectHeight[crown.Y * w + crown.X])) continue;
            var position = at(crown.X + 0.5, crown.Y + 0.5);
            if (!block.Owns(position.X, position.Z)) continue;
            bool bush = crown.CrownRadius < 1.5f;
            if (bush && !settings.Bushes) continue;
            trees.Add(new TreeFeature(position, bush ? Math.Min(crown.Height, minTree) : Math.Max(crown.Height, minTree), crown.CrownRadius,
                bush ? TreeKind.Bush : TreeKind.Unknown, Estimated: true));
        }
    }

    // A road's pieces inside the block's own part: cut where it leaves it, the cut ends joined to the neighbors' later.
    private static void Clip(Block block, RoadTrace trace, Func<double, double, Vector3> at, List<RoadPiece> pieces)
    {
        double s = block.Spacing;
        var points = trace.Points.Select(p => new Vector2((float)(block.ReadX0 + p.X * s), (float)(block.ReadZ0 + p.Y * s))).ToList();
        long node(int id) => (long)block.Index << 32 | (uint)id;
        Vector3 Point(Vector2 p) => at((p.X - block.ReadX0) / s, (p.Y - block.ReadZ0) / s);

        List<Vector3>? current = null;
        long start = -1;
        for (int k = 0; k + 1 < points.Count; k++)
        {
            var a = points[k];
            var b = points[k + 1];
            if (!ClipSegment(block, a, b, out double t0, out double t1))
            {
                continue;
            }
            var enter = Vector2.Lerp(a, b, (float)t0);
            var leave = Vector2.Lerp(a, b, (float)t1);
            if (current is null)
            {
                current = [Point(enter)];
                start = k == 0 && t0 == 0 ? node(trace.Start) : -1;
            }
            current.Add(Point(leave));
            if (t1 < 1)
            {
                pieces.Add(new RoadPiece(current, trace.Width, trace.Surface, block.Index, start, -1));
                current = null;
            }
        }
        if (current is { Count: >= 2 }) pieces.Add(new RoadPiece(current, trace.Width, trace.Surface, block.Index, start, node(trace.End)));
    }

    // The part t0…t1 of segment a–b inside the block's own part (Liang–Barsky); false when none is.
    private static bool ClipSegment(Block block, Vector2 a, Vector2 b, out double t0, out double t1)
    {
        t0 = 0;
        t1 = 1;
        double dx = b.X - a.X, dz = b.Y - a.Y;
        Span<double> p = [-dx, dx, -dz, dz];
        Span<double> q = [a.X - block.X0, block.X1 - a.X, a.Y - block.Z0, block.Z1 - a.Y];
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0) return false;
                continue;
            }
            double r = q[i] / p[i];
            if (p[i] < 0) t0 = Math.Max(t0, r);
            else t1 = Math.Min(t1, r);
        }
        return t0 < t1;
    }

    // Buildings: the parts of smooth, tall, not green objects (thinner bits than a meter taken off: railings, a crown's
    // smooth patch), as their outline and the smallest rectangle around it; squared to the rectangle when they nearly fill it.
    // Returns the mask of every building's pixels (the block's own, and those of its neighbors in the margin).
    private static bool[] FindBuildings(Block block, BlockData data, bool[] built, float[] objectHeight, List<BuildingFeature> buildings)
    {
        int w = block.Columns, h = block.Rows;
        double s = block.Spacing;
        var mask = new bool[w * h];
        var (labels, sizes) = Skeletons.Components(built, w, h);
        int smallest = (int)Math.Ceiling(MinBuildingArea / (s * s));
        var parts = new Dictionary<int, List<int>>();
        for (int i = 0; i < labels.Length; i++)
        {
            int label = labels[i];
            if (label < 0 || sizes[label] < smallest) continue;
            if (!parts.TryGetValue(label, out var list)) parts[label] = list = [];
            list.Add(i);
        }

        int step = Math.Max(1, (int)Math.Round(0.5 / s));
        foreach (var (label, pixels) in parts)
        {
            // A roof is made of planes; a crown bends everywhere.
            var bends = new List<float>(pixels.Count);
            foreach (int i in pixels)
            {
                int x = i % w, y = i / w;
                if (x < step || y < step || x >= w - step || y >= h - step) continue;
                int[] around = [i - step, i + step, i - step * w, i + step * w];
                if (around.Any(j => labels[j] != label || float.IsNaN(objectHeight[j])) || float.IsNaN(objectHeight[i])) continue;
                bends.Add(Math.Abs(around.Sum(j => objectHeight[j]) - 4 * objectHeight[i]));
            }
            if (bends.Count > 0 && bends.Order().ElementAt(bends.Count / 2) > MaxRoofCurvature) continue;

            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            float ground = float.MaxValue;
            var tops = new List<float>(pixels.Count);
            foreach (int i in pixels)
            {
                int x = i % w, y = i / w;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                ground = Math.Min(ground, data.Ground[i]);
                if (!float.IsNaN(objectHeight[i])) tops.Add(objectHeight[i]);
            }
            if (tops.Count == 0) continue;
            // Its outline, from a mask of the part alone with a free pixel around it.
            int bw = maxX - minX + 3, bh = maxY - minY + 3;
            var own = new bool[bw * bh];
            foreach (int i in pixels) own[(i / w - minY + 1) * bw + i % w - minX + 1] = true;
            var traced = Polygons.Trace(own, bw, bh);
            if (traced.Count == 0) continue;
            var outline = traced.MaxBy(p => -Polygons.SignedArea(p.Outline))!.Outline;
            var footprint = Polygons.SimplifyRing(outline, Math.Max(0.7, 0.4 / s))
                .Select(p => new Vector2((float)(block.ReadX0 + (p.X + minX - 1) * s), (float)(block.ReadZ0 + (p.Y + minY - 1) * s)))
                .ToList();
            var rectangle = Polygons.MinimumRectangle(footprint);
            double area = pixels.Count * s * s, fill = area / Math.Max(1e-6, rectangle.Length * rectangle.Width);
            // Compact: walls and hedges are long and thin, a building fills most of its rectangle.
            if (rectangle.Width < 2.5f || fill < 0.45) continue;
            tops.Sort();
            float top = tops[(int)(tops.Count * 0.95)], eaves = tops[(int)(tops.Count * 0.1)], ninety = tops[(int)(tops.Count * 0.9)];
            if (top < GroundCovers.TallObject) continue;
            foreach (int i in pixels) mask[i] = true;
            if (!block.Owns(rectangle.Center.X, rectangle.Center.Y)) continue;
            if (fill > 0.85) footprint = Corners(rectangle);
            buildings.Add(new BuildingFeature(new Vector3(rectangle.Center.X, ground, rectangle.Center.Y), rectangle.Length, rectangle.Width, AngleOf(rectangle.Direction),
                top, Math.Min(eaves, top), ninety - eaves > 1.5f ? RoofShape.Pitched : RoofShape.Flat, footprint));
        }
        return mask;
    }

    // A rectangle's corners, counterclockwise on screen (x east, z south: seen from above).
    private static List<Vector2> Corners(OrientedRectangle rectangle)
    {
        var u = rectangle.Direction * rectangle.Length / 2;
        var v = new Vector2(-rectangle.Direction.Y, rectangle.Direction.X) * rectangle.Width / 2;
        var corners = new List<Vector2> { rectangle.Center - u - v, rectangle.Center + u - v, rectangle.Center + u + v, rectangle.Center - u + v };
        if (Polygons.SignedArea(corners) > 0) corners.Reverse();
        return corners;
    }

    // Boulders: small parts of rock with little rock and nothing built around them, not standing above the terrain model (cars do).
    private static void FindRocks(Block block, BlockData data, GroundCover[] covers, float[]? objectHeight, List<RockFeature> rocks)
    {
        int w = block.Columns, h = block.Rows;
        double s = block.Spacing;
        var mask = new bool[w * h];
        for (int i = 0; i < mask.Length; i++) mask[i] = covers[i] == GroundCover.Rock;
        var (labels, sizes) = Skeletons.Components(mask, w, h);
        int smallest = (int)Math.Ceiling(1.5 / (s * s)), largest = (int)(40 / (s * s));
        var parts = new Dictionary<int, List<int>>();
        for (int i = 0; i < labels.Length; i++)
        {
            int label = labels[i];
            if (label < 0 || sizes[label] < smallest || sizes[label] > largest) continue;
            if (!parts.TryGetValue(label, out var list)) parts[label] = list = [];
            list.Add(i);
        }
        if (parts.Count == 0) return;
        var smooth = Rasters.Blur(data.Ground, w, h, (float)(2 / s));

        foreach (var (label, pixels) in parts)
        {
            double cx = 0, cy = 0;
            float relief = 0;
            var standing = new List<float>();
            foreach (int i in pixels)
            {
                cx += i % w + 0.5;
                cy += i / w + 0.5;
                relief = Math.Max(relief, data.Ground[i] - smooth[i]);
                if (objectHeight is not null && !float.IsNaN(objectHeight[i])) standing.Add(objectHeight[i]);
            }
            cx /= pixels.Count;
            cy /= pixels.Count;
            double x = block.ReadX0 + cx * s, z = block.ReadZ0 + cy * s;
            if (!block.Owns(x, z)) continue;
            if (standing.Count > 0 && standing.Order().ElementAt(standing.Count / 2) > 1) continue;
            float radius = (float)Math.Sqrt(pixels.Count * s * s / Math.PI);
            // Around it (a box of 2.5 radii, at least 3 m): little other rock, no road or building, not in a forest (a gap
            // between crowns shows bare ground, not a boulder).
            int reach = (int)Math.Ceiling(Math.Max(3, 2.5 * radius) / s);
            int rock = 0, built = 0, trees = 0, around = 0;
            for (int y = Math.Max(0, (int)cy - reach); y < Math.Min(h, (int)cy + reach + 1); y++)
            {
                for (int xx = Math.Max(0, (int)cx - reach); xx < Math.Min(w, (int)cx + reach + 1); xx++)
                {
                    int i = y * w + xx;
                    if (labels[i] == label) continue;
                    around++;
                    if (covers[i] == GroundCover.Rock) rock++;
                    else if (covers[i] is GroundCover.Paved or GroundCover.Gravel or GroundCover.Building) built++;
                    else if (covers[i] == GroundCover.Tree) trees++;
                }
            }
            if (around == 0 || rock > 0.35 * around || built > 0.1 * around || trees > 0.5 * around) continue;
            float height = Math.Clamp(relief, 0.5f * radius, 2 * radius);
            rocks.Add(new RockFeature(new Vector3((float)x, GroundAt(data.Ground, w, h, cx, cy), (float)z), radius, height));
        }
    }

    private static double Length(IReadOnlyList<Vector3> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(new(points[i].X, points[i].Z), new(points[i - 1].X, points[i - 1].Z));
        return length;
    }

    // Points no block had (on the area's last edge) take their neighbor's height; on bridges and in tunnels the heights
    // run straight between the ends.
    private static void Settle(List<Vector3> points, RoadStructure structure)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < points.Count; i++)
            {
                int j = pass == 0 ? i : points.Count - 1 - i, k = pass == 0 ? j - 1 : j + 1;
                if (float.IsNaN(points[j].Y) && k >= 0 && k < points.Count && !float.IsNaN(points[k].Y)) points[j] = points[j] with { Y = points[k].Y };
            }
        }
        for (int i = 0; i < points.Count; i++) if (float.IsNaN(points[i].Y)) points[i] = points[i] with { Y = 0 };
        if (structure == RoadStructure.Ground) return;
        double total = Length(points), along = 0;
        float a = points[0].Y, b = points[^1].Y;
        for (int i = 1; i < points.Count; i++)
        {
            along += Vector2.Distance(new(points[i].X, points[i].Z), new(points[i - 1].X, points[i - 1].Z));
            points[i] = points[i] with { Y = (float)(a + (b - a) * along / Math.Max(total, 1e-6)) };
        }
    }

    // The long side's direction as an angle clockwise from north (−z), 0–180°.
    private static float AngleOf(Vector2 direction)
    {
        float angle = MathF.Atan2(direction.X, -direction.Y) * 180 / MathF.PI;
        return ((angle % 180) + 180) % 180;
    }

    // The map's buildings: their rectangles, and their heights measured, else from their tags or kind.
    private static IEnumerable<BuildingFeature> Finish(List<MapBuilding> mapBuildings)
    {
        foreach (var building in mapBuildings)
        {
            var tags = new Dictionary<string, string>(building.Tags) { ["building"] = building.Class };
            var (top, eaves, roof) = MapFeatures.BuildingHeights(tags);
            bool measured = building.Top > GroundCovers.TallObject;
            if (measured)
            {
                top = building.Top;
                eaves = Math.Clamp(building.Eaves, 0, top);
                if (!tags.ContainsKey("roof:shape")) roof = top - eaves > 1.5f ? RoofShape.Pitched : RoofShape.Flat;
            }
            var rectangle = Polygons.MinimumRectangle(building.Footprint);
            float ground = float.IsNaN(building.Ground) ? 0 : building.Ground;
            yield return new BuildingFeature(new Vector3(rectangle.Center.X, ground, rectangle.Center.Y), rectangle.Length, rectangle.Width,
                AngleOf(rectangle.Direction), top, eaves, roof, building.Footprint)
            {
                Origin = FeatureOrigin.Map,
                Measured = measured,
                Class = building.Class,
                Tags = building.Tags,
            };
        }
    }

    /// <summary>Where the map's buildings, parking, water, roads and railways are, to test points against.</summary>
    private sealed class MapMask
    {
        private const float Cell = 25;
        private readonly Dictionary<(int, int), List<IReadOnlyList<Vector2>>> _buildings = [], _paved = [], _water = [];
        private readonly Dictionary<(int, int), List<(Vector2 A, Vector2 B, float Half)>> _roads = [], _railways = [];

        public MapMask(MapContent content)
        {
            void Rings(Dictionary<(int, int), List<IReadOnlyList<Vector2>>> grid, IEnumerable<IReadOnlyList<Vector2>> rings)
            {
                foreach (var ring in rings)
                {
                    var (min, max) = Box(ring);
                    foreach (var key in Cells(min, max)) Add(grid, key, ring);
                }
            }
            void Lines(Dictionary<(int, int), List<(Vector2 A, Vector2 B, float Half)>> grid, IReadOnlyList<Vector3> points, float half)
            {
                for (int i = 1; i < points.Count; i++)
                {
                    var a = new Vector2(points[i - 1].X, points[i - 1].Z);
                    var b = new Vector2(points[i].X, points[i].Z);
                    foreach (var key in Cells(Vector2.Min(a, b) - new Vector2(2 * half + 5), Vector2.Max(a, b) + new Vector2(2 * half + 5))) Add(grid, key, (a, b, half));
                }
            }
            Rings(_buildings, content.Buildings.Select(b => b.Footprint));
            Rings(_paved, content.Areas.Where(a => a.Kind == AreaKind.Paved).Select(a => a.Outline));
            Rings(_water, content.Areas.Where(a => a.Kind == AreaKind.Water).Select(a => a.Outline));
            foreach (var road in content.Roads) Lines(_roads, road.Points, road.Width / 2);
            foreach (var railway in content.Lines.Where(l => l.Kind == LineKind.Railway)) Lines(_railways, railway.Points, 2.5f);
        }

        // Inside a building, or within `margin` of one.
        public bool InBuilding(Vector2 p, float margin) => Near(_buildings, p, margin);

        public bool OnPaved(Vector2 p) => Near(_paved, p, 0);

        public bool InWater(Vector2 p) => Near(_water, p, 0);

        // On a road or railway (within `margin` of its edges).
        public bool OnRoad(Vector2 p, float margin) => _roads.TryGetValue(Key(p), out var segments) && segments.Any(s => Distance(p, s.A, s.B) <= s.Half + margin);

        public bool OnRailway(Vector2 p) => _railways.TryGetValue(Key(p), out var segments) && segments.Any(s => Distance(p, s.A, s.B) <= s.Half);

        private static bool Near(Dictionary<(int, int), List<IReadOnlyList<Vector2>>> grid, Vector2 p, float margin)
        {
            foreach (var key in Cells(p - new Vector2(margin), p + new Vector2(margin)))
            {
                if (!grid.TryGetValue(key, out var rings)) continue;
                foreach (var ring in rings)
                {
                    if (Polygons.Contains(ring, p)) return true;
                    if (margin <= 0) continue;
                    for (int i = 0; i < ring.Count; i++)
                    {
                        if (Distance(p, ring[i], ring[(i + 1) % ring.Count]) <= margin) return true;
                    }
                }
            }
            return false;
        }

        private static (int, int) Key(Vector2 p) => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell));

        private static IEnumerable<(int, int)> Cells(Vector2 min, Vector2 max)
        {
            var (x0, z0) = Key(min);
            var (x1, z1) = Key(max);
            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++) yield return (x, z);
            }
        }

        private static void Add<T>(Dictionary<(int, int), List<T>> grid, (int, int) key, T item)
        {
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(item);
        }

        private static (Vector2 Min, Vector2 Max) Box(IReadOnlyList<Vector2> ring)
        {
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in ring)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            return (min, max);
        }

        private static float Distance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            return Vector2.Distance(p, a + ab * t);
        }
    }

    // Whether a building the surface model found is one the map lacks (sampled every half meter): not mostly inside a
    // mapped one (under 30%), not a sliver along one's outline (an offset outline, a roof's overhang: half within 1 m,
    // or nine tenths within 3 m), not a small piece by one (under 30 m², most within 3 m: parasols, bays), not a bridge
    // or a train (a third on a road, railway or water). Measured in Interlaken (2879 mapped): of 181 found before these
    // checks and MaxRoofCurvature, 87 were dark red and purple trees, 20 bridges and trains, 10 slivers and small pieces;
    // the 64 left are sheds, garages, carports, greenhouses, canopies, tents and a few unmapped houses.
    private static bool Unmapped(BuildingFeature building, MapMask map)
    {
        var footprint = building.Footprint;
        var min = new Vector2(footprint.Min(p => p.X), footprint.Min(p => p.Y));
        var max = new Vector2(footprint.Max(p => p.X), footprint.Max(p => p.Y));
        int samples = 0, inside = 0, within1 = 0, within3 = 0, crossing = 0;
        Polygons.Scan([footprint.Select(p => (p - min) * 2).ToList()], (int)Math.Ceiling((max.X - min.X) * 2) + 1, (int)Math.Ceiling((max.Y - min.Y) * 2) + 1, (x, y) =>
        {
            var p = min + new Vector2(x + 0.5f, y + 0.5f) / 2;
            samples++;
            if (map.InBuilding(p, 0)) inside++;
            if (map.InBuilding(p, 1)) within1++;
            if (map.InBuilding(p, 3)) within3++;
            if (map.OnRoad(p, 0) || map.OnRailway(p) || map.InWater(p)) crossing++;
        });
        if (samples == 0) return true;
        double area = Math.Abs(Polygons.SignedArea(footprint));
        return inside < 0.3 * samples && within1 < 0.5 * samples && within3 < 0.9 * samples && !(area < 30 && within3 >= 0.6 * samples) && crossing < 0.3 * samples;
    }

    // The trees found and the map's: the map's single trees and rows where none was found near, and the kind of leaves
    // of the map's forests for trees found without it.
    private static List<TreeFeature> Merge(List<TreeFeature> found, List<MapPoint> mapTrees, List<AreaFeature> mapAreas)
    {
        var trees = new List<TreeFeature>(found);
        var forests = mapAreas.Where(a => a.Kind == AreaKind.Forest && MapFeatures.KindOfLeaves(a.Tags?.GetValueOrDefault("leaf_type")) != TreeKind.Unknown).ToList();
        if (forests.Count > 0)
        {
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i].Kind != TreeKind.Unknown) continue;
                var p = new Vector2(trees[i].Position.X, trees[i].Position.Z);
                var forest = forests.FirstOrDefault(f => Polygons.Contains(f.Outline, p) && !f.Holes.Any(h => Polygons.Contains(h, p)));
                if (forest != null) trees[i] = trees[i] with { Kind = MapFeatures.KindOfLeaves(forest.Tags!["leaf_type"]) };
            }
        }
        var grid = Grid(trees.Select(t => t.Position), 10);
        foreach (var mapTree in mapTrees)
        {
            var position = mapTree.Point[0];
            var (height, crown, kind) = MapFeatures.TreeOf(mapTree.Tags);
            int nearest = Nearest(grid, trees.Select(t => t.Position).ToList(), position, Math.Max(2.5f, crown), 10);
            if (nearest >= 0)
            {
                if (trees[nearest].Kind == TreeKind.Unknown && kind != TreeKind.Unknown) trees[nearest] = trees[nearest] with { Kind = kind, Tags = mapTree.Tags };
                continue;
            }
            trees.Add(new TreeFeature(position, height ?? 10, crown, kind, Estimated: height is null) { Origin = FeatureOrigin.Map, Tags = mapTree.Tags });
        }
        return trees;
    }

    // The rocks found and the map's stones and rocks where none was found within 3 m.
    private static List<RockFeature> Merge(List<RockFeature> found, List<MapPoint> mapRocks)
    {
        var rocks = new List<RockFeature>(found);
        var grid = Grid(rocks.Select(r => r.Position), 10);
        foreach (var mapRock in mapRocks)
        {
            var position = mapRock.Point[0];
            if (Nearest(grid, found.Select(r => r.Position).ToList(), position, 3, 10) >= 0) continue;
            float size = mapRock.Class == "natural=stone" ? 2 : 1.5f;
            rocks.Add(new RockFeature(position, size, size) { Origin = FeatureOrigin.Map });
        }
        return rocks;
    }

    private static Dictionary<(int, int), List<int>> Grid(IEnumerable<Vector3> positions, float cell)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        int i = 0;
        foreach (var p in positions)
        {
            var key = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Z / cell));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(i++);
        }
        return grid;
    }

    // The nearest of the positions within `reach` of p (−1 for none), through a grid of `cell` meters.
    private static int Nearest(Dictionary<(int, int), List<int>> grid, List<Vector3> positions, Vector3 p, float reach, float cell)
    {
        int best = -1;
        float bestDistance = reach;
        int cx = (int)Math.Floor(p.X / cell), cz = (int)Math.Floor(p.Z / cell), span = (int)Math.Ceiling(reach / cell);
        for (int dz = -span; dz <= span; dz++)
        {
            for (int dx = -span; dx <= span; dx++)
            {
                if (!grid.TryGetValue((cx + dx, cz + dz), out var list)) continue;
                foreach (int i in list)
                {
                    float d = Vector2.Distance(new(positions[i].X, positions[i].Z), new(p.X, p.Z));
                    if (d > bestDistance) continue;
                    bestDistance = d;
                    best = i;
                }
            }
        }
        return best;
    }

    private static LineFeature Finish(MapLine line)
    {
        Settle(line.Points, line.Structure);
        return new LineFeature(line.Points, line.Kind, line.Width, line.Height) { Structure = line.Structure, Class = line.Class, Tags = line.Tags };
    }

    private static LandmarkFeature FinishLandmark(MapPoint point)
    {
        Settle(point.Point, RoadStructure.Ground);
        return new LandmarkFeature(point.Point[0], MapFeatures.LandmarkKindOf(point.Tags, out _) ?? LandmarkKind.FlyingSite)
        {
            Name = point.Tags.GetValueOrDefault("name"),
            Elevation = MapFeatures.Meters(point.Tags.GetValueOrDefault("ele")),
            Height = MapFeatures.Meters(point.Tags.GetValueOrDefault("height")),
            Class = point.Class,
            Tags = point.Tags,
        };
    }

    // The map's water, glaciers, buildings and roads painted into the cover map: the images can't tell a building
    // without a surface model, and a lake's or road's edge is cleaner on the map.
    private static void Burn(CoverMap map, MapContent content, List<RoadFeature> roads, List<BuildingFeature> buildings)
    {
        Vector2 Cell(Vector2 p) => new((float)((p.X - map.X0) / map.Spacing), (float)((p.Y - map.Z0) / map.Spacing));
        void Fill(IReadOnlyList<Vector2> outline, IEnumerable<IReadOnlyList<Vector2>> holes, GroundCover cover)
        {
            var rings = new List<IReadOnlyList<Vector2>> { outline.Select(Cell).ToList() };
            rings.AddRange(holes.Select(h => (IReadOnlyList<Vector2>)h.Select(Cell).ToList()));
            Polygons.Scan(rings, map.Columns, map.Rows, (x, y) => map.Cells[y * map.Columns + x] = (byte)cover);
        }
        foreach (var area in content.Areas)
        {
            if (area.Kind == AreaKind.Water) Fill(area.Outline, area.Holes, GroundCover.Water);
            else if (area.Class == "natural=glacier") Fill(area.Outline, area.Holes, GroundCover.Snow);
        }
        foreach (var road in roads)
        {
            if (road.Origin != FeatureOrigin.Map || road.Structure != RoadStructure.Ground) continue;
            var cover = road.Surface switch { RoadSurface.Paved => GroundCover.Paved, RoadSurface.Gravel => GroundCover.Gravel, _ => GroundCover.Soil };
            Polygons.Stroke(road.Points.Select(p => Cell(new Vector2(p.X, p.Z))).ToList(), (float)(road.Width / 2 / map.Spacing), map.Columns, map.Rows,
                (x, y) => map.Cells[y * map.Columns + x] = (byte)cover);
        }
        foreach (var building in buildings)
        {
            if (building.Origin == FeatureOrigin.Map) Fill(building.Footprint, [], GroundCover.Building);
        }
    }

    // The map's roads with their heights (bridges and tunnels straight between their ends), surfaces and kinds; junctions
    // where two or more of them meet at a node (two only where the kind changes: the same kind was joined into one road).
    private static (List<RoadFeature> Roads, List<Vector3> Junctions) Finish(List<MapRoad> mapRoads)
    {
        var ends = new Dictionary<long, int>();
        foreach (var road in mapRoads)
        {
            foreach (long node in new[] { road.Start, road.End })
            {
                if (node >= 0) ends[node] = ends.GetValueOrDefault(node) + 1;
            }
        }
        var junctionOf = new Dictionary<long, int>();
        var junctions = new List<Vector3>();
        var roads = new List<RoadFeature>();
        foreach (var road in mapRoads)
        {
            var points = road.Points;
            Settle(points, road.Structure);
            int Junction(long node, Vector3 at)
            {
                if (node < 0 || ends[node] < 2) return -1;
                if (junctionOf.TryGetValue(node, out int index)) return index;
                junctions.Add(at);
                return junctionOf[node] = junctions.Count - 1;
            }
            var surface = road.ResolveSurface();
            roads.Add(new RoadFeature(points, road.Width, surface, MapRoads.KindOf(road.Class, surface), Junction(road.Start, points[0]), Junction(road.End, points[^1]))
            {
                Origin = FeatureOrigin.Map,
                Structure = road.Structure,
                Class = road.Class,
                Tags = road.Tags,
            });
        }
        return (roads, junctions);
    }

    // The images' roads that run mostly (70% or more) away from the map's: more than 5 m beyond their edges.
    private static List<RoadFeature> Unmapped(List<RoadFeature> traced, List<RoadFeature> mapped)
    {
        const double Cell = 20;
        var grid = new Dictionary<(int, int), List<(Vector2 A, Vector2 B, float Reach)>>();
        foreach (var road in mapped)
        {
            float reach = road.Width / 2 + 5;
            for (int i = 1; i < road.Points.Count; i++)
            {
                var a = new Vector2(road.Points[i - 1].X, road.Points[i - 1].Z);
                var b = new Vector2(road.Points[i].X, road.Points[i].Z);
                var min = Vector2.Min(a, b) - new Vector2(reach);
                var max = Vector2.Max(a, b) + new Vector2(reach);
                for (int cz = (int)Math.Floor(min.Y / Cell); cz <= (int)Math.Floor(max.Y / Cell); cz++)
                {
                    for (int cx = (int)Math.Floor(min.X / Cell); cx <= (int)Math.Floor(max.X / Cell); cx++)
                    {
                        if (!grid.TryGetValue((cx, cz), out var list)) grid[(cx, cz)] = list = [];
                        list.Add((a, b, reach));
                    }
                }
            }
        }
        bool Near(Vector2 p)
        {
            if (!grid.TryGetValue(((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell)), out var list)) return false;
            foreach (var (a, b, reach) in list)
            {
                var ab = b - a;
                float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                if (Vector2.Distance(p, a + ab * t) <= reach) return true;
            }
            return false;
        }
        var kept = new List<RoadFeature>();
        foreach (var road in traced)
        {
            int near = 0, samples = 0;
            for (int i = 1; i < road.Points.Count; i++)
            {
                var a = new Vector2(road.Points[i - 1].X, road.Points[i - 1].Z);
                var b = new Vector2(road.Points[i].X, road.Points[i].Z);
                int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(a, b) / 2));
                for (int s = 0; s < steps; s++, samples++) if (Near(Vector2.Lerp(a, b, (float)s / steps))) near++;
            }
            if (samples > 0 && near < 0.3 * samples) kept.Add(road);
        }
        return kept;
    }

    // The map's roads and junctions first, then the images' roads with the junctions they still use, renumbered.
    private static (List<RoadFeature> Roads, List<Vector3> Junctions) Combine(List<RoadFeature> mapped, List<Vector3> mapJunctions,
        List<RoadFeature> traced, List<Vector3> tracedJunctions)
    {
        var roads = new List<RoadFeature>(mapped);
        var junctions = new List<Vector3>(mapJunctions);
        var renumbered = new Dictionary<int, int>();
        int Renumber(int index)
        {
            if (index < 0) return -1;
            if (renumbered.TryGetValue(index, out int now)) return now;
            junctions.Add(tracedJunctions[index]);
            return renumbered[index] = junctions.Count - 1;
        }
        foreach (var road in traced) roads.Add(road with { Start = Renumber(road.Start), End = Renumber(road.End) });
        return (roads, junctions);
    }

    // Joins the blocks' road pieces into the network: the ends at one node (a junction or dead end in a block) meet, the
    // ends cut at a block's edge meet the nearest cut end of the neighbor's piece across it; then pieces meeting in twos
    // become one road.
    private static (List<RoadFeature> Roads, List<Vector3> Junctions) Join(List<RoadPiece> pieces, double tolerance, double spacing)
    {
        int slots = pieces.Count * 2;
        var parent = Enumerable.Range(0, slots).ToArray();
        int Find(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }
        void Union(int a, int b) => parent[Find(a)] = Find(b);
        Vector3 EndOf(int slot) => slot % 2 == 0 ? pieces[slot / 2].Points[0] : pieces[slot / 2].Points[^1];
        long KeyOf(int slot) => slot % 2 == 0 ? pieces[slot / 2].Start : pieces[slot / 2].End;

        var nodes = new Dictionary<long, int>();
        var cut = new List<int>();
        for (int slot = 0; slot < slots; slot++)
        {
            long key = KeyOf(slot);
            if (key < 0)
            {
                cut.Add(slot);
                continue;
            }
            if (nodes.TryGetValue(key, out int other)) Union(slot, other);
            else nodes[key] = slot;
        }

        // Cut ends: the nearest pairs of different blocks first.
        var grid = new Dictionary<(int, int), List<int>>();
        (int, int) Cell(Vector3 p) => ((int)Math.Floor(p.X / tolerance), (int)Math.Floor(p.Z / tolerance));
        foreach (int slot in cut)
        {
            var c = Cell(EndOf(slot));
            if (!grid.TryGetValue(c, out var list)) grid[c] = list = [];
            list.Add(slot);
        }
        var pairs = new List<(float Distance, int A, int B)>();
        foreach (int slot in cut)
        {
            var p = EndOf(slot);
            var (cx, cz) = Cell(p);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((cx + dx, cz + dz), out var list)) continue;
                    foreach (int other in list)
                    {
                        if (other <= slot || pieces[other / 2].Block == pieces[slot / 2].Block) continue;
                        var q = EndOf(other);
                        float d = Vector2.Distance(new(p.X, p.Z), new(q.X, q.Z));
                        if (d <= tolerance) pairs.Add((d, slot, other));
                    }
                }
            }
        }
        var matched = new bool[slots];
        foreach (var (_, a, b) in pairs.OrderBy(p => p.Distance))
        {
            if (matched[a] || matched[b]) continue;
            matched[a] = matched[b] = true;
            Union(a, b);
        }

        var groups = new Dictionary<int, List<int>>();
        for (int slot = 0; slot < slots; slot++)
        {
            int root = Find(slot);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(slot);
        }
        Vector3 Where(int root)
        {
            var sum = Vector3.Zero;
            foreach (int slot in groups[root]) sum += EndOf(slot);
            return sum / groups[root].Count;
        }

        // Chains through the groups of two, from every other group; what is left are rings of pieces.
        var used = new bool[pieces.Count];
        var chains = new List<(List<Vector3> Points, int Start, int End, List<(RoadPiece Piece, double Length)> Parts)>();
        void Walk(int slot)
        {
            var points = new List<Vector3>();
            var parts = new List<(RoadPiece, double)>();
            int startGroup = Find(slot);
            int current = slot;
            while (true)
            {
                int index = current / 2;
                used[index] = true;
                var piece = pieces[index];
                var line = current % 2 == 0 ? piece.Points : Enumerable.Reverse(piece.Points).ToList();
                if (points.Count > 0)
                {
                    // The two cut ends meet halfway.
                    points[^1] = (points[^1] + line[0]) / 2;
                    points.AddRange(line.Skip(1));
                }
                else
                {
                    points.AddRange(line);
                }
                parts.Add((piece, Length(line)));
                int other = current ^ 1;
                int group = Find(other);
                var members = groups[group];
                if (members.Count != 2 || group == startGroup)
                {
                    chains.Add((points, startGroup, group, parts));
                    return;
                }
                int next = members[0] == other ? members[1] : members[0];
                if (used[next / 2])
                {
                    chains.Add((points, startGroup, group, parts));
                    return;
                }
                current = next;
            }
        }
        foreach (var (root, members) in groups)
        {
            if (members.Count == 2) continue;
            foreach (int slot in members)
            {
                if (!used[slot / 2]) Walk(slot);
            }
        }
        for (int index = 0; index < pieces.Count; index++)
        {
            if (!used[index]) Walk(index * 2);
        }

        // Junctions: where three or more roads meet, and where a ring closes.
        var junctionOf = new Dictionary<int, int>();
        var junctions = new List<Vector3>();
        int Junction(int group, bool ring)
        {
            if (groups[group].Count < 3 && !ring) return -1;
            if (junctionOf.TryGetValue(group, out int index)) return index;
            junctions.Add(Where(group));
            return junctionOf[group] = junctions.Count - 1;
        }
        var roads = new List<RoadFeature>();
        foreach (var (points, start, end, parts) in chains)
        {
            double length = parts.Sum(p => p.Length);
            if (length < Math.Max(2, 4 * spacing) && groups[start].Count == 1 && groups[end].Count == 1) continue;
            bool ring = start == end;
            int a = Junction(start, ring), b = Junction(end, ring);
            // The ends at their junction, so the network is connected.
            if (a >= 0) points[0] = junctions[a];
            if (b >= 0) points[^1] = junctions[b];
            var line = Skeletons.Simplify(points.Select(p => ((double)p.X, (double)p.Z)).ToList(), spacing * 0.5);
            var kept = new List<Vector3>(line.Count);
            int k = 0;
            foreach (var (x, z) in line)
            {
                while (k < points.Count && (points[k].X != (float)x || points[k].Z != (float)z)) k++;
                kept.Add(k < points.Count ? points[k] : new Vector3((float)x, points[^1].Y, (float)z));
            }
            float width = (float)(parts.Sum(p => p.Piece.Width * p.Length) / Math.Max(1e-6, length));
            var surface = parts.GroupBy(p => p.Piece.Surface).MaxBy(g => g.Sum(p => p.Length))!.Key;
            roads.Add(new RoadFeature(kept, width, surface, RoadFeature.KindOf(width, surface), a, b));
        }
        return (roads, junctions);
    }

    // Shadowed cells take the most common cover around them, growing in from the shadow's edges.
    private static void ResolveShadows(CoverMap map)
    {
        int w = map.Columns, h = map.Rows;
        var cells = map.Cells;
        for (int pass = 0; pass < 16; pass++)
        {
            var next = (byte[])cells.Clone();
            bool changed = false, left = false;
            Parallel.For(0, h, () => (false, false), (y, _, state) =>
            {
                Span<int> tally = stackalloc int[16];
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (cells[i] != (byte)GroundCover.Shadow) continue;
                    tally.Clear();
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            byte c = cells[yy * w + xx];
                            if (c != (byte)GroundCover.Shadow && c != (byte)GroundCover.None) tally[c]++;
                        }
                    }
                    int best = 0;
                    for (int k = 1; k < tally.Length; k++) if (tally[k] > tally[best]) best = k;
                    if (tally[best] > 0)
                    {
                        next[i] = (byte)best;
                        state.Item1 = true;
                    }
                    else
                    {
                        state.Item2 = true;
                    }
                }
                return state;
            }, state =>
            {
                if (state.Item1) changed = true;
                if (state.Item2) left = true;
            });
            Array.Copy(next, cells, cells.Length);
            if (!changed || !left) break;
        }
    }

    // The areas of each kind in the cover map: where the kind is most of the cells around, smoothed, small holes filled
    // and small parts dropped; gravel and asphalt opened so roads aren't areas; not where (within 5 m) the map has areas
    // of the kind (or of kinds that include it: scree is rock to the images).
    private static List<AreaFeature> Outline(CoverMap map, float[] heights, List<AreaFeature> mapAreas)
    {
        int w = map.Columns, h = map.Rows;
        double cell = map.Spacing;
        var ground = (float[])heights.Clone();
        for (int i = 0; i < ground.Length; i++) if (float.IsNaN(ground[i])) ground[i] = 0;
        var slope = Rasters.Slope(ground, w, h, cell);
        var areas = new List<AreaFeature>();
        int Cells(double meters) => Math.Max(1, (int)Math.Round(meters / cell));

        bool[]? Mapped(AreaKind[] kinds)
        {
            var mapped = mapAreas.Where(a => kinds.Contains(a.Kind)).ToList();
            if (mapped.Count == 0) return null;
            var mask = new bool[w * h];
            Vector2 Cell(Vector2 p) => new((float)((p.X - map.X0) / cell), (float)((p.Y - map.Z0) / cell));
            foreach (var area in mapped)
            {
                var rings = new List<IReadOnlyList<Vector2>> { area.Outline.Select(Cell).ToList() };
                rings.AddRange(area.Holes.Select(hole => (IReadOnlyList<Vector2>)hole.Select(Cell).ToList()));
                Polygons.Scan(rings, w, h, (x, y) => mask[y * w + x] = true);
            }
            return Skeletons.Dilate(mask, w, h, Cells(5));
        }

        void Add(AreaKind kind, Func<GroundCover, float, bool> member, double radius, double open, double minArea, double maxHole, params AreaKind[] mappedAs)
        {
            var share = new float[w * h];
            for (int i = 0; i < share.Length; i++) share[i] = member((GroundCover)map.Cells[i], slope[i]) ? 1 : 0;
            var mean = Rasters.BoxMean(share, w, h, Cells(radius));
            var mask = new bool[w * h];
            var mapped = Mapped([kind, .. mappedAs]);
            for (int i = 0; i < mask.Length; i++) mask[i] = mean[i] > 0.5f && mapped?[i] != true;
            if (open > 0) mask = Skeletons.Dilate(Skeletons.Erode(mask, w, h, Cells(open)), w, h, Cells(open));
            Skeletons.FillHoles(mask, w, h, (int)(maxHole / (cell * cell)));
            var (labels, sizes) = Skeletons.Components(mask, w, h);
            int smallest = (int)(minArea / (cell * cell));
            for (int i = 0; i < mask.Length; i++) if (mask[i] && sizes[labels[i]] < smallest) mask[i] = false;
            Vector2 Local(Vector2 p) => new((float)(map.X0 + p.X * cell), (float)(map.Z0 + p.Y * cell));
            double tolerance = Math.Max(1, 1 / cell);
            foreach (var polygon in Polygons.Trace(mask, w, h))
            {
                var outline = Polygons.SimplifyRing(polygon.Outline, tolerance).Select(Local).ToList();
                var holes = polygon.Holes.Select(hole => (IReadOnlyList<Vector2>)Polygons.SimplifyRing(hole, tolerance).Select(Local).ToList()).ToList();
                areas.Add(new AreaFeature(kind, outline, holes));
            }
        }

        Add(AreaKind.Forest, (c, _) => c == GroundCover.Tree, 5, 0, 1000, 400);
        Add(AreaKind.Rock, (c, s) => c == GroundCover.Rock && s < 45, 1.5, 0, 100, 50, AreaKind.Scree, AreaKind.Cliff);
        Add(AreaKind.Cliff, (c, s) => c == GroundCover.Rock && s >= 45, 1.5, 0, 50, 50, AreaKind.Rock, AreaKind.Scree);
        Add(AreaKind.Gravel, (c, _) => c is GroundCover.Gravel or GroundCover.Soil, 1.5, 3, 200, 50, AreaKind.Scree, AreaKind.Sand, AreaKind.Rock);
        // Dark gray scree looks like asphalt to the images.
        Add(AreaKind.Paved, (c, _) => c == GroundCover.Paved, 1.5, 4, 300, 50, AreaKind.Scree, AreaKind.Rock);
        Add(AreaKind.Water, (c, _) => c == GroundCover.Water, 2, 0, 100, 50);
        Add(AreaKind.Snow, (c, _) => c == GroundCover.Snow, 2, 0, 200, 50);
        return areas;
    }
}
