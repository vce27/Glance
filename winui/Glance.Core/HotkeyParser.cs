namespace Glance.Core;

/// <summary>Parses Tauri-style hotkey strings into Win32 modifiers + virtual key.</summary>
public readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey, string Source)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;
}

public static class HotkeyParser
{
    public static bool TryParse(string? raw, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        uint mods = HotkeySpec.ModNoRepeat;
        uint? vk = null;
        foreach (var part in raw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Equals("CommandOrControl", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Control", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("CmdOrCtrl", StringComparison.OrdinalIgnoreCase))
            {
                mods |= HotkeySpec.ModControl;
            }
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Option", StringComparison.OrdinalIgnoreCase))
            {
                mods |= HotkeySpec.ModAlt;
            }
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                mods |= HotkeySpec.ModShift;
            }
            else if (p.Equals("Super", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Meta", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                mods |= HotkeySpec.ModWin;
            }
            else
            {
                vk = ResolveKey(p);
                if (vk is null) return false;
            }
        }

        if (vk is null) return false;
        spec = new HotkeySpec(mods, vk.Value, raw);
        return true;
    }

    /// <summary>
    /// Build a settings string from a captured key + modifiers (e.g. F4, Ctrl+Shift+X).
    /// Returns null for modifier-only presses or unsupported keys.
    /// </summary>
    public static string? FormatCapture(bool ctrl, bool alt, bool shift, bool win, uint virtualKey)
    {
        // Modifier-only — wait for the real key.
        if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C // Shift/Ctrl/Alt/Win
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) // left/right variants
            return null;

        var key = FormatKey(virtualKey);
        if (key is null) return null;

        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);
        return string.Join('+', parts);
    }

    private static string? FormatKey(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1), // F1–F24
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A–Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0–9
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        0xBB => "Plus",
        0xBD => "Minus",
        _ => null,
    };

    private static uint? ResolveKey(string key)
    {
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (key.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1); // VK_F1

        return key.ToUpperInvariant() switch
        {
            "SPACE" or "SPACEBAR" => 0x20,
            "TAB" => 0x09,
            "ESCAPE" or "ESC" => 0x1B,
            "ENTER" or "RETURN" => 0x0D,
            "BACKSPACE" or "BACK" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "UP" or "ARROWUP" => 0x26,
            "DOWN" or "ARROWDOWN" => 0x28,
            "LEFT" or "ARROWLEFT" => 0x25,
            "RIGHT" or "ARROWRIGHT" => 0x27,
            "PLUS" or "=" => 0xBB,
            "MINUS" or "-" => 0xBD,
            _ => null,
        };
    }
}

public static class AutostartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Glance";

    public static void Apply(bool enabled, string exePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (enabled)
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
        else
            key.DeleteValue(ValueName, false);
    }
}
