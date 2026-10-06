using System.Text.Json.Serialization;
using Atelier.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using ParagliderToolbox.Framework.Commands;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Serialization;

namespace ParagliderToolbox.Framework.Modules;

/// <summary>
/// The application's shared state and extension points: the registries modules fill at startup, the open project and
/// the selected node.
/// </summary>
/// <remarks>
/// Views get the toolbox and follow <see cref="Document"/> and <see cref="SelectedNode"/> through
/// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>.
/// </remarks>
public sealed class Toolbox : ObservableObject
{
    private readonly List<IToolboxModule> _modules = [];
    private ProjectSerializer? _serializer;
    private ProjectDocument _document;
    private ProjectNode? _selectedNode;

    private IShellDialogs? _dialogs;

    /// <summary>Initializes a toolbox with an empty project and no modules.</summary>
    public Toolbox()
    {
        _document = CreateDocument(new Project(), null);
        _selectedNode = _document.Project;
        ProjectCommands = new ProjectCommands(this);
    }

    /// <summary>Gets the commands that edit the project tree (add, rename, duplicate, delete, move).</summary>
    public ProjectCommands ProjectCommands { get; }

    /// <summary>Gets or sets the dialogs commands ask with; the main window provides them.</summary>
    public IShellDialogs Dialogs
    {
        get => _dialogs ?? throw new InvalidOperationException("The main window provides the dialogs.");
        set => _dialogs = value;
    }

    #region Extension points

    /// <summary>Gets the kinds of nodes projects can contain.</summary>
    public NodeTypeRegistry NodeTypes { get; } = new();

    /// <summary>
    /// Gets the views the workspace areas can show (project explorer, properties, detail, ...). The content factories
    /// run once per area that shows the view.
    /// </summary>
    public AreaEditorRegistry Views { get; } = new();

    /// <summary>Gets the object specific views the Detail view shows for the selected node.</summary>
    public DetailViewRegistry DetailViews { get; } = new();

    /// <summary>
    /// Gets the setups applied to every property editor (the <c>PropertyGrid</c> of the Properties view), for
    /// registering editors of custom property types: <c>registry =&gt; registry.Register&lt;Vector3&gt;(ctx =&gt; ...)</c>.
    /// </summary>
    public IList<Action<PropertyEditorRegistry>> PropertyEditors { get; } = new List<Action<PropertyEditorRegistry>>();

    /// <summary>
    /// Gets the property categories the property editor lists first, in this order (e.g. the steps of a design); other
    /// categories follow alphabetically.
    /// </summary>
    public List<string> PropertyCategoryOrder { get; } = ["General"];

    /// <summary>Gets converters for node property types System.Text.Json doesn't handle on its own.</summary>
    public IList<JsonConverter> JsonConverters { get; } = new List<JsonConverter>();

    /// <summary>Gets the items modules add to the main menu.</summary>
    public MenuRegistry Menus { get; } = new();

    /// <summary>
    /// Gets keybinding groups whose commands work wherever the focus is in the window, with the object they run on
    /// (for example a module's export commands, which act on the selected node). Commands of views that only work while
    /// the view has the focus belong in a <c>KeybindingHandler</c> around the view instead.
    /// </summary>
    public IList<(string Group, object Target)> GlobalCommands { get; } = new List<(string Group, object Target)>();

    /// <summary>Gets the workspaces a fresh installation (or Reset layout) starts with.</summary>
    public IList<WorkspaceDefinition> DefaultWorkspaces { get; } = new List<WorkspaceDefinition>();

    /// <summary>Gets the workspace templates the "+" button of the workspace tabs offers.</summary>
    public IList<WorkspaceDefinition> WorkspaceTemplates { get; } = new List<WorkspaceDefinition>();

    /// <summary>Gets the registered modules.</summary>
    public IReadOnlyList<IToolboxModule> Modules => _modules;

    /// <summary>Gets whether <see cref="Initialize"/> ran.</summary>
    public bool IsInitialized => _serializer != null;

    /// <summary>
    /// Lets <paramref name="modules"/> register their parts, then freezes the node types for saving and loading.
    /// </summary>
    public void Initialize(IEnumerable<IToolboxModule> modules)
    {
        if (IsInitialized) throw new InvalidOperationException("The toolbox is already initialized.");
        foreach (var module in modules)
        {
            _modules.Add(module);
            module.Register(this);
        }
        _serializer = new ProjectSerializer(NodeTypes, JsonConverters);
        ProjectCommands.RegisterAddCommands();
    }

    /// <summary>Gets the serializer for project files and copies of nodes.</summary>
    public ProjectSerializer Serializer => _serializer ?? throw new InvalidOperationException("Initialize the toolbox first.");

    #endregion

    #region Document and selection

    /// <summary>Gets the open project.</summary>
    public ProjectDocument Document
    {
        get => _document;
        private set
        {
            if (!SetProperty(ref _document, value)) return;
            SelectedNode = value.Project;
        }
    }

    /// <summary>
    /// Gets or sets the selected node, shown in the property editor and the detail view; <c>null</c> for none. Nodes
    /// outside the open project can't be selected.
    /// </summary>
    public ProjectNode? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value != null && value.Project == Document.Project ? value : null);
    }

    /// <summary>Replaces the open project with a new, empty one.</summary>
    public void NewProject() => Document = CreateDocument(new Project(), null);

    /// <summary>Opens the project file at <paramref name="path"/>, replacing the open project.</summary>
    /// <exception cref="ProjectFileException">The file is not a project file this version can read.</exception>
    public void OpenProject(string path) => Document = CreateDocument(Serializer.Load(path), Path.GetFullPath(path));

    /// <summary>Saves the open project to <paramref name="path"/> and remembers that file.</summary>
    public void SaveProject(string path)
    {
        Serializer.Save(Document.Project, path);
        Document.FilePath = Path.GetFullPath(path);
        Document.IsModified = false;
    }

    private ProjectDocument CreateDocument(Project project, string? path)
    {
        var document = new ProjectDocument(project, path);
        // A selected node that leaves the project (deleted, or its folder deleted) unselects; its old parent, if still in
        // the project, takes the selection.
        project.SubtreeChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectNode.Children) && _selectedNode != null && _selectedNode.Project != project)
            {
                SelectedNode = e.Node.Project == project ? e.Node : project;
            }
        };
        return document;
    }

    #endregion
}
