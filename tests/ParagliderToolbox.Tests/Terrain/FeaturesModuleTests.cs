using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Core;
using ParagliderToolbox.Modules.Features;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Features;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Tests.Terrain;

public class FeaturesModuleTests
{
    private static Toolbox CreateToolbox()
    {
        var toolbox = new Toolbox();
        var terrains = new TerrainModule();
        toolbox.Initialize([new CoreModule(), terrains, new FeaturesModule(terrains)]);
        toolbox.Dialogs = new FakeDialogs();
        return toolbox;
    }

    private static Task<FeatureSet> SceneFeaturesAsync() =>
        new FeatureDetector(FeatureScene.Sources(surface: true)).DetectAsync(new GeoPoint(46.6863, 7.8632), FeatureArea.Around(0, 0, 200),
            new FeatureSettings { RoadsFrom = RoadSource.Images, Map = false });

    [Fact]
    public void Features_GoUnderTerrains_AndNowhereElse()
    {
        var toolbox = CreateToolbox();
        var terrain = new TerrainNode();
        var folder = new FolderNode();
        toolbox.Document.Project.Children.Add(terrain);
        toolbox.Document.Project.Children.Add(folder);
        var type = toolbox.NodeTypes.Find("features")!;

        Assert.True(terrain.CanContain(typeof(FeaturesNode)));
        Assert.False(folder.CanContain(typeof(FeaturesNode)));
        toolbox.SelectedNode = folder;
        Assert.False(toolbox.ProjectCommands.CanAddNode(type));
        toolbox.SelectedNode = terrain;
        Assert.Same(terrain, toolbox.ProjectCommands.TargetFor(type));
    }

    [Fact]
    public async Task FeaturesAndTheirResult_RoundTripThroughTheProjectFile()
    {
        var toolbox = CreateToolbox();
        var terrain = new TerrainNode { Size = 4 };
        var node = new FeaturesNode
        {
            Name = "Village", Center = "46.6870, 7.8640", Size = 600, Detail = FeatureDetail.Fine, Rocks = false, MinTreeHeight = 4, RoadsFrom = RoadSource.Both,
            Map = false, Lines = false,
        };
        terrain.Children.Add(node);
        toolbox.Document.Project.Children.Add(terrain);
        node.Result = await SceneFeaturesAsync();

        var loaded = toolbox.Serializer.Deserialize(toolbox.Serializer.Serialize(toolbox.Document.Project));

        var copy = Assert.IsType<FeaturesNode>(Assert.Single(Assert.IsType<TerrainNode>(Assert.Single(loaded.Children)).Children));
        Assert.Equal(("Village", "46.6870, 7.8640", 600.0, FeatureDetail.Fine, false, 4.0, RoadSource.Both),
            (copy.Name, copy.Center, copy.Size, copy.Detail, copy.Rocks, copy.MinTreeHeight, copy.RoadsFrom));
        Assert.Equal((RoadSource.Both, false, false, true), (copy.Settings().RoadsFrom, copy.Settings().Map, copy.Settings().Lines, copy.Settings().Landmarks));
        var result = Assert.IsType<FeatureSet>(copy.Result);
        Assert.Equal(node.Result.Trees.Count, result.Trees.Count);
        Assert.Equal(node.Result.Roads.Count, result.Roads.Count);
        Assert.Equal(node.Result.Buildings.Count, result.Buildings.Count);
        // The saved result is of the saved settings.
        Assert.False(copy.IsOutdated);
        Assert.Contains("49", copy.TreeSummary);
    }

    [Fact]
    public async Task ChangingASetting_MakesTheResultOutdated()
    {
        var node = new FeaturesNode { Result = await SceneFeaturesAsync() };
        Assert.False(node.IsOutdated);
        node.Detail = FeatureDetail.Quick;
        Assert.True(node.IsOutdated);
        node.Result = node.Result;
        Assert.True(node.IsOutdated);
        node.Result = await SceneFeaturesAsync();
        Assert.False(node.IsOutdated);
    }

    [Fact]
    public void TheArea_IsTheSquareAroundTheCenter_InsideTheTerrain()
    {
        var terrain = new TerrainNode(new TerrainSettings { Center = new GeoPoint(46.6863, 7.8632), Size = 2048, Resolution = 4, TileSamples = 129 });
        var node = new FeaturesNode { Size = 500 };
        terrain.Children.Add(node);

        var area = node.Area(terrain);
        Assert.Equal((-250.0, -250.0, 500.0, 500.0), (area.X0, area.Z0, area.Width, area.Depth));

        // 900 m east of the center: cut at the terrain's east edge (1024 m).
        var east = new LocalFrame(terrain.Snapshot().Center).ToGeo(900, 0);
        node.Center = $"{east.Latitude:0.000000}, {east.Longitude:0.000000}";
        area = node.Area(terrain);
        Assert.Equal(650, area.X0, 0);
        Assert.Equal(1024, area.X0 + area.Width, 0);

        // Not a position: the last valid one stays.
        node.Center = "somewhere";
        Assert.NotEqual("somewhere", node.Center);
        Assert.Equal(0.5, FeaturesNode.SpacingOf(node.Detail));
    }

    [Fact]
    public async Task TheOverlays_HaveGeometryForEveryKindOfFeature()
    {
        var origin = new GeoPoint(46.6863, 7.8632);
        var features = await new FeatureDetector(FeatureScene.Sources(surface: true, new FeatureScene.FakeMap(FeatureScene.Map(origin))))
            .DetectAsync(origin, FeatureArea.Around(0, 0, 200), new FeatureSettings());
        var geometry = FeaturesScene.Build(features, FeatureArea.Around(0, 0, 200), new FeatureViewOptions { LandCover = true }, (x, z) => PlaneSource.Plane(x, z));

        Assert.Equal(features.Trees.Count * 8, geometry.Trees.Positions.Length);
        Assert.NotEmpty(geometry.Roads.Indices);
        Assert.Equal(features.Junctions.Count, geometry.Junctions.Positions.Length);
        Assert.NotEmpty(geometry.Buildings.Indices);
        Assert.Equal(features.Rocks.Count * 6, geometry.Rocks.Positions.Length);
        Assert.NotEmpty(geometry.Areas.Indices);
        Assert.NotEmpty(geometry.LandCover.Indices);
        Assert.NotEmpty(geometry.Bounds.Indices);
        // The power line's three towers as posts and the wires between them; the culverted stream isn't drawn.
        Assert.Equal(3 * 2 + 2 * 8 * 2, geometry.Wires.Positions.Length);
        Assert.Empty(geometry.Strips.Indices);
        Assert.Equal(features.Landmarks.Count, geometry.PinHeads.Positions.Length);
        // Everything sits on (or a little above) the terrain; a building on a slope stands on its lowest corner.
        Assert.All(geometry.Roads.Positions, p => Assert.InRange(p.Y - PlaneSource.Plane(p.X, p.Z), 1, 1.5));
        Assert.All(geometry.Trees.Positions, p => Assert.InRange(p.Y - PlaneSource.Plane(p.X, p.Z), 0, 16));
        Assert.All(geometry.Buildings.Positions, p => Assert.InRange(p.Y - PlaneSource.Plane(p.X, p.Z), -2.5, 10));

        var outline = FeaturesScene.Build(null, FeatureArea.Around(0, 0, 200), new FeatureViewOptions(), (x, z) => PlaneSource.Plane(x, z));
        Assert.NotEmpty(outline.Bounds.Indices);
        Assert.Empty(outline.Trees.Indices);
    }
}
