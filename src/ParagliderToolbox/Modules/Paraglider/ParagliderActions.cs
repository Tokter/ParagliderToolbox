using System.ComponentModel;
using System.Text;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Export;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The commands on the selected paraglider that work anywhere in the window (the <see cref="Group"/> group): exporting
/// the model, the proxy and the line plan, and importing a custom airfoil.
/// </summary>
/// <remarks>
/// Exports generate the model at full quality (the design's texture size) in the background, then write the files.
/// </remarks>
public sealed partial class ParagliderActions : ObservableObject
{
    /// <summary>The keybinding group of the commands.</summary>
    public const string Group = "Paraglider";

    private const string AirfoilFilter = "Airfoil coordinates (*.dat;*.txt)|*.dat;*.txt|All files (*.*)|*.*";
    private readonly Toolbox _toolbox;
    private string? _lastFolder;

    /// <summary>Initializes the commands for <paramref name="toolbox"/>.</summary>
    public ParagliderActions(Toolbox toolbox)
    {
        _toolbox = toolbox;
        _toolbox.PropertyChanged += OnToolboxPropertyChanged;
    }

    private ParagliderNode? Selected => _toolbox.SelectedNode as ParagliderNode;

    private bool HasParaglider() => Selected != null && !IsExporting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportAllCommand), nameof(ExportGlbCommand), nameof(ExportProxyJsonCommand), nameof(ExportObjCommand), nameof(ExportLinePlanCommand))]
    private bool _isExporting;

    [RelayCommand(CanExecute = nameof(HasParaglider))]
    [property: Command("ExportAll", Group, Label = "_Export paraglider…", Icon = MaterialIcons.Output,
        Description = "Write the glTF model (skinned to the proxy), the proxy JSON, the OBJ mesh with textures and the line plan into a folder",
        DefaultKeybinding = "Ctrl+E")]
    private async Task ExportAllAsync()
    {
        if (Selected is not { } node) return;
        string? folder = await _toolbox.Dialogs.PickFolderAsync("Export paraglider into", _lastFolder ?? ProjectFolder());
        if (folder is null) return;
        _lastFolder = folder;
        await RunExportAsync(node, model => GliderExporter.ExportAll(model, folder, BaseName(node)));
    }

    [RelayCommand(CanExecute = nameof(HasParaglider))]
    [property: Command("ExportGlb", Group, Label = "Export _glTF…", Icon = MaterialIcons.ViewInAr,
        Description = "Write the high resolution model with its proxy skeleton as glTF binary (.glb), for Blender and Godot")]
    private Task ExportGlbAsync() => ExportFileAsync("Export glTF", "glTF binary (*.glb)|*.glb", ".glb",
        (model, path) => File.WriteAllBytes(path, GliderExporter.ToGlb(model)));

    [RelayCommand(CanExecute = nameof(HasParaglider))]
    [property: Command("ExportProxyJson", Group, Label = "Export proxy _JSON…", Icon = MaterialIcons.DataObject,
        Description = "Write the physics proxy (nodes, constraints, aerodynamic strips, controls, polar) as JSON for a game")]
    private Task ExportProxyJsonAsync() => ExportFileAsync("Export proxy JSON", "JSON (*.json)|*.json", "_proxy.json",
        (model, path) => File.WriteAllText(path, model.Proxy.Model.ToJson(), Encoding.UTF8), textures: false);

    [RelayCommand(CanExecute = nameof(HasParaglider))]
    [property: Command("ExportObj", Group, Label = "Export _OBJ…", Icon = MaterialIcons.Description,
        Description = "Write the static mesh as Wavefront OBJ with MTL and PNG textures")]
    private Task ExportObjAsync() => ExportFileAsync("Export OBJ", "Wavefront OBJ (*.obj)|*.obj", ".obj",
        (model, path) => GliderExporter.WriteObj(model, Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)));

    [RelayCommand(CanExecute = nameof(HasParaglider))]
    [property: Command("ExportLinePlan", Group, Label = "Export _line plan…", Icon = MaterialIcons.TableChart,
        Description = "Write every line with its level, row, diameter and length as CSV")]
    private Task ExportLinePlanAsync() => ExportFileAsync("Export line plan", "CSV (*.csv)|*.csv", "_lineplan.csv",
        (model, path) => File.WriteAllText(path, GliderExporter.ToLinePlanCsv(model.Rigging), Encoding.UTF8), textures: false);

    [RelayCommand(CanExecute = nameof(HasParaglider))]
    [property: Command("ImportAirfoil", Group, Label = "_Import airfoil…", Icon = MaterialIcons.FileOpen,
        Description = "Use airfoil coordinates from a Selig or Lednicer .dat file for the ribs")]
    private async Task ImportAirfoilAsync()
    {
        if (Selected is not { } node) return;
        string? path = await _toolbox.Dialogs.PickOpenFileAsync("Import airfoil", AirfoilFilter, _lastFolder ?? ProjectFolder());
        if (path is null) return;
        try
        {
            string text = await File.ReadAllTextAsync(path);
            var airfoil = ParagliderToolbox.Paraglider.Geometry.Airfoil.FromDat(text); // validates
            node.CustomAirfoil = text;
            node.AirfoilSource = ParagliderToolbox.Paraglider.Design.AirfoilSource.Custom;
            await _toolbox.Dialogs.ShowMessageAsync("Airfoil imported", $"{airfoil.Name} is now the profile of '{node.Name}'. Its thickness is scaled to the root and tip thickness.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't import the airfoil", $"{path}\n\n{e.Message}");
        }
    }

    /// <summary>Stores <paramref name="recording"/> as a polar under <paramref name="node"/> and selects it.</summary>
    public PolarNode AddPolar(ParagliderNode node, ParagliderToolbox.Paraglider.Polar.PolarRecording recording)
    {
        var polar = new PolarNode
        {
            Name = $"Polar {recording.Recorded:yyyy-MM-dd HH:mm}{(recording.IsComplete ? "" : " (canceled)")}",
            Recording = recording,
        };
        node.Children.Add(polar);
        _toolbox.SelectedNode = polar;
        return polar;
    }

    /// <summary>Writes a polar's steady points and samples as two CSV files.</summary>
    public async Task ExportPolarCsvAsync(PolarNode polar)
    {
        string? path = await _toolbox.Dialogs.PickSaveFileAsync("Export polar", "CSV (*.csv)|*.csv",
            BaseName(polar.Name) + "_points.csv", _lastFolder ?? ProjectFolder());
        if (path is null) return;
        _lastFolder = Path.GetDirectoryName(path);
        try
        {
            string samples = Path.Combine(Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path).Replace("_points", "") + "_samples.csv");
            await File.WriteAllTextAsync(path, ParagliderToolbox.Paraglider.Polar.PolarCsv.Points(polar.Recording), Encoding.UTF8);
            await File.WriteAllTextAsync(samples, ParagliderToolbox.Paraglider.Polar.PolarCsv.Samples(polar.Recording), Encoding.UTF8);
            await _toolbox.Dialogs.ShowMessageAsync("Export finished", $"{Path.GetFileName(path)}\n{Path.GetFileName(samples)}\n\nin {Path.GetDirectoryName(path)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't export", e.Message);
        }
    }

    /// <summary>Exports <paramref name="node"/> with a recorded flight baked into the proxy joints.</summary>
    public async Task ExportAnimationAsync(ParagliderNode node, GliderModel previewModel, ProxyAnimation animation)
    {
        string? path = await _toolbox.Dialogs.PickSaveFileAsync("Export animation", "glTF binary (*.glb)|*.glb",
            BaseName(node) + "_flight.glb", _lastFolder ?? ProjectFolder());
        if (path is null) return;
        _lastFolder = Path.GetDirectoryName(path);
        int nodes = previewModel.Proxy.Model.Nodes.Count;
        await RunExportAsync(node, model =>
        {
            // The animation drives the joints by node index: the full quality model must have the same proxy.
            if (model.Proxy.Model.Nodes.Count != nodes) throw new InvalidOperationException("The design changed since the recording; record again.");
            File.WriteAllBytes(path, GliderExporter.ToGlb(model, new GlbOptions { Animation = animation }));
            return [path];
        });
    }

    private async Task ExportFileAsync(string title, string filter, string suffix, Action<GliderModel, string> write, bool textures = true)
    {
        if (Selected is not { } node) return;
        string? path = await _toolbox.Dialogs.PickSaveFileAsync(title, filter, BaseName(node) + suffix, _lastFolder ?? ProjectFolder());
        if (path is null) return;
        _lastFolder = Path.GetDirectoryName(path);
        await RunExportAsync(node, model =>
        {
            write(model, path);
            return [path];
        }, textures);
    }

    private async Task RunExportAsync(ParagliderNode node, Func<GliderModel, List<string>> write, bool textures = true)
    {
        IsExporting = true;
        try
        {
            var design = node.Snapshot();
            var files = await Task.Run(() => write(GliderGenerator.Generate(design, new GenerateOptions(textures ? 0 : -1))));
            await _toolbox.Dialogs.ShowMessageAsync("Export finished",
                string.Join("\n", files.Select(f => $"{Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024.0:#,0} KB)")) + $"\n\nin {Path.GetDirectoryName(files[0])}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            await _toolbox.Dialogs.ShowErrorAsync("Couldn't export", e.Message);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private string? ProjectFolder() => _toolbox.Document.FilePath is { } path ? Path.GetDirectoryName(path) : null;

    private static string BaseName(ParagliderNode node) => BaseName(node.Name);

    private static string BaseName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(text.Select(c => invalid.Contains(c) || c == ' ' || c == ':' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "paraglider" : name;
    }

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Toolbox.SelectedNode)) return;
        ExportAllCommand.NotifyCanExecuteChanged();
        ExportGlbCommand.NotifyCanExecuteChanged();
        ExportProxyJsonCommand.NotifyCanExecuteChanged();
        ExportObjCommand.NotifyCanExecuteChanged();
        ExportLinePlanCommand.NotifyCanExecuteChanged();
        ImportAirfoilCommand.NotifyCanExecuteChanged();
    }
}
