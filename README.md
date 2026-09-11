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

Install (Velopack, enables auto-update):

```powershell
# after vpk pack … -o .\publish\velopack
.\winui\publish\velopack\Glance-win-Setup.exe
# or silent:
.\winui\publish\velopack\Glance-win-Setup.exe --silent
```

App installs to `%LOCALAPPDATA%\Glance\`. Do **not** manually copy `publish\win-x64\*` into that folder — it breaks Velopack.

## Features (MVP)

- Light **Mica** main window (Fluent controls)
- Text translate via **Bing**
- Screenshot region translate via **Youdao** image OCR
- OCR-copy hotkey → clipboard
- Tray (close hides to tray), single instance, autostart `--minimized`
- Settings at `%APPDATA%\com.harukaon.glance\`
- **Velopack** auto-update from GitHub Releases (Setup installer); `v*` tags publish Setup + portable ZIP

## Project layout

```
winui/
  Glance.App/       WinUI shell (Mica, tray, hotkeys, main UI, Velopack)
  Glance.Core/      settings JSON, Youdao, Bing, proxy, LLM
  Glance.Capture/   BitBlt + selection overlay
legacy-tauri/       archived Tauri 2 + WebView sources
```

## Updates (Velopack)

Install via the `Glance-win-Setup.exe` from GitHub Releases (not the portable ZIP). Then:

- Settings → **自动检查**: startup check, notify when a newer release exists
- **检查更新**: download + restart into the new version

Update feed defaults to `https://github.com/vce27/Glance` Releases. Override locally with `GLANCE_UPDATE_SOURCE`.

## Hotkeys

Configured in settings (Tauri-style strings still accepted), e.g. `F4`, `Alt+M`, `CommandOrControl+Shift+X`.

## Legacy

The previous Tauri implementation lives under `legacy-tauri/` for reference. Windows development continues on this WinUI tree.
