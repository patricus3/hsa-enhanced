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

# Fetches the Hearthstone Access release (hearthstoneaccess.com) and its source diff (DevTools
# on GitHub) into downloads\. Nothing of HSA is kept in this repository; the build takes what it
# needs from these two files on the player's PC. Returns $true when a new pair was stored (a rebuild
# is due; downloads\rebuild_pending is set). A download that fails or a pair whose versions do not
# match yet keeps the last good pair; with none at all it throws.
function Update-Hsa {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $dl = Join-Path $Data 'downloads'
    New-Item -ItemType Directory -Force $dl | Out-Null
    $zip = Join-Path $dl 'hsa.zip'; $diff = Join-Path $dl 'hsa.diff.patch'
    $havePair = (Test-Path $zip) -and (Test-Path $diff)
    try {
        Invoke-WebRequest -UseBasicParsing 'https://hearthstoneaccess.com/files/pre_patch.zip' -OutFile "$zip.new"
        Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/antonshusharin/DevTools/master/diff.patch' -OutFile "$diff.new"
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead("$zip.new")
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -eq 'patch/Accessibility/hsa_manifest.json' } | Select-Object -First 1
            $reader = New-Object IO.StreamReader($entry.Open())
            $zipver = [string](($reader.ReadToEnd() | ConvertFrom-Json).accessibility_version)
            $reader.Close()
        } finally { $archive.Dispose() }
        $gitver = (Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/antonshusharin/DevTools/master/hsa_version').Content.Trim()
        Log "HSA version in the release: $zipver, in the repository: $gitver"
        # right after an HSA update one of the two may lag behind: wait for both
        if ($zipver -ne $gitver) { throw 'the release and its source diff do not match yet' }
        $sha = (Get-FileHash -Algorithm SHA256 "$zip.new").Hash + (Get-FileHash -Algorithm SHA256 "$diff.new").Hash
        $shaFile = Join-Path $dl 'hsa.sha256'
        if ($havePair -and (Test-Path $shaFile) -and (Get-Content $shaFile -Raw).Trim() -eq $sha) {
            Remove-Item "$zip.new", "$diff.new" -Force; Log "HSA is up to date ($zipver)"; return $false
        }
        Move-Item -Force "$zip.new" $zip; Move-Item -Force "$diff.new" $diff
        Set-Content $shaFile $sha
        Set-Content (Join-Path $dl 'rebuild_pending') $zipver
        Log "HSA $zipver downloaded"
        return $true
    } catch {
        Remove-Item "$zip.new", "$diff.new" -Force -ErrorAction SilentlyContinue
        if (-not $havePair) { throw "Could not get a matching Hearthstone Access release and source diff ($($_.Exception.Message)). Try again later." }
        Log "HSA update: $($_.Exception.Message); keeping the release already downloaded"
        return $false
    }
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
