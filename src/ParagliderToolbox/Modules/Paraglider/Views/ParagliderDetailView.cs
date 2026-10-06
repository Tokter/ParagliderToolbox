using System.ComponentModel;
using System.Numerics;
using Atelier.Charts;
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
/// The detail view of a paraglider: the 3D preview of the generated model (canopy, ribs, rigging and the physics proxy),
/// and the flight simulation of the proxy with the pilot's controls.
/// </summary>
/// <remarks>
/// The view is a <see cref="KeybindingHandler"/> for the <see cref="ParagliderPreview.Group"/> commands (1–4 toggle the
/// parts, P simulates, Q/E/F collapse, B big ears, G a gust, R resets, Ctrl+R records, Ctrl+P records the polar). The viewport has its own
/// navigation: middle drag orbits, Shift+middle drag pans, the wheel zooms, Home frames the model, Shift+Z wireframe.
/// </remarks>
public sealed class ParagliderDetailView : KeybindingHandler
{
    private readonly ParagliderPreview _preview;
    private readonly Viewport3D _viewport;
    private Vector3? _followTarget;

    // While the simulation doesn't run, the game controller is read here (A starts it); the simulation reads it every frame.
    private readonly DispatcherTimer _gamepadTimer;

    /// <summary>Initializes the view of <paramref name="preview"/>.</summary>
    public ParagliderDetailView(ParagliderPreview preview) : base(ParagliderPreview.Group)
    {
        _preview = preview;
        DataContext = preview;

        _viewport = new Viewport3D().Scene(preview.Scene).ShowGrid(true);
        _viewport.Rendered += (_, _) =>
        {
            if (_preview.Advance()) _viewport.RequestRender();
        };
        _preview.ModelApplied += (_, _) => Frame();
        _preview.SimulationStopped += (_, _) => Dispatcher.Post(Frame);
        _gamepadTimer = new DispatcherTimer(TimeSpan.FromSeconds(1 / 30.0), (_, _) =>
        {
            if (!_preview.IsSimulating) _preview.PollGamepad();
        });
        _preview.Simulated += (_, target) => Follow(target);

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(),
                _viewport.Row(1),
                SimulationPanel().Row(2),
                PolarPanel().Row(3),
                new TextBlock().Row(4).BodySmall().Muted().Margin(12, 4, 12, 6).TextTrimming().BindText(preview, p => p.IsGenerating ? "Generating…  " + p.Status : p.Status));

        if (preview.Model != null) Dispatcher.Post(Frame);
    }

    private UIElement Toolbar()
    {
        static ToggleButton Toggle(string text, MaterialIconKind icon, ParagliderPreview preview, Func<ParagliderPreview, bool> get, Action<ParagliderPreview, bool> set, string tip) =>
            new ToggleButton()
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(icon).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock(text).VerticalAlignment(VerticalAlignment.Center)))
                .Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip(tip)
                .BindIsChecked(preview, get, set);

        static Button Action(System.Windows.Input.ICommand command) =>
            new Button().Variant(ButtonVariant.Text).Command(command).Height(30).MinHeight(0).Padding(8, 0);

        var simulate = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_preview.ToggleSimulationCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.IsSimulating ? MaterialIconKind.Pause : MaterialIconKind.PlayArrow),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.IsSimulating ? "Pause" : "Simulate")));

        return new WrapPanel().Spacing(6, 6).Margin(8, 6).Children(
            Toggle("Canopy", MaterialIconKind.Paragliding, _preview, p => p.ShowCanopy, (p, v) => p.ShowCanopy = v, "Show the canopy (1)"),
            Toggle("Ribs", MaterialIconKind.ViewColumn, _preview, p => p.ShowRibs, (p, v) => p.ShowRibs = v, "Show the internal ribs (4)"),
            Toggle("Rigging", MaterialIconKind.Timeline, _preview, p => p.ShowRigging, (p, v) => p.ShowRigging = v, "Show the lines, risers and toggles (2)"),
            Toggle("Proxy", MaterialIconKind.Hub, _preview, p => p.ShowProxy, (p, v) => p.ShowProxy = v, "Show the physics proxy (3)"),
            new Button().Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(MaterialIconKind.CenterFocusStrong).Size(18)).ToolTip("Frame the glider (Home)").OnClick(Frame),
            new Border().Width(12),
            simulate,
            Action(_preview.ResetSimulationCommand),
            new ToggleButton()
                .Height(30).MinHeight(0).Padding(10, 0)
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(MaterialIconKind.FiberManualRecord).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock("Record").VerticalAlignment(VerticalAlignment.Center)))
                .ToolTip("Record the simulation for the animation export (Ctrl+R)")
                .BindIsChecked(_preview, p => p.IsRecording, (p, v) => { if (v != p.IsRecording) p.ToggleRecordingCommand.Execute(null); }),
            Action(_preview.ExportRecordingCommand),
            new Border().Width(12),
            new Button()
                .Variant(ButtonVariant.Tonal)
                .Command(_preview.RecordPolarCommand)
                .Height(30).MinHeight(0).Padding(12, 0)
                .ToolTip("Choose the settings and record the polar: the whole speed range, from full speed bar to the stall (Ctrl+P); again to cancel")
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.IsRecordingPolar ? MaterialIconKind.Stop : MaterialIconKind.ShowChart),
                    new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.IsRecordingPolar ? "Cancel polar" : "Polar"))));
    }

    // While a polar is recorded: the phase, the progress and the samples and points as they come in.
    private UIElement PolarPanel()
    {
        var chart = new XYChart { ShowLegend = false };
        var polar = new PolarChart(chart, "Recording polar…");
        _preview.PolarStarted += (_, _) => polar.Clear();
        _preview.PolarSampleRecorded += (_, sample) => polar.AddSample(sample);
        _preview.PolarPointRecorded += (_, point) => polar.AddPoint(point);

        return new Border()
            .Margin(8, 0, 8, 2)
            .Padding(12, 8)
            .CornerRadius(10)
            .Themed(Border.BackgroundProperty, c => c.SurfaceContainerLow)
            .BindIsVisible(_preview, p => p.IsRecordingPolar)
            .Child(new Grid().Rows(GridLength.Auto, GridLength.Auto, GridLength.Pixels(260)).Children(
                new TextBlock().BodySmall().BindText(_preview, p => $"{p.PolarPhase}  ({p.PolarProgress:P0})"),
                new ProgressBar().Row(1).Margin(0, 6).BindValue(_preview, p => (float)(p.PolarProgress * 100)),
                chart.Row(2)));
    }

    private UIElement SimulationPanel()
    {
        static UIElement Slider(string label, float minimum, ParagliderPreview preview, Func<ParagliderPreview, float> get, Action<ParagliderPreview, float> set) =>
            new StackPanel().Width(150).Spacing(0).Children(
                new TextBlock().LabelSmall().Muted().BindText(preview, p => $"{label} {get(p):P0}"),
                new Atelier.Controls.Slider().Minimum(minimum).Maximum(1).BindValue(preview, get, set));

        static Button Pulse(System.Windows.Input.ICommand command) =>
            new Button().Variant(ButtonVariant.Outlined).Command(command).Height(30).MinHeight(0).Padding(10, 0);

        var controls = new WrapPanel().Spacing(16, 8).Children(
            Slider("Left brake", 0, _preview, p => p.BrakeLeft, (p, v) => p.BrakeLeft = v),
            Slider("Right brake", 0, _preview, p => p.BrakeRight, (p, v) => p.BrakeRight = v),
            Slider("Speed bar", 0, _preview, p => p.SpeedBar, (p, v) => p.SpeedBar = v),
            Slider("Weight shift", -1, _preview, p => p.WeightShift, (p, v) => p.WeightShift = v),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).VerticalAlignment(VerticalAlignment.Bottom).Children(
                Pulse(_preview.CollapseLeftCommand),
                Pulse(_preview.CollapseRightCommand),
                Pulse(_preview.FrontalCollapseCommand),
                Pulse(_preview.BigEarsCommand),
                Pulse(_preview.GustCommand)));

        return new Border()
            .Margin(8, 0, 8, 2)
            .Padding(12, 8)
            .CornerRadius(10)
            .Themed(Border.BackgroundProperty, c => c.SurfaceContainerLow)
            .BindIsVisible(_preview, p => p.Simulator != null || p.IsSimulating)
            .Child(new StackPanel().Spacing(6).Children(
                controls,
                new TextBlock().BodySmall().FontFamily("Consolas").BindText(_preview, p => p.Telemetry)));
    }

    private void Frame()
    {
        if (_preview.Scene.GetBounds() is not { } bounds) return;
        _viewport.Camera.Frame(bounds.Min, bounds.Max);
        // A three-quarter view from the front and above, like the glider is seen from a hillside.
        _viewport.Camera.Yaw = 0.65f;
        _viewport.Camera.Pitch = 0.32f;
        _followTarget = null;
        _viewport.GridHeight = 0;
        _viewport.RequestRender();
    }

    // The camera moves along with the glider by exactly as much as it moved, keeping the user's orbit and zoom.
    private void Follow(Vector3 target)
    {
        if (_followTarget is { } last) _viewport.Camera.Target += target - last;
        // The grid stays below the glider (its lines fixed in the world, so they flow past and show the motion).
        _viewport.GridHeight = target.Y - 10;
        _followTarget = target;
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _preview.PropertyChanged += OnPreviewPropertyChanged;
        _gamepadTimer.Start();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _preview.PropertyChanged -= OnPreviewPropertyChanged;
        // Hidden views don't simulate.
        _preview.PauseSimulation();
        _gamepadTimer.Stop();
        base.OnDetachedFromVisualTree();
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParagliderPreview.IsSimulating) && _preview.IsSimulating) _viewport.RequestRender();
        if (e.PropertyName is nameof(ParagliderPreview.Model)) _followTarget = null;
    }
}
