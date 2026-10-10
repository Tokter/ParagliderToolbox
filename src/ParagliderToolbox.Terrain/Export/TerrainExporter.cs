using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParagliderToolbox.Paraglider.Export;
using ParagliderToolbox.Terrain.Meshing;

namespace ParagliderToolbox.Terrain.Export;

/// <summary>The heightmap files of a tile export.</summary>
public enum HeightmapFormat
{
    /// <summary>No heightmaps.</summary>
    None,

    /// <summary>16-bit grayscale PNG (Unreal, Godot, most tools).</summary>
    Png16,

    /// <summary>16-bit little-endian RAW without a header (Unity's terrain import).</summary>
    Raw16,
}

/// <summary>What a tile export writes.</summary>
public sealed record TerrainExportOptions
{
    /// <summary>Gets whether every tile is written as glTF (mesh with skirts, and its texture embedded).</summary>
    public bool Meshes { get; init; } = true;

    /// <summary>Gets the heightmap files written per tile.</summary>
    public HeightmapFormat Heightmaps { get; init; } = HeightmapFormat.Png16;

    /// <summary>Gets whether every tile's texture is written as JPEG.</summary>
    public bool Textures { get; init; } = true;
}

/// <summary>
/// Writes a built terrain for games and tools: a folder with a manifest (<c>terrain.json</c>: the frame, the quadtree of
/// tiles and levels, the height range, the sources and their credits) and per tile a glTF mesh, a heightmap and a
/// texture (see docs/TerrainFormat.md); or the most detailed tiles as one glTF file, for Blender and small terrains.
/// </summary>
public static class TerrainExporter
{
    /// <summary>The manifest's format name.</summary>
    public const string Format = "paraglider-toolbox-terrain";

    /// <summary>The manifest's format version.</summary>
    public const int FormatVersion = 1;

    private const string Generator = "Paraglider Toolbox";

    /// <summary>Writes the terrain into <paramref name="folder"/> as a tile set.</summary>
    /// <returns>The files written.</returns>
    public static List<string> ExportTiles(TerrainModel model, string folder, TerrainExportOptions? options = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new TerrainExportOptions();
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        string Write(string relative, byte[] data)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
            files.Add(path);
            return relative;
        }

        var layout = model.Layout;
        var entries = new JsonArray();
        int done = 0;
        foreach (var key in layout.Tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!model.Tiles.TryGetValue(key, out var tile)) continue;
            string name = $"L{key.Level}/{key.X}_{key.Y}";
            var entry = TileEntry(model, tile);
            if (options.Meshes) entry["mesh"] = Write($"tiles/{name}.glb", ToGlb(model, [tile]));
            if (options.Heightmaps != HeightmapFormat.None)
            {
                var values = Heightmaps.Quantize(tile.InnerHeights(), model.MinHeight, model.MaxHeight);
                entry["heightmap"] = options.Heightmaps == HeightmapFormat.Raw16
                    ? Write($"heightmaps/{name}.raw", Heightmaps.ToRaw(values))
                    : Write($"heightmaps/{name}.png", Heightmaps.ToPng(values, tile.Rect.Columns, tile.Rect.Rows));
            }
            if (options.Textures && tile.Texture is { } jpeg) entry["texture"] = Write($"textures/{name}.jpg", jpeg);
            entries.Add(entry);
            progress?.Report(++done / (double)layout.Tiles.Count);
        }

        var manifest = Manifest(model, entries);
        Write("terrain.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        Write("ATTRIBUTION.txt", Encoding.UTF8.GetBytes(AttributionText(model)));
        return files;
    }

    /// <summary>
    /// Writes the most detailed tiles everywhere (the quadtree's leaves, but none finer than <paramref name="finestLevel"/>)
    /// as one glTF binary, each tile a node with its mesh and texture.
    /// </summary>
    public static byte[] ToGlb(TerrainModel model, int finestLevel = 0) => ToGlb(model, Leaves(model, finestLevel).ToList());

    /// <summary>Gets the tiles drawn when no tile is coarser than needed: the leaves, but none finer than <paramref name="finestLevel"/>.</summary>
    public static IEnumerable<TerrainTile> Leaves(TerrainModel model, int finestLevel = 0)
    {
        var layout = model.Layout;
        var stack = new Stack<TileKey>(layout.Roots.Reverse());
        while (stack.Count > 0)
        {
            var key = stack.Pop();
            if (key.Level > finestLevel && layout.IsSplit(key) && layout.Children(key).All(model.Tiles.ContainsKey))
            {
                foreach (var child in layout.Children(key).Reverse()) stack.Push(child);
            }
            else if (model.Tiles.TryGetValue(key, out var tile))
            {
                yield return tile;
            }
        }
    }

    /// <summary>Writes <paramref name="tiles"/> as one glTF binary.</summary>
    public static byte[] ToGlb(TerrainModel model, IReadOnlyList<TerrainTile> tiles)
    {
        var gltf = new GltfWriter();
        var shading = model.Settings.Shading;
        int root = gltf.AddNode(new JsonObject
        {
            ["name"] = "Terrain",
            ["children"] = new JsonArray(),
            ["extras"] = new JsonObject
            {
                ["latitude"] = model.Settings.Center.Latitude,
                ["longitude"] = model.Settings.Center.Longitude,
                ["frame"] = "x east, y up (m above sea level), z south; transverse Mercator centered on the origin",
                ["attribution"] = model.Attribution,
            },
        }, root: true);
        var children = (JsonArray)gltf.Nodes[root]!["children"]!;
        foreach (var tile in tiles)
        {
            var mesh = TerrainMesh.Build(tile, shading, model.Layout.SkirtDepthAt(tile.Key.Level),
                shading == TerrainShading.Faceted ? tile.DecodeTexture() : null);
            var attributes = new JsonObject
            {
                ["POSITION"] = gltf.AddAccessor(mesh.Positions, minMax: true),
                ["NORMAL"] = gltf.AddAccessor(mesh.Normals),
            };
            var pbr = new JsonObject { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.95 };
            if (mesh.Colors is { } colors)
            {
                attributes["COLOR_0"] = gltf.AddAccessor(colors);
            }
            else
            {
                attributes["TEXCOORD_0"] = gltf.AddAccessor(mesh.TexCoords);
                if (tile.Texture is { } jpeg)
                {
                    int texture = gltf.AddTexture(gltf.AddImage(jpeg, tile.Key.ToString(), "image/jpeg"));
                    pbr["baseColorTexture"] = new JsonObject { ["index"] = texture };
                }
            }
            int material = gltf.AddMaterial(new JsonObject { ["name"] = tile.Key.ToString(), ["pbrMetallicRoughness"] = pbr });
            int meshIndex = gltf.AddMesh(new JsonObject
            {
                ["name"] = tile.Key.ToString(),
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = attributes,
                    ["indices"] = gltf.AddIndices(mesh.Indices),
                    ["material"] = material,
                    ["mode"] = 4,
                }),
            });
            int node = gltf.AddNode(new JsonObject
            {
                ["name"] = tile.Key.ToString(),
                ["mesh"] = meshIndex,
                ["translation"] = new JsonArray(mesh.Origin.X, mesh.Origin.Y, mesh.Origin.Z),
                ["extras"] = new JsonObject { ["level"] = tile.Key.Level, ["x"] = tile.Key.X, ["y"] = tile.Key.Y },
            });
            children.Add(node);
        }
        return gltf.ToGlb(Generator);
    }

    /// <summary>Gets the manifest of a tile export (without file names when <paramref name="tiles"/> is <c>null</c>).</summary>
    public static JsonObject Manifest(TerrainModel model, JsonArray? tiles = null)
    {
        var layout = model.Layout;
        var settings = model.Settings;
        if (tiles is null)
        {
            tiles = new JsonArray();
            foreach (var key in layout.Tiles)
            {
                if (model.Tiles.TryGetValue(key, out var tile)) tiles.Add(TileEntry(model, tile));
            }
        }
        JsonArray Sources(IReadOnlyList<SourceShare> shares) => new(shares.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Source.Id,
            ["name"] = s.Source.Name,
            ["share"] = Math.Round(s.Share, 4),
            ["attribution"] = s.Source.Attribution,
            ["license"] = s.Source.License,
        }).ToArray());

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["generator"] = Generator,
            ["origin"] = new JsonObject { ["latitude"] = settings.Center.Latitude, ["longitude"] = settings.Center.Longitude },
            ["frame"] = new JsonObject
            {
                ["projection"] = "transverse Mercator on WGS84, central meridian through the origin, scale 1",
                ["axes"] = "x east, y up, z south; meters; y is the height above sea level",
            },
            ["extent"] = layout.Extent,
            ["spacing"] = layout.Spacing,
            ["tileSamples"] = layout.Samples,
            ["tileSize"] = layout.TileSize,
            ["tilesAcross"] = layout.TilesAcross,
            ["levels"] = layout.LevelCount,
            ["detailSize"] = layout.DetailSize,
            ["shading"] = settings.Shading == TerrainShading.Faceted ? "faceted" : "smooth",
            ["heights"] = new JsonObject
            {
                ["min"] = model.MinHeight,
                ["max"] = model.MaxHeight,
                ["heightmap"] = "height = min + value / 65535 × (max − min)",
            },
            ["texture"] = settings.Texture == TerrainTexture.None ? null : new JsonObject
            {
                ["size"] = layout.TextureSize,
                ["format"] = "jpeg",
                ["content"] = settings.Texture == TerrainTexture.Imagery ? "imagery" : "elevation colors",
            },
            ["roots"] = new JsonArray(layout.Roots.Select(k => (JsonNode)k.ToString()).ToArray()),
            ["tiles"] = tiles,
            ["sources"] = new JsonObject { ["elevation"] = Sources(model.ElevationSources), ["imagery"] = Sources(model.ImagerySources) },
            ["attribution"] = model.Attribution,
        };
    }

    private static JsonObject TileEntry(TerrainModel model, TerrainTile tile)
    {
        var layout = model.Layout;
        var rect = tile.Rect;
        return new JsonObject
        {
            ["key"] = tile.Key.ToString(),
            ["level"] = tile.Key.Level,
            ["x"] = tile.Key.X,
            ["y"] = tile.Key.Y,
            ["bounds"] = new JsonArray(rect.X0, rect.Z0, rect.X0 + rect.Width, rect.Z0 + rect.Depth),
            ["samples"] = new JsonArray(rect.Columns, rect.Rows),
            ["spacing"] = rect.Spacing,
            ["minHeight"] = tile.MinHeight,
            ["maxHeight"] = tile.MaxHeight,
            ["skirt"] = layout.SkirtDepthAt(tile.Key.Level),
            ["textureSize"] = tile.Texture is null ? null : new JsonArray(tile.TextureWidth, tile.TextureHeight),
            ["children"] = new JsonArray(layout.Children(tile.Key).Select(k => (JsonNode)k.ToString()).ToArray()),
        };
    }

    /// <summary>Gets the credits and terms of the sources of <paramref name="model"/>, for an ATTRIBUTION file.</summary>
    public static string AttributionText(TerrainModel model)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Terrain around {model.Settings.Center} ({model.Layout.Extent / 1000:0.##} km), made with {Generator}."));
        text.AppendLine();
        foreach (var share in model.ElevationSources.Concat(model.ImagerySources).DistinctBy(s => s.Source.Id))
        {
            text.AppendLine(share.Source.Name);
            text.AppendLine("  " + share.Source.Attribution);
            text.AppendLine("  " + share.Source.License);
            text.AppendLine();
        }
        return text.ToString();
    }
}
