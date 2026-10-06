# Shared by install.ps1, rebuild.ps1 and uninstall.ps1 (dot-sourced; $Data is set by the caller).
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Log([string]$text) {
    if ($text -eq $global:HsaLastLog) { return }   # an error logged by rebuild.ps1 and again by install.ps1
    $global:HsaLastLog = $text
    Write-Host $text
    try {
        $dir = Join-Path $Data 'logs'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Add-Content -Path (Join-Path $dir 'rebuild.log') -Value "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $text"
    } catch { }
}

# Hearthstone's folder: HSA_GAME_DIR, then Battle.net's uninstall entry, then the default
function Find-Game {
    $candidates = @()
    if ($env:HSA_GAME_DIR) { $candidates += $env:HSA_GAME_DIR }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone',
                     'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone') {
        $loc = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
        if ($loc) { $candidates += $loc }
    }
    $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Hearthstone'), (Join-Path $env:ProgramFiles 'Hearthstone')
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'Hearthstone_Data\Managed\Assembly-CSharp.dll'))) { return (Resolve-Path $c).Path }
    }
    throw 'Hearthstone was not found. Install it with Battle.net, or set HSA_GAME_DIR to its folder.'
}

# True for an Assembly-CSharp that already carries Hearthstone Access
function Test-HsaAssembly([string]$path) {
    $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($path))
    return $text.Contains('AccessibilityMgr')
}

# A .NET SDK 8 or newer; installed into ProgramData when missing
function Use-Dotnet {
    $local = Join-Path $Data 'dotnet'
    if (Test-Path (Join-Path $local 'dotnet.exe')) { $env:PATH = "$local;$env:PATH"; $env:DOTNET_ROOT = $local }
    $sdks = @()
    try { $sdks = & dotnet --list-sdks 2>$null } catch { }
    foreach ($s in $sdks) { if ([int](($s -split '\.')[0]) -ge 8) { return } }
    Log 'Installing the .NET 8 SDK from Microsoft (about 200 MB, this takes a while)'
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -InstallDir $local | Out-Null
    if (-not (Test-Path (Join-Path $local 'dotnet.exe'))) { throw '.NET installation failed.' }
    $env:PATH = "$local;$env:PATH"; $env:DOTNET_ROOT = $local
}

function Build([string]$project) {
    & dotnet build -c Release -v q $project | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "building $project failed" }
}

# Tolk and the screen reader client libraries of the official HSA: speech now goes through Prism
function Remove-OldSpeech([string]$managed) {
    Remove-Item (Join-Path $managed 'TolkDotNet.dll') -Force -ErrorAction SilentlyContinue
    foreach ($f in 'Tolk.dll', 'nvdaControllerClient64.dll', 'nvdaControllerClient32.dll', 'SAAPI64.dll', 'SAAPI32.dll', 'dolapi32.dll', 'dolapi64.dll') {
        Remove-Item (Join-Path $managed "Accessibility\$f") -Force -ErrorAction SilentlyContinue
    }
}
