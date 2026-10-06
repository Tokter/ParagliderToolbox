using Atelier.Core.Inspection;
using ParagliderToolbox.Framework.Model;

namespace ParagliderToolbox.Tests;

public class ProjectModelTests
{
    [Fact]
    public void AddingANode_ToAnotherParent_MovesIt()
    {
        var a = new FolderNode();
        var b = new FolderNode();
        var node = new SampleNode();
        a.Children.Add(node);

        b.Children.Add(node);

        Assert.Empty(a.Children);
        Assert.Same(b, node.Parent);
    }

    [Fact]
    public void RemovingAndClearing_UnlinkTheParent()
    {
        var folder = new FolderNode();
        var first = new SampleNode();
        var second = new SampleNode();
        folder.Children.Add(first);
        folder.Children.Add(second);

        folder.Children.Remove(first);
        folder.Children.Clear();

        Assert.Null(first.Parent);
        Assert.Null(second.Parent);
    }

    [Fact]
    public void AddingANode_BelowItself_Throws()
    {
        var outer = new FolderNode();
        var inner = new FolderNode();
        outer.Children.Add(inner);

        Assert.Throws<InvalidOperationException>(() => inner.Children.Add(outer));
    }

    [Fact]
    public void Changes_DeepInTheTree_MarkTheDocumentModified()
    {
        var project = new Project();
        var folder = new FolderNode();
        var node = new SampleNode();
        folder.Children.Add(node);
        project.Children.Add(folder);
        var document = new ProjectDocument(project);
        var changes = new List<string?>();
        project.SubtreeChanged += (_, e) => changes.Add($"{e.Node.Name}.{e.PropertyName}");

        node.Span = 9;

        Assert.True(document.IsModified);
        Assert.Contains("Sample.Span", changes);
    }

    [Fact]
    public void RenamingTheProject_UpdatesTheDocumentName()
    {
        var document = new ProjectDocument(new Project());
        var changed = new List<string?>();
        document.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        document.Project.Name = "Speed wings";

        Assert.Equal("Speed wings", document.DisplayName);
        Assert.Contains(nameof(ProjectDocument.DisplayName), changed);
    }

    [Fact]
    public void Containers_AcceptNodesButNotProjects_AndLeavesAcceptNothing()
    {
        Assert.True(new FolderNode().CanContain(typeof(SampleNode)));
        Assert.True(new Project().CanContain(typeof(FolderNode)));
        Assert.False(new FolderNode().CanContain(typeof(Project)));
        Assert.False(new SampleNode().CanContain(typeof(FolderNode)));
    }

    [Fact]
    public void PropertyEditorMetadata_IncludesTheBaseClassName_ButNotTheTreeLinks()
    {
        var names = ObjectInspector.GetProperties(new Project()).Select(p => p.Name).ToList();

        Assert.Equal(nameof(ProjectNode.Name), names[0]);
        Assert.Contains(nameof(Project.Description), names);
        Assert.Contains(nameof(ContainerNode.ItemCount), names);
        Assert.DoesNotContain(nameof(ProjectNode.Children), names);
        Assert.DoesNotContain(nameof(ProjectNode.Parent), names);
        Assert.DoesNotContain(nameof(ProjectNode.Id), names);
    }
}
