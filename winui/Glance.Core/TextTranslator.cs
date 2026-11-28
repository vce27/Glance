namespace Glance.Core;

public sealed class TextTranslator
{
    private readonly BingTranslateClient _bing = new();
    private readonly LlmTranslateClient _llm = new();
    private OfflineTranslateClient _offline = new();

    public OfflineTranslateClient Offline => _offline;

    public void ResetOffline()
    {
        try { _offline.Dispose(); } catch { /* ignore */ }
        _offline = new OfflineTranslateClient();
    }

    public async Task<TextTranslationResult> TranslateAsync(
        string text,
        string from,
        string to,
        TranslatorSettings settings,
        CancellationToken ct = default)
    {
        if (settings.TextTranslateEngine == TextTranslateEngine.Offline)
        {
            var offlineOnly = await _offline.TranslateAsync(text, from, to, settings, ct);
            offlineOnly.SourceEngine = "离线";
            return offlineOnly;
        }

        Exception? onlineError = null;
        try
        {
            var result = settings.TextTranslateEngine == TextTranslateEngine.Llm
                ? await _llm.TranslateAsync(text, from, to, settings, ct)
                : await _bing.TranslateAsync(text, from, to, settings, ct);
            result.SourceEngine = settings.TextTranslateEngine == TextTranslateEngine.Llm ? "AI 大模型" : "必应";
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onlineError = ex;
        }

        // Auto offline when pack is installed — no need to switch engine in settings.
        if (_offline.Pack.IsInstalled())
        {
            try
            {
                var fallback = await _offline.TranslateAsync(text, from, to, settings, ct);
                fallback.SourceEngine = "离线";
                return fallback;
            }
            catch (Exception offlineEx) when (offlineEx is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"在线翻译失败，离线也失败。在线: {onlineError?.Message}; 离线: {offlineEx.Message}",
                    offlineEx);
            }
        }

        throw onlineError ?? new InvalidOperationException("翻译失败");
    }
}
