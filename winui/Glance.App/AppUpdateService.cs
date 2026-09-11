using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace Glance.App;

public sealed class AppUpdateService
{
    /// <summary>GitHub repo that hosts Velopack release assets.</summary>
    public const string GitHubRepoUrl = "https://github.com/vce27/Glance";
    private const string GitHubLatestApi = "https://api.github.com/repos/vce27/Glance/releases/latest";

    private readonly UpdateManager _mgr;
    private readonly HttpClient _http = new();
    private UpdateInfo? _pending;
    private string? _pendingNotes;

    public AppUpdateService(string? localSourceOverride = null)
    {
        IUpdateSource source = !string.IsNullOrWhiteSpace(localSourceOverride)
            ? new SimpleFileSource(new DirectoryInfo(localSourceOverride))
            : new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false);
        _mgr = new UpdateManager(source);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glance", CurrentVersionDisplay));
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool IsInstalled => _mgr.IsInstalled;

    public string CurrentVersionDisplay
    {
        get
        {
            if (_mgr.CurrentVersion is not null)
                return _mgr.CurrentVersion.ToString();
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        }
    }

    public UpdateInfo? PendingUpdate => _pending;
    public string? PendingNotes => _pendingNotes;

    public async Task<UpdateCheckOutcome> CheckAsync(CancellationToken ct = default)
    {
        if (!_mgr.IsInstalled)
        {
            return new UpdateCheckOutcome(
                UpdateCheckKind.NotInstalled,
                CurrentVersionDisplay,
                null,
                "当前为开发/便携构建，需通过安装包运行才支持自动更新");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            _pending = info;
            _pendingNotes = null;
            if (info is null)
            {
                return new UpdateCheckOutcome(
                    UpdateCheckKind.UpToDate,
                    CurrentVersionDisplay,
                    null,
                    "当前已是最新版本");
            }

            var remote = info.TargetFullRelease.Version.ToString();
            _pendingNotes = await FetchReleaseNotesAsync(remote, ct).ConfigureAwait(false);
            var message = string.IsNullOrWhiteSpace(_pendingNotes)
                ? $"发现新版本 {remote}"
                : $"发现新版本 {remote}\n\n更新说明：\n{_pendingNotes}";
            return new UpdateCheckOutcome(
                UpdateCheckKind.UpdateAvailable,
                CurrentVersionDisplay,
                remote,
                message,
                _pendingNotes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("update check failed: " + ex);
            return new UpdateCheckOutcome(
                UpdateCheckKind.Failed,
                CurrentVersionDisplay,
                null,
                "检查更新失败: " + ex.Message);
        }
    }

    public async Task<UpdateApplyOutcome> DownloadAndApplyAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (!_mgr.IsInstalled)
            return new UpdateApplyOutcome(false, "当前构建不支持自动更新");

        try
        {
            var info = _pending ?? await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
                return new UpdateApplyOutcome(false, "当前已是最新版本");

            _pending = info;
            await _mgr.DownloadUpdatesAsync(
                info,
                p => progress?.Report(p),
                ct).ConfigureAwait(false);

            // Exits process after scheduling apply + restart.
            _mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
            return new UpdateApplyOutcome(true, "正在安装更新并重启…");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("update apply failed: " + ex);
            return new UpdateApplyOutcome(false, "更新失败: " + ex.Message);
        }
    }

    private async Task<string?> FetchReleaseNotesAsync(string version, CancellationToken ct)
    {
        try
        {
            // Prefer the matching tag; fall back to latest.
            var urls = new[]
            {
                $"https://api.github.com/repos/vce27/Glance/releases/tags/v{version}",
                GitHubLatestApi,
            };
            foreach (var url in urls)
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("body", out var body)) continue;
                var notes = body.GetString();
                if (string.IsNullOrWhiteSpace(notes)) continue;
                return SanitizeReleaseNotes(notes);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("release notes fetch failed: " + ex.Message);
        }
        return null;
    }

    private static string SanitizeReleaseNotes(string markdown)
    {
        var lines = markdown
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l
                .TrimStart('#', ' ', '-', '*')
                .Replace("**", "")
                .Trim())
            .Where(l => l.Length > 0
                        && !l.StartsWith("下载", StringComparison.Ordinal)
                        && !l.StartsWith("Glance-win-", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("`*.nupkg", StringComparison.Ordinal))
            .Take(12);
        return string.Join("\n", lines.Select(l => "· " + l));
    }
}

public enum UpdateCheckKind
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckOutcome(
    UpdateCheckKind Kind,
    string CurrentVersion,
    string? RemoteVersion,
    string Message,
    string? Notes = null);

public sealed record UpdateApplyOutcome(bool Started, string Message);
