using System.Diagnostics;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Glance.App;

public sealed class AppUpdateService
{
    /// <summary>GitHub repo that hosts Velopack release assets.</summary>
    public const string GitHubRepoUrl = "https://github.com/vce27/Glance";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public AppUpdateService(string? localSourceOverride = null)
    {
        // Multi-thread Range download for large nupkg (PCL2-style); small JSON stays single-stream.
        IFileDownloader downloader = new MultiThreadFileDownloader(parts: 8);
        IUpdateSource source = !string.IsNullOrWhiteSpace(localSourceOverride)
            ? new SimpleFileSource(new DirectoryInfo(localSourceOverride))
            : new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false, downloader: downloader);
        _mgr = new UpdateManager(source);
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
            if (info is null)
            {
                return new UpdateCheckOutcome(
                    UpdateCheckKind.UpToDate,
                    CurrentVersionDisplay,
                    null,
                    "当前已是最新版本");
            }

            var remote = info.TargetFullRelease.Version.ToString();
            var notes = SanitizeReleaseNotes(info.TargetFullRelease.NotesMarkdown);
            var message = string.IsNullOrWhiteSpace(notes)
                ? $"发现新版本 {remote}"
                : $"发现新版本 {remote}\n\n更新说明：\n{notes}";
            return new UpdateCheckOutcome(
                UpdateCheckKind.UpdateAvailable,
                CurrentVersionDisplay,
                remote,
                message,
                notes);
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

            _mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
            return new UpdateApplyOutcome(true, "正在安装更新并重启…");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("update apply failed: " + ex);
            return new UpdateApplyOutcome(false, "更新失败: " + ex.Message);
        }
    }

    private static string? SanitizeReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
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
            .Take(12)
            .Select(l => "· " + l)
            .ToArray();
        return lines.Length == 0 ? null : string.Join("\n", lines);
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
