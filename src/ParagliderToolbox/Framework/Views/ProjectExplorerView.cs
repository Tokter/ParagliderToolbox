using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Commands;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Framework.Views;

/// <summary>
/// The project tree: every node of the open project, the selected one highlighted. Selecting a node here selects it in
/// the toolbox (and so in the property editor and the detail view), and selecting it elsewhere reveals it here.
/// </summary>
/// <remarks>
/// The view is a <see cref="KeybindingHandler"/> for the <see cref="ProjectCommands.ExplorerGroup"/> commands (Delete,
/// Alt+Up, Alt+Down), so those keys only act on the tree while it has the focus.
/// </remarks>
public sealed class ProjectExplorerView : KeybindingHandler
{
    private readonly Toolbox _toolbox;
    private readonly ProjectCommands _commands;
    private readonly TreeView _tree;
    private Project? _shownProject;
    private bool _isSyncing;

    /// <summary>Initializes the explorer for <paramref name="toolbox"/>.</summary>
    public ProjectExplorerView(Toolbox toolbox) : base(ProjectCommands.ExplorerGroup)
    {
        _toolbox = toolbox;
        _commands = toolbox.ProjectCommands;
        DataContext = _commands;

        _tree = new TreeView()
            .Margin(4)
            .WithChildrenSelector((ProjectNode node) => node.Children)
            .WithItemTemplate((ProjectNode node) => Row(node));
        _tree.SelectionChanged += (_, item) =>
        {
            if (!_isSyncing) _toolbox.SelectedNode = item as ProjectNode;
        };
        Content = _tree;
        ShowProject();
    }

    /// <summary>Creates the explorer's header tools: a menu to add nodes, and buttons to expand or collapse the tree.</summary>
    public static UIElement CreateHeader(Toolbox toolbox, Area area) =>
        new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).VerticalAlignment(VerticalAlignment.Center).Children(
            HeaderButton(MaterialIconKind.Add, "Add a node at the selection", button => ShowAddMenu(toolbox, button)),
            HeaderButton(MaterialIconKind.UnfoldMore, "Expand all", _ => (area.EditorContent as ProjectExplorerView)?._tree.ExpandAll()),
            HeaderButton(MaterialIconKind.UnfoldLess, "Collapse all", _ => (area.EditorContent as ProjectExplorerView)?.CollapseAll()));

    private static Button HeaderButton(MaterialIconKind icon, string toolTip, Action<Button> click)
    {
        var button = new Button()
            .Variant(ButtonVariant.Text)
            .StyleKey(Area.EditorButtonStyleKey)
            .Size(26, 26).MinWidth(0).MinHeight(0).Padding(0)
            .Content(new Icon().Kind(icon).Size(18))
            .ToolTip(toolTip);
        button.Click += (_, _) => click(button);
        return button;
    }

    /// <summary>Opens a menu under <paramref name="target"/> listing the node types that can be added at the selection.</summary>
    public static void ShowAddMenu(Toolbox toolbox, UIElement target)
    {
        var menu = new ContextMenu();
        foreach (var item in CreateAddMenuItems(toolbox, toolbox.SelectedNode ?? toolbox.Document.Project)) menu.Items.Add(item);
        if (menu.Items.Count > 0 && menu.Open(target)) menu.Placement = PlacementMode.Bottom;
    }

    /// <summary>
    /// Creates menu items for adding the node types that fit at <paramref name="node"/> (in it, or in the nearest node
    /// above that can hold them), grouped by category.
    /// </summary>
    public static IEnumerable<object> CreateAddMenuItems(Toolbox toolbox, ProjectNode node)
    {
        string? category = null;
        bool first = true;
        foreach (var type in toolbox.NodeTypes.Types.Where(t => t.IsCreatable).OrderBy(t => t.Category ?? string.Empty))
        {
            if (!first && type.Category != category) yield return new Separator();
            first = false;
            category = type.Category;
            yield return new MenuItem().Command(toolbox.ProjectCommands.AddCommand(type));
        }
    }

    private UIElement Row(ProjectNode node)
    {
        var row = new StackPanel()
            .Orientation(Orientation.Horizontal)
            .Spacing(8)
            .Children(
                new Icon().Kind(_toolbox.NodeTypes.IconOf(node)).Size(18).VerticalAlignment(VerticalAlignment.Center),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(node, n => n.Name));
        row.ContextMenu(NodeMenu(node));
        return row;
    }

    // The context menu of a node; opening it selects the node, so the commands act on it.
    private ContextMenu NodeMenu(ProjectNode node)
    {
        var add = new MenuItem("_Add").Icon(MaterialIconKind.Add);
        foreach (var item in CreateAddMenuItems(_toolbox, node)) add.Items.Add(item);
        return new ContextMenu { DataContext = _commands }
            .OnOpening((_, _) => _toolbox.SelectedNode = node)
            .Items(
                add,
                new Separator(),
                new MenuItem().Command(_commands.RenameCommand),
                new MenuItem().Command(_commands.DuplicateCommand),
                new MenuItem().Command(_commands.DeleteCommand),
                new Separator(),
                new MenuItem().Command(_commands.MoveUpCommand),
                new MenuItem().Command(_commands.MoveDownCommand));
    }

    private void CollapseAll()
    {
        _tree.CollapseAll();
        // Keep the project's top-level nodes in view.
        if (_tree.RootItems.Count > 0) _tree.RootItems[0].IsExpanded = true;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _toolbox.PropertyChanged += OnToolboxPropertyChanged;
        ShowProject();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _toolbox.PropertyChanged -= OnToolboxPropertyChanged;
        base.OnDetachedFromVisualTree();
    }

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Toolbox.Document)) ShowProject();
        else if (e.PropertyName == nameof(Toolbox.SelectedNode)) ShowSelection();
    }

    private void ShowProject()
    {
        var project = _toolbox.Document.Project;
        if (project != _shownProject)
        {
            _shownProject = project;
            _isSyncing = true;
            try
            {
                _tree.ItemsSource = new[] { project };
                if (_tree.RootItems.Count > 0) _tree.RootItems[0].IsExpanded = true;
            }
            finally
            {
                _isSyncing = false;
            }
        }
        ShowSelection();
    }

    // Selects the toolbox's selected node in the tree, expanding the nodes above it.
    private void ShowSelection()
    {
        var node = _toolbox.SelectedNode;
        if (node != null && _tree.SelectedNode?.ItemValue == node) return;
        _isSyncing = true;
        try
        {
            if (node != null) Reveal(node);
            _tree.SelectedItem = null;
            _tree.SelectedItem = node;
            if (_tree.SelectedNode is { } item) _tree.ScrollIntoView(item);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void Reveal(ProjectNode node)
    {
        IReadOnlyList<TreeViewItem> items = _tree.RootItems;
        foreach (var ancestor in node.Ancestors().Reverse())
        {
            var item = items.FirstOrDefault(i => i.ItemValue == ancestor);
            if (item == null) return;
            item.IsExpanded = true;
            items = item.ChildrenItems;
        }
    }
}
