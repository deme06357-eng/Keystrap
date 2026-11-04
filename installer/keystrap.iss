; Keystrap installer (Inno Setup 6). Build it with tools\build-installer.ps1, which passes the version in from
; version.txt (/DAppVersion=...) and packages what app\build.ps1 already put in dist\.
;
; It installs per user, into %LocalAppData%\Programs\Keystrap, with no admin prompt. That matters: Keystrap's updater
; replaces its own files in place, which it couldn't do under Program Files without admin rights.
; Keystrap's data (settings, profiles, playtime) lives in %LocalAppData%\Keystrap and is left alone on uninstall.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define Root ".."

[Setup]
; Keep this AppId forever: it's how Windows knows a new version is an upgrade of the same app.
AppId={{6F3C2A91-5B7E-4D2A-9C1F-8E4B0D7A3F62}
AppName=Keystrap
AppVersion={#AppVersion}
AppVerName=Keystrap v{#AppVersion}
AppPublisher=Keystrap
AppPublisherURL=https://github.com/deme06357-eng/Keystrap
AppSupportURL=https://github.com/deme06357-eng/Keystrap
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=Keystrap

DefaultDirName={localappdata}\Programs\Keystrap
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
UsePreviousAppDir=yes

; WebView2 needs Windows 10 or later, and Keystrap is 64-bit.
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

SetupIconFile={#Root}\app\keystrap.ico
UninstallDisplayIcon={app}\Keystrap.exe
UninstallDisplayName=Keystrap
WizardStyle=modern
OutputDir={#Root}\release
OutputBaseFilename=KeystrapSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

; Close a running Keystrap before replacing it, and start it again afterwards.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Root}\dist\Keystrap.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\dist\Keystrap.Updater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\dist\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\dist\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\dist\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Keystrap"; Filename: "{app}\Keystrap.exe"
Name: "{autodesktop}\Keystrap"; Filename: "{app}\Keystrap.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Keystrap.exe"; Description: "{cm:LaunchProgram,Keystrap}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Give roblox.com's Play links back to Roblox before the exe goes away.
Filename: "{app}\Keystrap.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterLinks"

[UninstallDelete]
; Backups the updater leaves behind (Keystrap.exe.old, Keystrap.Updater.exe.old-<ticks>).
Type: files; Name: "{app}\*.old"
Type: files; Name: "{app}\*.old-*"
