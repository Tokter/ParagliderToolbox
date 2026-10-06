using System.Collections.Specialized;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Modules.Core;

/// <summary>
/// The detail view of folders (and other containers without their own): the nodes in it as tiles; clicking one selects
/// it.
/// </summary>
public class ContainerDetailView : ContentControl
{
    private readonly ContainerNode _container;
    private readonly WrapPanel _tiles = new WrapPanel().Spacing(12, 12);
    private readonly ContentControl _body = new();

    /// <summary>Initializes the view of <paramref name="container"/>.</summary>
    public ContainerDetailView(Toolbox toolbox, ContainerNode container)
    {
        Toolbox = toolbox;
        _container = container;
        Content = new ScrollViewer().Content(new StackPanel().Margin(24).Spacing(16).Children(CreateHeader(), _body));
        Rebuild();
    }

    /// <summary>Gets the toolbox.</summary>
    protected Toolbox Toolbox { get; }

    /// <summary>Creates what is shown above the tiles: the container's name.</summary>
    protected virtual UIElement CreateHeader() =>
        new TextBlock().HeadlineSmall().BindText(_container, c => c.Name);

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _container.Children.CollectionChanged += OnChildrenChanged;
        Rebuild();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _container.Children.CollectionChanged -= OnChildrenChanged;
        base.OnDetachedFromVisualTree();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _tiles.Clear();
        foreach (var child in _container.Children) _tiles.Add(Tile(child));
        _body.Content = _container.Children.Count > 0
            ? _tiles
            : new TextBlock("Nothing in here yet. Add objects with the + button of the project tree, the Edit menu or the command palette (Ctrl+Shift+P).")
                .BodyMedium().Muted().TextWrapping();
    }

    private UIElement Tile(ProjectNode node)
    {
        var type = Toolbox.NodeTypes.Find(node);
        return new Button()
            .Variant(ButtonVariant.Outlined)
            .Width(180).Height(96)
            .ToolTip(type?.Description ?? type?.DisplayName ?? node.GetType().Name)
            .OnClick(() => Toolbox.SelectedNode = node)
            .Content(new StackPanel().Spacing(6).HorizontalAlignment(HorizontalAlignment.Center).Children(
                new Icon().Kind(Toolbox.NodeTypes.IconOf(node)).Size(32).HorizontalAlignment(HorizontalAlignment.Center),
                new TextBlock().LabelLarge().TextTrimming().HorizontalAlignment(HorizontalAlignment.Center).BindText(node, n => n.Name),
                new TextBlock(type?.DisplayName ?? node.GetType().Name).LabelSmall().Muted().HorizontalAlignment(HorizontalAlignment.Center)));
    }
}
