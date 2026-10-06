using System.Numerics;
using ParagliderToolbox.Paraglider.Geometry;

namespace ParagliderToolbox.Paraglider.Rigging;

/// <summary>
/// Builds the meshes of the rigging: the suspension and brake lines as tubes, the riser webbing, the maillons, pulleys
/// and carabiners, and the brake toggles. Every vertex is bound to the rigging points it lies between.
/// </summary>
public sealed class RiggingBuilder(RiggingLayout layout, Design.GliderDesign design)
{
    /// <summary>The line colors by row (A, B, C, D) in linear RGB; brakes use <see cref="BrakeColor"/>.</summary>
    public static readonly Vector4[] RowColors =
    [
        SrgbToLinear(0xD3, 0x2F, 0x2F),
        SrgbToLinear(0xF9, 0xA8, 0x25),
        SrgbToLinear(0x19, 0x76, 0xD2),
        SrgbToLinear(0x38, 0x8E, 0x3C),
    ];

    /// <summary>The color of the brake lines.</summary>
    public static readonly Vector4 BrakeColor = SrgbToLinear(0xFF, 0x6F, 0x00);

    /// <summary>Builds the line tubes.</summary>
    public MeshPart BuildLines()
    {
        var part = new MeshPart("Lines", GliderMaterial.Lines);
        int sides = Math.Clamp(design.LineSides, 3, 16);
        foreach (var line in layout.Lines)
        {
            if (line.Level == LineLevel.Riser) continue;
            var color = line.Row == RiggingLayout.BrakeRow ? BrakeColor : RowColors[Math.Min(line.Row, RowColors.Length - 1)];
            float radius = (float)(line.Diameter / 2000 * Math.Max(1, design.LineDisplayScale));
            AddTube(part, layout.Points[line.Upper], layout.Points[line.Lower], radius, sides, color);
        }
        part.ComputeNormals();
        return part;
    }

    /// <summary>Builds the riser webbing.</summary>
    public MeshPart BuildRisers()
    {
        var part = new MeshPart("Risers", GliderMaterial.Risers);
        foreach (var line in layout.Lines.Where(l => l.Level == LineLevel.Riser))
        {
            AddStrap(part, layout.Points[line.Upper], layout.Points[line.Lower], 0.025f, 0.003f);
        }
        part.ComputeNormals();
        return part;
    }

    /// <summary>Builds the maillons, pulleys and carabiners.</summary>
    public MeshPart BuildHardware()
    {
        var part = new MeshPart("Hardware", GliderMaterial.Metal);
        foreach (var point in layout.Points)
        {
            switch (point.Kind)
            {
                case RigPointKind.RiserTop:
                    AddRing(part, point, Vector3.UnitX, 0.011f, 0.0022f, stretch: 1.5f);
                    break;
                case RigPointKind.Pulley:
                    AddRing(part, point, Vector3.UnitX, 0.012f, 0.004f, stretch: 1f);
                    break;
                case RigPointKind.Carabiner:
                    AddRing(part, point, Vector3.UnitX, 0.035f, 0.0055f, stretch: 1.6f);
                    break;
            }
        }
        part.ComputeNormals();
        return part;
    }

    /// <summary>Builds the brake toggles: a loop and a grip below it.</summary>
    public MeshPart BuildToggles()
    {
        var part = new MeshPart("Toggles", GliderMaterial.Toggles);
        foreach (var toggle in layout.Points.Where(p => p.Kind == RigPointKind.Toggle))
        {
            AddRing(part, toggle, Vector3.UnitX, 0.018f, 0.004f, stretch: 1.2f);
            // The grip hangs below the loop, toward the pilot's hand.
            var top = toggle.Position - new Vector3(0, 0.02f, 0);
            var bottom = top - new Vector3(0, 0.11f, 0);
            AddCylinder(part, top, bottom, 0.0135f, 12, toggle.Id);
        }
        part.ComputeNormals();
        return part;
    }

    private static void AddTube(MeshPart part, RigPoint a, RigPoint b, float radius, int sides, Vector4 color)
    {
        var axis = b.Position - a.Position;
        float length = axis.Length();
        if (length < 1e-6f) return;
        axis /= length;
        var (u, v) = Perpendiculars(axis);
        // Rings at the ends and in between, so the tube follows a sagging, deformed line smoothly.
        int rings = Math.Clamp((int)(length / 0.5f) + 1, 1, 12) + 1;
        uint start = (uint)part.VertexCount;
        for (int r = 0; r < rings; r++)
        {
            float f = r / (float)(rings - 1);
            var center = Vector3.Lerp(a.Position, b.Position, f);
            for (int s = 0; s < sides; s++)
            {
                double angle = 2 * Math.PI * s / sides;
                var offset = (u * (float)Math.Cos(angle) + v * (float)Math.Sin(angle)) * radius;
                part.AddVertex(center + offset, new Vector2(s / (float)sides, f * length), VertexBind.OnRigging(a.Id, b.Id, f), color);
            }
        }
        for (int r = 0; r < rings - 1; r++)
        {
            for (int s = 0; s < sides; s++)
            {
                uint i0 = start + (uint)(r * sides + s), i1 = start + (uint)(r * sides + (s + 1) % sides);
                uint j0 = i0 + (uint)sides, j1 = i1 + (uint)sides;
                part.AddQuad(i0, i1, j1, j0);
            }
        }
    }

    private static void AddStrap(MeshPart part, RigPoint top, RigPoint bottom, float width, float thickness)
    {
        var axis = Vector3.Normalize(top.Position - bottom.Position);
        var across = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ));
        if (float.IsNaN(across.X)) across = Vector3.UnitX;
        var depth = Vector3.Normalize(Vector3.Cross(across, axis));
        Vector3[] corners =
        [
            across * width / 2 + depth * thickness / 2,
            -across * width / 2 + depth * thickness / 2,
            -across * width / 2 - depth * thickness / 2,
            across * width / 2 - depth * thickness / 2,
        ];
        uint start = (uint)part.VertexCount;
        foreach (var (point, f) in new[] { (top, 0f), (bottom, 1f) })
        {
            for (int c = 0; c < 4; c++)
            {
                part.AddVertex(point.Position + corners[c], new Vector2(c / 4f, f), VertexBind.OnRigging(top.Id, bottom.Id, f));
            }
        }
        for (int c = 0; c < 4; c++)
        {
            uint a = start + (uint)c, b = start + (uint)((c + 1) % 4);
            part.AddQuad(a, b, b + 4, a + 4);
        }
    }

    // A ring (torus) around the point, in the plane perpendicular to the normal, stretched along Y into an oval.
    private static void AddRing(MeshPart part, RigPoint point, Vector3 normal, float radius, float tube, float stretch)
    {
        const int segments = 24, sides = 8;
        var (u, v) = Perpendiculars(normal);
        if (Math.Abs(v.Y) < Math.Abs(u.Y)) (u, v) = (v, u); // v is the long, vertical axis
        uint start = (uint)part.VertexCount;
        for (int s = 0; s < segments; s++)
        {
            double a = 2 * Math.PI * s / segments;
            var ringDir = u * (float)Math.Cos(a) + v * (float)(Math.Sin(a) * stretch);
            var center = point.Position + ringDir * radius;
            var outward = Vector3.Normalize(u * (float)Math.Cos(a) + v * (float)Math.Sin(a));
            for (int t = 0; t < sides; t++)
            {
                double b = 2 * Math.PI * t / sides;
                var p = center + (outward * (float)Math.Cos(b) + normal * (float)Math.Sin(b)) * tube;
                part.AddVertex(p, new Vector2(s / (float)segments, t / (float)sides), VertexBind.OnRigging(point.Id, point.Id, 0));
            }
        }
        for (int s = 0; s < segments; s++)
        {
            for (int t = 0; t < sides; t++)
            {
                uint a = start + (uint)(s * sides + t);
                uint b = start + (uint)(s * sides + (t + 1) % sides);
                uint c = start + (uint)((s + 1) % segments * sides + (t + 1) % sides);
                uint d = start + (uint)((s + 1) % segments * sides + t);
                part.AddQuad(a, d, c, b);
            }
        }
    }

    private static void AddCylinder(MeshPart part, Vector3 top, Vector3 bottom, float radius, int sides, int pointId)
    {
        var axis = Vector3.Normalize(bottom - top);
        var (u, v) = Perpendiculars(axis);
        uint start = (uint)part.VertexCount;
        foreach (var (center, f) in new[] { (top, 0f), (bottom, 1f) })
        {
            for (int s = 0; s < sides; s++)
            {
                double a = 2 * Math.PI * s / sides;
                // Slightly barrel-shaped grip.
                part.AddVertex(center + (u * (float)Math.Cos(a) + v * (float)Math.Sin(a)) * radius, new Vector2(s / (float)sides, f), VertexBind.OnRigging(pointId, pointId, 0));
            }
        }
        for (int s = 0; s < sides; s++)
        {
            uint a = start + (uint)s, b = start + (uint)((s + 1) % sides);
            part.AddQuad(a, b, b + (uint)sides, a + (uint)sides);
        }
        // Caps.
        foreach (var (center, offset, flip) in new[] { (top, 0, true), (bottom, sides, false) })
        {
            uint c = part.AddVertex(center, new Vector2(0.5f, 0.5f), VertexBind.OnRigging(pointId, pointId, 0));
            for (int s = 0; s < sides; s++)
            {
                uint a = start + (uint)(offset + s), b = start + (uint)(offset + (s + 1) % sides);
                if (flip) part.AddTriangle(c, b, a);
                else part.AddTriangle(c, a, b);
            }
        }
    }

    private static (Vector3 U, Vector3 V) Perpendiculars(Vector3 axis)
    {
        var reference = Math.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, reference));
        var v = Vector3.Cross(axis, u);
        return (u, v);
    }

    internal static Vector4 SrgbToLinear(byte r, byte g, byte b) => new(ToLinear(r), ToLinear(g), ToLinear(b), 1);

    private static float ToLinear(byte c)
    {
        float s = c / 255f;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }
}
