using System.Runtime.CompilerServices;
using Atelier.Controls;
using Atelier.Markup;
using ParagliderToolbox.Framework.Modules;
using ParagliderToolbox.Modules.Paraglider.Views;
using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The Paraglider Model Creator: the paraglider node (every design parameter), its 3D preview and flight simulation in
/// the detail view, the curve editor for its distributions, and the exports (glTF skinned to the physics proxy, proxy
/// JSON, OBJ, line plan).
/// </summary>
public sealed class ParagliderModule : IToolboxModule
{
    // One preview per paraglider, kept while the paraglider lives, so switching the selection or an area's editor back
    // finds the generated model (and a paused simulation) as it was.
    private readonly ConditionalWeakTable<ParagliderNode, ParagliderPreview> _previews = new();
    private ParagliderActions? _actions;

    /// <inheritdoc/>
    public string Name => "Paraglider Model Creator";

    /// <inheritdoc/>
    public void Register(Toolbox toolbox)
    {
        toolbox.NodeTypes.Register<ParagliderNode>("paraglider", "Paraglider", MaterialIconKind.Paragliding,
            category: "Paragliders", description: "Add a paraglider design: generate its model and physics proxy from parameters",
            defaultKeybinding: "Ctrl+Shift+G");

        var actions = _actions = new ParagliderActions(toolbox);
        toolbox.GlobalCommands.Add((ParagliderActions.Group, actions));

        toolbox.DetailViews.Register<ParagliderNode>(node =>
            new ParagliderDetailView(_previews.GetValue(node, n => new ParagliderPreview(n, () => _actions!))));
        toolbox.DetailViews.Register<PolarNode>(polar => new PolarDetailView(polar, actions));

        toolbox.PropertyEditors.Add(registry => registry.Register<Curve>(CurvePropertyEditor.Create));
        toolbox.PropertyCategoryOrder.AddRange(ParagliderNode.CategoryOrder);
        toolbox.PropertyCategoryOrder.AddRange(["Polar", "Recording"]);
        toolbox.NodeTypes.Register<PolarNode>("polar", "Polar", MaterialIconKind.ShowChart, isCreatable: false,
            description: "A recorded polar curve of a paraglider");

        toolbox.Menus
            .Add(MenuRegistry.File, () => new MenuItem { DataContext = actions }.Command(actions.ExportAllCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportAllCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportGlbCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportProxyJsonCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportObjCommand))
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ExportLinePlanCommand))
            .Add(MenuRegistry.Tools, () => new Separator())
            .Add(MenuRegistry.Tools, () => new MenuItem { DataContext = actions }.Command(actions.ImportAirfoilCommand));
    }
}
