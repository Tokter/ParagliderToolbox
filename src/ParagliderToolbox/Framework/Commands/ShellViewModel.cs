using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParagliderToolbox.Framework.Infrastructure;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Framework.Serialization;
using ParagliderToolbox.Framework.Settings;

namespace ParagliderToolbox.Framework.Commands;

/// <summary>
/// The application commands (the <see cref="Group"/> keybinding group): project files, the command palette, the
/// keybinding editor, the theme and the layout. They work wherever the focus is in the window.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>The keybinding group of the application commands.</summary>
    public const string Group = "Application";

    /// <summary>The application's name, shown in the title bar.</summary>
    public const string ApplicationName = "Paraglider Toolbox";

    private readonly AppSettings _settings;

    /// <summary>Initializes the commands for <paramref name="toolbox"/>.</summary>
    public ShellViewModel(Toolbox toolbox, AppSettings settings)
    {
        Toolbox = toolbox;
        _settings = settings;
        Toolbox.PropertyChanged += OnToolboxPropertyChanged;
        Toolbox.Document.PropertyChanged += OnDocumentPropertyChanged;
    }

    /// <summary>Gets the toolbox.</summary>
    public Toolbox Toolbox { get; }

    private IShellDialogs Dialogs => Toolbox.Dialogs;

    /// <summary>Gets the title bar text: the project's name, a dot while it has unsaved changes, and the application.</summary>
    public string Title => $"{Toolbox.Document.DisplayName}{(Toolbox.Document.IsModified ? " •" : "")} — {ApplicationName}";

    /// <summary>Gets the recently used project files, most recent first.</summary>
    public IReadOnlyList<string> RecentFiles => _settings.RecentFiles;

    #region Project files

    [RelayCommand]
    [property: Command("NewProject", Group, Label = "_New project", Icon = MaterialIcons.NoteAdd,
        Description = "Close the project and start an empty one", DefaultKeybinding = "Ctrl+N")]
    private async Task NewProjectAsync()
    {
        if (!await ConfirmCloseAsync()) return;
        Toolbox.NewProject();
    }

    [RelayCommand]
    [property: Command("OpenProject", Group, Label = "_Open project…", Icon = MaterialIcons.FolderOpen,
        Description = "Open a project file", DefaultKeybinding = "Ctrl+O")]
    private async Task OpenProjectAsync()
    {
        if (!await ConfirmCloseAsync()) return;
        string? path = await Dialogs.PickFileToOpenAsync(InitialDirectory());
        if (path != null) await LoadAsync(path);
    }

    /// <summary>Opens the project file at <paramref name="path"/> (from the recent files), asking about unsaved changes first.</summary>
    public async Task OpenRecentAsync(string path)
    {
        if (!await ConfirmCloseAsync()) return;
        await LoadAsync(path);
    }

    [RelayCommand]
    [property: Command("SaveProject", Group, Label = "_Save", Icon = MaterialIcons.Save,
        Description = "Save the project to its file", DefaultKeybinding = "Ctrl+S")]
    private Task<bool> SaveProjectAsync() =>
        Toolbox.Document.FilePath is { } path ? SaveToAsync(path) : SaveProjectAsAsync();

    [RelayCommand]
    [property: Command("SaveProjectAs", Group, Label = "Save _as…", Icon = MaterialIcons.SaveAs,
        Description = "Save the project to another file", DefaultKeybinding = "Ctrl+Shift+S")]
    private async Task<bool> SaveProjectAsAsync()
    {
        var document = Toolbox.Document;
        string suggested = document.FilePath != null ? Path.GetFileName(document.FilePath) : SafeFileName(document.DisplayName) + ProjectSerializer.FileExtension;
        string? path = await Dialogs.PickFileToSaveAsync(suggested, InitialDirectory());
        return path != null && await SaveToAsync(path);
    }

    [RelayCommand]
    [property: Command("Exit", Group, Label = "E_xit", Icon = MaterialIcons.Logout, Description = "Close Paraglider Toolbox")]
    private async Task ExitAsync()
    {
        if (await ConfirmCloseAsync()) Dialogs.CloseWindow();
    }

    /// <summary>
    /// Asks what to do with unsaved changes, if there are any, and saves when asked to. Returns whether the project may
    /// be closed.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!Toolbox.Document.IsModified) return true;
        return await Dialogs.AskSaveChangesAsync(Toolbox.Document.DisplayName) switch
        {
            SaveChangesChoice.Save => await SaveProjectAsync(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            Toolbox.OpenProject(path);
            _settings.AddRecentFile(path);
            OnPropertyChanged(nameof(RecentFiles));
        }
        catch (Exception e) when (e is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            if (e is FileNotFoundException or DirectoryNotFoundException)
            {
                _settings.RemoveRecentFile(path);
                OnPropertyChanged(nameof(RecentFiles));
            }
            await Dialogs.ShowErrorAsync("Couldn't open the project", $"{path}\n\n{e.Message}");
        }
    }

    private async Task<bool> SaveToAsync(string path)
    {
        try
        {
            Toolbox.SaveProject(path);
            _settings.AddRecentFile(path);
            OnPropertyChanged(nameof(RecentFiles));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            await Dialogs.ShowErrorAsync("Couldn't save the project", $"{path}\n\n{e.Message}");
            return false;
        }
    }

    private string? InitialDirectory()
    {
        string? file = Toolbox.Document.FilePath ?? _settings.RecentFiles.FirstOrDefault();
        return file != null ? Path.GetDirectoryName(file) : null;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "Untitled" : safe.Trim();
    }

    #endregion

    #region Window

    [RelayCommand]
    [property: Command("CommandPalette", Group, Label = "Command _palette…", Icon = MaterialIcons.Search,
        Description = "Search and run the commands that work where the focus is", DefaultKeybinding = "Ctrl+Shift+P")]
    private void ShowCommandPalette() => Dialogs.ShowCommandPalette();

    [RelayCommand]
    [property: Command("Keybindings", Group, Label = "_Keyboard shortcuts…", Icon = MaterialIcons.Keyboard,
        Description = "Change the labels, icons and shortcuts of the commands", DefaultKeybinding = "Ctrl+K, Ctrl+S")]
    private void ShowKeybindings() => Dialogs.ShowKeybindingEditor();

    [RelayCommand]
    [property: Command("ToggleTheme", Group, Label = "Toggle _dark theme", Icon = MaterialIcons.DarkMode,
        Description = "Switch between the dark and the light theme")]
    private void ToggleTheme()
    {
        _settings.IsDarkTheme = !ToolboxTheme.IsDark;
        ToolboxTheme.Apply(_settings.IsDarkTheme);
        _settings.Save();
    }

    [RelayCommand]
    [property: Command("ResetLayout", Group, Label = "_Reset layout", Icon = MaterialIcons.RestartAlt,
        Description = "Put the workspaces and their areas back to the defaults")]
    private void ResetLayout() => Dialogs.ResetLayout();

    #endregion

    private void OnToolboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Modules.Toolbox.Document)) return;
        Toolbox.Document.PropertyChanged += OnDocumentPropertyChanged;
        OnPropertyChanged(nameof(Title));
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == Toolbox.Document) OnPropertyChanged(nameof(Title));
    }
}
