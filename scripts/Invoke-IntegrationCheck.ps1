#requires -Version 5.1
<#
.SYNOPSIS
  Real end-to-end check for UnrealAssetTextDiff.

.DESCRIPTION
  Runs the built app against a live UnrealEditor-Cmd.exe + project with real
  SVN revisions fetched via svn cat, then verifies:
   - exit code 0
   - base/mine comparison exports exist and are non-empty
   - raw exports retained
   - normalized exports contain no /Temp/Diff/<staged-name> leftovers
   - input hashes unchanged
  This is NOT a unit test - it launches the real engine. Takes ~1-2 min.

.PARAMETER Exe
  Path to UnrealAssetTextDiff.exe (defaults to dist build).

.PARAMETER Project
  Path to the .uproject to load assets with.

.PARAMETER Engine
  Path to UnrealEditor-Cmd.exe.

.PARAMETER AssetPath
  Project-relative path (from WcRoot) of a versioned .uasset to diff two
  revisions of — pick one that actually changed between Rev1 and Rev2.

.EXAMPLE
  .\Invoke-IntegrationCheck.ps1 -Project "D:\MyProject\MyProject.uproject" `
      -Engine "C:\UnrealEngine\UE_5.6\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" `
      -WcRoot "D:\MyProject" -AssetPath "Content/Blueprints/BP_Thing.uasset" `
      -Rev1 100 -Rev2 200
#>
param(
    [string]$Exe      = "$PSScriptRoot\..\dist\UnrealAssetTextDiff.exe",
    [Parameter(Mandatory=$true)][string]$Project,
    [Parameter(Mandatory=$true)][string]$Engine,
    [string]$Tortoise = "C:\Program Files\TortoiseSVN\bin\TortoiseMerge.exe",
    [Parameter(Mandatory=$true)][string]$WcRoot,
    [Parameter(Mandatory=$true)][string]$AssetPath,
    [Parameter(Mandatory=$true)][int]$Rev1,
    [Parameter(Mandatory=$true)][int]$Rev2
)

$ErrorActionPreference = 'Stop'
$fail = 0
function Check($cond, $msg) {
    if ($cond) { Write-Host "  PASS  $msg" -ForegroundColor Green }
    else { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:fail++ }
}

foreach ($p in @($Exe, $Project, $Engine)) {
    if (-not (Test-Path $p)) { Write-Host "missing: $p"; exit 2 }
}

$work = Join-Path $env:TEMP "uatd-itest-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $work | Out-Null
$old = Join-Path $work "old.uasset"
$new = Join-Path $work "new.uasset"

Push-Location $WcRoot
try {
    # Binary-safe download: svn cat writes raw bytes to stdout and cmd's >
    # preserves them. svn cat has NO --output option in SVN 1.14.x (verified
    # via `svn cat --help`), and PowerShell's own > redirection would decode/
    # re-encode and corrupt the binary. cmd /c propagates svn's exit code.
    $svnVer = (svn --version --quiet)
    Write-Host "svn version: $svnVer"
    cmd /c "svn cat -r $Rev1 $AssetPath > `"$old`""
    if ($LASTEXITCODE -ne 0) { throw "svn cat -r $Rev1 failed (exit $LASTEXITCODE)" }
    cmd /c "svn cat -r $Rev2 $AssetPath > `"$new`""
    if ($LASTEXITCODE -ne 0) { throw "svn cat -r $Rev2 failed (exit $LASTEXITCODE)" }
}
finally { Pop-Location }

# Byte sanity: package files start with the little-endian uint32 magic 0x9E2A83C1.
function Test-UAssetMagic($p) {
    $fs = [System.IO.File]::OpenRead($p)
    try { $b = New-Object byte[] 4; [void]$fs.Read($b, 0, 4)
          return ($b[0] -eq 0xC1 -and $b[1] -eq 0x83 -and $b[2] -eq 0x2A -and $b[3] -eq 0x9E) }
    finally { $fs.Dispose() }
}
Check ((Test-Path $old) -and (Get-Item $old).Length -gt 0) "r$Rev1 download non-empty"
Check (Test-UAssetMagic $old) "r$Rev1 download has uasset magic (binary intact)"
Check ((Test-Path $new) -and (Get-Item $new).Length -gt 0) "r$Rev2 download non-empty"
Check (Test-UAssetMagic $new) "r$Rev2 download has uasset magic (binary intact)"

$h1 = (Get-FileHash $old -Algorithm SHA256).Hash
$h2 = (Get-FileHash $new -Algorithm SHA256).Hash
$before = Get-Date

& $Exe --project $Project --engine $Engine --tortoise $Tortoise `
    --base $old --mine $new --base-label "r$Rev1" --mine-label "r$Rev2" `
    --export-only | Out-Host
$code = $LASTEXITCODE
Check ($code -eq 0) "exit code 0 (got $code)"

$root = Join-Path $env:LOCALAPPDATA "UnrealAssetTextDiff\Sessions"
$session = Get-ChildItem $root -Directory | Where-Object { $_.CreationTime -gt $before } |
           Sort-Object CreationTime | Select-Object -Last 1
Check ($null -ne $session) "session directory created"

if ($session) {
    $exp = Join-Path $session.FullName 'export'
    $base = Join-Path $exp 'base.t3d'; $mine = Join-Path $exp 'mine.t3d'
    Check ((Test-Path $base) -and (Test-Path $mine)) "comparison exports exist"
    Check ((Get-Item $base).Length -gt 0 -and (Get-Item $mine).Length -gt 0) "exports non-empty"
    Check ((Test-Path "$base".Replace('base.t3d','base.raw.t3d'))) "raw exports retained"
    Check (-not (Select-String -Path $base -Pattern '/Temp/Diff/[A-Za-z0-9_]+__base__' -Quiet)) "staged name normalized"
    $d = Compare-Object (Get-Content $base) (Get-Content $mine)
    Check ($null -ne $d) "real differences present in exports"
}

Check ((Get-FileHash $old -Algorithm SHA256).Hash -eq $h1) "base input unchanged"
Check ((Get-FileHash $new -Algorithm SHA256).Hash -eq $h2) "mine input unchanged"

Remove-Item $work -Recurse -Force
if ($fail -eq 0) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$fail check(s) failed" -ForegroundColor Red; exit 1
