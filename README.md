# Glance (WinUI 3)

Screenshot OCR / translate desktop app for Windows. Native **WinUI 3 + Mica** rewrite of the former Tauri/WebView build.

## Requirements

- Windows 10 1809+ (Windows 11 recommended)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- Developer Mode recommended for first-run tooling

## Build & run

```powershell
cd winui
dotnet restore Glance.sln
dotnet build Glance.sln -c Release -p:Platform=x64
dotnet run --project Glance.App -c Release -p:Platform=x64
```

Publish self-contained:

```powershell
cd winui
dotnet publish Glance.App\Glance.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishTrimmed=false -o .\publish\win-x64
```

Install locally (replaces prior portable install):

```powershell
$dst = Join-Path $env:LOCALAPPDATA Glance
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item .\winui\publish\win-x64\* $dst -Recurse -Force
Start-Process (Join-Path $dst Glance.exe)
```

## Features (MVP)

- Light **Mica** main window (Fluent controls)
- Text translate via **Bing**
- Screenshot region translate via **Youdao** image OCR
- OCR-copy hotkey → clipboard
- Tray (close hides to tray), single instance, autostart `--minimized`
- Settings / history at `%APPDATA%\com.harukaon.glance\` (same JSON as Tauri)

## Project layout

```
winui/
  Glance.App/       WinUI shell (Mica, tray, hotkeys, main UI)
  Glance.Core/      settings/history JSON, Youdao, Bing
  Glance.Capture/   BitBlt + selection overlay
legacy-tauri/       archived Tauri 2 + WebView sources
```

## Hotkeys

Configured in settings (Tauri-style strings still accepted), e.g. `F4`, `Alt+M`, `CommandOrControl+Shift+X`.

## Legacy

The previous Tauri implementation lives under `legacy-tauri/` for reference. Windows development continues on this WinUI tree.
