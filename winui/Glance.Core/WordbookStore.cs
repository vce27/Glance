using System.Text.Json;

namespace Glance.Core;

public sealed class WordbookEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Word { get; set; } = "";
    public string Translation { get; set; } = "";
    /// <summary>IPA / pinyin display, e.g. "美 /həˈloʊ/  英 /həˈləʊ/" or "nǐ hǎo".</summary>
    public string Phonetic { get; set; } = "";
    public long AddedAtUnix { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>Local JSON wordbook under the same AppData folder as settings.</summary>
public sealed class WordbookStore
{
    private readonly string _file;

    public WordbookStore(string baseDir)
    {
        Directory.CreateDirectory(baseDir);
        _file = Path.Combine(baseDir, "wordbook.json");
    }

    public List<WordbookEntry> Load()
    {
        if (!File.Exists(_file)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<WordbookEntry>>(File.ReadAllText(_file), ConfigStore.JsonOptions)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<WordbookEntry> entries)
    {
        File.WriteAllText(_file, JsonSerializer.Serialize(entries.ToList(), ConfigStore.JsonOptions));
    }

    /// <summary>Add or refresh translation for the same word (case-insensitive).</summary>
    public WordbookEntry Upsert(string word, string translation, string? phonetic = null)
    {
        word = word.Trim();
        translation = translation.Trim();
        phonetic = phonetic?.Trim() ?? "";
        if (string.IsNullOrEmpty(word))
            throw new ArgumentException("empty word");

        var list = Load();
        var existing = list.FirstOrDefault(e =>
            string.Equals(e.Word, word, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.IsNullOrEmpty(translation))
                existing.Translation = translation;
            if (!string.IsNullOrEmpty(phonetic))
                existing.Phonetic = phonetic;
            existing.AddedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Newest first.
            list.Remove(existing);
            list.Insert(0, existing);
            Save(list);
            return existing;
        }

        var entry = new WordbookEntry { Word = word, Translation = translation, Phonetic = phonetic };
        list.Insert(0, entry);
        Save(list);
        return entry;
    }

    public bool Remove(string id)
    {
        var list = Load();
        var n = list.RemoveAll(e => e.Id == id);
        if (n == 0) return false;
        Save(list);
        return true;
    }
}
