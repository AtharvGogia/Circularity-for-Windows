; Inno Setup script for CircularitySetup.exe. Built by build\build.ps1, which passes:
;   AppVersion  - e.g. 0.1.0
;   PublishDir  - the self-contained app folder (with ffmpeg.exe and licences)
;   OutputDir   - where the setup .exe is written

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F3C2B1E-8A47-4C5D-9E21-C1A2C7B9D4F0}
AppName=Circularity
AppVersion={#AppVersion}
AppVerName=Circularity {#AppVersion}
AppPublisher=Atharv Gogia
AppCopyright=© 2026 Atharv Gogia
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Atharv Gogia
VersionInfoDescription=Circularity Setup

; Installs for the current user (%LOCALAPPDATA%\Programs\Circularity), so no admin prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Circularity
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 or later.
MinVersion=10.0.17763

OutputDir={#OutputDir}
OutputBaseFilename=CircularitySetup-{#AppVersion}
SetupIconFile=..\..\src\Circularity\Assets\Circularity.ico
UninstallDisplayIcon={app}\Circularity.exe
UninstallDisplayName=Circularity
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Circularity runs in the tray; close it before upgrading or uninstalling.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Circularity when I sign in"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{autoprograms}\Circularity"; Filename: "{app}\Circularity.exe"
Name: "{autodesktop}\Circularity"; Filename: "{app}\Circularity.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Circularity"; \
    ValueData: """{app}\Circularity.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Markdown/HTML/TXT → PDF needs WebView2. It's part of Windows 11; this only runs on PCs without it.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
    StatusMsg: "Installing Microsoft Edge WebView2..."; Check: NeedsWebView2; Flags: waituntilterminated
Filename: "{app}\Circularity.exe"; Description: "Start Circularity now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im Circularity.exe /f"; Flags: runhidden; RunOnceId: "StopCircularity"

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Version(Root: Integer; Key: String): String;
begin
  if not RegQueryStringValue(Root, Key + WebView2Client, 'pv', Result) then
    Result := '';
end;

function NeedsWebView2: Boolean;
var
  Version: String;
begin
  Version := WebView2Version(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\');
  if (Version = '') or (Version = '0.0.0.0') then
    Version := WebView2Version(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\');
  Result := (Version = '') or (Version = '0.0.0.0');
end;
