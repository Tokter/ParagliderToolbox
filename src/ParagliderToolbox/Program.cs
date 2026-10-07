using Atelier.Platform.Silk;
using ParagliderToolbox.Framework.Commands;
using ParagliderToolbox.Framework.Infrastructure;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Framework.Settings;
using ParagliderToolbox.Framework.Views;
using ParagliderToolbox.Modules.Core;
using ParagliderToolbox.Modules.Paraglider;

namespace ParagliderToolbox;

internal static class Program
{
    /// <summary>The toolbox's features. Add new modules here.</summary>
    private static IEnumerable<IToolboxModule> Modules =>
    [
        new CoreModule(),
        new ParagliderModule(),
    ];

    private static void Main(string[] args)
    {
        // [Command] declarations are discovered at compile time; the user's changes to them are applied on top.
        Atelier.Generated.GeneratedKeybindings.RegisterKeybindings();

        var settings = AppSettings.Load();
        ToolboxTheme.Apply(settings.IsDarkTheme);

        var toolbox = new Toolbox();
        toolbox.Initialize(Modules);
        AppSettings.LoadAndKeepKeybindingsSaved();

        var shell = new ShellViewModel(toolbox, settings);
        var window = new SilkWindow(
            title: ShellViewModel.ApplicationName,
            width: 1440,
            height: 900,
            isTitleLess: true,
            isTransparent: true,
            windowOpacity: 1.0f,
            iconPath: "ParagliderToolbox.png");

        // A factory lambda enables hot reload: the view is rebuilt from the same state.
        window.SetContent(() => new MainView(shell));

        // A project file given on the command line (or dropped on the executable) opens at startup.
        if (args.Length > 0 && File.Exists(args[0]))
        {
            _ = shell.OpenRecentAsync(args[0]);
        }

        window.Show();
        window.Run();
    }
}
