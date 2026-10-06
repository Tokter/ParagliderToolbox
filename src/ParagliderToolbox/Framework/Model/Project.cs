using Atelier.Core.Inspection;

namespace ParagliderToolbox.Framework.Model;

/// <summary>
/// The root of a project tree: the object saved to and loaded from a project file. Its children are the project's
/// top-level nodes.
/// </summary>
[Inspectable]
public partial class Project : ContainerNode
{
    private string _description = string.Empty;
    private string _author = string.Empty;

    /// <summary>Initializes an empty project named "Untitled".</summary>
    public Project()
    {
        Name = "Untitled";
    }

    /// <summary>Gets or sets a free text description of the project.</summary>
    [MultilineText(MinLines = 3)]
    [InspectableProperty("Description", "Project", Description = "What the project is about.")]
    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    /// <summary>Gets or sets who made the project.</summary>
    [InspectableProperty("Author", "Project", Description = "Who made the project.")]
    public string Author
    {
        get => _author;
        set => SetProperty(ref _author, value ?? string.Empty);
    }

    /// <summary>Finds the node with <paramref name="id"/> in the project (the project itself included), or <c>null</c>.</summary>
    public ProjectNode? FindNode(Guid id) => Id == id ? this : Descendants().FirstOrDefault(n => n.Id == id);
}
