using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Core;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Geodesy;

namespace ParagliderToolbox.Tests.Terrain;

public class TerrainModuleTests
{
    private static Toolbox CreateToolbox(FakeDialogs? dialogs = null)
    {
        var toolbox = new Toolbox();
        toolbox.Initialize([new CoreModule(), new TerrainModule()]);
        toolbox.Dialogs = dialogs ?? new FakeDialogs();
        return toolbox;
    }

    [Fact]
    public void Terrain_RoundTripsThroughTheProjectFile()
    {
        var toolbox = CreateToolbox();
        var node = new TerrainNode
        {
            Name = "Fiesch",
            Coordinates = "46.4060, 8.1250",
            Size = 12,
            DetailSize = 3,
            Resolution = 2,
            TileSamples = 257,
            LodLevels = 4,
            Texture = TerrainTexture.ElevationColors,
            TextureSize = 1024,
            Shading = TerrainShading.Faceted,
            ElevationSource = new SourceChoice(SourceKind.Elevation, "copernicus-glo30"),
        };
        toolbox.Document.Project.Children.Add(node);

        var loaded = toolbox.Serializer.Deserialize(toolbox.Serializer.Serialize(toolbox.Document.Project));

        var copy = Assert.IsType<TerrainNode>(Assert.Single(loaded.Children));
        Assert.Equal("Fiesch", copy.Name);
        Assert.Equal(node.Snapshot(), copy.Snapshot());
        Assert.Equal("copernicus-glo30", copy.ElevationSource.Id);
        Assert.Null(copy.ImagerySource.Id);
    }

    [Fact]
    public void Coordinates_AcceptPastedPositions_AndKeepTheLastValidOne()
    {
        var node = new TerrainNode();
        node.Coordinates = "45.8130, 6.2460";
        Assert.Equal(45.813, node.Latitude, 9);
        Assert.Equal(6.246, node.Longitude, 9);
        node.Coordinates = "45.8";
        Assert.Equal(6.246, node.Longitude, 9);
        node.Coordinates = "2'632'500 1'169'500";
        Assert.InRange(node.Latitude, 46.67, 46.68);
        Assert.StartsWith("2'632'5", node.SwissCoordinates);
    }

    [Fact]
    public void ChangingASetting_BumpsTheVersion_AndTheSummary()
    {
        var node = new TerrainNode();
        int version = node.SettingsVersion;
        int tiles = node.TileCount;
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.Size = 20;

        Assert.True(node.SettingsVersion > version);
        Assert.True(node.TileCount > tiles);
        Assert.Contains(nameof(TerrainNode.TileCount), changed);
        node.TileSamples = 200;
        Assert.Equal(257, node.TileSamples);
        node.TextureSize = 700;
        Assert.Equal(512, node.TextureSize);
    }

    [Fact]
    public async Task NewTerrain_TakesTheLocationAndStyle_FromTheDialog()
    {
        var dialogs = new FakeDialogs
        {
            DialogAction = content =>
            {
                var options = (NewTerrainOptions)((Atelier.Controls.ContentControl)content).DataContext!;
                options.Choose(NewTerrainOptions.Sites.Single(s => s.Name.StartsWith("Annecy")));
                options.Style = NewTerrainOptions.Styles[0];
            },
        };
        var toolbox = CreateToolbox(dialogs);
        var node = await TerrainModule.AskNewTerrainAsync(toolbox);

        Assert.NotNull(node);
        Assert.StartsWith("Annecy", node.Name);
        Assert.Equal(45.813, node.Latitude, 6);
        Assert.Equal(TerrainShading.Faceted, node.Shading);
        Assert.Equal(new GeoPoint(45.813, 6.246), node.Snapshot().Center);
    }

    [Fact]
    public void NewTerrainOptions_ExplainTheLocation()
    {
        var options = new NewTerrainOptions();
        Assert.True(options.IsValid);
        Assert.Contains("Switzerland", options.LocationInfo);
        options.Location = "nowhere";
        Assert.False(options.IsValid);
        Assert.Null(options.CreateNode());
        Assert.All(NewTerrainOptions.Styles, s => Assert.Contains("triangles", NewTerrainOptions.Summary(s)));
    }

    [Fact]
    public void Module_RegistersTheTerrainAndItsCommands()
    {
        var toolbox = CreateToolbox();
        var type = toolbox.Type<TerrainNode>();
        Assert.Equal("terrain", type.Id);
        Assert.NotNull(type.CreateInteractively);
        Assert.Contains(toolbox.GlobalCommands, c => c.Group == TerrainActions.Group);
    }
}
