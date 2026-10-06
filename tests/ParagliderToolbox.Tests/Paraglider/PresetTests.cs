using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Export;
using ParagliderToolbox.Paraglider.Rigging;
using Xunit.Abstractions;

namespace ParagliderToolbox.Tests.Paraglider;

public class PresetTests(ITestOutputHelper output)
{
    public static TheoryData<WingClass, MeshDetail> ClassesAndDetails()
    {
        var data = new TheoryData<WingClass, MeshDetail>();
        foreach (var c in GliderPresets.Classes)
        {
            foreach (var d in new[] { MeshDetail.LowPoly, MeshDetail.Medium, MeshDetail.High }) data.Add(c, d);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ClassesAndDetails))]
    public void EveryClassAndDetail_GeneratesAValidModel_WithinItsTriangleBudget(WingClass wingClass, MeshDetail detail)
    {
        var model = GliderGenerator.Generate(GliderPresets.Create(wingClass, detail), new GenerateOptions(-1));
        output.WriteLine($"{wingClass} {detail}: {model.TriangleCount} triangles, proxy {model.Proxy.Model.Nodes.Count} nodes");
        var (min, max) = detail switch
        {
            MeshDetail.LowPoly => (300, 1000),
            MeshDetail.Medium => (10_000, 45_000),
            _ => (150_000, 500_000),
        };
        Assert.InRange(model.TriangleCount, min, max);

        int joints = model.Proxy.Model.Nodes.Count;
        Assert.True(joints > 20);
        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            Assert.Equal(part.VertexCount, model.Skin[p].Length);
            Assert.Equal(part.VertexCount, part.TexCoords.Count);
            Assert.All(part.Indices, i => Assert.True(i < part.VertexCount));
            Assert.All(part.Positions, v => Assert.False(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)));
            Assert.All(model.Skin[p], w =>
            {
                Assert.InRange(w.W0 + w.W1 + w.W2 + w.W3, 0.999f, 1.001f);
                Assert.True(w.J0 < joints && w.J1 < joints && w.J2 < joints && w.J3 < joints);
            });
        }
        if (detail != MeshDetail.High)
        {
            byte[] glb = GliderExporter.ToGlb(model);
            Assert.Equal("glTF"u8.ToArray(), glb[..4]);
        }
    }

    [Fact]
    public void HighDetail_IsTheDefaultMesh()
    {
        int defaults = GliderGenerator.Generate(new GliderDesign(), new GenerateOptions(-1)).TriangleCount;
        int high = GliderGenerator.Generate(new GliderDesign { MeshDetail = MeshDetail.High, SpanwiseSegmentsPerCell = 2 }, new GenerateOptions(-1)).TriangleCount;
        Assert.Equal(defaults, high);
    }

    [Fact]
    public void CustomDetail_UsesTheExplicitSettings_AndPresetsOverrideThem()
    {
        var design = new GliderDesign { SpanwiseSegmentsPerCell = 3, ChordwiseSegments = 20, LineSides = 2, HardwareSegments = 0 };
        Assert.Equal(MeshDetail.Custom, design.MeshDetail);
        var custom = MeshSettings.FromDesign(design);
        Assert.Equal((3, 20, 2), (custom.SpanwiseSegmentsPerCell, custom.ChordwiseSegments, custom.LineSides));
        Assert.True(custom.SimpleHardware);

        design.MeshDetail = MeshDetail.High;
        Assert.Equal(110, MeshSettings.FromDesign(design).ChordwiseSegments);
        design.MeshDetail = MeshDetail.LowPoly;
        Assert.True(MeshSettings.FromDesign(design).IsLowPoly);
    }

    [Theory]
    [InlineData(MeshDetail.LowPoly)]
    [InlineData(MeshDetail.Medium)]
    public void Presets_WriteTheirDetailIntoTheMeshSettings(MeshDetail detail)
    {
        var design = GliderPresets.Create(WingClass.EnC, detail);
        Assert.Equal(detail, design.MeshDetail);
        var copy = design.Clone();
        copy.MeshDetail = MeshDetail.Custom;
        Assert.Equal(MeshSettings.FromDesign(design), MeshSettings.FromDesign(copy));
    }

    [Fact]
    public void Classes_GrowInAspectRatioAndCells_FromEnAToEnD()
    {
        var designs = GliderPresets.Classes.Select(c => GliderPresets.Create(c)).ToList();
        for (int i = 1; i < designs.Count; i++)
        {
            Assert.True(designs[i].FlatAspectRatio > designs[i - 1].FlatAspectRatio);
            Assert.True(designs[i].CellCount > designs[i - 1].CellCount);
            Assert.True(designs[i].BrakeTravel <= designs[i - 1].BrakeTravel);
            Assert.True(designs[i].MainLineDiameter < designs[i - 1].MainLineDiameter);
            Assert.True(designs[i].PilotDragArea <= designs[i - 1].PilotDragArea);
        }
        Assert.Equal([3, 3, 3, 2, 2], designs.Select(d => d.RowPositions.Length));
        Assert.All(GliderPresets.Classes, c => Assert.False(string.IsNullOrWhiteSpace(GliderPresets.Info(c).Description)));
        // Sized for about 85–100 kg all up.
        Assert.All(designs, d => Assert.InRange(d.FlatArea, 21, 27));
    }

    [Fact]
    public void TwoLiners_HaveOnlyAAndBRisersAndLines()
    {
        var model = GliderGenerator.Generate(GliderPresets.Create(WingClass.EnD, MeshDetail.LowPoly), new GenerateOptions(-1));

        Assert.Equal(2, model.Rigging.Points.Count(p => p.Kind == RigPointKind.RiserTop && p.Side == 1));
        Assert.All(model.Rigging.Lines, l => Assert.True(l.Row is 0 or 1 or RiggingLayout.BrakeRow));
        string plan = GliderExporter.ToLinePlanCsv(model.Rigging);
        Assert.Contains(",A,", plan);
        Assert.Contains(",B,", plan);
        Assert.DoesNotContain(",C,", plan);
        // The speed bar still shortens the A risers.
        Assert.NotEmpty(model.Proxy.Model.FindControl("SpeedBar")!.Constraints);
    }
}
