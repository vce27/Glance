using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Velopack.Sources;

namespace Glance.App;

/// <summary>
/// Velopack file downloader with HTTP Range multi-part transfer
/// (same idea as PCL2's ModNet multi-thread engine, much smaller).
/// Falls back to single-stream when Range is unsupported or the file is small.
/// </summary>
internal sealed class MultiThreadFileDownloader : HttpClientFileDownloader
{
    private const long MinBytesForParallel = 2 * 1024 * 1024; // 2 MB
    private readonly int _parts;

    public MultiThreadFileDownloader(int parts = 8)
    {
        _parts = Math.Clamp(parts, 2, 16);
    }

    public override async Task DownloadFile(
        string url,
        string targetFile,
        Action<int> progress,
        IDictionary<string, string>? headers = null,
        double timeout = 30,
        CancellationToken cancelToken = default)
    {
        try
        {
            if (await TryDownloadParallelAsync(url, targetFile, progress, headers, timeout, cancelToken)
                    .ConfigureAwait(false))
                return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("parallel download failed, falling back: " + ex.Message);
            try { if (File.Exists(targetFile)) File.Delete(targetFile); } catch { /* ignore */ }
        }

        await base.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryDownloadParallelAsync(
        string url,
        string targetFile,
        Action<int> progress,
        IDictionary<string, string>? headers,
        double timeout,
        CancellationToken cancelToken)
    {
        using var client = CreateHttpClient(headers, timeout);

        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using var probe = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancelToken)
            .ConfigureAwait(false);

        // Some CDNs reject HEAD — fall back to GET headers only.
        long? length = null;
        var acceptsRanges = false;
        if (probe.IsSuccessStatusCode)
        {
            length = probe.Content.Headers.ContentLength;
            acceptsRanges = SupportsRanges(probe);
        }
        else
        {
            using var get = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancelToken)
                .ConfigureAwait(false);
            if (!get.IsSuccessStatusCode) return false;
            length = get.Content.Headers.ContentLength;
            acceptsRanges = SupportsRanges(get);
            // Do not consume body — abandon and re-request with Range.
        }

        if (!acceptsRanges || length is null || length.Value < MinBytesForParallel)
            return false;

        var total = length.Value;
        var partSize = (total + _parts - 1) / _parts;
        var tempDir = Path.Combine(Path.GetTempPath(), "GlanceDl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var partFiles = new string[_parts];
            var partBytes = new long[_parts];
            var done = 0L;
            var lastPct = -1;
            var gate = new object();

            void Report()
            {
                var pct = total <= 0 ? 0 : (int)(Interlocked.Read(ref done) * 100 / total);
                if (pct - lastPct < 2 && pct < 100) return;
                lastPct = pct;
                progress(Math.Clamp(pct, 0, 100));
            }

            var tasks = new Task[_parts];
            for (var i = 0; i < _parts; i++)
            {
                var index = i;
                var start = index * partSize;
                var end = Math.Min(total - 1, start + partSize - 1);
                partFiles[index] = Path.Combine(tempDir, $"part-{index:D2}.bin");

                tasks[index] = Task.Run(async () =>
                {
                    using var partClient = CreateHttpClient(headers, timeout);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new RangeHeaderValue(start, end);
                    using var resp = await partClient.SendAsync(
                        req, HttpCompletionOption.ResponseHeadersRead, cancelToken).ConfigureAwait(false);
                    if (resp.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
                        throw new InvalidOperationException($"Range download HTTP {(int)resp.StatusCode}");

                    await using var src = await resp.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
                    await using var dst = new FileStream(
                        partFiles[index], FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, true);
                    var buffer = new byte[80 * 1024];
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancelToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), cancelToken).ConfigureAwait(false);
                        Interlocked.Add(ref partBytes[index], read);
                        Interlocked.Add(ref done, read);
                        lock (gate) Report();
                    }
                }, cancelToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var dir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using (var output = new FileStream(
                             targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 80 * 1024, true))
            {
                for (var i = 0; i < _parts; i++)
                {
                    await using var part = new FileStream(
                        partFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read, 80 * 1024, true);
                    await part.CopyToAsync(output, cancelToken).ConfigureAwait(false);
                }
            }

            if (lastPct < 100) progress(100);
            return true;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static bool SupportsRanges(HttpResponseMessage response)
    {
        if (response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase))
            return true;
        if (response.StatusCode == HttpStatusCode.PartialContent)
            return true;
        // GitHub CDN often omits Accept-Ranges on HEAD; Content-Length alone is not enough —
        // caller still tries Range and falls back on failure.
        return response.Content.Headers.ContentLength is > MinBytesForParallel;
    }
}
