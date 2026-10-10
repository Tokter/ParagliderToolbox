using System.ComponentModel;
using System.Diagnostics;
using Atelier.Controls;
using Atelier.Core.Events;
using Atelier.Core.Keybinding;
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
/// cancels, L tints the levels of detail, T toggles the texture) and the <see cref="TerrainFlight.Group"/> commands,
/// which fly the camera through the landscape: W, A, S, D move it while held (where it looks, back, left, right), E and Q
/// up and down, Shift faster, a right drag looks around. The viewport has its own navigation: middle drag orbits,
/// Shift+middle drag pans, the wheel zooms, Home frames, Shift+Z wireframe.
/// </remarks>
public sealed class TerrainDetailView : KeybindingHandler
{
    private readonly TerrainPreview _preview;
    private readonly Viewport3D _viewport;
    private readonly TerrainFlight _flight;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private bool _levelsQueued;

    /// <summary>Initializes the view of <paramref name="preview"/>.</summary>
    public TerrainDetailView(TerrainPreview preview, TerrainActions actions) : base(TerrainPreview.Group)
    {
        _preview = preview;
        DataContext = preview;

        _viewport = new Viewport3D().Scene(preview.Scene).ShowGrid(false);
        _viewport.Camera.FieldOfView = 0.9f;
        _viewport.Camera.Changed += (_, _) => QueueLevels();
        TerrainFlightCommands.Register();
        _flight = new TerrainFlight(_viewport.Camera, (x, z) => _preview.Tiles.HeightAt(x, z));
        AdditionalScopes.Add(new KeybindingScope(TerrainFlight.Group, _flight));
        _viewport.Rendered += (_, _) =>
        {
            double now = _clock.Elapsed.TotalSeconds;
            bool flying = _flight.Update((float)(now - _lastFrame));
            _lastFrame = now;
            // Flying, or meshes still to make: another frame.
            if (_preview.UpdateLevels(_viewport.Camera, Math.Max(1, _viewport.PixelSize.Height), _flight) || flying) _viewport.RequestRender();
            KeepClippingInRange();
        };
        _preview.FirstTilesShown += (_, _) => Frame();
        _preview.TilesChanged += (_, _) => QueueLevels();

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(actions),
                Outdated().Row(1),
                new Grid().Row(2).Children(_viewport, FlightOverlay()),
                // Busy until the first tile is in (the coarsest can take a while), then how many tiles are built.
                new ProgressBar().Row(3).Margin(12, 4).BindIsVisible(preview, p => p.IsBuilding)
                    .BindIsIndeterminate(preview, p => p.IsBuilding && p.Progress <= 0)
                    .BindValue(preview, p => (float)(p.Progress * 100)),
                new TextBlock().Row(4).BodySmall().Muted().Margin(12, 4, 12, 0).TextTrimming()
                    .BindText(preview, p => p.Status + (p.View.Length > 0 && !p.IsBuilding ? " · " + p.View : "")),
                new TextBlock().Row(5).BodySmall().Muted().Margin(12, 2, 12, 6).TextTrimming()
                    .BindText(preview, p => p.IsBuilding && p.StatusNote.Length > 0 ? p.StatusNote : p.Sources)
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

    // Over the viewport's top left corner: the camera's altitude, height above the ground and speed, and until the first
    // flight of the session, the keys that fly (as currently bound).
    private UIElement FlightOverlay()
    {
        static string Key(string name)
        {
            string gesture = KeybindingManager.FindCommand(TerrainFlight.Group, name)?.Keybinding ?? "";
            // "RightDrag" reads "right drag".
            return gesture.EndsWith("Drag", StringComparison.Ordinal) ? gesture[..^4].ToLowerInvariant() + " drag" : gesture;
        }
        var white = Color.FromRgb(255, 255, 255);
        _hint = new TextBlock(
                $"{Key("FlyForward")} {Key("FlyLeft")} {Key("FlyBack")} {Key("FlyRight")} fly · {Key("FlyUp")} {Key("FlyDown")} up, down · Shift faster · {Key("LookAround")} looks around")
            .LabelSmall().Foreground(Color.FromArgb(0xC8, 255, 255, 255))
            .IsVisible(!s_hasFlown);
        return new Border()
            .HorizontalAlignment(HorizontalAlignment.Left).VerticalAlignment(VerticalAlignment.Top)
            .Margin(10).Padding(10, 6).CornerRadius(8)
            .Background(Color.FromArgb(0x8C, 0x12, 0x16, 0x1C))
            .IsHitTestVisible(false)
            .BindIsVisible(_preview, p => p.FlightInfo.Length > 0)
            .Child(new StackPanel().Spacing(2).Children(
                new TextBlock().BodyMedium().Foreground(white).FontFamily("Consolas").BindText(_preview, p => p.FlightInfo),
                _hint));
    }

    // Shown until the first flight of the session.
    private static bool s_hasFlown;
    private TextBlock? _hint;

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

    // The orbit camera's clipping planes follow how close it is to its target and to the ground, so the ground doesn't
    // clip when zoomed in or flying low.
    private void KeepClippingInRange()
    {
        var camera = _viewport.Camera;
        float extent = (float)(_preview.Tiles.Layout?.Extent ?? 1000);
        float near = Math.Clamp(Math.Min(camera.Distance / 2000, Math.Max(0.5f, _flight.HeightAboveGround) / 3), 0.05f, 50);
        float far = Math.Max(extent * 3, camera.Distance * 20);
        if (Math.Abs(camera.Near - near) > near * 0.25f) camera.Near = near;
        if (Math.Abs(camera.Far - far) > far * 0.25f) camera.Far = far;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Keys bound to a fly command (alone, or with Shift for speed) fly while they're held, before the view's other
    /// keybindings see them; with Ctrl or Alt they're left to the other shortcuts.
    /// </remarks>
    public override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;
        _flight.IsFast = (e.Modifiers & ModifierKeys.Shift) != 0 || e.Key is Key.LeftShift or Key.RightShift;
        if ((e.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        if (TerrainFlightCommands.DirectionOf(e.Key) is not { } direction) return;
        e.Handled = true;
        if (!_flight.IsMoving) _lastFrame = _clock.Elapsed.TotalSeconds;
        _flight.Press(direction);
        s_hasFlown = true;
        _hint?.IsVisible(false);
        _viewport.RequestRender();
    }

    /// <inheritdoc/>
    public override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        _flight.IsFast = (e.Modifiers & ModifierKeys.Shift) != 0 && e.Key is not (Key.LeftShift or Key.RightShift);
        if (TerrainFlightCommands.DirectionOf(e.Key) is { } direction) _flight.Release(direction);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _preview.PropertyChanged += OnPreviewPropertyChanged;
        FocusManager.FocusChanged += OnFocusChanged;
        QueueLevels();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _preview.PropertyChanged -= OnPreviewPropertyChanged;
        FocusManager.FocusChanged -= OnFocusChanged;
        _flight.ReleaseAll();
        base.OnDetachedFromVisualTree();
    }

    // Keys released elsewhere don't reach the view: when the focus leaves it, the camera stops.
    private void OnFocusChanged(UIElement? previous, UIElement? focused)
    {
        if (focused is null || (focused != this && !focused.IsDescendantOf(this))) _flight.ReleaseAll();
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerrainPreview.ShowLevels) or nameof(TerrainPreview.Textured) or nameof(TerrainPreview.Model)) QueueLevels();
    }
}
