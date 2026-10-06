using System.Numerics;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Mathematics;
using ParagliderToolbox.Paraglider.Rigging;

namespace ParagliderToolbox.Tests.Paraglider;

public class CurveTests
{
    [Fact]
    public void Curve_PassesThroughItsPoints_AndDoesNotOvershoot()
    {
        var curve = GliderDesign.DefaultChord;

        foreach (var p in curve.Points) Assert.Equal(p.Y, curve.Evaluate(p.X), 9);
        double previous = double.MaxValue;
        for (int i = 0; i <= 200; i++)
        {
            double y = curve.Evaluate(i / 200.0);
            Assert.True(y <= previous + 1e-12, $"rises at {i / 200.0}");
            previous = y;
        }
    }

    [Fact]
    public void Curve_TextRoundTrips()
    {
        var curve = new Curve(0, 1, 0.5, 0.8, 1, 0.25);
        Assert.Equal(curve, Curve.Parse(curve.ToString()));
        Assert.Throws<FormatException>(() => Curve.Parse("0:1; nonsense"));
    }
}

public class AirfoilTests
{
    [Theory]
    [InlineData(0.2)]
    [InlineData(0.3)]
    [InlineData(0.45)]
    public void ParametricThickness_PeaksAtItsPosition_WithAClosedTrailingEdge(double position)
    {
        var airfoil = Airfoil.Parametric(position, 6, 0.03, 0.3, 0.005);

        Assert.Equal(0.5, airfoil.HalfThickness(position), 3);
        Assert.True(airfoil.HalfThickness(position - 0.05) < 0.5);
        Assert.True(airfoil.HalfThickness(position + 0.05) < 0.5);
        Assert.Equal(0, airfoil.HalfThickness(1), 4);
        Assert.Equal(0, airfoil.Camber(0), 9);
        Assert.Equal(0, airfoil.Camber(1), 9);
    }

    [Fact]
    public void Reflex_BendsTheTailOfTheCamberLineUp()
    {
        var plain = Airfoil.Parametric(0.22, 6, 0.03, 0.3, 0);
        var reflexed = Airfoil.Parametric(0.22, 6, 0.03, 0.3, 0.01);

        Assert.True(reflexed.Camber(0.85) < plain.Camber(0.85));
        Assert.Equal(plain.Camber(0.3), reflexed.Camber(0.3), 9);
    }

    [Fact]
    public void DatImport_ReadsSeligCoordinates_AndNormalizesThickness()
    {
        // NACA 2412 from its formula, in Selig order (upper TE -> LE -> lower TE).
        var lines = new List<string> { "NACA 2412" };
        var upper = new List<(double, double)>();
        var lower = new List<(double, double)>();
        for (int i = 0; i <= 40; i++)
        {
            double x = 0.5 * (1 - Math.Cos(Math.PI * i / 40));
            double t = 0.12 / 0.2 * (0.2969 * Math.Sqrt(x) - 0.126 * x - 0.3516 * x * x + 0.2843 * x * x * x - 0.1036 * x * x * x * x);
            double c = x < 0.4 ? 0.02 / 0.16 * (0.8 * x - x * x) : 0.02 / 0.36 * (0.2 + 0.8 * x - x * x);
            upper.Add((x, c + t));
            lower.Add((x, c - t));
        }
        foreach (var (x, y) in Enumerable.Reverse(upper)) lines.Add(FormattableString.Invariant($"{x:F6} {y:F6}"));
        foreach (var (x, y) in lower.Skip(1)) lines.Add(FormattableString.Invariant($"{x:F6} {y:F6}"));

        var airfoil = Airfoil.FromDat(string.Join('\n', lines));

        Assert.Equal("NACA 2412", airfoil.Name);
        Assert.InRange(airfoil.HalfThickness(0.3), 0.49, 0.501);
        Assert.InRange(airfoil.Camber(0.4), 0.019, 0.021);
        Assert.Throws<FormatException>(() => Airfoil.FromDat("only\n0 0\n1 0"));
    }
}

public class ShapeTests
{
    private static readonly GliderShape Shape = new(new GliderDesign());

    [Fact]
    public void Planform_HasTheDesignsAreaAndSpan()
    {
        var design = Shape.Design;
        double area = 0;
        const int n = 2000;
        for (int i = 0; i < n; i++) area += Shape.Chord(-1 + 2 * (i + 0.5) / n) * (2.0 / n) * Shape.HalfSpan;

        Assert.Equal(design.FlatArea, area, 2);
        Assert.Equal(Math.Sqrt(5.5 * 24), design.FlatSpan, 6);
    }

    [Fact]
    public void Arc_IsSolvedForTheProjectedSpanRatio()
    {
        Assert.Equal(Shape.Design.ProjectedSpanRatio, Shape.ProjectedSpan / Shape.Design.FlatSpan, 3);
        Assert.InRange(Shape.ProjectedAspectRatio, 3.8, 4.6);
    }

    [Fact]
    public void Ribs_AreSymmetric_WithACenterRibForAnEvenCellCount()
    {
        var ribs = Shape.RibPositions;
        Assert.Equal(53, ribs.Length);
        Assert.Equal(-1, ribs[0]);
        Assert.Equal(1, ribs[^1]);
        Assert.Equal(0, ribs[26]);
        for (int i = 0; i < ribs.Length; i++) Assert.Equal(-ribs[i], ribs[^(i + 1)], 9);
        // Cells get narrower toward the tips.
        Assert.True(ribs[27] - ribs[26] > ribs[52] - ribs[51]);
    }

    [Fact]
    public void Canopy_FliesAboveTheCarabiners_AtTheLineLength()
    {
        var center = Shape.SurfacePoint(0, -0.3);
        Assert.InRange(center.Y, 7.2, 8.4);
        Assert.InRange(Math.Abs(center.X), 0, 1e-4);
        // The left tip is on +X (glTF: +X is the pilot's left), drooping below the center.
        var tip = Shape.SurfacePoint(1, 0);
        Assert.True(tip.X > 3);
        Assert.True(tip.Y < center.Y - 1.5);
        // The nose points forward (+Z).
        Assert.True(Shape.SurfacePoint(0, 0).Z > Shape.SurfacePoint(0, 1).Z);
    }
}

public class CanopyMeshTests
{
    private static readonly Lazy<GliderModel> Model = new(() =>
        GliderGenerator.Generate(new GliderDesign { SpanwiseSegmentsPerCell = 6, ChordwiseSegments = 50 }, new GenerateOptions(TextureSize: -1)));

    [Fact]
    public void Skin_FacesOutward()
    {
        var canopy = Model.Value.Part(GliderMaterial.Canopy)!;
        // The vertex nearest the top of the center cell: its normal points up.
        int top = Enumerable.Range(0, canopy.VertexCount)
            .Where(i => Math.Abs(canopy.Positions[i].X) < 0.15f)
            .MaxBy(i => canopy.Positions[i].Y);
        Assert.True(canopy.Normals[top].Y > 0.8f, $"normal {canopy.Normals[top]}");
        // And one at the bottom of the center cell points down.
        int bottom = Enumerable.Range(0, canopy.VertexCount)
            .Where(i => Math.Abs(canopy.Positions[i].X) < 0.15f && canopy.Binds[i].C == 0 && canopy.Binds[i].B > 0.4f && canopy.Binds[i].B < 0.6f)
            .MinBy(i => canopy.Positions[i].Y);
        Assert.True(canopy.Normals[bottom].Y < -0.8f, $"normal {canopy.Normals[bottom]}");
    }

    [Fact]
    public void Ballooning_BulgesTheSkinBetweenTheRibs()
    {
        var shape = Model.Value.Shape;
        var canopy = Model.Value.Part(GliderMaterial.Canopy)!;
        double ribEta = shape.RibPositions[26], nextEta = shape.RibPositions[27];
        double midEta = (ribEta + nextEta) / 2;
        // Upper surface at 40% chord: the skin mid-cell lies outside the smooth surface, the rib seam on it.
        Vector3 Nearest(double eta) => canopy.Positions[Enumerable.Range(0, canopy.VertexCount)
            .Where(i => canopy.Binds[i].C == 1)
            .MinBy(i => Math.Abs(canopy.Binds[i].A - eta) * 10 + Math.Abs(canopy.Binds[i].B - 0.4))];
        double t = GliderShape.ProfileParameter(0.4, upper: true);
        var smoothMid = shape.SurfacePoint(midEta, t);
        var skinMid = Nearest(midEta);
        double width = Vector3.Distance(shape.SurfacePoint(ribEta, t), shape.SurfacePoint(nextEta, t));
        double bulge = Vector3.Distance(skinMid, smoothMid);
        Assert.InRange(bulge, 0.3 * shape.Design.Ballooning * width, 2.0 * shape.Design.Ballooning * width);
    }

    [Fact]
    public void Inlets_LeaveHolesInTheSkin_ExceptAtTheTips()
    {
        var closed = GliderGenerator.Generate(new GliderDesign { SpanwiseSegmentsPerCell = 6, ChordwiseSegments = 50, InletEnd = 0.0051, InletStart = 0.005 }, new GenerateOptions(-1));
        Assert.True(Model.Value.Part(GliderMaterial.Canopy)!.TriangleCount < closed.Part(GliderMaterial.Canopy)!.TriangleCount);
    }

    [Fact]
    public void Model_HasEveryPart()
    {
        var parts = Model.Value.Parts.Select(p => p.Material).ToList();
        Assert.Equal([GliderMaterial.Canopy, GliderMaterial.Ribs, GliderMaterial.Lines, GliderMaterial.Risers, GliderMaterial.Metal, GliderMaterial.Toggles], parts);
        Assert.All(Model.Value.Parts, p =>
        {
            Assert.Equal(p.VertexCount, p.Normals.Count);
            Assert.Equal(p.VertexCount, p.TexCoords.Count);
            Assert.All(p.Indices, i => Assert.True(i < p.VertexCount));
            Assert.All(p.Positions, v => Assert.False(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)));
        });
    }
}

public class RiggingTests
{
    private static readonly GliderShape Shape = new(new GliderDesign());
    private static readonly RiggingLayout Layout = RiggingLayout.Build(Shape);

    [Fact]
    public void Tabs_SitOnEveryNthRibFromTheTips_OnEveryRow()
    {
        int tabs = Layout.Points.Count(p => p.Kind == RigPointKind.Tab);
        Assert.Equal(Layout.TabRibs.Count * 3, tabs);
        Assert.Contains(0, Layout.TabRibs);
        Assert.Contains(52, Layout.TabRibs);
        Assert.DoesNotContain(26, Layout.TabRibs); // the center rib belongs to neither side
    }

    [Fact]
    public void EveryTab_LeadsDownToItsRiserOrPulley()
    {
        foreach (var tab in Layout.Points.Where(p => p.Kind is RigPointKind.Tab or RigPointKind.BrakeTab))
        {
            int point = tab.Id;
            for (int guard = 0; guard < 10; guard++)
            {
                var line = Layout.Lines.FirstOrDefault(l => l.Upper == point);
                if (line is null) break;
                point = line.Lower;
            }
            var end = Layout.Points[point];
            Assert.Contains(end.Kind, new[] { RigPointKind.Carabiner, RigPointKind.Toggle });
            Assert.Equal(tab.Side, end.Side);
        }
    }

    [Fact]
    public void Lines_HavePlausibleLengths()
    {
        double longest = Layout.Lines.Where(l => l.Level != LineLevel.Riser).Max(l => l.Length);
        Assert.InRange(longest, 2.0, 6.5);
        double totalA = Layout.Lines.Where(l => l.Row == 0 && l.Level != LineLevel.Riser).Sum(l => l.Length);
        Assert.InRange(totalA, 30, 160);
    }
}
