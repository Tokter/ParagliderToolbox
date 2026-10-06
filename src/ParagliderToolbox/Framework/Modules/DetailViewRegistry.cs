using Atelier.Core.Tree;
using ParagliderToolbox.Framework.Model;

namespace ParagliderToolbox.Framework.Modules;

/// <summary>
/// The detail views of node types: the object specific UI the Detail view shows for the selected node.
/// </summary>
/// <remarks>
/// A view registered for a base class is used for derived classes without their own, so a view for
/// <see cref="ContainerNode"/> covers folders and the project unless they have a more specific one.
/// </remarks>
public sealed class DetailViewRegistry
{
    private readonly Dictionary<Type, Func<ProjectNode, UIElement>> _factories = [];

    /// <summary>Registers the detail view of <typeparamref name="TNode"/> nodes, replacing an earlier one.</summary>
    /// <param name="factory">Creates the view for a node; called every time such a node is selected.</param>
    /// <returns>This registry, for chaining.</returns>
    public DetailViewRegistry Register<TNode>(Func<TNode, UIElement> factory) where TNode : ProjectNode
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[typeof(TNode)] = node => factory((TNode)node);
        return this;
    }

    /// <summary>Returns whether a detail view is registered for <paramref name="node"/>'s class or a base class.</summary>
    public bool HasView(ProjectNode node) => FindFactory(node.GetType()) != null;

    /// <summary>Creates the detail view of <paramref name="node"/>, or returns <c>null</c> when none is registered.</summary>
    public UIElement? Create(ProjectNode node) => FindFactory(node.GetType())?.Invoke(node);

    private Func<ProjectNode, UIElement>? FindFactory(Type type)
    {
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            if (_factories.TryGetValue(t, out var factory)) return factory;
        }
        return null;
    }
}
