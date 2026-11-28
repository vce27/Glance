using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance.Core;

/// <summary>On-disk Opus-MT language pack under AppData (optional download).</summary>
public sealed class OfflineMtPack
{
    public const string PackVersion = "2";
    public const string DefaultDownloadUrl =
        "https://github.com/vce27/Glance/releases/download/offline-mt-v1/Glance-offline-mt.zip";

    private static readonly string[] DirectionDirs = ["en-zh", "zh-en"];
    private static readonly string[] RequiredFiles =
    [
        "encoder_model_quantized.onnx",
        "decoder_model_merged_quantized.onnx",
        "source.spm",
        "target.spm",
        "vocab.json",
    ];

    private readonly string _root;

    public OfflineMtPack(string? baseDir = null)
    {
        var root = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.harukaon.glance");
        _root = Path.Combine(root, "offline-mt");
    }

    public string Root => _root;
    public string ManifestPath => Path.Combine(_root, "manifest.json");

    public bool IsInstalled()
    {
        if (!File.Exists(ManifestPath)) return false;
        try
        {
            var man = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
            if (man is null || man.Version != PackVersion) return false;
        }
        catch
        {
            return false;
        }

        foreach (var dir in DirectionDirs)
        {
            var folder = Path.Combine(_root, dir);
            foreach (var file in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(folder, file)))
                    return false;
            }
        }

        return true;
    }

    public string DirectionPath(string direction) => Path.Combine(_root, direction);

    public string StatusText()
        => IsInstalled() ? "语言包已安装（英↔中）" : "未安装语言包（需联网下载一次）";

    public async Task InstallAsync(
        string? url,
        IProgress<OfflineMtProgress>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);
        var downloadUrl = string.IsNullOrWhiteSpace(url) ? DefaultDownloadUrl : url.Trim();
        var zipPath = Path.Combine(_root, "Glance-offline-mt.zip.download");
        var extractDir = Path.Combine(_root, "_extract");

        progress?.Report(new OfflineMtProgress(0, "正在下载语言包…"));
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Glance-OfflineMt/1");
            using var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1024 * 256];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                {
                    var pct = (int)Math.Clamp(read * 100 / total, 0, 99);
                    progress?.Report(new OfflineMtProgress(pct, $"正在下载语言包… {pct}%"));
                }
            }
        }

        progress?.Report(new OfflineMtProgress(99, "正在解压…"));
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // Accept either flat en-zh/… or nested Glance-offline-mt/en-zh/…
        var contentRoot = FindContentRoot(extractDir)
            ?? throw new InvalidOperationException("语言包格式无效：缺少 en-zh / zh-en");

        foreach (var dir in DirectionDirs)
        {
            var src = Path.Combine(contentRoot, dir);
            var dest = Path.Combine(_root, dir);
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            try { Directory.Move(src, dest); }
            catch { CopyDirectory(src, dest); }
        }

        var man = new Manifest { Version = PackVersion };
        await File.WriteAllTextAsync(ManifestPath, JsonSerializer.Serialize(man, new JsonSerializerOptions
        {
            WriteIndented = true,
        }), ct);

        try { File.Delete(zipPath); } catch { /* ignore */ }
        try { Directory.Delete(extractDir, recursive: true); } catch { /* ignore */ }

        if (!IsInstalled())
            throw new InvalidOperationException("语言包解压后仍不完整，请重试下载");

        progress?.Report(new OfflineMtProgress(100, "语言包已就绪"));
    }

    public void Uninstall()
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort: remove known pieces.
            foreach (var dir in DirectionDirs)
            {
                var p = Path.Combine(_root, dir);
                if (Directory.Exists(p))
                    Directory.Delete(p, recursive: true);
            }
            if (File.Exists(ManifestPath))
                File.Delete(ManifestPath);
        }
    }

    private static string? FindContentRoot(string extractDir)
    {
        if (LooksLikePackRoot(extractDir))
            return extractDir;
        foreach (var sub in Directory.GetDirectories(extractDir))
        {
            if (LooksLikePackRoot(sub))
                return sub;
        }
        return null;
    }

    private static bool LooksLikePackRoot(string dir)
        => Directory.Exists(Path.Combine(dir, "en-zh"))
           && Directory.Exists(Path.Combine(dir, "zh-en"));

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private sealed class Manifest
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
    }
}

public readonly record struct OfflineMtProgress(int Percent, string Message);
