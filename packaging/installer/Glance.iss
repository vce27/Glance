; Outer installer: directory picker UI, then silent Velopack install (keeps auto-update).
; Build (CI): iscc /DMyAppVersion=x.y.z /DVelopackSetup=...\Glance-win-VelopackSetup.exe Glance.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef VelopackSetup
  #define VelopackSetup "Glance-win-VelopackSetup.exe"
#endif
#ifndef MyAppIcon
  #define MyAppIcon "..\..\winui\Glance.App\Assets\AppIcon.ico"
#endif

[Setup]
AppId={{A7E3C2B1-4F5D-4A8E-9C1B-GlanceWinUI00}}
AppName=Glance
AppVersion={#MyAppVersion}
AppPublisher=vce27
AppPublisherURL=https://github.com/vce27/Glance
DefaultDirName={localappdata}\Glance
DefaultGroupName=Glance
DisableProgramGroupPage=yes
; Per-user by default; allow elevation if the user picks Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#MyAppIcon}
Uninstallable=no
CreateUninstallRegKey=no
DirExistsWarning=no
UsePreviousAppDir=yes
OutputDir=..\..\artifacts\release
OutputBaseFilename=Glance-win-Setup
LicenseFile=license.txt
InfoBeforeFile=welcome.txt

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Only stage the Velopack bootstrapper; it lays down the real app under {app}.
Source: "{#VelopackSetup}"; DestDir: "{tmp}"; DestName: "Glance-win-VelopackSetup.exe"; Flags: ignoreversion deleteafterinstall

[Run]
Filename: "{tmp}\Glance-win-VelopackSetup.exe"; \
  Parameters: "--silent --installto ""{app}"""; \
  StatusMsg: "正在安装 Glance…"; \
  Flags: waituntilterminated

[Code]
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if Trim(WizardDirValue) = '' then
    begin
      MsgBox('请选择安装目录。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
