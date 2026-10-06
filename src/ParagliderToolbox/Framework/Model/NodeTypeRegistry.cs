using Atelier.Controls;

namespace ParagliderToolbox.Framework.Model;

/// <summary>
/// A kind of <see cref="ProjectNode"/>: the id it is saved under, how it is shown in menus and the tree, and how to
/// create one.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is written to the project file as the node's <c>$type</c>, so keep it stable when the class is
/// renamed or moved.
/// </remarks>
public sealed class NodeType
{
    internal NodeType(string id, Type clrType, Func<ProjectNode> create, string displayName, MaterialIconKind icon)
    {
        Id = id;
        ClrType = clrType;
        Create = create;
        DisplayName = displayName;
        Icon = icon;
    }

    /// <summary>Gets the stable id saved in project files, e.g. <c>"folder"</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the node class.</summary>
    public Type ClrType { get; }

    /// <summary>Gets the factory of new nodes.</summary>
    public Func<ProjectNode> Create { get; }

    /// <summary>Gets the name shown in menus, e.g. "Folder".</summary>
    public string DisplayName { get; }

    /// <summary>Gets the icon shown in the tree and menus.</summary>
    public MaterialIconKind Icon { get; }

    /// <summary>Gets or sets the group the Add menu lists the type under, or <c>null</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Gets or sets a short description, shown in tooltips and the command palette.</summary>
    public string? Description { get; init; }

    /// <summary>Gets or sets whether users can add nodes of this type (the project root, for example, can't be added).</summary>
    public bool IsCreatable { get; init; } = true;

    /// <summary>Gets or sets a keyboard shortcut for adding a node of this type, e.g. <c>"Ctrl+Shift+N"</c>.</summary>
    public string? DefaultKeybinding { get; init; }

    /// <inheritdoc/>
    public override string ToString() => DisplayName;
}

/// <summary>The kinds of nodes a project can contain, registered by modules.</summary>
public sealed class NodeTypeRegistry
{
    private readonly List<NodeType> _types = [];
    private readonly Dictionary<string, NodeType> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, NodeType> _byClrType = [];

    /// <summary>Gets the registered node types in registration order.</summary>
    public IReadOnlyList<NodeType> Types => _types;

    /// <summary>Registers the node class <typeparamref name="T"/> under <paramref name="id"/>.</summary>
    /// <param name="id">The stable id saved in project files.</param>
    /// <param name="displayName">The name shown in menus.</param>
    /// <param name="icon">The icon shown in the tree and menus.</param>
    /// <param name="category">The group the Add menu lists the type under, or <c>null</c>.</param>
    /// <param name="description">A short description for tooltips and the command palette.</param>
    /// <param name="isCreatable">Whether users can add nodes of this type.</param>
    /// <param name="defaultKeybinding">A shortcut for adding a node of this type, or <c>null</c>.</param>
    /// <returns>The registered type.</returns>
    public NodeType Register<T>(string id, string displayName, MaterialIconKind icon,
        string? category = null, string? description = null, bool isCreatable = true, string? defaultKeybinding = null)
        where T : ProjectNode, new() =>
        Register(new NodeType(id, typeof(T), static () => new T(), displayName, icon)
        {
            Category = category,
            Description = description,
            IsCreatable = isCreatable,
            DefaultKeybinding = defaultKeybinding,
        });

    private NodeType Register(NodeType type)
    {
        if (string.IsNullOrWhiteSpace(type.Id)) throw new ArgumentException("A node type needs an id.");
        if (_byId.ContainsKey(type.Id)) throw new InvalidOperationException($"A node type with the id '{type.Id}' is already registered.");
        if (_byClrType.ContainsKey(type.ClrType)) throw new InvalidOperationException($"{type.ClrType.Name} is already registered.");
        _types.Add(type);
        _byId[type.Id] = type;
        _byClrType[type.ClrType] = type;
        return type;
    }

    /// <summary>Gets the type registered under <paramref name="id"/>, or <c>null</c>.</summary>
    public NodeType? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Gets the type of <paramref name="node"/>'s class, or <c>null</c> when it isn't registered.</summary>
    public NodeType? Find(ProjectNode node) => _byClrType.GetValueOrDefault(node.GetType());

    /// <summary>Gets the icon of <paramref name="node"/>'s type, or a generic one.</summary>
    public MaterialIconKind IconOf(ProjectNode node) => Find(node)?.Icon ?? MaterialIconKind.Description;

    /// <summary>Gets the types users can add below <paramref name="parent"/>.</summary>
    public IEnumerable<NodeType> CreatableIn(ProjectNode parent) =>
        _types.Where(t => t.IsCreatable && parent.CanContain(t.ClrType));
}
