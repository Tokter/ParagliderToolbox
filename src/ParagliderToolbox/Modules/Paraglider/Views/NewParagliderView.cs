using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Paraglider.Design;

namespace ParagliderToolbox.Modules.Paraglider.Views;

/// <summary>
/// The content of the new-paraglider dialog: the name, a card per wing class (with its key numbers) and the mesh detail
/// as a segmented choice (with the triangle counts), bound to <see cref="NewParagliderOptions"/>.
/// </summary>
public sealed class NewParagliderView : ContentControl
{
    private readonly NewParagliderOptions _options;

    /// <summary>Initializes the view of <paramref name="options"/>.</summary>
    public NewParagliderView(NewParagliderOptions options)
    {
        _options = options;
        DataContext = options;

        var classes = new UniformGrid().Columns(GliderPresets.Classes.Count).Spacing(8);
        foreach (var wingClass in GliderPresets.Classes) classes.Add(ClassCard(wingClass));

        var details = new UniformGrid().Columns(NewParagliderOptions.Details.Count).Spacing(0);
        for (int i = 0; i < NewParagliderOptions.Details.Count; i++) details.Add(DetailButton(NewParagliderOptions.Details[i], i, NewParagliderOptions.Details.Count));

        Content = new StackPanel().Width(760).Spacing(10).Children(
            new TextBox().Label("Name").BindText(options, o => o.Name, (o, v) => o.Name = v),
            new TextBlock("Wing class").TitleSmall().Margin(0, 6, 0, 0),
            classes,
            new TextBlock().BodyMedium().TextWrapping().MinHeight(40).BindText(options, o => o.Description),
            new TextBlock("Model detail").TitleSmall().Margin(0, 6, 0, 0),
            details,
            new TextBlock("The class sets every design parameter; change any of them in the properties afterwards. The detail can be changed there too (Mesh).")
                .BodySmall().Muted().TextWrapping());
    }

    private ToggleButton ClassCard(WingClass wingClass)
    {
        var info = NewParagliderOptions.Info(wingClass);
        return new ToggleButton()
            .CornerRadius(12)
            .Padding(12, 10)
            .MinHeight(0)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .VerticalContentAlignment(VerticalAlignment.Top)
            .ToolTip(info.Description)
            .Content(new StackPanel().Spacing(2).Children(
                new TextBlock(info.Name).TitleMedium(),
                new TextBlock(info.Title).BodySmall().TextWrapping(),
                new TextBlock(NewParagliderOptions.Summary(wingClass)).LabelSmall().Muted().TextWrapping().Margin(0, 4, 0, 0)))
            .BindIsChecked(_options, o => o.Class == wingClass, (o, on) => { if (on) o.Class = wingClass; else o.Class = o.Class; });
    }

    // A segment: rounded only on the outer ends of the row.
    private ToggleButton DetailButton(MeshDetail detail, int index, int count)
    {
        float left = index == 0 ? 20 : 0, right = index == count - 1 ? 20 : 0;
        return new ToggleButton()
            .CornerRadius(left, right, right, left)
            .Padding(12, 8)
            .MinHeight(0)
            .HorizontalContentAlignment(HorizontalAlignment.Center)
            .Content(new StackPanel().Spacing(0).Children(
                new TextBlock(NewParagliderOptions.DetailName(detail)).LabelLarge().HorizontalAlignment(HorizontalAlignment.Center),
                new TextBlock().LabelSmall().Muted().HorizontalAlignment(HorizontalAlignment.Center)
                    .BindText(_options, o => $"{o.Triangles(detail)} · {NewParagliderOptions.DetailUse(detail)}")))
            .BindIsChecked(_options, o => o.Detail == detail, (o, on) => { if (on) o.Detail = detail; else o.Detail = o.Detail; });
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree()
    {
        base.OnAttachedToVisualTree();
        _options.StartCounting();
    }
}
