using System.Runtime.CompilerServices;

// The keybinding registry is static and shared by all tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ParagliderToolbox.Tests;

internal static class AssemblySetup
{
    // Keep the tests' recent files and settings out of the user's real settings folder.
    [ModuleInitializer]
    internal static void UseTemporarySettingsFolder() =>
        Environment.SetEnvironmentVariable("PARAGLIDERTOOLBOX_SETTINGS", Path.Combine(Path.GetTempPath(), "ParagliderToolbox.Tests"));
}
