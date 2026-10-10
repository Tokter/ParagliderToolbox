using System.Runtime.CompilerServices;
using Atelier.Controls;
using Atelier.Markup;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Terrain.Views;
using ParagliderToolbox.Terrain.Data;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>
/// The Terrain Generator: terrains of real places for paragliding games, built from the best elevation and imagery
/// data there is at each point (swisstopo's in Switzerland, Copernicus and Sentinel-2 worldwide), split into tiles and
/// levels of detail, previewed in 3D and exported as a tile set or one glTF file.
/// </summary>
public sealed class TerrainModule : IToolboxModule
{
    // One preview per terrain, kept while the terrain lives, so switching the selection back finds it built.
    private readonly ConditionalWeakTable<TerrainNode, TerrainPreview> _previews = new();
    private TerrainActions? _actions;

    /// <summary>Gets the folder downloaded terrain data is kept in; <c>PARAGLIDERTOOLBOX_TERRAIN_CACHE</c> overrides it.</summary>
    public static string CacheFolder { get; } =
        Environment.GetEnvironmentVariable("PARAGLIDERTOOLBOX_TERRAIN_CACHE") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParagliderToolbox", "TerrainCache");

    /// <summary>Gets the sources terrains are built from (add a dataset here; see <see cref="TerrainSources"/>).</summary>
    public TerrainSources Sources { get; } = TerrainSources.CreateDefault(new DataCache(CacheFolder));

    /// <inheritdoc/>
    public string Name => "Terrain Generator";

    /// <inheritdoc/>
    public void Register(Toolbox toolbox)
    {
        toolbox.NodeTypes.Register<TerrainNode>("terrain", "Terrain", MaterialIconKind.Terrain,
            category: "Terrains", description: "Add the terrain of a real place: elevation and aerial images in tiles and levels of detail, for a game",
            defaultKeybinding: "Ctrl+Shift+T", createInteractively: () => AskNewTerrainAsync(toolbox));

        TerrainFlightCommands.Register();
        var actions = _actions = new TerrainActions(toolbox, Sources, node => _previews.TryGetValue(node, out var preview) ? preview.Model : null);
        toolbox.GlobalCommands.Add((TerrainActions.Group, actions));
        toolbox.DetailViews.Register<TerrainNode>(node =>
            new TerrainDetailView(_previews.GetValue(node, n => new TerrainPreview(n, () => _actions!)), _actions!));

        toolbox.PropertyEditors.Add(registry => registry.Register<SourceChoice>(context => SourceChoiceEditor.Create(context, Sources)));
        toolbox.PropertyCategoryOrder.AddRange(TerrainNode.CategoryOrder);
        toolbox.PropertyCategoryOrder.AddRange(["Files", "Detail"]);

        toolbox.Menus
            .Add(MenuRegistry.Tools, () => new Separator())
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportTilesCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportGlbCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ClearCacheCommand));
    }

    /// <summary>Asks for the location, style and name of a new terrain; returns it, or <c>null</c> when canceled.</summary>
    public static async Task<TerrainNode?> AskNewTerrainAsync(Toolbox toolbox)
    {
        var options = new NewTerrainOptions();
        while (true)
        {
            bool create = await toolbox.Dialogs.ShowDialogAsync("New terrain", new NewTerrainView(options), "Create", maxWidth: 780);
            if (!create) return null;
            if (options.CreateNode() is { } node) return node;
            await toolbox.Dialogs.ShowErrorAsync("Not a location", $"'{options.Location}' isn't a position. {options.LocationInfo}.");
        }
    }
}
