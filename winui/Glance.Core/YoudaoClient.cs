using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glance.Core;

public sealed class YoudaoClient
{
    private const string ImageTranslateSecret = "VPaHE3kX_vl4BhgYiu2n";
    private const string DefaultClientele = "deskdict";
    private HttpClient? _http;
    private string _proxyKey = "\0";

    public YoudaoClient()
    {
    }

    public async Task<TranslationResponse> TranslateImageAsync(
        byte[] bytes,
        string fileName,
        string mimeType,
        string fromLang,
        string toLang,
        TranslatorSettings settings,
        CancellationToken ct = default)
    {
        var clientele = string.IsNullOrWhiteSpace(settings.Clientele) ? DefaultClientele : settings.Clientele;
        var salt = RandomSalt();
        var sign = BuildUploadSign(clientele, bytes, salt);
        var http = ClientFor(settings);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(fileContent, "multipartFile", fileName);
        form.Add(new StringContent(clientele), "clientele");
        form.Add(new StringContent(salt), "salt");
        form.Add(new StringContent(sign), "sign");
        form.Add(new StringContent(fromLang), "from");
        form.Add(new StringContent(toLang), "to");
        form.Add(new StringContent("true"), "isSaveHistory");
        form.Add(new StringContent("true"), "isSyncSaveHistory");
        form.Add(new StringContent("photo_translate"), "funDesc");

        using var resp = await http.PostAsync("https://ocrtran.youdao.com/ocr/imgtranocr", form, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var payload = JsonSerializer.Deserialize<RawTranslationResponse>(json, ConfigStore.JsonOptions)
            ?? throw new InvalidOperationException("empty youdao response");

        return payload.ToResponse();
    }

    private HttpClient ClientFor(TranslatorSettings settings)
    {
        var key = ProxyResolver.Resolve(settings) ?? "";
        if (_http is not null && _proxyKey == key)
            return _http;

        _http?.Dispose();
        _http = new HttpClient(ProxyResolver.CreateHandler(settings));
        _proxyKey = key;
        return _http;
    }

    internal static string BuildUploadSign(string clientele, byte[] bytes, string salt)
    {
        var imageB64 = Convert.ToBase64String(bytes);
        var digestSource = imageB64[..10] + imageB64.Length + imageB64[^10..];
        return Md5Hex(clientele + digestSource + salt + ImageTranslateSecret);
    }

    private static string Md5Hex(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string RandomSalt()
        => Random.Shared.NextDouble().ToString("R");

    private sealed class RawRegion
    {
        public string Context { get; set; } = "";
        public string TranContent { get; set; } = "";
    }

    private sealed class RawTranslationResponse
    {
        public string ErrorCode { get; set; } = "";
        public string Image { get; set; } = "";
        public string LanFrom { get; set; } = "";
        public string LanTo { get; set; } = "";
        public string RequestId { get; set; } = "";
        public List<RawRegion> ResRegions { get; set; } = [];

        public TranslationResponse ToResponse()
        {
            if (ErrorCode != "0")
                throw new InvalidOperationException($"youdao errorCode={ErrorCode}");

            var pairs = ResRegions
                .Where(r => !string.IsNullOrEmpty(r.Context) || !string.IsNullOrEmpty(r.TranContent))
                .Select(r => new TranslationPair { Source = r.Context, Target = r.TranContent })
                .ToList();

            return new TranslationResponse
            {
                RequestId = RequestId,
                LanFrom = LanFrom,
                LanTo = LanTo,
                RenderedImageBase64 = Image,
                Pairs = pairs,
            };
        }
    }
}
