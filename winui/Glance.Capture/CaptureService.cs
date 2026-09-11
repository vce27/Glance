using System.Drawing;
using System.Windows.Forms;
using Glance.Core;

namespace Glance.Capture;

public sealed class CaptureSessionResult
{
    public bool Cancelled { get; init; }
    public TranslationResponse? Translation { get; init; }
    public string? CopiedText { get; init; }
}

/// <summary>
/// Orchestrates cursor-monitor capture → selection → Youdao OCR/translate.
/// Runs WinForms overlay on an STA thread.
/// </summary>
public sealed class CaptureService
{
    private readonly YoudaoClient _youdao;
    private readonly ConfigStore _store;
    private int _busy;

    public CaptureService(YoudaoClient youdao, ConfigStore store)
    {
        _youdao = youdao;
        _store = store;
    }

    public bool IsBusy => Interlocked.CompareExchange(ref _busy, 0, 0) != 0;

    public Task<CaptureSessionResult> RunAsync(CaptureMode mode, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return Task.FromResult(new CaptureSessionResult { Cancelled = true });

        var tcs = new TaskCompletionSource<CaptureSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                var settings = _store.LoadSettings();
                var monitor = MonitorCapture.FindCursorMonitor();
                var snapshot = MonitorCapture.CaptureMonitor(monitor);

                using var overlay = new CaptureOverlayForm(snapshot);
                using var pulse = new AutoResetEvent(false);
                void OnChanged(object? _, EventArgs __) => pulse.Set();
                overlay.Changed += OnChanged;
                overlay.FormClosed += OnChanged;

                overlay.Show();
                Application.DoEvents();

                TranslationResponse? lastTranslation = null;

                while (!overlay.IsDisposed)
                {
                    WaitOverlay(overlay, pulse, () => overlay.Result is not null || overlay.IsDisposed, ct);

                    if (overlay.IsDisposed)
                        break;

                    if (overlay.Result is null || overlay.Result.Kind == CaptureOverlayResultKind.Cancelled)
                    {
                        tcs.TrySetResult(lastTranslation is null
                            ? new CaptureSessionResult { Cancelled = true }
                            : new CaptureSessionResult { Translation = lastTranslation, Cancelled = false });
                        return;
                    }

                    var sel = overlay.Result.Selection;
                    var png = MonitorCapture.CropToPng(snapshot.Bitmap, sel);

                    overlay.ShowLoading(mode == CaptureMode.CopyText ? "识别中…" : "翻译中…");
                    Application.DoEvents();

                    TranslationResponse response;
                    try
                    {
                        response = _youdao.TranslateImageAsync(
                            png,
                            "capture.png",
                            "image/png",
                            settings.FromLang,
                            settings.ToLang,
                            settings,
                            ct: ct).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        overlay.ShowLoading("失败: " + ex.Message);
                        Application.DoEvents();
                        Thread.Sleep(1200);
                        overlay.AwaitNextSelection();
                        continue;
                    }

                    lastTranslation = response;

                    if (mode == CaptureMode.CopyText)
                    {
                        var text = string.Join("\n", response.Pairs
                            .Select(p => p.Source)
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            try { Clipboard.SetText(text); } catch { /* ignore */ }
                        }
                        overlay.Finish(CaptureOverlayResultKind.Cancelled);
                        tcs.TrySetResult(new CaptureSessionResult { CopiedText = text, Cancelled = false });
                        return;
                    }

                    overlay.ShowTranslatedResult(DecodeImagePayload(response.RenderedImageBase64), response.Pairs);

                    WaitOverlay(overlay, pulse,
                        () => overlay.IsDisposed || !overlay.IsShowingResult || overlay.Result is not null,
                        ct);

                    if (overlay.IsDisposed)
                        break;

                    if (overlay.Result?.Kind == CaptureOverlayResultKind.Cancelled)
                    {
                        tcs.TrySetResult(new CaptureSessionResult { Translation = lastTranslation, Cancelled = false });
                        return;
                    }

                    if (overlay.Result?.Kind == CaptureOverlayResultKind.Selection)
                        continue;

                    break;
                }

                tcs.TrySetResult(lastTranslation is null
                    ? new CaptureSessionResult { Cancelled = true }
                    : new CaptureSessionResult { Translation = lastTranslation, Cancelled = false });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                try { Application.ExitThread(); } catch { /* ignore */ }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "glance-capture";
        thread.Start();
        return tcs.Task;
    }

    private static void WaitOverlay(
        CaptureOverlayForm overlay,
        AutoResetEvent pulse,
        Func<bool> done,
        CancellationToken ct)
    {
        while (!done())
        {
            Application.DoEvents();
            if (ct.IsCancellationRequested)
            {
                overlay.Finish(CaptureOverlayResultKind.Cancelled);
                return;
            }
            pulse.WaitOne(16);
        }
    }

    private static byte[]? DecodeImagePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var data = payload;
        var comma = data.IndexOf(',');
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            data = data[(comma + 1)..];
        try { return Convert.FromBase64String(data); }
        catch { return null; }
    }
}
