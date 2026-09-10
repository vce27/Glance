using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance.Core;

public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _baseDir;
    private readonly string _settingsFile;
    private readonly string _historyFile;

    public ConfigStore(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.harukaon.glance");
        _settingsFile = Path.Combine(_baseDir, "settings.json");
        _historyFile = Path.Combine(_baseDir, "history.json");
    }

    public string BaseDir => _baseDir;

    public void Ensure()
    {
        Directory.CreateDirectory(_baseDir);
        if (!File.Exists(_settingsFile))
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(new TranslatorSettings(), JsonOptions));
        if (!File.Exists(_historyFile))
            File.WriteAllText(_historyFile, "[]");
    }

    public TranslatorSettings LoadSettings()
    {
        Ensure();
        var json = File.ReadAllText(_settingsFile);
        return JsonSerializer.Deserialize<TranslatorSettings>(json, JsonOptions) ?? new TranslatorSettings();
    }

    public void SaveSettings(TranslatorSettings settings)
    {
        Ensure();
        File.WriteAllText(_settingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public List<TranslationHistoryItem> LoadHistory()
    {
        Ensure();
        var json = File.ReadAllText(_historyFile);
        return JsonSerializer.Deserialize<List<TranslationHistoryItem>>(json, JsonOptions) ?? [];
    }

    public void SaveHistory(IReadOnlyList<TranslationHistoryItem> history)
    {
        Ensure();
        File.WriteAllText(_historyFile, JsonSerializer.Serialize(history, JsonOptions));
    }

    public void AppendHistory(TranslationHistoryItem item)
    {
        var history = LoadHistory();
        history.Insert(0, item);
        if (history.Count > 200)
            history = history.Take(200).ToList();
        SaveHistory(history);
    }
}
