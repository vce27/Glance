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
    public bool Autostart { get; set; }
    public string Hotkey { get; set; } = "CommandOrControl+Shift+X";
    public string CopyHotkey { get; set; } = "CommandOrControl+Shift+C";
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
}

public sealed class TranslationPair
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class TranslationResponse
{
    public string RequestId { get; set; } = "";
    public string LanFrom { get; set; } = "";
    public string LanTo { get; set; } = "";
    public string RenderedImageBase64 { get; set; } = "";
    public List<TranslationPair> Pairs { get; set; } = [];
}

public sealed class TextTranslationResult
{
    public string TranslatedText { get; set; } = "";
    public string FromLangDetected { get; set; } = "";
}

public enum CaptureMode
{
    Translate,
    CopyText,
}
