using System.ComponentModel;
using System.Globalization;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Terrain.Features;

namespace ParagliderToolbox.Modules.Features;

/// <summary>The feature commands that work wherever the focus is: the export of the selected features for a game.</summary>
public sealed partial class FeaturesActions : ObservableObject
{
    /// <summary>The keybinding group of the commands.</summary>
    public const string Group = "Features";

    private readonly Toolbox _toolbox;
    private FeaturesNode? _watched;
    private string? _lastFolder;

    /// <summary>Initializes the commands for <paramref name="toolbox"/>.</summary>
    public FeaturesActions(Toolbox toolbox)
    {
        _toolbox = toolbox;
        _toolbox.PropertyChanged += OnToolboxPropertyChanged;
    }

    private FeaturesNode? Selected => _toolbox.SelectedNode as FeaturesNode;

    private bool CanExport() => Selected?.Result != null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    [property: Command("ExportFeatures", Group, Label = "Export _features…", Icon = MaterialIcons.Output,
        Description = "Write the features found for a game: features.json (trees, roads, buildings, boulders, areas), the ground cover map as PNG, GeoJSON for map tools")]
    private async Task ExportAsync()
    {
        if (Selected is not { Result: { } features } node) return;
        string? folder = await _toolbox.Dialogs.PickFolderAsync("Export features into",
            _lastFolder ?? (_toolbox.Document.FilePath is { } project ? Path.GetDirectoryName(project) : null));
        if (folder is null) return;
        _lastFolder = folder;
        string target = Path.Combine(folder, FileName(node.Terrain?.Name is { } terrain ? $"{terrain} {node.Name}" : node.Name));
        try
        {
            var files = await Task.Run(() => FeatureFiles.Export(features, target));
            long bytes = files.Sum(f => new FileInfo(f).Length);
            await _toolbox.Dialogs.ShowMessageAsync("Export finished", string.Create(CultureInfo.CurrentCulture,
                $"{files.Count} files ({bytes / 1e6:0.0} MB): {features.Trees.Count:#,0} trees, {features.Roads.Count:#,0} roads, {features.Buildings.Count:#,0} buildings, {features.Rocks.Count:#,0} boulders, {features.Areas.Count:#,0} areas.\n\nin {target}\n\ndocs/FeaturesFormat.md describes the files; ParagliderToolbox.Terrain reads them (FeatureFiles.FromJson)."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't export", e.Message);
        }
    }

    private static string FileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(text.Select(c => invalid.Contains(c) || c == ' ' || c == ':' || c == ',' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "features" : name;
    }

    // The export follows the selection and the selected node's result.
    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Toolbox.SelectedNode)) return;
        if (_watched != null) _watched.PropertyChanged -= OnNodePropertyChanged;
        _watched = Selected;
        if (_watched != null) _watched.PropertyChanged += OnNodePropertyChanged;
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeaturesNode.Result)) ExportCommand.NotifyCanExecuteChanged();
    }
}
