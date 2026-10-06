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
}
