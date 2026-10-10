using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using ParagliderToolbox.Terrain;
using ParagliderToolbox.Terrain.Export;
using ParagliderToolbox.Terrain.Geodesy;
using ParagliderToolbox.Terrain.Meshing;
using ParagliderToolbox.Terrain.Sources;

namespace ParagliderToolbox.Tests.Terrain;

public class TerrainExportTests
{
    private static async Task<TerrainModel> BuildAsync(TerrainShading shading = TerrainShading.Smooth)
    {
        var sources = new TerrainSources();
        sources.Elevation.Add(new PlaneSource("plane", 1, (_, _) => true));
        return await new TerrainBuilder(sources).BuildAsync(new TerrainSettings
        {
            Center = new GeoPoint(46.6863, 7.8632), Size = 1024, Resolution = 8, TileSamples = 33, TextureSize = 32,
            Texture = TerrainTexture.ElevationColors, Shading = shading,
        });
    }

    [Fact]
    public void Heightmaps_WriteSixteenBitGrayPng_ThatDecodesToTheValues()
    {
        var heights = new float[17 * 9];
        for (int i = 0; i < heights.Length; i++) heights[i] = 400 + (i * 7919 % 1000) * 1.5f;
        var values = Heightmaps.Quantize(heights, 400, 1900);
        var png = Heightmaps.ToPng(values, 17, 9);

        var (width, height, bitDepth, colorType, decoded) = DecodePng(png);
        Assert.Equal((17, 9, 16, 0), (width, height, bitDepth, colorType));
        Assert.Equal(values, decoded);
        Assert.Equal(0, Heightmaps.Quantize([400], 400, 1900)[0]);
        Assert.Equal(65535, Heightmaps.Quantize([1900], 400, 1900)[0]);

        var raw = Heightmaps.ToRaw(values);
        Assert.Equal(values[3], BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6)));
    }

    // A minimal PNG reader: the header, the chunks' CRCs and the five row filters.
    private static (int Width, int Height, int BitDepth, int ColorType, ushort[] Values) DecodePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int at = 8, width = 0, height = 0, bitDepth = 0, colorType = 0;
        using var idat = new MemoryStream();
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(at + 4, 4 + length)), crc);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            at += 12 + length;
        }
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        zlib.CopyTo(inflated);
        var bytes = inflated.ToArray();
        int rowBytes = width * 2;
        var rows = new byte[height * rowBytes];
        for (int r = 0; r < height; r++)
        {
            int filter = bytes[r * (rowBytes + 1)];
            for (int i = 0; i < rowBytes; i++)
            {
                int x = bytes[r * (rowBytes + 1) + 1 + i];
                int a = i >= 2 ? rows[r * rowBytes + i - 2] : 0, b = r > 0 ? rows[(r - 1) * rowBytes + i] : 0;
                int c = i >= 2 && r > 0 ? rows[(r - 1) * rowBytes + i - 2] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int predicted = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => 0 };
                rows[r * rowBytes + i] = (byte)(x + predicted);
            }
        }
        var values = new ushort[width * height];
        for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt16BigEndian(rows.AsSpan(i * 2));
        return (width, height, bitDepth, colorType, values);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    [Fact]
    public async Task TileMesh_HasSkirts_FacesUp_AndSitsOnTheTileCenter()
    {
        var model = await BuildAsync();
        var tile = model.Tiles[new TileKey(0, 2, 1)];
        var mesh = TerrainMesh.Build(tile, TerrainShading.Smooth, 10);

        Assert.Equal(new Vector3(128, 0, -128), mesh.Origin);
        Assert.Equal(33 * 33 + 4 * 32, mesh.Positions.Length);
        Assert.Equal(2 * 32 * 32 + 2 * 4 * 32, mesh.TriangleCount);
        // The grid's triangles face up, the skirts' face outwards.
        for (int t = 0; t < 2 * 32 * 32; t++)
        {
            Assert.True(Normal(mesh, t).Y > 0.9f);
        }
        var skirt = Normal(mesh, 2 * 32 * 32);
        Assert.True(skirt.Z < -0.9f);
        Assert.Equal(tile.Height(0, 0), mesh.Positions[0].Y, 3);
        Assert.Equal(-128, mesh.Positions[0].X, 3);
        Assert.Equal(Vector2.Zero, mesh.TexCoords[0]);
        Assert.Equal(Vector2.One, mesh.TexCoords[33 * 33 - 1]);
    }

    private static Vector3 Normal(TerrainMesh mesh, int triangle)
    {
        var a = mesh.Positions[mesh.Indices[triangle * 3]];
        var b = mesh.Positions[mesh.Indices[triangle * 3 + 1]];
        var c = mesh.Positions[mesh.Indices[triangle * 3 + 2]];
        return Vector3.Normalize(Vector3.Cross(b - a, c - a));
    }

    [Fact]
    public async Task FacetedMesh_GivesEveryTriangleItsOwnColoredVertices()
    {
        var model = await BuildAsync(TerrainShading.Faceted);
        var tile = model.Tiles[new TileKey(0, 0, 0)];
        var mesh = TerrainMesh.Build(tile, TerrainShading.Faceted, 0, tile.DecodeTexture());
        Assert.Equal(2 * 32 * 32 * 3, mesh.Positions.Length);
        Assert.NotNull(mesh.Colors);
        Assert.Equal(mesh.Normals[0], mesh.Normals[2]);
    }

    [Fact]
    public async Task ExportTiles_WritesTheManifest_AndEveryTilesFiles()
    {
        var model = await BuildAsync();
        string folder = Path.Combine(Path.GetTempPath(), "ParagliderToolbox.Tests", "terrain-export-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var files = TerrainExporter.ExportTiles(model, folder, new TerrainExportOptions { Heightmaps = HeightmapFormat.Raw16 });
            Assert.Equal(model.Tiles.Count * 3 + 2, files.Count);

            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "terrain.json")))!;
            Assert.Equal(TerrainExporter.Format, (string?)manifest["format"]);
            Assert.Equal(1024, (double)manifest["extent"]!);
            Assert.Equal(3, (int)manifest["levels"]!);
            Assert.Equal("L2_0_0", (string?)manifest["roots"]![0]);
            var tiles = manifest["tiles"]!.AsArray();
            Assert.Equal(model.Tiles.Count, tiles.Count);
            var root = tiles.First(t => (string?)t!["key"] == "L2_0_0")!;
            Assert.Equal(4, root["children"]!.AsArray().Count);
            Assert.True(File.Exists(Path.Combine(folder, (string)root["mesh"]!)));
            Assert.Equal(33 * 33 * 2, new FileInfo(Path.Combine(folder, (string)root["heightmap"]!)).Length);
            Assert.Contains("© plane", File.ReadAllText(Path.Combine(folder, "ATTRIBUTION.txt")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Glb_HoldsTheMostDetailedTiles_AsNodesWithTextures()
    {
        var model = await BuildAsync();
        var glb = TerrainExporter.ToGlb(model);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        int jsonLength = BitConverter.ToInt32(glb, 12);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))!;
        // 4 × 4 finest tiles under the terrain node.
        Assert.Equal(16, json["nodes"]![0]!["children"]!.AsArray().Count);
        Assert.Equal(16, json["images"]!.AsArray().Count);
        Assert.Equal("image/jpeg", (string?)json["images"]![0]!["mimeType"]);

        // Coarser: the level 1 tiles.
        var coarse = TerrainExporter.ToGlb(model, finestLevel: 1);
        var coarseJson = JsonNode.Parse(Encoding.UTF8.GetString(coarse, 20, BitConverter.ToInt32(coarse, 12)))!;
        Assert.Equal(4, coarseJson["nodes"]![0]!["children"]!.AsArray().Count);
    }
}
