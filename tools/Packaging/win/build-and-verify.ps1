<#
  Compiles the DeziroJoy Windows installer with Inno Setup and verifies the result.

  Usage (Release workflow, one call per Windows RID):
    pwsh tools/Packaging/win/build-and-verify.ps1 `
      -Version 0.0.1-beta.1-preview -Rid win-x64 -PublishDir publish/win-x64

  Every path is resolved from the script's own location ($PSScriptRoot), never from
  the caller's working directory, so the invocation works from anywhere.

  What is verified here:
    * ISCC is resolved from PATH or a standard install directory, with a clear error
      listing everything that was tried when it is missing.
    * The publish payload (DeziroJoy.exe) carries the machine type the RID implies
      (x64 / ARM64), and the .iss maps the RID to the matching ArchitecturesAllowed /
      ArchitecturesInstallIn64BitMode: those two decide where a setup may install.
    * The compiled setup.exe is the x86 bootstrap Inno Setup always emits, whatever
      the payload architecture is.
    * For win-x64, the setup is silently installed into a throwaway directory, the
      installed DeziroJoy.exe and its version are asserted, and it is then silently
      uninstalled with the install directory, the uninstall registry key and the
      shortcuts asserted gone.

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

# --- resolve ISCC -----------------------------------------------------------------
# windows-latest ships Inno Setup 6 on PATH. Fall back to the standard install
# directories so the step also works on a runner image without the PATH entry.
$iscc = $null
$onPath = Get-Command ISCC -ErrorAction SilentlyContinue
if ($onPath) {
  $iscc = $onPath.Source
}
else {
  $candidates = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
  ) | Where-Object { Test-Path $_ }
  if ($candidates) { $iscc = $candidates[0] }
}
if (-not $iscc) {
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
Write-Host "ISCC: $iscc"

# --- compile ----------------------------------------------------------------------
& $iscc $iss `
  "/DDeziroJoyVersion=$Version" `
  "/DDeziroJoyRid=$Rid" `
  "/DDeziroJoyPublishDir=$publish" `
  "/DDeziroJoyIconFile=$icon" `
  "/DDeziroJoyOutputDir=$repoRoot"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

if (-not (Test-Path $setupExePath)) { throw "ISCC reported success but $setupExeName is missing from $repoRoot" }
Write-Host "built $setupExeName ($((Get-Item $setupExePath).Length) bytes)"

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
$installDir = Join-Path $env:TEMP ("DeziroJoy-Install-{0}" -f [guid]::NewGuid().ToString('N'))
$installLog = Join-Path $env:TEMP ("DeziroJoy-Install-{0}.log" -f [guid]::NewGuid().ToString('N'))
$uninstallLog = Join-Path $env:TEMP ("DeziroJoy-Uninstall-{0}.log" -f [guid]::NewGuid().ToString('N'))

try {
  & $setupExePath /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "/DIR=$installDir" "/LOG=$installLog"
  if ($LASTEXITCODE -ne 0) {
    if (Test-Path $installLog) { Get-Content $installLog -Tail 40 }
    throw "silent install failed with exit code $LASTEXITCODE"
  }

  $exe = Join-Path $installDir 'DeziroJoy.exe'
  if (-not (Test-Path $exe)) {
    Get-ChildItem -Path $installDir -ErrorAction SilentlyContinue | Select-Object -First 20
    throw "DeziroJoy.exe missing after silent install"
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

  & $uninstaller.FullName /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "/LOG=$uninstallLog"
  if ($LASTEXITCODE -ne 0) {
    if (Test-Path $uninstallLog) { Get-Content $uninstallLog -Tail 40 }
    throw "silent uninstall failed with exit code $LASTEXITCODE"
  }

  if (Test-Path $installDir) {
    Get-ChildItem -Path $installDir -Recurse -ErrorAction SilentlyContinue | Select-Object -First 20
    throw "install directory still present after uninstall: $installDir"
  }

  $leftoverKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E565E61E-24C0-4A2C-9DFF-4B8D02DD7382}_is1',
    'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{E565E61E-24C0-4A2C-9DFF-4B8D02DD7382}_is1'
  ) | Where-Object { Test-Path $_ }
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
  Remove-Item -Path $installLog, $uninstallLog -ErrorAction SilentlyContinue
}
