using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Airflow.Atmosphere;

namespace ParagliderToolbox.Modules.Airflow.Views;

/// <summary>
/// The content of the new-airflow dialog: a card per sounding (what kind of day it makes, and the thermals' top and
/// cloud base it gives at the chosen time), the wind (compass buttons or degrees, and its speed), the time and the
/// detail, bound to <see cref="NewAirflowOptions"/>.
/// </summary>
public sealed class NewAirflowView : ContentControl
{
    private readonly NewAirflowOptions _options;

    /// <summary>Initializes the view of <paramref name="options"/>.</summary>
    public NewAirflowView(NewAirflowOptions options)
    {
        _options = options;
        DataContext = options;

        var soundings = new UniformGrid().Columns(4).Spacing(8);
        foreach (var preset in SoundingPresets.All) soundings.Add(Card(preset));

        var compass = new WrapPanel().Spacing(4, 4);
        foreach (var (name, degrees) in NewAirflowOptions.Directions)
        {
            compass.Add(new ToggleButton().Content(new TextBlock(name).HorizontalAlignment(HorizontalAlignment.Center)).Width(44).Height(30).MinHeight(0).Padding(0)
                .ToolTip($"Wind from {name} ({degrees:0}°)")
                .BindIsChecked(options, o => o.Direction == degrees, (o, on) => { if (on) o.WindDirection = degrees.ToString("0", System.Globalization.CultureInfo.InvariantCulture); }));
        }

        var details = new UniformGrid().Columns(4).Spacing(8);
        foreach (var detail in new[] { AirflowDetail.Quick, AirflowDetail.Normal, AirflowDetail.Fine, AirflowDetail.Thermals })
        {
            details.Add(new ToggleButton()
                .CornerRadius(10).Padding(10, 6).MinHeight(0)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .VerticalContentAlignment(VerticalAlignment.Top)
                .ToolTip(detail == AirflowDetail.Thermals ? "The thermals resolved: they rise, build cumulus and die in their shadows; the run plays back over time" : null)
                .Content(new StackPanel().Spacing(1).Children(
                    new TextBlock(detail.ToString()).TitleSmall(),
                    new TextBlock().LabelSmall().Muted().TextWrapping().BindText(options, o => o.DetailInfo(detail))))
                .BindIsChecked(options, o => o.Detail == detail, (o, on) => { if (on) o.Detail = detail; }));
        }

        string where = options.Terrain is { } terrain ? $"over {terrain.Name} ({terrain.ActualSize})" : "";
        Content = new StackPanel().Width(820).Spacing(10).Children(
            new TextBox().Label("Name").BindText(options, o => o.Name, (o, v) => o.Name = v),
            new TextBlock("Sounding").TitleSmall().Margin(0, 4, 0, 0),
            soundings,
            new TextBlock("Wind").TitleSmall().Margin(0, 4, 0, 0),
            new StackPanel().Orientation(Orientation.Horizontal).Spacing(12).Children(
                compass.VerticalAlignment(VerticalAlignment.Center),
                new TextBox().Label("From (°)").Width(90).BindText(options, o => o.WindDirection, (o, v) => o.WindDirection = v),
                new TextBox().Label("Speed (km/h)").Width(110).BindText(options, o => o.WindSpeed, (o, v) => o.WindSpeed = v),
                new TextBox().Label("Time").Width(90).BindText(options, o => o.Time, (o, v) => o.Time = v)),
            new TextBlock("Detail").TitleSmall().Margin(0, 4, 0, 0),
            details,
            new TextBlock($"The airflow runs {where} on its own grid: the sun of that day and time heats the slopes by their angle, their shadows and what covers them (read from the aerial images); the sounding sets how high the thermals go and where clouds form. Everything can be changed in the properties afterwards.")
                .BodySmall().Muted().TextWrapping());
    }

    private ToggleButton Card(SoundingPreset preset) =>
        new ToggleButton()
            .CornerRadius(12)
            .Padding(12, 10)
            .MinHeight(0)
            .HorizontalContentAlignment(HorizontalAlignment.Stretch)
            .VerticalContentAlignment(VerticalAlignment.Top)
            .Content(new StackPanel().Spacing(2).Children(
                new TextBlock(preset.Name).TitleMedium(),
                new TextBlock(preset.Description).BodySmall().TextWrapping(),
                new TextBlock().LabelSmall().Muted().TextWrapping().Margin(0, 4, 0, 0)
                    .BindText(_options, o => o.Summaries.TryGetValue(preset.Id, out var summary) ? summary : "")))
            .BindIsChecked(_options, o => o.Preset == preset, (o, on) => { if (on) o.Preset = preset; else o.Preset = o.Preset; });
}
