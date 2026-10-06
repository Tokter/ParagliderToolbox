using Atelier.Core.Keybinding;
using ParagliderToolbox.Framework.Commands;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Settings;

namespace ParagliderToolbox.Tests;

public class ProjectCommandsTests
{
    [Fact]
    public void AddNode_GoesIntoTheSelectedContainer_AndSelectsTheNewNode()
    {
        var toolbox = TestToolbox.Create();
        var folder = new FolderNode();
        toolbox.Document.Project.Children.Add(folder);
        toolbox.SelectedNode = folder;

        var node = toolbox.ProjectCommands.AddNode(toolbox.Type<SampleNode>());

        Assert.Same(folder, node!.Parent);
        Assert.Same(node, toolbox.SelectedNode);
    }

    [Fact]
    public void AddNode_AtALeaf_GoesIntoTheLeafsParent()
    {
        var toolbox = TestToolbox.Create();
        var leaf = new SampleNode();
        toolbox.Document.Project.Children.Add(leaf);
        toolbox.SelectedNode = leaf;

        var node = toolbox.ProjectCommands.AddNode(toolbox.Type<FolderNode>());

        Assert.Same(toolbox.Document.Project, node!.Parent);
    }

    [Fact]
    public void AddNode_NumbersRepeatedNames()
    {
        var toolbox = TestToolbox.Create();
        var type = toolbox.Type<FolderNode>();

        var names = Enumerable.Range(0, 3).Select(_ =>
        {
            toolbox.SelectedNode = toolbox.Document.Project;
            return toolbox.ProjectCommands.AddNode(type)!.Name;
        }).ToList();

        Assert.Equal(["Folder", "Folder 2", "Folder 3"], names);
    }

    [Fact]
    public void AddCommands_AreRegisteredPerCreatableType()
    {
        var toolbox = TestToolbox.Create();

        var folder = KeybindingManager.FindCommand(ProjectCommands.Group, "AddFolder");
        Assert.NotNull(folder);
        Assert.Equal("Ctrl+Shift+N", folder.Keybinding);
        Assert.NotNull(KeybindingManager.FindCommand(ProjectCommands.Group, "AddSample"));
        Assert.Null(KeybindingManager.FindCommand(ProjectCommands.Group, "AddProject"));
    }

    [Fact]
    public async Task Delete_AsksForNodesWithContent_AndSelectsTheNextSibling()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = true };
        var toolbox = TestToolbox.Create(dialogs);
        var project = toolbox.Document.Project;
        var folder = new FolderNode();
        folder.Children.Add(new SampleNode());
        var next = new SampleNode();
        project.Children.Add(folder);
        project.Children.Add(next);
        toolbox.SelectedNode = folder;

        await toolbox.ProjectCommands.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Confirm:Delete"], dialogs.Asked);
        Assert.Equal([next], project.Children);
        Assert.Same(next, toolbox.SelectedNode);
    }

    [Fact]
    public void Duplicate_InsertsACopyAfterTheNode()
    {
        var toolbox = TestToolbox.Create();
        var project = toolbox.Document.Project;
        var original = new SampleNode { Name = "Wing", Span = 10 };
        project.Children.Add(original);
        project.Children.Add(new FolderNode());
        toolbox.SelectedNode = original;

        toolbox.ProjectCommands.DuplicateCommand.Execute(null);

        var copy = Assert.IsType<SampleNode>(project.Children[1]);
        Assert.Equal("Wing 2", copy.Name);
        Assert.Equal(10, copy.Span);
        Assert.Same(copy, toolbox.SelectedNode);
    }

    [Fact]
    public void TheProject_CantBeDeletedOrMoved()
    {
        var toolbox = TestToolbox.Create();
        toolbox.SelectedNode = toolbox.Document.Project;

        Assert.False(toolbox.ProjectCommands.DeleteCommand.CanExecute(null));
        Assert.False(toolbox.ProjectCommands.DuplicateCommand.CanExecute(null));
        Assert.False(toolbox.ProjectCommands.MoveUpCommand.CanExecute(null));
    }

    [Fact]
    public void Removing_TheSelectedNodesFolder_SelectsItsParent()
    {
        var toolbox = TestToolbox.Create();
        var folder = new FolderNode();
        var node = new SampleNode();
        folder.Children.Add(node);
        toolbox.Document.Project.Children.Add(folder);
        toolbox.SelectedNode = node;

        toolbox.Document.Project.Children.Remove(folder);

        Assert.Same(toolbox.Document.Project, toolbox.SelectedNode);
    }
}

public class ShellViewModelTests
{
    [Fact]
    public async Task SaveAndOpen_RoundTripTheProject_AndTrackModifications()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pgt-{Guid.NewGuid():N}.pgtproj");
        var dialogs = new FakeDialogs { SaveAnswer = path, OpenAnswer = path };
        var toolbox = TestToolbox.Create(dialogs);
        var shell = new ShellViewModel(toolbox, new AppSettings());
        try
        {
            toolbox.Document.Project.Name = "Saved";
            Assert.True(toolbox.Document.IsModified);
            Assert.EndsWith("•" + " — " + ShellViewModel.ApplicationName, shell.Title);

            await shell.SaveProjectCommand.ExecuteAsync(null);
            Assert.False(toolbox.Document.IsModified);
            Assert.Equal(Path.GetFullPath(path), toolbox.Document.FilePath);
            Assert.Contains("Save:Saved.pgtproj", dialogs.Asked);

            await shell.NewProjectCommand.ExecuteAsync(null);
            Assert.Equal("Untitled", toolbox.Document.Project.Name);

            await shell.OpenProjectCommand.ExecuteAsync(null);
            Assert.Equal("Saved", toolbox.Document.Project.Name);
            Assert.Same(toolbox.Document.Project, toolbox.SelectedNode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ClosingAModifiedProject_CanBeCanceled()
    {
        var dialogs = new FakeDialogs { SaveChangesAnswer = SaveChangesChoice.Cancel };
        var toolbox = TestToolbox.Create(dialogs);
        var shell = new ShellViewModel(toolbox, new AppSettings());
        toolbox.Document.Project.Name = "Keep me";

        await shell.NewProjectCommand.ExecuteAsync(null);
        await shell.ExitCommand.ExecuteAsync(null);

        Assert.Equal("Keep me", toolbox.Document.Project.Name);
        Assert.False(dialogs.WindowClosed);
    }

    [Fact]
    public async Task OpeningABrokenFile_ShowsAnError_AndKeepsTheProject()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pgt-{Guid.NewGuid():N}.pgtproj");
        File.WriteAllText(path, "{ \"format\": \"nope\" }");
        var dialogs = new FakeDialogs { OpenAnswer = path };
        var toolbox = TestToolbox.Create(dialogs);
        var shell = new ShellViewModel(toolbox, new AppSettings());
        var before = toolbox.Document;
        try
        {
            await shell.OpenProjectCommand.ExecuteAsync(null);

            Assert.Contains("Error:Couldn't open the project", dialogs.Asked);
            Assert.Same(before, toolbox.Document);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
