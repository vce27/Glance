namespace Glance.Core;

public enum TextTranslateEngine
{
    Bing,
    Google,
    Microsoft,
    Transmart,
    Yandex,
    Iciba,
    Llm,
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
    public string Prompt { get; set; } =
        "You are a professional translator. Translate the following text from {from} to {to}. Only output the translation, nothing else. Do not add explanations or notes.";
    public string AutoPrompt { get; set; } =
        "You are a professional translator. Detect the source language and translate the following text to {to}. Only output the translation, nothing else. Do not add explanations or notes.";
}

public sealed class TranslatorSettings
{
    public string FromLang { get; set; } = "auto";
    public string ToLang { get; set; } = "zh-CHS";
    public string Clientele { get; set; } = "deskdict";
    public string Client { get; set; } = "deskdict";
    public string Vendor { get; set; } = "fanyiweb_navigation";
    public string InputChannel { get; set; } = "YoudaoDict_fanyiweb_navigation";
    public string AppVersion { get; set; } = "10.3.0.0";
    public string AbTest { get; set; } = "2";
    public string Model { get; set; } = "default";
    public string Screen { get; set; } = "1920*1080";
    public string OsVersion { get; set; } = "10.0";
    public string Network { get; set; } = "none";
    public string Mid { get; set; } = "windows10.0";
    public string Product { get; set; } = "deskdict";
    public string Yduuid { get; set; } = NewYduuid();
    public float OverlayOpacity { get; set; } = 0.92f;
    public float OverlayFontScale { get; set; } = 1.0f;
    public bool CloseOnOutsideClick { get; set; } = true;
    public bool Autostart { get; set; }
    public string Hotkey { get; set; } = "CommandOrControl+Shift+X";
    public string CopyHotkey { get; set; } = "CommandOrControl+Shift+C";
    public TextTranslateEngine TextTranslateEngine { get; set; } = TextTranslateEngine.Bing;
    public LlmConfig LlmConfig { get; set; } = new();
    public string? PopupShortcut { get; set; }
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    public string CustomProxy { get; set; } = "";
    public bool PinOnTop { get; set; }

    public static string NewYduuid()
    {
        var hex = Guid.NewGuid().ToString("N");
        return hex[..17];
    }
}

public sealed class SelectionPayload
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string MonitorId { get; set; } = "";
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }
    public uint MonitorWidth { get; set; }
    public uint MonitorHeight { get; set; }
    public double MonitorScaleFactor { get; set; } = 1.0;
}

public sealed class BoundingBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class RegionLine
{
    public string Text { get; set; } = "";
}

public sealed class OverlayRegion
{
    public BoundingBox Rect { get; set; } = new();
    public BoundingBox LocalRect { get; set; } = new();
    public string Source { get; set; } = "";
    public string Translated { get; set; } = "";
    public string Color { get; set; } = "default";
    public List<RegionLine> Lines { get; set; } = [];
}

public sealed class TranslationPair
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class TranslationHistoryItem
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string FromLang { get; set; } = "";
    public string ToLang { get; set; } = "";
    public SelectionPayload Selection { get; set; } = new();
    public List<TranslationPair> Pairs { get; set; } = [];
}

public sealed class TranslationResponse
{
    public string RequestId { get; set; } = "";
    public string LanFrom { get; set; } = "";
    public string LanTo { get; set; } = "";
    public string RenderedImageBase64 { get; set; } = "";
    public List<OverlayRegion> Regions { get; set; } = [];
    public List<TranslationPair> Pairs { get; set; } = [];
    public TranslationHistoryItem HistoryItem { get; set; } = new();
}

public sealed class TextTranslationResult
{
    public string TranslatedText { get; set; } = "";
    public string FromLangDetected { get; set; } = "";
    public List<string> Alternatives { get; set; } = [];
}

public enum CaptureMode
{
    Translate,
    CopyText,
}
