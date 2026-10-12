using System.ComponentModel;
using System.Globalization;
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

namespace ParagliderToolbox.Modules.Airflow.Views;

/// <summary>
/// The detail view of an airflow: its terrain in 3D with the airflow's overlays (streamlines, particles, a slice above
/// the ground, a vertical section, the thermals, the clouds, the ground's heating and the thermal triggers), the color
/// scale, and the simulation's progress.
/// </summary>
/// <remarks>
/// The view is a <see cref="KeybindingHandler"/> for the <see cref="AirflowPreview.Group"/> commands (R simulates or
/// cancels, L, P, H, V, T, C and G toggle the overlays, N the slice's quantity, Page Up and Down move the slice) and
/// the <see cref="TerrainFlight.Group"/> commands, which fly the camera (W, A, S, D, E, Q, Shift, right drag).
/// </remarks>
public sealed class AirflowDetailView : KeybindingHandler
{
    private static readonly string[] s_quantities = ["Lift and sink", "Turbulence", "Wind speed", "Lift chance", "Warming"];
    private static readonly string[] s_groundMaps = ["Heating", "Land cover"];

    private readonly AirflowPreview _preview;
    private readonly TerrainViewport _viewport;
    private readonly StackPanel _legend = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2);

    /// <summary>Initializes the view of <paramref name="preview"/>.</summary>
    public AirflowDetailView(AirflowPreview preview, AirflowActions actions) : base(AirflowPreview.Group)
    {
        _preview = preview;
        DataContext = preview;

        _viewport = new TerrainViewport(preview.Tiles);
        AdditionalScopes.Add(new KeybindingScope(TerrainFlight.Group, _viewport.Flight));
        _viewport.Animate = dt => (_preview.ShowParticles && _preview.Overlays.Particles.Step(dt)) | _preview.Advance(dt);
        _preview.SectionCenter = () => new Vector2(_viewport.Viewport.Camera.Target.X, _viewport.Viewport.Camera.Target.Z);
        // The particles start where the camera looks, as far around as it sees.
        _preview.Overlays.Particles.Focus = () => (new Vector2(_viewport.Viewport.Camera.Target.X, _viewport.Viewport.Camera.Target.Z), 1.2f * _viewport.Viewport.Camera.Distance);
        _preview.TerrainShown += (_, _) => _viewport.Frame();
        _preview.OverlaysChanged += (_, _) => _viewport.RequestRender();

        Content = new Grid()
            .Rows(GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Star, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto)
            .Children(
                Toolbar(actions),
                Banner("This airflow isn't under a terrain: add airflows to a terrain (select it, then Add airflow).", p => p.HasNoTerrain).Row(1),
                Banner("The settings changed: Simulate (R) runs the airflow again.", p => p.IsOutdated && !p.HasNoTerrain).Row(2),
                _viewport.Row(3),
                Timeline().Row(4),
                GameBar().Row(4),
                new ProgressBar().Row(5).Margin(12, 4).BindIsVisible(preview, p => p.IsRunning)
                    .BindIsIndeterminate(preview, p => p.IsRunning && p.Progress <= 0)
                    .BindValue(preview, p => (float)(p.Progress * 100)),
                new TextBlock().Row(6).BodySmall().Muted().Margin(12, 4, 12, 0).TextTrimming().BindText(preview, p => p.Status),
                _legend.Row(7).Margin(12, 4, 12, 8));
        ShowLegend();
        if (preview.Tiles.Bounds() != null) Dispatcher.Post(_viewport.Frame);
    }

    private UIElement Toolbar(AirflowActions actions)
    {
        ToggleButton Toggle(string text, MaterialIconKind icon, Func<AirflowPreview, bool> get, Action<AirflowPreview, bool> set, string tip) =>
            new ToggleButton()
                .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                    new Icon().Kind(icon).Size(16).VerticalAlignment(VerticalAlignment.Center),
                    new TextBlock(text).VerticalAlignment(VerticalAlignment.Center)))
                .Height(30).MinHeight(0).Padding(10, 0)
                .ToolTip(tip)
                .BindIsChecked(_preview, get, set);

        var simulate = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_preview.SimulateCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .ToolTip("Simulate the airflow over the terrain (R); again to cancel")
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.IsRunning ? MaterialIconKind.Stop : MaterialIconKind.PlayArrow),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.IsRunning ? "Cancel" : "Simulate")));

        var quantity = new ComboBox { ItemsSource = s_quantities, Height = 30, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2), Width = 150 }
            .ToolTip("What the slice and the section show (N)")
            .BindSelectedIndex(_preview, p => (int)p.Quantity, (p, v) => p.Quantity = (AirflowQuantity)Math.Max(0, v));
        var groundMap = new ComboBox { ItemsSource = s_groundMaps, Height = 30, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2), Width = 120 }
            .ToolTip("What the ground map shows")
            .BindSelectedIndex(_preview, p => (int)p.GroundMap, (p, v) => p.GroundMap = (GroundMap)Math.Max(0, v))
            .BindIsVisible(_preview, p => p.ShowGround);
        var height = new Slider().Range(0, 2500).SnapTo(10).Width(150).VerticalAlignment(VerticalAlignment.Center)
            .ToolTip("The slice's height above the ground, where the streamlines start (Page Up, Page Down)")
            .BindValue(_preview, p => (float)p.SliceHeight, (p, v) => p.SliceHeight = v);
        var heightText = new TextBlock().BodySmall().VerticalAlignment(VerticalAlignment.Center).Width(110)
            .BindText(_preview, p => string.Create(CultureInfo.CurrentCulture, $"{p.SliceHeight:0} m above ground"));

        return new WrapPanel().Spacing(6, 6).Margin(8, 6).Children(
            simulate,
            Toggle("Over time", MaterialIconKind.Movie, p => p.OverTime, (p, v) => p.OverTime = v,
                    "The run over time: its clouds, their shadows and the lift coming and going (O); off, the mean of its last minutes")
                .BindIsVisible(_preview, p => p.HasFilm),
            Toggle("Game", MaterialIconKind.SportsEsports, p => p.ShowGame, (p, v) => p.ShowGame = v,
                    "The thermals and cumulus as a game plays them over the base flow (M): the convection model of the run")
                .BindIsVisible(_preview, p => p.HasGame),
            new Border().Width(10),
            Toggle("Streamlines", MaterialIconKind.Timeline, p => p.ShowStreamlines, (p, v) => p.ShowStreamlines = v, "The mean wind's paths (L)"),
            Toggle("Particles", MaterialIconKind.Air, p => p.ShowParticles, (p, v) => p.ShowParticles = v, "Particles streaming with the wind, sped up 60 times (P)"),
            Toggle("Slice", MaterialIconKind.Layers, p => p.ShowSlice, (p, v) => p.ShowSlice = v, "A slice at a height above the ground (H)"),
            Toggle("Section", MaterialIconKind.VerticalSplit, p => p.ShowSection, (p, v) => p.ShowSection = v,
                "A vertical section along the wind through the camera's target (V): waves, rotors, the thermals' tops"),
            Toggle("Thermals", MaterialIconKind.Whatshot, p => p.ShowThermals, (p, v) => p.ShowThermals = v, "The thermals to their tops, with their cumulus' bases (T)"),
            Toggle("Clouds", MaterialIconKind.Cloud, p => p.ShowClouds, (p, v) => p.ShowClouds = v, "The clouds (C)"),
            Toggle("Ground", MaterialIconKind.Map, p => p.ShowGround, (p, v) => p.ShowGround = v, "The ground's heating or land cover, and the thermal triggers (G)"),
            Toggle("Sun", MaterialIconKind.WbSunny, p => p.ShowSun, (p, v) => p.ShowSun = v, "Where the sun stands and how its light falls (U)"),
            Toggle("Without thermals", MaterialIconKind.LayersClear, p => p.WithoutThermals, (p, v) => p.WithoutThermals = v,
                    "The base flow: the mean wind of the air between the thermals and clouds, what a game plays its thermals over (B)")
                .BindIsVisible(_preview, p => p.HasBaseFlow),
            new Border().Width(10),
            quantity,
            groundMap,
            height,
            heightText,
            new Button().Variant(ButtonVariant.Text).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(MaterialIconKind.CenterFocusStrong).Size(18)).ToolTip("Frame the terrain").OnClick(_viewport.Frame),
            new Button().Variant(ButtonVariant.Text).Command(actions.ExportCommand).Height(30).MinHeight(0).Padding(8, 0));
    }

    // The run over time: play, frame steps, speed, the timeline (drag to scrub) and what the frame shows.
    private UIElement Timeline()
    {
        Button IconButton(System.Windows.Input.ICommand command, MaterialIconKind icon, string tip) =>
            new Button().Variant(ButtonVariant.Text).Command(command).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(icon).Size(18)).ToolTip(tip);

        var play = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_preview.TogglePlayCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .ToolTip("Play or pause the run (Space)")
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.IsPlaying ? MaterialIconKind.Pause : MaterialIconKind.PlayArrow),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.IsPlaying ? "Pause" : "Play")));

        var timeline = new Slider().VerticalAlignment(VerticalAlignment.Center)
            .BindMinimum(_preview, p => (float)p.FilmStart)
            .BindMaximum(_preview, p => (float)Math.Max(p.FilmStart + 1, p.FilmEnd))
            .BindValue(_preview, p => (float)p.FilmTime, (p, v) =>
            {
                if (Math.Abs(v - p.FilmTime) < 0.5) return;
                p.Seek(v);
            });

        return new Border()
            .Margin(8, 4, 8, 0)
            .Padding(8, 6)
            .CornerRadius(10)
            .Themed(Border.BackgroundProperty, c => c.SurfaceContainerLow)
            .BindIsVisible(_preview, p => p.ShowsFilm)
            .Child(new Grid().Columns(GridLength.Auto, GridLength.Star, GridLength.Auto).Rows(GridLength.Auto, GridLength.Auto).Children(
                new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
                    play,
                    IconButton(_preview.RestartCommand, MaterialIconKind.SkipPrevious, "Back to the start (Home)"),
                    IconButton(_preview.PreviousFrameCommand, MaterialIconKind.ChevronLeft, "Half a minute back (,)"),
                    IconButton(_preview.NextFrameCommand, MaterialIconKind.ChevronRight, "Half a minute on (.)"),
                    IconButton(_preview.SlowerCommand, MaterialIconKind.FastRewind, "Slower (-)"),
                    new TextBlock().VerticalAlignment(VerticalAlignment.Center).MinWidth(40).BindText(_preview, p => p.SpeedText)
                        .ToolTip("Seconds of the run per second"),
                    IconButton(_preview.FasterCommand, MaterialIconKind.FastForward, "Faster (=)")),
                timeline.Column(1).Margin(12, 0),
                new TextBlock().Column(2).BodySmall().FontFamily("Consolas").VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.FilmClock),
                new TextBlock().Row(1).ColumnSpan(3).BodySmall().Muted().Margin(4, 2, 4, 0).TextTrimming().BindText(_preview, p => p.FrameText)));
    }

    // The game: play or pause, another sky, the speed, and what the model holds against the run.
    private UIElement GameBar()
    {
        Button IconButton(System.Windows.Input.ICommand command, MaterialIconKind icon, string tip) =>
            new Button().Variant(ButtonVariant.Text).Command(command).Height(30).MinHeight(0).Padding(8, 0)
                .Content(new Icon().Kind(icon).Size(18)).ToolTip(tip);

        var play = new Button()
            .Variant(ButtonVariant.Tonal)
            .Command(_preview.TogglePlayCommand)
            .Height(30).MinHeight(0).Padding(12, 0)
            .ToolTip("Play or pause the game (Space)")
            .Content(new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(
                new Icon().Size(18).VerticalAlignment(VerticalAlignment.Center).BindKind(_preview, p => p.GamePlaying ? MaterialIconKind.Pause : MaterialIconKind.PlayArrow),
                new TextBlock().VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.GamePlaying ? "Pause" : "Play")));

        return new Border()
            .Margin(8, 4, 8, 0)
            .Padding(8, 6)
            .CornerRadius(10)
            .Themed(Border.BackgroundProperty, c => c.SurfaceContainerLow)
            .BindIsVisible(_preview, p => p.ShowGame)
            .Child(new Grid().Columns(GridLength.Auto, GridLength.Star).Children(
                new StackPanel().Orientation(Orientation.Horizontal).Spacing(4).Children(
                    play,
                    IconButton(_preview.RestartCommand, MaterialIconKind.Refresh, "Another sky: the same thermals' kind, other ones (Home)"),
                    IconButton(_preview.SlowerCommand, MaterialIconKind.FastRewind, "Slower (-)"),
                    new TextBlock().VerticalAlignment(VerticalAlignment.Center).MinWidth(40).BindText(_preview, p => p.GameSpeedText)
                        .ToolTip("Seconds of the game per second"),
                    IconButton(_preview.FasterCommand, MaterialIconKind.FastForward, "Faster (=)")),
                new TextBlock().Column(1).BodySmall().Muted().Margin(12, 0, 4, 0).VerticalAlignment(VerticalAlignment.Center).TextTrimming()
                    .BindText(_preview, p => p.GameText)));
    }

    private UIElement Banner(string text, Func<AirflowPreview, bool> visible) =>
        new Border()
            .Margin(8, 0, 8, 4)
            .Padding(12, 6)
            .CornerRadius(8)
            .Themed(Border.BackgroundProperty, c => c.SecondaryContainer)
            .BindIsVisible(_preview, visible)
            .Child(new TextBlock(text).BodySmall());

    // The color scales shown: the slice's (or the vertical wind's, which colors the lines and thermals) and the ground map's.
    private void ShowLegend()
    {
        _legend.Clear();
        var scale = _preview.ShowSlice || _preview.ShowSection ? _preview.Scale : AirflowColors.Lift;
        Scale(scale.Title, scale.Unit, scale.Stops.Select(s => (s.Value.ToString("0.#", CultureInfo.CurrentCulture), s.Color)));
        _legend.Add(new TextBlock().BodySmall().Muted().Margin(16, 0, 0, 0).VerticalAlignment(VerticalAlignment.Center).BindText(_preview, p => p.SunText));
        if (!_preview.ShowGround) return;
        _legend.Add(new Border().Width(24));
        if (_preview.GroundMap == GroundMap.Heating)
        {
            var heating = AirflowColors.Heating;
            Scale("Ground", heating.Unit, heating.Stops.Select(s => (s.Value.ToString("0", CultureInfo.CurrentCulture), s.Color)));
        }
        else
        {
            Scale("Ground", "", Enum.GetValues<ParagliderToolbox.Airflow.Surface.LandCover>().Select(c => (c.ToString(), AirflowColors.Of(c))));
        }

        void Scale(string title, string unit, IEnumerable<(string Label, Vector3 Color)> stops)
        {
            _legend.Add(new TextBlock($"{title}:").BodySmall().Muted().Margin(0, 0, 8, 0).VerticalAlignment(VerticalAlignment.Center));
            foreach (var (label, color) in stops)
            {
                var swatch = Color.FromRgb((byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255));
                _legend.Add(new Border().Width(14).Height(14).CornerRadius(3).Background(swatch).VerticalAlignment(VerticalAlignment.Center));
                _legend.Add(new TextBlock(label).LabelSmall().Margin(3, 0, 10, 0).VerticalAlignment(VerticalAlignment.Center));
            }
            if (unit.Length > 0) _legend.Add(new TextBlock(unit).BodySmall().Muted().VerticalAlignment(VerticalAlignment.Center));
        }
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

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AirflowPreview.Scale) or nameof(AirflowPreview.ShowSlice) or nameof(AirflowPreview.ShowSection)
            or nameof(AirflowPreview.ShowGround) or nameof(AirflowPreview.GroundMap))
        {
            ShowLegend();
        }
        if (e.PropertyName is nameof(AirflowPreview.ShowParticles) || e.PropertyName == nameof(AirflowPreview.IsPlaying) && _preview.IsPlaying
            || e.PropertyName == nameof(AirflowPreview.GamePlaying) && _preview.GamePlaying)
        {
            _viewport.RequestRender();
        }
    }
}
