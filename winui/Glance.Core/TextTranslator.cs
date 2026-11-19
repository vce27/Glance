namespace Glance.Core;

public sealed class TextTranslator
{
    private readonly BingTranslateClient _bing = new();
    private readonly LlmTranslateClient _llm = new();

    public Task<TextTranslationResult> TranslateAsync(
        string text,
        string from,
        string to,
        TranslatorSettings settings,
        CancellationToken ct = default)
        => settings.TextTranslateEngine == TextTranslateEngine.Llm
            ? _llm.TranslateAsync(text, from, to, settings, ct)
            : _bing.TranslateAsync(text, from, to, settings, ct);
}
