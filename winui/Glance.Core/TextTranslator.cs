namespace Glance.Core;

public sealed class TextTranslator
{
    private readonly BingTranslateClient _bing = new();
    private readonly BuiltinTranslateClient _builtin = new();
    private readonly LlmTranslateClient _llm = new();

    public Task<TextTranslationResult> TranslateAsync(
        string text,
        string from,
        string to,
        TranslatorSettings settings,
        CancellationToken ct = default)
    {
        var proxy = ProxyResolver.Resolve(settings);
        return settings.TextTranslateEngine switch
        {
            TextTranslateEngine.Bing => _bing.TranslateAsync(text, from, to, ct),
            TextTranslateEngine.Google => _builtin.GoogleAsync(text, from, to, proxy, ct),
            TextTranslateEngine.Microsoft => _builtin.MicrosoftAsync(text, from, to, proxy, ct),
            TextTranslateEngine.Transmart => _builtin.TransmartAsync(text, from, to, proxy, ct),
            TextTranslateEngine.Yandex => _builtin.YandexAsync(text, from, to, proxy, ct),
            TextTranslateEngine.Iciba => _builtin.IcibaAsync(text, from, to, proxy, ct),
            TextTranslateEngine.Llm => _llm.TranslateAsync(text, from, to, settings.LlmConfig, ct),
            _ => _bing.TranslateAsync(text, from, to, ct),
        };
    }
}
