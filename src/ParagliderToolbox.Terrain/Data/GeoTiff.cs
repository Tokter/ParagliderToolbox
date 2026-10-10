using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ParagliderToolbox.Terrain.Data;

/// <summary>How the samples of a TIFF image are stored.</summary>
public enum TiffSampleFormat
{
    /// <summary>Unsigned integers.</summary>
    UnsignedInteger = 1,
    /// <summary>Signed integers.</summary>
    SignedInteger = 2,
    /// <summary>IEEE floating point.</summary>
    FloatingPoint = 3,
}

/// <summary>One image of a TIFF file (the full resolution or an overview): its size, blocks (tiles or strips) and encoding.</summary>
public sealed class TiffImage
{
    internal TiffImage()
    {
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; internal init; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; internal init; }

    /// <summary>Gets the width of a block (a tile, or the whole width for strips).</summary>
    public int BlockWidth { get; internal init; }

    /// <summary>Gets the height of a block (a tile, or the rows per strip).</summary>
    public int BlockHeight { get; internal init; }

    /// <summary>Gets the number of blocks across.</summary>
    public int BlocksAcross => (Width + BlockWidth - 1) / BlockWidth;

    /// <summary>Gets the number of blocks down.</summary>
    public int BlocksDown => (Height + BlockHeight - 1) / BlockHeight;

    /// <summary>Gets whether the blocks are tiles (otherwise strips, which may be shorter at the bottom).</summary>
    public bool IsTiled { get; internal init; }

    /// <summary>Gets the samples per pixel.</summary>
    public int SamplesPerPixel { get; internal init; } = 1;

    /// <summary>Gets the bits per sample.</summary>
    public int BitsPerSample { get; internal init; } = 1;

    /// <summary>Gets the sample format.</summary>
    public TiffSampleFormat SampleFormat { get; internal init; } = TiffSampleFormat.UnsignedInteger;

    /// <summary>Gets the compression (1 none, 5 LZW, 7 JPEG, 8 Deflate, 32773 PackBits).</summary>
    public int Compression { get; internal init; } = 1;

    /// <summary>Gets the predictor (1 none, 2 horizontal, 3 floating point).</summary>
    public int Predictor { get; internal init; } = 1;

    /// <summary>Gets the photometric interpretation (0/1 gray, 2 RGB, 6 YCbCr).</summary>
    public int Photometric { get; internal init; } = 1;

    /// <summary>Gets whether the image is a transparency mask (skipped).</summary>
    public bool IsMask { get; internal init; }

    internal int PlanarConfiguration { get; init; } = 1;
    internal long[] Offsets { get; init; } = [];
    internal long[] ByteCounts { get; init; } = [];
    internal byte[]? JpegTables { get; init; }
}

/// <summary>
/// A GeoTIFF (and Cloud Optimized GeoTIFF) reader: the images (full resolution and overviews), the georeferencing and
/// the decoded blocks, read through an <see cref="IRangeReader"/> so only the blocks needed are fetched.
/// </summary>
/// <remarks>
/// Supports classic TIFF and BigTIFF in either byte order; tiles and strips; no compression, LZW, Deflate, PackBits
/// and JPEG (also with shared tables and YCbCr); the horizontal and the floating point predictor; 8 to 64 bit integer
/// and floating point samples (<see cref="ReadElevationBlockAsync"/>), 8 bit gray, RGB and RGBA
/// (<see cref="ReadColorBlockAsync"/>). The georeferencing is read from ModelTiepoint and ModelPixelScale (or a
/// ModelTransformation without rotation), the coordinate system's EPSG code and pixel-is-point from the GeoKeys, and
/// the no-data value from GDAL's tag.
/// </remarks>
public sealed class GeoTiff
{
    private const int HeaderBytes = 64 * 1024;

    private readonly IRangeReader _reader;
    private readonly bool _bigEndian;
    private byte[] _header = [];

    private GeoTiff(IRangeReader reader, bool bigEndian)
    {
        _reader = reader;
        _bigEndian = bigEndian;
    }

    /// <summary>Gets the file's name.</summary>
    public string Name => _reader.Name;

    /// <summary>Gets the images: the full resolution first, then the overviews from fine to coarse.</summary>
    public IReadOnlyList<TiffImage> Images { get; private set; } = [];

    /// <summary>Gets the EPSG code of the coordinate system (projected, else geographic), or <c>null</c>.</summary>
    public int? Epsg { get; private set; }

    /// <summary>Gets the x of the full resolution's upper left pixel corner, in the coordinate system's units.</summary>
    public double OriginX { get; private set; }

    /// <summary>Gets the y of the full resolution's upper left pixel corner.</summary>
    public double OriginY { get; private set; }

    /// <summary>Gets the full resolution's pixel width.</summary>
    public double PixelWidth { get; private set; } = 1;

    /// <summary>Gets the full resolution's pixel height (positive: rows run towards smaller y).</summary>
    public double PixelHeight { get; private set; } = 1;

    /// <summary>Gets the no-data value, or <c>null</c>.</summary>
    public double? NoData { get; private set; }

    /// <summary>Reads the file's structure (its first 64 KiB, more if the directories need it).</summary>
    /// <exception cref="InvalidDataException">The file isn't a TIFF.</exception>
    public static async Task<GeoTiff> OpenAsync(IRangeReader reader, CancellationToken cancellationToken = default)
    {
        var header = await reader.ReadAsync(0, HeaderBytes, cancellationToken);
        if (header.Length < 8) throw new InvalidDataException($"{reader.Name} isn't a TIFF file.");
        bool bigEndian = header[0] == 'M' && header[1] == 'M';
        if (!bigEndian && !(header[0] == 'I' && header[1] == 'I')) throw new InvalidDataException($"{reader.Name} isn't a TIFF file.");
        var tiff = new GeoTiff(reader, bigEndian) { _header = header };
        await tiff.ReadDirectoriesAsync(cancellationToken);
        return tiff;
    }

    /// <summary>Gets the size of an image's pixels in the coordinate system's units (the overviews' are larger).</summary>
    public (double Width, double Height) PixelSize(int image) =>
        (PixelWidth * Images[0].Width / Images[image].Width, PixelHeight * Images[0].Height / Images[image].Height);

    /// <summary>Gets the coordinates of the center of an image's upper left pixel.</summary>
    public (double X, double Y) FirstPixelCenter(int image)
    {
        var (w, h) = PixelSize(image);
        return (OriginX + w / 2, OriginY - h / 2);
    }

    /// <summary>
    /// Reads a block of an image as heights: one value per pixel of the block (row by row, <see cref="TiffImage.BlockWidth"/>
    /// wide), <see cref="float.NaN"/> where there is no data.
    /// </summary>
    public async Task<float[]> ReadElevationBlockAsync(int image, int blockX, int blockY, CancellationToken cancellationToken = default)
    {
        var info = Images[image];
        var (data, rows) = await ReadBlockBytesAsync(info, blockX, blockY, cancellationToken);
        int count = info.BlockWidth * info.BlockHeight;
        var values = new float[count];
        if (info.Compression == 7)
        {
            var pixels = DecodeJpeg(info, data);
            for (int i = 0; i < Math.Min(count, pixels.Length); i++) values[i] = pixels[i] & 0xFF;
            return values;
        }
        int spp = info.SamplesPerPixel, bytes = info.BitsPerSample / 8;
        int available = Math.Min(count, rows * info.BlockWidth);
        float noData = NoData is { } nd ? (float)nd : float.NaN;
        bool hasNoData = NoData.HasValue;
        for (int i = 0; i < available; i++)
        {
            int at = i * spp * bytes;
            if (at + bytes > data.Length) break;
            float v = (info.SampleFormat, bytes) switch
            {
                (TiffSampleFormat.FloatingPoint, 4) => BitConverter.ToSingle(data, at),
                (TiffSampleFormat.FloatingPoint, 8) => (float)BitConverter.ToDouble(data, at),
                (TiffSampleFormat.SignedInteger, 1) => (sbyte)data[at],
                (TiffSampleFormat.SignedInteger, 2) => BitConverter.ToInt16(data, at),
                (TiffSampleFormat.SignedInteger, 4) => BitConverter.ToInt32(data, at),
                (_, 1) => data[at],
                (_, 2) => BitConverter.ToUInt16(data, at),
                (_, 4) => BitConverter.ToUInt32(data, at),
                _ => throw new NotSupportedException($"{Name}: {info.BitsPerSample} bit samples aren't supported."),
            };
            values[i] = hasNoData && (v == noData || Math.Abs(v - noData) <= Math.Abs(noData) * 1e-6f) ? float.NaN : v;
        }
        for (int i = available; i < count; i++) values[i] = float.NaN;
        return values;
    }

    /// <summary>
    /// Reads a block of an image as colors: RGBA packed into a <see cref="uint"/> per pixel (red in the lowest byte),
    /// alpha 0 where there is no data.
    /// </summary>
    public async Task<uint[]> ReadColorBlockAsync(int image, int blockX, int blockY, CancellationToken cancellationToken = default)
    {
        var info = Images[image];
        var (data, rows) = await ReadBlockBytesAsync(info, blockX, blockY, cancellationToken);
        int count = info.BlockWidth * info.BlockHeight;
        if (info.Compression == 7) return DecodeJpeg(info, data);
        if (info.BitsPerSample != 8) throw new NotSupportedException($"{Name}: only 8 bit color images are supported.");
        int spp = info.SamplesPerPixel;
        int noData = NoData is { } nd ? (int)Math.Round(nd) : -1;
        var colors = new uint[count];
        int available = Math.Min(count, rows * info.BlockWidth);
        for (int i = 0; i < available; i++)
        {
            int at = i * spp;
            if (at + spp > data.Length) break;
            uint r = data[at], g = spp >= 3 ? data[at + 1] : r, b = spp >= 3 ? data[at + 2] : r;
            uint a = spp is 2 or 4 ? data[at + spp - 1] : 255u;
            if (r == noData && g == noData && b == noData) a = 0;
            colors[i] = r | g << 8 | b << 16 | a << 24;
        }
        return colors;
    }

    #region Blocks

    private async Task<(byte[] Data, int Rows)> ReadBlockBytesAsync(TiffImage info, int blockX, int blockY, CancellationToken cancellationToken)
    {
        if (blockX < 0 || blockY < 0 || blockX >= info.BlocksAcross || blockY >= info.BlocksDown) throw new ArgumentOutOfRangeException(nameof(blockX));
        if (info.PlanarConfiguration != 1 && info.SamplesPerPixel > 1) throw new NotSupportedException($"{Name}: separate sample planes aren't supported.");
        int index = blockY * info.BlocksAcross + blockX;
        int rows = info.IsTiled ? info.BlockHeight : Math.Min(info.BlockHeight, info.Height - blockY * info.BlockHeight);
        long offset = info.Offsets[index], length = info.ByteCounts[index];
        if (length <= 0) return ([], 0);
        byte[] raw = await _reader.ReadAsync(offset, (int)length, cancellationToken);
        if (info.Compression == 7) return (raw, rows);

        int bytesPerSample = Math.Max(1, info.BitsPerSample / 8);
        int rowBytes = info.BlockWidth * info.SamplesPerPixel * bytesPerSample;
        byte[] data = info.Compression switch
        {
            1 => raw,
            5 => Decode(raw, rowBytes * rows, Codecs.DecodeLzw),
            8 or 32946 => DecodeDeflate(raw, rowBytes * rows),
            32773 => Decode(raw, rowBytes * rows, Codecs.DecodePackBits),
            _ => throw new NotSupportedException($"{Name}: compression {info.Compression} isn't supported."),
        };
        if (data.Length < rowBytes * rows) Array.Resize(ref data, rowBytes * rows);

        if (info.Predictor == 3)
        {
            UndoFloatingPointPredictor(data, info.BlockWidth * info.SamplesPerPixel, rows, info.SamplesPerPixel, bytesPerSample);
        }
        else
        {
            if (_bigEndian && bytesPerSample > 1) SwapBytes(data, bytesPerSample);
            if (info.Predictor == 2) UndoHorizontalPredictor(data, info.BlockWidth, rows, info.SamplesPerPixel, bytesPerSample);
        }
        return (data, rows);
    }

    private delegate int SpanDecoder(ReadOnlySpan<byte> input, Span<byte> output);

    private static byte[] Decode(byte[] raw, int size, SpanDecoder decoder)
    {
        var output = new byte[size];
        decoder(raw, output);
        return output;
    }

    private static byte[] DecodeDeflate(byte[] raw, int size)
    {
        var output = new byte[size];
        Codecs.DecodeDeflate(raw, output);
        return output;
    }

    private static void SwapBytes(byte[] data, int bytesPerSample)
    {
        for (int i = 0; i + bytesPerSample <= data.Length; i += bytesPerSample) data.AsSpan(i, bytesPerSample).Reverse();
    }

    // Each sample was stored as the difference to the one before it in the row (per channel), in native byte order.
    private static void UndoHorizontalPredictor(byte[] data, int width, int rows, int spp, int bytesPerSample)
    {
        int rowSamples = width * spp;
        for (int r = 0; r < rows; r++)
        {
            int start = r * rowSamples;
            switch (bytesPerSample)
            {
                case 1:
                    for (int i = spp; i < rowSamples; i++) data[start + i] += data[start + i - spp];
                    break;
                case 2:
                {
                    var row = MemoryMarshal.Cast<byte, ushort>(data.AsSpan(start * 2, rowSamples * 2));
                    for (int i = spp; i < row.Length; i++) row[i] += row[i - spp];
                    break;
                }
                case 4:
                {
                    var row = MemoryMarshal.Cast<byte, uint>(data.AsSpan(start * 4, rowSamples * 4));
                    for (int i = spp; i < row.Length; i++) row[i] += row[i - spp];
                    break;
                }
                case 8:
                {
                    var row = MemoryMarshal.Cast<byte, ulong>(data.AsSpan(start * 8, rowSamples * 8));
                    for (int i = spp; i < row.Length; i++) row[i] += row[i - spp];
                    break;
                }
            }
        }
    }

    // The floating point predictor (libtiff's fpAcc): each row's bytes were split into planes from the most significant
    // byte down and differenced byte by byte; the result is in little-endian order whatever the file's byte order.
    private static void UndoFloatingPointPredictor(byte[] data, int rowSamples, int rows, int spp, int bytesPerSample)
    {
        int rowBytes = rowSamples * bytesPerSample;
        var plane = new byte[rowBytes];
        for (int r = 0; r < rows; r++)
        {
            var row = data.AsSpan(r * rowBytes, rowBytes);
            for (int i = spp; i < rowBytes; i++) row[i] += row[i - spp];
            row.CopyTo(plane);
            for (int s = 0; s < rowSamples; s++)
            {
                for (int b = 0; b < bytesPerSample; b++) row[s * bytesPerSample + b] = plane[(bytesPerSample - b - 1) * rowSamples + s];
            }
        }
    }

    private uint[] DecodeJpeg(TiffImage info, byte[] tile)
    {
        // Tiles share the quantization and Huffman tables: put them in front of the tile's own stream.
        byte[] stream = tile;
        if (info.JpegTables is { Length: > 4 } tables && tile.Length > 2)
        {
            stream = new byte[tables.Length - 2 + tile.Length - 2];
            Array.Copy(tables, 0, stream, 0, tables.Length - 2);
            Array.Copy(tile, 2, stream, tables.Length - 2, tile.Length - 2);
        }
        var pixels = new uint[info.BlockWidth * info.BlockHeight];
        if (stream.Length == 0) return pixels;
        using var skData = SKData.CreateCopy(stream);
        using var codec = SKCodec.Create(skData) ?? throw new InvalidDataException($"{Name}: a JPEG tile can't be decoded.");
        int width = Math.Min(codec.Info.Width, info.BlockWidth), height = Math.Min(codec.Info.Height, info.BlockHeight);
        var decoded = new uint[codec.Info.Width * codec.Info.Height];
        var target = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(decoded, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(target, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidDataException($"{Name}: a JPEG tile can't be decoded ({result}).");
        }
        finally
        {
            handle.Free();
        }
        for (int y = 0; y < height; y++) Array.Copy(decoded, y * codec.Info.Width, pixels, y * info.BlockWidth, width);
        return pixels;
    }

    #endregion

    #region Directories

    private async Task<byte[]> ReadBytesAsync(long offset, long count, CancellationToken cancellationToken)
    {
        if (offset + count <= _header.Length) return _header.AsSpan((int)offset, (int)count).ToArray();
        return await _reader.ReadAsync(offset, (int)count, cancellationToken);
    }

    private ushort U16(ReadOnlySpan<byte> b) => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
    private uint U32(ReadOnlySpan<byte> b) => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
    private ulong U64(ReadOnlySpan<byte> b) => _bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);

    private async Task ReadDirectoriesAsync(CancellationToken cancellationToken)
    {
        bool big = U16(_header.AsSpan(2)) == 43;
        if (!big && U16(_header.AsSpan(2)) != 42) throw new InvalidDataException($"{Name} isn't a TIFF file.");
        long next = big ? (long)U64(_header.AsSpan(8)) : U32(_header.AsSpan(4));
        var images = new List<TiffImage>();
        var visited = new HashSet<long>();
        bool first = true;
        while (next > 0 && visited.Add(next) && images.Count < 64)
        {
            var tags = await ReadDirectoryAsync(next, big, cancellationToken);
            next = tags.Next;
            var image = await CreateImageAsync(tags.Entries, big, first, cancellationToken);
            first = false;
            if (!image.IsMask) images.Add(image);
        }
        if (images.Count == 0) throw new InvalidDataException($"{Name} has no image.");
        // The full resolution first, then the overviews from fine to coarse.
        Images = images.Take(1).Concat(images.Skip(1).Where(i => i.Width < images[0].Width).OrderByDescending(i => i.Width)).ToList();
    }

    private readonly record struct Entry(ushort Tag, ushort Type, long Count, byte[] Value);

    private async Task<(List<Entry> Entries, long Next)> ReadDirectoryAsync(long offset, bool big, CancellationToken cancellationToken)
    {
        int countSize = big ? 8 : 2, entrySize = big ? 20 : 12, offsetSize = big ? 8 : 4;
        var countBytes = await ReadBytesAsync(offset, countSize, cancellationToken);
        long count = big ? (long)U64(countBytes) : U16(countBytes);
        var block = await ReadBytesAsync(offset + countSize, count * entrySize + offsetSize, cancellationToken);
        var entries = new List<Entry>((int)count);
        for (int i = 0; i < count; i++)
        {
            var e = block.AsSpan(i * entrySize, entrySize);
            ushort tag = U16(e), type = U16(e[2..]);
            long n = big ? (long)U64(e[4..]) : U32(e[4..]);
            long size = TypeSize(type) * n;
            var inline = e[(big ? 12 : 8)..];
            byte[] value;
            if (size <= offsetSize)
            {
                value = inline[..(int)size].ToArray();
            }
            else
            {
                long at = big ? (long)U64(inline) : U32(inline);
                value = await ReadBytesAsync(at, size, cancellationToken);
            }
            entries.Add(new Entry(tag, type, n, value));
        }
        var tail = block.AsSpan((int)(count * entrySize));
        long next = big ? (long)U64(tail) : U32(tail);
        return (entries, next);
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 1,
    };

    private long[] Integers(Entry e)
    {
        int size = TypeSize(e.Type);
        var values = new long[e.Count];
        for (int i = 0; i < e.Count; i++)
        {
            var b = e.Value.AsSpan(i * size, size);
            values[i] = e.Type switch
            {
                1 or 7 => b[0],
                6 => (sbyte)b[0],
                3 => U16(b),
                8 => (short)U16(b),
                4 => U32(b),
                9 => (int)U32(b),
                16 or 18 => (long)U64(b),
                17 => (long)U64(b),
                _ => 0,
            };
        }
        return values;
    }

    private double[] Doubles(Entry e)
    {
        int size = TypeSize(e.Type);
        var values = new double[e.Count];
        for (int i = 0; i < e.Count; i++)
        {
            var b = e.Value.AsSpan(i * size, size);
            values[i] = e.Type switch
            {
                11 => BitConverter.Int32BitsToSingle((int)U32(b)),
                12 => BitConverter.Int64BitsToDouble((long)U64(b)),
                5 => U32(b[4..]) == 0 ? 0 : (double)U32(b) / U32(b[4..]),
                10 => (int)U32(b[4..]) == 0 ? 0 : (double)(int)U32(b) / (int)U32(b[4..]),
                _ => Integers(e with { Count = 1, Value = b.ToArray() })[0],
            };
        }
        return values;
    }

    private static string Ascii(Entry e) => System.Text.Encoding.ASCII.GetString(e.Value).TrimEnd('\0', ' ');

    private Task<TiffImage> CreateImageAsync(List<Entry> entries, bool big, bool first, CancellationToken cancellationToken)
    {
        Entry? Find(ushort tag) => entries.FirstOrDefault(e => e.Tag == tag) is { Tag: > 0 } e ? e : null;
        long Int(ushort tag, long fallback) => Find(tag) is { } e && e.Count > 0 ? Integers(e)[0] : fallback;

        int width = (int)Int(256, 0), height = (int)Int(257, 0);
        bool tiled = Find(322) != null;
        int blockWidth = tiled ? (int)Int(322, width) : width;
        int blockHeight = tiled ? (int)Int(323, height) : (int)Math.Min(Int(278, height), height);
        var offsets = Find(tiled ? (ushort)324 : (ushort)273) is { } o ? Integers(o) : [];
        var counts = Find(tiled ? (ushort)325 : (ushort)279) is { } c ? Integers(c) : [];
        var image = new TiffImage
        {
            Width = width,
            Height = height,
            IsTiled = tiled,
            BlockWidth = Math.Max(1, blockWidth),
            BlockHeight = Math.Max(1, blockHeight),
            SamplesPerPixel = (int)Int(277, 1),
            BitsPerSample = (int)Int(258, 1),
            SampleFormat = (TiffSampleFormat)Int(339, 1),
            Compression = (int)Int(259, 1),
            Predictor = (int)Int(317, 1),
            Photometric = (int)Int(262, 1),
            PlanarConfiguration = (int)Int(284, 1),
            IsMask = (Int(254, 0) & 4) != 0,
            Offsets = offsets,
            ByteCounts = counts,
            JpegTables = Find(347)?.Value,
        };

        if (first) ReadGeoreferencing(Find, image);
        return Task.FromResult(image);
    }

    private void ReadGeoreferencing(Func<ushort, Entry?> find, TiffImage image)
    {
        bool pixelIsPoint = false;
        if (find(34735) is { } keyEntry)
        {
            var keys = Integers(keyEntry);
            for (int i = 4; i + 3 < keys.Length; i += 4)
            {
                long id = keys[i], location = keys[i + 1], value = keys[i + 3];
                if (location != 0) continue;
                if (id == 1025) pixelIsPoint = value == 2;
                else if (id == 3072 && value is > 0 and < 32767) Epsg = (int)value;
                else if (id == 2048 && value is > 0 and < 32767 && Epsg == null) Epsg = (int)value;
            }
        }

        if (find(33550) is { } scaleEntry && find(33922) is { } tieEntry)
        {
            var scale = Doubles(scaleEntry);
            var tie = Doubles(tieEntry);
            PixelWidth = scale[0];
            PixelHeight = scale[1];
            OriginX = tie[3] - tie[0] * PixelWidth;
            OriginY = tie[4] + tie[1] * PixelHeight;
        }
        else if (find(34264) is { } transformEntry)
        {
            var m = Doubles(transformEntry);
            PixelWidth = m[0];
            PixelHeight = -m[5];
            OriginX = m[3];
            OriginY = m[7];
        }
        // A point raster's tie point is a pixel's center; keep the corner.
        if (pixelIsPoint)
        {
            OriginX -= PixelWidth / 2;
            OriginY += PixelHeight / 2;
        }

        if (find(42113) is { } noData && double.TryParse(Ascii(noData), NumberStyles.Float, CultureInfo.InvariantCulture, out double value2))
        {
            NoData = value2;
        }
    }

    #endregion
}
