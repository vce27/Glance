using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance.Core;

public sealed class DictSense
{
    public string Pos { get; init; } = "";
    public string Meaning { get; init; } = "";
}

public sealed class DictLookup
{
    public string Headword { get; init; } = "";
    public string Phonetic { get; init; } = "";
    public IReadOnlyList<DictSense> Senses { get; init; } = [];
    public IReadOnlyList<string> Alternatives { get; init; } = [];
}

/// <summary>Best-effort dict lookup via Youdao JSON (phonetic + senses).</summary>
public sealed class YoudaoDictClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 Glance" } },
    };

    private static readonly Regex WordLike = new(@"^[\p{L}\p{M}'’.\-]+$", RegexOptions.Compiled);

    public async Task<DictLookup?> LookupAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length is 0 or > 40) return null;
        if (text.Contains('\n') || text.Contains(' ') && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
            return null;
        if (!WordLike.IsMatch(text.Replace(" ", ""))) return null;

        var url = "https://dict.youdao.com/jsonapi_s?doctype=json&jsonversion=4&q="
                  + Uri.EscapeDataString(text);
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string? us = null, uk = null, phone = null, head = text;
        var senses = new List<DictSense>();
        var alts = new List<string>();

        if (TryWord(root, "ec", out var ec))
        {
            us = Str(ec, "usphone");
            uk = Str(ec, "ukphone");
            head = Str(ec, "return-phrase") ?? text;
            CollectEcSenses(ec, senses);
        }

        if (TryWord(root, "ce", out var ce))
        {
            phone ??= Str(ce, "phone");
            head = Str(ce, "return-phrase") ?? head;
            CollectCeSenses(ce, senses, alts);
        }

        if (root.TryGetProperty("simple", out var simple) &&
            simple.TryGetProperty("word", out var words) &&
            words.ValueKind == JsonValueKind.Array &&
            words.GetArrayLength() > 0)
        {
            var w = words[0];
            us ??= Str(w, "usphone");
            uk ??= Str(w, "ukphone");
            phone ??= Str(w, "phone");
            head = Str(w, "return-phrase") ?? head;
        }

        CollectWebAlts(root, text, alts);

        var phonetic = FormatPhonetic(us, uk, phone);
        if (string.IsNullOrEmpty(phonetic) && senses.Count == 0 && alts.Count == 0)
            return null;

        return new DictLookup
        {
            Headword = head,
            Phonetic = phonetic,
            Senses = senses
                .Select(s => new DictSense { Pos = s.Pos, Meaning = ShortenMeaning(s.Meaning, 2) })
                .Take(3)
                .ToList(),
            Alternatives = DedupPreserve(alts, text).Take(5).ToList(),
        };
    }

    private static string ShortenMeaning(string meaning, int maxParts)
    {
        if (string.IsNullOrWhiteSpace(meaning) || maxParts <= 0) return meaning;
        var parts = meaning.Split(['；', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= maxParts) return meaning.Trim();
        return string.Join("；", parts.Take(maxParts));
    }

    private static string FormatPhonetic(string? us, string? uk, string? phone)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(us) && !string.IsNullOrWhiteSpace(uk) &&
            string.Equals(us, uk, StringComparison.Ordinal))
        {
            parts.Add($"/{us.Trim()}/");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(uk)) parts.Add($"英 /{uk.Trim()}/");
            if (!string.IsNullOrWhiteSpace(us)) parts.Add($"美 /{us.Trim()}/");
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(phone))
            parts.Add(phone.Trim());
        return string.Join("    ", parts);
    }

    private static void CollectEcSenses(JsonElement word, List<DictSense> senses)
    {
        if (!word.TryGetProperty("trs", out var trs) || trs.ValueKind != JsonValueKind.Array) return;
        foreach (var t in trs.EnumerateArray())
        {
            var pos = (Str(t, "pos") ?? "").Trim();
            var tran = Str(t, "tran");
            if (string.IsNullOrWhiteSpace(tran)) continue;
            if (tran.Contains("（姓氏）", StringComparison.Ordinal) || tran.StartsWith("【名】", StringComparison.Ordinal))
                continue;
            senses.Add(new DictSense { Pos = pos, Meaning = CleanHtml(tran.Trim()) });
        }
    }

    private static void CollectCeSenses(JsonElement word, List<DictSense> senses, List<string> alts)
    {
        if (!word.TryGetProperty("trs", out var trs) || trs.ValueKind != JsonValueKind.Array) return;
        foreach (var t in trs.EnumerateArray())
        {
            var head = Str(t, "#text") ?? Str(t, "text");
            var tran = Str(t, "#tran") ?? Str(t, "tran");
            if (!string.IsNullOrWhiteSpace(head))
                alts.Add(head.Trim());
            if (!string.IsNullOrWhiteSpace(tran))
                senses.Add(new DictSense { Pos = "", Meaning = CleanHtml(tran.Trim()) });
        }
    }

    private static void CollectWebAlts(JsonElement root, string query, List<string> alts)
    {
        if (!root.TryGetProperty("web_trans", out var web)) return;
        if (!web.TryGetProperty("web-translation", out var list) || list.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in list.EnumerateArray())
        {
            var key = Str(item, "key");
            if (!string.IsNullOrEmpty(key) &&
                !string.Equals(key, query, StringComparison.OrdinalIgnoreCase) &&
                alts.Count > 0)
                continue;

            if (!item.TryGetProperty("trans", out var trans) || trans.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var t in trans.EnumerateArray())
            {
                var v = Str(t, "value");
                if (!string.IsNullOrWhiteSpace(v))
                    alts.Add(v.Trim());
            }

            if (alts.Count > 0) break;
        }
    }

    private static IEnumerable<string> DedupPreserve(IEnumerable<string> items, string exclude)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exclude.Trim() };
        foreach (var item in items)
        {
            var t = item.Trim();
            if (t.Length == 0 || t.Length > 40) continue;
            if (!seen.Add(t)) continue;
            yield return t;
        }
    }

    private static string CleanHtml(string s)
        => Regex.Replace(s, "<[^>]+>", "").Replace("&nbsp;", " ").Trim();

    private static bool TryWord(JsonElement root, string key, out JsonElement word)
    {
        word = default;
        return root.TryGetProperty(key, out var section) &&
               section.TryGetProperty("word", out word) &&
               word.ValueKind == JsonValueKind.Object;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
