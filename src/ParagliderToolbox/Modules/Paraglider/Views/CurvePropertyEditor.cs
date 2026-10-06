using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Paraglider.Mathematics;

namespace ParagliderToolbox.Modules.Paraglider.Views;

/// <summary>
/// The property editor of <see cref="Curve"/> properties: a preview and a button that opens the curve in a dialog,
/// where it can be dragged into shape or typed as x:y pairs. The property changes when the dialog closes with OK (the
/// model regenerates once); Cancel keeps the curve as it was.
/// </summary>
public static class CurvePropertyEditor
{
    /// <summary>Creates the editor for a property.</summary>
    public static UIElement Create(PropertyEditorContext context)
    {
        var preview = new CurveEditor { IsReadOnly = true, Curve = context.Value as Curve, VerticalAlignment = VerticalAlignment.Center };
        context.ValueChanged += value => preview.Curve = value as Curve;
        var edit = new Button("Edit…")
            .Variant(ButtonVariant.Outlined)
            .Height(28).MinHeight(0).Padding(10, 0)
            .VerticalAlignment(VerticalAlignment.Center)
            .IsEnabled(!context.IsReadOnly)
            .ToolTip("Edit the curve: drag the points, double-click to add one, right-click to remove one");
        edit.Click += (_, _) => _ = EditAsync(context, edit);
        return new Grid().Columns(GridLength.Star, GridLength.Auto).ColumnSpacing(6).Children(preview, edit.Column(1));
    }

    // The dialog edits a copy; the property (and with it the model, which takes a while to regenerate) changes only on OK.
    private static async Task EditAsync(PropertyEditorContext context, UIElement owner)
    {
        if (context.Value is not Curve original) return;
        const float contentWidth = 640;
        // The editor stretches to the content width, so every point (the end points sit at its edges) stays inside.
        var editor = new CurveEditor { Curve = original, Height = 300 };
        var text = new TextBox().Label("Points (x:y; …)").Text(original.ToString()).Multiline(2, 4);
        bool updatingText = false;
        void ShowText(Curve curve)
        {
            updatingText = true;
            text.Text = curve.ToString();
            updatingText = false;
        }
        editor.CurveEdited += (_, curve) => ShowText(curve);
        text.TextChanged += (_, value) =>
        {
            if (updatingText) return;
            try
            {
                editor.Curve = Curve.Parse(value);
            }
            catch (FormatException)
            {
                // Keep editing; the curve updates once the text is valid again.
            }
        };
        var revert = new Button("Revert").Variant(ButtonVariant.Text).Height(28).MinHeight(0).Padding(10, 0)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .ToolTip("Put the curve back as it was when the editor opened")
            .OnClick(() =>
            {
                editor.Curve = original;
                ShowText(original);
            });

        var content = new StackPanel().Spacing(12).Width(contentWidth).Children(
            new TextBlock(context.Descriptor.Description ?? string.Empty).BodyMedium().Muted().TextWrapping(),
            editor,
            text,
            new Grid().Columns(GridLength.Star, GridLength.Auto).ColumnSpacing(12).Children(
                new TextBlock("x runs from 0 to 1 (center to tip along the span, or nose to tail along the chord). Drag the points; double-click adds a point, right-click removes one.")
                    .BodySmall().Muted().TextWrapping(),
                revert.Column(1)));
        var response = await new Dialog(context.Descriptor.DisplayName)
            .Content(content)
            .AddButton("Cancel", DialogResult.Cancel, isCancel: true)
            .AddButton("OK", DialogResult.Ok, isDefault: true, variant: ButtonVariant.Filled)
            // Dialogs are at most 560 wide by default: room for the content and the dialog's padding.
            .MaxWidth(contentWidth + 80)
            .ShowAsync(owner);
        if (response.Result == DialogResult.Ok && editor.Curve is { } edited && !edited.Equals(original)) context.UpdateValue(edited);
    }
}
