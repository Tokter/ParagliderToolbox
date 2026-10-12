using System.Text.Json.Nodes;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Atmosphere;
using ParagliderToolbox.Airflow.Surface;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Airflow;
using ParagliderToolbox.Modules.Core;
using ParagliderToolbox.Modules.Terrain;

namespace ParagliderToolbox.Tests.Airflow;

public class AirflowModuleTests
{
    private static Toolbox CreateToolbox(FakeDialogs? dialogs = null)
    {
        var toolbox = new Toolbox();
        var terrains = new TerrainModule();
        toolbox.Initialize([new CoreModule(), terrains, new AirflowModule(terrains)]);
        toolbox.Dialogs = dialogs ?? new FakeDialogs();
        return toolbox;
    }

    // A quick airflow over a small hill: 15 × 15 columns, half a minute.
    private static AirflowField SmallField()
    {
        var terrain = HeightField.Sample(-750, -750, 25, 61, 61, (x, z) => (float)(900 + 200 * Math.Exp(-(x * x + z * z) / (2 * 300.0 * 300))));
        var settings = new AirflowSettings { Duration = 30, AveragingTime = 15, TopClearance = 1000, Sounding = SoundingPresets.Find("spring-unstable").Sounding };
        return new AirflowSimulation(terrain, null, settings).Run();
    }

    [Fact]
    public void Airflows_GoUnderTerrains_AndNowhereElse()
    {
        var toolbox = CreateToolbox();
        var terrain = new TerrainNode();
        var folder = new FolderNode();
        toolbox.Document.Project.Children.Add(terrain);
        toolbox.Document.Project.Children.Add(folder);
        var type = toolbox.NodeTypes.Find("airflow")!;

        Assert.True(terrain.CanContain(typeof(AirflowNode)));
        Assert.False(toolbox.Document.Project.CanContain(typeof(AirflowNode)));
        Assert.False(folder.CanContain(typeof(AirflowNode)));
        // Terrains still go anywhere, and hold nothing else.
        Assert.True(folder.CanContain(typeof(TerrainNode)));
        Assert.False(terrain.CanContain(typeof(TerrainNode)));

        toolbox.SelectedNode = folder;
        Assert.False(toolbox.ProjectCommands.CanAddNode(type));
        toolbox.SelectedNode = terrain;
        Assert.Same(terrain, toolbox.ProjectCommands.TargetFor(type));
    }

    [Fact]
    public async Task AddingAnAirflow_AsksForItsWeather_AndTakesItsTerrainsSize()
    {
        var dialogs = new FakeDialogs();
        var toolbox = CreateToolbox(dialogs);
        var terrain = new TerrainNode { Size = 4 };
        toolbox.Document.Project.Children.Add(terrain);
        toolbox.SelectedNode = terrain;

        var node = Assert.IsType<AirflowNode>(await toolbox.ProjectCommands.AddNodeInteractivelyAsync(toolbox.NodeTypes.Find("airflow")!));

        Assert.Contains("Dialog:New airflow", dialogs.Asked);
        Assert.Same(terrain, node.Parent);
        Assert.Equal("summer-classic", node.SoundingId);
        Assert.Equal("15 July", node.Date);
        Assert.Equal(2, node.UtcOffset);
        Assert.Equal(AirflowDetail.Normal, node.Detail);
        // About 100 cells across the terrain.
        Assert.Equal(AirflowNode.DetailSettings(AirflowDetail.Normal, terrain.Layout.Extent).CellSize, node.CellSize);
        Assert.InRange(node.CellSize, 35, 45);
        var settings = node.Settings(terrain);
        Assert.Equal(terrain.Latitude, settings.Latitude);
        Assert.Equal(new DateTime(2026, 7, 15, 12, 0, 0), settings.UtcTime);
    }

    [Fact]
    public void AnAirflow_RoundTripsThroughTheProjectFile_WithItsResult()
    {
        var toolbox = CreateToolbox();
        var terrain = new TerrainNode { Size = 4 };
        toolbox.Document.Project.Children.Add(terrain);
        var node = new AirflowNode { Name = "Föhn day" };
        terrain.Children.Add(node);
        node.Sounding = new SoundingChoice("foehn");
        node.Detail = AirflowDetail.Quick;
        node.Time = "15:30";
        node.CloudShadows = false;
        node.Result = SmallField();
        Assert.False(node.IsOutdated);

        var loaded = toolbox.Serializer.Deserialize(toolbox.Serializer.Serialize(toolbox.Document.Project));

        var copy = Assert.IsType<AirflowNode>(Assert.Single(Assert.IsType<TerrainNode>(Assert.Single(loaded.Children)).Children));
        Assert.Equal("Föhn day", copy.Name);
        Assert.Equal("foehn", copy.SoundingId);
        // The föhn's south wind and its season came with the preset.
        Assert.Equal((180.0, 50.0), (copy.WindDirection, copy.WindSpeed));
        Assert.Equal("25 March", copy.Date);
        Assert.Equal("15:30", copy.Time);
        Assert.False(copy.CloudShadows);
        // Loading keeps the saved cell size (it isn't computed again from a terrain the node doesn't have yet).
        Assert.Equal(AirflowDetail.Quick, copy.Detail);
        Assert.Equal(node.CellSize, copy.CellSize);
        Assert.Equal(node.Duration, copy.Duration);
        Assert.NotNull(copy.Result);
        Assert.Equal((node.Result.Nx, node.Result.Ny, node.Result.Nz), (copy.Result.Nx, copy.Result.Ny, copy.Result.Nz));
        Assert.Equal(node.Result.Info.Sounding, copy.Result.Info.Sounding);
        Assert.False(copy.IsOutdated);
    }

    [Fact]
    public void ProjectsWithThermalsSimulatedInBoxes_LoadWithoutThem()
    {
        var toolbox = CreateToolbox();
        var terrain = new TerrainNode { Size = 4 };
        toolbox.Document.Project.Children.Add(terrain);
        terrain.Children.Add(new AirflowNode { Name = "Summer" });
        // Such a project held thermal nodes under its airflows.
        var file = JsonNode.Parse(toolbox.Serializer.Serialize(toolbox.Document.Project))!;
        var airflow = file["project"]!["children"]![0]!["children"]![0]!.AsObject();
        airflow["children"] = new JsonArray(new JsonObject { ["$type"] = "thermal", ["id"] = Guid.NewGuid().ToString(), ["name"] = "Thermal 1", ["recording"] = "AAAA" });

        var loaded = toolbox.Serializer.Deserialize(file.ToJsonString());

        var copy = Assert.IsType<AirflowNode>(Assert.Single(Assert.IsType<TerrainNode>(Assert.Single(loaded.Children)).Children));
        Assert.Equal("Summer", copy.Name);
        Assert.Empty(copy.Children);
    }

    [Fact]
    public void AnAirflowsView_ShowsTheSun_AndTheBaseFlowOfARunThatFollowedItsThermals()
    {
        var toolbox = CreateToolbox();
        var airflow = new AirflowNode { Result = SmallField() };
        var preview = Assert.IsType<AirflowPreview>(toolbox.DetailViews.Create(airflow)!.DataContext);
        Assert.False(preview.HasBaseFlow);
        preview.ToggleSunCommand.Execute(null);
        Assert.StartsWith("Sun from ", preview.SunText);

        // A run that followed its thermals has a base flow: B shows it instead of the mean.
        var followed = SmallField();
        int cells = followed.WindX.Length;
        followed.BaseWindX = new float[cells];
        followed.BaseWindY = Enumerable.Repeat(-0.4f, cells).ToArray();
        followed.BaseWindZ = new float[cells];
        followed.Convection = new ParagliderToolbox.Airflow.Convection.ConvectionClimate { Start = 900, End = 1800, Interval = 30 };
        airflow.Result = followed;
        Assert.True(preview.HasBaseFlow);
        preview.ToggleBaseFlowCommand.Execute(null);
        Assert.True(preview.WithoutThermals);
        Assert.Same(followed.BaseWindY, preview.Field!.WindY);
        Assert.Contains("base flow", preview.Status);
        preview.ToggleBaseFlowCommand.Execute(null);
        Assert.Same(followed.WindY, preview.Field!.WindY);
    }

    [Fact]
    public void AnAirflowsView_PlaysItsThermalsAsAGame_OverTheBaseFlow()
    {
        var toolbox = CreateToolbox();
        var airflow = new AirflowNode { Result = SmallField() };
        var preview = Assert.IsType<AirflowPreview>(toolbox.DetailViews.Create(airflow)!.DataContext);
        // A mean alone has no thermals to play.
        Assert.False(preview.HasGame);
        preview.ToggleGameCommand.Execute(null);
        Assert.False(preview.ShowGame);

        var followed = ConvectionModelTests.Field(ConvectionModelTests.Climate());
        airflow.Result = followed;
        Assert.True(preview.HasGame);
        preview.ToggleGameCommand.Execute(null);
        Assert.True(preview.ShowGame && preview.GamePlaying);
        Assert.False(preview.OverTime);
        var model = Assert.IsType<ParagliderToolbox.Airflow.Convection.ConvectionModel>(preview.Model);
        // Under the game, the base flow (the model's wind is it with the thermals).
        Assert.Same(followed.BaseWindY, preview.Field!.WindY);
        Assert.Contains("thermals", preview.GameText);
        Assert.Contains("the run had", preview.GameText);

        // At 30 times real time, a second plays half a minute.
        double before = model.Time;
        Assert.True(preview.Advance(1));
        Assert.Equal(before + 30, model.Time, 6);
        preview.SlowerCommand.Execute(null);
        Assert.Equal("×15", preview.GameSpeedText);
        preview.FasterCommand.Execute(null);
        preview.FasterCommand.Execute(null);
        Assert.Equal(60, preview.GameSpeed);
        preview.TogglePlayCommand.Execute(null);
        Assert.False(preview.GamePlaying);
        Assert.False(preview.Advance(1));
        // Another sky: another model.
        preview.RestartCommand.Execute(null);
        Assert.NotSame(model, preview.Model);

        // The run over time instead, or another result, ends the game.
        preview.ToggleGameCommand.Execute(null);
        preview.ToggleGameCommand.Execute(null);
        Assert.True(preview.ShowGame);
        preview.OverTime = true;
        Assert.False(preview.ShowGame);
        Assert.Null(preview.Model);
        preview.ShowGame = true;
        airflow.Result = SmallField();
        Assert.False(preview.ShowGame);
    }

    [Fact]
    public void AnAirflowsResult_SumsUpTheThermalsAndCumulusItFollowed()
    {
        var node = new AirflowNode { Result = SmallField() };
        Assert.StartsWith("not followed", node.Followed);
        Assert.Equal("–", node.ThermalLives);
        Assert.Equal("–", node.BaseFlow);

        var field = SmallField();
        int cells = field.WindX.Length;
        field.BaseWindX = new float[cells];
        field.BaseWindY = Enumerable.Repeat(-0.4f, cells).ToArray();
        field.BaseWindZ = new float[cells];
        // Two thermals in the hour followed (one there from its start), one feeding a cumulus.
        field.Convection = new ParagliderToolbox.Airflow.Convection.ConvectionClimate
        {
            Start = 1800, End = 5400, Interval = 30, Area = 1e8,
            Thermals =
            [
                new(0, 1800, 600, 0, 0, 900, 10, 2000, 2.5f, 4, 300, 0, 0, 2, 0, 0, true),
                new(1, 2400, 300, 100, 0, 900, 10, 1900, 2, 3.5f, 200, 0, 0, 2, 0, -1, false),
            ],
            Clouds = [new(0, 2100, 480, 50, 0, 2200, 2500, 400, 30, 120, 0, false)],
            Sites = [new(50, 0, 900, 2, 300, 2.2f, 3.7f, 250, 1950, 0.5f, 0, 0, 2)],
        };
        node.Result = field;
        Assert.Equal("2 thermals and 1 cumulus in 60 min", node.Followed);
        Assert.StartsWith("1 an hour, living 5 min", node.ThermalLives);
        Assert.Contains("climbing 2.0 m/s (to 3.5), 200 m wide", node.ThermalLives);
        Assert.StartsWith("1 an hour, living 8 min (growing 2 min), base 2", node.CloudLives);
        Assert.Contains("50", node.CloudLives);
        Assert.Equal("1, the busiest 2 an hour", node.Sites);
        Assert.Contains("rises −0.40 m/s on average", node.BaseFlow);
    }

    [Fact]
    public void AnAirflowsView_PlaysItsRunOverTime()
    {
        var toolbox = CreateToolbox();
        var airflow = new AirflowNode();
        var film = FilmTests.SmallFilm();
        airflow.SetResult(SmallField(), film);
        var preview = Assert.IsType<AirflowPreview>(toolbox.DetailViews.Create(airflow)!.DataContext);
        Assert.Same(film, preview.Film);
        Assert.True(preview.HasFilm && preview.ShowsFilm);
        Assert.Equal((30.0, 90.0), (preview.FilmStart, preview.FilmEnd));
        // It starts at the end (what the run ended with); played from there it starts over.
        Assert.Equal(90, preview.FilmTime);
        Assert.Equal("1:30 / 1:30", preview.FilmClock);
        preview.TogglePlayCommand.Execute(null);
        Assert.True(preview.IsPlaying);
        Assert.Equal(30, preview.FilmTime);
        // At 60 times real time, half a second plays half a minute; at the end it stops.
        Assert.True(preview.Advance(0.5));
        Assert.Equal(60, preview.FilmTime, 3);
        Assert.False(preview.Advance(1));
        Assert.False(preview.IsPlaying);
        Assert.Equal(90, preview.FilmTime);
        preview.SlowerCommand.Execute(null);
        Assert.Equal("×30", preview.SpeedText);
        preview.PreviousFrameCommand.Execute(null);
        Assert.Equal(60, preview.FilmTime);
        preview.RestartCommand.Execute(null);
        Assert.Equal(30, preview.FilmTime);
        preview.Seek(500);
        Assert.Equal(90, preview.FilmTime);

        // The mean instead: no timeline; a new result drops the film.
        preview.ToggleOverTimeCommand.Execute(null);
        Assert.False(preview.ShowsFilm);
        Assert.False(preview.Advance(1));
        airflow.Result = SmallField();
        Assert.Null(airflow.Film);
        Assert.False(preview.HasFilm);
    }

    [Fact]
    public void TheThermalsDetail_ResolvesThermals_WithCloudShadowsAndTurbulentInflow()
    {
        // 200 cells across, an hour and a half, and room above for the cumulus.
        Assert.Equal((50.0, 25.0, 90.0, 60.0, 3500.0), AirflowNode.DetailSettings(AirflowDetail.Thermals, 10240));
        var node = new AirflowNode();
        Assert.True(node.CloudShadows);
        Assert.Equal(0, node.Settings(null).InflowTurbulence);
        node.Detail = AirflowDetail.Thermals;
        var settings = node.Settings(null);
        Assert.Equal((50.0, 90 * 60.0, 60 * 60.0, 3500.0), (settings.CellSize, settings.Duration, settings.AveragingTime, settings.TopClearance));
        node.Detail = AirflowDetail.Normal;
        Assert.Equal(2500, node.TopClearance);
        node.Detail = AirflowDetail.Thermals;
        Assert.True(settings.CloudShadows);
        Assert.Equal(0.3, settings.InflowTurbulence);
        // Its thermals and clouds are followed, and the base flow between them kept.
        Assert.True(settings.TrackConvection);
        Assert.False(new AirflowNode().Settings(null).TrackConvection);
        node.CloudShadows = false;
        Assert.False(node.Settings(null).CloudShadows);
    }

    [Fact]
    public void EditingASetting_OutdatesTheResult_AndAnExplicitOneMakesTheDetailCustom()
    {
        var node = new AirflowNode { Result = SmallField() };
        Assert.False(node.IsOutdated);
        node.WindSpeed = 25;
        Assert.True(node.IsOutdated);
        Assert.Equal(AirflowDetail.Normal, node.Detail);
        node.CellSize = 70;
        Assert.Equal(AirflowDetail.Custom, node.Detail);
        node.Detail = AirflowDetail.Fine;
        Assert.Equal(AirflowNode.DetailSettings(AirflowDetail.Fine, 10240).CellSize, node.CellSize);
        Assert.Contains("thermals to", node.Expected);
        Assert.Contains("high", node.Sun);
    }

    [Theory]
    [InlineData("15 July", 7, 15)]
    [InlineData("15 Jul", 7, 15)]
    [InlineData("15.7.", 7, 15)]
    [InlineData("2026-03-25", 3, 25)]
    public void Dates_AreReadAsTyped(string text, int month, int day)
    {
        Assert.True(AirflowNode.TryParseDate(text, out var date));
        Assert.Equal((month, day), (date.Month, date.Day));
    }

    [Fact]
    public void Times_AreReadAsTyped()
    {
        Assert.True(AirflowNode.TryParseTime("14:30", out double hours));
        Assert.Equal(14.5, hours);
        Assert.True(AirflowNode.TryParseTime("9", out hours));
        Assert.Equal(9, hours);
        Assert.False(AirflowNode.TryParseTime("25", out _));
        Assert.False(AirflowNode.TryParseTime("noon", out _));
    }

    [Theory]
    [InlineData(46.7, 7.9, 7, 15, 2)]   // Interlaken in summer
    [InlineData(46.7, 7.9, 1, 20, 1)]   // and in winter
    [InlineData(46.7, 7.9, 3, 25, 1)]   // before the last Sunday of March 2026 (the 29th)
    [InlineData(38.7, -9.1, 7, 1, 1)]   // Lisbon
    [InlineData(36.5, 29.1, 7, 1, 3)]   // Ölüdeniz
    [InlineData(40.0, -105.0, 7, 1, -7)] // Colorado: the longitude's hour
    public void UtcOffsets_FollowEuropeanSummerTime(double latitude, double longitude, int month, int day, double offset) =>
        Assert.Equal(offset, NewAirflowOptions.UtcOffset(latitude, longitude, month, day));
}
