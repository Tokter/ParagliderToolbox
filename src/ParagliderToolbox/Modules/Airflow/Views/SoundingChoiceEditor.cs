using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using ParagliderToolbox.Airflow.Atmosphere;

namespace ParagliderToolbox.Modules.Airflow.Views;

/// <summary>The property editor of <see cref="SoundingChoice"/> properties: a list of the sounding presets.</summary>
public static class SoundingChoiceEditor
{
    /// <summary>Creates the editor for a property.</summary>
    public static UIElement Create(PropertyEditorContext context)
    {
        var presets = SoundingPresets.All;
        var items = presets.Select(p => p.Name).ToArray();
        int IndexOf(object? value) => value is SoundingChoice { Id: var id } ? Math.Max(0, presets.ToList().FindIndex(p => p.Id == id)) : 0;

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
            if (updating || selected is not string name) return;
            int index = Array.IndexOf(items, name);
            if (index < 0) return;
            updating = true;
            try
            {
                context.UpdateValue(new SoundingChoice(presets[index].Id));
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
