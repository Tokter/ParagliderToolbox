using System.Numerics;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Design;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Proxy;

namespace ParagliderToolbox.Tests.Paraglider;

public class ProxyTests
{
    private static GliderModel Generate(ProxyComplexity complexity) =>
        GliderGenerator.Generate(new GliderDesign { ProxyComplexity = complexity, SpanwiseSegmentsPerCell = 4, ChordwiseSegments = 30 }, new GenerateOptions(-1));

    [Fact]
    public void Complexity_ScalesTheProxy()
    {
        var counts = new[] { ProxyComplexity.Arcade, ProxyComplexity.Low, ProxyComplexity.Medium, ProxyComplexity.High }
            .Select(c => Generate(c).Proxy.Model.Nodes.Count)
            .ToList();
        for (int i = 1; i < counts.Count; i++) Assert.True(counts[i] > counts[i - 1], string.Join(", ", counts));
        Assert.InRange(counts[0], 20, 150);
    }

    [Theory]
    [InlineData(ProxyComplexity.Arcade)]
    [InlineData(ProxyComplexity.Medium)]
    [InlineData(ProxyComplexity.High)]
    public void Proxy_IsConsistent(ProxyComplexity complexity)
    {
        var proxy = Generate(complexity).Proxy.Model;

        Assert.All(proxy.Nodes, (n, i) => Assert.Equal(i, n.Id));
        Assert.All(proxy.Nodes, n => Assert.True(n.Mass > 0));
        Assert.All(proxy.Constraints, c =>
        {
            Assert.InRange(c.A, 0, proxy.Nodes.Count - 1);
            Assert.InRange(c.B, 0, proxy.Nodes.Count - 1);
            Assert.True(c.RestLength > 0);
        });
        Assert.Equal(ProxyNodeKind.Pilot, proxy.Nodes[proxy.Pilot].Kind);
        Assert.InRange(proxy.Nodes.Sum(n => n.Mass), 85 + 4.8, 85 + 7);
        foreach (string control in new[] { "BrakeLeft", "BrakeRight", "SpeedBar", "CollapseLeft", "CollapseRight", "Frontal" })
        {
            var c = proxy.FindControl(control);
            Assert.NotNull(c);
            Assert.NotEmpty(c.Constraints);
        }
        // Every canopy node hangs on something: no node without constraints.
        var used = proxy.Constraints.SelectMany(c => new[] { c.A, c.B }).ToHashSet();
        Assert.All(proxy.Nodes, n => Assert.Contains(n.Id, used));
    }

    [Fact]
    public void PressureCells_AreClosedAndFaceOutward()
    {
        var proxy = Generate(ProxyComplexity.Medium).Proxy.Model;
        foreach (var strip in proxy.Strips)
        {
            // Open at the inner rib walls, so measured from the strip's center (inside the cell).
            var center = strip.Surface.Aggregate(Vector3.Zero, (s, i) => s + proxy.Nodes[i].Position) / strip.Surface.Count;
            double volume = 0;
            for (int t = 0; t < strip.Surface.Count; t += 3)
            {
                var a = proxy.Nodes[strip.Surface[t]].Position - center;
                var b = proxy.Nodes[strip.Surface[t + 1]].Position - center;
                var c = proxy.Nodes[strip.Surface[t + 2]].Position - center;
                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6;
            }
            Assert.True(volume > 0, $"strip {strip.SectionA}: volume {volume}");
        }
    }

    [Fact]
    public void Proxy_JsonRoundTrips()
    {
        var proxy = Generate(ProxyComplexity.Low).Proxy.Model;
        var copy = ProxyModel.FromJson(proxy.ToJson());

        Assert.Equal(proxy.Nodes.Count, copy.Nodes.Count);
        Assert.True(Vector3.Distance(proxy.Nodes[5].Position, copy.Nodes[5].Position) < 1e-4f);
        Assert.Equal(proxy.Constraints.Count, copy.Constraints.Count);
        Assert.Equal(proxy.Polar.Lift.Count, copy.Polar.Lift.Count);
        Assert.Contains("\"kind\": \"upper\"", proxy.ToJson());
    }

    [Fact]
    public void Skin_WeightsSumToOne_AndTheRestPoseDoesNotMoveTheMesh()
    {
        var model = Generate(ProxyComplexity.Medium);
        var deformer = new ProxyDeformer(model.Proxy.Model);
        var rest = model.Proxy.Model.Nodes.Select(n => n.Position).ToArray();
        var skin = new Matrix4x4[rest.Length];
        deformer.ComputeSkinMatrices(rest, skin);

        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            var weights = model.Skin[p];
            Assert.All(weights, w => Assert.Equal(1, w.W0 + w.W1 + w.W2 + w.W3, 4));
            var positions = new Vector3[part.VertexCount];
            var normals = new Vector3[part.VertexCount];
            ProxyDeformer.Deform(part.Positions, part.Normals, weights, skin, positions, normals);
            for (int i = 0; i < positions.Length; i += 97) Assert.True(Vector3.Distance(positions[i], part.Positions[i]) < 1e-3f);
        }
    }

    [Fact]
    public void Skin_CarriesTheMeshAlong_WhenTheProxyMovesRigidly()
    {
        var model = Generate(ProxyComplexity.Low);
        var deformer = new ProxyDeformer(model.Proxy.Model);
        var rotation = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(3, 1, -2);
        var moved = model.Proxy.Model.Nodes.Select(n => Vector3.Transform(n.Position, rotation)).ToArray();
        var skin = new Matrix4x4[moved.Length];
        deformer.ComputeSkinMatrices(moved, skin);

        var canopy = model.Part(GliderMaterial.Canopy)!;
        var weights = model.Skin[0];
        var positions = new Vector3[canopy.VertexCount];
        var normals = new Vector3[canopy.VertexCount];
        ProxyDeformer.Deform(canopy.Positions, canopy.Normals, weights, skin, positions, normals);
        for (int i = 0; i < positions.Length; i += 53)
        {
            Assert.True(Vector3.Distance(positions[i], Vector3.Transform(canopy.Positions[i], rotation)) < 2e-3f);
            Assert.True(Vector3.Dot(normals[i], Vector3.TransformNormal(canopy.Normals[i], rotation)) > 0.99f);
        }
    }
}
