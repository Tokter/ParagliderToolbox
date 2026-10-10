using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Threading;
using Atelier.Core.Tree;
using Atelier.Graphics3D;
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
/// cancels, L tints the levels of detail, T toggles the texture). The viewport has its own navigation: middle drag
/// orbits, Shift+middle drag pans, the wheel zooms, Home frames, Shift+Z wireframe.
/// </remarks>
public sealed class TerrainDetailView : KeybindingHandler
{
    private readonly TerrainPreview _preview;
    private readonly Viewport3D _viewport;
    private bool _levelsQueued;

    /// <summary>Initializes the view of <paramref name="preview"/>.</summary>
    public TerrainDetailView(TerrainPreview preview, TerrainActions actions) : base(TerrainPreview.Group)
    {
        _preview = preview;
        DataContext = preview;

        _viewport = new Viewport3D().Scene(preview.Scene).ShowGrid(false);
        _viewport.Camera.FieldOfView = 0.9f;
        _viewport.Camera.Changed += (_, _) => QueueLevels();
        _viewport.Rendered += (_, _) =>
        {
            // Meshes still to make: choose again next frame.
            if (_preview.UpdateLevels(_viewport.Camera, Math.Max(1, _viewport.PixelSize.Height))) _viewport.RequestRender();
            KeepClippingInRange();
        };
        _preview.FirstTilesShown += (_, _) => Frame();
        _preview.TilesChanged += (_, _) => QueueLevels();

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(actions),
                Outdated().Row(1),
                _viewport.Row(2),
                new ProgressBar().Row(3).Margin(12, 4).BindIsVisible(preview, p => p.IsBuilding).BindValue(preview, p => (float)(p.Progress * 100)),
                new TextBlock().Row(4).BodySmall().Muted().Margin(12, 4, 12, 0).TextTrimming()
                    .BindText(preview, p => p.Status + (p.View.Length > 0 && !p.IsBuilding ? " · " + p.View : "")),
                new TextBlock().Row(5).BodySmall().Muted().Margin(12, 2, 12, 6).TextTrimming()
                    .BindText(preview, p => p.Sources)
                    .BindToolTip(preview, p => p.Model?.Attribution ?? ""));

        if (preview.Tiles.Bounds() != null) Dispatcher.Post(Frame);
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
                .Content(new Icon().Kind(MaterialIconKind.CenterFocusStrong).Size(18)).ToolTip("Frame the terrain (Home)").OnClick(Frame),
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

    private void QueueLevels()
    {
        if (_levelsQueued) return;
        _levelsQueued = true;
        Dispatcher.Post(() =>
        {
            _levelsQueued = false;
            _viewport.RequestRender();
        });
    }

    private void Frame()
    {
        if (_preview.Tiles.Bounds() is not { } bounds) return;
        // A three-quarter view from the south and above, like the view from a launch. Framing fits the box's diagonal;
        // a flat terrain fills the view from closer (its far corners just outside).
        _viewport.Camera.SetAngles(0.5f, 0.4f);
        _viewport.Camera.Frame(bounds.Min, bounds.Max);
        _viewport.Camera.Distance *= 0.5f;
        _viewport.RequestRender();
    }

    // The orbit camera's clipping planes follow how close it is, so the ground doesn't clip when zoomed in.
    private void KeepClippingInRange()
    {
        var camera = _viewport.Camera;
        float extent = (float)(_preview.Tiles.Layout?.Extent ?? 1000);
        float near = Math.Clamp(camera.Distance / 2000, 0.05f, 50);
        float far = Math.Max(extent * 3, camera.Distance * 20);
        if (Math.Abs(camera.Near - near) > near * 0.25f) camera.Near = near;
        if (Math.Abs(camera.Far - far) > far * 0.25f) camera.Far = far;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _preview.PropertyChanged += OnPreviewPropertyChanged;
        QueueLevels();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _preview.PropertyChanged -= OnPreviewPropertyChanged;
        base.OnDetachedFromVisualTree();
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerrainPreview.ShowLevels) or nameof(TerrainPreview.Textured) or nameof(TerrainPreview.Model)) QueueLevels();
    }
}
