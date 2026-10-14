<#
.SYNOPSIS
    Builds Sticky Notes as a self-contained StickyNotes.exe and, optionally, an Inno Setup installer.
.EXAMPLE
    .\build.ps1                            # artifacts\app\StickyNotes.exe
.EXAMPLE
    .\build.ps1 -Installer -Version 1.2.0  # also artifacts\installer\StickyNotes-Setup-1.2.0.exe
#>
[CmdletBinding()]
param(
    [string]$Version = '1.1.1',
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appDir = Join-Path $root 'artifacts\app'
$installerDir = Join-Path $root 'artifacts\installer'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.2.3 (got '$Version')."
}

if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }

dotnet publish (Join-Path $root 'src\StickyNotes\StickyNotes.csproj') `
    -c Release -r win-x64 --self-contained true -o $appDir `
    -p:UseAppHost=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

$exe = Join-Path $appDir 'StickyNotes.exe'
if (-not (Test-Path $exe)) {
    throw "StickyNotes.exe wasn't produced. If antivirus blocked it, build in GitHub Actions instead."
}
Write-Host "App: $exe"

if (-not $Installer) { return }

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup'
}

& $iscc "/DAppVersion=$Version" "/DAppSourceDir=$appDir" "/DOutputDir=$installerDir" `
    (Join-Path $root 'installer\StickyNotes.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }

Write-Host "Installer: $(Join-Path $installerDir "StickyNotes-Setup-$Version.exe")"
