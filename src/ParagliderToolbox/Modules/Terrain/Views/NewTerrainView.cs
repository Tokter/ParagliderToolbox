using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Layout;
using Atelier.Markup;

namespace ParagliderToolbox.Modules.Terrain.Views;

/// <summary>
/// The content of the new-terrain dialog: the name, the location (typed or pasted, or a known site) and a card per style
/// (with its key numbers), bound to <see cref="NewTerrainOptions"/>.
/// </summary>
public sealed class NewTerrainView : ContentControl
{
    private readonly NewTerrainOptions _options;

    /// <summary>Initializes the view of <paramref name="options"/>.</summary>
    public NewTerrainView(NewTerrainOptions options)
    {
        _options = options;
        DataContext = options;

        var sites = new WrapPanel().Spacing(6, 6);
        foreach (var site in NewTerrainOptions.Sites)
        {
            sites.Add(new Button(site.Name).Variant(ButtonVariant.Outlined).Height(28).MinHeight(0).Padding(10, 0)
                .ToolTip(site.Location.ToString())
                .OnClick(() => options.Choose(site)));
        }

        var styles = new UniformGrid().Columns(NewTerrainOptions.Styles.Count).Spacing(8);
        foreach (var style in NewTerrainOptions.Styles) styles.Add(StyleCard(style));

        Content = new StackPanel().Width(700).Spacing(10).Children(
            new TextBox().Label("Name").BindText(options, o => o.Name, (o, v) => o.Name = v),
            new TextBox().Label("Location (center)").BindText(options, o => o.Location, (o, v) => o.Location = v),
            new TextBlock().BodySmall().TextWrapping().BindText(options, o => o.LocationInfo),
            sites,
            new TextBlock("Style").TitleSmall().Margin(0, 6, 0, 0),
            styles,
            new TextBlock("Every point takes the finest data there is: swisstopo's in Switzerland, Copernicus and Sentinel-2 elsewhere. Size, resolution, tiles and levels of detail can be changed in the properties afterwards.")
                .BodySmall().Muted().TextWrapping());
    }

    private ToggleButton StyleCard(TerrainStyle style) =>
        new ToggleButton()
            .CornerRadius(12)
            .Padding(12, 10)
            .MinHeight(0)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .VerticalContentAlignment(VerticalAlignment.Top)
            .Content(new StackPanel().Spacing(2).Children(
                new TextBlock(style.Name).TitleMedium(),
                new TextBlock(style.Use).BodySmall().TextWrapping(),
                new TextBlock(NewTerrainOptions.Summary(style)).LabelSmall().Muted().TextWrapping().Margin(0, 4, 0, 0)))
            .BindIsChecked(_options, o => o.Style == style, (o, on) => { if (on) o.Style = style; else o.Style = o.Style; });
}
