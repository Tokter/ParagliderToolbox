using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ParagliderToolbox.Terrain.Export;

/// <summary>
/// Writes heightmaps for game engines' terrain systems: 16-bit grayscale PNG (Unreal, Godot) and 16-bit little-endian
/// RAW (Unity), the heights mapped linearly from a range to 0–65535.
/// </summary>
public static class Heightmaps
{
    /// <summary>Maps heights to 16-bit values: <paramref name="min"/> is 0, <paramref name="max"/> is 65535.</summary>
    public static ushort[] Quantize(ReadOnlySpan<float> heights, float min, float max)
    {
        var values = new ushort[heights.Length];
        float scale = max > min ? 65535f / (max - min) : 0;
        for (int i = 0; i < heights.Length; i++) values[i] = (ushort)Math.Clamp(MathF.Round((heights[i] - min) * scale), 0, 65535);
        return values;
    }

    /// <summary>Writes 16-bit values as RAW: little-endian, row by row, no header.</summary>
    public static byte[] ToRaw(ReadOnlySpan<ushort> values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    /// <summary>Writes 16-bit values as a grayscale PNG (bit depth 16, each row with the filter that compresses it best).</summary>
    public static byte[] ToPng(ReadOnlySpan<ushort> values, int width, int height)
    {
        if (values.Length != width * height) throw new ArgumentException("There must be width × height values.", nameof(values));
        int rowBytes = width * 2;
        var raw = new byte[rowBytes * height];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(i * 2), values[i]);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var filtered = new byte[rowBytes];
            var best = new byte[rowBytes];
            for (int row = 0; row < height; row++)
            {
                var current = raw.AsSpan(row * rowBytes, rowBytes);
                var above = row > 0 ? raw.AsSpan((row - 1) * rowBytes, rowBytes) : default;
                int bestFilter = 0;
                long bestScore = long.MaxValue;
                for (int filter = 0; filter <= 4; filter++)
                {
                    Filter(filter, current, above, filtered, 2);
                    long score = 0;
                    foreach (byte b in filtered) score += (sbyte)b < 0 ? -(sbyte)b : b;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    bestFilter = filter;
                    filtered.CopyTo(best, 0);
                }
                zlib.WriteByte((byte)bestFilter);
                zlib.Write(best);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 16; // bit depth
        header[9] = 0; // grayscale
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Filter(int filter, ReadOnlySpan<byte> current, ReadOnlySpan<byte> above, Span<byte> output, int bpp)
    {
        for (int i = 0; i < current.Length; i++)
        {
            int a = i >= bpp ? current[i - bpp] : 0;
            int b = above.IsEmpty ? 0 : above[i];
            int c = i >= bpp && !above.IsEmpty ? above[i - bpp] : 0;
            int predicted = filter switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            output[i] = (byte)(current[i] - predicted);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        // The CRC covers the type and the data.
        uint crc = Crc32(typeBytes, 0xFFFFFFFF, finish: false);
        crc = Crc32(data, crc, finish: true);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] s_crcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc, bool finish)
    {
        foreach (byte b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return finish ? crc ^ 0xFFFFFFFF : crc;
    }
}
