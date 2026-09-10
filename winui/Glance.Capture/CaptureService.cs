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
                // Show modeless so we can update loading/result without closing.
                overlay.Show();
                Application.DoEvents();

                // Wait until selection or cancel.
                while (!overlay.IsDisposed && overlay.Result is null)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                    if (ct.IsCancellationRequested)
                    {
                        overlay.Finish(CaptureOverlayResultKind.Cancelled);
                        break;
                    }
                }

                if (overlay.Result is null || overlay.Result.Kind == CaptureOverlayResultKind.Cancelled)
                {
                    tcs.TrySetResult(new CaptureSessionResult { Cancelled = true });
                    return;
                }

                var sel = overlay.Result.Selection;
                var png = MonitorCapture.CropToPng(snapshot.Bitmap, sel);
                var selection = new SelectionPayload
                {
                    X = sel.X,
                    Y = sel.Y,
                    Width = sel.Width,
                    Height = sel.Height,
                    MonitorId = $"capture:{sel.X}:{sel.Y}:{sel.Width}:{sel.Height}",
                    MonitorX = monitor.X,
                    MonitorY = monitor.Y,
                    MonitorWidth = (uint)monitor.Width,
                    MonitorHeight = (uint)monitor.Height,
                    MonitorScaleFactor = monitor.ScaleFactor,
                };

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
                        selection,
                        settings,
                        ct: ct).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    overlay.ShowLoading("失败: " + ex.Message);
                    Application.DoEvents();
                    Thread.Sleep(1200);
                    overlay.Finish(CaptureOverlayResultKind.Cancelled);
                    tcs.TrySetResult(new CaptureSessionResult { Cancelled = true });
                    return;
                }

                try { _store.AppendHistory(response.HistoryItem); } catch { /* ignore */ }

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

                // Show rendered image in-place.
                var imageBytes = DecodeImagePayload(response.RenderedImageBase64);
                if (imageBytes is not null)
                {
                    overlay.ShowTranslatedResult(imageBytes);
                    // Wait until user dismisses (Esc / click).
                    while (!overlay.IsDisposed)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                        if (overlay.Result?.Kind == CaptureOverlayResultKind.Cancelled ||
                            overlay.DialogResult != DialogResult.None)
                            break;
                    }
                }
                else
                {
                    overlay.Finish(CaptureOverlayResultKind.Cancelled);
                }

                tcs.TrySetResult(new CaptureSessionResult { Translation = response, Cancelled = false });
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
