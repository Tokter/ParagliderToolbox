using CommunityToolkit.Mvvm.ComponentModel;

namespace ParagliderToolbox.Framework.Model;

/// <summary>
/// An open project: its tree, the file it was loaded from or saved to, and whether it changed since.
/// </summary>
public sealed class ProjectDocument : ObservableObject
{
    private string? _filePath;
    private bool _isModified;

    /// <summary>Initializes a document for <paramref name="project"/>.</summary>
    /// <param name="project">The project tree.</param>
    /// <param name="filePath">The file the project was loaded from, or <c>null</c> for a new project.</param>
    public ProjectDocument(Project project, string? filePath = null)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        _filePath = filePath;
        Project.SubtreeChanged += (_, e) =>
        {
            IsModified = true;
            if (e.Node == Project && e.PropertyName == nameof(Model.Project.Name)) OnPropertyChanged(nameof(DisplayName));
        };
    }

    /// <summary>Gets the project tree.</summary>
    public Project Project { get; }

    /// <summary>Gets or sets the project's file, or <c>null</c> when it was never saved.</summary>
    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (SetProperty(ref _filePath, value)) OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>Gets or sets whether the project changed since it was loaded or saved.</summary>
    public bool IsModified
    {
        get => _isModified;
        set => SetProperty(ref _isModified, value);
    }

    /// <summary>Gets the name to show for the document: the project's name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Project.Name) ? "Untitled" : Project.Name;
}
