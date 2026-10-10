using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Modules.Terrain.Views;

/// <summary>
/// The property editor of <see cref="SourceChoice"/> properties: a list of "Automatic" and the registered sources of
/// that kind (with their resolution), so sources added later appear by themselves.
/// </summary>
public static class SourceChoiceEditor
{
    private const string Automatic = "Automatic (the finest there is)";

    /// <summary>Creates the editor for a property.</summary>
    public static UIElement Create(PropertyEditorContext context, TerrainSources sources)
    {
        var kind = (context.Value as SourceChoice)?.Kind ?? SourceKind.Elevation;
        var choices = (kind == SourceKind.Elevation ? sources.Elevation.Cast<ITerrainSource>() : sources.Imagery)
            .OrderBy(s => s.Resolution).ToList();
        string Label(ITerrainSource s) => $"{s.Name} ({s.Resolution:0.##} m)";
        var items = new[] { Automatic }.Concat(choices.Select(Label)).ToArray();
        int IndexOf(object? value) => value is SourceChoice { Id: { } id } ? Math.Max(0, choices.FindIndex(s => s.Id == id) + 1) : 0;

        var comboBox = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = IndexOf(context.Value),
            IsEnabled = !context.IsReadOnly,
            Height = 32,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        bool updating = false;
        comboBox.SelectionChanged += (_, selected) =>
        {
            if (updating || selected is not string label) return;
            int index = Array.IndexOf(items, label);
            if (index < 0) return;
            updating = true;
            try
            {
                context.UpdateValue(new SourceChoice(kind, index == 0 ? null : choices[index - 1].Id));
            }
            finally
            {
                updating = false;
            }
        };
        context.ValueChanged += value =>
        {
            if (updating) return;
            updating = true;
            try
            {
                comboBox.SelectedIndex = IndexOf(value);
            }
            finally
            {
                updating = false;
            }
        };
        return comboBox;
    }
}
