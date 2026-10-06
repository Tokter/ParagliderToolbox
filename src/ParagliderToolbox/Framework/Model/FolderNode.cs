using Atelier.Core.Inspection;

namespace ParagliderToolbox.Framework.Model;

/// <summary>A node that groups other nodes in the project tree.</summary>
[Inspectable]
public partial class FolderNode : ContainerNode
{
    /// <summary>Initializes a folder named "Folder".</summary>
    public FolderNode()
    {
        Name = "Folder";
    }
}
