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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _baseDir;
    private readonly string _settingsFile;

    public ConfigStore(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.harukaon.glance");
        _settingsFile = Path.Combine(_baseDir, "settings.json");
    }

    public string BaseDir => _baseDir;

    public void Ensure()
    {
        Directory.CreateDirectory(_baseDir);
        if (!File.Exists(_settingsFile))
            File.WriteAllText(_settingsFile, JsonSerializer.Serialize(new TranslatorSettings(), JsonOptions));
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
}
