using System.Numerics;
using System.Text.Json;
using ParagliderToolbox.Terrain.Export;
using ParagliderToolbox.Terrain.Features;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Sources;
using SkiaSharp;

namespace ParagliderToolbox.Tests.Terrain;

/// <summary>
/// A landscape to find features in, in the terrain's frame: meadow, a forest of 7 × 7 conifers (cones 3 m in radius,
/// 15 m high, 6 m apart), a gravel road 5 m wide from west to east at z = 20 with a branch 4 m wide north from x = 0, a
/// flat-roofed building of 15 × 10 m and 8 m, a boulder of 1.5 m radius and a bridge (a gray deck 8 × 30 m, 6 m up: to
/// the surface model as smooth, tall and gray as a roof).
/// </summary>
internal static class FeatureScene
{
    public static uint Rgb(int r, int g, int b) => 0xFF000000u | (uint)b << 16 | (uint)g << 8 | (uint)r;

    public static readonly uint Grass = Rgb(95, 140, 70), Crown = Rgb(40, 80, 35), Gravel = Rgb(190, 192, 194), Roof = Rgb(150, 70, 60);

    public static IEnumerable<(double X, double Z)> Trees =>
        from i in Enumerable.Range(0, 7) from j in Enumerable.Range(0, 7) select (-80.0 + 6 * i, -80.0 + 6 * j);

    public static bool OnRoad(double x, double z) => Math.Abs(z - 20) <= 2.5 || Math.Abs(x) <= 2 && z <= 20;

    public static bool InBuilding(double x, double z) => x >= 30 && x <= 45 && z >= -50 && z <= -40;

    public static bool OnBoulder(double x, double z) => (x - 60) * (x - 60) + (z - 60) * (z - 60) <= 1.5 * 1.5;

    public static bool OnDeck(double x, double z) => Math.Abs(x - 60) <= 4 && z >= -55 && z <= -25;

    public static float TreeHeight(double x, double z)
    {
        double best = 0;
        foreach (var (tx, tz) in Trees)
        {
            double d = Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));
            if (d < 3) best = Math.Max(best, 15 * (1 - d / 3));
        }
        return (float)best;
    }

    public static uint Color(double x, double z)
    {
        if (InBuilding(x, z)) return Roof;
        if (OnDeck(x, z)) return Rgb(150, 150, 150);
        if (TreeHeight(x, z) > 0) return Crown;
        // A boulder is gray and rough: light and dark stones.
        if (OnBoulder(x, z)) return ((int)Math.Floor(x / 0.5) + (int)Math.Floor(z / 0.5)) % 2 == 0 ? Rgb(110, 110, 110) : Rgb(195, 195, 195);
        return OnRoad(x, z) ? Gravel : Grass;
    }

    public static float Top(double x, double z) => InBuilding(x, z) ? 8 : OnDeck(x, z) ? 6 : TreeHeight(x, z);

    public static TerrainSources Sources(bool surface, IMapSource? map = null)
    {
        var sources = new TerrainSources();
        sources.Elevation.Add(new PlaneSource("plane", 0.5, (_, _) => true));
        sources.Imagery.Add(new SceneImagery());
        if (surface) sources.Surface.Add(new SceneSurface());
        if (map != null) sources.Maps.Add(map);
        return sources;
    }

    /// <summary>A way through local points (x, z) of the frame centered on <paramref name="origin"/>.</summary>
    public static MapWay Way(GeoPoint origin, long id, (long Node, double X, double Z)[] nodes, params (string Key, string Value)[] tags)
    {
        var frame = new LocalFrame(origin);
        return new MapWay(id, nodes.Select(n => n.Node).ToList(), nodes.Select(n => frame.ToGeo(n.X, n.Z)).ToList(), tags.ToDictionary(t => t.Key, t => t.Value));
    }

    /// <summary>A node at local (x, z).</summary>
    public static MapNode Node(GeoPoint origin, long id, double x, double z, params (string Key, string Value)[] tags) =>
        new(id, new LocalFrame(origin).ToGeo(x, z), tags.ToDictionary(t => t.Key, t => t.Value));

    /// <summary>A closed way around the rectangle x0…x1, z0…z1.</summary>
    public static MapWay Box(GeoPoint origin, long id, double x0, double z0, double x1, double z1, params (string Key, string Value)[] tags) =>
        Way(origin, id, [(id * 10, x0, z0), (id * 10 + 1, x1, z0), (id * 10 + 2, x1, z1), (id * 10 + 3, x0, z1), (id * 10, x0, z0)], tags);

    /// <summary>
    /// A map of the scene: its roads (<see cref="MapWays"/>); the building as a house and a house of two floors in the
    /// meadow; the forest as a multipolygon of conifers with a clearing; a pond and scree; a power line across the area
    /// and a stream in a culvert; a peak and a takeoff; a broadleaf tree, a tree where the forest's corner tree stands,
    /// a row of trees, a stone.
    /// </summary>
    public static MapData Map(GeoPoint origin, bool branch = true)
    {
        var frame = new LocalFrame(origin);
        List<GeoPoint> Ring(params (double X, double Z)[] points) => points.Select(p => frame.ToGeo(p.X, p.Z)).ToList();
        var ways = MapWays(origin, branch).ToList();
        ways.Add(Box(origin, 100, 30, -50, 45, -40, ("building", "house")));
        ways.Add(Box(origin, 101, 70, 70, 80, 78, ("building", "yes"), ("building:levels", "2")));
        ways.Add(Box(origin, 102, -90, 70, -60, 95, ("natural", "water"), ("name", "Pond")));
        ways.Add(Box(origin, 103, 50, 30, 90, 55, ("natural", "scree")));
        ways.Add(Way(origin, 104, [(1040, -130, -80), (1041, 0, -80), (1042, 130, -80)], ("power", "line")));
        ways.Add(Way(origin, 105, [(1050, 20, 60), (1051, 20, 90)], ("waterway", "stream"), ("tunnel", "culvert")));
        ways.Add(Way(origin, 106, [(1060, 50, 80), (1061, 90, 80)], ("natural", "tree_row")));
        var forest = new MapRelation(200,
        [
            // The outline in two ways, as mappers draw large ones.
            new MapMember("outer", Ring((-85, -85), (-35, -85), (-35, -35))),
            new MapMember("outer", Ring((-35, -35), (-85, -35), (-85, -85))),
            new MapMember("inner", Ring((-70, -70), (-60, -70), (-60, -60), (-70, -60), (-70, -70))),
        ], new Dictionary<string, string> { ["landuse"] = "forest", ["type"] = "multipolygon", ["leaf_type"] = "needleleaved" });
        var nodes = new List<MapNode>
        {
            Node(origin, 300, 10, 10, ("natural", "peak"), ("name", "Test Peak"), ("ele", "2000")),
            Node(origin, 301, -20, -20, ("sport", "free_flying"), ("free_flying:site", "takeoff"), ("name", "Test Takeoff")),
            Node(origin, 302, 80, -60, ("natural", "tree"), ("leaf_type", "broadleaved"), ("height", "12"), ("genus", "Quercus")),
            Node(origin, 303, -80, -80, ("natural", "tree")),
            Node(origin, 304, -30, 80, ("natural", "stone")),
        };
        return new MapData(nodes, ways, [forest]);
    }

    /// <summary>A map of the scene's roads (in the frame centered on <paramref name="origin"/>): the road west to east (a track without a surface, beyond the area), the branch north (gravel, 4 m), a road on the bridge, a path in two ways of the same tags, a road under construction.</summary>
    public static IReadOnlyList<MapWay> MapWays(GeoPoint origin, bool branch = true)
    {
        var ways = new List<MapWay>
        {
            Way(origin, 1, [(1, -130, 20), (2, 0, 20), (3, 130, 20)], ("highway", "track")),
            Way(origin, 3, [(5, 60, -60), (6, 60, -20)], ("highway", "residential"), ("bridge", "yes")),
            Way(origin, 4, [(11, -90, -95), (12, 90, -95)], ("highway", "construction")),
            Way(origin, 5, [(7, -90, 60), (8, -70, 70), (9, -50, 60)], ("highway", "path"), ("sac_scale", "hiking")),
            Way(origin, 6, [(9, -50, 60), (10, -30, 60)], ("highway", "path"), ("sac_scale", "hiking")),
        };
        if (branch) ways.Add(Way(origin, 2, [(2, 0, 20), (4, 0, -130)], ("highway", "track"), ("surface", "gravel"), ("width", "4")));
        return ways;
    }

    /// <summary>A map with fixed data, or one that can't be reached.</summary>
    public sealed class FakeMap(MapData? data) : IMapSource
    {
        public string Id => "fake-map";
        public string Name => "Fake map";
        public string Description => "";
        public string Attribution => "© fake map";
        public string License => "test";
        public double Resolution => 1;
        public SourceScope Scope => SourceScope.Global;
        public bool Covers(GeoBounds bounds) => true;

        public Task<MapData> DataAsync(GeoBounds bounds, CancellationToken cancellationToken) =>
            data is null ? throw new HttpRequestException("offline") : Task.FromResult(data);
    }

    private sealed class SceneImagery : IImagerySource
    {
        public string Id => "scene";
        public string Name => "Scene";
        public string Description => "";
        public string Attribution => "© scene";
        public string License => "test";
        public double Resolution => 0.1;
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
                    if (colors[i] >> 24 != 0) continue;
                    colors[i] = Color(request.X0 + column * request.Spacing, request.Z0 + row * request.Spacing);
                    filled++;
                }
            }
            return Task.FromResult(filled);
        }
    }

    private sealed class SceneSurface : IElevationSource
    {
        public string Id => "scene-surface";
        public string Name => "Scene surface";
        public string Description => "";
        public string Attribution => "© scene surface";
        public string License => "test";
        public double Resolution => 0.5;
        public SourceScope Scope => SourceScope.Regional;
        public bool Covers(GeoBounds bounds) => true;

        public Task<int> FillAsync(SampleRequest request, float[] heights, CancellationToken cancellationToken)
        {
            int filled = 0;
            for (int row = 0; row < request.Rows; row++)
            {
                for (int column = 0; column < request.Columns; column++)
                {
                    int i = row * request.Columns + column;
                    if (!float.IsNaN(heights[i])) continue;
                    double x = request.X0 + column * request.Spacing, z = request.Z0 + row * request.Spacing;
                    heights[i] = PlaneSource.Plane(x, z) + Top(x, z);
                    filled++;
                }
            }
            return Task.FromResult(filled);
        }
    }
}

public class FeatureTests
{
    private static readonly GeoPoint s_origin = new(46.6863, 7.8632);

    // Small blocks, so the road crosses several and is joined across them; the roads traced in the images unless a map is given.
    private static Task<FeatureSet> DetectAsync(bool surface, IMapSource? map = null, RoadSource roads = RoadSource.Images) =>
        new FeatureDetector(FeatureScene.Sources(surface, map)) { BlockPixels = 128 }
            .DetectAsync(s_origin, FeatureArea.Around(0, 0, 200), new FeatureSettings { Spacing = 0.5, RoadsFrom = roads, Map = map != null });

    [Fact]
    public async Task WithASurfaceModel_FindsEveryTree_WithItsHeight_AndNoneTwice()
    {
        var features = await DetectAsync(surface: true);

        var trees = features.Trees.Where(t => t.Kind != TreeKind.Bush).ToList();
        Assert.Equal(49, trees.Count);
        foreach (var (x, z) in FeatureScene.Trees)
        {
            var tree = trees.MinBy(t => Vector2.Distance(new(t.Position.X, t.Position.Z), new((float)x, (float)z)));
            Assert.InRange(Vector2.Distance(new(tree.Position.X, tree.Position.Z), new((float)x, (float)z)), 0, 0.8);
            // The highest sample is a third of a meter from the cone's tip.
            Assert.InRange(tree.Height, 13, 15.5);
            Assert.Equal(PlaneSource.Plane(tree.Position.X, tree.Position.Z), tree.Position.Y, 1);
            // A cone is pointed: a conifer.
            Assert.Equal(TreeKind.Conifer, tree.Kind);
            Assert.False(tree.Estimated);
        }
        Assert.Equal(1, features.SurfaceShare, 2);
        Assert.Empty(features.Notes);
    }

    [Fact]
    public async Task TheRoad_IsJoinedAcrossTheBlocks_IntoANetworkWithOneJunction()
    {
        var features = await DetectAsync(surface: true);

        var junction = Assert.Single(features.Junctions);
        Assert.InRange(Vector2.Distance(new(junction.X, junction.Z), new(0, 20)), 0, 3);
        Assert.Equal(3, features.Roads.Count);
        Assert.All(features.Roads, road =>
        {
            Assert.Equal(RoadSurface.Gravel, road.Surface);
            Assert.Equal(RoadKind.Track, road.Kind);
            Assert.Contains(0, new[] { road.Start, road.End });
            Assert.Contains(-1, new[] { road.Start, road.End });
        });
        // West to east 5 m wide, the branch north 4 m wide, each to the area's edge.
        var branch = features.Roads.Single(r => r.Points.All(p => Math.Abs(p.X) < 3));
        Assert.InRange(branch.Width, 3.4, 4.5);
        Assert.InRange(branch.Length, 110, 122);
        var main = features.Roads.Where(r => r != branch).ToList();
        Assert.All(main, r => Assert.InRange(r.Width, 4.4, 5.5));
        Assert.InRange(main.Sum(r => r.Length), 190, 202);
        Assert.All(main.SelectMany(r => r.Points), p => Assert.InRange(p.Z, 19, 21));
        Assert.All(features.Roads.SelectMany(r => r.Points), p => Assert.Equal(PlaneSource.Plane(p.X, p.Z), p.Y, 1));
    }

    [Fact]
    public async Task FindsTheBuilding_WithItsRectangleAndHeight_AndTheBoulder()
    {
        var features = await DetectAsync(surface: true);

        // Without a map the bridge is a building too.
        Assert.Equal(2, features.Buildings.Count);
        var building = features.Buildings.Single(b => b.Length < 20);
        Assert.InRange(Vector2.Distance(new(building.Position.X, building.Position.Z), new(37.5f, -45)), 0, 0.6);
        Assert.InRange(building.Length, 14, 16);
        Assert.InRange(building.Width, 9, 11);
        // The long side runs east–west.
        Assert.InRange(Math.Abs(building.Angle - 90), 0, 3);
        Assert.InRange(building.Height, 7.5, 8.5);
        Assert.Equal(RoofShape.Flat, building.Roof);
        Assert.Equal(4, building.Footprint.Count);
        Assert.True(Polygons.SignedArea(building.Footprint) < 0, "Footprints run counterclockwise seen from above.");

        var rock = Assert.Single(features.Rocks);
        Assert.InRange(Vector2.Distance(new(rock.Position.X, rock.Position.Z), new(60, 60)), 0, 0.5);
        Assert.InRange(rock.Radius, 1.2, 1.8);
    }

    [Fact]
    public async Task OutlinesTheForest_AndMapsTheGroundCover()
    {
        var features = await DetectAsync(surface: true);

        var forest = Assert.Single(features.Areas, a => a.Kind == AreaKind.Forest);
        // The crowns (where higher than a tree) cover 36 × 36 m and a little more.
        Assert.InRange(forest.Size, 1000, 2000);
        Assert.True(Polygons.SignedArea(forest.Outline) < 0, "Outlines run counterclockwise seen from above.");
        Assert.True(Polygons.Contains(forest.Outline, new Vector2(-62, -62)));

        var map = Assert.IsType<CoverMap>(features.Covers);
        Assert.Equal((200, 200), (map.Columns, map.Rows));
        Assert.Equal(GroundCover.Grass, map.At(80, -80));
        Assert.Equal(GroundCover.Gravel, map.At(-50, 20));
        Assert.Equal(GroundCover.Building, map.At(37, -45));
        Assert.Equal(GroundCover.Tree, map.At(-62, -62));
    }

    [Fact]
    public async Task WithoutASurfaceModel_FindsTheCrownsInTheImage_WithEstimatedHeights_AndNoBuildings()
    {
        var features = await DetectAsync(surface: false);

        Assert.Empty(features.Buildings);
        var trees = features.Trees.Where(t => t.Position.X < -40 && t.Position.Z < -40).ToList();
        Assert.InRange(trees.Count, 40, 58);
        Assert.All(trees, t => Assert.True(t.Estimated));
        Assert.InRange(trees.Select(t => t.CrownRadius).Order().ElementAt(trees.Count / 2), 2, 5);
        Assert.Equal(0, features.SurfaceShare);
        Assert.Contains(features.Notes, n => n.Contains("No surface model"));
        // The road needs no heights.
        Assert.Equal(3, features.Roads.Count);
    }

    [Fact]
    public async Task Bytes_And_Json_ReadBackAsWritten()
    {
        var features = await DetectAsync(surface: true);
        var back = FeatureFiles.FromBytes(FeatureFiles.ToBytes(features));

        Assert.Equal(features.Origin, back.Origin);
        Assert.Equal((features.X0, features.Z0, features.Width, features.Depth), (back.X0, back.Z0, back.Width, back.Depth));
        Assert.Equal(features.Trees.Count, back.Trees.Count);
        Assert.Equal(features.Trees[5].Kind, back.Trees[5].Kind);
        Assert.Equal(features.Trees[5].Height, back.Trees[5].Height, 1);
        Assert.Equal(features.Roads.Select(r => (r.Kind, r.Surface, r.Start, r.End, r.Points.Count, r.Origin)),
            back.Roads.Select(r => (r.Kind, r.Surface, r.Start, r.End, r.Points.Count, r.Origin)));
        Assert.Equal(features.Junctions.Count, back.Junctions.Count);
        Assert.Equal(features.Buildings[0].Footprint.Count, back.Buildings[0].Footprint.Count);
        Assert.Equal(features.Buildings[0].Roof, back.Buildings[0].Roof);
        Assert.Equal(features.Rocks.Count, back.Rocks.Count);
        Assert.Equal(features.Areas.Select(a => (a.Kind, a.Outline.Count, a.Holes.Count)), back.Areas.Select(a => (a.Kind, a.Outline.Count, a.Holes.Count)));
        Assert.Equal(features.Covers!.Cells, back.Covers!.Cells);
        Assert.Equal(features.Attribution, back.Attribution);
    }

    [Fact]
    public async Task Export_WritesTheJson_TheCoverMaps_GeoJson_AndTheCredits()
    {
        var features = await DetectAsync(surface: true);
        string folder = Path.Combine(Path.GetTempPath(), "features-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = FeatureFiles.Export(features, folder);
            Assert.Equal(5, files.Count);

            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FeatureFiles.JsonFile)));
            Assert.Equal(FeatureFiles.Format, json.RootElement.GetProperty("format").GetString());
            Assert.Equal(features.Trees.Count, json.RootElement.GetProperty("trees").GetArrayLength());
            Assert.Equal("track", json.RootElement.GetProperty("roads")[0].GetProperty("kind").GetString());
            Assert.Equal(FeatureFiles.CoverFile, json.RootElement.GetProperty("landCover").GetProperty("file").GetString());

            using var map = SKBitmap.Decode(Path.Combine(folder, FeatureFiles.CoverFile));
            Assert.Equal((200, 200), (map.Width, map.Height));
            // Gray values are the classes' numbers.
            Assert.Equal((byte)GroundCover.Gravel, map.GetPixel(50, 120).Red);

            using var geo = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, FeatureFiles.GeoJsonFile)));
            var items = geo.RootElement.GetProperty("features");
            int expected = features.Areas.Count + features.Buildings.Count + features.Roads.Count + features.Trees.Count + features.Rocks.Count;
            Assert.Equal(expected, items.GetArrayLength());
            var point = items.EnumerateArray().First(f => f.GetProperty("geometry").GetProperty("type").GetString() == "Point")
                .GetProperty("geometry").GetProperty("coordinates");
            Assert.InRange(point[0].GetDouble(), 7.86, 7.87);
            Assert.InRange(point[1].GetDouble(), 46.68, 46.69);

            Assert.Contains("© scene", File.ReadAllText(Path.Combine(folder, FeatureFiles.AttributionFile)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task MapRoads_AreSplitAtJunctions_JoinedWhereTheirTagsMatch_AndCutAtTheArea()
    {
        var features = await DetectAsync(true, new FeatureScene.FakeMap(FeatureScene.Map(s_origin)), RoadSource.OpenStreetMap);

        // West, east, the branch, the bridge and the path (two ways of the same tags); no road under construction.
        Assert.Equal(5, features.Roads.Count);
        Assert.All(features.Roads, r => Assert.Equal(FeatureOrigin.Map, r.Origin));
        var junction = Assert.Single(features.Junctions);
        Assert.Equal(0, junction.X, 2);
        Assert.Equal(20, junction.Z, 2);
        Assert.All(features.Roads.SelectMany(r => r.Points), p => Assert.Equal(PlaneSource.Plane(p.X, p.Z), p.Y, 1));

        // The track without a surface: gravel, as the images show; cut at the area's edges.
        var tracks = features.Roads.Where(r => r.Class == "track" && r.Tags!.Count == 1).ToList();
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t =>
        {
            Assert.Equal((RoadSurface.Gravel, RoadKind.Track, 3f), (t.Surface, t.Kind, t.Width));
            Assert.Equal(new[] { -1, 0 }, new[] { t.Start, t.End }.Order());
            Assert.Equal(100, t.Length, 0);
        });
        var branch = features.Roads.Single(r => r.Tags!.ContainsKey("width"));
        Assert.Equal((RoadSurface.Gravel, RoadKind.Track, 4f), (branch.Surface, branch.Kind, branch.Width));
        Assert.Equal(120, branch.Length, 0);

        var bridge = features.Roads.Single(r => r.Class == "residential");
        Assert.Equal((RoadStructure.Bridge, RoadKind.Road, RoadSurface.Paved), (bridge.Structure, bridge.Kind, bridge.Surface));

        // Over the meadow the images show no path: a path's usual dirt.
        var path = features.Roads.Single(r => r.Class == "path");
        Assert.Equal((RoadSurface.Dirt, RoadKind.Path, -1, -1), (path.Surface, path.Kind, path.Start, path.End));
        Assert.Equal(20 * Math.Sqrt(1.25) * 2 + 20, path.Length, 1);
        Assert.Equal("hiking", path.Tags!["sac_scale"]);
        // Points at most 5 m apart, so they follow the ground.
        Assert.All(features.Roads.SelectMany(r => r.Points.Zip(r.Points.Skip(1))), pair =>
            Assert.InRange(Vector2.Distance(new(pair.First.X, pair.First.Z), new(pair.Second.X, pair.Second.Z)), 0, 5.01));

        Assert.Contains("© fake map", features.Attribution);
        Assert.Contains(features.Notes, n => n.Contains("Open Database License"));
    }

    [Fact]
    public async Task Both_AddsTheImagesRoads_WhereTheMapHasNone()
    {
        var features = await DetectAsync(true, new FeatureScene.FakeMap(FeatureScene.Map(s_origin, branch: false)), RoadSource.Both);

        var traced = Assert.Single(features.Roads, r => r.Origin == FeatureOrigin.Image);
        // The branch north, which the map lacks; the road west to east, which it has, comes from the map alone.
        Assert.All(traced.Points, p => Assert.InRange(p.X, -3, 3));
        // Without the branch the track west to east is one road.
        Assert.Equal(3, features.Roads.Count(r => r.Origin == FeatureOrigin.Map));
        Assert.All(features.Roads, r => Assert.True(r.Start < features.Junctions.Count && r.End < features.Junctions.Count));
    }

    [Fact]
    public async Task AMapThatCantBeReached_LeavesTheRoadsToTheImages()
    {
        var features = await DetectAsync(true, new FeatureScene.FakeMap(null), RoadSource.OpenStreetMap);

        Assert.Equal(3, features.Roads.Count);
        Assert.All(features.Roads, r => Assert.Equal(FeatureOrigin.Image, r.Origin));
        Assert.Contains(features.Notes, n => n.Contains("couldn't be reached"));
    }

    [Fact]
    public async Task TheMapsBuildings_TakeTheSurfaceModelsHeights_OrTheirTags_AndReplaceTheImagesOnes()
    {
        var features = await DetectAsync(true, new FeatureScene.FakeMap(FeatureScene.Map(s_origin)), RoadSource.OpenStreetMap);

        // The mapped house and house of two floors; not the found house again, nor the bridge the mapped road crosses on.
        Assert.Equal(2, features.Buildings.Count);
        Assert.All(features.Buildings, b => Assert.Equal(FeatureOrigin.Map, b.Origin));
        var house = features.Buildings.Single(b => b.Class == "house");
        Assert.True(house.Measured);
        Assert.InRange(house.Height, 7.5, 8.5);
        Assert.Equal(RoofShape.Flat, house.Roof);
        Assert.Equal((15f, 10f), (house.Length, house.Width));
        Assert.Equal(PlaneSource.Plane(30, -50), house.Position.Y, 1);
        // In the meadow the surface model sees nothing: two floors and a roof, from the tags.
        var tagged = features.Buildings.Single(b => b.Class == "yes");
        Assert.False(tagged.Measured);
        Assert.Equal((9f, 6f, RoofShape.Pitched), (tagged.Height, tagged.Eaves, tagged.Roof));
        Assert.Equal("2", tagged.Tags!["building:levels"]);
    }

    [Fact]
    public async Task TheMapsAreasLinesLandmarksTreesAndStones_AreRead_WithTheGroundsHeights()
    {
        var features = await DetectAsync(true, new FeatureScene.FakeMap(FeatureScene.Map(s_origin)), RoadSource.OpenStreetMap);

        // The forest: the map's multipolygon (two outer ways joined, the clearing a hole), not the images' outline.
        var forest = Assert.Single(features.Areas, a => a.Kind == AreaKind.Forest);
        Assert.Equal((FeatureOrigin.Map, "landuse=forest"), (forest.Origin, forest.Class));
        Assert.Single(forest.Holes);
        Assert.Equal(50 * 50 - 10 * 10, forest.Size, 0);
        Assert.Contains(features.Areas, a => a.Kind == AreaKind.Scree && a.Origin == FeatureOrigin.Map);
        var pond = Assert.Single(features.Areas, a => a.Kind == AreaKind.Water);
        Assert.Equal("Pond", pond.Tags!["name"]);
        // The map's water and buildings in the cover map.
        Assert.Equal(GroundCover.Water, features.Covers!.At(-75, 80));
        Assert.Equal(GroundCover.Building, features.Covers.At(75, 74));

        var power = Assert.Single(features.Lines, l => l.Kind == LineKind.PowerLine);
        // The towers stay the points, cut at the area's edges.
        Assert.Equal(new[] { -100f, 0, 100 }, power.Points.Select(p => p.X));
        Assert.Equal(25, power.Height);
        var stream = Assert.Single(features.Lines, l => l.Kind == LineKind.Stream);
        Assert.Equal(RoadStructure.Tunnel, stream.Structure);
        Assert.Equal(7, stream.Points.Count);
        Assert.All(features.Lines.SelectMany(l => l.Points), p => Assert.Equal(PlaneSource.Plane(p.X, p.Z), p.Y, 1));

        var peak = Assert.Single(features.Landmarks, l => l.Kind == LandmarkKind.Peak);
        Assert.Equal(("Test Peak", 2000f), (peak.Name, peak.Elevation));
        Assert.Equal(PlaneSource.Plane(10, 10), peak.Position.Y, 1);
        Assert.Equal("Test Takeoff", Assert.Single(features.Landmarks, l => l.Kind == LandmarkKind.Takeoff).Name);

        // The oak in the meadow is added; the tree mapped where the forest's corner tree stands isn't, twice.
        var oak = Assert.Single(features.Trees, t => t.Origin == FeatureOrigin.Map && t.Tags?.GetValueOrDefault("genus") == "Quercus");
        Assert.Equal((12f, TreeKind.Broadleaf, false), (oak.Height, oak.Kind, oak.Estimated));
        Assert.Equal(6, features.Trees.Count(t => t.Origin == FeatureOrigin.Map && t.Position.Z == 80));
        Assert.Equal(49, features.Trees.Count(t => t.Origin == FeatureOrigin.Image && t.Kind == TreeKind.Conifer));
        Assert.Contains(features.Rocks, r => r.Origin == FeatureOrigin.Map && Math.Abs(r.Position.X + 30) < 0.1);
    }

    [Fact]
    public async Task WithoutASurfaceModel_TheMapsForestsTellTheImagesTrees_ConifersFromBroadleaf()
    {
        var features = await DetectAsync(false, new FeatureScene.FakeMap(FeatureScene.Map(s_origin)), RoadSource.OpenStreetMap);

        var inForest = features.Trees.Where(t => t.Origin == FeatureOrigin.Image && t.Position.X < -40 && t.Position.Z < -40).ToList();
        Assert.NotEmpty(inForest);
        Assert.All(inForest, t => Assert.True(t.Kind == TreeKind.Conifer || (t.Position.X > -70 && t.Position.X < -60 && t.Position.Z > -70 && t.Position.Z < -60)));
        // Buildings from the map alone, their heights from its tags or their kind.
        Assert.Equal(2, features.Buildings.Count);
        Assert.All(features.Buildings, b => Assert.False(b.Measured));
        Assert.Contains(features.Notes, n => n.Contains("only the map's buildings"));
    }

    [Fact]
    public void MapTags_GiveBuildingHeights_LineSizes_AndLandmarks()
    {
        Dictionary<string, string> Tags(params string[] pairs) => pairs.Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);

        Assert.Equal((12f, 12f, RoofShape.Flat), MapFeatures.BuildingHeights(Tags("building=commercial", "height=12")));
        Assert.Equal((15f, 12f, RoofShape.Pitched), MapFeatures.BuildingHeights(Tags("building=apartments", "building:levels=4", "roof:shape=gabled")));
        Assert.Equal((3f, 3f, RoofShape.Flat), MapFeatures.BuildingHeights(Tags("building=garage")));
        Assert.Equal((0f, 50f), MapFeatures.LineSize(LineKind.PowerLine, Tags("power=line", "voltage=380000;220000")));
        Assert.Equal((0f, 10f), MapFeatures.LineSize(LineKind.Aerialway, Tags("aerialway=chair_lift")));
        Assert.Equal((4f, 0f), MapFeatures.LineSize(LineKind.Stream, Tags("waterway=stream", "width=4")));
        Assert.Equal(LandmarkKind.Landing, MapFeatures.LandmarkKindOf(Tags("sport=free_flying", "free_flying:site=landing"), out _));
        Assert.Equal(LandmarkKind.Guidepost, MapFeatures.LandmarkKindOf(Tags("tourism=information", "information=guidepost"), out string kind));
        Assert.Equal("information=guidepost", kind);
        Assert.Equal(AreaKind.Scrub, MapFeatures.AreaKindOf(Tags("natural=scrub"), out _));
        Assert.Null(MapFeatures.AreaKindOf(Tags("natural=tree_row"), out _));
        Assert.Equal(LineKind.Aerialway, MapFeatures.LineKindOf(Tags("aerialway=gondola"), out _));
        Assert.Equal(TreeKind.Conifer, MapFeatures.TreeOf(Tags("natural=tree", "species=Picea abies")).Kind);
        Assert.Equal(TreeKind.Broadleaf, MapFeatures.TreeOf(Tags("natural=tree", "genus=Fagus")).Kind);
        Assert.Equal((null, 4f, TreeKind.Unknown), MapFeatures.TreeOf(Tags("natural=tree", "diameter_crown=8")));
    }

    [Fact]
    public void MapTags_GiveWidthSurfaceKindAndStructure()
    {
        Dictionary<string, string> Tags(params string[] pairs) => pairs.Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);

        Assert.Equal(4.5f, MapRoads.WidthOf(Tags("highway=track", "width=4.5 m")));
        Assert.Equal(6.5f, MapRoads.WidthOf(Tags("highway=residential", "width=6,5")));
        Assert.Equal(6.5f, MapRoads.WidthOf(Tags("highway=primary", "lanes=2")));
        Assert.Equal(1f, MapRoads.WidthOf(Tags("highway=path")));
        Assert.Equal(RoadSurface.Paved, MapRoads.SurfaceOf(Tags("highway=track", "surface=asphalt")));
        Assert.Equal(RoadSurface.Gravel, MapRoads.SurfaceOf(Tags("highway=track", "tracktype=grade2")));
        Assert.Equal(RoadSurface.Dirt, MapRoads.SurfaceOf(Tags("highway=path", "surface=ground")));
        Assert.Null(MapRoads.SurfaceOf(Tags("highway=path")));
        Assert.Equal(RoadKind.MainRoad, MapRoads.KindOf("secondary", RoadSurface.Paved));
        Assert.Equal(RoadKind.Track, MapRoads.KindOf("service", RoadSurface.Gravel));
        Assert.Equal(RoadKind.Road, MapRoads.KindOf("service", RoadSurface.Paved));
        Assert.Equal(RoadKind.Path, MapRoads.KindOf("cycleway", RoadSurface.Paved));
        Assert.Equal(RoadStructure.Tunnel, MapRoads.StructureOf(Tags("highway=primary", "tunnel=yes")));
        Assert.Equal(RoadStructure.Ground, MapRoads.StructureOf(Tags("highway=primary", "bridge=no")));
        Assert.False(MapRoads.IsRoad("construction"));
    }

    [Fact]
    public void OverpassAnswers_AreRead_AndErrorsRefused()
    {
        string json = """
            {"version":0.6,"elements":[
              {"type":"node","id":5,"lat":46.62,"lon":7.82,"tags":{"natural":"peak","name":"Top"}},
              {"type":"way","id":7,"nodes":[1,2],"geometry":[{"lat":46.6,"lon":7.8},{"lat":46.61,"lon":7.81}],"tags":{"highway":"track"}},
              {"type":"way","id":8,"nodes":[3,4],"geometry":[{"lat":46.6,"lon":7.8},null],"tags":{"highway":"path"}},
              {"type":"relation","id":9,"members":[
                {"type":"way","ref":10,"role":"outer","geometry":[{"lat":46.6,"lon":7.8},{"lat":46.6,"lon":7.81},{"lat":46.61,"lon":7.81},{"lat":46.6,"lon":7.8}]},
                {"type":"node","ref":11,"role":"label","lat":46.6,"lon":7.8}],"tags":{"type":"multipolygon","landuse":"forest"}}]}
            """;
        var data = OpenStreetMapSource.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        var way = Assert.Single(data.Ways);
        Assert.Equal((7L, "track"), (way.Id, way.Tags["highway"]));
        Assert.Equal(new GeoPoint(46.61, 7.81), way.Points[1]);
        Assert.Equal("Top", Assert.Single(data.Nodes).Tags["name"]);
        var relation = Assert.Single(data.Relations);
        Assert.Equal(("outer", 4), (Assert.Single(relation.Members).Role, relation.Members[0].Points.Count));

        string timedOut = """{"elements":[],"remark":"runtime error: Query timed out in "query" at line 1 after 81 seconds."}""";
        Assert.Throws<InvalidDataException>(() => OpenStreetMapSource.Parse(System.Text.Encoding.UTF8.GetBytes(timedOut)));
    }

    [Fact]
    public void Trace_FindsOutlinesAndHoles_JoiningDiagonalNeighbors()
    {
        // A 6 × 6 square with a 2 × 2 hole, and two pixels touching at a corner.
        int w = 12, h = 8;
        var mask = new bool[w * h];
        for (int y = 1; y <= 6; y++) for (int x = 1; x <= 6; x++) mask[y * w + x] = !(x is 3 or 4 && y is 3 or 4);
        mask[2 * w + 9] = true;
        mask[3 * w + 10] = true;

        var polygons = Polygons.Trace(mask, w, h);
        Assert.Equal(2, polygons.Count);
        var square = polygons.Single(p => p.Holes.Count == 1);
        Assert.Equal(-36, Polygons.SignedArea(square.Outline));
        Assert.Equal(4, square.Outline.Count);
        Assert.Equal(4, Polygons.SignedArea(square.Holes[0]));
        var diagonal = polygons.Single(p => p.Holes.Count == 0);
        Assert.Equal(-2, Polygons.SignedArea(diagonal.Outline));
    }

    [Fact]
    public void MinimumRectangle_FitsATurnedRectangle()
    {
        var direction = Vector2.Normalize(new Vector2(1, 1));
        var across = new Vector2(-direction.Y, direction.X);
        var center = new Vector2(10, 5);
        var corners = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }.Select(c => center + direction * 6 * c.Item1 + across * 2 * c.Item2);
        var rectangle = Polygons.MinimumRectangle(corners);
        Assert.Equal(12, rectangle.Length, 3);
        Assert.Equal(4, rectangle.Width, 3);
        Assert.Equal(center.X, rectangle.Center.X, 3);
        Assert.Equal(1, Math.Abs(Vector2.Dot(rectangle.Direction, direction)), 3);
    }

    [Fact]
    public void RoadKinds_FollowWidthAndSurface()
    {
        Assert.Equal(RoadKind.MainRoad, RoadFeature.KindOf(7, RoadSurface.Paved));
        Assert.Equal(RoadKind.Road, RoadFeature.KindOf(4, RoadSurface.Paved));
        Assert.Equal(RoadKind.Path, RoadFeature.KindOf(2, RoadSurface.Paved));
        Assert.Equal(RoadKind.Track, RoadFeature.KindOf(3.5, RoadSurface.Gravel));
        Assert.Equal(RoadKind.Path, RoadFeature.KindOf(2.5, RoadSurface.Dirt));
    }

    [Fact]
    public void ClassMaps_AreEightBitGrayPngs()
    {
        var values = new byte[] { 0, 1, 2, 3, 250, 11 };
        using var bitmap = SKBitmap.Decode(Heightmaps.ToPng(values, 3, 2));
        Assert.Equal((3, 2), (bitmap.Width, bitmap.Height));
        Assert.Equal(250, bitmap.GetPixel(1, 1).Red);
        Assert.Equal(11, bitmap.GetPixel(2, 1).Red);
    }
}
