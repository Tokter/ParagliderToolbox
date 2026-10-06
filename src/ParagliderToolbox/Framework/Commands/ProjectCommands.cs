using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Framework.Commands;

/// <summary>
/// The commands that edit the project tree, on the selected node: adding nodes of every registered type, renaming,
/// duplicating, deleting and reordering.
/// </summary>
/// <remarks>
/// The <see cref="Group"/> commands work wherever the focus is; the <see cref="ExplorerGroup"/> commands (Delete and
/// moving with Alt+arrows, keys other views use too) only while the project explorer has the focus. Adding a node of a
/// type is a command per registered <see cref="NodeType"/> (see <see cref="RegisterAddCommands"/>), so it is in the
/// command palette and can get a shortcut.
/// </remarks>
public sealed partial class ProjectCommands : ObservableObject
{
    /// <summary>The keybinding group of the project commands that work anywhere in the window.</summary>
    public const string Group = "Project";

    /// <summary>The keybinding group of the commands that work while the project explorer has the focus.</summary>
    public const string ExplorerGroup = "Project explorer";

    /// <summary>Initializes the commands for <paramref name="toolbox"/>.</summary>
    public ProjectCommands(Toolbox toolbox)
    {
        Toolbox = toolbox;
        Toolbox.PropertyChanged += OnToolboxPropertyChanged;
    }

    /// <summary>Gets the toolbox.</summary>
    public Toolbox Toolbox { get; }

    private ProjectNode? Selected => Toolbox.SelectedNode;

    #region Adding nodes

    /// <summary>
    /// Registers an "Add …" command for every node type users can create, in the <see cref="Group"/> group (named
    /// <c>Add</c> + the type id, e.g. <c>AddFolder</c>). Call once after the modules registered their node types.
    /// </summary>
    public void RegisterAddCommands()
    {
        foreach (var type in Toolbox.NodeTypes.Types.Where(t => t.IsCreatable))
        {
            KeybindingManager.RegisterOrUpdateKeybinding(new KeybindingDescriptor(
                AddCommandName(type), Group, type.DefaultKeybinding ?? string.Empty,
                new AtelierRelayCommand(() => AddNode(type), () => CanAddNode(type)),
                label: $"Add {type.DisplayName.ToLowerInvariant()}",
                description: type.Description ?? $"Add a {type.DisplayName.ToLowerInvariant()} to the project",
                icon: type.Icon.ToString()));
        }
    }

    /// <summary>Gets the name of the command that adds a node of <paramref name="type"/>.</summary>
    public static string AddCommandName(NodeType type) =>
        "Add" + string.Concat(type.Id.Split('-', '_', '.', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>Gets the command that adds a node of <paramref name="type"/>, for menus and buttons.</summary>
    public System.Windows.Input.ICommand? AddCommand(NodeType type) => KeybindingManager.FindCommand(Group, AddCommandName(type))?.Command;

    /// <summary>Gets the node a new node of <paramref name="type"/> goes into: the selected node, or the nearest node above it that can hold it.</summary>
    public ProjectNode? TargetFor(NodeType type)
    {
        for (var node = Selected ?? Toolbox.Document.Project; node != null; node = node.Parent)
        {
            if (node.CanContain(type.ClrType)) return node;
        }
        return null;
    }

    /// <summary>Returns whether a node of <paramref name="type"/> can be added at the selection.</summary>
    public bool CanAddNode(NodeType type) => TargetFor(type) != null;

    /// <summary>Adds a node of <paramref name="type"/> at the selection (see <see cref="TargetFor"/>) and selects it.</summary>
    /// <returns>The new node, or <c>null</c> when it can't be added.</returns>
    public ProjectNode? AddNode(NodeType type)
    {
        if (TargetFor(type) is not { } parent) return null;
        var node = type.Create();
        node.Name = UniqueName(parent, string.IsNullOrWhiteSpace(node.Name) ? type.DisplayName : node.Name);
        parent.Children.Add(node);
        Toolbox.SelectedNode = node;
        return node;
    }

    /// <summary>Returns <paramref name="name"/>, or with a number added (<c>Folder 2</c>) when a child of <paramref name="parent"/> has it.</summary>
    public static string UniqueName(ProjectNode parent, string name, ProjectNode? except = null)
    {
        bool Taken(string candidate) => parent.Children.Any(c => c != except && string.Equals(c.Name, candidate, StringComparison.CurrentCultureIgnoreCase));
        if (!Taken(name)) return name;
        // "Folder 2" continues as "Folder 3", not "Folder 2 2".
        string stem = name;
        int space = name.LastIndexOf(' ');
        if (space > 0 && int.TryParse(name[(space + 1)..], out _)) stem = name[..space];
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} {i}";
            if (!Taken(candidate)) return candidate;
        }
    }

    #endregion

    #region Editing

    private bool HasEditableSelection() => Selected is { Parent: not null };

    [RelayCommand(CanExecute = nameof(CanRename))]
    [property: Command("Rename", Group, Label = "_Rename…", Icon = MaterialIcons.Edit,
        Description = "Rename the selected node", DefaultKeybinding = "F2")]
    private async Task RenameAsync()
    {
        if (Selected is not { } node) return;
        string? name = await Toolbox.Dialogs.AskTextAsync("Rename", "Name", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name) return;
        node.Name = node.Parent != null ? UniqueName(node.Parent, name.Trim(), except: node) : name.Trim();
    }

    private bool CanRename() => Selected != null;

    [RelayCommand(CanExecute = nameof(HasEditableSelection))]
    [property: Command("Duplicate", Group, Label = "_Duplicate", Icon = MaterialIcons.ContentCopy,
        Description = "Copy the selected node and everything in it", DefaultKeybinding = "Ctrl+D")]
    private void Duplicate()
    {
        if (Selected is not { Parent: { } parent } node) return;
        var copy = Toolbox.Serializer.Clone(node);
        copy.Name = UniqueName(parent, node.Name);
        parent.Children.Insert(parent.Children.IndexOf(node) + 1, copy);
        Toolbox.SelectedNode = copy;
    }

    [RelayCommand(CanExecute = nameof(HasEditableSelection))]
    [property: Command("Delete", ExplorerGroup, Label = "De_lete", Icon = MaterialIcons.Delete,
        Description = "Delete the selected node and everything in it", DefaultKeybinding = "Delete")]
    private async Task DeleteAsync()
    {
        if (Selected is not { Parent: { } parent } node) return;
        int contained = node.Descendants().Count();
        if (contained > 0 && !await Toolbox.Dialogs.ConfirmAsync("Delete",
                $"Delete '{node.Name}' and the {contained} node{(contained == 1 ? "" : "s")} in it?", "Delete"))
        {
            return;
        }

        int index = parent.Children.IndexOf(node);
        parent.Children.RemoveAt(index);
        // Select the next sibling, else the previous one, else the parent.
        Toolbox.SelectedNode = parent.Children.Count > 0 ? parent.Children[Math.Min(index, parent.Children.Count - 1)] : parent;
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    [property: Command("MoveUp", ExplorerGroup, Label = "Move _up", Icon = MaterialIcons.ArrowUpward,
        Description = "Move the selected node before its previous sibling", DefaultKeybinding = "Alt+Up")]
    private void MoveUp() => Move(-1);

    private bool CanMoveUp() => Selected is { Parent: { } parent } node && parent.Children.IndexOf(node) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    [property: Command("MoveDown", ExplorerGroup, Label = "Move do_wn", Icon = MaterialIcons.ArrowDownward,
        Description = "Move the selected node after its next sibling", DefaultKeybinding = "Alt+Down")]
    private void MoveDown() => Move(1);

    private bool CanMoveDown() => Selected is { Parent: { } parent } node && parent.Children.IndexOf(node) < parent.Children.Count - 1;

    private void Move(int delta)
    {
        if (Selected is not { Parent: { } parent } node) return;
        int index = parent.Children.IndexOf(node);
        int target = index + delta;
        if (target < 0 || target >= parent.Children.Count) return;
        parent.Children.Move(index, target);
        UpdateCanExecute();
    }

    #endregion

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Modules.Toolbox.SelectedNode) or nameof(Modules.Toolbox.Document)) UpdateCanExecute();
    }

    private void UpdateCanExecute()
    {
        RenameCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
