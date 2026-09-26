# Rebuilds Hearthstone Access for Windows against the installed game and installs it.
#   rebuild.ps1 [-Zip hsa.zip] [-Auto]
# -Auto (scheduled task): only acts when a game update replaced the mod, and never while the game runs.
# Needs administrator rights (the game lives in Program Files). Windows counterpart of ../rebuild.sh.
param([string]$Zip, [switch]$Auto)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Src  = Split-Path -Parent $PSScriptRoot          # the Resources folder
$Data = if ($env:HSA_DATA_DIR) { $env:HSA_DATA_DIR } else { Join-Path $env:ProgramData 'HearthstoneAccess' }
$Work = Join-Path $Data 'work'
$Backup = Join-Path $Data 'vanilla'
. (Join-Path $PSScriptRoot 'common.ps1')
trap { Log "ERROR: $($_.Exception.Message)"; break }   # logged, then passed on (install shows it, the task records it)

$Game = Find-Game
$Managed = Join-Path $Game 'Hearthstone_Data\Managed'
$GameAsm = Join-Path $Managed 'Assembly-CSharp.dll'
$InstalledHash = Join-Path $Data 'installed.sha256'
if (-not $Zip) { $Zip = Join-Path $Data 'downloads\hsa.zip' }
$Diff = [IO.Path]::ChangeExtension($Zip, '.diff.patch')

function Hash($p) { (Get-FileHash -Algorithm SHA256 $p).Hash.ToLowerInvariant() }
function Read-Text($p) { if (Test-Path $p) { (Get-Content $p -Raw).Trim() } else { '' } }

# --- a new HSA release (the task looks once a day) -----------------------------------------
$pending = Join-Path $Data 'downloads\rebuild_pending'
$lastCheck = Join-Path $Data 'downloads\last_update_check'
if ($Auto -and (-not (Test-Path $lastCheck) -or (Get-Item $lastCheck).LastWriteTime -lt (Get-Date).AddDays(-1))) {
    Set-Content $lastCheck (Get-Date -Format o)
    try { Update-Hsa | Out-Null } catch { Log "HSA update check failed: $($_.Exception.Message)" }
}

# --- which Assembly-CSharp is the game's own? ---------------------------------------------
$current = Hash $GameAsm
if ($current -eq (Read-Text $InstalledHash)) {
    if ($Auto -and -not (Test-Path $pending)) { exit 0 }    # our build is in place and HSA has nothing new
    if ($Auto -and (Get-Process Hearthstone -ErrorAction SilentlyContinue)) { exit 0 }
    $vanilla = Join-Path $Backup 'Assembly-CSharp.dll'      # rebuild from the saved original
    if (-not (Test-Path $vanilla)) { throw "The original Assembly-CSharp.dll backup is missing. Repair the game in Battle.net (Options > Scan and Repair) and run the installer again." }
} else {
    if (Test-HsaAssembly $GameAsm) { throw "The game has the official Hearthstone Access (or another mod) installed. Repair the game in Battle.net (Options > Scan and Repair) and run the installer again." }
    if ($Auto -and (Get-Process Hearthstone -ErrorAction SilentlyContinue)) { exit 0 }
    Log "game Assembly-CSharp.dll is new ($current), building the mod for it"
    New-Item -ItemType Directory -Force $Backup | Out-Null
    Copy-Item $GameAsm (Join-Path $Backup 'Assembly-CSharp.dll') -Force
    $vanilla = Join-Path $Backup 'Assembly-CSharp.dll'
}
if (Get-Process Hearthstone -ErrorAction SilentlyContinue) { throw 'Hearthstone is running. Quit the game and try again.' }
if (-not (Test-Path $Zip) -or -not (Test-Path $Diff)) { throw "Missing $Zip or $Diff. Run the installer again." }
Log "HSA zip: $Zip"

# --- tools --------------------------------------------------------------------------------
Use-Dotnet
Log '== build tools'
Build (Join-Path $Src 'tools\port')
Build (Join-Path $Src 'compat')
Build (Join-Path $PSScriptRoot 'speech')
$PortDll = Join-Path $Src 'tools\port\bin\Release\net8.0\port.dll'
$Compat  = Join-Path $Src 'compat\bin\Release\net472\HSACompat.dll'
$Speech  = Join-Path $PSScriptRoot 'speech\bin\Release\net472\HSAPrism.dll'
# a native command with all its output in $log; stderr lines must not stop the script (PowerShell 5.1)
function Invoke-Logged([string]$log, [scriptblock]$command) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $command *> $log } finally { $ErrorActionPreference = $old }
    return $LASTEXITCODE
}
function Port { & dotnet $PortDll @args; if ($LASTEXITCODE -ne 0) { throw "port $($args[0]) failed" } }
function Unresolved { param([string[]]$a) $o = & dotnet $PortDll check @a; $o | Out-File -Encoding utf8 (Join-Path $Work 'check.log'); [int](($o | Select-Object -Last 1) -split ' ')[1] }

Log '== unpack HSA'
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
New-Item -ItemType Directory -Force (Join-Path $Work 'hsa'), (Join-Path $Work 'out') | Out-Null
Expand-Archive -Path $Zip -DestinationPath (Join-Path $Work 'hsa') -Force
$W = Join-Path $Work 'hsa\patch\Hearthstone_Data\Managed\Assembly-CSharp.dll'
Copy-Item $vanilla (Join-Path $Work 'vanilla-Assembly-CSharp.dll')
Copy-Item $Diff (Join-Path $Work 'diff.patch')
$V = Join-Path $Work 'vanilla-Assembly-CSharp.dll'
$Out = Join-Path $Work 'out\Assembly-CSharp.dll'

Log "== map HSA's source diff onto the IL"
Push-Location $Work
try {
    Port hunks diff.patch hunks.json
    Port match $W $V hunks.json
    Port seeds hunk_matched_detail.txt hunk_edit_seeds.txt
    Log '== transplant into the game Assembly-CSharp'
    $code = Invoke-Logged (Join-Path $Work 'port.log') { & dotnet $PortDll port $W $V $Out $Managed --windows }
    if ($code -ne 0) { Get-Content port.log -Tail 20; throw 'port failed' }
    Get-Content port.log | Select-String 'enum constants|existing types|closure|PROBLEM|missing external' | ForEach-Object { $_.Line }
} finally { Pop-Location }

Log '== speech through Prism (no Tolk)'
Port speech $Out $Managed

Log '== check references against the game assemblies'
$base = Unresolved @($V, $Managed)
$now  = Unresolved @($Out, $Managed, (Split-Path $Compat), (Split-Path $Speech))
Log "unresolved: vanilla $base, ported $now"
if ($now -gt $base) { throw "the ported assembly has unresolved references, see $Work\check.log" }

Log "== enhancements: Black Market, menus built from what the game shows (enhanced\)"
# optional: if this fails (e.g. a game update renamed something) the plain HSA port is installed
$Enh = $null
$E = Join-Path $Work 'enh'
New-Item -ItemType Directory -Force (Join-Path $E 'check') | Out-Null
$EAsm = Join-Path $E 'Assembly-CSharp.dll'
Copy-Item $Out $EAsm
try {
    Port expose $EAsm $Managed
    $code = Invoke-Logged (Join-Path $Work 'enhanced-build.log') { & dotnet build -c Release -v q (Join-Path $Src 'enhanced') "-p:GameManaged=$Managed" "-p:HsaAssembly=$EAsm" -o (Join-Path $E 'bin') }
    if ($code -ne 0) { throw 'build failed' }
    $EDll = Join-Path $E 'bin\HSAEnhanced.dll'
    $menusFile = Join-Path $Data 'menus.txt'
    if ((Test-Path $menusFile) -and (Get-Content $menusFile -Raw).Trim() -eq 'hsa') { Port hook $EAsm $EDll $EAsm $Managed --hsa-menus }
    else { Port hook $EAsm $EDll $EAsm $Managed }
    Copy-Item $EAsm, $EDll (Join-Path $E 'check') -Force
    # the new build first: the game folder still holds the previous HSAEnhanced.dll
    $enow = Unresolved @((Join-Path $E 'check\Assembly-CSharp.dll'), (Join-Path $E 'check'), $Managed, (Split-Path $Compat), (Split-Path $Speech))
    $eadd = Unresolved @((Join-Path $E 'check\HSAEnhanced.dll'), (Join-Path $E 'check'), $Managed)
    if ($enow -le $base -and $eadd -eq 0) { $Enh = $EDll; Copy-Item $EAsm $Out -Force }
    else { Log "WARNING: enhancements have unresolved references, installing plain Hearthstone Access" }
} catch { Log "WARNING: enhancements could not be built ($($_.Exception.Message), see $Work\enhanced-build.log), installing plain Hearthstone Access" }

# --- install ------------------------------------------------------------------------------
Log "== install into $Game"
if (Get-Process Hearthstone -ErrorAction SilentlyContinue) { throw 'Hearthstone was started during the build. Quit it and try again.' }
$Acc = Join-Path $Managed 'Accessibility'
New-Item -ItemType Directory -Force $Acc | Out-Null
Remove-OldSpeech $Managed
Copy-Item (Join-Path $PSScriptRoot 'prism\prism.dll') $Acc -Force
$licenses = Join-Path $Acc 'prism'                          # Prism's MPL-2.0 and third-party notices
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'prism\LICENSES'), (Join-Path $PSScriptRoot 'prism\NOTICE') $licenses -Recurse -Force
Copy-Item $Compat, $Speech $Managed -Force
if ($Enh) { Copy-Item $Enh $Managed -Force } else { Remove-Item (Join-Path $Managed 'HSAEnhanced.dll') -ErrorAction SilentlyContinue }

$hsa = Join-Path $Work 'hsa\patch'
$gameAccessibility = Join-Path $Game 'Accessibility'
New-Item -ItemType Directory -Force $gameAccessibility | Out-Null
Copy-Item (Join-Path $hsa 'Accessibility\Sounds') $gameAccessibility -Recurse -Force
Copy-Item (Join-Path $hsa 'Accessibility\hsa_manifest.json') $gameAccessibility -Force -ErrorAction SilentlyContinue
$extra = Get-Content -Raw (Join-Path $Src 'enhanced\ACCESSIBILITY_ENHANCED.txt')
foreach ($d in Get-ChildItem -Directory (Join-Path $hsa 'Strings')) {
    $f = Join-Path $d.FullName 'ACCESSIBILITY.txt'
    if (-not (Test-Path $f)) { continue }
    $dest = Join-Path $Game "Strings\$($d.Name)"
    New-Item -ItemType Directory -Force $dest | Out-Null
    $text = [IO.File]::ReadAllText($f)
    if ($Enh) { $text = $text.TrimEnd("`r", "`n") + "`n" + $extra }    # English texts of the enhancements (ACCESSIBILITY_ENH_*)
    [IO.File]::WriteAllText((Join-Path $dest 'ACCESSIBILITY.txt'), $text, (New-Object Text.UTF8Encoding $false))
}
# the game file last: until here the game still starts as it was
Copy-Item $Out $GameAsm -Force
Hash $GameAsm | Set-Content $InstalledHash
Hash $vanilla | Set-Content (Join-Path $Data 'built_for.sha256')
Split-Path -Leaf $Zip | Set-Content (Join-Path $Data 'built_from.txt')
Remove-Item $pending -Force -ErrorAction SilentlyContinue
Log "done: built for $(Read-Text (Join-Path $Data 'built_for.sha256')), enhancements $(if ($Enh) { 'on' } else { 'off' })"
