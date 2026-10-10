using System.IO.Compression;
using System.Text;

namespace ParagliderToolbox.Tests.Terrain;

/// <summary>An image of a <see cref="TestTiff"/>: its size, tiling and encoding, and the raw bytes of each tile.</summary>
internal sealed record TestImage(int Width, int Height, int TileWidth, int TileHeight, int Bits, int SampleFormat, int Samples,
    int Compression, int Predictor, Func<int, int, byte[]> Tile, int Photometric = 1);

/// <summary>Writes small tiled little-endian GeoTIFFs for the reader's tests (with LZW, Deflate and the predictors).</summary>
internal static class TestTiff
{
    public static byte[] Create(IReadOnlyList<TestImage> images, double originX, double originY, double pixel, int epsg,
        bool pixelIsPoint = false, string? noData = null)
    {
        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(0u);
        long patch = 4;
        for (int index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var offsets = new List<uint>();
            var counts = new List<uint>();
            for (int ty = 0; ty < (image.Height + image.TileHeight - 1) / image.TileHeight; ty++)
            {
                for (int tx = 0; tx < (image.Width + image.TileWidth - 1) / image.TileWidth; tx++)
                {
                    var raw = image.Tile(tx, ty);
                    var data = image.Compression switch
                    {
                        5 => EncodeLzw(Predict(raw, image)),
                        8 => Deflate(Predict(raw, image)),
                        7 => raw,
                        _ => Predict(raw, image),
                    };
                    offsets.Add((uint)stream.Position);
                    counts.Add((uint)data.Length);
                    w.Write(data);
                }
            }
            if (stream.Position % 2 == 1) w.Write((byte)0);

            var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>();
            void Short(ushort tag, params ushort[] values) => entries.Add((tag, 3, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray()));
            void Long(ushort tag, params uint[] values) => entries.Add((tag, 4, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray()));
            void Double(ushort tag, params double[] values) => entries.Add((tag, 12, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray()));
            if (index > 0) Long(254, 1);
            Short(256, (ushort)image.Width);
            Short(257, (ushort)image.Height);
            Short(258, Enumerable.Repeat((ushort)image.Bits, image.Samples).ToArray());
            Short(259, (ushort)image.Compression);
            Short(262, (ushort)image.Photometric);
            Short(277, (ushort)image.Samples);
            Short(284, 1);
            Short(317, (ushort)image.Predictor);
            Short(322, (ushort)image.TileWidth);
            Short(323, (ushort)image.TileHeight);
            Long(324, offsets.ToArray());
            Long(325, counts.ToArray());
            Short(339, Enumerable.Repeat((ushort)image.SampleFormat, image.Samples).ToArray());
            if (index == 0)
            {
                Double(33550, pixel, pixel, 0);
                Double(33922, 0, 0, 0, originX, originY, 0);
                ushort geographic = (ushort)(epsg == 4326 ? 1 : 0);
                Short(34735, 1, 1, 0, 3, 1024, 0, 1, (ushort)(geographic == 1 ? 2 : 1), 1025, 0, 1, (ushort)(pixelIsPoint ? 2 : 1),
                    (ushort)(geographic == 1 ? 2048 : 3072), 0, 1, (ushort)epsg);
                if (noData != null) entries.Add((42113, 2, (uint)noData.Length + 1, Encoding.ASCII.GetBytes(noData + "\0")));
            }
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            long ifd = stream.Position;
            Patch(stream, patch, (uint)ifd);
            long data2 = ifd + 2 + entries.Count * 12 + 4;
            var pending = new List<byte[]>();
            w.Write((ushort)entries.Count);
            foreach (var (tag, type, count, value) in entries)
            {
                w.Write(tag);
                w.Write(type);
                w.Write(count);
                if (value.Length <= 4)
                {
                    w.Write(value);
                    for (int i = value.Length; i < 4; i++) w.Write((byte)0);
                }
                else
                {
                    w.Write((uint)data2);
                    pending.Add(value);
                    data2 += value.Length + value.Length % 2;
                }
            }
            patch = stream.Position;
            w.Write(0u);
            foreach (var value in pending)
            {
                w.Write(value);
                if (value.Length % 2 == 1) w.Write((byte)0);
            }
        }
        w.Flush();
        return stream.ToArray();
    }

    private static void Patch(MemoryStream stream, long at, uint value)
    {
        long here = stream.Position;
        stream.Position = at;
        stream.Write(BitConverter.GetBytes(value));
        stream.Position = here;
    }

    // The inverse of the reader's predictors, row by row.
    private static byte[] Predict(byte[] raw, TestImage image)
    {
        var data = (byte[])raw.Clone();
        int bytes = image.Bits / 8, rowSamples = image.TileWidth * image.Samples, rowBytes = rowSamples * bytes;
        for (int r = 0; r < image.TileHeight; r++)
        {
            var row = data.AsSpan(r * rowBytes, rowBytes);
            if (image.Predictor == 2 && bytes == 2)
            {
                var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row);
                for (int i = values.Length - 1; i >= image.Samples; i--) values[i] -= values[i - image.Samples];
            }
            else if (image.Predictor == 3)
            {
                var planes = new byte[rowBytes];
                for (int s = 0; s < rowSamples; s++)
                {
                    for (int p = 0; p < bytes; p++) planes[p * rowSamples + s] = row[s * bytes + (bytes - 1 - p)];
                }
                for (int i = rowBytes - 1; i >= image.Samples; i--) planes[i] -= planes[i - image.Samples];
                planes.CopyTo(row);
            }
        }
        return data;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>Encodes TIFF LZW (as libtiff: MSB-first codes, wider once the next free code passes the width).</summary>
    public static byte[] EncodeLzw(byte[] data)
    {
        var output = new List<byte>();
        uint buffer = 0;
        int buffered = 0, bits = 9;
        void Put(int code)
        {
            buffer = (buffer << bits) | (uint)code;
            buffered += bits;
            while (buffered >= 8)
            {
                output.Add((byte)(buffer >> (buffered - 8)));
                buffered -= 8;
            }
        }
        var table = new Dictionary<(int, byte), int>();
        int next = 258;
        Put(256);
        int current = -1;
        foreach (byte b in data)
        {
            if (current < 0)
            {
                current = b;
                continue;
            }
            if (table.TryGetValue((current, b), out int code))
            {
                current = code;
                continue;
            }
            Put(current);
            table[(current, b)] = next++;
            if (next == 4094)
            {
                Put(256);
                table.Clear();
                next = 258;
                bits = 9;
            }
            else if (next > (1 << bits) - 1 && bits < 12)
            {
                bits++;
            }
            current = b;
        }
        if (current >= 0)
        {
            // The decoder adds an entry for the last code too, which may widen the end code.
            Put(current);
            next++;
            if (next > (1 << bits) - 1 && bits < 12) bits++;
        }
        Put(257);
        if (buffered > 0) output.Add((byte)(buffer << (8 - buffered)));
        return output.ToArray();
    }
}
