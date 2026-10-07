using System.Numerics;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Paraglider;
using ParagliderToolbox.Paraglider.Geometry;
using ParagliderToolbox.Paraglider.Proxy;
using ParagliderToolbox.Paraglider.Texturing;
using SkiaSharp;

namespace ParagliderToolbox.Modules.Paraglider;

/// <summary>
/// The 3D scene of a generated paraglider: its high resolution parts with their materials, skinned to the physics proxy,
/// and the proxy itself as lines and points. Shows the model at rest or posed by the proxy's node positions; the live
/// preview and the replay of recorded flights share it.
/// </summary>
public sealed class GliderScene
{
    private readonly Dictionary<GliderMaterial, Material3D> _materials = [];
    private readonly List<(MeshPart Part, Mesh3D Mesh, MeshInstance3D Instance, SkinWeights[] Weights)> _parts = [];
    private readonly Mesh3D _proxyLines = new(PrimitiveTopology.Lines);
    private readonly Mesh3D _proxyPoints = new(PrimitiveTopology.Points);
    private readonly MeshInstance3D _proxyLinesInstance;
    private readonly MeshInstance3D _proxyPointsInstance;
    private ProxyDeformer? _deformer;
    private Matrix4x4[] _skin = [];
    private Vector3[] _positionBuffer = [];
    private Vector3[] _normalBuffer = [];
    private bool _showCanopy = true, _showRibs = true, _showRigging = true, _showProxy;

    /// <summary>Initializes an empty scene.</summary>
    public GliderScene()
    {
        _proxyLinesInstance = new MeshInstance3D(_proxyLines, new Material3D { BaseColor = Color.FromRgb(255, 196, 0), Unlit = true }) { Name = "Proxy", DrawOnTop = true, IsVisible = false };
        _proxyPointsInstance = new MeshInstance3D(_proxyPoints, new Material3D { BaseColor = Color.FromRgb(255, 112, 67), Unlit = true, PointSize = 5 }) { Name = "Proxy nodes", DrawOnTop = true, IsVisible = false };
        Scene.Instances.Add(_proxyLinesInstance);
        Scene.Instances.Add(_proxyPointsInstance);
    }

    /// <summary>Gets the scene a viewport shows.</summary>
    public Scene3D Scene { get; } = new();

    /// <summary>Gets the model shown, or null before the first.</summary>
    public GliderModel? Model { get; private set; }

    /// <summary>Gets or sets whether the canopy (and its ribs) is shown.</summary>
    public bool ShowCanopy { get => _showCanopy; set { _showCanopy = value; UpdateVisibility(); } }

    /// <summary>Gets or sets whether the internal ribs are shown (with the canopy).</summary>
    public bool ShowRibs { get => _showRibs; set { _showRibs = value; UpdateVisibility(); } }

    /// <summary>Gets or sets whether the lines, risers, hardware and toggles are shown.</summary>
    public bool ShowRigging { get => _showRigging; set { _showRigging = value; UpdateVisibility(); } }

    /// <summary>Gets or sets whether the physics proxy is shown (drawn on top).</summary>
    public bool ShowProxy { get => _showProxy; set { _showProxy = value; UpdateVisibility(); } }

    /// <summary>Adds an instance drawn over the model (e.g. force arrows).</summary>
    public void AddOverlay(MeshInstance3D instance) => Scene.Instances.Add(instance);

    /// <summary>Shows <paramref name="model"/> at rest (reusing the meshes when the part list is the same).</summary>
    public void Show(GliderModel model)
    {
        Model = model;
        UpdateMaterials(model);
        while (_parts.Count > model.Parts.Count)
        {
            Scene.Instances.Remove(_parts[^1].Instance);
            _parts.RemoveAt(_parts.Count - 1);
        }
        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            var colors = part.Colors.Count == part.VertexCount ? part.Colors.ToArray() : null;
            if (p < _parts.Count)
            {
                var existing = _parts[p];
                existing.Mesh.SetGeometry(part.Positions.ToArray(), part.Indices.ToArray(), part.Normals.ToArray(), part.TexCoords.ToArray(), colors);
                existing.Instance.Material = _materials[part.Material];
                existing.Instance.Name = part.Name;
                _parts[p] = (part, existing.Mesh, existing.Instance, model.Skin[p]);
            }
            else
            {
                var mesh = new Mesh3D();
                mesh.SetGeometry(part.Positions.ToArray(), part.Indices.ToArray(), part.Normals.ToArray(), part.TexCoords.ToArray(), colors);
                var instance = new MeshInstance3D(mesh, _materials[part.Material]) { Name = part.Name };
                Scene.Instances.Insert(Scene.Instances.IndexOf(_proxyLinesInstance), instance);
                _parts.Add((part, mesh, instance, model.Skin[p]));
            }
        }
        _deformer = new ProxyDeformer(model.Proxy.Model, model.SkinAttachments);
        _skin = new Matrix4x4[_deformer.JointCount];
        int largest = model.Parts.Count == 0 ? 0 : model.Parts.Max(p => p.VertexCount);
        _positionBuffer = new Vector3[largest];
        _normalBuffer = new Vector3[largest];
        BuildProxyMeshes(model.Proxy.Model, RestPositions(model), rebuild: true);
        UpdateVisibility();
    }

    /// <summary>Puts the model back in its rest pose.</summary>
    public void ShowRest()
    {
        if (Model is not { } model) return;
        foreach (var (part, mesh, _, _) in _parts) mesh.UpdatePositions(part.Positions.ToArray(), part.Normals.ToArray());
        BuildProxyMeshes(model.Proxy.Model, RestPositions(model), rebuild: false);
    }

    /// <summary>Poses the model by the proxy's node positions (the visible parts are skinned to them).</summary>
    public void Pose(ReadOnlySpan<Vector3> positions)
    {
        if (Model is not { } model || _deformer is not { } deformer) return;
        deformer.ComputeSkinMatrices(positions, _skin);
        foreach (var (part, mesh, instance, weights) in _parts)
        {
            if (!instance.IsVisible) continue;
            int count = part.VertexCount;
            ProxyDeformer.Deform(part.Positions, part.Normals, weights, _skin, _positionBuffer, _normalBuffer);
            mesh.UpdatePositions(_positionBuffer.AsSpan(0, count), _normalBuffer.AsSpan(0, count));
        }
        if (ShowProxy) BuildProxyMeshes(model.Proxy.Model, positions, rebuild: false);
    }

    /// <summary>Gets the point a camera follows: between the pilot and the middle of the canopy.</summary>
    public Vector3 FollowPoint(ReadOnlySpan<Vector3> positions)
    {
        if (Model is not { } model) return Vector3.Zero;
        var proxy = model.Proxy.Model;
        return Vector3.Lerp(positions[proxy.Pilot], positions[proxy.Sections[proxy.Sections.Count / 2].LeadingEdge], 0.6f);
    }

    private static Vector3[] RestPositions(GliderModel model) => model.Proxy.Model.Nodes.Select(n => n.Position).ToArray();

    private void UpdateMaterials(GliderModel model)
    {
        var design = model.Design;
        Material3D Get(GliderMaterial key) => _materials.TryGetValue(key, out var m) ? m : _materials[key] = new Material3D();

        var canopy = Get(GliderMaterial.Canopy);
        canopy.BaseColor = Color.FromRgb(255, 255, 255);
        canopy.Roughness = 0.72f;
        canopy.Transmission = (float)design.FabricTranslucency;
        canopy.DoubleSided = true;
        canopy.BaseColorTexture = model.BaseColor is { } baseColor ? new Texture3D(baseColor.Width, baseColor.Height, baseColor.Pixels) : null;
        canopy.NormalTexture = model.NormalMap is { } normal ? new Texture3D(normal.Width, normal.Height, normal.Pixels, srgb: false) : null;

        var ribs = Get(GliderMaterial.Ribs);
        ribs.BaseColor = ToColor(design.RibColor);
        ribs.Roughness = 0.8f;
        ribs.Transmission = (float)design.FabricTranslucency;

        var lines = Get(GliderMaterial.Lines);
        lines.BaseColor = Color.FromRgb(255, 255, 255);
        lines.Roughness = 0.6f;
        lines.DoubleSided = true; // low poly lines are flat ribbons

        var risers = Get(GliderMaterial.Risers);
        risers.BaseColor = Color.FromRgb(0x26, 0x32, 0x38);
        risers.Roughness = 0.9f;

        var metal = Get(GliderMaterial.Metal);
        metal.BaseColor = Color.FromRgb(210, 210, 215);
        metal.Metallic = 1;
        metal.Roughness = 0.3f;

        var toggles = Get(GliderMaterial.Toggles);
        toggles.BaseColor = ToColor(design.AccentColor);
        toggles.Roughness = 0.55f;
    }

    private static Color ToColor(string hex)
    {
        var c = CanopyTextureGenerator.ParseColor(hex, SKColors.Gray);
        return Color.FromRgb(c.Red, c.Green, c.Blue);
    }

    private void BuildProxyMeshes(ProxyModel proxy, ReadOnlySpan<Vector3> positions, bool rebuild)
    {
        if (rebuild)
        {
            var indices = new List<uint>();
            foreach (var c in proxy.Constraints)
            {
                if (c.Kind is ConstraintKind.Chordwise or ConstraintKind.Spanwise or ConstraintKind.Rib or ConstraintKind.Line or ConstraintKind.Riser or ConstraintKind.Harness)
                {
                    indices.Add((uint)c.A);
                    indices.Add((uint)c.B);
                }
            }
            _proxyLines.SetGeometry(positions.ToArray(), indices.ToArray());
            _proxyPoints.SetGeometry(positions.ToArray(), Enumerable.Range(0, positions.Length).Select(i => (uint)i).ToArray());
        }
        else
        {
            _proxyLines.UpdatePositions(positions);
            _proxyPoints.UpdatePositions(positions);
        }
    }

    private void UpdateVisibility()
    {
        foreach (var (part, _, instance, _) in _parts)
        {
            instance.IsVisible = part.Material switch
            {
                GliderMaterial.Canopy => ShowCanopy,
                GliderMaterial.Ribs => ShowRibs && ShowCanopy,
                _ => ShowRigging,
            };
        }
        _proxyLinesInstance.IsVisible = ShowProxy;
        _proxyPointsInstance.IsVisible = ShowProxy;
    }
}
