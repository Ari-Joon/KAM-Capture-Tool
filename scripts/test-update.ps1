<#
    The whole update, end to end, beside the real install and never touching it.

    Builds this version and a pretend newer one, installs the older into a
    sandbox, runs it hidden, and points it at a local copy of a GitHub release
    feed. Then it asks the running copy to update twice: once with a checksum
    that does not match, which must be refused with nothing changed, and once
    for real, which must end with the newer version installed where the old one
    was, running, and the old one gone.

    KAM_CAPTURE_SANDBOX keeps every copy started here in its own folder,
    registry key and single-instance lock. KAM_CAPTURE_UPDATE_FEED is only
    read inside a sandbox.

        ./scripts/test-update.ps1
#>
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root 'src\KamCapture\KamCapture.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ('kam-update-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$sandbox = Join-Path $work 'sandbox'
$failures = 0

function Expect([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

function Wait-Until([scriptblock]$condition, [int]$seconds = 60) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Read-Log { if (Test-Path $script:log) { Get-Content $script:log -Raw } else { '' } }

# A fingerprint of the real install, to prove at the end that nothing here touched it.
function Get-RealState {
    $app = Get-ItemProperty 'HKCU:\Software\KAM\Capture Tool' -ErrorAction SilentlyContinue
    $run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).'KAM Capture Tool'
    $exe = if ($app) { Join-Path $app.InstallPath 'KamCapture.exe' } else { '' }
    $stamp = if ($exe -and (Test-Path $exe)) { (Get-Item $exe).LastWriteTimeUtc.Ticks } else { '-' }
    $procs = (Get-CimInstance Win32_Process -Filter "Name='KamCapture.exe'" |
              Where-Object { $_.ExecutablePath -notlike "$work*" } | ForEach-Object { $_.ProcessId }) -join ','
    "$($app.InstallPath)|$($app.Version)|$run|$stamp|$procs"
}

function Publish([string]$out, [string]$version) {
    $args = @('publish', $proj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
              '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false',
              '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $out, '--nologo', '-v', 'quiet')
    if ($version) { $args += @("-p:Version=$version", "-p:FileVersion=$version.0", "-p:InformationalVersion=$version") }
    & dotnet @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($out)" }
    Join-Path $out 'KamCapture.exe'
}

function Write-Feed([string]$exe, [string]$version, [string]$sha) {
    $feed = [ordered]@{
        tag_name   = "v$version"
        name       = "KAM Capture Tool $version - end-to-end check"
        html_url   = 'https://github.com/Ari-Joon/KAM-Capture-Tool/releases'
        draft      = $false
        prerelease = $false
        assets     = @([ordered]@{
            name                 = 'KamCapture.exe'
            size                 = (Get-Item $exe).Length
            digest               = "sha256:$sha"
            browser_download_url = ([Uri]$exe).AbsoluteUri
        })
    }
    $feed | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $script:feedPath
}

$before = Get-RealState
$running = @()

try {
    Write-Host 'building the installed version and a newer one'
    [xml]$csproj = Get-Content $proj
    $oldVersion = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    $parts = $oldVersion.Split('.')
    $newVersion = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 50)"
    $oldExe = Publish (Join-Path $work 'old') $null
    $newExe = Publish (Join-Path $work 'new') $newVersion
    $newSha = (Get-FileHash $newExe -Algorithm SHA256).Hash
    Write-Host "  $oldVersion -> $newVersion"

    New-Item -ItemType Directory -Force $sandbox | Out-Null
    $env:KAM_CAPTURE_SANDBOX = $sandbox
    $script:feedPath = Join-Path $work 'feed.json'
    $env:KAM_CAPTURE_UPDATE_FEED = $script:feedPath
    $script:log = Join-Path $sandbox 'AppData\kam-capture.log'

    $bytes = [Text.Encoding]::UTF8.GetBytes(([IO.Path]::GetFullPath($sandbox)).ToLowerInvariant())
    $id = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-', '').Substring(0, 8)
    $regRoot = "HKCU:\Software\KAM\Capture Tool (sandbox $id)"

    Write-Host 'install the older version into the sandbox'
    Start-Process $oldExe -ArgumentList '--install-silent' -Wait
    $installDir = (Get-ItemProperty "$regRoot\App" -ErrorAction SilentlyContinue).InstallPath
    $installed = Join-Path "$installDir" 'KamCapture.exe'
    Expect ($installDir -like "$sandbox*") 'it installs inside the sandbox'
    Expect ((Get-Item $installed).VersionInfo.ProductVersion -like "$oldVersion*") "the installed copy is $oldVersion"

    Write-Host 'run it, hidden, against a feed announcing the newer version'
    Write-Feed $newExe $newVersion ('0' * 64)
    $old = Start-Process $installed -ArgumentList '--tray', '--no-tray' -PassThru
    $running += $old.Id
    Expect (Wait-Until { (Read-Log) -match "Update check: latest is $([regex]::Escape($newVersion))" } 30) 'its own check finds the newer version'

    Write-Host 'update with a checksum that does not match'
    Start-Process $installed -ArgumentList '--update' -Wait
    Expect (Wait-Until { (Read-Log) -match 'did not match the checksum' } 60) 'the download is refused'
    Start-Sleep -Seconds 1
    Expect (-not $old.HasExited) 'the running copy carries on'
    Expect ((Get-ItemProperty "$regRoot\App").Version -eq $oldVersion) 'nothing was installed'
    Expect (@(Get-ChildItem (Join-Path $sandbox 'Updates') -File -ErrorAction SilentlyContinue).Count -eq 0) 'nothing was left in the downloads folder'

    Write-Host 'update for real'
    Write-Feed $newExe $newVersion $newSha
    Start-Process $installed -ArgumentList '--update' -Wait
    $done = Wait-Until { (Get-ItemProperty "$regRoot\App" -ErrorAction SilentlyContinue).Version -eq $newVersion } 90
    Expect $done "the install now says $newVersion"
    Expect (Wait-Until { $old.HasExited } 30) 'the old copy closed'
    Expect (Wait-Until { (Read-Log) -match "Now running $([regex]::Escape($newVersion)), updated from $([regex]::Escape($oldVersion))" } 30) 'the new copy started and knows what it replaced'
    $new = Get-CimInstance Win32_Process -Filter "Name='KamCapture.exe'" | Where-Object { $_.ExecutablePath -eq $installed }
    if ($new) { $running += $new.ProcessId }
    Expect ([bool]$new) 'it runs from the same install folder'
    Expect ((Get-Item $installed).VersionInfo.ProductVersion -like "$newVersion*") "the program in that folder is $newVersion"
    Expect ((Test-Path (Join-Path $sandbox 'Desktop\KAM Capture Tool.lnk')) -and
            (Test-Path (Join-Path $sandbox 'Start Menu\KAM Capture Tool.lnk'))) 'its shortcuts are still there'
}
catch {
    Expect $false $_.Exception.Message
}
finally {
    # Close whatever this started, through the tool's own exit where possible.
    if ($installed -and (Test-Path $installed)) {
        try { Start-Process $installed -ArgumentList '--exit' -Wait } catch { }
    }
    foreach ($p in $running) { Stop-Process -Id $p -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -Filter "Name='KamCapture.exe'" |
        Where-Object { $_.ExecutablePath -like "$work*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1

    Remove-Item Env:KAM_CAPTURE_SANDBOX -ErrorAction SilentlyContinue
    Remove-Item Env:KAM_CAPTURE_UPDATE_FEED -ErrorAction SilentlyContinue
    if ($regRoot) { Remove-Item $regRoot -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Expect ((Get-RealState) -eq $before) 'the real install, its settings and any running copy were not touched'

if ($failures -gt 0) { Write-Host "update end-to-end FAILED: $failures expectation(s) not met" -ForegroundColor Red; exit 1 }
Write-Host 'update end-to-end OK' -ForegroundColor Green
