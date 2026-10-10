namespace ParagliderToolbox.Terrain.Data;

/// <summary>Reads byte ranges of a file, local or remote (e.g. the tiles of a Cloud Optimized GeoTIFF).</summary>
public interface IRangeReader
{
    /// <summary>Gets the file's path or URL, for messages.</summary>
    string Name { get; }

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/>; fewer at the end of the file.</summary>
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default);
}

/// <summary>Reads byte ranges of a local file.</summary>
public sealed class FileRangeReader(string path) : IRangeReader
{
    /// <inheritdoc/>
    public string Name => path;

    /// <inheritdoc/>
    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        long length = RandomAccess.GetLength(handle);
        int available = (int)Math.Clamp(length - offset, 0, count);
        var buffer = new byte[available];
        int read = 0;
        while (read < available)
        {
            int n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), offset + read, cancellationToken);
            if (n == 0) break;
            read += n;
        }
        return read == available ? buffer : buffer[..read];
    }
}

/// <summary>Reads byte ranges of a file in memory.</summary>
public sealed class MemoryRangeReader(byte[] data, string name = "memory") : IRangeReader
{
    /// <inheritdoc/>
    public string Name => name;

    /// <summary>Gets how many reads were made.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc/>
    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default)
    {
        Reads++;
        int start = (int)Math.Clamp(offset, 0, data.Length);
        int end = (int)Math.Clamp(offset + count, 0, data.Length);
        return Task.FromResult(data[start..end]);
    }
}
