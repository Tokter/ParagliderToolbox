using System.ComponentModel;
using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Events;
using Atelier.Core.Keybinding;
using Atelier.Core.Primitives;
using Atelier.Core.Threading;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Infrastructure;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Modules.Terrain.Views;
using ParagliderToolbox.Terrain.Features;

namespace ParagliderToolbox.Modules.Features.Views;

/// <summary>
/// The detail view of features: their terrain in 3D with the area to search outlined and the features found on it
/// (trees, roads, buildings, boulders, areas, the ground cover map), what the colors mean, and the search's progress.
/// </summary>
/// <remarks>
/// The view is a <see cref="KeybindingHandler"/> for the <see cref="FeaturesPreview.Group"/> commands (R detects or
/// cancels, T, P, B, K, O, L, I and M toggle the overlays) and the <see cref="TerrainFlight.Group"/> commands, which fly the
/// camera (W, A, S, D, E, Q, Shift, right drag).
/// </remarks>
public sealed class FeaturesDetailView : KeybindingHandler
{
    private readonly FeaturesPreview _preview;
    private readonly TerrainViewport _viewport;

    /// <summary>Initializes the view of <paramref name="preview"/>.</summary>
    public FeaturesDetailView(FeaturesPreview preview, FeaturesActions actions) : base(FeaturesPreview.Group)
    {
        _preview = preview;
        DataContext = preview;

        _viewport = new TerrainViewport(preview.Tiles);
        AdditionalScopes.Add(new KeybindingScope(TerrainFlight.Group, _viewport.Flight));
        _preview.TerrainShown += (_, _) => FrameArea();
        _preview.OverlaysChanged += (_, _) => _viewport.RequestRender();

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(actions),
                Banner("These features aren't under a terrain: add features to a terrain (select it, then Add features).", p => p.HasNoTerrain).Row(1),
                Banner("The settings changed: Detect (R) looks for the features again.", p => p.IsOutdated && !p.HasNoTerrain).Row(2),
                _viewport.Row(3),
                new ProgressBar().Row(4).Margin(12, 4).BindIsVisible(preview, p => p.IsRunning)
                    .BindIsIndeterminate(preview, p => p.IsRunning && p.Progress <= 0)
                    .BindValue(preview, p => (float)(p.Progress * 100)),
                new TextBlock().Row(5).BodySmall().Muted().Margin(12, 4, 12, 0).TextTrimming().BindText(preview, p => p.Status),
                Legend().Row(6).Margin(12, 4, 12, 8));
        if (preview.Tiles.Bounds() != null) Dispatcher.Post(FrameArea);
    }

    private UIElement Toolbar(FeaturesActions actions)
    {
        ToggleButton Toggle(string text, MaterialIconKind icon, Func<FeaturesPreview, bool> get, Action<FeaturesPreview, bool> set, string tip) =>
            new ToggleButton()
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(icon).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock(text).VerticalAlignment(VerticalAlignment.Center)))
                .Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip(tip)
                .BindIsChecked(_preview, get, set);

        var detect = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_preview.DetectCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .ToolTip("Look for the features in the outlined square (R); again to cancel")
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.IsRunning ? MaterialIconKind.Stop : MaterialIconKind.ImageSearch),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.IsRunning ? "Cancel" : "Detect")));

        return new WrapPanel().Spacing(6, 6).Margin(8, 6).Children(
            detect,
            new Border().Width(10),
            Toggle("Trees", MaterialIconKind.Forest, p => p.ShowTrees, (p, v) => p.ShowTrees = v, "The trees: cones are conifers (T)"),
            Toggle("Roads", MaterialIconKind.Route, p => p.ShowRoads, (p, v) => p.ShowRoads = v, "The roads and paths by kind, and their junctions (P)"),
            Toggle("Buildings", MaterialIconKind.House, p => p.ShowBuildings, (p, v) => p.ShowBuildings = v, "The buildings (B)"),
            Toggle("Boulders", MaterialIconKind.Landslide, p => p.ShowRocks, (p, v) => p.ShowRocks = v, "The boulders (K)"),
            Toggle("Areas", MaterialIconKind.Polyline, p => p.ShowAreas, (p, v) => p.ShowAreas = v, "The outlines of forest, rock, cliffs, gravel, asphalt, water and snow (O)"),
            Toggle("Ground cover", MaterialIconKind.Map, p => p.ShowLandCover, (p, v) => p.ShowLandCover = v, "What covers every square meter (L)"),
            Toggle("Lines", MaterialIconKind.Timeline, p => p.ShowLines, (p, v) => p.ShowLines = v,
                "Waterways, power lines and aerialways with their towers, railways, fences and walls, cliffs' edges (I)"),
            Toggle("Landmarks", MaterialIconKind.Place, p => p.ShowLandmarks, (p, v) => p.ShowLandmarks = v,
                "Takeoffs (green), landings (blue), masts and towers (red), peaks (white), huts, signposts … (yellow) (M)"),
            new Border().Width(10),
            new Button().Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(MaterialIconKind.CenterFocusStrong).Size(18)).ToolTip("Look at the area").OnClick(FrameArea),
            new Button().Variant(ButtonVariant.Text).Command(actions.ExportCommand).Height(30).MinHeight(0).Padding(8, 0));
    }

    private UIElement Banner(string text, Func<FeaturesPreview, bool> visible) =>
        new Border()
            .Margin(8, 0, 8, 4)
            .Padding(12, 6)
            .CornerRadius(8)
            .Themed(Border.BackgroundProperty, c => c.SecondaryContainer)
            .BindIsVisible(_preview, visible)
            .Child(new TextBlock(text).BodySmall());

    // What the colors mean: the road kinds, the tree kinds and the areas.
    private static UIElement Legend()
    {
        var legend = new WrapPanel().Spacing(2, 4);
        void Scale<T>(string title, IReadOnlyDictionary<T, Vector3> colors) where T : struct, Enum
        {
            legend.Add(new TextBlock($"{title}:").BodySmall().Muted().Margin(0, 0, 8, 0).VerticalAlignment(VerticalAlignment.Center));
            foreach (var (value, color) in colors)
            {
                var swatch = Color.FromRgb((byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255));
                legend.Add(new Border().Width(14).Height(14).CornerRadius(3).Background(swatch).VerticalAlignment(VerticalAlignment.Center));
                legend.Add(new TextBlock(Words(value.ToString())).LabelSmall().Margin(3, 0, 10, 0).VerticalAlignment(VerticalAlignment.Center));
            }
            legend.Add(new Border().Width(16));
        }
        Scale("Roads", FeaturesScene.RoadColors);
        Scale("Trees", FeaturesScene.TreeColors);
        Scale("Lines", FeaturesScene.LineColors);
        Scale("Areas", FeaturesScene.AreaColors);
        return legend;
    }

    // "MainRoad" reads "main road".
    private static string Words(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : i == 0 ? char.ToLowerInvariant(c).ToString() : c.ToString()));

    // Looks at the area from the south and above, as the terrain view looks at the terrain.
    private void FrameArea()
    {
        if (_preview.Area is not { } area || _preview.Tiles.Bounds() is not { } bounds)
        {
            _viewport.Frame();
            return;
        }
        float x0 = (float)area.X0, z0 = (float)area.Z0, x1 = (float)(area.X0 + area.Width), z1 = (float)(area.Z0 + area.Depth);
        var heights = new[] { (x0, z0), (x1, z0), (x0, z1), (x1, z1), ((x0 + x1) / 2, (z0 + z1) / 2) }
            .Select(p => _preview.Tiles.HeightAt(p.Item1, p.Item2) ?? bounds.Min.Y).ToArray();
        var camera = _viewport.Viewport.Camera;
        camera.SetAngles(0.5f, 0.5f);
        camera.Frame(new Vector3(x0, heights.Min(), z0), new Vector3(x1, heights.Max(), z1));
        camera.Distance *= 0.7f;
        _viewport.RequestRender();
    }

    /// <inheritdoc/>
    public override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && _viewport.HandleKeyDown(e)) e.Handled = true;
    }

    /// <inheritdoc/>
    public override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        _viewport.HandleKeyUp(e);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _preview.PropertyChanged += OnPreviewPropertyChanged;
        FocusManager.FocusChanged += OnFocusChanged;
        _viewport.RequestRender();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _preview.PropertyChanged -= OnPreviewPropertyChanged;
        FocusManager.FocusChanged -= OnFocusChanged;
        _viewport.ReleaseKeys();
        base.OnDetachedFromVisualTree();
    }

    // Keys released elsewhere don't reach the view: when the focus leaves it, the camera stops.
    private void OnFocusChanged(UIElement? previous, UIElement? focused)
    {
        if (focused is null || (focused != this && !focused.IsDescendantOf(this))) _viewport.ReleaseKeys();
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e) => _viewport.RequestRender();
}
