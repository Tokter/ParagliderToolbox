using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ParagliderToolbox.Terrain.Data;

/// <summary>
/// Downloads the sources' data and keeps it on disk, so a terrain is built again without the network: whole small
/// resources (catalog queries, map tiles) and byte ranges of large files (Cloud Optimized GeoTIFFs, kept in 64 KiB
/// chunks, so only the parts a terrain needs are ever downloaded: a terrain reads the headers of hundreds of files).
/// </summary>
/// <remarks>
/// <para>
/// Resources are cached by URL (and an optional version, e.g. the catalog's last-modified time of a file).
/// <see cref="GetAsync"/> refreshes a resource older than its maximum age, but falls back to the stale copy when the
/// network fails, so a terrain built once can be rebuilt offline. Byte ranges never expire: the files they come from
/// don't change (new data comes as new files).
/// </para>
/// <para>At most six requests run at once, and failed requests are retried twice.</para>
/// </remarks>
public sealed class DataCache
{
    /// <summary>The size of the chunks large files are kept in.</summary>
    public const int ChunkSize = 64 * 1024;

    private static readonly Lazy<HttpClient> s_client = new(CreateHttpClient);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _requests = new(6);
    private readonly ConcurrentDictionary<string, HttpRangeReader> _readers = new();
    private long _downloaded;
    private int _requestCount;

    /// <summary>Initializes a cache in <paramref name="folder"/> (created when first written to).</summary>
    /// <param name="folder">The cache folder.</param>
    /// <param name="client">The HTTP client, or <c>null</c> for a shared one.</param>
    public DataCache(string folder, HttpClient? client = null)
    {
        Folder = folder;
        _client = client ?? s_client.Value;
    }

    /// <summary>Gets the cache folder.</summary>
    public string Folder { get; }

    /// <summary>Gets the bytes downloaded since the cache was created.</summary>
    public long BytesDownloaded => Interlocked.Read(ref _downloaded);

    /// <summary>Gets the number of HTTP requests made since the cache was created.</summary>
    public int RequestCount => _requestCount;

    /// <summary>Gets or sets whether the network is used; when <c>false</c> only cached data is read.</summary>
    public bool IsOffline { get; set; }

    /// <summary>Creates the shared HTTP client (identifies the toolbox to the servers).</summary>
    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 6,
        })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ParagliderToolbox", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(terrain generator)"));
        return client;
    }

    /// <summary>
    /// Gets a whole resource: from the cache when it is younger than <paramref name="maxAge"/>, else downloaded (and
    /// cached). Returns <c>null</c> when the server has none (404), which is remembered as well.
    /// </summary>
    /// <exception cref="HttpRequestException">The download failed and nothing is cached.</exception>
    public async Task<byte[]?> GetAsync(string url, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(Folder, "http", Hash(url));
        string data = path + ".bin", missing = path + ".404";
        var cached = File.Exists(data) ? data : File.Exists(missing) ? missing : null;
        if (cached != null && (IsOffline || DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < maxAge))
        {
            return cached == data ? await File.ReadAllBytesAsync(data, cancellationToken) : null;
        }
        if (IsOffline) return null;

        try
        {
            var (status, bytes) = await SendAsync(url, null, cancellationToken);
            if (status == HttpStatusCode.NotFound)
            {
                await WriteAtomicAsync(missing, [], cancellationToken);
                TryDelete(data);
                return null;
            }
            await WriteAtomicAsync(data, bytes, cancellationToken);
            TryDelete(missing);
            return bytes;
        }
        catch (Exception e) when (cached != null && e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Offline: the stale copy is better than nothing.
            return cached == data ? await File.ReadAllBytesAsync(data, cancellationToken) : null;
        }
    }

    /// <summary>Gets a reader of byte ranges of <paramref name="url"/>, cached in chunks.</summary>
    /// <param name="url">The file's URL.</param>
    /// <param name="version">A version of the file (e.g. its last-modified time), so a changed file isn't read from old chunks.</param>
    public IRangeReader OpenRanges(string url, string? version = null) =>
        _readers.GetOrAdd(url + "#" + version, _ => new HttpRangeReader(this, url, Path.Combine(Folder, $"ranges-{ChunkSize / 1024}k", Hash(url + "#" + version))));

    /// <summary>Gets the size of the cached data (bytes).</summary>
    public long GetSize()
    {
        if (!Directory.Exists(Folder)) return 0;
        long size = 0;
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories)) size += file.Length;
        return size;
    }

    /// <summary>Deletes everything cached.</summary>
    public void Clear()
    {
        _readers.Clear();
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }

    // One request (with a byte range or not), retried twice on network errors and server failures.
    internal async Task<(HttpStatusCode Status, byte[] Data)> SendAsync(string url, (long From, long To)? range, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            await _requests.WaitAsync(cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (range is { } r) request.Headers.Range = new RangeHeaderValue(r.From, r.To);
                Interlocked.Increment(ref _requestCount);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    return (response.StatusCode, []);
                }
                if ((int)response.StatusCode >= 500 && attempt < 2) throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                Interlocked.Add(ref _downloaded, bytes.Length);
                // A server that ignores the range sends the whole file.
                if (range is { } wanted && response.StatusCode == HttpStatusCode.OK)
                {
                    long from = Math.Min(wanted.From, bytes.Length), to = Math.Min(wanted.To + 1, bytes.Length);
                    bytes = bytes[(int)from..(int)to];
                }
                return (response.StatusCode, bytes);
            }
            catch (Exception e) when (attempt < 2 && !cancellationToken.IsCancellationRequested &&
                                      (e is HttpRequestException or TaskCanceledException or IOException))
            {
                // Retried below, after a pause.
            }
            finally
            {
                _requests.Release();
            }
            await Task.Delay(attempt == 0 ? 500 : 2000, cancellationToken);
        }
    }

    internal static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllBytesAsync(temp, data, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();

    /// <summary>Reads byte ranges of a remote file through the cache's chunks; consecutive missing chunks are fetched in one request.</summary>
    private sealed class HttpRangeReader(DataCache cache, string url, string folder) : IRangeReader
    {
        private readonly SemaphoreSlim _fetching = new(1);

        public string Name => url;

        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default)
        {
            if (count <= 0) return [];
            // A file the server doesn't have (e.g. a Copernicus tile over the sea) is remembered for a month.
            string missingMarker = Path.Combine(folder, "missing.404");
            if (File.Exists(missingMarker) && (cache.IsOffline || DateTime.UtcNow - File.GetLastWriteTimeUtc(missingMarker) < TimeSpan.FromDays(30)))
            {
                throw new FileNotFoundException($"{url} doesn't exist.", url);
            }
            long first = offset / ChunkSize, last = (offset + count - 1) / ChunkSize;
            var chunks = new byte[last - first + 1][];
            var missing = new List<long>();
            for (long k = first; k <= last; k++)
            {
                if (await TryReadChunkAsync(k, cancellationToken) is { } chunk) chunks[k - first] = chunk;
                else missing.Add(k);
            }
            if (missing.Count > 0)
            {
                if (cache.IsOffline) throw new HttpRequestException($"Offline, and {url} isn't cached.");
                await _fetching.WaitAsync(cancellationToken);
                try
                {
                    await FetchAsync(missing, first, chunks, cancellationToken);
                }
                finally
                {
                    _fetching.Release();
                }
            }

            // Copy the range out of the chunks; a short chunk is the end of the file.
            var result = new byte[count];
            int written = 0;
            for (long k = first; k <= last && written < count; k++)
            {
                var chunk = chunks[k - first];
                int start = k == first ? (int)(offset - first * ChunkSize) : 0;
                if (start >= chunk.Length) break;
                int n = Math.Min(chunk.Length - start, count - written);
                Array.Copy(chunk, start, result, written, n);
                written += n;
                if (chunk.Length < ChunkSize) break;
            }
            return written == count ? result : result[..written];
        }

        private async Task FetchAsync(List<long> missing, long first, byte[][] chunks, CancellationToken cancellationToken)
        {
            // Another reader may have fetched them while this one waited.
            for (int i = missing.Count - 1; i >= 0; i--)
            {
                if (await TryReadChunkAsync(missing[i], cancellationToken) is { } chunk)
                {
                    chunks[missing[i] - first] = chunk;
                    missing.RemoveAt(i);
                }
            }
            int index = 0;
            while (index < missing.Count)
            {
                int end = index;
                while (end + 1 < missing.Count && missing[end + 1] == missing[end] + 1) end++;
                long from = missing[index] * ChunkSize, to = (missing[end] + 1) * ChunkSize - 1;
                var (status, data) = await cache.SendAsync(url, (from, to), cancellationToken);
                if (status == HttpStatusCode.NotFound)
                {
                    await WriteAtomicAsync(Path.Combine(folder, "missing.404"), [], cancellationToken);
                    throw new FileNotFoundException($"{url} doesn't exist.", url);
                }
                for (int i = index; i <= end; i++)
                {
                    long k = missing[i];
                    int start = (int)((k - missing[index]) * ChunkSize);
                    var chunk = start >= data.Length ? [] : data[start..Math.Min(start + ChunkSize, data.Length)];
                    chunks[k - first] = chunk;
                    await WriteAtomicAsync(ChunkPath(k), chunk, cancellationToken);
                }
                index = end + 1;
            }
            if (!File.Exists(Path.Combine(folder, "url.txt")))
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "url.txt"), url, cancellationToken);
            }
        }

        private string ChunkPath(long index) => Path.Combine(folder, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");

        private async Task<byte[]?> TryReadChunkAsync(long index, CancellationToken cancellationToken)
        {
            string path = ChunkPath(index);
            try
            {
                return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
