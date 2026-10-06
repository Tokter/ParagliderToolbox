using System.ComponentModel;
using System.Globalization;
using Atelier.Charts;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Infrastructure;
using ParagliderToolbox.Paraglider.Polar;

namespace ParagliderToolbox.Modules.Paraglider.Views;

/// <summary>
/// The detail view of a recorded polar: the polar chart with its key figures labeled (zoom with the wheel, pan by
/// dragging, double-click to fit), and a table of every control setting flown.
/// </summary>
public sealed class PolarDetailView : ContentControl
{
    private readonly PolarNode _node;
    private readonly PolarChart _chart;
    private readonly StackPanel _table = new StackPanel().Spacing(0);

    /// <summary>Initializes the view of <paramref name="node"/>.</summary>
    public PolarDetailView(PolarNode node, ParagliderActions actions)
    {
        _node = node;
        var chart = new XYChart { MinHeight = 320 };
        _chart = new PolarChart(chart, PolarTitle(node));

        var toolbar = new WrapPanel().Spacing(6, 6).Margin(8, 6).Children(
            new Button("Fit").Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip("Show the whole polar (double-click the chart or Home)").OnClick(() => chart.ResetView()),
            new Button("Export CSV…").Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip("Write the steady points and the samples as CSV").OnClick(() => _ = actions.ExportPolarCsvAsync(_node)),
            new TextBlock("Wheel: zoom · drag: pan · right-drag: zoom box · double-click: fit").BodySmall().Muted()
                .VerticalAlignment(VerticalAlignment.Center).Margin(12, 0));

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Stars(2), GridLength.Stars(1))
            .Children(
                toolbar,
                chart.Row(1).Margin(8, 0, 8, 8),
                new Border().Row(2).Margin(8, 0, 8, 8).Padding(12, 8).CornerRadius(10)
                    .Themed(Border.BackgroundProperty, c => c.SurfaceContainerLow)
                    .Child(new ScrollViewer().Content(_table)));
        Show();
    }

    private static string PolarTitle(PolarNode node) => node.Parent is { } glider ? $"{glider.Name} — {node.Name}" : node.Name;

    private void Show()
    {
        _chart.Chart.Title = PolarTitle(_node);
        _chart.Show(_node.Recording);
        BuildTable(_node.Recording);
    }

    private void BuildTable(PolarRecording recording)
    {
        _table.Clear();
        string[] headers = ["Setting", "Airspeed", "Sink", "Glide", "AoA", "Spread", ""];
        _table.Add(Row(headers, header: true));
        foreach (var p in recording.Points)
        {
            string state = p.IsStalled ? "stalled" : p.IsStable ? "steady" : "unsteady";
            _table.Add(Row(
            [
                p.Label,
                string.Create(CultureInfo.CurrentCulture, $"{p.Airspeed * 3.6:0.0} km/h"),
                string.Create(CultureInfo.CurrentCulture, $"{p.SinkRate:0.00} m/s"),
                float.IsFinite(p.GlideRatio) ? string.Create(CultureInfo.CurrentCulture, $"{p.GlideRatio:0.0}") : "–",
                string.Create(CultureInfo.CurrentCulture, $"{p.AngleOfAttack:0.0}°"),
                string.Create(CultureInfo.CurrentCulture, $"±{p.SinkSpread:0.00}"),
                state,
            ], header: false, muted: !p.IsStable));
        }
    }

    private static UIElement Row(string[] cells, bool header, bool muted = false)
    {
        var grid = new Grid().Columns(GridLength.Pixels(150), GridLength.Pixels(100), GridLength.Pixels(90), GridLength.Pixels(60),
            GridLength.Pixels(60), GridLength.Pixels(70), GridLength.Star).Margin(0, 2);
        for (int i = 0; i < cells.Length; i++)
        {
            var text = new TextBlock(cells[i]).Column(i);
            if (header) text.LabelMedium().Muted();
            else text.BodySmall().Muted(muted);
            grid.Add(text);
        }
        return grid;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _node.PropertyChanged += OnNodeChanged;
        Show();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _node.PropertyChanged -= OnNodeChanged;
        base.OnDetachedFromVisualTree();
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PolarNode.Recording)) Show();
        else if (e.PropertyName is nameof(PolarNode.Name)) _chart.Chart.Title = PolarTitle(_node);
    }
}
