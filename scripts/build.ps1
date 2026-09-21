# Builds KAM Capture Tool end to end: icon, compile, self-test, single-file publish.
#
#   ./scripts/build.ps1                 build and publish to dist/
#   ./scripts/build.ps1 -Install        ... and install it for the current user
#   ./scripts/build.ps1 -DocShots       ... and regenerate docs/images
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$DocShots,
    [switch]$SkipIcon,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root 'src\KamCapture\KamCapture.csproj'
$dist = Join-Path $root 'dist\app'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not $SkipIcon) {
    Step 'Icon'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\make-icon.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'icon generation failed' }
}

Step "Build ($Configuration)"
dotnet build $proj -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Step 'Self-test'
$exe = Join-Path $root "src\KamCapture\bin\$Configuration\net9.0-windows10.0.19041.0\KamCapture.exe"
$shot = Join-Path ([System.IO.Path]::GetTempPath()) 'kam-selftest.png'
& $exe "--selftest=$shot"
if ($LASTEXITCODE -ne 0) { throw 'self-test failed' }

if ($DocShots) {
    Step 'Documentation screenshots'
    & $exe "--docshots=$(Join-Path $root 'docs\images')"
    if ($LASTEXITCODE -ne 0) { throw 'doc shots failed' }
}

Step 'Publish'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $dist --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$published = Join-Path $dist 'KamCapture.exe'
$size = [math]::Round((Get-Item $published).Length / 1MB, 1)
Write-Host "`n    $published  ($size MB, no runtime required)" -ForegroundColor Green

if ($Install) {
    Step 'Install'
    & $published --install-silent
    Start-Sleep -Seconds 2
    $target = (Get-ItemProperty 'HKCU:\Software\KAM\Capture Tool' -ErrorAction SilentlyContinue).InstallPath
    if ($target) {
        Write-Host "    installed to $target" -ForegroundColor Green
    } else {
        Write-Warning 'install did not register; check the log in %APPDATA%\KAM Capture Tool'
    }
}

Write-Host "`nDone." -ForegroundColor Green
