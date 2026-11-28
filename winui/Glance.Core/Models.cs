namespace Glance.Core;

public enum TextTranslateEngine
{
    Bing,
    Llm,
    Offline,
}

public enum ProxyMode
{
    None,
    System,
    Custom,
}

public sealed class LlmConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
}

public sealed class TranslatorSettings
{
    public string FromLang { get; set; } = "auto";
    public string ToLang { get; set; } = "zh-CHS";
    public bool Autostart { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+X";
    public string CopyHotkey { get; set; } = "Ctrl+Shift+C";
    public TextTranslateEngine TextTranslateEngine { get; set; } = TextTranslateEngine.Bing;
    public LlmConfig LlmConfig { get; set; } = new();
    public string? PopupShortcut { get; set; }
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    public string CustomProxy { get; set; } = "";
    public bool PinOnTop { get; set; }
    /// <summary>light | dark</summary>
    public string UiTheme { get; set; } = "light";
    /// <summary>Check GitHub for Velopack updates once after startup.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Show translation compare card under the screenshot selection.</summary>
    public bool ShowCompareOverlay { get; set; } = true;
    /// <summary>Select text in other apps to show a dictionary popup (划词取词).</summary>
    public bool WordCaptureEnabled { get; set; } = true;
}

public sealed class TranslationPair
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>Region box in cropped screenshot pixels (Youdao boundingBox). Null if unknown.</summary>
    public TranslationBounds? Bounds { get; set; }
}

public sealed class TranslationBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class TranslationResponse
{
    public string RenderedImageBase64 { get; set; } = "";
    public List<TranslationPair> Pairs { get; set; } = [];
}

public sealed class TextTranslationResult
{
    public string TranslatedText { get; set; } = "";
    /// <summary>Target-side romanization from Bing (Chinese pinyin, Japanese romaji, …).</summary>
    public string Transliteration { get; set; } = "";
    /// <summary>Which engine produced this result (必应 / AI 大模型 / 离线).</summary>
    public string SourceEngine { get; set; } = "";
}

public enum CaptureMode
{
    Translate,
    CopyText,
}
