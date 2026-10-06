using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParagliderToolbox.Paraglider.Proxy;

/// <summary>What a proxy node represents.</summary>
public enum ProxyNodeKind
{
    /// <summary>A point on the upper canopy surface.</summary>
    Upper,
    /// <summary>A point on the lower canopy surface.</summary>
    Lower,
    /// <summary>A point on the camber surface of a single-surface proxy (or the shared nose and trailing edge points).</summary>
    Camber,
    /// <summary>A cascade knot.</summary>
    Knot,
    /// <summary>The top of a riser.</summary>
    RiserTop,
    /// <summary>A carabiner.</summary>
    Carabiner,
    /// <summary>The brake pulley.</summary>
    Pulley,
    /// <summary>The brake toggle.</summary>
    Toggle,
    /// <summary>The pilot's center of mass.</summary>
    Pilot,
}

/// <summary>A mass point of the proxy.</summary>
public sealed class ProxyNode
{
    /// <summary>Gets or sets the index in <see cref="ProxyModel.Nodes"/>.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets a readable name, also the joint name in the glTF export (e.g. "S03_U2").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets what the node represents.</summary>
    public ProxyNodeKind Kind { get; set; }

    /// <summary>Gets or sets the rest position (m).</summary>
    [JsonConverter(typeof(Vector3JsonConverter))]
    public Vector3 Position { get; set; }

    /// <summary>Gets or sets the mass (kg).</summary>
    public float Mass { get; set; }

    /// <summary>Gets or sets the section (canopy nodes), otherwise −1.</summary>
    public int Section { get; set; } = -1;

    /// <summary>Gets or sets the chord fraction (canopy nodes).</summary>
    public float Chord { get; set; }

    /// <summary>Gets or sets the side: +1 left, −1 right, 0 center.</summary>
    public int Side { get; set; }
}

/// <summary>The kind of a proxy constraint.</summary>
public enum ConstraintKind
{
    /// <summary>Fabric along the chord.</summary>
    Chordwise,
    /// <summary>Fabric along the span.</summary>
    Spanwise,
    /// <summary>Fabric diagonals (shear).</summary>
    Shear,
    /// <summary>Across the profile: the rib holding upper and lower surface together.</summary>
    Rib,
    /// <summary>Resistance to folding (skips a node).</summary>
    Bend,
    /// <summary>A suspension or brake line.</summary>
    Line,
    /// <summary>A riser.</summary>
    Riser,
    /// <summary>The pilot's connection to the carabiners and the chest strap.</summary>
    Harness,
}

/// <summary>
/// A distance constraint between two nodes, for an XPBD solver: compliance (m/N; 0 is rigid) when stretched, and when
/// compressed (ignored for <see cref="TensionOnly"/> constraints, which don't resist compression at all).
/// </summary>
public sealed class ProxyConstraint
{
    public int A { get; set; }
    public int B { get; set; }
    public ConstraintKind Kind { get; set; }
    public float RestLength { get; set; }
    public float Compliance { get; set; }
    public float CompressionCompliance { get; set; }
    public bool TensionOnly { get; set; }

    /// <summary>Gets or sets the line's name (lines and risers), e.g. "A main L1".</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the diameter for line drag (m): the real line diameter, or for a proxy without cascades the
    /// diameter that gives its lines the drag area of all the real lines.
    /// </summary>
    public float DragDiameter { get; set; }
}

/// <summary>A profile section of the proxy, at a rib: the nodes around it from nose to trailing edge.</summary>
public sealed class ProxySection
{
    /// <summary>Gets or sets the span position η of the rib.</summary>
    public float Eta { get; set; }

    /// <summary>Gets or sets the chord (m).</summary>
    public float Chord { get; set; }

    /// <summary>Gets or sets the nose node.</summary>
    public int LeadingEdge { get; set; }

    /// <summary>Gets or sets the trailing edge node.</summary>
    public int TrailingEdge { get; set; }

    /// <summary>Gets or sets the upper surface nodes from nose to tail, without the nose and tail (empty for a single surface).</summary>
    public List<int> Upper { get; set; } = [];

    /// <summary>Gets or sets the lower (or camber) surface nodes from nose to tail, without the nose and tail.</summary>
    public List<int> Lower { get; set; } = [];

    /// <summary>Gets or sets the chord fractions of the stations in <see cref="Upper"/> and <see cref="Lower"/>.</summary>
    public List<float> Stations { get; set; } = [];
}

/// <summary>
/// An aerodynamic strip between two neighboring sections: forces are computed per strip from the local flow and the
/// polar, and spread over its nodes; its closed volume is a pressure cell.
/// </summary>
public sealed class ProxyStrip
{
    public int SectionA { get; set; }
    public int SectionB { get; set; }

    /// <summary>Gets or sets the flat area of the strip (m²).</summary>
    public float Area { get; set; }

    /// <summary>Gets or sets whether the strip's cells have an inlet (tip cells are closed).</summary>
    public bool HasInlet { get; set; } = true;

    /// <summary>Gets or sets the triangles (node index triples, counterclockwise seen from outside) enclosing the strip's volume.</summary>
    public List<int> Surface { get; set; } = [];
}

/// <summary>A named group of constraints the pilot controls by changing their rest length (brakes, speed bar, collapse lines).</summary>
public sealed class ProxyControl
{
    /// <summary>Gets or sets the control's name: BrakeLeft, BrakeRight, SpeedBar, CollapseLeft, CollapseRight, Frontal, BigEars.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the controlled constraints.</summary>
    public List<int> Constraints { get; set; } = [];

    /// <summary>Gets or sets how much each constraint shortens at full input (m), in the order of <see cref="Constraints"/>.</summary>
    public List<float> Travel { get; set; } = [];
}

/// <summary>A sampled section polar: lift, drag and moment coefficients by angle of attack.</summary>
public sealed class ProxyPolar
{
    public List<float> AlphaDegrees { get; set; } = [];
    public List<float> Lift { get; set; } = [];
    public List<float> Drag { get; set; } = [];
    public List<float> Moment { get; set; } = [];
}

/// <summary>
/// The low resolution physics proxy of a paraglider: mass points, distance constraints (fabric, ribs, tension-only lines,
/// risers, harness), profile sections, aerodynamic strips with pressure cells, pilot controls and the section polar.
/// Serialized as the JSON a game loads to run the simulation (see <see cref="ToJson"/>), and the skeleton the high
/// resolution mesh is skinned to (one joint per node).
/// </summary>
public sealed class ProxyModel
{
    /// <summary>The version of the JSON format.</summary>
    public const int CurrentFormatVersion = 1;

    public string Format { get; set; } = "ParagliderToolbox.Proxy";
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>Gets or sets the coordinate convention: glTF, meters, +Y up, +Z forward, +X to the pilot's left.</summary>
    public string Coordinates { get; set; } = "glTF: meters, +Y up, +Z forward (flight direction), +X pilot's left";

    public string Complexity { get; set; } = string.Empty;
    public List<ProxyNode> Nodes { get; set; } = [];
    public List<ProxyConstraint> Constraints { get; set; } = [];
    public List<ProxySection> Sections { get; set; } = [];
    public List<ProxyStrip> Strips { get; set; } = [];
    public List<ProxyControl> Controls { get; set; } = [];
    public ProxyPolar Polar { get; set; } = new();

    /// <summary>Gets or sets the pilot node.</summary>
    public int Pilot { get; set; }

    /// <summary>Gets or sets the pilot's drag area (m², Cd·A).</summary>
    public float PilotDragArea { get; set; } = 0.4f;

    /// <summary>Gets or sets the drag coefficient of the lines on their frontal area.</summary>
    public float LineDragCoefficient { get; set; } = 0.95f;

    /// <summary>Gets or sets the span efficiency for the induced angle of attack (with <see cref="InducedAspectRatio"/>).</summary>
    public float SpanEfficiency { get; set; } = 1f;

    /// <summary>Gets or sets the estimated trim airspeed (m/s), to start a simulation in steady glide.</summary>
    public float TrimAirspeed { get; set; }

    /// <summary>Gets or sets the trim flight path angle below the horizon (degrees).</summary>
    public float TrimFlightPathAngle { get; set; }

    /// <summary>Gets or sets the flat area of the canopy (m²).</summary>
    public float FlatArea { get; set; }

    /// <summary>Gets or sets the projected aspect ratio.</summary>
    public float ProjectedAspectRatio { get; set; }

    /// <summary>Gets or sets the aspect ratio for the induced angle of attack (between the flat and the projected one: the arc helps).</summary>
    public float InducedAspectRatio { get; set; }

    /// <summary>Gets or sets the pressure inside a fully open cell, as a fraction of the dynamic pressure at the inlet.</summary>
    public float InternalPressureCoefficient { get; set; } = 0.75f;

    /// <summary>Gets or sets the angle of attack (degrees) below which the inlets close and the cells deflate.</summary>
    public float InletClosingAlpha { get; set; } = -1.5f;

    /// <summary>Gets or sets how long a cell takes to refill or deflate (s).</summary>
    public float CellPressureTimeConstant { get; set; } = 0.6f;

    /// <summary>Gets the node with <paramref name="name"/>, or null.</summary>
    public ProxyNode? FindNode(string name) => Nodes.FirstOrDefault(n => n.Name == name);

    /// <summary>Gets the control with <paramref name="name"/>, or null.</summary>
    public ProxyControl? FindControl(string name) => Controls.FirstOrDefault(c => c.Name == name);

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Writes the proxy as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, s_options);

    /// <summary>Reads a proxy written by <see cref="ToJson"/>.</summary>
    public static ProxyModel FromJson(string json) =>
        JsonSerializer.Deserialize<ProxyModel>(json, s_options) ?? throw new JsonException("The JSON holds no proxy.");
}

/// <summary>Writes vectors as [x, y, z].</summary>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    /// <inheritdoc/>
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A vector is an [x, y, z] array.");
        reader.Read();
        float x = reader.GetSingle(); reader.Read();
        float y = reader.GetSingle(); reader.Read();
        float z = reader.GetSingle(); reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("A vector is an [x, y, z] array.");
        return new Vector3(x, y, z);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(MathF.Round(value.X, 5));
        writer.WriteNumberValue(MathF.Round(value.Y, 5));
        writer.WriteNumberValue(MathF.Round(value.Z, 5));
        writer.WriteEndArray();
    }
}
