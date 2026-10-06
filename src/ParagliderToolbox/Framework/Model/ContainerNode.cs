using System.Collections.Specialized;
using Atelier.Core.Inspection;

namespace ParagliderToolbox.Framework.Model;

/// <summary>
/// The base class of nodes that hold other nodes, such as <see cref="FolderNode"/> and <see cref="Project"/>.
/// </summary>
/// <remarks>
/// Derive [Inspectable] node classes from this class or <see cref="ProjectNode"/>, not from another [Inspectable] class:
/// the generated metadata already includes the base classes' properties.
/// </remarks>
public abstract class ContainerNode : ProjectNode
{
    /// <summary>Initializes a container.</summary>
    protected ContainerNode()
    {
        Children.CollectionChanged += OnContainerChildrenChanged;
    }

    /// <summary>Gets the number of nodes directly in the container.</summary>
    [InspectableProperty("Items", "General", Order = -50, IsReadOnly = true, Description = "The number of nodes directly in here.")]
    public int ItemCount => Children.Count;

    /// <inheritdoc/>
    /// <remarks>Containers accept every node except a project.</remarks>
    public override bool CanContain(Type childType) => !typeof(Project).IsAssignableFrom(childType);

    private void OnContainerChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(ItemCount));
}
