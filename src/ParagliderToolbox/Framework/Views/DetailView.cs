using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Framework.Views;

/// <summary>
/// Shows the object specific UI of the selected node, from <see cref="Toolbox.DetailViews"/>; a hint when the node has
/// none.
/// </summary>
/// <remarks>
/// The node's view is created when the node is selected and dropped when another one is, so detail views keep their
/// state in the node (or a view model it owns), not in the view.
/// </remarks>
public sealed class DetailView : ContentControl
{
    private readonly Toolbox _toolbox;
    private ProjectNode? _shownNode;
    private bool _hasContent;

    /// <summary>Initializes the detail view for <paramref name="toolbox"/>.</summary>
    public DetailView(Toolbox toolbox)
    {
        _toolbox = toolbox;
        ShowNode();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _toolbox.PropertyChanged += OnToolboxPropertyChanged;
        ShowNode();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _toolbox.PropertyChanged -= OnToolboxPropertyChanged;
        base.OnDetachedFromVisualTree();
    }

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Toolbox.SelectedNode)) ShowNode();
    }

    private void ShowNode()
    {
        var node = _toolbox.SelectedNode;
        if (node == _shownNode && _hasContent) return;
        _shownNode = node;
        _hasContent = true;
        Content = node == null ? Hint(MaterialIconKind.AdsClick, "Select an object in the project to see it here.")
            : _toolbox.DetailViews.Create(node)
              ?? Hint(_toolbox.NodeTypes.IconOf(node), $"{_toolbox.NodeTypes.Find(node)?.DisplayName ?? node.GetType().Name} objects have no detail view; edit them in the properties.");
    }

    /// <summary>Creates a centered icon and message, for views with nothing to show.</summary>
    public static UIElement Hint(MaterialIconKind icon, string message) =>
        new StackPanel()
            .Spacing(12)
            .MaxWidth(360)
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Center)
            .Children(
                new Icon().Kind(icon).Size(48).Opacity(0.5f).HorizontalAlignment(HorizontalAlignment.Center),
                new TextBlock(message).BodyMedium().Muted().TextWrapping().TextAlignment(TextAlignment.Center));
}
