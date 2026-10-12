using System.Runtime.CompilerServices;
using Atelier.Controls;
using Atelier.Markup;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Airflow.Views;
using ParagliderToolbox.Modules.Terrain;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>
/// The Airflow Simulator: airflow simulations over a terrain (nodes under it) for a sounding, a wind and a time of day,
/// run by the fluid solver of ParagliderToolbox.Airflow, shown in 3D (streamlines, particles, slices, thermals, clouds,
/// the ground's heating) and exported for a game.
/// </summary>
/// <param name="terrains">The terrain module, which builds the terrains the airflows run over.</param>
public sealed class AirflowModule(TerrainModule terrains) : IToolboxModule
{
    // One view model per airflow, kept while the airflow lives, so switching the selection back finds it as it was.
    private readonly ConditionalWeakTable<AirflowNode, AirflowPreview> _previews = new();

    /// <inheritdoc/>
    public string Name => "Airflow Simulator";

    /// <inheritdoc/>
    public void Register(Toolbox toolbox)
    {
        TerrainNode.ChildTypes.Add(typeof(AirflowNode));
        ContainerNode.PlacedTypes.Add(typeof(AirflowNode));
        toolbox.NodeTypes.Register<AirflowNode>("airflow", "Airflow", MaterialIconKind.Air,
            category: "Terrains", description: "Add an airflow simulation over the terrain: the wind, thermals and turbulence of a sounding and a time of day",
            defaultKeybinding: "Ctrl+Shift+A", createInteractively: () => AskNewAirflowAsync(toolbox));
        // Thermals simulated one by one in boxes of their own, under an airflow, are no more.
        toolbox.NodeTypes.Retired.Add("thermal");

        var actions = new AirflowActions(toolbox);
        toolbox.GlobalCommands.Add((AirflowActions.Group, actions));
        toolbox.DetailViews.Register<AirflowNode>(node =>
            new AirflowDetailView(_previews.GetValue(node, n => new AirflowPreview(n, terrains)), actions));
        toolbox.PropertyEditors.Add(registry => registry.Register<SoundingChoice>(SoundingChoiceEditor.Create));
        toolbox.JsonConverters.Add(new AirflowFieldJsonConverter());
        toolbox.PropertyCategoryOrder.AddRange(AirflowNode.CategoryOrder);

        toolbox.Menus.Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportCommand));
    }

    /// <summary>Asks for the sounding, wind, time and detail of a new airflow over the selected terrain; returns it, or <c>null</c> when canceled.</summary>
    public static async Task<AirflowNode?> AskNewAirflowAsync(Toolbox toolbox)
    {
        TerrainNode? terrain = null;
        for (ProjectNode? node = toolbox.SelectedNode; node != null && terrain is null; node = node.Parent) terrain = node as TerrainNode;
        var options = new NewAirflowOptions(terrain);
        while (true)
        {
            bool create = await toolbox.Dialogs.ShowDialogAsync("New airflow", new NewAirflowView(options), "Create", maxWidth: 900);
            if (!create) return null;
            if (options.Problem is not { } problem) return options.CreateNode();
            await toolbox.Dialogs.ShowErrorAsync("Can't make the airflow", problem);
        }
    }
}
