using System.ComponentModel;
using System.Globalization;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Export;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>
/// The commands on the selected terrain that work anywhere in the window (the <see cref="Group"/> group): exporting it
/// as tiles or as one glTF file, and clearing the download cache. Also holds the sources and the builder the previews
/// share.
/// </summary>
/// <remarks>
/// Exports use the terrain the preview built when it matches the settings; otherwise they build it first (from the
/// cache where the data is already downloaded).
/// </remarks>
public sealed partial class TerrainActions : ObservableObject
{
    /// <summary>The keybinding group of the commands.</summary>
    public const string Group = "Terrain";

    /// <summary>Builds above this many height samples ask first.</summary>
    public const long LargeBuildSamples = 60_000_000;

    private readonly Toolbox _toolbox;
    private readonly Func<TerrainNode, TerrainModel?> _built;
    private string? _lastFolder;
    private TerrainTileExportOptions _tileOptions = new();
    private int _glbLevel;

    /// <summary>Initializes the commands for <paramref name="toolbox"/>.</summary>
    /// <param name="toolbox">The toolbox.</param>
    /// <param name="sources">The terrain sources.</param>
    /// <param name="built">Gets the terrain a preview built for a node, or <c>null</c>.</param>
    public TerrainActions(Toolbox toolbox, TerrainSources sources, Func<TerrainNode, TerrainModel?> built)
    {
        _toolbox = toolbox;
        _built = built;
        Sources = sources;
        Builder = new TerrainBuilder(sources);
        _toolbox.PropertyChanged += OnToolboxPropertyChanged;
    }

    /// <summary>Gets the sources terrains are built from.</summary>
    public TerrainSources Sources { get; }

    /// <summary>Gets the builder.</summary>
    public TerrainBuilder Builder { get; }

    private TerrainNode? Selected => _toolbox.SelectedNode as TerrainNode;

    private bool HasTerrain() => Selected != null && !IsExporting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportTilesCommand), nameof(ExportGlbCommand))]
    private bool _isExporting;

    /// <summary>Asks before a very large build; returns whether to go ahead.</summary>
    public async Task<bool> ConfirmLargeBuildAsync(TerrainNode node, TerrainLayout layout)
    {
        if (layout.SampleCount <= LargeBuildSamples) return true;
        return await _toolbox.Dialogs.ConfirmAsync("Build a large terrain?",
            string.Create(CultureInfo.CurrentCulture,
                $"'{node.Name}' has {layout.SampleCount / 1e6:0}M height samples in {layout.Tiles.Count} tiles: building it takes a while, much memory and possibly gigabytes of downloads. A coarser resolution, larger tiles or a smaller full detail area (Area) make it smaller."),
            "Build");
    }

    [RelayCommand(CanExecute = nameof(HasTerrain))]
    [property: Command("ExportTiles", Group, Label = "Export terrain _tiles…", Icon = MaterialIcons.Output,
        Description = "Write the terrain as a tile set for a game: per tile a glTF mesh, a heightmap and a texture, and a manifest of the levels of detail")]
    private async Task ExportTilesAsync()
    {
        if (Selected is not { } node) return;
        var options = _tileOptions.Copy();
        if (!await _toolbox.Dialogs.EditPropertiesAsync("Export terrain tiles", options, "Export",
            $"Writes every tile of '{node.Name}' ({node.TileCount} in {node.Layout.LevelCount} levels) and terrain.json, which describes them, into a folder."))
        {
            return;
        }
        _tileOptions = options;
        string? folder = await _toolbox.Dialogs.PickFolderAsync("Export terrain into", _lastFolder ?? ProjectFolder());
        if (folder is null) return;
        _lastFolder = folder;
        string target = Path.Combine(folder, BaseName(node.Name));
        await RunExportAsync(node, model =>
        {
            var files = TerrainExporter.ExportTiles(model, target, options.ToOptions());
            long bytes = files.Sum(f => new FileInfo(f).Length);
            return string.Create(CultureInfo.CurrentCulture, $"{files.Count} files ({bytes / 1e6:0.0} MB) in\n{target}");
        });
    }

    [RelayCommand(CanExecute = nameof(HasTerrain))]
    [property: Command("ExportTerrainGlb", Group, Label = "Export terrain _glTF…", Icon = MaterialIcons.ViewInAr,
        Description = "Write the most detailed tiles of the terrain as one glTF binary (.glb), for Blender or a small game map")]
    private async Task ExportGlbAsync()
    {
        if (Selected is not { } node) return;
        var options = new TerrainGlbExportOptions(node.Layout.LevelCount) { FinestLevel = Math.Min(_glbLevel, node.Layout.LevelCount - 1) };
        if (!await _toolbox.Dialogs.EditPropertiesAsync("Export terrain glTF", options, "Export",
            "Writes the most detailed tiles everywhere, down to the chosen level, as one file: each tile a node with its mesh and texture."))
        {
            return;
        }
        _glbLevel = options.FinestLevel;
        string? path = await _toolbox.Dialogs.PickSaveFileAsync("Export terrain glTF", "glTF binary (*.glb)|*.glb", BaseName(node.Name) + ".glb",
            _lastFolder ?? ProjectFolder());
        if (path is null) return;
        _lastFolder = Path.GetDirectoryName(path);
        await RunExportAsync(node, model =>
        {
            File.WriteAllBytes(path, TerrainExporter.ToGlb(model, options.FinestLevel));
            string credits = Path.ChangeExtension(path, ".attribution.txt");
            File.WriteAllText(credits, TerrainExporter.AttributionText(model));
            return string.Create(CultureInfo.CurrentCulture, $"{Path.GetFileName(path)} ({new FileInfo(path).Length / 1e6:0.0} MB)\n{Path.GetFileName(credits)}\n\nin {Path.GetDirectoryName(path)}");
        });
    }

    [RelayCommand]
    [property: Command("ClearTerrainCache", Group, Label = "Clear terrain _cache…", Icon = MaterialIcons.DeleteSweep,
        Description = "Delete the downloaded elevation and imagery data (terrains download it again when built)")]
    private async Task ClearCacheAsync()
    {
        if (Sources.Cache is not { } cache) return;
        long size = await Task.Run(cache.GetSize);
        if (!await _toolbox.Dialogs.ConfirmAsync("Clear the terrain cache?",
            string.Create(CultureInfo.CurrentCulture, $"{size / 1e6:0.0} MB of downloaded data in {cache.Folder}. Terrains download what they need again when they are built."),
            "Clear"))
        {
            return;
        }
        try
        {
            await Task.Run(cache.Clear);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't clear the cache", e.Message);
        }
    }

    private async Task RunExportAsync(TerrainNode node, Func<TerrainModel, string> write)
    {
        IsExporting = true;
        try
        {
            var settings = node.Snapshot();
            var model = _built(node) is { } shown && shown.Settings == settings ? shown : null;
            if (model is null)
            {
                if (!await ConfirmLargeBuildAsync(node, node.Layout)) return;
                model = await Task.Run(() => Builder.BuildAsync(settings));
            }
            string result = await Task.Run(() => write(model));
            await _toolbox.Dialogs.ShowMessageAsync("Export finished", result);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't export", e.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private string? ProjectFolder() => _toolbox.Document.FilePath is { } path ? Path.GetDirectoryName(path) : null;

    private static string BaseName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(text.Select(c => invalid.Contains(c) || c == ' ' || c == ':' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "terrain" : name;
    }

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Toolbox.SelectedNode)) return;
        ExportTilesCommand.NotifyCanExecuteChanged();
        ExportGlbCommand.NotifyCanExecuteChanged();
    }
}
