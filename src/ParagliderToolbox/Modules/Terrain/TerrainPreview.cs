using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using Atelier.Core.Threading;
using Atelier.Graphics3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Terrain;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>
/// The preview of a <see cref="TerrainNode"/>: builds the terrain from the sources in the background (the first time
/// it is shown, then when asked), shows the tiles as they come in and chooses their levels of detail for the camera.
/// The commands are the <see cref="Group"/> keybinding group, active while the preview has the focus.
/// </summary>
/// <remarks>
/// Building downloads data, so a change to the settings doesn't rebuild by itself: the preview reports that its terrain
/// is out of date (<see cref="IsOutdated"/>) until <see cref="GenerateCommand"/> runs.
/// </remarks>
public sealed partial class TerrainPreview : ObservableObject, IDisposable
{
    /// <summary>The keybinding group of the preview's commands.</summary>
    public const string Group = "Terrain preview";

    private readonly TerrainNode _node;
    private readonly Func<TerrainActions> _actions;
    private readonly TerrainScene _scene = new();
    private readonly Dictionary<TileKey, TerrainTile> _built = [];
    private CancellationTokenSource? _build;
    private int _builtVersion = -1;
    private bool _disposed;
    private bool _framed;

    [ObservableProperty] private TerrainModel? _model;
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _sources = "";
    [ObservableProperty] private string _view = "";
    [ObservableProperty] private bool _isOutdated;
    [ObservableProperty] private bool _showLevels;
    [ObservableProperty] private bool _textured = true;

    /// <summary>Initializes the preview of <paramref name="node"/> and starts building it.</summary>
    public TerrainPreview(TerrainNode node, Func<TerrainActions> actions)
    {
        _node = node;
        _actions = actions;
        _node.PropertyChanged += OnNodePropertyChanged;
        _ = GenerateAsync();
    }

    /// <summary>Gets the node.</summary>
    public TerrainNode Node => _node;

    /// <summary>Gets the scene the viewport shows.</summary>
    public Scene3D Scene => _scene.Scene;

    /// <summary>Gets the tiles shown.</summary>
    public TerrainScene Tiles => _scene;

    /// <summary>Occurs when the first tiles of a new terrain are in (the view frames them).</summary>
    public event EventHandler? FirstTilesShown;

    /// <summary>Occurs when tiles were added (the view chooses the levels of detail again).</summary>
    public event EventHandler? TilesChanged;

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        IsOutdated = _node.SettingsVersion != _builtVersion && (Model != null || IsBuilding);

    partial void OnShowLevelsChanged(bool value) => _scene.ShowLevels = value;

    partial void OnTexturedChanged(bool value) => _scene.Textured = value;

    #region Commands

    [RelayCommand(AllowConcurrentExecutions = true)]
    [property: Command("Generate", Group, Label = "Generate", Icon = MaterialIcons.Refresh,
        Description = "Build the terrain from its settings (downloading what isn't cached yet); again to cancel", DefaultKeybinding = "G")]
    private async Task GenerateAsync()
    {
        if (IsBuilding)
        {
            _build?.Cancel();
            return;
        }
        var settings = _node.Snapshot();
        var layout = _node.Layout;
        if (!await _actions().ConfirmLargeBuildAsync(_node, layout) || _disposed) return;

        var cancellation = _build = new CancellationTokenSource();
        int version = _node.SettingsVersion;
        IsBuilding = true;
        IsOutdated = false;
        Progress = 0;
        Status = "Finding the data…";
        _built.Clear();
        _scene.Show(settings, layout, _built);
        _framed = false;
        var progress = new UiProgress(p =>
        {
            if (cancellation.IsCancellationRequested || _disposed) return;
            if (p.Tile is { } tile) _built[tile.Key] = tile;
            Progress = p.Fraction;
            Status = string.Create(CultureInfo.CurrentCulture, $"Building: {p.TilesDone} of {p.TileCount} tiles · {p.BytesDownloaded / 1e6:0.0} MB downloaded");
            if (!_framed && layout.Roots.All(_built.ContainsKey))
            {
                _framed = true;
                FirstTilesShown?.Invoke(this, EventArgs.Empty);
            }
            TilesChanged?.Invoke(this, EventArgs.Empty);
        });
        try
        {
            var model = await Task.Run(() => _actions().Builder.BuildAsync(settings, progress, cancellation.Token), cancellation.Token);
            if (_disposed) return;
            // Progress reports may still be queued; the model has every tile.
            foreach (var (key, tile) in model.Tiles) _built[key] = tile;
            Model = model;
            _builtVersion = version;
            IsOutdated = _node.SettingsVersion != version;
            Status = Describe(model);
            Sources = string.Join("   ·   ", new[]
            {
                Shares("Elevation", model.ElevationSources),
                Shares("Imagery", model.ImagerySources),
            }.Where(s => s.Length > 0));
            if (!_framed)
            {
                _framed = true;
                FirstTilesShown?.Invoke(this, EventArgs.Empty);
            }
            TilesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled: the tiles built so far are shown.";
            IsOutdated = true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or NotSupportedException)
        {
            Status = $"Couldn't build the terrain: {e.Message}";
            IsOutdated = true;
        }
        finally
        {
            if (_build == cancellation) _build = null;
            IsBuilding = false;
            Progress = 1;
        }
    }

    private static string Shares(string label, IReadOnlyList<SourceShare> shares) => shares.Count == 0 ? ""
        : label + ": " + string.Join(", ", shares.Select(s => string.Create(CultureInfo.CurrentCulture, $"{s.Source.Name} {s.Share:P0}")));

    private static string Describe(TerrainModel model)
    {
        var layout = model.Layout;
        string missing = model.MissingShare > 0.0005 ? string.Create(CultureInfo.CurrentCulture, $" · {model.MissingShare:P1} without data") : "";
        return string.Create(CultureInfo.CurrentCulture,
            $"{layout.Extent / 1000:0.##} km · {layout.Tiles.Count} tiles in {layout.LevelCount} levels · heights {model.MinHeight:0}–{model.MaxHeight:0} m · " +
            $"built in {model.Elapsed.TotalSeconds:0.0} s ({model.BytesDownloaded / 1e6:0.0} MB downloaded){missing}");
    }

    [RelayCommand]
    [property: Command("ToggleLevels", Group, Label = "Show levels of detail", Icon = MaterialIcons.Layers,
        Description = "Tint the tiles by their level of detail", DefaultKeybinding = "L")]
    private void ToggleLevels() => ShowLevels = !ShowLevels;

    [RelayCommand]
    [property: Command("ToggleTexture", Group, Label = "Show texture", Icon = MaterialIcons.Texture,
        Description = "Show the texture or a plain surface", DefaultKeybinding = "T")]
    private void ToggleTexture() => Textured = !Textured;

    #endregion

    /// <summary>Chooses the tiles for the camera; returns whether another frame should choose again (meshes still to make).</summary>
    public bool UpdateLevels(OrbitCamera camera, float viewportHeight)
    {
        _scene.Select(camera.Position, camera.FieldOfView, viewportHeight);
        View = string.Create(CultureInfo.CurrentCulture, $"showing {_scene.ShownTiles} tiles, {_scene.ShownTriangles / 1000.0:0.#}k triangles");
        return _scene.NeedsAnotherPass;
    }

    // Reports on the UI thread (the builder runs on worker threads).
    private sealed class UiProgress(Action<TerrainProgress> report) : IProgress<TerrainProgress>
    {
        public void Report(TerrainProgress value) => Dispatcher.Post(() => report(value));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _build?.Cancel();
        _node.PropertyChanged -= OnNodePropertyChanged;
    }
}
