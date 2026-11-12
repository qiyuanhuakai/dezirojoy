<#
  Compiles the DeziroJoy Windows installer with Inno Setup and verifies the result.

  Usage (Release workflow, one call per Windows RID):
    pwsh tools/Packaging/win/build-and-verify.ps1 `
      -Version 0.0.1 -Rid win-x64 -PublishDir publish/win-x64

  Every path is resolved from the script's own location ($PSScriptRoot), never from
  the caller's working directory, so the invocation works from anywhere.

  Two setups are compiled, deliberately (M64 leftover, wired for the 0.0.1 release):
    * The published setup.exe is compiled WITH /DDeziroJoyUpdateRun, so it carries the
      conditional [Run] relaunch entry in DeziroJoy.iss. The in-app updater relaunches
      this setup /VERYSILENT; that entry omits skipifsilent so Setup - not the updater,
      which the Restart Manager closes mid-install - restarts the app once the new files
      are in place. This is the artefact the workflow uploads.
    * The silent install / verify / uninstall loop compiles and installs a SECOND,
      define-free setup. The relaunch entry above would launch the app during a
      /VERYSILENT install, so the loop must run against a build that does not carry it:
      same .iss, same payload, same RID, only the update-run entry differs. The
      verification behaviour is therefore exactly what it has always been.

  What is verified here:
    * ISCC is resolved to the real Inno compiler: the directory the Chocolatey
      shim's ISCC.shim redirects to is tried first (on windows-latest PATH
      resolves ISCC to that shim, a redirector that only forwards to the real
      compiler), then the directory PATH resolves ISCC from, then the standard
      Inno Setup 6/7 install directories; the first directory carrying ISCC.exe
      wins. The Chinese resource file no longer has to sit next to it - the
      installer references the repository's own translation instead (see below).
    * The installer ships a Simplified Chinese interface built from the
      repository's own ChineseSimplified.isl (Inno Setup's official translation,
      referenced as Languages\ChineseSimplified.isl relative to the .iss), with
      English kept as the fallback, and the wizard's custom texts (the
      desktop-icon task and the launch-after-install entry) are referenced through
      the language files ({cm:...}) instead of hard-coded English, so both
      languages cover them; the repository translation file and the message keys
      it must define are verified before the compile.
    * The publish payload (DeziroJoy.exe) carries the machine type the RID implies
      (x64 / ARM64), and the .iss maps the RID to the matching ArchitecturesAllowed /
      ArchitecturesInstallIn64BitMode: those two decide where a setup may install.
    * The compiled setup.exe is the x86 bootstrap Inno Setup always emits, whatever
      the payload architecture is.
    * For win-x64, the setup is silently installed into a throwaway directory. The
      install is waited for twice - first the setup process, whose own exit code
      (never a stale $LASTEXITCODE) decides success, then the installed artefact
      itself under a bounded timeout - the installed DeziroJoy.exe and its version
      are asserted, and it is then silently uninstalled with the install directory,
      the uninstall registry key and the shortcuts asserted gone. Any install
      failure or missing artefact prints the Inno install log tail and every
      directory the payload could have landed in before failing.

  Inno Setup 6 compiles a 32-bit x86 setup bootstrap for every architecture: the
  loader is architecture-neutral, and the payload plus ArchitecturesAllowed decide
  where a setup installs. A win-arm64 setup therefore cannot be installed on the x64
  windows-latest image (ArchitecturesAllowed=arm64 refuses to run there, and the
  ARM64 payload cannot execute), so for that RID the runtime install/uninstall cycle
  is reported as skipped: the compile and the static architecture checks are all a
  cross-architecture artefact can prove here. The arm64 cycle needs an ARM64 runner
  (windows-11-arm).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Rid,
  [Parameter(Mandatory)][string]$PublishDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PeMachineType([string]$Path) {
  $bytes = [System.IO.File]::ReadAllBytes($Path)
  if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
    throw "not a DOS/PE executable: $Path"
  }
  $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
  if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
    throw "PE header offset out of range: $Path"
  }
  if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
    throw "missing PE signature: $Path"
  }
  return [BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

# The script lives at tools/Packaging/win, so the repository root is three levels
# up. Deriving it from $PSScriptRoot keeps every path below independent of the
# caller's working directory; walking one level short lands on tools/ and each
# joined path then gains a spurious tools\ segment, so assert the root itself.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (-not (Test-Path (Join-Path $repoRoot 'DeziroJoy.slnx'))) {
  throw "resolved path is not the repository root: $repoRoot"
}
$iss = Join-Path $repoRoot 'tools/Packaging/win/DeziroJoy.iss'
$icon = Join-Path $repoRoot 'src/DeziroJoy.App/Assets/dezirojoy.ico'
$setupExeName = "DeziroJoy-$Version-$Rid-setup.exe"
$setupExePath = Join-Path $repoRoot $setupExeName

# The Release workflow passes the publish directory relative to the repository
# root; accept a repository-root-relative one as well so the script also works
# when invoked from another working directory.
$publish = $null
foreach ($candidate in @($PublishDir, (Join-Path $repoRoot $PublishDir))) {
  if (Test-Path $candidate) { $publish = (Resolve-Path $candidate).Path; break }
}
if (-not $publish) {
  throw "publish directory not found: $PublishDir (looked in the working directory and under $repoRoot)"
}

foreach ($required in @($iss, $icon)) {
  if (-not (Test-Path $required)) { throw "required file missing: $required" }
}
$payloadExe = Join-Path $publish 'DeziroJoy.exe'
if (-not (Test-Path $payloadExe)) {
  throw "DeziroJoy.exe missing from publish output: $publish"
}

# --- payload and .iss must agree with the RID ----------------------------------------
# The architecture a user actually receives is decided by two things: the machine
# type of the payload binaries and the ArchitecturesAllowed /
# ArchitecturesInstallIn64BitMode pair in the .iss. The compiled setup.exe bootstrap
# is architecture-neutral (checked after the compile), so both are verified here,
# before the compile: a mislabeled publish directory or a loosened [Setup] directive
# then fails the build instead of shipping a setup that installs ARM64 binaries on
# x64 Windows.
$expectedMachine = if ($Rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
$expectedAllowed = if ($Rid -eq 'win-x64') { 'x64compatible' } else { 'arm64' }

$payloadMachine = Get-PeMachineType $payloadExe
if ($payloadMachine -ne $expectedMachine) {
  throw "unexpected payload machine type 0x{0:X4} for $Rid, expected 0x{1:X4}: $payloadExe" -f $payloadMachine, $expectedMachine
}
Write-Host ("payload machine type: 0x{0:X4} (as expected for {1})" -f $payloadMachine, $Rid)

# Walk the RID -> ArchitecturesAllowed mapping in the .iss the way the Inno
# preprocessor resolves it, so the branch that matches this RID is the one checked,
# and both [Setup] directives must reference that mapping rather than a literal.
$allowedInIss = $null
$inRidBranch = $false
foreach ($line in Get-Content $iss) {
  if ($line -match '^\s*#(?:if|elif)\s+DeziroJoyRid\s*==\s*"(win-x64|win-arm64)"') {
    $inRidBranch = ($Matches[1] -eq $Rid)
  }
  elseif ($line -match '^\s*#(?:else|endif)') {
    $inRidBranch = $false
  }
  elseif ($inRidBranch -and $line -match '^\s*#define\s+MyArchAllowed\s+"([^"]+)"') {
    $allowedInIss = $Matches[1]
  }
}
if (-not $allowedInIss) {
  throw "DeziroJoy.iss maps no ArchitecturesAllowed value for $Rid"
}
if ($allowedInIss -ne $expectedAllowed) {
  throw "DeziroJoy.iss allows '$allowedInIss' for $Rid, expected '$expectedAllowed'"
}
$issText = Get-Content $iss -Raw
foreach ($directive in @('ArchitecturesAllowed', 'ArchitecturesInstallIn64BitMode')) {
  if ($issText -notmatch "(?m)^\s*$directive=\{#MyArchAllowed\}\s*$") {
    throw "DeziroJoy.iss: $directive must be {#MyArchAllowed} so it tracks the RID mapping"
  }
}
Write-Host "$Rid ArchitecturesAllowed/InstallIn64BitMode: $allowedInIss"

# --- verify the AppId resolves to the stable uninstall key --------------------------
# The per-user uninstall registration Inno writes at the end of a successful install
# is named '<AppId>_is1' (Inno help, topic_setup_appid), and both the install wait
# below and the post-uninstall assertion key off it. Inno Setup treats a lone '{' as
# the start of a constant: a literal opening brace must be escaped as '{{', while a
# closing brace needs no escape and stays a single '}' (Inno help, topic_consts).
# beta.1 shipped 'AppId={{...}}' - the doubled closing brace survived into the value -
# so the installer registered '{GUID}}_is1' while the wait below polled for
# '{GUID}_is1' and failed 180 s later (run 42678407586). Resolve the AppId the way
# Inno's tokenizer does and fail here, before the compile and the install, when it is
# not exactly the expected key: that is the static regression for this brace bug.
# The GUID itself is asserted unchanged because changing it orphans existing installs
# from upgrades and from their entry in Settings > Apps.
$expectedAppId = '{E565E61E-24C0-4A2C-9DFF-4B8D02DD7382}'
if ($issText -notmatch '(?m)^\s*AppId=(.+)$') {
  throw "DeziroJoy.iss: no AppId directive found"
}
$resolvedAppId = $Matches[1].Trim() -replace '\{\{', '{'
if ($resolvedAppId -ne $expectedAppId) {
  throw "DeziroJoy.iss: AppId resolves to '{0}', expected '{1}' (a literal opening brace is escaped as '{{' in an Inno script value; a closing brace must not be doubled)" -f $resolvedAppId, $expectedAppId
}
$uninstallKeyName = "${resolvedAppId}_is1"
$uninstallKeyPaths = @(
  "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$uninstallKeyName",
  "HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$uninstallKeyName"
)
Write-Host "AppId: $resolvedAppId (uninstall key: $uninstallKeyName)"

# --- verify the installer languages (M30) ------------------------------------------
# The setup must ship a Simplified Chinese interface and keep English as the
# fallback. Simplified Chinese comes from the repository's own
# ChineseSimplified.isl (Inno Setup's official translation, LanguageID $0804),
# referenced as Languages\ChineseSimplified.isl relative to the .iss, so the
# build no longer depends on the runner's Inno install carrying that language
# file; English stays the compiler:Default.isl fallback. With more than one
# language declared, Setup shows the language dialog and preselects the system
# locale, and English stays selectable there. Both entries must be in
# [Languages] - dropping either the Chinese entry or the English fallback fails
# here, before the compile.
$languagesSection = [regex]::Match($issText, '(?ms)^\[Languages\][ \t]*\r?\n(.*?)(?=^\[|\z)').Groups[1].Value
if (-not $languagesSection) {
  throw "DeziroJoy.iss: no [Languages] section found"
}
if ($languagesSection -notmatch '(?m)^\s*Name:\s*"english"\s*;\s*MessagesFile:\s*"compiler:Default\.isl"\s*$') {
  throw "DeziroJoy.iss: [Languages] must keep English via compiler:Default.isl as the fallback language"
}
if ($languagesSection -notmatch '(?m)^\s*Name:\s*"chinesesimplified"\s*;\s*MessagesFile:\s*"Languages\\ChineseSimplified\.isl"\s*$') {
  throw "DeziroJoy.iss: [Languages] must declare Simplified Chinese via Languages\ChineseSimplified.isl (the repository's own translation file, next to this script)"
}

# The wizard's custom texts - the desktop-icon task and the launch-after-install
# entry - must be referenced through the language files with {cm:...} constants,
# because a hard-coded English string there stays English on a Chinese install
# and a missing message would print the constant itself. The keys used below are
# exactly the ones the .iss references; the Chinese resource must define them
# (checked against the repository file below).
if ($issText -notmatch '(?m)^\s*Name:\s*"desktopicon"\s*;\s*Description:\s*"\{cm:CreateDesktopIcon\}"') {
  throw "DeziroJoy.iss: the desktopicon task must be localized through {cm:CreateDesktopIcon}"
}
if ($issText -notmatch 'Description:\s*"\{cm:LaunchProgram,\{#MyAppName\}\}"') {
  throw "DeziroJoy.iss: the launch-after-install entry must be localized through {cm:LaunchProgram,...}"
}
Write-Host "installer languages: english (fallback) + chinesesimplified; custom texts via {cm:CreateDesktopIcon} / {cm:LaunchProgram}"

# --- resolve ISCC ------------------------------------------------------------------
# windows-latest ships Inno Setup 6 on PATH, but a PATH hit is not necessarily the
# real compiler: the Chocolatey ISCC shim at C:\ProgramData\Chocolatey\bin\ISCC.exe
# only redirects to it. The shim carries the answer next to itself: a companion
# text file (ISCC.shim) whose 'path = <real ISCC.exe>' line names the compiler it
# redirects to. Read it when present - one level only, so a malformed shim cannot
# loop - and try the redirect target's directory first; otherwise the candidate
# directories are the one PATH resolves ISCC from, then the standard Inno Setup
# 6/7 install directories. The compiler used is the first candidate carrying
# ISCC.exe. The Chinese resource file no longer participates in this choice: the
# installer references the repository's own ChineseSimplified.isl, so a runner
# whose Inno install omits that language file still compiles - the beta.3-preview
# runs (42962660225, 42972786624, 42975039773) failed exactly there.
$standardDirs = @(
  "$env:ProgramFiles\Inno Setup 6",
  "${env:ProgramFiles(x86)}\Inno Setup 6",
  "$env:ProgramFiles\Inno Setup 7",
  "${env:ProgramFiles(x86)}\Inno Setup 7"
)
$onPath = Get-Command ISCC -ErrorAction SilentlyContinue

# The Chocolatey shim keeps its redirect target in a companion .shim file next
# to the shimmed executable (ISCC.shim beside ISCC.exe). Parse it loosely - the
# first 'path = <file>' line wins, surrounding whitespace and quotes are
# stripped - and keep the target only when it exists. A missing shim, a shim
# without a usable path line, or a target that is not there changes nothing: the
# candidates below then apply exactly as before.
$shimTarget = $null
if ($onPath) {
  $onPathDir = Split-Path -Parent $onPath.Source
  $shimCandidate = Join-Path $onPathDir ([System.IO.Path]::GetFileNameWithoutExtension($onPath.Source) + '.shim')
  if (Test-Path -LiteralPath $shimCandidate) {
    $shimText = Get-Content -LiteralPath $shimCandidate -Raw
    if ($shimText -match '(?im)^\s*path\s*=\s*(.+)$') {
      $shimTarget = $Matches[1].Trim().Trim('"', "'").Trim()
    }
    if ($shimTarget -and (Test-Path -LiteralPath $shimTarget)) {
      Write-Host "ISCC on PATH ($($onPath.Source)) is a Chocolatey shim; $shimCandidate redirects to $shimTarget"
    }
    else {
      # An unusable target (no path line, or the file it names is not there)
      # is dropped; the candidate directories below then apply as before.
      $shimTarget = $null
    }
  }
}

$candidateDirs = @()
if ($shimTarget) { $candidateDirs += Split-Path -Parent $shimTarget }
if ($onPath) { $candidateDirs += (Split-Path -Parent $onPath.Source) }
$candidateDirs += $standardDirs
$candidateDirs = @($candidateDirs | Select-Object -Unique)

$compilerDir = $null
foreach ($dir in $candidateDirs) {
  if (Test-Path -LiteralPath (Join-Path $dir 'ISCC.exe')) {
    $compilerDir = $dir
    break
  }
}
if (-not $compilerDir) {
  throw @"
ISCC (Inno Setup Compiler) not found.
Looked for ISCC on PATH and at:
  $env:ProgramFiles\Inno Setup 6\ISCC.exe
  ${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe
  $env:ProgramFiles\Inno Setup 7\ISCC.exe
  ${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe
Install Inno Setup 6 or newer, or add its directory to PATH.
"@
}

$iscc = Join-Path $compilerDir 'ISCC.exe'
if ($shimTarget -and (Split-Path -Parent $shimTarget) -eq $compilerDir) {
  Write-Host "compiling with the real Inno compiler the Chocolatey shim redirects to: $iscc"
}
Write-Host "ISCC: $iscc"

# The Simplified Chinese interface is only as real as this file. Verify it is
# present and defines the messages the installer's custom texts reference: a
# missing or stripped translation file, or one missing a key, would compile but
# leave the affected pages showing the raw {cm:...} constant. It ships with the
# repository next to the .iss, so this no longer depends on the resolved ISCC.
$zhIsl = Join-Path $repoRoot 'tools/Packaging/win/Languages/ChineseSimplified.isl'
if (-not (Test-Path -LiteralPath $zhIsl)) {
  throw "the repository's Simplified Chinese translation is missing: $zhIsl"
}
$zhIslText = Get-Content -LiteralPath $zhIsl -Raw
foreach ($message in @('CreateDesktopIcon', 'LaunchProgram')) {
  if ($zhIslText -notmatch "(?m)^\s*$message\s*=") {
    throw "ChineseSimplified.isl ($zhIsl) does not define the message '$message' that the installer's custom texts reference"
  }
}
Write-Host "ChineseSimplified.isl: $zhIsl (defines CreateDesktopIcon and LaunchProgram)"

# --- compile helper ----------------------------------------------------------------
# Drives ISCC with the full /D set the .iss requires. -WithUpdateRun adds
# /DDeziroJoyUpdateRun, which compiles in the conditional [Run] relaunch entry the
# in-app updater relies on (DeziroJoy.iss). That entry omits skipifsilent so the
# updater's silent update run restarts the app - which is exactly why only the
# release artefact may carry it, and the verification loop below compiles a second,
# define-free setup instead.
function Invoke-Compile([string]$OutputDir, [switch]$WithUpdateRun) {
  $defines = @(
    "/DDeziroJoyVersion=$Version"
    "/DDeziroJoyRid=$Rid"
    "/DDeziroJoyPublishDir=$publish"
    "/DDeziroJoyIconFile=$icon"
    "/DDeziroJoyOutputDir=$OutputDir"
  )
  if ($WithUpdateRun) { $defines += "/DDeziroJoyUpdateRun" }
  # ISCC's own progress output is left on the success stream on purpose: the callers
  # run this as a statement (never assigning its result), so it stays visible in the
  # CI log. Nothing is returned - the compiled path is deterministic and the caller
  # reads it back from $OutputDir + $setupExeName.
  & $iscc $iss @defines
  if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE (OutputDir=$OutputDir, WithUpdateRun=$WithUpdateRun)"
  }
  $built = Join-Path $OutputDir $setupExeName
  if (-not (Test-Path $built)) {
    throw "ISCC reported success but $setupExeName is missing from $OutputDir"
  }
  if ($WithUpdateRun) {
    Write-Host "compiled $setupExeName into $OutputDir WITH the in-app updater relaunch entry (/DDeziroJoyUpdateRun), $((Get-Item $built).Length) bytes"
  }
  else {
    Write-Host "compiled $setupExeName into $OutputDir define-free (no updater relaunch entry), $((Get-Item $built).Length) bytes"
  }
}

# --- compile the release artefact --------------------------------------------------
# The published setup.exe is built WITH /DDeziroJoyUpdateRun so it carries the
# conditional [Run] relaunch entry: when the in-app updater relaunches this setup
# /VERYSILENT, Setup restarts the app once the new files are in place (the updater
# itself is closed mid-install by the Restart Manager, CloseApplications=yes, so the
# relaunch has to come from Setup). This is the artefact the workflow uploads.
Invoke-Compile -OutputDir $repoRoot -WithUpdateRun
$setupExePath = Join-Path $repoRoot $setupExeName

# --- verify the compiled bootstrap ----------------------------------------------------
# Inno Setup always compiles a 32-bit x86 setup bootstrap, whatever the payload is:
# the loader is architecture-neutral and the ArchitecturesAllowed pair plus the
# payload decide where the setup installs. Expecting x64/arm64 on setup.exe itself
# fails on every Inno build (beta.1 run 42670117262: bootstrap machine 0x014C on
# both Windows RIDs); expecting the x86 loader catches a replaced or corrupted
# bootstrap, while the payload check above guards the architecture the user actually
# receives.
$bootstrapMachine = Get-PeMachineType $setupExePath
if ($bootstrapMachine -ne 0x014C) {
  throw "unexpected setup.exe machine type 0x{0:X4}, expected the Inno x86 bootstrap 0x014C" -f $bootstrapMachine
}
Write-Host ("setup.exe bootstrap machine type: 0x{0:X4} (Inno x86 loader, architecture-neutral)" -f $bootstrapMachine)

if ($Rid -ne 'win-x64') {
  Write-Host "ARM64 setup cannot install on this x64 runner (ArchitecturesAllowed=arm64 refuses, payload cannot execute): silent install/uninstall smoke skipped."
  return
}

# --- silent install into a throwaway directory --------------------------------------
# The compiled setup.exe is Inno's x86 bootstrap: it extracts the real Setup program
# into TEMP and waits for it to terminate before returning its exit code, so waiting
# on the bootstrap process already covers the whole install. That wait is still not
# trusted alone here: the beta.1 run (42672692136) saw this call return exit code 0
# about 140 ms after the bootstrap check with nothing installed, which no documented
# Inno exit path produces - SetupLdr only exits 0 after a successful Setup child, and
# every failure path in it exits non-zero. (That `&` waits at all is not in doubt
# either: the 37-second ISCC compile above is waited for the same way in this very
# step.) Whether the process never entered the install or the check raced it cannot
# be told apart from outside, so completion is established twice: Start-Process -Wait
# for the process, whose own ExitCode - not a possibly stale $LASTEXITCODE - decides
# success, and then a bounded poll for the installed artefact, which is what the rest
# of the verification actually depends on. A genuinely broken install therefore fails
# at the poll timeout with the log tail and directory listing below, never by
# silently skipping the check.
$installTimeoutSeconds = 180
$installPollMilliseconds = 250

$installDir = Join-Path $env:TEMP ("DeziroJoy-Install-{0}" -f [guid]::NewGuid().ToString('N'))
$installLog = Join-Path $env:TEMP ("DeziroJoy-Install-{0}.log" -f [guid]::NewGuid().ToString('N'))
$uninstallLog = Join-Path $env:TEMP ("DeziroJoy-Uninstall-{0}.log" -f [guid]::NewGuid().ToString('N'))
# The verification loop installs a SECOND, define-free setup rather than the release
# artefact compiled above: the release build carries the in-app updater relaunch entry,
# which omits skipifsilent and would launch the app during a /VERYSILENT install. The
# loop must observe exactly the behaviour it always has - no relaunch - so it compiles
# and installs this define-free setup instead (same .iss, payload and RID; only the
# update-run entry differs, and only the release build is meant to carry it).
$verifyDir = Join-Path $env:TEMP ("DeziroJoy-Verify-{0}" -f [guid]::NewGuid().ToString('N'))
$exe = Join-Path $installDir 'DeziroJoy.exe'

function Show-InnoLogTail([string]$LogPath, [string]$What) {
  # The Inno install/uninstall log is the only record of what the setup actually did
  # (destination directory, per-file results, exit path). The beta.1 run deleted both
  # logs unread in its cleanup and failed with nothing but an empty directory
  # listing, so every failure path below prints the log tail before cleaning up.
  if (Test-Path $LogPath) {
    Write-Host "--- $What log (tail): $LogPath ---"
    Get-Content $LogPath -Tail 40
    Write-Host "--- end $What log ---"
  }
  else {
    Write-Host "no $What log was produced at $LogPath"
  }
}

function Invoke-InnoSilent([string]$FilePath, [string[]]$Arguments, [string]$LogPath, [string]$What, [switch]$Diagnose) {
  # Runs an Inno setup or uninstaller silently, waits for the process it started, and
  # decides success from that process's own exit code. On failure the matching log
  # tail is printed; with -Diagnose (the install) the failure is routed through
  # Show-InstallDiagnostics instead, so a failed install also lists every directory
  # the payload could have landed in. That routing matters because the caller's
  # cleanup removes the logs and the throwaway install directory as soon as the
  # error propagates - both pieces of evidence must be printed before then. The
  # uninstall keeps the plain log tail.
  try {
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru
  }
  catch {
    $reason = "$What could not be started: $($_.Exception.Message)"
    if ($Diagnose) { Show-InstallDiagnostics $reason } else { Write-Host $reason; Show-InnoLogTail $LogPath $What }
    throw
  }
  if ($process.ExitCode -ne 0) {
    $reason = "$What failed with exit code $($process.ExitCode)"
    if ($Diagnose) { Show-InstallDiagnostics $reason } else { Show-InnoLogTail $LogPath $What }
    throw $reason
  }
}

function Show-InstallDiagnostics([string]$Reason) {
  # Everything an install failure needs: the install log tail, the throwaway
  # directory, and the per-user default directory the setup falls back to if /DIR
  # were ever ignored, plus the publish payload. Every install failure path - the
  # process not starting, a nonzero exit, and the poll-timeout and missing-key
  # cases below - comes through here, so none of them can fail with only a log
  # tail or only a directory listing.
  Write-Host "--- install diagnostics: $Reason ---"
  Show-InnoLogTail $installLog 'silent install'
  foreach ($dir in @($installDir, (Join-Path $env:LOCALAPPDATA 'Programs\DeziroJoy'), $publish)) {
    if (Test-Path $dir) {
      Write-Host "--- $dir ---"
      Get-ChildItem -Path $dir -ErrorAction SilentlyContinue | Select-Object -First 20 |
        Format-Table Mode, Length, LastWriteTime, Name -AutoSize | Out-String | Write-Host
    }
    else {
      Write-Host "$dir does not exist"
    }
  }
}

# The per-user uninstall registration Inno writes at the end of a successful install
# ($uninstallKeyPaths, resolved from the .iss AppId above). Requiring it here is the
# script's own model of "the install finished": Inno creates the key only once the
# files and shortcuts are in place, so the checks below cannot race a half-finished
# copy, and the same paths are asserted gone after the uninstall. Both registry views
# are checked so the wait does not depend on the bitness of the pwsh running this
# script.
function Test-InstallCompleted {
  (Test-Path $exe) -and (@($uninstallKeyPaths | Where-Object { Test-Path $_ }).Count -gt 0)
}

try {
  # Compile the define-free verification setup here (see $verifyDir above): the release
  # artefact carries the updater relaunch entry and must not be the one installed here.
  New-Item -ItemType Directory -Path $verifyDir -Force | Out-Null
  Invoke-Compile -OutputDir $verifyDir
  $verifySetup = Join-Path $verifyDir $setupExeName

  Invoke-InnoSilent $verifySetup @(
    '/VERYSILENT'
    '/SUPPRESSMSGBOXES'
    '/NORESTART'
    "/DIR=$installDir"
    "/LOG=$installLog"
  ) $installLog 'silent install' -Diagnose

  # Second wait: the install itself, not just the process that launched it. The
  # process wait above covers the documented Inno flow; this covers every way it
  # could return early, and turns a silent "exit 0, nothing installed" into a
  # bounded, diagnosable failure.
  $startedAt = Get-Date
  $deadline = $startedAt.AddSeconds($installTimeoutSeconds)
  $lastReport = $null
  while (-not (Test-InstallCompleted) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds $installPollMilliseconds
    $elapsed = ((Get-Date) - $startedAt).TotalSeconds
    if ($null -eq $lastReport -or ($elapsed - $lastReport) -ge 15) {
      $lastReport = $elapsed
      Write-Host ("waiting for the install to finish... {0:n0}s of {1}s" -f $elapsed, $installTimeoutSeconds)
    }
  }
  if (-not (Test-Path $exe)) {
    Show-InstallDiagnostics "setup exited 0 but no DeziroJoy.exe appeared within $installTimeoutSeconds s"
    throw "DeziroJoy.exe missing after silent install"
  }
  if (-not (@($uninstallKeyPaths | Where-Object { Test-Path $_ }).Count -gt 0)) {
    Show-InstallDiagnostics "DeziroJoy.exe appeared but the uninstall registry key never did within $installTimeoutSeconds s"
    throw "uninstall registry key missing after silent install"
  }
  $productVersion = (Get-Item $exe).VersionInfo.ProductVersion
  Write-Host "installed ProductVersion: $productVersion"
  if ($productVersion -notlike "$Version*") {
    throw "installed version mismatch: $productVersion (expected $Version*)"
  }

  # Per-user Start Menu entry (PrivilegesRequired=lowest, group page disabled).
  $programsRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
  $shortcut = Get-ChildItem -Path $programsRoot -Filter 'DeziroJoy.lnk' -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $shortcut) { throw "no DeziroJoy.lnk found under $programsRoot" }
  Write-Host "start menu shortcut: $($shortcut.FullName)"

  # --- silent uninstall and cleanup -------------------------------------------------
  $uninstaller = Get-ChildItem -Path $installDir -Filter 'unins*.exe' -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $uninstaller) { throw "unins*.exe missing in $installDir" }

  Invoke-InnoSilent $uninstaller.FullName @(
    '/VERYSILENT'
    '/SUPPRESSMSGBOXES'
    '/NORESTART'
    "/LOG=$uninstallLog"
  ) $uninstallLog 'silent uninstall'

  if (Test-Path $installDir) {
    Get-ChildItem -Path $installDir -Recurse -ErrorAction SilentlyContinue | Select-Object -First 20
    throw "install directory still present after uninstall: $installDir"
  }

  $leftoverKeys = @($uninstallKeyPaths | Where-Object { Test-Path $_ })
  if ($leftoverKeys) { throw "uninstall registry key left behind: $($leftoverKeys -join ', ')" }

  $leftoverShortcuts = @()
  $menuLeftover = @(Get-ChildItem -Path $programsRoot -Filter 'DeziroJoy.lnk' -Recurse -ErrorAction SilentlyContinue)
  if ($menuLeftover.Count -gt 0) { $leftoverShortcuts += $menuLeftover.FullName }
  $desktopDir = [Environment]::GetFolderPath('Desktop')
  if ($desktopDir) {
    $desktopLeftover = @(Get-ChildItem -Path $desktopDir -Filter 'DeziroJoy.lnk' -ErrorAction SilentlyContinue)
    if ($desktopLeftover.Count -gt 0) { $leftoverShortcuts += $desktopLeftover.FullName }
  }
  if ($leftoverShortcuts.Count -gt 0) { throw "shortcuts left behind: $($leftoverShortcuts -join ', ')" }

  Write-Host "silent install and uninstall verified for $setupExeName"
}
finally {
  # Every failure path above prints its log tails and directory listings before this
  # runs. The throwaway install directory is removed here as well, so a failed or
  # interrupted run does not leave a half-installed payload or uninstaller behind;
  # after a successful uninstall it is already gone and this is a no-op. The
  # define-free verification setup is dropped the same way.
  Remove-Item -Path $installLog, $uninstallLog -ErrorAction SilentlyContinue
  Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item -Path $verifyDir -Recurse -Force -ErrorAction SilentlyContinue
}
