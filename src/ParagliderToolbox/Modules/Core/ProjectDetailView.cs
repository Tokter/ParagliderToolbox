using Atelier.Controls;
using Atelier.Core.Tree;
using Atelier.Layout;
using Atelier.Markup;
using ParagliderToolbox.Framework.Model;
using ParagliderToolbox.Framework.Modules;

namespace ParagliderToolbox.Modules.Core;

/// <summary>The detail view of the project: its name, an editable description and the author above its top-level objects.</summary>
public sealed class ProjectDetailView(Toolbox toolbox, Project project) : ContainerDetailView(toolbox, project)
{
    /// <inheritdoc/>
    protected override UIElement CreateHeader() =>
        new StackPanel().Spacing(6).Children(
            new TextBlock().HeadlineMedium().BindText(project, p => p.Name),
            new TextBox()
                .Label("Description")
                .Placeholder("What is the project about?")
                .Multiline(minLines: 3, maxLines: 12)
                .BindText(project, p => p.Description, (p, text) => p.Description = text),
            new TextBlock().BodySmall().Muted().BindText(project, p => string.IsNullOrWhiteSpace(p.Author) ? string.Empty : $"by {p.Author}"));
}
