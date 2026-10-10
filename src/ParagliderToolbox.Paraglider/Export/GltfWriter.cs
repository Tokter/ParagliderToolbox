using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ParagliderToolbox.Paraglider.Export;

/// <summary>
/// A small glTF 2.0 binary (.glb) writer: buffers, accessors, meshes, materials with embedded PNG textures, nodes, skins
/// and animations. Matrices are System.Numerics row-vector matrices; glTF's column-major layout of the column-vector
/// matrix is the same memory, so they are written element by element (M11, M12, ...).
/// </summary>
public sealed class GltfWriter
{
    private readonly MemoryStream _bin = new();
    private readonly JsonArray _bufferViews = [];
    private readonly JsonArray _accessors = [];
    private readonly JsonArray _meshes = [];
    private readonly JsonArray _materials = [];
    private readonly JsonArray _textures = [];
    private readonly JsonArray _images = [];
    private readonly JsonArray _samplers = [];
    private readonly JsonArray _nodes = [];
    private readonly JsonArray _skins = [];
    private readonly JsonArray _animations = [];
    private readonly JsonArray _sceneNodes = [];

    public const int Float = 5126, UnsignedInt = 5125, UnsignedShort = 5123;
    public const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    /// <summary>Gets the node list, to add children.</summary>
    public JsonArray Nodes => _nodes;

    public int AddBufferView(ReadOnlySpan<byte> data, int? target = null)
    {
        Align(4);
        long offset = _bin.Position;
        _bin.Write(data);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
        if (target is { } t) view["target"] = t;
        _bufferViews.Add(view);
        return _bufferViews.Count - 1;
    }

    public int AddAccessor(Vector3[] data, bool minMax = false, int? target = ArrayBuffer)
    {
        int view = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()), target);
        var accessor = new JsonObject { ["bufferView"] = view, ["componentType"] = Float, ["count"] = data.Length, ["type"] = "VEC3" };
        if (minMax && data.Length > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in data)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            accessor["min"] = new JsonArray(min.X, min.Y, min.Z);
            accessor["max"] = new JsonArray(max.X, max.Y, max.Z);
        }
        return Add(_accessors, accessor);
    }

    public int AddAccessor(Vector2[] data) =>
        Add(_accessors, new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()), ArrayBuffer),
            ["componentType"] = Float, ["count"] = data.Length, ["type"] = "VEC2",
        });

    public int AddAccessor(Vector4[] data, int? target = ArrayBuffer) =>
        Add(_accessors, new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()), target),
            ["componentType"] = Float, ["count"] = data.Length, ["type"] = "VEC4",
        });

    public int AddAccessor(Quaternion[] data) =>
        Add(_accessors, new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan())),
            ["componentType"] = Float, ["count"] = data.Length, ["type"] = "VEC4",
        });

    public int AddJoints(ushort[] joints) =>
        Add(_accessors, new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(joints.AsSpan()), ArrayBuffer),
            ["componentType"] = UnsignedShort, ["count"] = joints.Length / 4, ["type"] = "VEC4",
        });

    public int AddIndices(uint[] indices) =>
        Add(_accessors, new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()), ElementArrayBuffer),
            ["componentType"] = UnsignedInt, ["count"] = indices.Length, ["type"] = "SCALAR",
        });

    public int AddScalars(float[] values, bool minMax = false)
    {
        var accessor = new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan())),
            ["componentType"] = Float, ["count"] = values.Length, ["type"] = "SCALAR",
        };
        if (minMax && values.Length > 0)
        {
            accessor["min"] = new JsonArray(values.Min());
            accessor["max"] = new JsonArray(values.Max());
        }
        return Add(_accessors, accessor);
    }

    public int AddMatrices(Matrix4x4[] matrices) =>
        Add(_accessors, new JsonObject
        {
            ["bufferView"] = AddBufferView(System.Runtime.InteropServices.MemoryMarshal.AsBytes(matrices.AsSpan())),
            ["componentType"] = Float, ["count"] = matrices.Length, ["type"] = "MAT4",
        });

    /// <summary>Embeds an image (PNG, or JPEG with <paramref name="mimeType"/> <c>"image/jpeg"</c>).</summary>
    public int AddImage(byte[] data, string name, string mimeType = "image/png")
    {
        int view = AddBufferView(data);
        return Add(_images, new JsonObject { ["bufferView"] = view, ["mimeType"] = mimeType, ["name"] = name });
    }

    public int AddTexture(int image, bool repeat = false)
    {
        if (_samplers.Count == 0)
        {
            _samplers.Add(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 33071, ["wrapT"] = 33071 });
            _samplers.Add(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
        }
        return Add(_textures, new JsonObject { ["sampler"] = repeat ? 1 : 0, ["source"] = image });
    }

    public int AddMaterial(JsonObject material) => Add(_materials, material);

    public int AddMesh(JsonObject mesh) => Add(_meshes, mesh);

    public int AddNode(JsonObject node, bool root = false)
    {
        int index = Add(_nodes, node);
        if (root) _sceneNodes.Add(index);
        return index;
    }

    public int AddSkin(JsonObject skin) => Add(_skins, skin);

    public void AddAnimation(JsonObject animation) => _animations.Add(animation);

    /// <summary>Writes the .glb file.</summary>
    public byte[] ToGlb(string generator)
    {
        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = generator },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = _sceneNodes }),
            ["nodes"] = _nodes,
            ["meshes"] = _meshes,
            ["materials"] = _materials,
            ["accessors"] = _accessors,
            ["bufferViews"] = _bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = Align4(_bin.Length) }),
        };
        if (_textures.Count > 0)
        {
            root["textures"] = _textures;
            root["images"] = _images;
            root["samplers"] = _samplers;
        }
        if (_skins.Count > 0) root["skins"] = _skins;
        if (_animations.Count > 0) root["animations"] = _animations;

        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonLength = (int)Align4(json.Length);
        Align(4);
        byte[] bin = _bin.ToArray();
        int binLength = (int)Align4(bin.Length);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(0x46546C67u); // "glTF"
        writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonLength + 8 + binLength));
        writer.Write((uint)jsonLength);
        writer.Write(0x4E4F534Au); // "JSON"
        writer.Write(json);
        for (int i = json.Length; i < jsonLength; i++) writer.Write((byte)' ');
        writer.Write((uint)binLength);
        writer.Write(0x004E4942u); // "BIN\0"
        writer.Write(bin);
        for (int i = bin.Length; i < binLength; i++) writer.Write((byte)0);
        writer.Flush();
        return output.ToArray();
    }

    private void Align(int alignment)
    {
        while (_bin.Position % alignment != 0) _bin.WriteByte(0);
    }

    private static long Align4(long length) => (length + 3) / 4 * 4;

    private static int Add(JsonArray array, JsonNode node)
    {
        array.Add(node);
        return array.Count - 1;
    }
}
