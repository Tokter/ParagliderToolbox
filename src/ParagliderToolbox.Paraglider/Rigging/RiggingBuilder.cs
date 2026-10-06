using System.Numerics;
using ParagliderToolbox.Paraglider.Geometry;

namespace ParagliderToolbox.Paraglider.Rigging;

/// <summary>
/// Builds the meshes of the rigging: the suspension and brake lines as tubes, the riser webbing, the maillons, pulleys
/// and carabiners, and the brake toggles. Every vertex is bound to the rigging points it lies between.
/// </summary>
/// <remarks>
/// The <see cref="Design.MeshSettings"/> set the detail: tubes with rings every
/// <see cref="Design.MeshSettings.LineSegmentLength"/> (or one straight segment), flat ribbons for two sides, and tori
/// or, for low poly, small diamonds for the hardware and a box for each toggle.
/// </remarks>
public sealed class RiggingBuilder(RiggingLayout layout, Design.GliderDesign design)
{
    private readonly Design.MeshSettings _settings = Design.MeshSettings.FromDesign(design);

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
        int sides = Math.Clamp(_settings.LineSides, 2, 16);
        foreach (var line in layout.Lines)
        {
            if (line.Level == LineLevel.Riser) continue;
            var color = line.Row == RiggingLayout.BrakeRow ? BrakeColor : RowColors[Math.Min(line.Row, RowColors.Length - 1)];
            float radius = (float)(line.Diameter / 2000 * Math.Max(1, design.LineDisplayScale));
            var a = layout.Points[line.Upper];
            var b = layout.Points[line.Lower];
            if (sides == 2) AddRibbon(part, a, b, radius, _settings.LineSegmentLength, color);
            else AddTube(part, a, b, radius, sides, _settings.LineSegmentLength, color);
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
            var (radius, tube, stretch) = point.Kind switch
            {
                RigPointKind.RiserTop => (0.011f, 0.0022f, 1.5f),
                RigPointKind.Pulley => (0.012f, 0.004f, 1f),
                RigPointKind.Carabiner => (0.035f, 0.0055f, 1.6f),
                _ => (0f, 0f, 0f),
            };
            if (radius <= 0) continue;
            if (_settings.SimpleHardware) AddDiamond(part, point, radius + tube, stretch);
            else AddRing(part, point, Vector3.UnitX, radius, tube, stretch, _settings.HardwareSegments);
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
            // The grip hangs below the loop, toward the pilot's hand.
            var top = toggle.Position - new Vector3(0, 0.02f, 0);
            var bottom = top - new Vector3(0, 0.11f, 0);
            if (_settings.SimpleHardware)
            {
                AddBox(part, (toggle.Position + bottom) / 2, new Vector3(0.014f, 0.075f, 0.014f), toggle.Id);
                continue;
            }
            AddRing(part, toggle, Vector3.UnitX, 0.018f, 0.004f, stretch: 1.2f, _settings.HardwareSegments);
            AddCylinder(part, top, bottom, 0.0135f, Math.Clamp(_settings.HardwareSegments / 2, 6, 12), toggle.Id);
        }
        part.ComputeNormals();
        return part;
    }

    // Rings at the ends and every segment length in between, so the tube follows a sagging, deformed line smoothly.
    private static int Rings(float length, double segmentLength) =>
        segmentLength > 0 ? Math.Clamp((int)(length / segmentLength) + 1, 1, 12) + 1 : 2;

    // A flat strip along the line, facing forward and backward (the lines' material is double-sided).
    private static void AddRibbon(MeshPart part, RigPoint a, RigPoint b, float radius, double segmentLength, Vector4 color)
    {
        var axis = b.Position - a.Position;
        float length = axis.Length();
        if (length < 1e-6f) return;
        axis /= length;
        var across = Vector3.Cross(axis, Vector3.UnitZ);
        across = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.UnitX;
        int rings = Rings(length, segmentLength);
        uint start = (uint)part.VertexCount;
        for (int r = 0; r < rings; r++)
        {
            float f = r / (float)(rings - 1);
            var center = Vector3.Lerp(a.Position, b.Position, f);
            for (int s = 0; s < 2; s++)
            {
                part.AddVertex(center + across * (s == 0 ? -radius : radius), new Vector2(s, f * length), VertexBind.OnRigging(a.Id, b.Id, f), color);
            }
        }
        for (int r = 0; r < rings - 1; r++)
        {
            uint i = start + (uint)(2 * r);
            part.AddQuad(i, i + 1, i + 3, i + 2);
        }
    }

    private static void AddTube(MeshPart part, RigPoint a, RigPoint b, float radius, int sides, double segmentLength, Vector4 color)
    {
        var axis = b.Position - a.Position;
        float length = axis.Length();
        if (length < 1e-6f) return;
        axis /= length;
        var (u, v) = Perpendiculars(axis);
        int rings = Rings(length, segmentLength);
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

    // A low poly stand-in for a ring: an octahedron around the point, stretched along Y like the ring.
    private static void AddDiamond(MeshPart part, RigPoint point, float radius, float stretch)
    {
        var bind = VertexBind.OnRigging(point.Id, point.Id, 0);
        Vector3[] corners =
        [
            new(radius, 0, 0), new(-radius, 0, 0),
            new(0, radius * stretch, 0), new(0, -radius * stretch, 0),
            new(0, 0, radius * 0.5f), new(0, 0, -radius * 0.5f),
        ];
        uint start = (uint)part.VertexCount;
        foreach (var c in corners) part.AddVertex(point.Position + c, new Vector2(0.5f, 0.5f), bind);
        // Faces of the octahedron, counterclockwise seen from outside.
        int[] faces = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
        for (int k = 0; k < faces.Length; k += 3) part.AddTriangle(start + (uint)faces[k], start + (uint)faces[k + 1], start + (uint)faces[k + 2]);
    }

    // A box centered on center with the given half extents, bound to one rigging point.
    private static void AddBox(MeshPart part, Vector3 center, Vector3 half, int pointId)
    {
        var bind = VertexBind.OnRigging(pointId, pointId, 0);
        // Four vertices per face, so the faces stay flat shaded.
        (Vector3 Normal, Vector3 U, Vector3 V)[] faces =
        [
            (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX),
        ];
        foreach (var (n, u, v) in faces)
        {
            uint start = (uint)part.VertexCount;
            for (int k = 0; k < 4; k++)
            {
                float su = k is 1 or 2 ? 1 : -1, sv = k >= 2 ? 1 : -1;
                part.AddVertex(center + (n + u * su + v * sv) * half, new Vector2((su + 1) / 2, (sv + 1) / 2), bind);
            }
            part.AddQuad(start, start + 1, start + 2, start + 3);
        }
    }

    // A ring (torus) around the point, in the plane perpendicular to the normal, stretched along Y into an oval.
    private static void AddRing(MeshPart part, RigPoint point, Vector3 normal, float radius, float tube, float stretch, int segments)
    {
        segments = Math.Clamp(segments, 6, 48);
        int sides = Math.Clamp(segments / 3, 4, 8);
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
