using Atelier.Core.Inspection;
using Atelier.Core.Primitives;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Core;
using ParagliderToolbox.Modules.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Tests.Paraglider;

public class ParagliderModuleTests
{
    private static Toolbox CreateToolbox(FakeDialogs? dialogs = null)
    {
        var toolbox = new Toolbox();
        toolbox.Initialize([new CoreModule(), new ParagliderModule()]);
        toolbox.Dialogs = dialogs ?? new FakeDialogs();
        return toolbox;
    }

    [Fact]
    public void Paraglider_RoundTripsThroughTheProjectFile()
    {
        var toolbox = CreateToolbox();
        var node = new ParagliderNode
        {
            Name = "Speed wing",
            FlatArea = 18,
            FlatAspectRatio = 4.6,
            CellCount = 38,
            Pattern = CanopyPattern.Stripes,
            ProxyComplexity = ProxyComplexity.Arcade,
            ChordDistribution = new Curve(0, 1, 0.5, 0.9, 1, 0.4),
            PrimaryColor = Color.FromRgb(0x12, 0x34, 0x56),
            BrandText = "FAST",
        };
        toolbox.Document.Project.Children.Add(node);

        var loaded = toolbox.Serializer.Deserialize(toolbox.Serializer.Serialize(toolbox.Document.Project));

        var copy = Assert.IsType<ParagliderNode>(Assert.Single(loaded.Children));
        Assert.Equal("Speed wing", copy.Name);
        Assert.Equal(18, copy.FlatArea);
        Assert.Equal(4.6, copy.FlatAspectRatio);
        Assert.Equal(38, copy.CellCount);
        Assert.Equal(CanopyPattern.Stripes, copy.Pattern);
        Assert.Equal(ProxyComplexity.Arcade, copy.ProxyComplexity);
        Assert.Equal(node.ChordDistribution, copy.ChordDistribution);
        Assert.Equal("#123456", copy.PrimaryColorHex);
        Assert.Equal("FAST", copy.BrandText);
        Assert.Equal(node.Snapshot().FlatSpan, copy.Snapshot().FlatSpan, 9);
    }

    [Fact]
    public void ChangingTheDesign_BumpsTheVersion_AndTheComputedDimensions()
    {
        var node = new ParagliderNode();
        int version = node.DesignVersion;
        double span = node.ProjectedSpan;
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.FlatArea = 28;

        Assert.True(node.DesignVersion > version);
        Assert.True(node.ProjectedSpan > span);
        Assert.Contains(nameof(ParagliderNode.FlatSpan), changed);
        Assert.Contains(nameof(ParagliderNode.ProjectedSpan), changed);
    }

    [Fact]
    public void FlatSpan_KeepsTheArea_AndAdjustsTheAspectRatio()
    {
        var node = new ParagliderNode { FlatArea = 24 };
        node.FlatSpan = 12;
        Assert.Equal(24, node.FlatArea);
        Assert.Equal(6, node.FlatAspectRatio, 6);
    }

    [Fact]
    public void Inspection_ListsTheParametersInDesignCategories()
    {
        var categories = ObjectInspector.GetProperties(new ParagliderNode()).Select(p => p.Category).Distinct().ToList();
        Assert.All(categories, c => Assert.Contains(c, ParagliderNode.CategoryOrder));
        Assert.DoesNotContain(ObjectInspector.GetProperties(new ParagliderNode()), p => p.Name.EndsWith("Hex"));
    }

    [Fact]
    public async Task ExportAll_WritesEveryFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"pgt-export-{Guid.NewGuid():N}");
        var dialogs = new FakeDialogs { FolderAnswer = folder };
        var toolbox = CreateToolbox(dialogs);
        var node = new ParagliderNode { Name = "Test wing", SpanwiseSegmentsPerCell = 3, ChordwiseSegments = 24, TextureSize = 512 };
        toolbox.Document.Project.Children.Add(node);
        toolbox.SelectedNode = node;
        var actions = new ParagliderActions(toolbox);
        try
        {
            Assert.True(actions.ExportAllCommand.CanExecute(null));
            await actions.ExportAllCommand.ExecuteAsync(null);

            Assert.Contains("Message:Export finished", dialogs.Asked);
            foreach (string name in new[] { "Test_wing.glb", "Test_wing_proxy.glb", "Test_wing_proxy.json", "Test_wing_lineplan.csv", "Test_wing.obj", "Test_wing.mtl", "Test_wing_basecolor.png" })
            {
                Assert.True(File.Exists(Path.Combine(folder, name)), name);
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ExportCommands_NeedAParaglider()
    {
        var toolbox = CreateToolbox();
        var actions = new ParagliderActions(toolbox);
        toolbox.SelectedNode = toolbox.Document.Project;
        Assert.False(actions.ExportAllCommand.CanExecute(null));
    }

    [Fact]
    public void Polars_AreStoredUnderTheirParaglider_AndRoundTrip()
    {
        var toolbox = CreateToolbox();
        var node = new ParagliderNode { ProxyComplexity = ProxyComplexity.Arcade };
        toolbox.Document.Project.Children.Add(node);
        var settings = new ParagliderToolbox.Paraglider.Polar.PolarRecorderSettings
        {
            SpeedBarSteps = [1f], BrakeSteps = [0.3f], StartSeconds = 3, SettleSeconds = 2, MeasureSeconds = 2,
        };
        var recording = ParagliderToolbox.Paraglider.Polar.PolarRecorder.Record(node.Snapshot(), settings);
        // A point that never sank has an infinite glide ratio: the file must still be written and read.
        recording.Points.Add(new ParagliderToolbox.Paraglider.Polar.PolarPoint { Label = "Lift", GlideRatio = float.PositiveInfinity });
        node.Children.Add(new PolarNode { Name = "First polar", Recording = recording });

        Assert.True(node.CanContain(typeof(PolarNode)));
        Assert.False(node.CanContain(typeof(ParagliderToolbox.Framework.Model.FolderNode)));

        var loaded = toolbox.Serializer.Deserialize(toolbox.Serializer.Serialize(toolbox.Document.Project));
        var polar = Assert.IsType<PolarNode>(Assert.Single(((ParagliderNode)loaded.Children[0]).Children));
        Assert.Equal("First polar", polar.Name);
        Assert.Equal(recording.Samples.Count, polar.Recording.Samples.Count);
        Assert.Equal(recording.Points.Count, polar.Recording.Points.Count);
        Assert.Equal(recording.Summary.Trim, polar.Recording.Summary.Trim);
        Assert.Equal(ProxyComplexity.Arcade, polar.Recording.Design!.ProxyComplexity);
        Assert.Equal(polar.Trim, new PolarNode { Recording = recording }.Trim);
    }

    [Fact]
    public void PolarOptions_RoundTripTheRecorderSettings()
    {
        var settings = new ParagliderToolbox.Paraglider.Polar.PolarRecorderSettings
        {
            SpeedBarSteps = ParagliderToolbox.Paraglider.Polar.PolarRecorderSettings.EvenSteps(8),
            BrakeSteps = ParagliderToolbox.Paraglider.Polar.PolarRecorderSettings.EvenSteps(20, 0.8f),
            SettleSeconds = 12, MeasureSeconds = 10, SampleInterval = 0.1f, StableSpread = 0.6f,
        };
        var options = PolarRecorderOptions.FromSettings(settings);

        Assert.Equal(8, options.SpeedBarSteps);
        Assert.Equal(20, options.BrakeSteps);
        Assert.Equal(0.8, options.MaxBrake);
        Assert.Equal(0.6, options.StableSpread);
        Assert.Equal(100, options.SamplesPerSetting);
        var copy = options.ToSettings();
        Assert.Equal(settings.SpeedBarSteps, copy.SpeedBarSteps);
        Assert.Equal(settings.BrakeSteps, copy.BrakeSteps);
        Assert.Equal(settings.FlightSeconds, copy.FlightSeconds, 3);
    }

    [Fact]
    public void PolarOptions_UpdateTheirEstimates()
    {
        var options = new PolarRecorderOptions();
        var changed = new List<string?>();
        options.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        string before = options.FlightTime;

        options.BrakeSteps = 20;

        Assert.NotEqual(before, options.FlightTime);
        Assert.Contains(nameof(PolarRecorderOptions.FlightTime), changed);
        Assert.Contains(nameof(PolarRecorderOptions.BrakeSettings), changed);
        Assert.Equal(PolarRecorderOptions.CategoryOrder.Order(), ObjectInspector.GetProperties(options).Select(p => p.Category!).Distinct().Order());
    }

    [Fact]
    public async Task AskingForPolarSettings_StartsFromTheLatestPolar_AndRemembersTheChoice()
    {
        var dialogs = new FakeDialogs();
        var toolbox = CreateToolbox(dialogs);
        var actions = new ParagliderActions(toolbox);
        var node = new ParagliderNode();
        node.Children.Add(new PolarNode { Recording = new() { Settings = new() { SettleSeconds = 15 } } });
        PolarRecorderOptions? shown = null;
        dialogs.EditAction = target =>
        {
            shown = (PolarRecorderOptions)target;
            shown.BrakeSteps = 20;
        };

        var settings = await actions.AskPolarSettingsAsync(node);

        Assert.Equal(15, shown!.SettleSeconds);
        Assert.Equal(20, settings!.BrakeSteps.Length);
        Assert.Contains("Edit:Record polar", dialogs.Asked);

        dialogs.EditAction = target => Assert.Equal(20, ((PolarRecorderOptions)target).BrakeSteps);
        Assert.NotNull(await actions.AskPolarSettingsAsync(new ParagliderNode()));

        dialogs.EditAnswer = false;
        Assert.Null(await actions.AskPolarSettingsAsync(node));
    }

    [Fact]
    public async Task AddingAParaglider_AsksForTheClassAndDetail_AndCreatesThatPreset()
    {
        var dialogs = new FakeDialogs();
        var toolbox = CreateToolbox(dialogs);
        toolbox.SelectedNode = toolbox.Document.Project;
        dialogs.DialogAction = content =>
        {
            var options = Assert.IsType<NewParagliderOptions>(content.DataContext);
            Assert.Equal("Low EN-B", options.Name); // follows the class until edited
            options.Class = WingClass.EnD;
            Assert.Equal("EN-D", options.Name);
            options.Detail = MeshDetail.LowPoly;
            options.Name = "Comp wing";
            options.Class = WingClass.EnC; // keeps the edited name
            options.Class = WingClass.EnD;
        };

        var node = Assert.IsType<ParagliderNode>(await toolbox.ProjectCommands.AddNodeInteractivelyAsync(toolbox.NodeTypes.Find("paraglider")!));

        Assert.Contains("Dialog:New paraglider", dialogs.Asked);
        Assert.Equal("Comp wing", node.Name);
        Assert.Equal(78, node.CellCount);
        Assert.Equal(2, node.RowCount);
        Assert.Equal(MeshDetail.LowPoly, node.MeshDetail);
        Assert.Same(node, toolbox.SelectedNode);
    }

    [Fact]
    public async Task CancelingTheNewParagliderDialog_AddsNothing()
    {
        var dialogs = new FakeDialogs { DialogAnswer = false };
        var toolbox = CreateToolbox(dialogs);
        toolbox.SelectedNode = toolbox.Document.Project;

        Assert.Null(await toolbox.ProjectCommands.AddNodeInteractivelyAsync(toolbox.NodeTypes.Find("paraglider")!));
        Assert.Empty(toolbox.Document.Project.Children);
    }

    [Fact]
    public void MeshDetail_WritesItsSettings_AndEditingASettingMakesItCustom()
    {
        var node = new ParagliderNode();
        Assert.Equal(MeshDetail.Custom, node.MeshDetail);
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.MeshDetail = MeshDetail.LowPoly;
        Assert.True(node.CellsPerSegment > 1);
        Assert.Equal(2, node.LineSides);
        Assert.Contains(nameof(ParagliderNode.CellsPerSegment), changed);

        node.ChordwiseSegments = 8;
        Assert.Equal(MeshDetail.Custom, node.MeshDetail);
        Assert.Equal(8, node.Snapshot().ChordwiseSegments);
    }

    [Fact]
    public void MeshDetail_RoundTrips_AndOldFilesLoadTheirOwnMeshSettings()
    {
        var toolbox = CreateToolbox();
        var node = new ParagliderNode(GliderPresets.Create(WingClass.EnA, MeshDetail.LowPoly)) { Name = "School" };
        toolbox.Document.Project.Children.Add(node);

        string json = toolbox.Serializer.Serialize(toolbox.Document.Project);
        var copy = Assert.IsType<ParagliderNode>(Assert.Single(toolbox.Serializer.Deserialize(json).Children));
        Assert.Equal(MeshDetail.LowPoly, copy.MeshDetail);
        Assert.Equal(node.CellsPerSegment, copy.CellsPerSegment);
        Assert.Equal(40, copy.CellCount);

        // A file from before the mesh detail: no "meshDetail", its own settings.
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var saved = root["project"]!["children"]![0]!.AsObject();
        Assert.True(saved.Remove("meshDetail"));
        saved["cellsPerSegment"] = 1;
        saved["spanwiseSegmentsPerCell"] = 4;
        var old = Assert.IsType<ParagliderNode>(Assert.Single(toolbox.Serializer.Deserialize(root.ToJsonString()).Children));
        Assert.Equal(MeshDetail.Custom, old.MeshDetail);
        Assert.Equal(4, old.SpanwiseSegmentsPerCell);
    }
}
