using System.Numerics;

namespace ParagliderToolbox.Paraglider.Geometry;

/// <summary>What a vertex is attached to, so the physics proxy can carry it along (see <see cref="Proxy.SkinBinder"/>).</summary>
public enum BindKind : byte
{
    /// <summary>A point of the canopy: span position η, chord fraction x and height fraction h (0 lower skin, 1 upper skin).</summary>
    Canopy,
    /// <summary>A point between two rigging points: <see cref="VertexBind.PointA"/>, <see cref="VertexBind.PointB"/> and the fraction <see cref="VertexBind.A"/> between them.</summary>
    Rigging,
}

/// <summary>The attachment of a vertex (see <see cref="BindKind"/>).</summary>
public readonly record struct VertexBind(BindKind Kind, float A, float B, float C, int PointA = -1, int PointB = -1)
{
    /// <summary>Creates a canopy binding.</summary>
    public static VertexBind OnCanopy(double eta, double x, double height) => new(BindKind.Canopy, (float)eta, (float)x, (float)height);

    /// <summary>Creates a binding between two rigging points.</summary>
    public static VertexBind OnRigging(int pointA, int pointB, double fraction) => new(BindKind.Rigging, (float)fraction, 0, 0, pointA, pointB);
}

/// <summary>The materials of the generated model.</summary>
public enum GliderMaterial
{
    /// <summary>The canopy fabric, textured with the color design and the seams.</summary>
    Canopy,
    /// <summary>The internal ribs.</summary>
    Ribs,
    /// <summary>The suspension lines (vertex colors give the line colors).</summary>
    Lines,
    /// <summary>The webbing of the risers.</summary>
    Risers,
    /// <summary>Maillons, pulleys and carabiners.</summary>
    Metal,
    /// <summary>The brake toggles.</summary>
    Toggles,
}

/// <summary>A triangle mesh of one part of the model (canopy, ribs, lines, ...) with one material.</summary>
public sealed class MeshPart(string name, GliderMaterial material)
{
    /// <summary>Gets the part's name, e.g. "Canopy".</summary>
    public string Name { get; } = name;

    /// <summary>Gets the part's material.</summary>
    public GliderMaterial Material { get; } = material;

    /// <summary>Gets the vertex positions.</summary>
    public List<Vector3> Positions { get; } = [];

    /// <summary>Gets the vertex normals (filled by <see cref="ComputeNormals"/> when not given).</summary>
    public List<Vector3> Normals { get; } = [];

    /// <summary>Gets the texture coordinates (one per vertex).</summary>
    public List<Vector2> TexCoords { get; } = [];

    /// <summary>Gets the vertex colors (linear RGBA), or empty for none.</summary>
    public List<Vector4> Colors { get; } = [];

    /// <summary>Gets the attachments of the vertices.</summary>
    public List<VertexBind> Binds { get; } = [];

    /// <summary>Gets the triangle indices.</summary>
    public List<uint> Indices { get; } = [];

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount => Positions.Count;

    /// <summary>Gets the number of triangles.</summary>
    public int TriangleCount => Indices.Count / 3;

    /// <summary>Adds a vertex and returns its index.</summary>
    public uint AddVertex(Vector3 position, Vector2 uv, VertexBind bind, Vector4? color = null)
    {
        Positions.Add(position);
        TexCoords.Add(uv);
        Binds.Add(bind);
        if (color is { } c) Colors.Add(c);
        return (uint)(Positions.Count - 1);
    }

    /// <summary>Adds a triangle.</summary>
    public void AddTriangle(uint a, uint b, uint c)
    {
        Indices.Add(a);
        Indices.Add(b);
        Indices.Add(c);
    }

    /// <summary>Adds the two triangles of the quad a-b-c-d (counterclockwise seen from the front).</summary>
    public void AddQuad(uint a, uint b, uint c, uint d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    /// <summary>Computes smooth, area-weighted normals from the triangles.</summary>
    public void ComputeNormals()
    {
        var normals = new Vector3[Positions.Count];
        for (int i = 0; i < Indices.Count; i += 3)
        {
            int a = (int)Indices[i], b = (int)Indices[i + 1], c = (int)Indices[i + 2];
            var n = Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        Normals.Clear();
        foreach (var n in normals)
        {
            float length = n.Length();
            Normals.Add(length > 1e-12f ? n / length : Vector3.UnitY);
        }
    }

    /// <summary>Gets the bounds of the vertices.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in Positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}
