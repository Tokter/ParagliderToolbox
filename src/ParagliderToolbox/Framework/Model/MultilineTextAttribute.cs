namespace ParagliderToolbox.Framework.Model;

/// <summary>
/// Edits a <see cref="string"/> property of a node as multi-line text in the property editor: Enter starts a new line
/// and long lines wrap.
/// </summary>
/// <example>
/// <code>
/// [MultilineText(MinLines = 3)]
/// [InspectableProperty("Notes", "General")]
/// public string Notes { get => _notes; set => SetProperty(ref _notes, value); }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class MultilineTextAttribute : Attribute
{
    /// <summary>Gets or sets the number of lines the editor is at least tall enough for. The default is 2.</summary>
    public int MinLines { get; set; } = 2;

    /// <summary>Gets or sets the number of lines the editor grows to before it scrolls; 0 grows with the text. The default is 8.</summary>
    public int MaxLines { get; set; } = 8;
}
