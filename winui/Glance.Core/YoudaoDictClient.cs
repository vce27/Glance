using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance.Core;

public sealed class DictSense
{
    public string Pos { get; init; } = "";
    public string PosLabel { get; init; } = "";
    public string Meaning { get; init; } = "";
}

public sealed class DictPhrase
{
    public string Phrase { get; init; } = "";
    public string Translation { get; init; } = "";
}

public sealed class DictLookup
{
    public string Headword { get; init; } = "";
    public string Phonetic { get; init; } = "";
    public string? SpeechWord { get; init; }
    public string PosSummary { get; init; } = "";
    public IReadOnlyList<DictSense> Senses { get; init; } = [];
    public IReadOnlyList<string> Alternatives { get; init; } = [];
    public IReadOnlyList<DictPhrase> Phrases { get; init; } = [];
}

/// <summary>Best-effort dict lookup via Youdao JSON (phonetic + senses + phrases).</summary>
public sealed class YoudaoDictClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 Glance" } },
    };

    private static readonly Regex WordLike = new(@"^[\p{L}\p{M}'’.\-]+$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> PosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["n."] = "名词",
        ["v."] = "动词",
        ["vt."] = "及物动词",
        ["vi."] = "不及物动词",
        ["adj."] = "形容词",
        ["adv."] = "副词",
        ["prep."] = "介词",
        ["conj."] = "连词",
        ["pron."] = "代词",
        ["int."] = "感叹词",
        ["num."] = "数词",
        ["art."] = "冠词",
        ["aux."] = "助动词",
        ["modal v."] = "情态动词",
    };

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

        string? us = null, uk = null, phone = null, speech = null, head = text;
        var senses = new List<DictSense>();
        var alts = new List<string>();
        var phrases = new List<DictPhrase>();

        if (TryWord(root, "ec", out var ec))
        {
            us = Str(ec, "usphone");
            uk = Str(ec, "ukphone");
            speech = Str(ec, "return-phrase") ?? text;
            head = speech ?? text;
            CollectEcSenses(ec, senses);
        }

        if (TryWord(root, "ce", out var ce))
        {
            phone ??= Str(ce, "phone");
            speech ??= Str(ce, "return-phrase") ?? text;
            head = speech ?? head;
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
            speech ??= Str(w, "return-phrase") ?? text;
        }

        CollectWebAlts(root, text, alts);
        CollectPhrases(root, phrases);

        var phonetic = FormatPhonetic(us, uk, phone);
        if (string.IsNullOrEmpty(phonetic) && senses.Count == 0 && alts.Count == 0 && phrases.Count == 0)
            return null;

        // Bing-style glance: few POS rows, each meaning clipped to the first couple of senses.
        var shortSenses = senses
            .Select(s => new DictSense
            {
                Pos = s.Pos,
                PosLabel = s.PosLabel,
                Meaning = ShortenMeaning(s.Meaning, 2),
            })
            .Take(3)
            .ToList();

        var posSummary = string.Join(" / ", shortSenses
            .Select(s => s.PosLabel)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .Take(4));

        return new DictLookup
        {
            Headword = head,
            Phonetic = phonetic,
            SpeechWord = speech ?? text,
            PosSummary = posSummary,
            Senses = shortSenses,
            Alternatives = DedupPreserve(alts, text).Take(5).ToList(),
            Phrases = phrases.Take(4).ToList(),
        };
    }

    /// <summary>Keep only the first N semicolon-separated glosses (Bing-style brevity).</summary>
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
            // Skip name-only junk lines.
            if (tran.Contains("（姓氏）", StringComparison.Ordinal) || tran.StartsWith("【名】", StringComparison.Ordinal))
                continue;
            senses.Add(new DictSense
            {
                Pos = pos,
                PosLabel = FormatPosLabel(pos),
                Meaning = CleanHtml(tran.Trim()),
            });
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
                senses.Add(new DictSense { Pos = "", PosLabel = "", Meaning = CleanHtml(tran.Trim()) });
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

    private static void CollectPhrases(JsonElement root, List<DictPhrase> phrases)
    {
        if (!root.TryGetProperty("phrs", out var phrs)) return;
        if (!phrs.TryGetProperty("phrs", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var item in list.EnumerateArray())
        {
            var phrase = Str(item, "headword");
            var tran = Str(item, "translation");
            if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(tran)) continue;
            phrases.Add(new DictPhrase
            {
                Phrase = phrase.Trim(),
                Translation = CleanHtml(tran.Trim()),
            });
        }
    }

    private static string FormatPosLabel(string pos)
    {
        if (string.IsNullOrWhiteSpace(pos)) return "";
        var key = pos.Trim().ToLowerInvariant();
        if (!key.EndsWith('.') && PosNames.ContainsKey(key + "."))
            key += ".";
        var code = pos.Trim().TrimEnd('.').ToUpperInvariant();
        if (PosNames.TryGetValue(key, out var name) || PosNames.TryGetValue(key.TrimEnd('.'), out name))
            return $"{code}. {name}";
        return pos.Trim();
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
