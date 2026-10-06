using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ParagliderToolbox.Framework.Model;

namespace ParagliderToolbox.Framework.Serialization;

/// <summary>
/// Saves projects to JSON and loads them back, and copies nodes (for duplicating them).
/// </summary>
/// <remarks>
/// <para>
/// A project file is an envelope with the format id and version around the project tree:
/// <code>
/// { "format": "ParagliderToolbox.Project", "version": 1, "project": { "id": "...", "name": "...", "children": [ { "$type": "folder", ... } ] } }
/// </code>
/// Every node is written with System.Text.Json (see <see cref="ProjectNode"/> for which properties are saved); nodes
/// in <see cref="ProjectNode.Children"/> carry their <see cref="NodeType.Id"/> as <c>$type</c>, so every node class
/// must be registered in the <see cref="NodeTypeRegistry"/> the serializer was made with.
/// </para>
/// <para>
/// Node properties of types System.Text.Json can't handle on its own need a converter: pass it to the constructor
/// (modules add theirs with <see cref="Modules.Toolbox.JsonConverters"/>).
/// </para>
/// </remarks>
public sealed class ProjectSerializer
{
    /// <summary>The format id written to every project file.</summary>
    public const string FormatId = "ParagliderToolbox.Project";

    /// <summary>The version of the file format this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The file extension of project files, with the dot.</summary>
    public const string FileExtension = ".pgtproj";

    /// <summary>The file dialog filter for project files.</summary>
    public const string FileFilter = "Paraglider Toolbox project (*.pgtproj)|*.pgtproj|JSON file (*.json)|*.json|All files (*.*)|*.*";

    private const string TypeDiscriminator = "$type";

    private readonly NodeTypeRegistry _types;

    /// <summary>Initializes a serializer for the node types in <paramref name="types"/>.</summary>
    /// <param name="types">The node types projects can contain. Register them all before the first use.</param>
    /// <param name="converters">Converters for property types System.Text.Json doesn't handle on its own.</param>
    public ProjectSerializer(NodeTypeRegistry types, IEnumerable<JsonConverter>? converters = null)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowOutOfOrderMetadataProperties = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigureNodeContract } },
        };
        Options.Converters.Add(new JsonStringEnumConverter());
        foreach (var converter in converters ?? []) Options.Converters.Add(converter);
    }

    /// <summary>Gets the options the project tree is written and read with.</summary>
    public JsonSerializerOptions Options { get; }

    /// <summary>Writes <paramref name="project"/> as a project file.</summary>
    /// <exception cref="InvalidOperationException">A node's class isn't registered in the <see cref="NodeTypeRegistry"/>.</exception>
    public string Serialize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Descendants().FirstOrDefault(n => _types.Find(n) == null) is { } unregistered)
        {
            throw new InvalidOperationException(
                $"'{unregistered.Name}' can't be saved: {unregistered.GetType().Name} isn't registered as a node type (see NodeTypeRegistry).");
        }
        var file = new JsonObject
        {
            ["format"] = FormatId,
            ["version"] = CurrentVersion,
            ["project"] = JsonSerializer.SerializeToNode(project, Options),
        };
        return file.ToJsonString(Options);
    }

    /// <summary>Reads a project file written by <see cref="Serialize"/>.</summary>
    /// <exception cref="ProjectFileException">The text is not a project file this version can read.</exception>
    public Project Deserialize(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject file)
            {
                throw new ProjectFileException("The file doesn't contain a project.");
            }
            if ((string?)file["format"] != FormatId)
            {
                throw new ProjectFileException("The file isn't a Paraglider Toolbox project.");
            }
            int version = (int?)file["version"] ?? 0;
            if (version > CurrentVersion)
            {
                throw new ProjectFileException($"The project was saved by a newer version of Paraglider Toolbox (format {version}; this version reads up to {CurrentVersion}).");
            }

            // Older formats are upgraded here, one version at a time, before the tree is read.
            var project = file["project"]?.Deserialize<Project>(Options)
                ?? throw new ProjectFileException("The file doesn't contain a project.");
            return project;
        }
        catch (JsonException e)
        {
            throw new ProjectFileException($"The project file is damaged or uses an unknown kind of object: {e.Message}", e);
        }
    }

    /// <summary>Saves <paramref name="project"/> to <paramref name="path"/>, replacing the file only once the new one is written.</summary>
    public void Save(Project project, string path)
    {
        string json = Serialize(project);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, fullPath, overwrite: true);
    }

    /// <summary>Loads the project file at <paramref name="path"/>.</summary>
    /// <exception cref="ProjectFileException">The file is not a project file this version can read.</exception>
    public Project Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>
    /// Copies <paramref name="node"/> and the nodes below it, as saving and loading them would. The copy has no parent
    /// and, with <paramref name="newIds"/>, new ids throughout.
    /// </summary>
    public ProjectNode Clone(ProjectNode node, bool newIds = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        var json = JsonSerializer.SerializeToNode<ProjectNode>(node, Options);
        var copy = json.Deserialize<ProjectNode>(Options)!;
        if (newIds)
        {
            copy.Id = Guid.NewGuid();
            foreach (var descendant in copy.Descendants()) descendant.Id = Guid.NewGuid();
        }
        return copy;
    }

    // Shapes the JSON contract of the node classes: children are written with their type id, and computed (get-only)
    // properties are left out.
    private void ConfigureNodeContract(JsonTypeInfo info)
    {
        if (!typeof(ProjectNode).IsAssignableFrom(info.Type) || info.Kind != JsonTypeInfoKind.Object) return;

        for (int i = info.Properties.Count - 1; i >= 0; i--)
        {
            var property = info.Properties[i];
            if (property.Set == null && property.ObjectCreationHandling != JsonObjectCreationHandling.Populate)
            {
                info.Properties.RemoveAt(i);
            }
        }

        if (info.Type == typeof(ProjectNode))
        {
            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = TypeDiscriminator,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };
            foreach (var type in _types.Types.Where(t => !t.ClrType.IsAbstract))
            {
                polymorphism.DerivedTypes.Add(new JsonDerivedType(type.ClrType, type.Id));
            }
            info.PolymorphismOptions = polymorphism;
        }
    }
}

/// <summary>A project file that can't be read: not a project, damaged, or from a newer version.</summary>
public sealed class ProjectFileException(string message, Exception? inner = null) : Exception(message, inner);
