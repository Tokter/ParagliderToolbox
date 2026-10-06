using Atelier.Controls;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Framework.Views;

namespace ParagliderToolbox.Modules.Core;

/// <summary>
/// The toolbox's own parts: the project and folder nodes, the Project, Properties and Detail views, and the default
/// workspaces.
/// </summary>
public sealed class CoreModule : IToolboxModule
{
    /// <summary>The id of the project explorer view.</summary>
    public const string ExplorerView = "explorer";

    /// <summary>The id of the property editor view.</summary>
    public const string PropertiesView = "properties";

    /// <summary>The id of the detail view.</summary>
    public const string DetailView = "detail";

    /// <inheritdoc/>
    public string Name => "Core";

    /// <inheritdoc/>
    public void Register(Toolbox toolbox)
    {
        toolbox.NodeTypes.Register<Project>("project", "Project", MaterialIconKind.Paragliding, isCreatable: false);
        toolbox.NodeTypes.Register<FolderNode>("folder", "Folder", MaterialIconKind.Folder,
            description: "Add a folder to group objects", defaultKeybinding: "Ctrl+Shift+N");

        toolbox.DetailViews
            .Register<ContainerNode>(container => new ContainerDetailView(toolbox, container))
            .Register<Project>(project => new ProjectDetailView(toolbox, project));

        toolbox.Views
            .Register(new AreaEditorType(ExplorerView, "Project", _ => new ProjectExplorerView(toolbox))
            {
                Icon = MaterialIconKind.AccountTree,
                Description = "The objects in the project",
                CreateHeader = area => ProjectExplorerView.CreateHeader(toolbox, area),
            })
            .Register(new AreaEditorType(DetailView, "Detail", _ => new DetailView(toolbox))
            {
                Icon = MaterialIconKind.Preview,
                Description = "The selected object's own view",
            })
            .Register(new AreaEditorType(PropertiesView, "Properties", _ => new PropertiesView(toolbox))
            {
                Icon = MaterialIconKind.Tune,
                Description = "The selected object's properties",
            });

        var main = new WorkspaceDefinition("Main", AreaDefinition.Row(
            AreaDefinition.Editor(ExplorerView, 1),
            AreaDefinition.Editor(DetailView, 3),
            AreaDefinition.Editor(PropertiesView, 1.3f)));
        var inspect = new WorkspaceDefinition("Inspect", AreaDefinition.Row(
            AreaDefinition.Editor(ExplorerView, 1),
            AreaDefinition.Column(AreaDefinition.Editor(DetailView, 2), AreaDefinition.Editor(PropertiesView, 1)).WithWeight(2.5f)));
        toolbox.DefaultWorkspaces.Add(main);
        toolbox.DefaultWorkspaces.Add(inspect);
        toolbox.WorkspaceTemplates.Add(main);
        toolbox.WorkspaceTemplates.Add(inspect);
        toolbox.WorkspaceTemplates.Add(new WorkspaceDefinition("Detail", AreaDefinition.Editor(DetailView)));
    }
}
