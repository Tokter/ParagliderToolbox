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
/// preview and the replay of recorded flights share it. Launching, a simple figure stands for the pilot (the flying
/// model has none).
/// </summary>
public sealed class GliderScene
{
    private readonly Dictionary<GliderMaterial, Material3D> _materials = [];
    private readonly List<(MeshPart Part, Mesh3D Mesh, MeshInstance3D Instance, SkinWeights[] Weights)> _parts = [];
    private readonly Mesh3D _proxyLines = new(PrimitiveTopology.Lines);
    private readonly Mesh3D _proxyPoints = new(PrimitiveTopology.Points);
    private readonly MeshInstance3D _proxyLinesInstance;
    private readonly MeshInstance3D _proxyPointsInstance;
    private readonly MeshInstance3D _pilot;
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
        _pilot = new MeshInstance3D(PilotFigure(), new Material3D { BaseColor = Color.FromRgb(0x37, 0x47, 0x4F), Roughness = 0.8f }) { Name = "Pilot", IsVisible = false };
        Scene.Instances.Add(_pilot);
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

    /// <summary>Shows the pilot figure with its feet at <paramref name="feet"/>, facing <paramref name="forward"/> (horizontal).</summary>
    public void PosePilot(Vector3 feet, Vector3 forward)
    {
        _pilot.Transform = Matrix4x4.CreateRotationY(MathF.Atan2(forward.X, forward.Z)) * Matrix4x4.CreateTranslation(feet);
        _pilot.IsVisible = true;
    }

    /// <summary>Hides the pilot figure.</summary>
    public void HidePilot() => _pilot.IsVisible = false;

    // A standing pilot 1.75 m tall, feet at the origin, facing +Z (+X is their left): legs, feet pointing forward, body,
    // arms reaching forward to the risers, head.
    private static Mesh3D PilotFigure()
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        void Limb(Vector3 a, Vector3 b, float radius)
        {
            const int sides = 10;
            var axis = Vector3.Normalize(b - a);
            var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(axis, u);
            uint start = (uint)positions.Count;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i * 2 * MathF.PI / sides;
                var n = u * MathF.Cos(angle) + v * MathF.Sin(angle);
                positions.Add(a + n * radius);
                positions.Add(b + n * radius);
                normals.Add(n);
                normals.Add(n);
            }
            for (uint i = 0; i < sides; i++)
            {
                uint k = start + i * 2;
                indices.AddRange([k, k + 2, k + 1, k + 1, k + 2, k + 3]);
            }
            Cap(a, -axis, radius);
            Cap(b, axis, radius);
        }
        void Cap(Vector3 center, Vector3 normal, float radius)
        {
            // A flat disc closing a limb.
            const int sides = 10;
            var u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var v = Vector3.Cross(normal, u);
            uint middle = (uint)positions.Count;
            positions.Add(center);
            normals.Add(normal);
            for (int i = 0; i < sides; i++)
            {
                float angle = i * 2 * MathF.PI / sides;
                positions.Add(center + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius);
                normals.Add(normal);
            }
            for (uint i = 0; i < sides; i++) indices.AddRange([middle, middle + 1 + i, middle + 1 + (i + 1) % sides]);
        }
        void Head(Vector3 center, float radius)
        {
            const int rings = 8, sides = 12;
            uint start = (uint)positions.Count;
            for (int r = 0; r <= rings; r++)
            {
                float polar = r * MathF.PI / rings;
                for (int i = 0; i <= sides; i++)
                {
                    float angle = i * 2 * MathF.PI / sides;
                    var n = new Vector3(MathF.Sin(polar) * MathF.Cos(angle), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(angle));
                    positions.Add(center + n * radius);
                    normals.Add(n);
                }
            }
            for (uint r = 0; r < rings; r++)
            {
                for (uint i = 0; i < sides; i++)
                {
                    uint k = start + r * (sides + 1) + i;
                    indices.AddRange([k, k + 1, k + sides + 1, k + 1, k + sides + 2, k + sides + 1]);
                }
            }
        }
        foreach (float side in new[] { 1f, -1f })
        {
            Limb(new Vector3(side * 0.1f, 0.06f, 0), new Vector3(side * 0.1f, 0.88f, 0), 0.075f);
            Limb(new Vector3(side * 0.1f, 0.05f, -0.05f), new Vector3(side * 0.1f, 0.05f, 0.17f), 0.05f);
            Limb(new Vector3(side * 0.21f, 1.42f, 0), new Vector3(side * 0.24f, 1.12f, 0.3f), 0.05f);
        }
        Limb(new Vector3(0, 0.85f, 0), new Vector3(0, 1.48f, 0), 0.18f);
        Head(new Vector3(0, 1.64f, 0.01f), 0.11f);
        var mesh = new Mesh3D();
        mesh.SetGeometry([.. positions], [.. indices], [.. normals]);
        return mesh;
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
