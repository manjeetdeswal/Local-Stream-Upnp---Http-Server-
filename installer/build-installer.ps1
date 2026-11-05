# Builds the Windows installer: installer\output\LocalStream-Setup-<version>.exe
#
#   1. Install Inno Setup 6 once: https://jrsoftware.org/isdl.php  (or: winget install JRSoftware.InnoSetup)
#   2. Run from this folder:  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#
# To publish an update: upload the .exe to a new GitHub release (tag like v1.2.1). The app finds
# it and offers to install it with one click.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = Resolve-Path '..\LocalStreamPC\LocalStreamPC.csproj'
[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in LocalStreamPC.csproj' }
Write-Host "Building LocalStream $version" -ForegroundColor Cyan

# Self-contained: works on PCs without .NET installed. Not single-file, so libvlc and ffmpeg sit beside the exe.
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if (-not (Test-Path 'publish\ffmpeg.exe')) { Write-Warning 'ffmpeg.exe is missing from the build: video thumbnails and live conversion will be off.' }
if (-not (Test-Path 'publish\libvlc')) { Write-Warning 'libvlc folder is missing: the built-in cast player will fall back to an installed VLC.' }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php' }

& $iscc "/DAppVersion=$version" 'LocalStream.iss'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
Write-Host "Done: $PSScriptRoot\output\LocalStream-Setup-$version.exe" -ForegroundColor Green
