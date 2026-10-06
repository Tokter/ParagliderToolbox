using System.Numerics;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Rigging;

namespace ParagliderToolbox.Paraglider.Proxy;

/// <summary>Up to four proxy nodes (joints) a vertex follows, with weights summing to 1.</summary>
public readonly record struct SkinWeights(int J0, int J1, int J2, int J3, float W0, float W1, float W2, float W3)
{
    /// <summary>Builds the weights from node/weight pairs, keeping the four largest and normalizing them.</summary>
    public static SkinWeights From(Dictionary<int, double> weights)
    {
        var top = weights.Where(p => p.Value > 1e-6).OrderByDescending(p => p.Value).Take(4).ToArray();
        double sum = top.Sum(p => p.Value);
        if (top.Length == 0 || sum <= 0) return new SkinWeights(0, 0, 0, 0, 1, 0, 0, 0);
        int J(int i) => i < top.Length ? top[i].Key : 0; // unused slots: joint 0 with weight 0, as glTF expects
        float W(int i) => i < top.Length ? (float)(top[i].Value / sum) : 0f;
        return new SkinWeights(J(0), J(1), J(2), J(3), W(0), W(1), W(2), W(3));
    }
}

/// <summary>
/// Binds the high resolution mesh to the proxy nodes: canopy points follow the nodes of the two sections and two chord
/// stations around them (bilinear in span and chord, blended between the upper and lower surface by height); rigging
/// points follow their proxy node, or, when the proxy has none (tabs off the sections, knots of a proxy without
/// cascades), the nodes their position is interpolated from.
/// </summary>
public sealed class SkinBinder
{
    private readonly ProxyBuild _build;
    private readonly RiggingLayout _rigging;
    private readonly Dictionary<int, Dictionary<int, double>> _rigWeights = [];

    /// <summary>Initializes a binder for a proxy and the rigging it was built from.</summary>
    public SkinBinder(ProxyBuild build, RiggingLayout rigging)
    {
        _build = build;
        _rigging = rigging;
    }

    /// <summary>Gets the weights of a vertex.</summary>
    public SkinWeights Bind(VertexBind bind) => SkinWeights.From(bind.Kind == BindKind.Canopy
        ? CanopyWeights(bind.A, bind.B, bind.C)
        : RiggingWeights(bind.PointA, bind.PointB, bind.A));

    /// <summary>Binds every vertex of <paramref name="part"/>.</summary>
    public SkinWeights[] Bind(MeshPart part)
    {
        var result = new SkinWeights[part.VertexCount];
        Parallel.For(0, result.Length, i => result[i] = Bind(part.Binds[i]));
        return result;
    }

    private Dictionary<int, double> CanopyWeights(double eta, double x, double h)
    {
        var sections = _build.Model.Sections;
        var weights = new Dictionary<int, double>();
        int k = 0;
        while (k < sections.Count - 2 && sections[k + 1].Eta <= eta) k++;
        double span = sections[k + 1].Eta - sections[k].Eta;
        double f = span > 0 ? Math.Clamp((eta - sections[k].Eta) / span, 0, 1) : 0;
        AddSection(weights, sections[k], x, h, 1 - f);
        AddSection(weights, sections[k + 1], x, h, f);
        return weights;
    }

    private static void AddSection(Dictionary<int, double> weights, ProxySection section, double x, double h, double scale)
    {
        if (scale <= 0) return;
        if (section.Upper.Count > 0)
        {
            AddChain(weights, ProxyBuilder.Chain(section, true), section.Stations, x, scale * h);
            AddChain(weights, ProxyBuilder.Chain(section, false), section.Stations, x, scale * (1 - h));
        }
        else
        {
            AddChain(weights, ProxyBuilder.Chain(section, false), section.Stations, x, scale);
        }
    }

    private static void AddChain(Dictionary<int, double> weights, List<int> chain, List<float> stations, double x, double scale)
    {
        if (scale <= 0) return;
        // Chain positions: 0, stations..., 1.
        int count = chain.Count;
        double Station(int i) => i == 0 ? 0 : i == count - 1 ? 1 : stations[i - 1];
        int k = 0;
        while (k < count - 2 && Station(k + 1) <= x) k++;
        double a = Station(k), b = Station(k + 1);
        double f = b > a ? Math.Clamp((x - a) / (b - a), 0, 1) : 0;
        Add(weights, chain[k], scale * (1 - f));
        Add(weights, chain[k + 1], scale * f);
    }

    private Dictionary<int, double> RiggingWeights(int pointA, int pointB, double fraction)
    {
        var weights = new Dictionary<int, double>();
        foreach (var (node, w) in RigPointWeights(pointA)) Add(weights, node, w * (1 - fraction));
        foreach (var (node, w) in RigPointWeights(pointB)) Add(weights, node, w * fraction);
        return weights;
    }

    private Dictionary<int, double> RigPointWeights(int pointId)
    {
        lock (_rigWeights)
        {
            if (_rigWeights.TryGetValue(pointId, out var cached)) return cached;
        }
        var weights = new Dictionary<int, double>();
        var point = _rigging.Points[pointId];
        if (_build.RigPointNodes.TryGetValue(pointId, out int node))
        {
            weights[node] = 1;
        }
        else if (point.Kind is RigPointKind.Tab or RigPointKind.BrakeTab)
        {
            weights = CanopyWeights(point.Eta, point.Chord, 0);
        }
        else if (point.Kind == RigPointKind.Knot)
        {
            // Between the tabs above it and the anchor below, where the layout put it.
            var tabs = LeafTabs(pointId);
            var anchor = Anchor(pointId);
            var center = Vector3.Zero;
            foreach (int t in tabs) center += _rigging.Points[t].Position;
            center /= Math.Max(1, tabs.Count);
            double total = Vector3.Distance(center, _rigging.Points[anchor].Position);
            double f = total > 0 ? Vector3.Distance(center, point.Position) / total : 0;
            foreach (int t in tabs)
            {
                foreach (var (n, w) in RigPointWeights(t)) Add(weights, n, w * (1 - f) / tabs.Count);
            }
            foreach (var (n, w) in RigPointWeights(anchor)) Add(weights, n, w * f);
        }
        lock (_rigWeights) _rigWeights[pointId] = weights;
        return weights;
    }

    private List<int> LeafTabs(int pointId)
    {
        var result = new List<int>();
        foreach (var line in _rigging.Lines.Where(l => l.Lower == pointId))
        {
            var upper = _rigging.Points[line.Upper];
            if (upper.Kind is RigPointKind.Tab or RigPointKind.BrakeTab) result.Add(upper.Id);
            else result.AddRange(LeafTabs(upper.Id));
        }
        return result;
    }

    private int Anchor(int pointId)
    {
        var line = _rigging.Lines.FirstOrDefault(l => l.Upper == pointId);
        if (line is null) return pointId;
        var lower = _rigging.Points[line.Lower];
        return lower.Kind == RigPointKind.Knot ? Anchor(lower.Id) : lower.Id;
    }

    private static void Add(Dictionary<int, double> weights, int node, double weight)
    {
        if (weight <= 0) return;
        weights[node] = weights.GetValueOrDefault(node) + weight;
    }
}

/// <summary>
/// Moves the high resolution mesh with the proxy: every node carries a frame (its position, and for canopy nodes the
/// directions to its chord and span neighbors), and each vertex follows its nodes' frames from the rest pose to the
/// current pose (linear blend skinning, as in the glTF skin export).
/// </summary>
public sealed class ProxyDeformer
{
    private readonly ProxyModel _model;
    private readonly (int ChordPrev, int ChordNext, int SpanPrev, int SpanNext)[] _neighbors;
    private readonly Matrix4x4[] _restInverse;

    /// <summary>Initializes a deformer for <paramref name="model"/>, its rest pose being the nodes' positions.</summary>
    public ProxyDeformer(ProxyModel model)
    {
        _model = model;
        _neighbors = FindNeighbors(model);
        var rest = model.Nodes.Select(n => n.Position).ToArray();
        _restInverse = new Matrix4x4[rest.Length];
        RestFrames = new Matrix4x4[rest.Length];
        for (int i = 0; i < rest.Length; i++)
        {
            RestFrames[i] = Frame(i, rest);
            Matrix4x4.Invert(RestFrames[i], out _restInverse[i]);
        }
    }

    /// <summary>Gets the nodes' frames in the rest pose (rows: axes, then position), the glTF joints' bind pose.</summary>
    public Matrix4x4[] RestFrames { get; }

    /// <summary>Gets the frame of node <paramref name="i"/> for the node positions <paramref name="positions"/>.</summary>
    public Matrix4x4 Frame(int i, ReadOnlySpan<Vector3> positions)
    {
        var p = positions[i];
        var (cp, cn, sp, sn) = _neighbors[i];
        if (cp < 0 && cn < 0) return Matrix4x4.CreateTranslation(p);
        var chord = positions[cn >= 0 ? cn : i] - positions[cp >= 0 ? cp : i];
        var span = positions[sn >= 0 ? sn : i] - positions[sp >= 0 ? sp : i];
        if (chord.LengthSquared() < 1e-12f || span.LengthSquared() < 1e-12f) return Matrix4x4.CreateTranslation(p);
        var x = Vector3.Normalize(chord);
        var z = Vector3.Cross(x, span);
        if (z.LengthSquared() < 1e-12f) return Matrix4x4.CreateTranslation(p);
        z = Vector3.Normalize(z);
        var y = Vector3.Cross(z, x);
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            p.X, p.Y, p.Z, 1);
    }

    /// <summary>Computes the skinning matrix of every node: from the rest pose to <paramref name="positions"/>.</summary>
    public void ComputeSkinMatrices(ReadOnlySpan<Vector3> positions, Span<Matrix4x4> result)
    {
        for (int i = 0; i < _restInverse.Length; i++) result[i] = _restInverse[i] * Frame(i, positions);
    }

    /// <summary>Deforms the vertices of a part with its weights and the skinning matrices.</summary>
    public static void Deform(IReadOnlyList<Vector3> restPositions, IReadOnlyList<Vector3> restNormals, SkinWeights[] weights,
        Matrix4x4[] skin, Vector3[] positions, Vector3[] normals)
    {
        Parallel.For(0, restPositions.Count, i =>
        {
            var w = weights[i];
            var p = restPositions[i];
            var n = i < restNormals.Count ? restNormals[i] : Vector3.UnitY;
            var pos = Vector3.Transform(p, skin[w.J0]) * w.W0;
            var nor = Vector3.TransformNormal(n, skin[w.J0]) * w.W0;
            if (w.W1 > 0) { pos += Vector3.Transform(p, skin[w.J1]) * w.W1; nor += Vector3.TransformNormal(n, skin[w.J1]) * w.W1; }
            if (w.W2 > 0) { pos += Vector3.Transform(p, skin[w.J2]) * w.W2; nor += Vector3.TransformNormal(n, skin[w.J2]) * w.W2; }
            if (w.W3 > 0) { pos += Vector3.Transform(p, skin[w.J3]) * w.W3; nor += Vector3.TransformNormal(n, skin[w.J3]) * w.W3; }
            positions[i] = pos;
            float length = nor.Length();
            normals[i] = length > 1e-9f ? nor / length : n;
        });
    }

    private static (int, int, int, int)[] FindNeighbors(ProxyModel model)
    {
        var result = Enumerable.Repeat((-1, -1, -1, -1), model.Nodes.Count).ToArray();
        var sections = model.Sections;
        for (int s = 0; s < sections.Count; s++)
        {
            var prev = s > 0 ? sections[s - 1] : null;
            var next = s + 1 < sections.Count ? sections[s + 1] : null;
            foreach (bool upper in sections[s].Upper.Count > 0 ? new[] { true, false } : new[] { false })
            {
                var chain = ProxyBuilder.Chain(sections[s], upper);
                var prevChain = prev != null ? ProxyBuilder.Chain(prev, upper) : null;
                var nextChain = next != null ? ProxyBuilder.Chain(next, upper) : null;
                for (int k = 0; k < chain.Count; k++)
                {
                    int node = chain[k];
                    if (upper == false && (k == 0 || k == chain.Count - 1) && result[node].Item1 >= 0) continue; // nose/tail set by the upper chain
                    int cp = k > 0 ? chain[k - 1] : -1;
                    int cn = k + 1 < chain.Count ? chain[k + 1] : -1;
                    result[node] = (cp, cn, prevChain?[k] ?? -1, nextChain?[k] ?? -1);
                }
            }
        }
        return result;
    }
}
