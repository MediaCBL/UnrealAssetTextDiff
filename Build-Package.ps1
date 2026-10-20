#requires -Version 5.1
<#
.SYNOPSIS
  Builds the portable single-file UnrealAssetTextDiff.exe into .\dist\.

.DESCRIPTION
  Publishes a self-contained win-x64 single file with all native WPF
  dependencies bundled (no sibling DLLs required next to the exe).

  Requires the .NET SDK 8.0 or newer (the reference build used 9.x; any
  SDK that supports net8.0-windows works). No NuGet packages are needed
  for the app itself. Building the unit tests additionally restores
  xUnit from nuget.org.
#>
param(
    [string]$OutDir = "$PSScriptRoot\dist",
    [switch]$RunTests
)

$ErrorActionPreference = 'Stop'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "dotnet SDK not found. Install .NET SDK 8.0+ from https://dotnet.microsoft.com/download"
}

# dotnet resolves global.json from the CURRENT directory upward. Run from
# the package root so its global.json (8.0+ any major) applies, shielding
# the build from any parent-directory global.json pinning a missing SDK.
Push-Location $PSScriptRoot
try {
Write-Host "dotnet SDK: $(dotnet --version)"

if ($RunTests) {
    Write-Host "Running unit tests..."
    dotnet test "$PSScriptRoot\tests\UnrealAssetTextDiff.Tests\UnrealAssetTextDiff.Tests.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed (exit $LASTEXITCODE)" }
}

Write-Host "Publishing self-contained single-file exe..."
dotnet publish "$PSScriptRoot\src\UnrealAssetTextDiff\UnrealAssetTextDiff.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $OutDir 'UnrealAssetTextDiff.exe'
if (-not (Test-Path $exe)) { throw "publish produced no exe at $exe" }
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "OK -> $exe ($mb MB, standalone - no other files needed)"
}
finally { Pop-Location }
