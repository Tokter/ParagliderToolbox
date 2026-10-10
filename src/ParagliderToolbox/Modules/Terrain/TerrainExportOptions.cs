using Atelier.Core.Inspection;
using CommunityToolkit.Mvvm.ComponentModel;
using ParagliderToolbox.Terrain.Export;

namespace ParagliderToolbox.Modules.Terrain;

/// <summary>What a terrain tile export writes, edited in the dialog before it starts (see <see cref="TerrainExportOptions"/>).</summary>
[Inspectable]
public sealed partial class TerrainTileExportOptions : ObservableObject
{
    private bool _meshes = true;
    private HeightmapFormat _heightmaps = HeightmapFormat.Png16;
    private bool _textures = true;

    [InspectableProperty("Meshes (glTF)", "Files", Order = 1, Description = "Every tile as a .glb: its mesh with skirts and its texture embedded.")]
    public bool Meshes { get => _meshes; set => SetProperty(ref _meshes, value); }

    [InspectableProperty("Heightmaps", "Files", Order = 2,
        Description = "Every tile's heights for an engine's terrain system: 16-bit PNG (Unreal, Godot) or 16-bit RAW (Unity), the heights from the manifest's minimum to maximum.")]
    public HeightmapFormat Heightmaps { get => _heightmaps; set => SetProperty(ref _heightmaps, value); }

    [InspectableProperty("Textures (JPEG)", "Files", Order = 3, Description = "Every tile's texture as a .jpg, for the heightmaps.")]
    public bool Textures { get => _textures; set => SetProperty(ref _textures, value); }

    /// <summary>Gets a copy, for a dialog that may be canceled.</summary>
    public TerrainTileExportOptions Copy() => new() { Meshes = Meshes, Heightmaps = Heightmaps, Textures = Textures };

    /// <summary>Gets the exporter's options.</summary>
    public TerrainExportOptions ToOptions() => new() { Meshes = Meshes, Heightmaps = Heightmaps, Textures = Textures };
}

/// <summary>How detailed a single-file glTF export of a terrain is.</summary>
[Inspectable]
public sealed partial class TerrainGlbExportOptions(int levelCount) : ObservableObject
{
    private int _finestLevel;

    [InspectableProperty("Finest level", "Detail", Order = 1,
        Description = "The finest level of detail written: 0 the most detailed tiles; each level up has a quarter of the triangles.")]
    public int FinestLevel { get => _finestLevel; set => SetProperty(ref _finestLevel, Math.Clamp(value, 0, Math.Max(0, levelCount - 1))); }
}
