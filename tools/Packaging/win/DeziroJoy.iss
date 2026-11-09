; Inno Setup script for the DeziroJoy Windows installer.
;
; Built by the Release workflow through tools/Packaging/win/build-and-verify.ps1, which
; passes every build-specific value as an ISCC /D define:
;
;   ISCC DeziroJoy.iss ^
;     /DDeziroJoyVersion=0.0.1-beta.1-preview ^
;     /DDeziroJoyRid=win-x64 ^
;     /DDeziroJoyPublishDir=<absolute publish output dir> ^
;     /DDeziroJoyIconFile=<absolute dezirojoy.ico> ^
;     /DDeziroJoyOutputDir=<absolute output dir>
;
; Design constraints (M14, from the M11 packaging audit):
; - Per-user install with PrivilegesRequired=lowest: no UAC prompt, nothing written
;   outside the user profile, default directory {localappdata}\Programs\DeziroJoy.
; - The installer copies files and creates shortcuts only. It must not register
;   scheduled tasks, autostart entries, services or udev rules - the app keeps those
;   opt-in features in-app (see src/DeziroJoy.App/Program.cs) - and it must never
;   delete the user's configuration under %APPDATA%\DeziroJoy.

#ifndef DeziroJoyVersion
  #error DeziroJoyVersion is not defined. Pass /DDeziroJoyVersion=<version> to ISCC.
#endif
#ifndef DeziroJoyRid
  #error DeziroJoyRid is not defined. Pass /DDeziroJoyRid=<win-x64|win-arm64> to ISCC.
#endif
#ifndef DeziroJoyPublishDir
  #error DeziroJoyPublishDir is not defined. Pass /DDeziroJoyPublishDir=<absolute publish dir> to ISCC.
#endif
#ifndef DeziroJoyIconFile
  #error DeziroJoyIconFile is not defined. Pass /DDeziroJoyIconFile=<absolute .ico path> to ISCC.
#endif
#ifndef DeziroJoyOutputDir
  #error DeziroJoyOutputDir is not defined. Pass /DDeziroJoyOutputDir=<absolute output dir> to ISCC.
#endif

#define MyAppName "DeziroJoy"
#define MyAppExeName "DeziroJoy.exe"
#define MyAppVersion DeziroJoyVersion

; win-x64 ships x64 binaries. "x64compatible" also lets the same setup run on
; Windows 11 on ARM through x64 emulation, which plain "x64" would refuse.
; win-arm64 ships ARM64 binaries that cannot run on x64 Windows at all, so it is
; restricted to arm64 there.
#if DeziroJoyRid == "win-x64"
  #define MyArchAllowed "x64compatible"
#elif DeziroJoyRid == "win-arm64"
  #define MyArchAllowed "arm64"
#else
  #error Unsupported DeziroJoyRid. Expected win-x64 or win-arm64.
#endif

[Setup]
; Fixed identity. Changing this GUID orphans existing installs from upgrades and
; from their entry in Settings > Apps.
AppId={{E565E61E-24C0-4A2C-9DFF-4B8D02DD7382}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#DeziroJoyOutputDir}
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-{#DeziroJoyRid}-setup
SetupIconFile={#DeziroJoyIconFile}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed={#MyArchAllowed}
ArchitecturesInstallIn64BitMode={#MyArchAllowed}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; DeziroJoy is a tray-resident app, so it is usually running when the user upgrades.
; Let Setup close it through the Restart Manager instead of deadlocking on its own
; executable; nothing is restarted automatically afterwards.
CloseApplications=yes

[Languages]
; English is compiled into Setup itself; no translation files ship for one language.
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; The complete self-contained publish output - cross-platform DLLs and PDBs included,
; because crash reports need the symbols. Nothing is excluded and nothing is added.
Source: "{#DeziroJoyPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch after install, but never during the workflow's silent install.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
