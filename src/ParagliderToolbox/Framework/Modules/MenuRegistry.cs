namespace ParagliderToolbox.Framework.Modules;

/// <summary>
/// Items modules add to the main menu in the title bar.
/// </summary>
/// <remarks>
/// The standard menus are <see cref="File"/>, <see cref="Edit"/>, <see cref="View"/>, <see cref="Tools"/> and
/// <see cref="Help"/>; items for another name get a menu of their own before Help. Menus without items (other than the
/// built-in ones) are not shown.
/// </remarks>
public sealed class MenuRegistry
{
    /// <summary>The File menu.</summary>
    public const string File = "_File";
    /// <summary>The Edit menu.</summary>
    public const string Edit = "_Edit";
    /// <summary>The View menu.</summary>
    public const string View = "_View";
    /// <summary>The Tools menu.</summary>
    public const string Tools = "_Tools";
    /// <summary>The Help menu.</summary>
    public const string Help = "_Help";

    private readonly List<(string Menu, Func<object> CreateItem)> _items = [];

    /// <summary>
    /// Adds an item to <paramref name="menu"/>. <paramref name="createItem"/> returns a <c>MenuItem</c> or a
    /// <c>Separator</c>, e.g. <c>() =&gt; new MenuItem().Command(vm.ExportCommand)</c>; it runs whenever the menu is built.
    /// </summary>
    /// <returns>This registry, for chaining.</returns>
    public MenuRegistry Add(string menu, Func<object> createItem)
    {
        _items.Add((menu, createItem ?? throw new ArgumentNullException(nameof(createItem))));
        return this;
    }

    /// <summary>Gets the names of the menus with items, in the order they were first used.</summary>
    public IEnumerable<string> Menus => _items.Select(i => i.Menu).Distinct();

    /// <summary>Creates the items modules added to <paramref name="menu"/>.</summary>
    public IEnumerable<object> CreateItems(string menu) => _items.Where(i => i.Menu == menu).Select(i => i.CreateItem());
}
