using System.ComponentModel;
using System.Globalization;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using Atelier.Core.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Features;

namespace ParagliderToolbox.Modules.Features;

/// <summary>
/// The view model of a <see cref="FeaturesNode"/>'s detail view: its terrain (built, or from its preview), the area
/// to search outlined on it, the search (in the background, downloading what isn't cached) and the features found as
/// overlays. The commands are the <see cref="Group"/> keybinding group, active while the view has the focus.
/// </summary>
public sealed partial class FeaturesPreview : ObservableObject, IDisposable
{
    /// <summary>The keybinding group of the view's commands.</summary>
    public const string Group = "Features view";

    private readonly FeaturesNode _node;
    private readonly TerrainModule _terrains;
    private readonly FeatureDetector _detector;
    private readonly TerrainScene _tiles = new();
    private readonly FeaturesScene _overlays;
    private CancellationTokenSource? _run;
    private TerrainModel? _terrain;
    private Task<TerrainModel?>? _loading;
    private int _overlayVersion;
    private bool _disposed;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showTrees = true;
    [ObservableProperty] private bool _showRoads = true;
    [ObservableProperty] private bool _showBuildings = true;
    [ObservableProperty] private bool _showRocks = true;
    [ObservableProperty] private bool _showAreas = true;
    [ObservableProperty] private bool _showLandCover;
    [ObservableProperty] private bool _showLines = true;
    [ObservableProperty] private bool _showLandmarks = true;

    /// <summary>Initializes the view model of <paramref name="node"/> and loads its terrain.</summary>
    public FeaturesPreview(FeaturesNode node, TerrainModule terrains, FeatureDetector detector)
    {
        _node = node;
        _terrains = terrains;
        _detector = detector;
        _overlays = new FeaturesScene(_tiles.Scene);
        _node.PropertyChanged += OnNodePropertyChanged;
        Status = node.Result is { } result ? Describe(result) : "Detect (R) looks for trees, roads, buildings, boulders and areas in the outlined square.";
        _ = LoadTerrainAsync();
    }

    /// <summary>Gets the node.</summary>
    public FeaturesNode Node => _node;

    /// <summary>Gets the terrain's tiles (the overlays are in their scene).</summary>
    public TerrainScene Tiles => _tiles;

    /// <summary>Gets whether the result is of other settings than the current ones.</summary>
    public bool IsOutdated => _node.IsOutdated && !IsRunning;

    /// <summary>Gets whether the features have no terrain to be found in.</summary>
    public bool HasNoTerrain => _node.Terrain is null;

    /// <summary>Gets the area to search (or searched), or <c>null</c> without a terrain.</summary>
    public FeatureArea? Area => _node.Terrain is { } terrain ? _node.Area(terrain) : null;

    /// <summary>Occurs when the terrain's first tiles are in (the view frames the area).</summary>
    public event EventHandler? TerrainShown;

    /// <summary>Occurs when the overlays changed (the view draws again).</summary>
    public event EventHandler? OverlaysChanged;

    partial void OnShowTreesChanged(bool value) => UpdateOverlays();
    partial void OnShowRoadsChanged(bool value) => UpdateOverlays();
    partial void OnShowBuildingsChanged(bool value) => UpdateOverlays();
    partial void OnShowRocksChanged(bool value) => UpdateOverlays();
    partial void OnShowAreasChanged(bool value) => UpdateOverlays();
    partial void OnShowLandCoverChanged(bool value) => UpdateOverlays();
    partial void OnShowLinesChanged(bool value) => UpdateOverlays();
    partial void OnShowLandmarksChanged(bool value) => UpdateOverlays();

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsOutdated));

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FeaturesNode.IsOutdated)) OnPropertyChanged(nameof(IsOutdated));
        if (e.PropertyName is "Parent")
        {
            OnPropertyChanged(nameof(HasNoTerrain));
            _ = LoadTerrainAsync();
        }
        // The area's outline follows the settings; the result shows when it comes.
        if (e.PropertyName is nameof(FeaturesNode.Center) or nameof(FeaturesNode.Size) or nameof(FeaturesNode.Result))
        {
            OnPropertyChanged(nameof(Area));
            if (!IsRunning) UpdateOverlays();
        }
        if (e.PropertyName is nameof(FeaturesNode.Result) && !IsRunning && _node.Result is { } result) Status = Describe(result);
    }

    #region Commands

    [RelayCommand(AllowConcurrentExecutions = true)]
    [property: Command("DetectFeatures", Group, Label = "Detect features", Icon = MaterialIcons.ImageSearch,
        Description = "Look for trees, roads, buildings, boulders and areas in the aerial images of the area (downloading what isn't cached); again to cancel",
        DefaultKeybinding = "R")]
    private async Task DetectAsync()
    {
        if (IsRunning)
        {
            _run?.Cancel();
            return;
        }
        if (_node.Terrain is not { } terrain)
        {
            Status = "Features are found in a terrain: add them to one (select the terrain, then Add features).";
            return;
        }
        var cancellation = _run = new CancellationTokenSource();
        var token = cancellation.Token;
        IsRunning = true;
        Progress = 0;
        try
        {
            var settings = _node.Settings();
            var area = _node.Area(terrain);
            var origin = terrain.Snapshot().Center;
            Status = "Starting…";
            var progress = new UiProgress<FeatureProgress>(p =>
            {
                if (token.IsCancellationRequested || _disposed || _run != cancellation) return;
                Progress = p.Fraction;
                Status = Describe(p);
            });
            var features = await Task.Run(() => _detector.DetectAsync(origin, area, settings, progress, token), token);
            if (_disposed) return;
            _node.Result = features;
            Status = Describe(features);
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            Status = $"Couldn't detect the features: {e.Message}";
        }
        finally
        {
            if (_run == cancellation) _run = null;
            IsRunning = false;
            Progress = 0;
            UpdateOverlays();
        }
    }

    [RelayCommand]
    [property: Command("ToggleFeatureTrees", Group, Label = "Show the trees", Icon = MaterialIcons.Forest,
        Description = "Show the trees (cones: conifers) and bushes", DefaultKeybinding = "T")]
    private void ToggleTrees() => ShowTrees = !ShowTrees;

    [RelayCommand]
    [property: Command("ToggleFeatureRoads", Group, Label = "Show the roads", Icon = MaterialIcons.Route,
        Description = "Show the roads and paths, colored by kind, and their junctions", DefaultKeybinding = "P")]
    private void ToggleRoads() => ShowRoads = !ShowRoads;

    [RelayCommand]
    [property: Command("ToggleFeatureBuildings", Group, Label = "Show the buildings", Icon = MaterialIcons.House,
        Description = "Show the buildings with their roofs", DefaultKeybinding = "B")]
    private void ToggleBuildings() => ShowBuildings = !ShowBuildings;

    [RelayCommand]
    [property: Command("ToggleFeatureRocks", Group, Label = "Show the boulders", Icon = MaterialIcons.Landslide,
        Description = "Show the boulders", DefaultKeybinding = "K")]
    private void ToggleRocks() => ShowRocks = !ShowRocks;

    [RelayCommand]
    [property: Command("ToggleFeatureAreas", Group, Label = "Show the areas", Icon = MaterialIcons.Polyline,
        Description = "Show the outlines of forests, rock, cliffs, gravel, asphalt, water and snow", DefaultKeybinding = "O")]
    private void ToggleAreas() => ShowAreas = !ShowAreas;

    [RelayCommand]
    [property: Command("ToggleLandCover", Group, Label = "Show the ground cover", Icon = MaterialIcons.Map,
        Description = "Show the ground cover map: what covers every square meter", DefaultKeybinding = "L")]
    private void ToggleLandCover() => ShowLandCover = !ShowLandCover;

    [RelayCommand]
    [property: Command("ToggleFeatureLines", Group, Label = "Show the lines", Icon = MaterialIcons.Timeline,
        Description = "Show the map's lines: waterways, power lines and aerialways with their towers, railways, fences and walls, cliffs' edges", DefaultKeybinding = "I")]
    private void ToggleLines() => ShowLines = !ShowLines;

    [RelayCommand]
    [property: Command("ToggleFeatureLandmarks", Group, Label = "Show the landmarks", Icon = MaterialIcons.Place,
        Description = "Show the map's landmarks as pins: takeoffs (green), landings (blue), masts and towers (red), peaks (white), huts, signposts … (yellow)", DefaultKeybinding = "M")]
    private void ToggleLandmarks() => ShowLandmarks = !ShowLandmarks;

    #endregion

    /// <summary>Builds the overlays again.</summary>
    public void UpdateOverlays()
    {
        if (_terrain is not { } terrain || Area is not { } area)
        {
            _overlays.Clear();
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var features = _node.Result;
        var options = new FeatureViewOptions
        {
            Trees = ShowTrees, Roads = ShowRoads, Buildings = ShowBuildings, Rocks = ShowRocks, Areas = ShowAreas, LandCover = ShowLandCover,
            Lines = ShowLines, Landmarks = ShowLandmarks,
        };
        int version = ++_overlayVersion;
        Task.Run(() => FeaturesScene.Build(features, area, options, terrain.HeightAt)).ContinueWith(task =>
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

    private async Task LoadTerrainAsync()
    {
        try
        {
            if (await TerrainAsync(CancellationToken.None) is null) return;
            UpdateOverlays();
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
            var model = await _terrains.ModelAsync(terrain, null, cancellationToken);
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

    private static string Describe(FeatureProgress p)
    {
        var culture = CultureInfo.CurrentCulture;
        string stage = p.Stage == "reading" ? string.Create(culture, $"block {Math.Min(p.BlocksDone + 1, p.BlockCount)} of {p.BlockCount}") : p.Stage;
        return string.Create(culture,
            $"Looking for features: {stage} · {p.Trees:#,0} trees, {p.RoadLength / 1000:0.0} km of roads, {p.Buildings:#,0} buildings so far · {p.BytesDownloaded / 1e6:0.0} MB downloaded in {p.Requests} requests · {p.Elapsed.TotalSeconds:0} s");
    }

    private static string Describe(FeatureSet features)
    {
        var culture = CultureInfo.CurrentCulture;
        double roads = features.Roads.Sum(r => r.Length);
        string notes = features.Notes.Count > 0 ? " · " + features.Notes[0] : "";
        return string.Create(culture,
            $"{features.Trees.Count:#,0} trees and bushes, {roads / 1000:0.0} km of roads and paths, {features.Buildings.Count:#,0} buildings, {features.Rocks.Count:#,0} boulders, {features.Areas.Count:#,0} areas, {features.Lines.Count:#,0} lines, {features.Landmarks.Count:#,0} landmarks in {features.Elapsed.TotalSeconds:0} s{notes}");
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
        _node.PropertyChanged -= OnNodePropertyChanged;
    }
}
