using System.Diagnostics;
using System.Text.Json;
using Atelier.Controls;
using Atelier.Core.Keybinding;

namespace ParagliderToolbox.Framework.Settings;

/// <summary>
/// The user's settings, kept in <c>%APPDATA%\ParagliderToolbox</c>: the theme and recent files
/// (<c>settings.json</c>), the customized commands (<c>keybindings.json</c>) and the workspace layout
/// (<c>workspaces.json</c>).
/// </summary>
public sealed class AppSettings
{
    private const int MaxRecentFiles = 10;

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Gets or sets whether the dark theme is used.</summary>
    public bool IsDarkTheme { get; set; } = true;

    /// <summary>Gets the recently opened or saved project files, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Gets the folder the settings are kept in; <c>PARAGLIDERTOOLBOX_SETTINGS</c> overrides it.</summary>
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("PARAGLIDERTOOLBOX_SETTINGS") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ParagliderToolbox");

    private static string SettingsPath => Path.Combine(Folder, "settings.json");
    private static string KeybindingsPath => Path.Combine(Folder, "keybindings.json");
    private static string WorkspacesPath => Path.Combine(Folder, "workspaces.json");

    /// <summary>Puts <paramref name="path"/> at the top of <see cref="RecentFiles"/> and saves the settings.</summary>
    public void AddRecentFile(string path)
    {
        string full = Path.GetFullPath(path);
        RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, full);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        Save();
    }

    /// <summary>Removes <paramref name="path"/> from <see cref="RecentFiles"/> (e.g. when it no longer exists) and saves.</summary>
    public void RemoveRecentFile(string path)
    {
        if (RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }

    /// <summary>Loads the settings, or returns the defaults when there are none or they can't be read.</summary>
    public static AppSettings Load() =>
        TryRead(SettingsPath, json => JsonSerializer.Deserialize<AppSettings>(json, s_options)) ?? new AppSettings();

    /// <summary>Saves the settings.</summary>
    public void Save() => TryWrite(SettingsPath, JsonSerializer.Serialize(this, s_options));

    /// <summary>Applies the saved command customizations and saves them again whenever they change.</summary>
    public static void LoadAndKeepKeybindingsSaved()
    {
        TryRead(KeybindingsPath, json => KeybindingManager.ImportCustomizations(json));
        KeybindingManager.CustomizationsChanged += (_, _) =>
        {
            if (KeybindingManager.Customizations.Count == 0) TryDelete(KeybindingsPath);
            else TryWrite(KeybindingsPath, KeybindingManager.ExportCustomizations());
        };
    }

    /// <summary>Loads the saved workspace layout, or returns <c>null</c>.</summary>
    public static WorkspacesDefinition? LoadWorkspaces() => TryRead(WorkspacesPath, WorkspacesDefinition.FromJson);

    /// <summary>Saves the workspace layout.</summary>
    public static void SaveWorkspaces(WorkspacesDefinition workspaces) => TryWrite(WorkspacesPath, workspaces.ToJson());

    private static T? TryRead<T>(string path, Func<string, T?> read)
    {
        try
        {
            return File.Exists(path) ? read(File.ReadAllText(path)) : default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Settings] Couldn't read {path}: {e.Message}");
            return default;
        }
    }

    private static void TryWrite(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Settings] Couldn't write {path}: {e.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Settings] Couldn't delete {path}: {e.Message}");
        }
    }
}
