using System.Runtime.CompilerServices;
using Atelier.Controls;
using Atelier.Markup;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Features.Views;
using ParagliderToolbox.Modules.Terrain;
using ParagliderToolbox.Terrain.Features;

namespace ParagliderToolbox.Modules.Features;

/// <summary>
/// The Feature Detector: trees, roads and paths, buildings, boulders and areas of forest, rock, water … found in a
/// terrain's aerial images and its terrain and surface models (nodes under the terrain), shown on the terrain in 3D and
/// exported for a game to place its models by.
/// </summary>
/// <param name="terrains">The terrain module, which builds the terrains and holds the sources the features are read from.</param>
public sealed class FeaturesModule(TerrainModule terrains) : IToolboxModule
{
    // One view model per node, kept while the node lives, so switching the selection back finds it as it was.
    private readonly ConditionalWeakTable<FeaturesNode, FeaturesPreview> _previews = new();

    /// <inheritdoc/>
    public string Name => "Feature Detector";

    /// <inheritdoc/>
    public void Register(Toolbox toolbox)
    {
        TerrainNode.ChildTypes.Add(typeof(FeaturesNode));
        ContainerNode.PlacedTypes.Add(typeof(FeaturesNode));
        toolbox.NodeTypes.Register<FeaturesNode>("features", "Features", MaterialIconKind.Forest,
            category: "Terrains", description: "Find the trees, roads and paths, buildings, boulders and areas in the terrain's aerial images, for a game to place its models by",
            defaultKeybinding: "Ctrl+Shift+F");

        var detector = new FeatureDetector(terrains.Sources);
        var actions = new FeaturesActions(toolbox);
        toolbox.GlobalCommands.Add((FeaturesActions.Group, actions));
        toolbox.DetailViews.Register<FeaturesNode>(node =>
            new FeaturesDetailView(_previews.GetValue(node, n => new FeaturesPreview(n, terrains, detector)), actions));
        toolbox.JsonConverters.Add(new FeatureSetJsonConverter());
        toolbox.PropertyCategoryOrder.AddRange(FeaturesNode.CategoryOrder);

        toolbox.Menus.Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportCommand));
    }
}
