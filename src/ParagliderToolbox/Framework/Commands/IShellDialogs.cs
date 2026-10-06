namespace ParagliderToolbox.Framework.Commands;

/// <summary>What to do with unsaved changes before the project is closed.</summary>
public enum SaveChangesChoice
{
    /// <summary>Save the project first.</summary>
    Save,
    /// <summary>Close it without saving.</summary>
    Discard,
    /// <summary>Keep it open.</summary>
    Cancel,
}

/// <summary>
/// The dialogs and window actions the commands need, provided by the main window so the command view models stay free
/// of UI code (and testable).
/// </summary>
public interface IShellDialogs
{
    /// <summary>Asks whether to save the changes to <paramref name="documentName"/>.</summary>
    Task<SaveChangesChoice> AskSaveChangesAsync(string documentName);

    /// <summary>Asks for a project file to open; returns its path, or <c>null</c> when canceled.</summary>
    Task<string?> PickFileToOpenAsync(string? initialDirectory);

    /// <summary>Asks where to save the project; returns the path, or <c>null</c> when canceled.</summary>
    Task<string?> PickFileToSaveAsync(string suggestedName, string? initialDirectory);

    /// <summary>Asks for a file to open; returns its path, or <c>null</c> when canceled.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="filter">The file types, e.g. <c>"Airfoil (*.dat)|*.dat|All files (*.*)|*.*"</c>.</param>
    /// <param name="initialDirectory">The folder to start in, or <c>null</c>.</param>
    Task<string?> PickOpenFileAsync(string title, string filter, string? initialDirectory);

    /// <summary>Asks where to save a file; returns the path, or <c>null</c> when canceled.</summary>
    Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName, string? initialDirectory);

    /// <summary>Asks for a folder; returns its path, or <c>null</c> when canceled.</summary>
    Task<string?> PickFolderAsync(string title, string? initialDirectory);

    /// <summary>Shows a message.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows an error message.</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>Asks to confirm an action; returns whether <paramref name="confirmText"/> was chosen.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Asks for a line of text; returns it, or <c>null</c> when canceled.</summary>
    Task<string?> AskTextAsync(string title, string label, string text);

    /// <summary>Opens the command palette.</summary>
    void ShowCommandPalette();

    /// <summary>Opens the keybinding editor.</summary>
    void ShowKeybindingEditor();

    /// <summary>Puts the workspaces back to the defaults.</summary>
    void ResetLayout();

    /// <summary>Closes the window (and with it the application) without asking.</summary>
    void CloseWindow();
}
