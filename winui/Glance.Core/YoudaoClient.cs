using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance.Core;

public sealed class YoudaoClient
{
    private const string ImageTranslateSecret = "VPaHE3kX_vl4BhgYiu2n";
    private readonly HttpClient _http;

    public YoudaoClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public async Task<TranslationResponse> TranslateImageAsync(
        byte[] bytes,
        string fileName,
        string mimeType,
        string fromLang,
        string toLang,
        SelectionPayload selection,
        TranslatorSettings settings,
        string? salt = null,
        CancellationToken ct = default)
    {
        salt ??= RandomSalt();
        var sign = BuildUploadSign(settings.Clientele, bytes, salt);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(fileContent, "multipartFile", fileName);
        form.Add(new StringContent(settings.Clientele), "clientele");
        form.Add(new StringContent(salt), "salt");
        form.Add(new StringContent(sign), "sign");
        form.Add(new StringContent(fromLang), "from");
        form.Add(new StringContent(toLang), "to");
        form.Add(new StringContent("true"), "isSaveHistory");
        form.Add(new StringContent("true"), "isSyncSaveHistory");
        form.Add(new StringContent("photo_translate"), "funDesc");

        using var resp = await _http.PostAsync("https://ocrtran.youdao.com/ocr/imgtranocr", form, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var payload = JsonSerializer.Deserialize<RawTranslationResponse>(json, ConfigStore.JsonOptions)
            ?? throw new InvalidOperationException("empty youdao response");

        return payload.ToResponse(selection);
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
    {
        var fraction = Random.Shared.NextDouble();
        return fraction.ToString("R");
    }

    private sealed class RawLine
    {
        public string Text { get; set; } = "";
    }

    private sealed class RawRegion
    {
        public string BoundingBox { get; set; } = "";
        public string Context { get; set; } = "";
        public string TranContent { get; set; } = "";
        public string Color { get; set; } = "";
        public List<RawLine> Lines { get; set; } = [];
    }

    private sealed class RawTranslationResponse
    {
        public string ErrorCode { get; set; } = "";
        public string Image { get; set; } = "";
        public string LanFrom { get; set; } = "";
        public string LanTo { get; set; } = "";
        public string RequestId { get; set; } = "";
        public List<RawRegion> ResRegions { get; set; } = [];

        public TranslationResponse ToResponse(SelectionPayload selection)
        {
            if (ErrorCode != "0")
                throw new InvalidOperationException($"youdao errorCode={ErrorCode}");

            var regions = new List<OverlayRegion>();
            var pairs = new List<TranslationPair>();
            foreach (var region in ResRegions)
            {
                var bounds = ParseBoundingBox(region.BoundingBox);
                if (!string.IsNullOrEmpty(region.Context) || !string.IsNullOrEmpty(region.TranContent))
                {
                    pairs.Add(new TranslationPair
                    {
                        Source = region.Context,
                        Target = region.TranContent,
                    });
                }

                regions.Add(new OverlayRegion
                {
                    Rect = new BoundingBox
                    {
                        X = selection.X + bounds.X,
                        Y = selection.Y + bounds.Y,
                        Width = bounds.Width,
                        Height = bounds.Height,
                    },
                    LocalRect = bounds,
                    Source = region.Context,
                    Translated = region.TranContent,
                    Color = string.IsNullOrEmpty(region.Color) ? "default" : region.Color,
                    Lines = region.Lines.Select(l => new RegionLine { Text = l.Text }).ToList(),
                });
            }

            var history = new TranslationHistoryItem
            {
                Id = RequestId,
                CreatedAt = DateTimeOffset.UtcNow,
                FromLang = LanFrom,
                ToLang = LanTo,
                Selection = selection,
                Pairs = pairs,
            };

            return new TranslationResponse
            {
                RequestId = RequestId,
                LanFrom = LanFrom,
                LanTo = LanTo,
                RenderedImageBase64 = Image,
                Regions = regions,
                Pairs = pairs,
                HistoryItem = history,
            };
        }
    }

    private static BoundingBox ParseBoundingBox(string raw)
    {
        var parts = raw.Split(',').Select(p => double.Parse(p.Trim())).ToArray();
        if (parts.Length != 4)
            throw new FormatException($"invalid bounding box: {raw}");
        return new BoundingBox { X = parts[0], Y = parts[1], Width = parts[2], Height = parts[3] };
    }
}
