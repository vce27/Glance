using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Glance.Core;

public sealed class LlmTranslateClient
{
    private const string Prompt =
        "You are a professional translator. Translate the following text from {from} to {to}. Only output the translation, nothing else. Do not add explanations or notes.";
    private const string AutoPrompt =
        "You are a professional translator. Detect the source language and translate the following text to {to}. Only output the translation, nothing else. Do not add explanations or notes.";

    private readonly ProxiedHttp _http = new();

    public async Task<TextTranslationResult> TranslateAsync(
        string text,
        string from,
        string to,
        TranslatorSettings settings,
        CancellationToken ct = default)
    {
        var config = settings.LlmConfig;
        var template = from == "auto" ? AutoPrompt : Prompt;
        var system = template
            .Replace("{from}", LangLabel(from))
            .Replace("{to}", LangLabel(to));

        var body = new
        {
            model = config.Model,
            temperature = 0.3,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = text },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl.Trim());
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.Get(settings).SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM API error (HTTP {(int)resp.StatusCode}): {respText[..Math.Min(500, respText.Length)]}");

        using var doc = JsonDocument.Parse(respText);
        var translated = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("LLM returned empty result");
        return new TextTranslationResult { TranslatedText = translated };
    }

    private static string LangLabel(string code) => code switch
    {
        "auto" => "auto-detected language",
        "zh-CHS" => "Simplified Chinese",
        "zh-CHT" => "Traditional Chinese",
        "en" => "English",
        "ja" => "Japanese",
        "ko" => "Korean",
        "fr" => "French",
        "de" => "German",
        "ru" => "Russian",
        "es" => "Spanish",
        _ => code,
    };
}
