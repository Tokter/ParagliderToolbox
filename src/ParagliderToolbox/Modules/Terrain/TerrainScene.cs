using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Atelier.Core.Primitives;
using Atelier.Graphics3D;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Meshing;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>
/// The 3D scene of a terrain: the tiles a camera needs, chosen by their level of detail from the quadtree (finer tiles
/// where a coarser one's samples would be far apart on screen), their meshes and textures made when first shown.
/// </summary>
/// <remarks>
/// <see cref="Select"/> walks the quadtree from the roots and splits a tile into its children while its sample spacing
/// is more than <see cref="PixelsPerSample"/> pixels on screen; a tile whose children aren't built yet is drawn
/// instead of them, so a terrain still building fills in coarse to fine. Making meshes is limited per call (a frame),
/// and the most recently shown few hundred tiles are kept.
/// </remarks>
public sealed class TerrainScene
{
    private const int KeptTiles = 400;
    private static readonly TimeSpan s_buildBudget = TimeSpan.FromMilliseconds(30);

    private static readonly Color[] s_levelColors =
    [
        Color.FromRgb(0xFF, 0x6E, 0x6E), Color.FromRgb(0xFF, 0xB3, 0x47), Color.FromRgb(0xFF, 0xEE, 0x58), Color.FromRgb(0x9C, 0xE0, 0x6A),
        Color.FromRgb(0x4D, 0xD0, 0xE1), Color.FromRgb(0x64, 0x8C, 0xFF), Color.FromRgb(0xB3, 0x88, 0xFF), Color.FromRgb(0xF4, 0x8F, 0xB1),
    ];

    private readonly Dictionary<TileKey, Entry> _entries = [];
    private TerrainLayout? _layout;
    private IReadOnlyDictionary<TileKey, TerrainTile> _tiles = new Dictionary<TileKey, TerrainTile>();
    private TerrainSettings? _settings;
    private bool _showLevels;
    private bool _textured = true;
    private long _frame;

    private sealed class Entry(MeshInstance3D instance, Material3D material, Texture3D? texture, int triangles)
    {
        public MeshInstance3D Instance { get; } = instance;
        public Material3D Material { get; } = material;
        public Texture3D? Texture { get; } = texture;
        public int Triangles { get; } = triangles;
        public long LastShown { get; set; }
    }

    /// <summary>Gets the scene a viewport shows.</summary>
    public Scene3D Scene { get; } = new();

    /// <summary>Gets or sets how far apart (pixels on screen) a tile's samples may be before its children are drawn instead.</summary>
    public float PixelsPerSample { get; set; } = 6;

    /// <summary>Gets the number of tiles drawn.</summary>
    public int ShownTiles { get; private set; }

    /// <summary>Gets the triangles drawn.</summary>
    public long ShownTriangles { get; private set; }

    /// <summary>Gets whether the last <see cref="Select"/> ran out of time to make meshes (another frame should select again).</summary>
    public bool NeedsAnotherPass { get; private set; }

    /// <summary>Gets the layout shown, or <c>null</c>.</summary>
    public TerrainLayout? Layout => _layout;

    /// <summary>Gets or sets whether the tiles are tinted by their level of detail.</summary>
    public bool ShowLevels
    {
        get => _showLevels;
        set
        {
            _showLevels = value;
            foreach (var (key, entry) in _entries) ApplyLook(key, entry);
        }
    }

    /// <summary>Gets or sets whether the textures are shown (otherwise plain gray, to see the shape).</summary>
    public bool Textured
    {
        get => _textured;
        set
        {
            _textured = value;
            foreach (var (key, entry) in _entries) ApplyLook(key, entry);
        }
    }

    /// <summary>Shows the tiles of <paramref name="layout"/> built so far (the dictionary may still grow); replaces what was shown.</summary>
    public void Show(TerrainSettings settings, TerrainLayout layout, IReadOnlyDictionary<TileKey, TerrainTile> tiles)
    {
        Clear();
        _settings = settings;
        _layout = layout;
        _tiles = tiles;
    }

    /// <summary>Removes everything.</summary>
    public void Clear()
    {
        Scene.Instances.Clear();
        _entries.Clear();
        _layout = null;
        _tiles = new Dictionary<TileKey, TerrainTile>();
        ShownTiles = 0;
        ShownTriangles = 0;
    }

    /// <summary>Gets the box around the terrain (the built tiles' heights), or <c>null</c> before any tile.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds()
    {
        if (_layout is not { } layout || _tiles.Count == 0) return null;
        var built = layout.Roots.Where(_tiles.ContainsKey).Select(k => _tiles[k]).ToList();
        if (built.Count == 0) built = _tiles.Values.ToList();
        float half = (float)(layout.Extent / 2);
        return (new Vector3(-half, built.Min(t => t.MinHeight), -half), new Vector3(half, built.Max(t => t.MaxHeight), half));
    }

    /// <summary>Gets the height at local (x, z) from the finest tile built there, or <c>null</c>.</summary>
    public float? HeightAt(double x, double z)
    {
        if (_layout is not { } layout) return null;
        TerrainTile? best = null;
        foreach (var root in layout.Roots)
        {
            TileKey? key = root;
            while (key is { } current && Inside(layout.Rect(current), x, z))
            {
                if (_tiles.TryGetValue(current, out var tile)) best = tile;
                key = layout.Children(current).Cast<TileKey?>().FirstOrDefault(k => Inside(layout.Rect(k!.Value), x, z));
            }
        }
        return best?.HeightAt(x, z);
    }

    private static bool Inside(TileRect rect, double x, double z) =>
        x >= rect.X0 && x <= rect.X0 + rect.Width && z >= rect.Z0 && z <= rect.Z0 + rect.Depth;

    /// <summary>Chooses and shows the tiles for a camera at <paramref name="camera"/> with a vertical field of view of <paramref name="fieldOfView"/> radians over <paramref name="viewportHeight"/> pixels.</summary>
    public void Select(Vector3 camera, float fieldOfView, float viewportHeight)
    {
        if (_layout is not { } layout || _settings is not { } settings) return;
        _frame++;
        NeedsAnotherPass = false;
        var clock = Stopwatch.StartNew();
        float pixelsPerRadian = viewportHeight / Math.Max(1e-3f, fieldOfView);
        var shown = new HashSet<TileKey>();

        bool Ready(TileKey key) => _entries.ContainsKey(key) || _tiles.ContainsKey(key) && clock.Elapsed < s_buildBudget;

        void Visit(TileKey key)
        {
            if (!_tiles.TryGetValue(key, out var tile)) return;
            if (layout.IsSplit(key) && Coarse(tile, camera, pixelsPerRadian))
            {
                var children = layout.Children(key).ToList();
                if (children.All(c => _tiles.ContainsKey(c) && Ready(c)))
                {
                    foreach (var child in children) Visit(child);
                    return;
                }
                if (children.All(_tiles.ContainsKey)) NeedsAnotherPass = true;
            }
            if (EnsureEntry(key, tile, settings) is not null) shown.Add(key);
            else NeedsAnotherPass = true;
        }

        foreach (var root in layout.Roots) Visit(root);

        long triangles = 0;
        foreach (var (key, entry) in _entries)
        {
            bool visible = shown.Contains(key);
            entry.Instance.IsVisible = visible;
            if (!visible) continue;
            entry.LastShown = _frame;
            triangles += entry.Triangles;
        }
        ShownTiles = shown.Count;
        ShownTriangles = triangles;
        Evict();
    }

    // Whether a tile's samples are further apart on screen than allowed, seen from the camera.
    private bool Coarse(TerrainTile tile, Vector3 camera, float pixelsPerRadian)
    {
        var rect = tile.Rect;
        double dx = Math.Max(Math.Max(rect.X0 - camera.X, camera.X - (rect.X0 + rect.Width)), 0);
        double dz = Math.Max(Math.Max(rect.Z0 - camera.Z, camera.Z - (rect.Z0 + rect.Depth)), 0);
        double dy = Math.Max(Math.Max(tile.MinHeight - camera.Y, camera.Y - tile.MaxHeight), 0);
        double distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        return rect.Spacing / distance * pixelsPerRadian > PixelsPerSample;
    }

    private Entry? EnsureEntry(TileKey key, TerrainTile tile, TerrainSettings settings)
    {
        if (_entries.TryGetValue(key, out var existing)) return existing;
        var layout = _layout!;
        uint[]? pixels = tile.DecodeTexture();
        var mesh = TerrainMesh.Build(tile, settings.Shading, layout.SkirtDepthAt(key.Level), settings.Shading == TerrainShading.Faceted ? pixels : null);
        var uvs = mesh.TexCoords;
        Texture3D? texture = null;
        if (pixels != null && settings.Shading == TerrainShading.Smooth)
        {
            texture = new Texture3D(tile.TextureWidth, tile.TextureHeight, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
            // The viewport's textures repeat: keep half a texel inside, so the edges don't sample the opposite side.
            float w = tile.TextureWidth, h = tile.TextureHeight;
            uvs = uvs.Select(t => new Vector2((0.5f + t.X * (w - 1)) / w, (0.5f + t.Y * (h - 1)) / h)).ToArray();
        }
        var geometry = new Mesh3D();
        geometry.SetGeometry(mesh.Positions, mesh.Indices, mesh.Normals, uvs, mesh.Colors);
        var material = new Material3D { Roughness = 0.95f, DoubleSided = false };
        var instance = new MeshInstance3D(geometry, material)
        {
            Name = key.ToString(),
            Transform = Matrix4x4.CreateTranslation(mesh.Origin),
            IsVisible = false,
        };
        var entry = new Entry(instance, material, texture, mesh.TriangleCount);
        ApplyLook(key, entry);
        _entries[key] = entry;
        Scene.Instances.Add(instance);
        return entry;
    }

    private void ApplyLook(TileKey key, Entry entry)
    {
        entry.Material.BaseColorTexture = _textured ? entry.Texture : null;
        var tint = _showLevels ? s_levelColors[key.Level % s_levelColors.Length] : Color.FromRgb(255, 255, 255);
        if (!_textured && !_showLevels) tint = Color.FromRgb(0xB8, 0xB4, 0xAC);
        entry.Material.BaseColor = tint;
    }

    // Keeps the tiles shown most recently.
    private void Evict()
    {
        if (_entries.Count <= KeptTiles) return;
        foreach (var (key, entry) in _entries.OrderBy(e => e.Value.LastShown).Take(_entries.Count - KeptTiles).ToList())
        {
            if (entry.Instance.IsVisible) continue;
            Scene.Instances.Remove(entry.Instance);
            _entries.Remove(key);
        }
    }
}
