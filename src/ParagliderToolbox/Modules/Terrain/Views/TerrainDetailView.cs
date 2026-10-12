using System.ComponentModel;
using System.Globalization;
using Atelier.Controls;
using Atelier.Core.Events;
using Atelier.Core.Keybinding;
using Atelier.Core.Primitives;
using Atelier.Core.Threading;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Infrastructure;

namespace ParagliderToolbox.Modules.Terrain.Views;

/// <summary>
/// The detail view of a terrain: the 3D view with its levels of detail chosen for the camera, the build's progress, and
/// where the data comes from (the sources' credits).
/// </summary>
/// <remarks>
/// The view is a <see cref="KeybindingHandler"/> for the <see cref="TerrainPreview.Group"/> commands (G builds or
/// cancels, L tints the levels of detail, T toggles the texture) and the <see cref="TerrainFlight.Group"/> commands,
/// which fly the camera through the landscape: W, A, S, D move it while held (where it looks, back, left, right), E and Q
/// up and down, Shift faster, a right drag looks around. The viewport has its own navigation: middle drag orbits,
/// Shift+middle drag pans, the wheel zooms, Home frames, Shift+Z wireframe.
/// </remarks>
public sealed class TerrainDetailView : KeybindingHandler
{
    private readonly TerrainPreview _preview;
    private readonly TerrainViewport _viewport;

    /// <summary>Initializes the view of <paramref name="preview"/>.</summary>
    public TerrainDetailView(TerrainPreview preview, TerrainActions actions) : base(TerrainPreview.Group)
    {
        _preview = preview;
        DataContext = preview;

        _viewport = new TerrainViewport(preview.Tiles);
        AdditionalScopes.Add(new KeybindingScope(TerrainFlight.Group, _viewport.Flight));
        _viewport.LevelsChosen += (_, _) => _preview.View = string.Create(CultureInfo.CurrentCulture,
            $"showing {preview.Tiles.ShownTiles} tiles, {preview.Tiles.ShownTriangles / 1000.0:0.#}k triangles");
        _preview.FirstTilesShown += (_, _) => _viewport.Frame();
        _preview.TilesChanged += (_, _) => _viewport.RequestRender();

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(actions),
                Outdated().Row(1),
                _viewport.Row(2),
                // Busy until the first tile is in (the coarsest can take a while), then how many tiles are built.
                new ProgressBar().Row(3).Margin(12, 4).BindIsVisible(preview, p => p.IsBuilding)
                    .BindIsIndeterminate(preview, p => p.IsBuilding && p.Progress <= 0)
                    .BindValue(preview, p => (float)(p.Progress * 100)),
                new TextBlock().Row(4).BodySmall().Muted().Margin(12, 4, 12, 0).TextTrimming()
                    .BindText(preview, p => p.Status + (p.View.Length > 0 && !p.IsBuilding ? " · " + p.View : "")),
                new TextBlock().Row(5).BodySmall().Muted().Margin(12, 2, 12, 6).TextTrimming()
                    .BindText(preview, p => p.IsBuilding && p.StatusNote.Length > 0 ? p.StatusNote : p.Sources)
                    .BindToolTip(preview, p => p.Model?.Attribution ?? ""));

        if (preview.Tiles.Bounds() != null) Dispatcher.Post(_viewport.Frame);
    }

    private UIElement Toolbar(TerrainActions actions)
    {
        static ToggleButton Toggle(string text, MaterialIconKind icon, TerrainPreview preview, Func<TerrainPreview, bool> get, Action<TerrainPreview, bool> set, string tip) =>
            new ToggleButton()
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(icon).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock(text).VerticalAlignment(VerticalAlignment.Center)))
                .Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip(tip)
                .BindIsChecked(preview, get, set);

        var generate = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_preview.GenerateCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .ToolTip("Build the terrain from its settings, downloading what isn't cached (G); again to cancel")
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.IsBuilding ? MaterialIconKind.Stop : MaterialIconKind.Refresh),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.IsBuilding ? "Cancel" : "Generate")));

        return new WrapPanel().Spacing(6, 6).Margin(8, 6).Children(
            generate,
            new Border().Width(12),
            Toggle("Texture", MaterialIconKind.Texture, _preview, p => p.Textured, (p, v) => p.Textured = v, "Show the texture or a plain surface (T)"),
            Toggle("Levels", MaterialIconKind.Layers, _preview, p => p.ShowLevels, (p, v) => p.ShowLevels = v, "Tint the tiles by their level of detail (L): finer tiles where the camera is close"),
            new Button().Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(MaterialIconKind.CenterFocusStrong).Size(18)).ToolTip("Frame the terrain (Home)").OnClick(_viewport.Frame),
            new Border().Width(12),
            new Button().Variant(ButtonVariant.Text).Command(actions.ExportTilesCommand).Height(30).MinHeight(0).Padding(8, 0),
            new Button().Variant(ButtonVariant.Text).Command(actions.ExportGlbCommand).Height(30).MinHeight(0).Padding(8, 0));
    }

    // While the terrain shown is older than the settings.
    private UIElement Outdated() =>
        new Border()
            .Margin(8, 0, 8, 4)
            .Padding(12, 6)
            .CornerRadius(8)
            .Themed(Border.BackgroundProperty, c => c.SecondaryContainer)
            .BindIsVisible(_preview, p => p.IsOutdated && !p.IsBuilding)
            .Child(new TextBlock("The settings changed: Generate (G) builds the terrain again.").BodySmall());

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

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerrainPreview.ShowLevels) or nameof(TerrainPreview.Textured) or nameof(TerrainPreview.Model)) _viewport.RequestRender();
    }
}
