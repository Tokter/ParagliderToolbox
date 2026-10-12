using System.ComponentModel;
using System.Globalization;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>The airflow commands that work wherever the focus is: the export of the selected airflow for a game.</summary>
public sealed partial class AirflowActions : ObservableObject
{
    /// <summary>The keybinding group of the commands.</summary>
    public const string Group = "Airflow";

    private readonly Toolbox _toolbox;
    private AirflowNode? _watched;
    private string? _lastFolder;

    /// <summary>Initializes the commands for <paramref name="toolbox"/>.</summary>
    public AirflowActions(Toolbox toolbox)
    {
        _toolbox = toolbox;
        _toolbox.PropertyChanged += OnToolboxPropertyChanged;
    }

    private AirflowNode? Selected => _toolbox.SelectedNode as AirflowNode;

    private bool CanExport() => Selected?.Result != null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    [property: Command("ExportAirflow", Group, Label = "Export _airflow…", Icon = MaterialIcons.Output,
        Description = "Write the simulated airflow for a game: the wind, turbulence, lift and clouds on a grid, the thermals and their triggers (.airflow)")]
    private async Task ExportAsync()
    {
        if (Selected is not { Result: { } field } node) return;
        string? path = await PickAsync("Export airflow", "Airflow (*.airflow)|*.airflow", node.Name, ".airflow");
        if (path is null) return;
        try
        {
            byte[] bytes = await Task.Run(field.ToBytes);
            await File.WriteAllBytesAsync(path, bytes);
            await _toolbox.Dialogs.ShowMessageAsync("Export finished", string.Create(CultureInfo.CurrentCulture,
                $"{Path.GetFileName(path)} ({bytes.Length / 1e6:0.0} MB): {field.Nx} × {field.Nz} × {field.Ny} cells, {field.Thermals.Count} thermals, {field.Triggers.Count} triggers.\n\nin {Path.GetDirectoryName(path)}\n\ndocs/AirflowFormat.md describes the file; ParagliderToolbox.Airflow reads it (AirflowField.Load)."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't export", e.Message);
        }
    }

    private async Task<string?> PickAsync(string title, string filter, string name, string extension)
    {
        string? folder = _lastFolder ?? (_toolbox.Document.FilePath is { } project ? Path.GetDirectoryName(project) : null);
        string? path = await _toolbox.Dialogs.PickSaveFileAsync(title, filter, FileName(name) + extension, folder);
        if (path != null) _lastFolder = Path.GetDirectoryName(path);
        return path;
    }

    private static string FileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(text.Select(c => invalid.Contains(c) || c == ' ' || c == ':' || c == ',' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "airflow" : name;
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
        if (e.PropertyName == nameof(AirflowNode.Result)) ExportCommand.NotifyCanExecuteChanged();
    }
}
