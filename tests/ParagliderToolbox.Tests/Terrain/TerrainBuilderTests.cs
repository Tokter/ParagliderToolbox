using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Tests.Terrain;

/// <summary>Heights of a tilted plane in the terrain's frame, where <c>covers</c> says the source has data.</summary>
internal sealed class PlaneSource(string id, double resolution, Func<double, double, bool> covers, float offset = 0) : IElevationSource
{
    public static float Plane(double x, double z) => (float)(500 + 0.1 * x + 0.05 * z);

    public string Id => id;
    public string Name => id;
    public string Description => id;
    public string Attribution => "© " + id;
    public string License => "test";
    public double Resolution => resolution;
    public SourceScope Scope { get; init; } = SourceScope.Regional;
    public TimeSpan Delay { get; init; }
    public bool Covers(GeoBounds bounds) => true;

    public async Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        int filled = 0;
        for (int row = 0; row < request.Rows; row++)
        {
            for (int column = 0; column < request.Columns; column++)
            {
                int i = row * request.Columns + column;
                double x = request.X0 + column * request.Spacing, z = request.Z0 + row * request.Spacing;
                if (!float.IsNaN(heights[i]) || !covers(x, z)) continue;
                heights[i] = Plane(x, z) + offset;
                filled++;
            }
        }
        return filled;
    }
}

/// <summary>One color where <c>covers</c> says the source has data.</summary>
internal sealed class ColorSource(uint color, Func<double, double, bool> covers) : IImagerySource
{
    public string Id => "color";
    public string Name => "Color";
    public string Description => "";
    public string Attribution => "© color";
    public string License => "test";
    public double Resolution => 1;
    public SourceScope Scope => SourceScope.Regional;
    public bool Covers(GeoBounds bounds) => true;

    public Task<int> FillAsync(SampleRequest request, uint[] colors, CancellationToken cancellationToken)
    {
        int filled = 0;
        for (int row = 0; row < request.Rows; row++)
        {
            for (int column = 0; column < request.Columns; column++)
            {
                int i = row * request.Columns + column;
                if (colors[i] >> 24 != 0 || !covers(request.X0 + column * request.Spacing, request.Z0 + row * request.Spacing)) continue;
                colors[i] = color;
                filled++;
            }
        }
        return Task.FromResult(filled);
    }
}

public class TerrainBuilderTests
{
    private static readonly TerrainSettings s_settings = new()
    {
        Center = new GeoPoint(46.6863, 7.8632), Size = 1024, Resolution = 8, TileSamples = 33, TextureSize = 64,
    };

    private static TerrainSources Sources(params ITerrainSource[] sources)
    {
        var result = new TerrainSources();
        result.Elevation.AddRange(sources.OfType<IElevationSource>());
        result.Imagery.AddRange(sources.OfType<IImagerySource>());
        return result;
    }

    [Fact]
    public async Task TakesEachPoint_FromTheFinestSourceThatHasIt()
    {
        var sources = Sources(new PlaneSource("coarse", 30, (_, _) => true, offset: 20), new PlaneSource("fine", 1, (x, _) => x < 0));
        var model = await new TerrainBuilder(sources) { BlendDistance = 0 }.BuildAsync(s_settings);

        Assert.Equal(model.Layout.Tiles.Count, model.Tiles.Count);
        Assert.Equal(PlaneSource.Plane(-300, 100), model.HeightAt(-300, 100), 3);
        Assert.Equal(PlaneSource.Plane(300, 100) + 20, model.HeightAt(300, 100), 3);
        Assert.Equal(new[] { "coarse", "fine" }, model.ElevationSources.Select(s => s.Source.Id).Order());
        Assert.InRange(model.ElevationSources[0].Share, 0.4, 0.6);
        Assert.Contains("© fine", model.Attribution);
        Assert.Contains("© coarse", model.Attribution);
        Assert.Equal(0, model.MissingShare);
    }

    [Fact]
    public async Task PreferredSource_GoesFirst()
    {
        var sources = Sources(new PlaneSource("coarse", 30, (_, _) => true, offset: 20), new PlaneSource("fine", 1, (x, _) => x < 0));
        var model = await new TerrainBuilder(sources).BuildAsync(s_settings with { ElevationSource = "coarse" });
        Assert.Equal("coarse", Assert.Single(model.ElevationSources).Source.Id);
        Assert.Equal(PlaneSource.Plane(-300, 100) + 20, model.HeightAt(-300, 100), 3);
    }

    [Fact]
    public async Task EasesAFinerSourceIntoTheNext_WhereItsDataEnds()
    {
        var sources = Sources(new PlaneSource("coarse", 30, (_, _) => true, offset: 20), new PlaneSource("fine", 1, (x, _) => x < 0));
        var hard = await new TerrainBuilder(sources) { BlendDistance = 0 }.BuildAsync(s_settings);
        var eased = await new TerrainBuilder(sources) { BlendDistance = 200 }.BuildAsync(s_settings);

        static float LargestStep(TerrainModel model)
        {
            float largest = 0;
            for (double x = -400; x < 400; x += 8)
            {
                float a = model.HeightAt(x, 40) - PlaneSource.Plane(x, 40), b = model.HeightAt(x + 8, 40) - PlaneSource.Plane(x + 8, 40);
                largest = Math.Max(largest, Math.Abs(b - a));
            }
            return largest;
        }
        Assert.InRange(LargestStep(hard), 19, 21);
        Assert.InRange(LargestStep(eased), 0, 2);
        // Beyond the blend distance the finer source is untouched; at its edge the next one's height takes over.
        Assert.Equal(PlaneSource.Plane(-320, 40), eased.HeightAt(-320, 40), 3);
        Assert.InRange(eased.HeightAt(-8, 40) - PlaneSource.Plane(-8, 40), 15, 20);
    }

    [Fact]
    public async Task NeighboringTiles_ShareTheirEdges_AndTheirBorders()
    {
        var sources = Sources(new PlaneSource("coarse", 30, (_, _) => true, offset: 20), new PlaneSource("fine", 1, (x, z) => x < 10 && z > -50));
        var model = await new TerrainBuilder(sources).BuildAsync(s_settings);
        var west = model.Tiles[new TileKey(0, 1, 1)];
        var east = model.Tiles[new TileKey(0, 2, 1)];
        int last = west.Rect.Columns - 1;
        for (int row = -1; row <= west.Rect.Rows; row++)
        {
            Assert.Equal(west.Height(last, row), east.Height(0, row));
            // The border reaches one sample into the neighbor.
            Assert.Equal(west.Height(last + 1, row), east.Height(1, row));
            Assert.Equal(west.Height(last - 1, row), east.Height(-1, row));
        }
    }

    [Fact]
    public async Task Textures_TakeTheImagery_AndElevationColorsWhereThereIsNone()
    {
        const uint red = 0xFF2020E0u;
        var sources = Sources(new PlaneSource("plane", 1, (_, _) => true), new ColorSource(red, (x, _) => x < 0));
        var model = await new TerrainBuilder(sources) { TextureQuality = 100 }.BuildAsync(s_settings);

        var tile = model.Tiles[new TileKey(0, 1, 1)];
        Assert.Equal((64, 64), (tile.TextureWidth, tile.TextureHeight));
        var pixels = tile.DecodeTexture()!;
        uint west = pixels[32 * 64 + 10], east = model.Tiles[new TileKey(0, 2, 1)].DecodeTexture()![32 * 64 + 50];
        Assert.InRange((int)(west & 0xFF), 0xD0, 0xF0);
        Assert.InRange((int)(east & 0xFF), 0, 0x80);
        Assert.Equal("color", Assert.Single(model.ImagerySources).Source.Id);

        var plain = await new TerrainBuilder(sources).BuildAsync(s_settings with { Texture = TerrainTexture.None });
        Assert.All(plain.Tiles.Values, t => Assert.Null(t.Texture));
    }

    [Fact]
    public async Task PointsNoSourceHas_TakeTheirNeighborsHeight()
    {
        var sources = Sources(new PlaneSource("partial", 1, (x, z) => x * x + z * z > 100 * 100));
        var model = await new TerrainBuilder(sources).BuildAsync(s_settings);
        Assert.True(model.MissingShare > 0);
        Assert.InRange(model.HeightAt(0, 0), 480, 520);
    }

    // Records every report on the thread it comes from, in order.
    private sealed class RecordingProgress : IProgress<TerrainProgress>
    {
        public System.Collections.Concurrent.ConcurrentQueue<TerrainProgress> Reports { get; } = new();

        public void Report(TerrainProgress value) => Reports.Enqueue(value);
    }

    [Fact]
    public async Task Progress_IsReportedWhileATileIsStillBuilding_CoarsestLevelFirst()
    {
        var sources = Sources(new PlaneSource("slow", 1, (_, _) => true) { Delay = TimeSpan.FromMilliseconds(300) });
        var progress = new RecordingProgress();
        await new TerrainBuilder(sources) { ProgressInterval = TimeSpan.FromMilliseconds(40), Parallelism = 1 }
            .BuildAsync(s_settings with { Size = 512, Texture = TerrainTexture.None }, progress);

        var reports = progress.Reports.ToList();
        var beforeFirstTile = reports.TakeWhile(r => r.Tile is null).ToList();
        Assert.True(beforeFirstTile.Count >= 3);
        Assert.All(beforeFirstTile, r =>
        {
            Assert.Equal(0, r.TilesDone);
            Assert.Equal(r.LevelCount - 1, r.Level);
            Assert.Equal(1, r.LevelTileCount);
        });
        var last = reports.Last(r => r.Tile != null);
        Assert.Equal(last.TileCount, last.TilesDone);
        Assert.Equal(0, last.Level);
        Assert.Equal(last.LevelTileCount, last.LevelTilesDone);
    }

    [Fact]
    public void Sources_RegionalFirst_TheCoarsestThatIsFineEnough_ThenFinerOnes()
    {
        var sources = Sources(
            new PlaneSource("global", 30, (_, _) => true) { Scope = SourceScope.Global },
            new PlaneSource("regional", 10, (_, _) => true),
            new PlaneSource("lidar", 0.5, (_, _) => true));
        var bounds = new GeoBounds(46, 7, 47, 8);
        string[] Order(double spacing, string? preferred = null) => sources.ElevationFor(bounds, preferred, spacing).Select(s => s.Id).ToArray();

        // Fine levels: the lidar, then the regional model where the lidar ends, the global model last.
        Assert.Equal(new[] { "lidar", "regional", "global" }, Order(4));
        // Coarse levels: the regional model has the same detail there, with far less to read.
        Assert.Equal(new[] { "regional", "lidar", "global" }, Order(10));
        Assert.Equal(new[] { "regional", "lidar", "global" }, Order(64));
        Assert.Equal(new[] { "global", "regional", "lidar" }, Order(64, preferred: "global"));
    }

    [Fact]
    public void ElevationColors_ShowWaterSnowAndRock()
    {
        uint water = ElevationColors.Color(0, 0), snow = ElevationColors.Color(3200, 10), rock = ElevationColors.Color(1500, 60), meadow = ElevationColors.Color(1200, 10);
        Assert.True((water >> 16 & 0xFF) > (water & 0xFF));
        Assert.True((snow & 0xFF) > 220);
        Assert.True((meadow >> 8 & 0xFF) > (meadow & 0xFF));
        Assert.InRange((int)(rock & 0xFF) - (int)(rock >> 8 & 0xFF), 0, 20);
    }
}
