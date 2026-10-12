using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using Atelier.Core.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Airflow;
using ParagliderToolbox.Airflow.Convection;
using ParagliderToolbox.Airflow.Gpu;
using ParagliderToolbox.Airflow.Sun;
using ParagliderToolbox.Airflow.Surface;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Terrain;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// The view model of an <see cref="AirflowNode"/>'s detail view: its terrain (built, or from its preview), the
/// simulation (run in the background, the flow shown live as it develops) and the overlays that show the result.
/// The commands are the <see cref="Group"/> keybinding group, active while the view has the focus.
/// </summary>
public sealed partial class AirflowPreview : ObservableObject, IDisposable
{
    /// <summary>The keybinding group of the view's commands.</summary>
    public const string Group = "Airflow view";

    private readonly AirflowNode _node;
    private readonly TerrainModule _terrains;
    private readonly TerrainScene _tiles = new();
    private readonly AirflowScene _overlays;
    private CancellationTokenSource? _run;
    private TerrainModel? _terrain;
    private Task<TerrainModel?>? _loading;
    private AirflowField? _shown;
    private int _overlayVersion;
    private bool _disposed;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showStreamlines;
    [ObservableProperty] private bool _showParticles;
    [ObservableProperty] private bool _showSlice = true;
    [ObservableProperty] private bool _showSection;
    [ObservableProperty] private bool _showThermals = true;
    [ObservableProperty] private bool _showClouds = true;
    [ObservableProperty] private bool _showGround;
    [ObservableProperty] private AirflowQuantity _quantity = AirflowQuantity.Lift;
    [ObservableProperty] private GroundMap _groundMap = GroundMap.Heating;
    [ObservableProperty] private double _sliceHeight = 300;
    [ObservableProperty] private bool _showSun;
    [ObservableProperty] private bool _withoutThermals;

    private readonly SunMarker _sun;

    // The run over time: the film of the run in progress, else the result's; the frame shown and how it was built.
    private static readonly double[] s_speeds = [15, 30, 60, 120, 240, 480];
    private const double FilmInterval = 30;
    private readonly FilmScene _filmScene;
    private AirflowFilm? _film;
    private int _filmFrames, _frameVersion;
    private (AirflowFilmFrame? Frame, bool Slice, bool Clouds, double Height, ConvectionClimate? Thermals) _frameShown;
    private bool _followLive = true;

    [ObservableProperty] private bool _overTime = true;
    [ObservableProperty] private double _filmTime;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _playSpeed = 60;
    [ObservableProperty] private string _frameText = "";

    // The game: a convection model of the result played over its base flow.
    private static readonly double[] s_gameSpeeds = [1, 5, 15, 30, 60, 120];
    private readonly GameScene _gameScene;
    private ConvectionModel? _model;
    private int _gameSeed = 1;
    private double _sinceSky, _sinceText;

    [ObservableProperty] private bool _showGame;
    [ObservableProperty] private bool _gamePlaying;
    [ObservableProperty] private double _gameSpeed = 30;
    [ObservableProperty] private string _gameText = "";

    /// <summary>Initializes the view model of <paramref name="node"/> and loads its terrain.</summary>
    public AirflowPreview(AirflowNode node, TerrainModule terrains)
    {
        _node = node;
        _terrains = terrains;
        _overlays = new AirflowScene(_tiles.Scene);
        _filmScene = new FilmScene(_tiles.Scene);
        _gameScene = new GameScene(_tiles.Scene);
        _sun = new SunMarker(_tiles.Scene);
        _node.PropertyChanged += OnNodePropertyChanged;
        Status = node.Result is null ? "Simulate (R) runs the airflow over the terrain." : Describe(node.Result);
        SetFilm(node.Film, toEnd: true);
        // The time estimate depends on whether the solver runs on a graphics card (looking for it takes a few seconds).
        if (AirflowGpu.IsKnownAvailable is null)
        {
            Task.Run(() => AirflowGpu.IsAvailable).ContinueWith(_ => Dispatcher.Post(_node.OnDeviceKnown), TaskScheduler.Default);
        }
        _ = LoadTerrainAsync();
    }

    /// <summary>Gets the film shown: of the run in progress, else of the result (kept while the app runs), or <c>null</c>.</summary>
    public AirflowFilm? Film => _film;

    /// <summary>Gets whether there is a film to play.</summary>
    public bool HasFilm => _filmFrames > 0;

    /// <summary>Gets whether the airflow shows over time (the film's frame), not its mean.</summary>
    public bool ShowsFilm => OverTime && HasFilm;

    /// <summary>Gets the film's first frame's time (s of simulated time).</summary>
    public double FilmStart => HasFilm ? _film![0].Time : 0;

    /// <summary>Gets the film's last frame's time (s of simulated time).</summary>
    public double FilmEnd => HasFilm ? _film![_filmFrames - 1].Time : 0;

    /// <summary>Gets the time shown and the film's end, for people.</summary>
    public string FilmClock => string.Create(CultureInfo.CurrentCulture, $"{Clock.Of(FilmTime)} / {Clock.Of(FilmEnd)}");

    /// <summary>Moves the film to <paramref name="time"/> (pauses; at the end of a run in progress it follows the run again).</summary>
    public void Seek(double time)
    {
        IsPlaying = false;
        _followLive = IsRunning && time >= FilmEnd - FilmInterval / 2;
        FilmTime = Math.Clamp(time, FilmStart, FilmEnd);
    }

    /// <summary>Plays the film, or the game, on by <paramref name="elapsed"/> seconds of wall-clock time; returns whether it plays on.</summary>
    public bool Advance(double elapsed)
    {
        if (ShowGame) return AdvanceGame(elapsed);
        if (!IsPlaying || !ShowsFilm) return false;
        double time = FilmTime + elapsed * PlaySpeed;
        if (time >= FilmEnd)
        {
            // At the end: a run in progress is followed as it goes on.
            IsPlaying = false;
            _followLive = IsRunning;
            time = FilmEnd;
        }
        FilmTime = time;
        return IsPlaying;
    }

    #region Game

    /// <summary>Gets whether the result can be played as a game: a run that followed its thermals, with its base flow.</summary>
    public bool HasGame => !IsRunning && _node.Result is { HasBaseFlow: true, Convection.Thermals.Count: > 0 };

    /// <summary>Gets the game's model while it shows, or <c>null</c>.</summary>
    public ConvectionModel? Model => _model;

    /// <summary>Gets how fast the game plays, for people.</summary>
    public string GameSpeedText => string.Create(CultureInfo.CurrentCulture, $"×{GameSpeed:0}");

    partial void OnShowGameChanged(bool value)
    {
        if (value && !HasGame)
        {
            ShowGame = false;
            return;
        }
        if (value)
        {
            OverTime = false;
            _model = new ConvectionModel(_node.Result!, _gameSeed);
            GamePlaying = true;
        }
        else
        {
            _model = null;
            GamePlaying = false;
            _gameScene.Clear();
            GameText = "";
        }
        OnPropertyChanged(nameof(Model));
        _overlays.Particles.Wind = _model is { } model ? (x, y, z) => model.WindAt(x, y, z) : null;
        ShowResult();
        ShowSky();
    }

    partial void OnGameSpeedChanged(double value) => OnPropertyChanged(nameof(GameSpeedText));

    // The game plays on: the model's sky drawn ten times a second, its numbers four times.
    private bool AdvanceGame(double elapsed)
    {
        if (_model is not { } model || !GamePlaying) return false;
        model.Advance(elapsed * GameSpeed);
        _sinceSky += elapsed;
        _sinceText += elapsed;
        if (_sinceSky >= 0.1) ShowSky();
        return true;
    }

    // The model's sky now, and what it holds against the run.
    private void ShowSky()
    {
        _sinceSky = 0;
        if (!ShowGame || _model is not { } model)
        {
            _gameScene.Clear();
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _gameScene.Show(model, ShowThermals, ShowClouds, ShowSlice && Quantity == AirflowQuantity.Lift ? SliceHeight : double.NaN, Ground);
        if (_sinceText >= 0.25 || GameText.Length == 0)
        {
            _sinceText = 0;
            GameText = Describe(model);
        }
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private string Describe(ConvectionModel model)
    {
        var culture = CultureInfo.CurrentCulture;
        string best = model.Thermals.Count == 0 ? "" : string.Create(culture, $", climbing up to {model.Thermals.Max(t => t.Strength):0.0} m/s");
        string run = "";
        if (model.Field.Convection is { } climate && climate.ObservedTime > 0)
        {
            double span = climate.ObservedTime + climate.Interval;
            run = string.Create(culture,
                $" · the run had {climate.Thermals.Sum(t => t.Duration) / span:0} thermals and {climate.Clouds.Where(c => c.ThermalId >= 0).Sum(c => c.Duration) / span:0} cumulus over them about");
        }
        return string.Create(culture,
            $"{model.Thermals.Count} thermals{best}, {model.Clouds.Count} cumulus · {Clock.Of(Math.Max(0, model.Time))} played at ×{GameSpeed:0}{run}");
    }

    #endregion

    // Shows another film (or none); new frames of the same one come through OnFilmFrames.
    private void SetFilm(AirflowFilm? film, bool toEnd)
    {
        if (film == _film && film != null) return;
        bool wasShowing = ShowsFilm;
        _film = film;
        _filmFrames = film?.Count ?? 0;
        _frameShown = default;
        OnPropertyChanged(nameof(Film));
        OnFilmFrames();
        FilmTime = toEnd ? FilmEnd : FilmStart;
        if (wasShowing != ShowsFilm) UpdateOverlays();
        ShowFrame();
    }

    // The film has more frames (live): the timeline grows, and the view follows the newest one unless the user went back.
    private void OnFilmFrames()
    {
        bool had = HasFilm;
        _filmFrames = _film?.Count ?? 0;
        OnPropertyChanged(nameof(HasFilm));
        OnPropertyChanged(nameof(ShowsFilm));
        OnPropertyChanged(nameof(FilmStart));
        OnPropertyChanged(nameof(FilmEnd));
        OnPropertyChanged(nameof(FilmClock));
        if (_followLive && IsRunning && HasFilm) FilmTime = FilmEnd;
        // The first frame: the film takes over the slice and the clouds.
        if (!had && ShowsFilm) UpdateOverlays();
    }

    partial void OnFilmTimeChanged(double value)
    {
        OnPropertyChanged(nameof(FilmClock));
        ShowFrame();
    }

    partial void OnOverTimeChanged(bool value)
    {
        if (value) ShowGame = false;
        IsPlaying = false;
        OnPropertyChanged(nameof(ShowsFilm));
        UpdateOverlays();
        ShowFrame();
    }

    partial void OnIsPlayingChanged(bool value)
    {
        // Played from the end, the film starts over.
        if (value && FilmTime >= FilmEnd - 1e-6 && !IsRunning) FilmTime = FilmStart;
        if (value) _followLive = false;
    }

    // The film's frame at the time shown: its lift at the slice's height, its clouds and their shadows, built in the background.
    private void ShowFrame()
    {
        if (!ShowsFilm || _film is not { } film || _terrain is not { } terrain || film.At(FilmTime) is not { } frame)
        {
            _frameShown = default;
            ++_frameVersion;
            _filmScene.Clear();
            FrameText = "";
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // The run's followed thermals alive then (the result's, once the run is over).
        var thermals = ShowThermals && !IsRunning && _node.Film == film ? _node.Result?.Convection : null;
        var key = (frame, ShowSlice && Quantity == AirflowQuantity.Lift, ShowClouds, SliceHeight, thermals);
        if (key == _frameShown) return;
        _frameShown = key;
        int version = ++_frameVersion;
        Task.Run(() => (Geometry: FilmScene.Build(film, frame, key.Item2 ? key.SliceHeight : double.NaN, key.ShowClouds, thermals, terrain.HeightAt),
            Text: Describe(film, frame, key.SliceHeight, thermals))).ContinueWith(task =>
        {
            if (task.IsFaulted || version != _frameVersion || _disposed) return;
            Dispatcher.Post(() =>
            {
                if (version != _frameVersion || _disposed) return;
                _filmScene.Apply(task.Result.Geometry);
                FrameText = task.Result.Text;
                OverlaysChanged?.Invoke(this, EventArgs.Empty);
            });
        }, TaskScheduler.Default);
    }

    // The frame's cumulus, the shade they cast, the best lift at the slice's height and the followed thermals alive.
    private static string Describe(AirflowFilm film, AirflowFilmFrame frame, double height, ConvectionClimate? thermals)
    {
        int columns = frame.Sunshine.Length, cloudy = 0, shaded = 0;
        float best = 0;
        for (int column = 0; column < columns; column++)
        {
            if (frame.CloudWater[column] * AirflowFilm.WaterStep >= 0.01) cloudy++;
            if (frame.Sunshine[column] < 128) shaded++;
            best = Math.Max(best, frame.LiftAt(column, height));
        }
        int alive = thermals?.Thermals.Count(t => frame.Time >= t.Start && frame.Time <= t.Start + t.Duration) ?? 0;
        string followed = thermals is null ? "" : frame.Time < thermals.Start - 1e-6
            ? " · thermals followed from " + Clock.Of(thermals.Start)
            : string.Create(CultureInfo.CurrentCulture, $" · {alive} thermals followed");
        return string.Create(CultureInfo.CurrentCulture,
            $"cumulus over {100.0 * cloudy / columns:0.#}% of the terrain, shading {100.0 * shaded / columns:0.#}% · lift up to {best:0.0} m/s {height:0} m above the ground{followed}");
    }

    /// <summary>Gets the node.</summary>
    public AirflowNode Node => _node;

    /// <summary>Gets the terrain's tiles (the overlays are in their scene).</summary>
    public TerrainScene Tiles => _tiles;

    /// <summary>Gets the overlays.</summary>
    public AirflowScene Overlays => _overlays;

    /// <summary>Gets the field shown: the result, or the flow so far while simulating.</summary>
    public AirflowField? Field => _shown;

    /// <summary>Gets whether the result is of other settings than the current ones.</summary>
    public bool IsOutdated => _node.IsOutdated && !IsRunning;

    /// <summary>Gets whether the airflow has no terrain to run over.</summary>
    public bool HasNoTerrain => _node.Terrain is null;

    /// <summary>Gets or sets where the vertical section goes through (the view sets it to its camera's target).</summary>
    public Func<Vector2>? SectionCenter { get; set; }

    /// <summary>Occurs when the terrain's first tiles are in (the view frames them).</summary>
    public event EventHandler? TerrainShown;

    /// <summary>Occurs when the overlays changed (the view draws again).</summary>
    public event EventHandler? OverlaysChanged;

    /// <summary>Gets the color scale of the slice and the section.</summary>
    public AirflowColors.Scale Scale => AirflowColors.Of(Quantity);

    partial void OnShowStreamlinesChanged(bool value) => UpdateOverlays();
    partial void OnShowSectionChanged(bool value) => UpdateOverlays();
    partial void OnShowThermalsChanged(bool value)
    {
        UpdateOverlays();
        ShowFrame();
        ShowSky();
    }
    partial void OnShowGroundChanged(bool value) => UpdateOverlays();
    partial void OnGroundMapChanged(GroundMap value) => UpdateOverlays();

    partial void OnShowSliceChanged(bool value)
    {
        UpdateOverlays();
        ShowFrame();
        ShowSky();
    }

    partial void OnShowCloudsChanged(bool value)
    {
        UpdateOverlays();
        ShowFrame();
        ShowSky();
    }

    partial void OnSliceHeightChanged(double value)
    {
        UpdateOverlays();
        ShowFrame();
        ShowSky();
    }

    partial void OnQuantityChanged(AirflowQuantity value)
    {
        OnPropertyChanged(nameof(Scale));
        UpdateOverlays();
        ShowFrame();
        ShowSky();
    }

    partial void OnShowParticlesChanged(bool value)
    {
        _overlays.Particles.Show(value ? _shown : null, Ground);
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOutdated));
        OnPropertyChanged(nameof(HasGame));
        if (value) ShowGame = false;
        // After the run, its frames show the thermals it followed.
        ShowFrame();
    }

    partial void OnPlaySpeedChanged(double value) => OnPropertyChanged(nameof(SpeedText));

    /// <summary>Gets how fast the film plays, for people.</summary>
    public string SpeedText => string.Create(CultureInfo.CurrentCulture, $"×{PlaySpeed:0}");

    partial void OnShowSunChanged(bool value) => UpdateSun();

    partial void OnWithoutThermalsChanged(bool value)
    {
        if (IsRunning) return;
        ShowResult();
        if (_node.Result is not { } result) return;
        Status = value && result.HasBaseFlow
            ? "The base flow: the mean wind of the air between the thermals and clouds, what a game plays its thermals over."
            : Describe(result);
    }

    /// <summary>Gets whether the result has a base flow (the air between its thermals), from a run that resolved them.</summary>
    public bool HasBaseFlow => _node.Result?.HasBaseFlow == true;

    /// <summary>Gets where the sun stands for the airflow shown (the result's time, else the settings'), or <c>null</c> without a terrain.</summary>
    public SunPosition? Sun
    {
        get
        {
            if ((_shown ?? _node.Result)?.Info is { SunElevation: not 0 } info) return new SunPosition(info.SunAzimuth, info.SunElevation);
            if (_node.Terrain is not { } terrain) return null;
            var settings = _node.Settings(terrain);
            return SolarPosition.At(settings.Latitude, settings.Longitude, settings.UtcTime);
        }
    }

    /// <summary>Gets the sun's position for people, while it shows.</summary>
    public string SunText => ShowSun && Sun is { } sun ? "Sun " + SunMarker.Describe(sun) : "";

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AirflowNode.IsOutdated)) OnPropertyChanged(nameof(IsOutdated));
        if (e.PropertyName is nameof(AirflowNode.Result))
        {
            OnPropertyChanged(nameof(HasBaseFlow));
            OnPropertyChanged(nameof(HasGame));
            // Another result: the game played the last one.
            ShowGame = false;
        }
        if (e.PropertyName is nameof(AirflowNode.Result) && !IsRunning) ShowResult();
        if (e.PropertyName is nameof(AirflowNode.Result) or nameof(AirflowNode.Film) && !IsRunning) SetFilm(_node.Film, toEnd: true);
        if (e.PropertyName is nameof(ProjectNodeParent)) OnPropertyChanged(nameof(HasNoTerrain));
        // Without a result the sun is the settings' (their date and time).
        if (ShowSun && _shown is null) UpdateSun();
    }

    // The result, or its base flow (the mean wind of the air between the thermals; under the game too).
    private void ShowResult()
    {
        var result = _node.Result;
        Show((WithoutThermals || ShowGame) && result is { HasBaseFlow: true } ? result.BaseFlow() : result);
    }

    // The sun over the middle of the terrain, half its width away.
    private void UpdateSun()
    {
        if (ShowSun && _tiles.Bounds() is { } bounds)
        {
            var center = (bounds.Min + bounds.Max) / 2;
            float ground = Ground(center.X, center.Z);
            center.Y = float.IsNaN(ground) ? bounds.Min.Y : ground;
            _sun.Show(Sun, center, 0.45f * Math.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Z - bounds.Min.Z));
        }
        else
        {
            _sun.Show(null, default, 0);
        }
        OnPropertyChanged(nameof(SunText));
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private const string ProjectNodeParent = "Parent";

    #region Commands

    [RelayCommand(AllowConcurrentExecutions = true)]
    [property: Command("Simulate", Group, Label = "Simulate", Icon = MaterialIcons.PlayArrow,
        Description = "Simulate the airflow over the terrain with the settings; again to cancel", DefaultKeybinding = "R")]
    private async Task SimulateAsync()
    {
        if (IsRunning)
        {
            _run?.Cancel();
            return;
        }
        if (_node.Terrain is not { } terrain)
        {
            Status = "An airflow runs over a terrain: add it to one (select the terrain, then Add airflow).";
            return;
        }
        var cancellation = _run = new CancellationTokenSource();
        var token = cancellation.Token;
        IsRunning = true;
        Progress = 0;
        IsPlaying = false;
        _followLive = true;
        SetFilm(null, toEnd: true);
        try
        {
            var model = await TerrainAsync(token) ?? throw new OperationCanceledException();
            var settings = _node.Settings(terrain);
            Status = "Preparing: the land cover from the aerial images, the sun on the slopes and the day's heating so far…";
            using var simulation = await Task.Run(() => Prepare(model, settings, token), token);
            // A frame of the run every half minute: the clouds and the lift as they come and go, live and afterwards.
            simulation.FilmInterval = FilmInterval;
            var grid = simulation.Grid;
            string size = string.Create(CultureInfo.CurrentCulture, $"{grid.Nx} × {grid.Nz} × {grid.Ny} cells on {Short(simulation.Solver.Device)}");
            var progress = new UiProgress<AirflowProgress>(p =>
            {
                if (token.IsCancellationRequested || _disposed || _run != cancellation) return;
                Progress = p.Fraction;
                Status = Describe(p, settings, size);
                if (p.Snapshot is { } snapshot) Show(snapshot);
                if (simulation.Film is not { } film) return;
                if (film != _film) SetFilm(film, toEnd: true);
                else if (film.Count != _filmFrames) OnFilmFrames();
            });
            var field = await Task.Run(() => simulation.Run(progress, token), token);
            if (_disposed) return;
            _node.SetResult(field, simulation.Film);
            SetFilm(simulation.Film, toEnd: true);
            OnFilmFrames();
            Show(field);
            Status = Describe(field);
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
            SetFilm(_node.Film, toEnd: true);
            Show(_node.Result);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or ArgumentException)
        {
            Status = $"Couldn't simulate: {e.Message}";
            SetFilm(_node.Film, toEnd: true);
            Show(_node.Result);
        }
        finally
        {
            if (_run == cancellation) _run = null;
            IsRunning = false;
            Progress = 0;
            if (WithoutThermals && !_disposed) ShowResult();
        }
    }

    [RelayCommand]
    [property: Command("ToggleSun", Group, Label = "Show the sun", Icon = MaterialIcons.WbSunny,
        Description = "Show where the sun stands and how its light falls on the terrain", DefaultKeybinding = "U")]
    private void ToggleSun() => ShowSun = !ShowSun;

    [RelayCommand]
    [property: Command("ToggleBaseFlow", Group, Label = "Without the thermals", Icon = MaterialIcons.LayersClear,
        Description = "Show the base flow: the mean wind of the air between the thermals and clouds, what a game plays its thermals over",
        DefaultKeybinding = "B")]
    private void ToggleBaseFlow() => WithoutThermals = !WithoutThermals;

    [RelayCommand]
    [property: Command("ToggleOverTime", Group, Label = "Over time", Icon = MaterialIcons.Movie,
        Description = "Show the run over time (its clouds and lift coming and going) or its mean", DefaultKeybinding = "O")]
    private void ToggleOverTime() => OverTime = !OverTime;

    [RelayCommand]
    [property: Command("ToggleGame", Group, Label = "Game", Icon = MaterialIcons.SportsEsports,
        Description = "Play the thermals and cumulus as a game would, over the base flow: the convection model of a run that followed them", DefaultKeybinding = "M")]
    private void ToggleGame() => ShowGame = !ShowGame;

    [RelayCommand]
    [property: Command("PlayAirflow", Group, Label = "Play the run", Icon = MaterialIcons.PlayArrow,
        Description = "Play or pause the run over time", DefaultKeybinding = "Space")]
    private void TogglePlay()
    {
        if (ShowGame)
        {
            GamePlaying = !GamePlaying;
            return;
        }
        if (!HasFilm) return;
        OverTime = true;
        IsPlaying = !IsPlaying;
    }

    [RelayCommand]
    [property: Command("RestartAirflow", Group, Label = "Back to the start", Icon = MaterialIcons.SkipPrevious,
        Description = "Back to the start of the run", DefaultKeybinding = "Home")]
    private void Restart()
    {
        if (!ShowGame)
        {
            Seek(FilmStart);
            return;
        }
        // The game: another sky, of the next seed.
        _gameSeed++;
        _model = new ConvectionModel(_node.Result!, _gameSeed);
        _overlays.Particles.Wind = _model is { } model ? (x, y, z) => model.WindAt(x, y, z) : null;
        OnPropertyChanged(nameof(Model));
        GameText = "";
        ShowSky();
    }

    [RelayCommand]
    [property: Command("PreviousAirflowFrame", Group, Label = "Previous frame", Icon = MaterialIcons.ChevronLeft,
        Description = "Half a minute of the run back (pauses)", DefaultKeybinding = "Comma")]
    private void PreviousFrame() => Seek(FilmTime - FilmInterval);

    [RelayCommand]
    [property: Command("NextAirflowFrame", Group, Label = "Next frame", Icon = MaterialIcons.ChevronRight,
        Description = "Half a minute of the run on (pauses)", DefaultKeybinding = "Period")]
    private void NextFrame() => Seek(FilmTime + FilmInterval);

    [RelayCommand]
    [property: Command("SlowerAirflow", Group, Label = "Slower", Icon = MaterialIcons.FastRewind,
        Description = "Play the run slower", DefaultKeybinding = "Minus")]
    private void Slower()
    {
        if (ShowGame) GameSpeed = s_gameSpeeds[Math.Max(0, Array.IndexOf(s_gameSpeeds, GameSpeed) - 1)];
        else PlaySpeed = s_speeds[Math.Max(0, SpeedIndex() - 1)];
    }

    [RelayCommand]
    [property: Command("FasterAirflow", Group, Label = "Faster", Icon = MaterialIcons.FastForward,
        Description = "Play the run faster", DefaultKeybinding = "Equal")]
    private void Faster()
    {
        if (ShowGame) GameSpeed = s_gameSpeeds[Math.Min(s_gameSpeeds.Length - 1, Array.IndexOf(s_gameSpeeds, GameSpeed) + 1)];
        else PlaySpeed = s_speeds[Math.Min(s_speeds.Length - 1, SpeedIndex() + 1)];
    }

    private int SpeedIndex()
    {
        int best = 0;
        for (int i = 1; i < s_speeds.Length; i++) if (Math.Abs(s_speeds[i] - PlaySpeed) < Math.Abs(s_speeds[best] - PlaySpeed)) best = i;
        return best;
    }

    [RelayCommand]
    [property: Command("ToggleStreamlines", Group, Label = "Show streamlines", Icon = MaterialIcons.Timeline,
        Description = "Show the mean wind's paths, from the slice's height above the ground", DefaultKeybinding = "L")]
    private void ToggleStreamlines() => ShowStreamlines = !ShowStreamlines;

    [RelayCommand]
    [property: Command("ToggleParticles", Group, Label = "Stream particles", Icon = MaterialIcons.Air,
        Description = "Stream particles with the wind (sped up), like streamers", DefaultKeybinding = "P")]
    private void ToggleParticles() => ShowParticles = !ShowParticles;

    [RelayCommand]
    [property: Command("ToggleSlice", Group, Label = "Show the slice", Icon = MaterialIcons.Layers,
        Description = "Show the lift (or another quantity) at the slice's height above the ground", DefaultKeybinding = "H")]
    private void ToggleSlice() => ShowSlice = !ShowSlice;

    [RelayCommand]
    [property: Command("ToggleSection", Group, Label = "Show the section", Icon = MaterialIcons.VerticalSplit,
        Description = "Show a vertical section along the wind through the camera's target: waves, rotors, the thermals' tops", DefaultKeybinding = "V")]
    private void ToggleSection() => ShowSection = !ShowSection;

    [RelayCommand]
    [property: Command("ToggleThermals", Group, Label = "Show the thermals", Icon = MaterialIcons.Whatshot,
        Description = "Show the thermals as columns to their tops, with their cumulus' bases", DefaultKeybinding = "T")]
    private void ToggleThermals() => ShowThermals = !ShowThermals;

    [RelayCommand]
    [property: Command("ToggleClouds", Group, Label = "Show the clouds", Icon = MaterialIcons.Cloud,
        Description = "Show the clouds (where there is cloud a good part of the time)", DefaultKeybinding = "C")]
    private void ToggleClouds() => ShowClouds = !ShowClouds;

    [RelayCommand]
    [property: Command("ToggleGround", Group, Label = "Show the ground map", Icon = MaterialIcons.Map,
        Description = "Show the ground's heating (or land cover) and the likely thermal triggers", DefaultKeybinding = "G")]
    private void ToggleGround() => ShowGround = !ShowGround;

    [RelayCommand]
    [property: Command("NextQuantity", Group, Label = "Next quantity", Icon = MaterialIcons.Palette,
        Description = "Color the slice and the section by the next quantity: lift, turbulence, wind, lift chance, warming", DefaultKeybinding = "N")]
    private void NextQuantity() => Quantity = (AirflowQuantity)(((int)Quantity + 1) % Enum.GetValues<AirflowQuantity>().Length);

    [RelayCommand]
    [property: Command("SliceUp", Group, Label = "Raise the slice", Icon = MaterialIcons.ArrowUpward,
        Description = "Raise the slice by 100 m above the ground", DefaultKeybinding = "PageUp")]
    private void SliceUp() => SliceHeight = Math.Min(4000, SliceHeight + 100);

    [RelayCommand]
    [property: Command("SliceDown", Group, Label = "Lower the slice", Icon = MaterialIcons.ArrowDownward,
        Description = "Lower the slice by 100 m above the ground", DefaultKeybinding = "PageDown")]
    private void SliceDown() => SliceHeight = Math.Max(20, SliceHeight - 100);

    #endregion

    /// <summary>Builds the overlays again (the section's center may have moved).</summary>
    public void UpdateOverlays()
    {
        if (_shown is not { } field || _terrain is not { } terrain)
        {
            _overlays.Clear();
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // Over time the film's frame shows the lift at the slice's height and the clouds, in the game the model; the
        // thermal columns are the mean's.
        bool film = ShowsFilm || ShowGame;
        var options = new AirflowViewOptions
        {
            Streamlines = ShowStreamlines, Particles = ShowParticles, Slice = ShowSlice && !(film && Quantity == AirflowQuantity.Lift),
            Section = ShowSection, Quantity = Quantity, SliceHeight = SliceHeight, SectionCenter = SectionCenter?.Invoke() ?? Vector2.Zero,
            Thermals = ShowThermals && !film, Clouds = ShowClouds && !film, Ground = ShowGround, GroundMap = GroundMap,
        };
        int version = ++_overlayVersion;
        Task.Run(() => AirflowScene.Build(field, options, terrain.HeightAt)).ContinueWith(task =>
        {
            if (task.IsFaulted || version != _overlayVersion || _disposed) return;
            Dispatcher.Post(() =>
            {
                if (version != _overlayVersion || _disposed) return;
                _overlays.Apply(task.Result);
                OverlaysChanged?.Invoke(this, EventArgs.Empty);
            });
        }, TaskScheduler.Default);
    }

    private void Show(AirflowField? field)
    {
        _shown = field;
        OnPropertyChanged(nameof(Field));
        if (ShowParticles) _overlays.Particles.Show(field, Ground);
        UpdateOverlays();
        if (ShowSun) UpdateSun();
    }

    private float Ground(double x, double z) => _terrain?.HeightAt(x, z) ?? float.NaN;

    private async Task LoadTerrainAsync()
    {
        try
        {
            if (await TerrainAsync(CancellationToken.None) is null) return;
            ShowResult();
            ShowFrame();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            Status = $"Couldn't build the terrain: {e.Message}";
        }
    }

    // The terrain, built once (or taken from its preview) and shown.
    private Task<TerrainModel?> TerrainAsync(CancellationToken cancellationToken)
    {
        if (_terrain is { } built && _node.Terrain is { } node && built.Settings == node.Snapshot()) return Task.FromResult<TerrainModel?>(built);
        if (_loading is { IsCompleted: false } loading) return loading;
        return _loading = LoadAsync();

        async Task<TerrainModel?> LoadAsync()
        {
            if (_node.Terrain is not { } terrain) return null;
            string before = Status;
            Status = "Building the terrain…";
            var progress = new UiProgress<TerrainProgress>(p =>
            {
                if (!_disposed && IsRunning) Progress = p.Fraction * 0.05;
            });
            var model = await _terrains.ModelAsync(terrain, progress, cancellationToken);
            if (_disposed) return null;
            bool first = _terrain is null;
            _terrain = model;
            _tiles.Show(model.Settings, model.Layout, model.Tiles);
            Status = before;
            if (first) TerrainShown?.Invoke(this, EventArgs.Empty);
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
            return model;
        }
    }

    // The terrain's heights at a quarter of the cell size (and its aerial images' colors there), and the simulation.
    private static AirflowSimulation Prepare(TerrainModel model, AirflowSettings settings, CancellationToken cancellationToken)
    {
        double extent = model.Layout.Extent;
        double spacing = Math.Max(settings.CellSize / 4, extent / 1600);
        int n = (int)Math.Floor(extent / spacing) + 1;
        double x0 = -(n - 1) * spacing / 2;
        float fallback = model.MinHeight;
        var heights = HeightField.Sample(x0, x0, spacing, n, n, (x, z) =>
        {
            float h = model.HeightAt(x, z);
            return float.IsNaN(h) ? fallback : h;
        });
        cancellationToken.ThrowIfCancellationRequested();
        uint[]? colors = model.Settings.Texture == TerrainTexture.Imagery ? model.SampleColors(x0, x0, spacing, n, n) : null;
        cancellationToken.ThrowIfCancellationRequested();
        // The graphics card when there is one (compiling its kernels the first time takes a moment).
        return new AirflowSimulation(heights, colors, settings, cancellationToken, AirflowGpu.Factory);
    }

    // "NVIDIA GeForce RTX 4090" → "the GeForce RTX 4090"; "CPU, 32 threads" → "the CPU".
    private static string Short(string device) =>
        device.StartsWith("CPU", StringComparison.Ordinal) ? "the CPU" : "the " + device.Replace("NVIDIA ", "", StringComparison.Ordinal);

    private static string Describe(AirflowProgress p, AirflowSettings settings, string size)
    {
        var culture = CultureInfo.CurrentCulture;
        string stage = p.Time >= settings.Duration - settings.AveragingTime ? "averaging" : "developing";
        double perStep = p.Steps > 0 ? p.Elapsed.TotalSeconds / p.Steps : 0;
        double left = p.Fraction > 0.02 ? p.Elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction : double.NaN;
        string remaining = double.IsNaN(left) ? "" : left < 90 ? string.Create(culture, $" · about {left:0} s left") : string.Create(culture, $" · about {left / 60:0} min left");
        return string.Create(culture,
            $"Simulating ({stage}): {Clock.Of(p.Time)} of {Clock.Of(p.Duration)} · {size} · {p.Steps} steps of {p.TimeStep:0.0} s, {perStep:0.00} s each{remaining}");
    }

    private static string Describe(AirflowField field)
    {
        var culture = CultureInfo.CurrentCulture;
        string thermals = field.Thermals.Count == 0 ? "no thermals"
            : string.Create(culture, $"{field.Thermals.Count} thermals, the best {field.Thermals.Max(t => t.Climb):0.0} m/s");
        var tops = field.BoundaryLayerTop.Order().ToArray();
        string top = string.Create(culture, $"boundary layer to {tops[tops.Length / 2]:#,0} m");
        return string.Create(culture,
            $"{thermals} · {top} · {field.Info.SimulatedTime / 60:0} min simulated in {field.Info.ComputeTime:0.#} s ({field.Nx} × {field.Nz} × {field.Ny} cells{(field.Info.Device.Length > 0 ? " on " + Short(field.Info.Device) : "")})");
    }

    // Reports on the UI thread.
    private sealed class UiProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => Dispatcher.Post(() => report(value));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _run?.Cancel();
        IsPlaying = false;
        _node.PropertyChanged -= OnNodePropertyChanged;
    }
}
