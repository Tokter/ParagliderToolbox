using System.Collections.Concurrent;
using System.Reflection;
using Atelier.Controls;
using Atelier.Core.Primitives;
using Atelier.Core.Tree;
using Atelier.Markup;
using ParagliderToolbox.Framework.Model;

namespace ParagliderToolbox.Framework.Views;

/// <summary>
/// The property editors the toolbox adds to Atelier's built-in ones, for every <see cref="PropertiesView"/>. Modules add
/// theirs through <see cref="Modules.Toolbox.PropertyEditors"/>.
/// </summary>
public static class ToolboxPropertyEditors
{
    private static readonly ConcurrentDictionary<(Type, string), MultilineTextAttribute?> s_multiline = new();

    /// <summary>Registers the toolbox's editors in <paramref name="registry"/>.</summary>
    public static void Register(PropertyEditorRegistry registry)
    {
        registry.Register(context => context.ValueType == typeof(string) && GetMultiline(context) != null, CreateMultilineEditor);
    }

    /// <summary>Gets the <see cref="MultilineTextAttribute"/> of the edited property, or <c>null</c>.</summary>
    public static MultilineTextAttribute? GetMultiline(PropertyEditorContext context) =>
        s_multiline.GetOrAdd((context.Target.GetType(), context.Descriptor.Name),
            static key => key.Item1.GetProperty(key.Item2)?.GetCustomAttribute<MultilineTextAttribute>());

    /// <summary>Creates a multi-line text editor: the built-in string editor, as a text area.</summary>
    public static UIElement CreateMultilineEditor(PropertyEditorContext context)
    {
        var options = GetMultiline(context) ?? new MultilineTextAttribute();
        var textBox = (TextBox)PropertyEditorRegistry.CreateStringEditor(context);
        // The built-in editor is one line tall; a text area sizes to its lines.
        textBox.ClearValue(UIElement.HeightProperty);
        textBox.Multiline(options.MinLines, options.MaxLines).VerticalAlignment(VerticalAlignment.Top);
        return textBox;
    }
}
