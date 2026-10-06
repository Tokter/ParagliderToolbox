using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Atelier.Core.Inspection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParagliderToolbox.Framework.Model;

/// <summary>
/// The base class of every object in a project: the items of the project tree, inspected in the property editor and
/// shown in the detail view.
/// </summary>
/// <remarks>
/// <para>
/// To add a kind of object, derive from this class (or from <see cref="FolderNode"/> to hold children), mark the class
/// <c>[Inspectable]</c> and <c>partial</c> so the property editor gets generated metadata, and register it with a
/// <see cref="NodeTypeRegistry"/> from a module (see <see cref="Modules.IToolboxModule"/>).
/// </para>
/// <para>
/// Properties are saved to the project file with System.Text.Json: every public property with a getter and a setter
/// (camel-cased), plus <see cref="Children"/>. Properties without a setter (computed values) are not saved; put
/// <see cref="JsonIgnoreAttribute"/> on settable properties that should not be. Raise
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> from setters (<see cref="ObservableObject.SetProperty{T}(ref T, T, string?)"/>):
/// that updates the views and marks the project as modified.
/// </para>
/// </remarks>
public abstract class ProjectNode : ObservableObject
{
    private string _name = string.Empty;
    private ProjectNode? _parent;

    /// <summary>Initializes a node with a new <see cref="Id"/>.</summary>
    protected ProjectNode()
    {
        Children.CollectionChanged += OnChildrenChanged;
    }

    /// <summary>Gets or sets the node's identity, kept across saving and loading (for references between nodes).</summary>
    [InspectableIgnore]
    [JsonPropertyOrder(-100)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the name shown in the project tree.</summary>
    [InspectableProperty("Name", "General", Order = -100, Description = "The name shown in the project tree.")]
    [JsonPropertyOrder(-90)]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    /// <summary>Gets the node this node is a child of, or <c>null</c> for the project and nodes not in a tree.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public ProjectNode? Parent
    {
        get => _parent;
        private set => SetProperty(ref _parent, value);
    }

    /// <summary>
    /// Gets the child nodes. Only nodes whose <see cref="CanContain"/> allows it should have children; adding a node
    /// removes it from its previous parent.
    /// </summary>
    [InspectableIgnore]
    [JsonPropertyOrder(1000)]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<ProjectNode> Children { get; } = [];

    /// <summary>Gets the project the node belongs to, or <c>null</c> when it is not part of one.</summary>
    [InspectableIgnore]
    [JsonIgnore]
    public Project? Project => this as Project ?? Parent?.Project;

    /// <summary>Occurs when a property of this node or of a node below it changed, or children were added or removed.</summary>
    public event EventHandler<NodeChangedEventArgs>? SubtreeChanged;

    /// <summary>Returns whether a node of <paramref name="childType"/> can be added as a child. Nodes are leaves by default.</summary>
    public virtual bool CanContain(Type childType) => false;

    /// <summary>Gets the nodes below this one, depth first.</summary>
    public IEnumerable<ProjectNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }

    /// <summary>Gets the parent, its parent and so on up to the root.</summary>
    public IEnumerable<ProjectNode> Ancestors()
    {
        for (var node = Parent; node != null; node = node.Parent) yield return node;
    }

    /// <summary>Returns whether <paramref name="node"/> is this node or one of its ancestors.</summary>
    public bool IsSelfOrAncestor(ProjectNode node) => node == this || Ancestors().Contains(node);

    /// <inheritdoc/>
    public override string ToString() => string.IsNullOrEmpty(Name) ? GetType().Name : Name;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // The parent link is structure, reported by the parent's children change.
        if (e.PropertyName != nameof(Parent)) NotifySubtreeChanged(new NodeChangedEventArgs(this, e.PropertyName));
    }

    private void NotifySubtreeChanged(NodeChangedEventArgs e)
    {
        for (var node = this; node != null; node = node.Parent)
        {
            node.SubtreeChanged?.Invoke(node, e);
        }
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Clear() doesn't report the removed items; whatever still points here is gone.
            foreach (var orphan in _adopted.Where(c => !Children.Contains(c)).ToList()) Release(orphan);
        }
        if (e.OldItems != null)
        {
            foreach (ProjectNode child in e.OldItems)
            {
                if (!Children.Contains(child)) Release(child);
            }
        }
        if (e.NewItems != null)
        {
            foreach (ProjectNode child in e.NewItems) Adopt(child);
        }
        NotifySubtreeChanged(new NodeChangedEventArgs(this, nameof(Children)));
    }

    private readonly HashSet<ProjectNode> _adopted = [];

    private void Adopt(ProjectNode child)
    {
        if (child.IsSelfOrAncestorOf(this))
        {
            throw new InvalidOperationException($"'{child}' can't be added below itself.");
        }
        if (child.Parent != null && child.Parent != this)
        {
            child.Parent.Children.Remove(child);
        }
        child.Parent = this;
        _adopted.Add(child);
    }

    private void Release(ProjectNode child)
    {
        _adopted.Remove(child);
        if (child.Parent == this) child.Parent = null;
    }

    private bool IsSelfOrAncestorOf(ProjectNode node) => node.IsSelfOrAncestor(this);
}

/// <summary>Describes a change in a project tree: the node that changed and which property (or <c>Children</c>).</summary>
public sealed class NodeChangedEventArgs(ProjectNode node, string? propertyName) : EventArgs
{
    /// <summary>Gets the node whose property or children changed.</summary>
    public ProjectNode Node { get; } = node;

    /// <summary>Gets the name of the changed property; <c>Children</c> for added, removed or moved children.</summary>
    public string? PropertyName { get; } = propertyName;
}
