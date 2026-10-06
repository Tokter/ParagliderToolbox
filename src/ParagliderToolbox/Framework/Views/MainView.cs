using System.ComponentModel;
using Atelier.Controls;
using Atelier.Core.Keybinding;
using Atelier.Core.Platform;
using Atelier.Core.Primitives;
using Atelier.Core.Threading;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Commands;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Framework.Serialization;
using ParagliderToolbox.Framework.Settings;

namespace ParagliderToolbox.Framework.Views;

/// <summary>
/// The main window's content: the title bar with the main menu, and the workspaces whose areas show the registered
/// views. It provides the dialogs the commands use (<see cref="IShellDialogs"/>).
/// </summary>
/// <remarks>
/// The view is the window's <see cref="KeybindingHandler"/>: it runs the <see cref="ShellViewModel.Group"/> commands
/// and, through a scope, the <see cref="ProjectCommands.Group"/> commands wherever the focus is.
/// </remarks>
public sealed class MainView : KeybindingHandler, IShellDialogs
{
    private readonly ShellViewModel _shell;
    private readonly Toolbox _toolbox;
    private readonly WorkspaceView _workspaces;
    private readonly TitleBar _titleBar;
    private bool _layoutSavePending;
    private IHostWindow? _window;

    /// <summary>Initializes the main view for <paramref name="shell"/>.</summary>
    public MainView(ShellViewModel shell) : base(ShellViewModel.Group)
    {
        _shell = shell;
        _toolbox = shell.Toolbox;
        _toolbox.Dialogs = this;
        DataContext = shell;
        AdditionalScopes.Add(new KeybindingScope(ProjectCommands.Group, _toolbox.ProjectCommands));
        foreach (var (group, target) in _toolbox.GlobalCommands) AdditionalScopes.Add(new KeybindingScope(group, target));

        _workspaces = new WorkspaceView(_toolbox.Views).Templates(_toolbox.WorkspaceTemplates.ToArray());
        LoadLayout();
        _workspaces.LayoutChanged += (_, _) => ScheduleLayoutSave();
        _workspaces.SelectionChanged += (_, _) => ScheduleLayoutSave();
        _workspaces.Workspaces.CollectionChanged += (_, _) => ScheduleLayoutSave();

        _titleBar = new TitleBar()
            .Bind(TitleBar.TitleProperty, shell, s => s.Title)
            .Icon(new Icon().Kind(MaterialIconKind.Paragliding).Size(22))
            .Menu(MainMenu());

        Content = new DialogHost()
            .Identifier("RootHost")
            .Content(new Grid()
                .Rows(GridLength.Auto, GridLength.Star)
                .Children(_titleBar, _workspaces.Row(1)));
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _shell.PropertyChanged += OnShellPropertyChanged;
        _window = Host;
        if (_window != null) _window.Closing += OnWindowClosing;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree()
    {
        _shell.PropertyChanged -= OnShellPropertyChanged;
        if (_window != null) _window.Closing -= OnWindowClosing;
        _window = null;
        base.OnDetachedFromVisualTree();
    }

    // Every way of closing the window (title bar, Alt+F4, taskbar) asks about unsaved changes first.
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveLayout();
        if (_toolbox.Document.IsModified) e.Defer(_shell.ConfirmCloseAsync());
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The File menu lists the recent files.
        if (e.PropertyName == nameof(ShellViewModel.RecentFiles)) _titleBar.Menu = MainMenu();
    }

    #region Main menu

    private Menu MainMenu()
    {
        var project = _toolbox.ProjectCommands;
        var menus = _toolbox.Menus;

        var file = new MenuItem(MenuRegistry.File).Items(
            new MenuItem().Command(_shell.NewProjectCommand),
            new MenuItem().Command(_shell.OpenProjectCommand),
            RecentFilesMenu(),
            new Separator(),
            new MenuItem().Command(_shell.SaveProjectCommand),
            new MenuItem().Command(_shell.SaveProjectAsCommand));
        AddModuleItems(file, MenuRegistry.File);
        file.Items.Add(new Separator());
        file.Items.Add(new MenuItem().Command(_shell.ExitCommand).InputGestureText("Alt+F4"));

        var add = new MenuItem("_Add").Icon(MaterialIconKind.Add);
        foreach (var item in ProjectExplorerView.CreateAddMenuItems(_toolbox, _toolbox.Document.Project)) add.Items.Add(item);
        var edit = new MenuItem(MenuRegistry.Edit).Items(
            add,
            new Separator(),
            new MenuItem().Command(project.RenameCommand),
            new MenuItem().Command(project.DuplicateCommand),
            new MenuItem().Command(project.DeleteCommand),
            new Separator(),
            new MenuItem().Command(project.MoveUpCommand),
            new MenuItem().Command(project.MoveDownCommand));
        // The project commands are properties of ProjectCommands: menu items find their labels, icons and shortcuts through
        // the DataContext.
        edit.DataContext = project;
        AddModuleItems(edit, MenuRegistry.Edit);

        var view = new MenuItem(MenuRegistry.View).Items(
            new MenuItem().Command(_shell.ShowCommandPaletteCommand),
            new MenuItem().Command(_shell.ShowKeybindingsCommand),
            new Separator(),
            new MenuItem().Command(_shell.ToggleThemeCommand),
            new MenuItem().Command(_shell.ResetLayoutCommand)
#if DEBUG
            , new MenuItem().Command(Atelier.DevTools.DevToolsManager.ToggleCommand)
#endif
            );
        AddModuleItems(view, MenuRegistry.View);

        var items = new List<object> { file, edit, view };
        string[] builtIn = [MenuRegistry.File, MenuRegistry.Edit, MenuRegistry.View, MenuRegistry.Help];
        foreach (string name in menus.Menus.Where(m => !builtIn.Contains(m)).OrderBy(m => m == MenuRegistry.Tools ? 0 : 1))
        {
            var menu = new MenuItem(name);
            AddModuleItems(menu, name);
            items.Add(menu);
        }

        var help = new MenuItem(MenuRegistry.Help).Items(
            new MenuItem("_About Paraglider Toolbox").Icon(MaterialIconKind.Info).OnClick(ShowAbout));
        AddModuleItems(help, MenuRegistry.Help);
        items.Add(help);

        return new Menu().Items(items.ToArray());
    }

    private void AddModuleItems(MenuItem menu, string name)
    {
        var moduleItems = _toolbox.Menus.CreateItems(name).ToList();
        if (moduleItems.Count == 0) return;
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        foreach (var item in moduleItems) menu.Items.Add(item);
    }

    private MenuItem RecentFilesMenu()
    {
        var recent = new MenuItem("Open _recent").Icon(MaterialIconKind.History);
        if (_shell.RecentFiles.Count == 0)
        {
            recent.Items.Add(new MenuItem("No recent projects").IsEnabled(false));
            return recent;
        }
        int number = 1;
        foreach (string path in _shell.RecentFiles)
        {
            string header = number <= 9 ? $"_{number} {Path.GetFileName(path)}" : Path.GetFileName(path);
            recent.Items.Add(new MenuItem(header).ToolTip(path).OnClick(() => _ = _shell.OpenRecentAsync(path)));
            number++;
        }
        return recent;
    }

    private void ShowAbout()
    {
        string version = typeof(MainView).Assembly.GetName().Version?.ToString(3) ?? "?";
        string modules = string.Join(", ", _toolbox.Modules.Select(m => m.Name));
        _ = Dialog.Information("About Paraglider Toolbox",
            $"Version {version}\n\nTools for paraglider simulation and games.\n\nModules: {modules}", this);
    }

    #endregion

    #region Layout

    private void LoadLayout()
    {
        var saved = AppSettings.LoadWorkspaces();
        if (saved != null && saved.Workspaces.Count > 0)
        {
            try
            {
                _workspaces.Load(saved);
                return;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[Layout] The saved layout couldn't be restored: {e.Message}");
            }
        }
        LoadDefaultLayout();
    }

    private void LoadDefaultLayout()
    {
        if (_toolbox.DefaultWorkspaces.Count > 0) _workspaces.Load(new WorkspacesDefinition(_toolbox.DefaultWorkspaces.ToList()));
    }

    // Layout changes come in bursts (dragging a border): save once they settle into a frame.
    private void ScheduleLayoutSave()
    {
        if (_layoutSavePending) return;
        _layoutSavePending = true;
        Dispatcher.Post(() =>
        {
            _layoutSavePending = false;
            SaveLayout();
        });
    }

    private void SaveLayout()
    {
        if (_workspaces.Workspaces.Count > 0) AppSettings.SaveWorkspaces(_workspaces.ToDefinition());
    }

    #endregion

    #region IShellDialogs

    /// <inheritdoc/>
    public async Task<SaveChangesChoice> AskSaveChangesAsync(string documentName)
    {
        var response = await new Dialog("Unsaved changes", $"Do you want to save the changes to '{documentName}'?")
            .AddButton("Cancel", DialogResult.Cancel, isCancel: true)
            .AddButton("Don't save", DialogResult.No)
            .AddButton("Save", DialogResult.Yes, isDefault: true, variant: ButtonVariant.Filled)
            .ShowAsync(this);
        return response.Result switch
        {
            DialogResult.Yes => SaveChangesChoice.Save,
            DialogResult.No => SaveChangesChoice.Discard,
            _ => SaveChangesChoice.Cancel,
        };
    }

    /// <inheritdoc/>
    public async Task<string?> PickFileToOpenAsync(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            Filter = ProjectSerializer.FileFilter,
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return await dialog.ShowAsync(this) ? dialog.FileName : null;
    }

    /// <inheritdoc/>
    public async Task<string?> PickFileToSaveAsync(string suggestedName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save project",
            Filter = ProjectSerializer.FileFilter,
            FileName = suggestedName,
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return await dialog.ShowAsync(this) ? dialog.FileName : null;
    }

    /// <inheritdoc/>
    public async Task<string?> PickOpenFileAsync(string title, string filter, string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return await dialog.ShowAsync(this) ? dialog.FileName : null;
    }

    /// <inheritdoc/>
    public async Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return await dialog.ShowAsync(this) ? dialog.FileName : null;
    }

    /// <inheritdoc/>
    public async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var dialog = new FolderBrowserDialog
        {
            Title = title,
            InitialDirectory = initialDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return await dialog.ShowAsync(this) ? dialog.FolderName : null;
    }

    /// <inheritdoc/>
    public Task ShowMessageAsync(string title, string message) => Dialog.Information(title, message, this);

    /// <inheritdoc/>
    public Task ShowErrorAsync(string title, string message) => Dialog.Information(title, message, this);

    /// <inheritdoc/>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var response = await new Dialog(title, message)
            .AddButton("Cancel", DialogResult.Cancel, isCancel: true)
            .AddButton(confirmText, DialogResult.Ok, isDefault: true, variant: ButtonVariant.Filled)
            .ShowAsync(this);
        return response.Result == DialogResult.Ok;
    }

    /// <inheritdoc/>
    public async Task<string?> AskTextAsync(string title, string label, string text)
    {
        var box = new TextBox().Label(label).Text(text).Width(360);
        var dialog = new Dialog(title)
            .Content(box)
            .AddButton("Cancel", DialogResult.Cancel, isCancel: true)
            .AddButton("OK", DialogResult.Ok, isDefault: true, variant: ButtonVariant.Filled);
        var showing = dialog.ShowAsync(this);
        Dispatcher.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
        var response = await showing;
        return response.Result == DialogResult.Ok ? box.Text : null;
    }

    /// <inheritdoc/>
    public void ShowCommandPalette() =>
        CommandPalette.Show(this, c => !(c.Descriptor.Group == ShellViewModel.Group && c.Descriptor.Name == "CommandPalette"));

    /// <inheritdoc/>
    public void ShowKeybindingEditor() =>
        _ = new Dialog("Keyboard shortcuts")
            .Content(new KeybindingEditor().Width(1080).Height(580))
            .AddButton("Close", DialogResult.Ok, isDefault: true, isCancel: true)
            .MaxWidth(1160)
            .ShowAsync(this);

    /// <inheritdoc/>
    public void ResetLayout() => LoadDefaultLayout();

    /// <inheritdoc/>
    /// <remarks>The commands asked about unsaved changes already, so the window closes without asking again.</remarks>
    public void CloseWindow()
    {
        SaveLayout();
        Host?.Close(force: true);
    }

    #endregion
}
