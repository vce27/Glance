using System.Runtime.InteropServices;

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
