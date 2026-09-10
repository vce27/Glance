using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Glance.Core;

public sealed class BuiltinTranslateClient
{
    private static readonly byte[] MsPrivateKey =
    [
        0xa2, 0x29, 0x3a, 0x3d, 0xd0, 0xdd, 0x32, 0x73, 0x97, 0x7a, 0x64, 0xdb, 0xc2, 0xf3, 0x27, 0xf5,
        0xd7, 0xbf, 0x87, 0xd9, 0x45, 0x9d, 0xf0, 0x5a, 0x09, 0x66, 0xc6, 0x30, 0xc6, 0x6a, 0xaa, 0x84,
        0x9a, 0x41, 0xaa, 0x94, 0x3a, 0xa8, 0xd5, 0x1a, 0x6e, 0x4d, 0xaa, 0xc9, 0xa3, 0x70, 0x12, 0x35,
        0xc7, 0xeb, 0x12, 0xf6, 0xe8, 0x23, 0x07, 0x9e, 0x47, 0x10, 0x95, 0x91, 0x88, 0x55, 0xd8, 0x17,
    ];

    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    private HttpClient? _cached;
    private string _cachedProxyKey = "\0";

    private HttpClient ClientFor(string? proxy)
    {
        var key = proxy ?? "";
        if (_cached is not null && _cachedProxyKey == key)
            return _cached;

        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(proxy))
            handler.Proxy = new WebProxy(proxy);
        else
            handler.UseProxy = false;

        _cached?.Dispose();
        _cached = new HttpClient(handler);
        _cachedProxyKey = key;
        return _cached;
    }

    public async Task<TextTranslationResult> GoogleAsync(string text, string from, string to, string? proxy, CancellationToken ct = default)
    {
        var sl = MapGoogle(from);
        var tl = MapGoogle(to);
        var url =
            $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(sl)}&tl={Uri.EscapeDataString(tl)}&dt=t&q={Uri.EscapeDataString(text)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
        using var resp = await ClientFor(proxy).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
            root[0].ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in root[0].EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                    seg[0].ValueKind == JsonValueKind.String)
                    sb.Append(seg[0].GetString());
            }
        }
        var detected = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 &&
                       root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString() ?? from
            : from;
        return Finalize(sb.ToString(), detected, "Google");
    }

    public async Task<TextTranslationResult> MicrosoftAsync(string text, string from, string to, string? proxy, CancellationToken ct = default)
    {
        var fromMs = MapMicrosoft(from);
        var toMs = MapMicrosoft(to);
        var requestPath = $"api.cognitive.microsofttranslator.com/translate?api-version=3.0&to={toMs}";
        if (!string.IsNullOrEmpty(fromMs) && fromMs != "auto")
            requestPath += $"&from={fromMs}";
        var signature = MsSignature(requestPath);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://" + requestPath);
        req.Headers.TryAddWithoutValidation("X-MT-Signature", signature);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }),
            Encoding.UTF8,
            "application/json");

        using var resp = await ClientFor(proxy).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var translated = root[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
        var detected = root[0].TryGetProperty("detectedLanguage", out var dl) &&
                       dl.TryGetProperty("language", out var lang)
            ? lang.GetString() ?? from
            : from;
        return Finalize(translated, detected, "Microsoft");
    }

    public async Task<TextTranslationResult> TransmartAsync(string text, string from, string to, string? proxy, CancellationToken ct = default)
    {
        var payload = new
        {
            header = new
            {
                fn = "auto_translation_block",
                client_key = "browser-chrome-110.0.0-Mac OS-df4bd4c5-a65d-44b2-a40f-42f34f3535f2-1677486696487",
            },
            type = "plain",
            model_category = "normal",
            source = new { lang = MapTransmart(from), text_block = text },
            target = new { lang = MapTransmart(to) },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36");
        req.Headers.Referrer = new Uri("https://yi.qq.com/zh-CN/index");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await ClientFor(proxy).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var translated = "";
        if (doc.RootElement.TryGetProperty("auto_translation", out var at))
        {
            translated = at.ValueKind switch
            {
                JsonValueKind.String => at.GetString() ?? "",
                JsonValueKind.Array => string.Join("\n",
                    at.EnumerateArray().Select(v => v.GetString()).Where(s => !string.IsNullOrEmpty(s))),
                _ => "",
            };
        }
        return Finalize(translated, from, "Transmart");
    }

    public async Task<TextTranslationResult> YandexAsync(string text, string from, string to, string? proxy, CancellationToken ct = default)
    {
        var src = MapYandex(from);
        var tgt = MapYandex(to);
        var lang = src == "auto" ? tgt : $"{src}-{tgt}";
        var ucid = Guid.NewGuid().ToString("N");
        var url = $"https://translate.yandex.net/api/v1/tr.json/translate?ucid={ucid}&srv=android&format=text";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "ru.yandex.translate/3.20.2024");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text,
            ["lang"] = lang,
        });
        using var resp = await ClientFor(proxy).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var translated = "";
        if (doc.RootElement.TryGetProperty("text", out var arr) && arr.ValueKind == JsonValueKind.Array &&
            arr.GetArrayLength() > 0)
            translated = arr[0].GetString() ?? "";
        return Finalize(translated, from, "Yandex");
    }

    public async Task<TextTranslationResult> IcibaAsync(string text, string from, string to, string? proxy, CancellationToken ct = default)
    {
        const string path = "/dictionary/fy/batch";
        const string client = "6";
        const string key = "1000006";
        const string salt = "7ece94d9f9c202b0d2ec557dg4r9bc";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var signSrc = $"{path}{client}{key}{timestamp}{salt}";
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signSrc))).ToLowerInvariant();

        var url =
            $"https://dictionary.iciba.com/dictionary/fy/batch?client={client}&key={key}&timestamp={timestamp}&signature={signature}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Origin", "https://www.iciba.com");
        req.Headers.Referrer = new Uri("https://www.iciba.com/");
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { from = MapIciba(from), to = MapIciba(to), textList = new[] { text } }),
            Encoding.UTF8,
            "application/json");
        using var resp = await ClientFor(proxy).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var ok = root.TryGetProperty("code", out var code) &&
                 ((code.ValueKind == JsonValueKind.Number && code.GetInt32() == 1) ||
                  (code.ValueKind == JsonValueKind.String && code.GetString() == "1"));
        if (!ok) throw new InvalidOperationException($"iCiba error: {root}");

        var lines = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()))
                    lines.Add(item.GetString()!);
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("out", out var o) &&
                         !string.IsNullOrEmpty(o.GetString()))
                    lines.Add(o.GetString()!);
            }
        }
        return Finalize(string.Join("\n", lines), from, "iCiba");
    }

    private static TextTranslationResult Finalize(string translated, string detected, string engine)
    {
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException($"{engine} returned empty result");
        return new TextTranslationResult
        {
            TranslatedText = translated,
            FromLangDetected = detected,
        };
    }

    private static string MsSignature(string requestPath)
    {
        var guid = Guid.NewGuid().ToString("N");
        var escapedUrl = Uri.EscapeDataString(requestPath);
        var dateTime = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss") + "GMT";
        var raw = $"MSTranslatorAndroidApp{escapedUrl}{dateTime}{guid}".ToLowerInvariant();
        using var hmac = new HMACSHA256(MsPrivateKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));
        var sigB64 = Convert.ToBase64String(hash);
        return $"MSTranslatorAndroidApp::{sigB64}::{dateTime}::{guid}";
    }

    private static string MapGoogle(string c) => c switch { "zh-CHS" => "zh-CN", "zh-CHT" => "zh-TW", _ => c };
    private static string MapMicrosoft(string c) => c switch { "zh-CHS" => "zh-Hans", "zh-CHT" => "zh-Hant", _ => c };
    private static string MapTransmart(string c) => c switch { "zh-CHS" => "zh", "zh-CHT" => "zh-TW", _ => c };
    private static string MapYandex(string c) => c switch { "zh-CHS" or "zh-CHT" => "zh", _ => c };
    private static string MapIciba(string c) => c switch { "zh-CHS" => "zh", "zh-CHT" => "cht", _ => c };
}

public static class ProxyResolver
{
    public static string? Resolve(TranslatorSettings settings) => settings.ProxyMode switch
    {
        ProxyMode.None => null,
        ProxyMode.Custom => Normalize(settings.CustomProxy),
        _ => SystemProxyUrl(),
    };

    public static string? Normalize(string? raw)
    {
        var t = raw?.Trim() ?? "";
        if (string.IsNullOrEmpty(t)) return null;
        return t.Contains("://", StringComparison.Ordinal) ? t : "http://" + t;
    }

    public static string? SystemProxyUrl()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;
            if (Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 0) return null;
            var server = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server)) return null;
            if (!server.Contains('=')) return "http://" + server;
            string? http = null;
            foreach (var part in server.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || string.IsNullOrWhiteSpace(kv[1])) continue;
                if (kv[0].Equals("https", StringComparison.OrdinalIgnoreCase))
                    return "http://" + kv[1].Trim();
                if (kv[0].Equals("http", StringComparison.OrdinalIgnoreCase))
                    http = "http://" + kv[1].Trim();
            }
            return http;
        }
        catch
        {
            return null;
        }
    }
}
