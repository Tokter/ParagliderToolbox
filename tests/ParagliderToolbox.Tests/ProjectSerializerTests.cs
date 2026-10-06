using System.Text.Json.Nodes;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Serialization;

namespace ParagliderToolbox.Tests;

public class ProjectSerializerTests
{
    private static Project SampleProject()
    {
        var project = new Project { Name = "Wings", Description = "Test project", Author = "Tester" };
        var folder = new FolderNode { Name = "Gliders" };
        folder.Children.Add(new SampleNode { Name = "A", Span = 12.25, Kind = SampleKind.Harness, Points = [4, 5] });
        project.Children.Add(folder);
        project.Children.Add(new SampleNode { Name = "B", Reference = folder.Id });
        return project;
    }

    [Fact]
    public void RoundTrip_KeepsTheTree_TypesAndProperties()
    {
        var serializer = TestToolbox.Create().Serializer;
        var original = SampleProject();

        var loaded = serializer.Deserialize(serializer.Serialize(original));

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal("Wings", loaded.Name);
        Assert.Equal("Test project", loaded.Description);
        Assert.Equal("Tester", loaded.Author);
        var folder = Assert.IsType<FolderNode>(loaded.Children[0]);
        Assert.Equal("Gliders", folder.Name);
        var a = Assert.IsType<SampleNode>(folder.Children[0]);
        Assert.Equal(12.25, a.Span);
        Assert.Equal(SampleKind.Harness, a.Kind);
        Assert.Equal([4, 5], a.Points);
        var b = Assert.IsType<SampleNode>(loaded.Children[1]);
        Assert.Equal(folder.Id, b.Reference);
    }

    [Fact]
    public void Loading_LinksEveryNodeToItsParent()
    {
        var serializer = TestToolbox.Create().Serializer;
        var loaded = serializer.Deserialize(serializer.Serialize(SampleProject()));

        Assert.Null(loaded.Parent);
        Assert.All(loaded.Children, c => Assert.Same(loaded, c.Parent));
        var folder = loaded.Children[0];
        Assert.Same(folder, folder.Children[0].Parent);
        Assert.Same(loaded, folder.Children[0].Project);
    }

    [Fact]
    public void Writing_UsesTheTypeIds_AndLeavesOutComputedAndIgnoredProperties()
    {
        var serializer = TestToolbox.Create().Serializer;
        var file = JsonNode.Parse(serializer.Serialize(SampleProject()))!;

        Assert.Equal(ProjectSerializer.FormatId, (string?)file["format"]);
        Assert.Equal(ProjectSerializer.CurrentVersion, (int?)file["version"]);
        var project = file["project"]!.AsObject();
        Assert.False(project.ContainsKey("itemCount"));
        Assert.False(project.ContainsKey("parent"));
        Assert.False(project.ContainsKey("project"));
        var folder = project["children"]![0]!.AsObject();
        Assert.Equal("folder", (string?)folder["$type"]);
        var sample = folder["children"]![0]!.AsObject();
        Assert.Equal("sample", (string?)sample["$type"]);
        Assert.Equal("Harness", (string?)sample["kind"]);
        Assert.False(sample.ContainsKey("halfSpan"));
        Assert.False(sample.ContainsKey("transient"));
    }

    [Fact]
    public void Reading_AcceptsTheTypeIdAfterOtherProperties()
    {
        var serializer = TestToolbox.Create().Serializer;
        string json = """
            { "format": "ParagliderToolbox.Project", "version": 1,
              "project": { "name": "P", "children": [ { "name": "Late type", "span": 3, "$type": "sample" } ] } }
            """;

        var node = Assert.IsType<SampleNode>(serializer.Deserialize(json).Children[0]);
        Assert.Equal("Late type", node.Name);
        Assert.Equal(3, node.Span);
    }

    [Theory]
    [InlineData("""{ "format": "Something.Else", "version": 1, "project": {} }""", "isn't a Paraglider Toolbox project")]
    [InlineData("""{ "format": "ParagliderToolbox.Project", "version": 99, "project": {} }""", "newer version")]
    [InlineData("""{ "format": "ParagliderToolbox.Project", "version": 1, "project": { "children": [ { "$type": "warp-drive" } ] } }""", "unknown kind of object")]
    [InlineData("""not json""", "damaged")]
    public void Reading_RejectsFilesItCannotRead_WithAReason(string json, string reason)
    {
        var serializer = TestToolbox.Create().Serializer;

        var e = Assert.Throws<ProjectFileException>(() => serializer.Deserialize(json));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void Writing_AnUnregisteredNodeType_Fails()
    {
        var types = new NodeTypeRegistry();
        types.Register<FolderNode>("folder", "Folder", Atelier.Controls.MaterialIconKind.Folder);
        var serializer = new ProjectSerializer(types);
        var project = new Project();
        project.Children.Add(new FolderNode());
        project.Children[0].Children.Add(new SampleNode { Name = "Stray" });

        var e = Assert.Throws<InvalidOperationException>(() => serializer.Serialize(project));
        Assert.Contains("SampleNode isn't registered", e.Message);
    }

    [Fact]
    public void Clone_CopiesTheSubtree_WithNewIds()
    {
        var serializer = TestToolbox.Create().Serializer;
        var folder = SampleProject().Children[0];

        var copy = serializer.Clone(folder);

        Assert.Null(copy.Parent);
        Assert.NotEqual(folder.Id, copy.Id);
        var child = Assert.IsType<SampleNode>(Assert.Single(copy.Children));
        Assert.NotEqual(folder.Children[0].Id, child.Id);
        Assert.Equal(12.25, child.Span);
    }

    [Fact]
    public void SaveAndLoad_GoThroughAFile()
    {
        var serializer = TestToolbox.Create().Serializer;
        string path = Path.Combine(Path.GetTempPath(), $"pgt-{Guid.NewGuid():N}{ProjectSerializer.FileExtension}");
        try
        {
            serializer.Save(SampleProject(), path);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("Wings", serializer.Load(path).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
