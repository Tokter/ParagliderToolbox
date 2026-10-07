using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Threading;
using Atelier.Core.Tree;
using Atelier.Graphics3D;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Infrastructure;

namespace ParagliderToolbox.Modules.Paraglider.Views;

/// <summary>
/// The detail view of a recorded flight: the 3D replay with play/pause, a timeline to scrub, frame steps and the
/// playback speed, the flight data of the moment shown, and the animation export.
/// </summary>
/// <remarks>
/// The view is a <see cref="KeybindingHandler"/> for the <see cref="RecordingPlayer.Group"/> commands (P plays or pauses,
/// R restarts, comma and period step a frame, minus and equals change the speed, 1–4 toggle the parts). The viewport has
/// its own navigation: middle drag orbits, Shift+middle drag pans, the wheel zooms, Home frames the glider. The camera
/// follows the glider, keeping the orbit and zoom.
/// </remarks>
public sealed class RecordingDetailView : KeybindingHandler
{
    private readonly RecordingPlayer _player;
    private readonly Viewport3D _viewport;
    private Vector3? _followTarget;

    /// <summary>Initializes the view of <paramref name="player"/>.</summary>
    public RecordingDetailView(RecordingPlayer player, ParagliderActions actions) : base(RecordingPlayer.Group)
    {
        _player = player;
        DataContext = player;

        _viewport = new Viewport3D().Scene(player.Scene).ShowGrid(true);
        _viewport.Rendered += (_, _) =>
        {
            if (_player.Advance()) _viewport.RequestRender();
        };
        _player.Loaded += (_, _) => Dispatcher.Post(Frame);
        _player.Moved += (_, target) => Follow(target);

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(actions),
                _viewport.Row(1),
                Timeline().Row(2),
                new TextBlock().Row(3).BodySmall().Muted().Margin(12, 4, 12, 6).TextTrimming()
                    .BindText(player, p => p.IsLoading ? "Generating the paraglider as it was flown…" : p.Status));

        if (!player.IsLoading) Dispatcher.Post(Frame);
    }

    private UIElement Toolbar(ParagliderActions actions)
    {
        static ToggleButton Toggle(string text, MaterialIconKind icon, RecordingPlayer player, Func<RecordingPlayer, bool> get, Action<RecordingPlayer, bool> set, string tip) =>
            new ToggleButton()
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(icon).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock(text).VerticalAlignment(VerticalAlignment.Center)))
                .Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip(tip)
                .BindIsChecked(player, get, set);

        static Button IconButton(System.Windows.Input.ICommand command, MaterialIconKind icon, string tip) =>
            new Button().Variant(ButtonVariant.Text).Command(command).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(icon).Size(18)).ToolTip(tip);

        var play = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_player.TogglePlayCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_player, p => p.IsPlaying ? MaterialIconKind.Pause : MaterialIconKind.PlayArrow),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_player, p => p.IsPlaying ? "Pause" : "Play")));

        return new WrapPanel().Spacing(6, 6).Margin(8, 6).Children(
            play,
            IconButton(_player.RestartCommand, MaterialIconKind.SkipPrevious, "Back to the start (R)"),
            IconButton(_player.PreviousFrameCommand, MaterialIconKind.ChevronLeft, "One frame back (,)"),
            IconButton(_player.NextFrameCommand, MaterialIconKind.ChevronRight, "One frame on (.)"),
            IconButton(_player.SlowerCommand, MaterialIconKind.FastRewind, "Slower (-)"),
            new TextBlock().VerticalAlignment(VerticalAlignment.Center).MinWidth(44).BindText(_player, p => string.Create(CultureInfo.CurrentCulture, $"×{p.Speed:0.##}")),
            IconButton(_player.FasterCommand, MaterialIconKind.FastForward, "Faster (=)"),
            new Border().Width(12),
            Toggle("Canopy", MaterialIconKind.Paragliding, _player, p => p.ShowCanopy, (p, v) => p.ShowCanopy = v, "Show the canopy (1)"),
            Toggle("Ribs", MaterialIconKind.ViewColumn, _player, p => p.ShowRibs, (p, v) => p.ShowRibs = v, "Show the internal ribs (4)"),
            Toggle("Rigging", MaterialIconKind.Timeline, _player, p => p.ShowRigging, (p, v) => p.ShowRigging = v, "Show the lines, risers and toggles (2)"),
            Toggle("Proxy", MaterialIconKind.Hub, _player, p => p.ShowProxy, (p, v) => p.ShowProxy = v, "Show the physics proxy (3)"),
            new Button().Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(MaterialIconKind.CenterFocusStrong).Size(18)).ToolTip("Frame the glider (Home)").OnClick(Frame),
            new Border().Width(12),
            new Button().Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(10, 0)
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(MaterialIconKind.Movie).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock("Export animation…").VerticalAlignment(VerticalAlignment.Center)))
                .ToolTip("Export the paraglider as flown with this motion baked into its proxy joints (glTF)")
                .OnClick(() => _ = actions.ExportRecordingAsync(_player.Node)));
    }

    // The timeline (drag to scrub) and the flight data of the moment shown.
    private UIElement Timeline() =>
        new Border()
            .Margin(8, 0, 8, 2)
            .Padding(12, 8)
            .CornerRadius(10)
            .Themed(Border.BackgroundProperty, c => c.SurfaceContainerLow)
            .Child(new StackPanel().Spacing(6).Children(
                new Slider().Minimum(0).Maximum(Math.Max(0.01f, _player.Duration))
                    .BindValue(_player, p => p.Time, (p, v) =>
                    {
                        if (Math.Abs(v - p.Time) < 1e-4f) return;
                        p.IsPlaying = false;
                        p.Time = v;
                    }),
                new TextBlock().BodySmall().FontFamily("Consolas").TextWrapping().BindText(_player, p => p.Telemetry)));

    private void Frame()
    {
        if (_player.Scene.GetBounds() is not { } bounds) return;
        _viewport.Camera.Frame(bounds.Min, bounds.Max);
        // A three-quarter view from the front and above, like the glider is seen from a hillside.
        _viewport.Camera.Yaw = 0.65f;
        _viewport.Camera.Pitch = 0.32f;
        _followTarget = null;
        _viewport.GridHeight = bounds.Min.Y - 2;
        _viewport.RequestRender();
    }

    // The camera moves along with the glider by exactly as much as it moved, keeping the user's orbit and zoom.
    private void Follow(Vector3 target)
    {
        if (_followTarget is { } last) _viewport.Camera.Target += target - last;
        _viewport.GridHeight = target.Y - 10;
        _followTarget = target;
        _viewport.RequestRender();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        // Hidden views don't play.
        _player.IsPlaying = false;
        base.OnDetachedFromVisualTree();
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingPlayer.IsPlaying) && _player.IsPlaying) _viewport.RequestRender();
    }
}
