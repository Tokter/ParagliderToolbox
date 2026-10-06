using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Infrastructure;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Framework.Views;

/// <summary>
/// The property editor: the selected node's properties in a <see cref="PropertyGrid"/>, under a header with its type
/// and name.
/// </summary>
/// <remarks>
/// The grid shows the properties of <c>[Inspectable]</c> node classes (see <see cref="ProjectNode"/>); editors for
/// custom property types come from <see cref="Toolbox.PropertyEditors"/>.
/// </remarks>
public sealed class PropertiesView : ContentControl
{
    private readonly Toolbox _toolbox;
    private readonly PropertyGrid _grid;
    private readonly Icon _typeIcon;
    private readonly TextBlock _typeName;
    private readonly TextBlock _nodeName;
    private ProjectNode? _shownNode;

    /// <summary>Initializes the property editor for <paramref name="toolbox"/>.</summary>
    public PropertiesView(Toolbox toolbox)
    {
        _toolbox = toolbox;
        _grid = new PropertyGrid().IsDescriptionVisible(true).LabelWidth(140);
        foreach (string category in toolbox.PropertyCategoryOrder.Distinct()) _grid.CategoryOrder.Add(category);
        ToolboxPropertyEditors.Register(_grid.EditorRegistry);
        foreach (var setup in toolbox.PropertyEditors) setup(_grid.EditorRegistry);

        _typeIcon = new Icon().Size(20).VerticalAlignment(VerticalAlignment.Center);
        _typeName = new TextBlock().LabelMedium().Muted().VerticalAlignment(VerticalAlignment.Center);
        _nodeName = new TextBlock().TitleSmall().VerticalAlignment(VerticalAlignment.Center).TextTrimming();

        var header = new Grid()
            .Columns(GridLength.Auto, GridLength.Star, GridLength.Auto)
            .ColumnSpacing(8)
            .Margin(12, 8)
            .Children(_typeIcon, _nodeName.Column(1), _typeName.Column(2));

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Star)
            .Children(header, _grid.Row(1));
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
        Watch(null);
        base.OnDetachedFromVisualTree();
    }

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Toolbox.SelectedNode)) ShowNode();
    }

    private void ShowNode()
    {
        var node = _toolbox.SelectedNode;
        Watch(node);
        _grid.SelectedObject = node;
        _typeIcon.Kind = node != null ? _toolbox.NodeTypes.IconOf(node) : MaterialIconKind.Info;
        _typeName.Text = node != null ? _toolbox.NodeTypes.Find(node)?.DisplayName ?? node.GetType().Name : string.Empty;
        _nodeName.Text = node?.Name ?? "Nothing selected";
    }

    private void Watch(ProjectNode? node)
    {
        if (_shownNode == node) return;
        if (_shownNode != null) _shownNode.PropertyChanged -= OnNodePropertyChanged;
        _shownNode = node;
        if (node != null) node.PropertyChanged += OnNodePropertyChanged;
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectNode.Name) && sender is ProjectNode node && node == _shownNode) _nodeName.Text = node.Name;
    }
}
