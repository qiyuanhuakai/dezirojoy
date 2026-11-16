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
; - The one thing it does beyond copying files is [UninstallRun] below: it asks the
;   installed app to hand back its own opt-in ms-gamebar protocol registration before
;   the files are removed. The installer still writes no registry entry of its own for
;   that; the app owns the registration and knows how to restore the previous command.

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
; from their entry in Settings > Apps. A literal opening brace is escaped as '{{'
; in an Inno script value; a closing brace needs no escape and stays a single '}'
; (beta.1 shipped '{{...}}', so the uninstall key gained a doubled closing brace -
; run 42678407586). build-and-verify.ps1 asserts this exact AppId before compiling.
AppId={{E565E61E-24C0-4A2C-9DFF-4B8D02DD7382}
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
; English is compiled into Setup itself and stays the fallback (compiler:Default.isl
; is the one file that always sits next to ISCC). Simplified Chinese ships with the
; repository instead of being read from the compiler: Inno Setup's own
; ChineseSimplified.isl (LanguageID $0804, the official translation since 6.5.0 and
; unchanged for 6.7.1) lives next to this script under Languages\ and is referenced
; relative to it, so the build no longer depends on the runner's Inno install
; carrying that language file. With more than one language declared, Setup shows the
; language dialog (ShowLanguageDialog=auto) and preselects the system language;
; English remains selectable there and is what an unknown locale falls back to.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

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

; The in-app updater's relaunch, compiled only when the build passes /DDeziroJoyUpdateRun. The
; updater launches this setup /VERYSILENT and then exits immediately, releasing the DeziroJoy.exe
; / DLL handles it holds (CloseApplications=yes above), so the relaunch cannot come from the
; updater - it must come from Setup, once the new files are in place. The CI silent
; install/uninstall loop and an ordinary release build never pass the define, so they keep
; exactly the single skipifsilent offer above and never relaunch on their own. This entry
; deliberately omits skipifsilent so the silent update run performs the relaunch, and omits a
; Description so it runs without a check box.
#ifdef DeziroJoyUpdateRun
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall
#endif

[UninstallRun]
; Ask the installed app to hand the per-user ms-gamebar no-op protocol handler back
; before the app files are removed, so a normal uninstall does not leave a registration
; pointing at a deleted DeziroJoy.exe. [UninstallRun] entries run as the first step of
; uninstallation - the executable is still in place - and Uninstall waits for each entry
; to terminate (waituntilterminated, the default, stated explicitly) before deleting
; anything. Two exceptions are accepted rather than papered over: if the executable is
; already gone the entry is skipped (skipifdoesntexist), and if the restore fails Inno
; does not abort the uninstall on the child's exit code - either way the registration can
; survive and must be handed back manually afterwards (settings card or the same command
; line), which is why the app - not the installer - owns the restore logic.
;
; Scope: the registration this app installed lives in the current user's own
; HKCU\Software\Classes\ms-gamebar. No HKLM, no service, no machine-wide write; the
; child runs as the uninstaller's user (runascurrentuser), which for this per-user
; install is the user whose hive holds the registration. The app - not the installer -
; owns the registration and knows how to restore the command the protocol had before,
; or to remove the key when it had none. It refuses to act when another program has
; taken the command over (the marker alone is not ownership), and it never claims to
; restore a machine-wide or third-party state it never recorded; the uninstaller only
; asks it to do that (--uninstall-gamebar-handler).
;
; Failure posture: skipifdoesntexist keeps a missing executable from raising the
; "some elements could not be removed" warning, and Inno never fails an uninstall on
; the child's exit code. There is deliberately no skipifsilent (that flag is not even
; valid here): a silent uninstall is a normal uninstall - the CI install/uninstall loop
; and any scripted one - and must not leave the stale association behind either. The
; in-app update flow never runs the uninstaller, so an update that keeps the install
; path leaves the registration untouched.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-gamebar-handler"; Flags: runhidden runascurrentuser waituntilterminated skipifdoesntexist; RunOnceId: "GameBarProtocolHandler"
