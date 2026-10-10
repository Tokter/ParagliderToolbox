using System.IO.Compression;

namespace ParagliderToolbox.Terrain.Data;

/// <summary>The TIFF decompressors: LZW (as TIFF writes it), Deflate (zlib) and PackBits.</summary>
internal static class Codecs
{
    /// <summary>Decodes TIFF LZW data (MSB-first codes of 9 to 12 bits, "early change") into <paramref name="output"/>.</summary>
    /// <returns>The number of bytes written.</returns>
    public static int DecodeLzw(ReadOnlySpan<byte> input, Span<byte> output)
    {
        const int clear = 256, end = 257, first = 258, maxCodes = 4096;
        var prefix = new short[maxCodes];
        var suffix = new byte[maxCodes];
        var head = new byte[maxCodes];
        var length = new short[maxCodes];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            head[i] = (byte)i;
            length[i] = 1;
        }

        int bits = 9, next = first, previous = -1, written = 0, position = 0;
        uint buffer = 0;
        int buffered = 0;
        while (true)
        {
            while (buffered < bits)
            {
                if (position >= input.Length) return written;
                buffer = (buffer << 8) | input[position++];
                buffered += 8;
            }
            int code = (int)(buffer >> (buffered - bits)) & ((1 << bits) - 1);
            buffered -= bits;

            if (code == end) return written;
            if (code == clear)
            {
                bits = 9;
                next = first;
                previous = -1;
                continue;
            }
            if (previous < 0)
            {
                if (code > 255) throw new InvalidDataException("Invalid LZW data.");
                if (written < output.Length) output[written++] = (byte)code;
                previous = code;
                continue;
            }

            if (code > next || code >= maxCodes) throw new InvalidDataException("Invalid LZW code.");
            if (next < maxCodes)
            {
                // The new string is the previous one plus the first byte of this one (of itself, for the code being defined).
                prefix[next] = (short)previous;
                suffix[next] = code < next ? head[code] : head[previous];
                head[next] = head[previous];
                length[next] = (short)(length[previous] + 1);
                next++;
            }

            // Write the string backwards from its last byte.
            int count = length[code];
            int stop = Math.Min(written + count, output.Length);
            for (int c = code, p = written + count - 1; c >= 0; c = prefix[c], p--)
            {
                if (p < stop) output[p] = suffix[c];
            }
            written = stop;
            previous = code;
            if (next == (1 << bits) - 1 && bits < 12) bits++;
        }
    }

    /// <summary>Decodes zlib (Deflate) data into <paramref name="output"/>.</summary>
    /// <returns>The number of bytes written.</returns>
    public static int DecodeDeflate(byte[] input, Span<byte> output)
    {
        using var stream = new ZLibStream(new MemoryStream(input), CompressionMode.Decompress);
        int written = 0;
        while (written < output.Length)
        {
            int n = stream.Read(output[written..]);
            if (n == 0) break;
            written += n;
        }
        return written;
    }

    /// <summary>Decodes PackBits data into <paramref name="output"/>.</summary>
    /// <returns>The number of bytes written.</returns>
    public static int DecodePackBits(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int written = 0, position = 0;
        while (position < input.Length && written < output.Length)
        {
            int n = (sbyte)input[position++];
            if (n >= 0)
            {
                int count = Math.Min(n + 1, Math.Min(input.Length - position, output.Length - written));
                input.Slice(position, count).CopyTo(output[written..]);
                position += n + 1;
                written += count;
            }
            else if (n != -128 && position < input.Length)
            {
                byte value = input[position++];
                int count = Math.Min(1 - n, output.Length - written);
                output.Slice(written, count).Fill(value);
                written += count;
            }
        }
        return written;
    }
}
