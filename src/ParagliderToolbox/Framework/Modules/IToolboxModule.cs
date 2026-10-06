namespace ParagliderToolbox.Framework.Modules;

/// <summary>
/// A feature of the toolbox: registers its node types, views, detail views, property editors, menu items and JSON
/// converters with the <see cref="Toolbox"/> at startup.
/// </summary>
/// <example>
/// <code>
/// public sealed class WingModule : IToolboxModule
/// {
///     public string Name => "Wings";
///
///     public void Register(Toolbox toolbox)
///     {
///         toolbox.NodeTypes.Register&lt;WingNode&gt;("wing", "Wing", MaterialIconKind.Paragliding);
///         toolbox.DetailViews.Register&lt;WingNode&gt;(wing =&gt; new WingDetailView(wing));
///         toolbox.Views.Register("wing-3d", "Wing 3D", MaterialIconKind.ViewInAr, _ =&gt; new Wing3DView(toolbox));
///     }
/// }
/// </code>
/// Add the module to the list in <c>Program.Modules</c>.
/// </example>
public interface IToolboxModule
{
    /// <summary>Gets the module's name, for messages.</summary>
    string Name { get; }

    /// <summary>Registers the module's parts. Called once, before the window opens and before any project is loaded.</summary>
    void Register(Toolbox toolbox);
}
