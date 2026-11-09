<#
  Compiles the DeziroJoy Windows installer with Inno Setup and verifies the result.

  Usage (Release workflow, one call per Windows RID):
    pwsh tools/Packaging/win/build-and-verify.ps1 `
      -Version 0.0.1-beta.1-preview -Rid win-x64 -PublishDir publish/win-x64

  What is verified here:
    * ISCC is resolved from PATH or a standard install directory, with a clear error
      listing everything that was tried when it is missing.
    * The compiled setup.exe has the machine type the RID implies (x64 / ARM64).
    * For win-x64, the setup is silently installed into a throwaway directory, the
      installed DeziroJoy.exe and its version are asserted, and it is then silently
      uninstalled with the install directory, the uninstall registry key and the
      shortcuts asserted gone.

  A win-arm64 setup.exe cannot execute on the x64 windows-latest image, so for that
  RID the runtime install/uninstall cycle is reported as skipped: the compile and the
  static architecture check are all a cross-architecture artefact can prove here. The
  arm64 cycle needs an ARM64 runner (windows-11-arm).
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

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$iss = Join-Path $repoRoot 'tools/Packaging/win/DeziroJoy.iss'
$icon = Join-Path $repoRoot 'src/DeziroJoy.App/Assets/dezirojoy.ico'
$setupExeName = "DeziroJoy-$Version-$Rid-setup.exe"
$setupExePath = Join-Path $repoRoot $setupExeName

if (-not (Test-Path $PublishDir)) {
  $fallback = Join-Path $repoRoot $PublishDir
  if (Test-Path $fallback) { $PublishDir = $fallback }
}
$publish = (Resolve-Path $PublishDir).Path

foreach ($required in @($iss, $icon)) {
  if (-not (Test-Path $required)) { throw "required file missing: $required" }
}
if (-not (Test-Path (Join-Path $publish 'DeziroJoy.exe'))) {
  throw "DeziroJoy.exe missing from publish output: $publish"
}

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

# --- static architecture check ------------------------------------------------------
$machine = Get-PeMachineType $setupExePath
$expectedMachine = if ($Rid -eq 'win-x64') { 0x8664 } else { 0xAA64 }
if ($machine -ne $expectedMachine) {
  throw "unexpected setup.exe machine type 0x{0:X4} for $Rid, expected 0x{1:X4}" -f $machine, $expectedMachine
}
Write-Host "setup.exe machine type: 0x{0:X4} (as expected for $Rid)" -f $machine

if ($Rid -ne 'win-x64') {
  Write-Host "ARM64 setup cannot execute on this x64 runner: silent install/uninstall smoke skipped."
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
