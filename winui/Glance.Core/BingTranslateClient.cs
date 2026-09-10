using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance.Core;

public sealed class BingTranslateClient
{
    private const string CnHost = "https://cn.bing.com";
    private const string WwwHost = "https://www.bing.com";

    private readonly HttpClient _http;
    private BingToken? _token;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BingTranslateClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public async Task<TextTranslationResult> TranslateAsync(string text, string from, string to, CancellationToken ct = default)
    {
        var token = await GetOrRefreshTokenAsync(ct);
        try
        {
            return await TranslateWithTokenAsync(text, from, to, token, ct);
        }
        catch
        {
            await _lock.WaitAsync(ct);
            try { _token = null; } finally { _lock.Release(); }
            token = await GetOrRefreshTokenAsync(ct, WwwHost);
            return await TranslateWithTokenAsync(text, from, to, token, ct);
        }
    }

    private async Task<TextTranslationResult> TranslateWithTokenAsync(
        string text, string from, string to, BingToken token, CancellationToken ct)
    {
        var fromBing = MapLang(from);
        var toBing = MapLang(to);
        var host = token.Host;

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{host}/ttranslatev3?isVertical=1&IG={Uri.EscapeDataString(token.Ig)}&IID={Uri.EscapeDataString(token.Iid)}");
        req.Headers.Referrer = new Uri($"{host}/translator");
        req.Headers.TryAddWithoutValidation("Origin", host);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = fromBing,
            ["to"] = toBing,
            ["text"] = text,
            ["token"] = token.Value,
            ["key"] = token.Key,
        });

        using var resp = await _http.SendAsync(req, ct);
        if ((int)resp.StatusCode is 301 or 302 or 429)
            throw new InvalidOperationException($"Bing translate status {(int)resp.StatusCode}");

        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        using var body = JsonDocument.Parse(bodyText);
        var root = body.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            (root.TryGetProperty("ShowCaptcha", out _) ||
             (root.TryGetProperty("statusCode", out var sc) && sc.GetInt32() >= 400)))
            throw new InvalidOperationException("Bing translate auth failed");

        var translated = "";
        var detected = from;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0];
            if (first.TryGetProperty("translations", out var translations) &&
                translations.GetArrayLength() > 0 &&
                translations[0].TryGetProperty("text", out var t))
                translated = t.GetString() ?? "";
            if (first.TryGetProperty("detectedLanguage", out var dl) &&
                dl.TryGetProperty("language", out var lang))
                detected = lang.GetString() ?? from;
        }

        if (string.IsNullOrEmpty(translated))
            throw new InvalidOperationException("Bing translate returned empty result");

        return new TextTranslationResult
        {
            TranslatedText = translated,
            FromLangDetected = NormalizeLang(detected),
        };
    }

    private async Task<BingToken> GetOrRefreshTokenAsync(CancellationToken ct, string? preferHost = null)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && preferHost is null)
                return _token;

            if (preferHost is not null)
            {
                _token = await FetchTokenAsync(preferHost, ct);
                return _token;
            }

            try
            {
                _token = await FetchTokenAsync(CnHost, ct);
            }
            catch
            {
                _token = await FetchTokenAsync(WwwHost, ct);
            }
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<BingToken> FetchTokenAsync(string host, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{host}/translator", ct);
        if ((int)resp.StatusCode is 301 or 302)
            throw new InvalidOperationException($"Bing {host} redirected");
        var html = await resp.Content.ReadAsStringAsync(ct);
        var (key, value) = ExtractAbusePreventionToken(html);
        var ig = ExtractIg(html);
        var iid = ExtractIid(html) ?? "translator.5023";
        return new BingToken(value, key, ig, iid, host);
    }

    private static (string Key, string Value) ExtractAbusePreventionToken(string html)
    {
        const string prefix = "params_AbusePreventionHelper = [";
        var start = html.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Bing: params_AbusePreventionHelper not found");
        var rest = html[(start + prefix.Length)..];
        var end = rest.IndexOf(']');
        if (end < 0) throw new InvalidOperationException("Bing: token array end not found");
        var parts = rest[..end].Split(',', 3);
        if (parts.Length < 2) throw new InvalidOperationException("Bing: token array too short");
        var key = parts[0].Trim();
        var value = parts[1].Trim().Trim('"');
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            throw new InvalidOperationException("Bing: empty token");
        return (key, value);
    }

    private static string ExtractIg(string html)
    {
        const string prefix = "IG:\"";
        var start = html.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Bing IG not found");
        var rest = html[(start + prefix.Length)..];
        var end = rest.IndexOf('"');
        return rest[..end];
    }

    private static string? ExtractIid(string html)
    {
        var m = Regex.Match(html, "data-iid=\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string MapLang(string code) => code switch
    {
        "zh-CHS" => "zh-Hans",
        "zh-CHT" => "zh-Hant",
        "auto" => "auto-detect",
        _ => code,
    };

    private static string NormalizeLang(string code) => code switch
    {
        "zh-Hans" => "zh-CHS",
        "zh-Hant" => "zh-CHT",
        "auto-detect" => "auto",
        _ => code.ToLowerInvariant(),
    };

    private sealed record BingToken(string Value, string Key, string Ig, string Iid, string Host);
}
