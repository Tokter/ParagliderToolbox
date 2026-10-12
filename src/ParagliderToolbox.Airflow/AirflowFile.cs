using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParagliderToolbox.Airflow.Surface;

namespace ParagliderToolbox.Airflow;

/// <summary>
/// The airflow file (.airflow): "PTAF", a version, a JSON header that describes the grid, what it was simulated with,
/// the arrays, the thermals and the triggers, then the arrays, each zlib-compressed and quantized as the header says
/// (value = stored × scale). docs/AirflowFormat.md describes it for readers in other languages.
/// </summary>
internal static class AirflowFile
{
    private static ReadOnlySpan<byte> Magic => "PTAF"u8;

    /// <summary>The format version.</summary>
    public const int Version = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
        // Vector3 keeps its coordinates in fields: the thermals' cores as {"x", "y", "z"}.
        IncludeFields = true,
    };

    // The 3D fields and maps: name, type, scale, and how to reach the array.
    private sealed record Array3D(string Name, string Type, double Scale, Func<AirflowField, float[]> Get);

    private static readonly Array3D[] s_fields =
    [
        new("windX", "int16", 0.01, f => f.WindX),
        new("windY", "int16", 0.01, f => f.WindY),
        new("windZ", "int16", 0.01, f => f.WindZ),
        new("turbulence", "int16", 0.01, f => f.Turbulence),
        new("liftChance", "uint8", 1 / 255.0, f => f.LiftChance),
        new("liftStrength", "int16", 0.01, f => f.LiftStrength),
        new("cloud", "uint8", 1 / 255.0, f => f.Cloud),
        new("temperature", "int16", 0.01, f => f.Temperature),
        new("vapor", "int16", 1e-6, f => f.Vapor),
    ];

    // The base flow's wind, when the field has one (made when read).
    private static readonly Array3D[] s_baseFields =
    [
        new("baseWindX", "int16", 0.01, f => f.BaseWindX ??= new float[f.Nx * f.Ny * f.Nz]),
        new("baseWindY", "int16", 0.01, f => f.BaseWindY ??= new float[f.Nx * f.Ny * f.Nz]),
        new("baseWindZ", "int16", 0.01, f => f.BaseWindZ ??= new float[f.Nx * f.Ny * f.Nz]),
    ];

    private static readonly Array3D[] s_maps =
    [
        new("ground", "float32", 1, f => f.GroundHeight),
        new("surfaceHeat", "int16", 1, f => f.SurfaceHeat),
        new("boundaryLayerTop", "float32", 1, f => f.BoundaryLayerTop),
        new("cloudBase", "float32", 1, f => f.CloudBase),
        new("thermalVelocity", "int16", 0.01, f => f.ThermalVelocity),
    ];

    public static void Write(AirflowField field, Stream stream)
    {
        Array3D[] fields = field.HasBaseFlow ? [.. s_fields, .. s_baseFields] : s_fields;
        var header = new JsonObject
        {
            ["format"] = "ParagliderToolbox.Airflow",
            ["version"] = Version,
            ["grid"] = new JsonObject
            {
                ["nx"] = field.Nx, ["ny"] = field.Ny, ["nz"] = field.Nz,
                ["cellSize"] = field.CellSize, ["layerHeight"] = field.LayerHeight,
                ["origin"] = new JsonArray(field.X0, field.Y0, field.Z0),
                ["order3D"] = "(j * nz + k) * nx + i", ["order2D"] = "k * nx + i",
            },
            ["info"] = JsonSerializer.SerializeToNode(field.Info, Json),
            ["fields"] = Describe(fields),
            ["maps"] = Describe(s_maps.Append(new Array3D("landCover", "uint8", 1, _ => []))),
            ["landCovers"] = new JsonArray(Enum.GetNames<LandCover>().Select(n => (JsonNode)JsonNamingPolicy.CamelCase.ConvertName(n)).ToArray()),
            ["thermals"] = JsonSerializer.SerializeToNode(field.Thermals, Json),
            ["triggers"] = JsonSerializer.SerializeToNode(field.Triggers.Select(t => new
            {
                t.X, t.Z, t.Height, t.Score, t.Heat, Cover = JsonNamingPolicy.CamelCase.ConvertName(t.Cover.ToString()),
            }), Json),
        };
        if (field.Convection != null) header["convection"] = JsonSerializer.SerializeToNode(field.Convection, Json);
        byte[] json = Encoding.UTF8.GetBytes(header.ToJsonString(Json));

        Span<byte> prefix = stackalloc byte[12];
        Magic.CopyTo(prefix);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(prefix[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[8..], (uint)json.Length);
        stream.Write(prefix);
        stream.Write(json);
        foreach (var array in fields) WriteBlock(stream, Encode(array.Get(field), array.Type, array.Scale));
        foreach (var array in s_maps) WriteBlock(stream, Encode(array.Get(field), array.Type, array.Scale));
        WriteBlock(stream, field.LandCover);
    }

    public static AirflowField Read(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[12];
        stream.ReadExactly(prefix);
        if (!prefix[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not an airflow file.");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(prefix[4..]);
        if (version > Version) throw new InvalidDataException($"The airflow file is of a newer version ({version}).");
        var json = new byte[BinaryPrimitives.ReadUInt32LittleEndian(prefix[8..])];
        stream.ReadExactly(json);
        var header = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The airflow file's header is missing.");
        var grid = header["grid"]!.AsObject();
        var origin = grid["origin"]!.AsArray();
        var field = new AirflowField((int)grid["nx"]!, (int)grid["ny"]!, (int)grid["nz"]!, (double)grid["cellSize"]!, (double)grid["layerHeight"]!,
            (double)origin[0]!, (double)origin[1]!, (double)origin[2]!);
        int cells = field.Nx * field.Ny * field.Nz, columns = field.Nx * field.Nz;

        foreach (var node in header["fields"]!.AsArray())
        {
            var (name, type, scale) = Entry(node!);
            var target = s_fields.Concat(s_baseFields).FirstOrDefault(f => f.Name == name)?.Get(field);
            Decode(ReadBlock(stream), type, scale, target, cells);
        }
        foreach (var node in header["maps"]!.AsArray())
        {
            var (name, type, scale) = Entry(node!);
            byte[] block = ReadBlock(stream);
            if (name == "landCover")
            {
                if (block.Length == columns) Array.Copy(block, field.LandCover, columns);
                continue;
            }
            Decode(block, type, scale, s_maps.FirstOrDefault(f => f.Name == name)?.Get(field), columns);
        }

        if (header["info"] is { } info) field.Info = info.Deserialize<AirflowInfo>(Json) ?? new AirflowInfo();
        if (header["thermals"] is JsonArray thermals) field.Thermals = thermals.Deserialize<List<Thermal>>(Json) ?? [];
        if (header["convection"] is JsonObject convection) field.Convection = convection.Deserialize<Convection.ConvectionClimate>(Json);
        if (header["triggers"] is JsonArray triggers)
        {
            field.Triggers = triggers.Select(t => new ThermalTrigger((double)t!["x"]!, (double)t["z"]!, (double)t["height"]!, (double)t["score"]!,
                (double)t["heat"]!, Enum.TryParse<LandCover>((string?)t["cover"], ignoreCase: true, out var cover) ? cover : LandCover.Meadow)).ToList();
        }
        return field;
    }

    private static JsonArray Describe(IEnumerable<Array3D> arrays) =>
        new(arrays.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["type"] = a.Type, ["scale"] = a.Scale }).ToArray());

    private static (string Name, string Type, double Scale) Entry(JsonNode node) =>
        ((string)node["name"]!, (string)node["type"]!, (double?)node["scale"] ?? 1);

    internal static byte[] Encode(float[] values, string type, double scale)
    {
        switch (type)
        {
            case "float32":
            {
                var bytes = new byte[values.Length * 4];
                for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
                return bytes;
            }
            case "int16":
            {
                var bytes = new byte[values.Length * 2];
                for (int i = 0; i < values.Length; i++)
                {
                    double v = values[i] / scale;
                    short s = double.IsNaN(v) ? (short)0 : (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), s);
                }
                return bytes;
            }
            case "uint8":
            {
                var bytes = new byte[values.Length];
                for (int i = 0; i < values.Length; i++) bytes[i] = (byte)Math.Clamp(Math.Round(values[i] / scale), 0, 255);
                return bytes;
            }
            default:
                throw new NotSupportedException(type);
        }
    }

    internal static void Decode(byte[] bytes, string type, double scale, float[]? target, int count)
    {
        if (target is null) return;
        int size = type switch { "float32" => 4, "int16" => 2, "uint8" => 1, _ => throw new InvalidDataException($"Unknown array type {type}.") };
        if (bytes.Length != count * size) throw new InvalidDataException("An array of the airflow file has the wrong size.");
        for (int i = 0; i < count; i++)
        {
            target[i] = type switch
            {
                "float32" => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4)),
                "int16" => (float)(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) * scale),
                _ => (float)(bytes[i] * scale),
            };
        }
    }

    internal static void WriteBlock(Stream stream, byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)compressed.Length);
        stream.Write(length);
        compressed.Position = 0;
        compressed.CopyTo(stream);
    }

    internal static byte[] ReadBlock(Stream stream)
    {
        Span<byte> length = stackalloc byte[4];
        stream.ReadExactly(length);
        var compressed = new byte[BinaryPrimitives.ReadUInt32LittleEndian(length)];
        stream.ReadExactly(compressed);
        using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var result = new MemoryStream();
        zlib.CopyTo(result);
        return result.ToArray();
    }
}
