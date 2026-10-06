# Rebuilds Hearthstone Access Enhanced for Windows against the installed game and installs it.
#   rebuild.ps1 [-Auto]
# -Auto (scheduled task, every minute): only acts when a game update replaced the mod. While the
# game runs it builds anyway and installs the moment the game closes.
# Needs administrator rights (the game lives in Program Files). Windows counterpart of ../rebuild.sh.
param([switch]$Auto)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Src  = Split-Path -Parent $PSScriptRoot          # the Resources folder
$Data = if ($env:HSA_DATA_DIR) { $env:HSA_DATA_DIR } else { Join-Path $env:ProgramData 'HearthstoneAccess' }
$Work = Join-Path $Data 'work'
$Backup = Join-Path $Data 'vanilla'
. (Join-Path $PSScriptRoot 'common.ps1')
trap { Log "ERROR: $($_.Exception.Message)"; Remove-Item (Join-Path $Data 'prompt\waiting.txt') -Force -ErrorAction SilentlyContinue; break }   # logged, then passed on (install shows it, the task records it)

$Game = Find-Game
$Managed = Join-Path $Game 'Hearthstone_Data\Managed'
$GameAsm = Join-Path $Managed 'Assembly-CSharp.dll'
$InstalledHash = Join-Path $Data 'installed.sha256'

function Hash($p) { (Get-FileHash -Algorithm SHA256 $p).Hash.ToLowerInvariant() }
function Read-Text($p) { if (Test-Path $p) { (Get-Content $p -Raw).Trim() } else { '' } }

# --- which Assembly-CSharp is the game's own? ---------------------------------------------
$current = Hash $GameAsm
if ($current -eq (Read-Text $InstalledHash)) {
    if ($Auto) { exit 0 }                                   # our build is in place
    $vanilla = Join-Path $Backup 'Assembly-CSharp.dll'      # rebuild from the saved original
    if (-not (Test-Path $vanilla)) { throw "The original Assembly-CSharp.dll backup is missing. Repair the game in Battle.net (Options > Scan and Repair) and run the installer again." }
} else {
    # Battle.net may still be writing the game's files: wait for them to settle (the next run)
    if ($Auto -and (Get-Item $GameAsm).LastWriteTime -gt (Get-Date).AddSeconds(-15)) { exit 0 }
    if (Test-HsaAssembly $GameAsm) { throw "The game has the official Hearthstone Access (or another mod) installed. Repair the game in Battle.net (Options > Scan and Repair) and run the installer again." }
    Log "game Assembly-CSharp.dll is new ($current), building the mod for it"
    New-Item -ItemType Directory -Force $Backup | Out-Null
    Copy-Item $GameAsm (Join-Path $Backup 'Assembly-CSharp.dll') -Force
    $vanilla = Join-Path $Backup 'Assembly-CSharp.dll'
}
# the build does not need the game closed, only the install (the task waits for it there)
if (-not $Auto -and (Get-Process Hearthstone -ErrorAction SilentlyContinue)) { throw 'Hearthstone is running. Quit the game and try again.' }

# The player's prompt (prompt.ps1, run as the signed-in player by the "Hearthstone Access prompt"
# task): while prompt\waiting.txt is there and the game runs without the mod, it offers to close
# the game so the mod goes back in. Started right away, not on the task's next minute.
$Marker = Join-Path $Data 'prompt\waiting.txt'
function Request-Prompt {
    if (Test-Path $Marker) { return }
    New-Item -ItemType Directory -Force (Split-Path $Marker) | Out-Null
    Set-Content $Marker "$current $(Get-Date -Format o)"
    try { Start-ScheduledTask -TaskName 'Hearthstone Access prompt' -ErrorAction Stop } catch { Log "prompt task: $($_.Exception.Message)" }
}
function Clear-Prompt { Remove-Item $Marker -Force -ErrorAction SilentlyContinue }
# a game started right after the update (Battle.net does): ask now, the build takes a minute
if ($Auto -and (Get-Process Hearthstone -ErrorAction SilentlyContinue)) {
    Log 'Hearthstone is running without the mod: asking whether to close it'
    Request-Prompt
}

# --- tools --------------------------------------------------------------------------------
Use-Dotnet
Log '== build tools'
Build (Join-Path $Src 'tools\port')
Build (Join-Path $PSScriptRoot 'speech')
$PortDll = Join-Path $Src 'tools\port\bin\Release\net8.0\port.dll'
$Speech  = Join-Path $PSScriptRoot 'speech\bin\Release\net472\HSAPrism.dll'
# a native command with all its output in $log; stderr lines must not stop the script (PowerShell 5.1)
function Invoke-Logged([string]$log, [scriptblock]$command) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $command *> $log } finally { $ErrorActionPreference = $old }
    return $LASTEXITCODE
}
function Port { & dotnet $PortDll @args; if ($LASTEXITCODE -ne 0) { throw "port $($args[0]) failed" } }
function Unresolved { param([string[]]$a) $o = & dotnet $PortDll check @a; $o | Out-File -Encoding utf8 (Join-Path $Work 'check.log'); [int](($o | Select-Object -Last 1) -split ' ')[1] }

# --- the mod: our code, hooked into the game's own Assembly-CSharp ----------------------------
Log '== building the mod'
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
New-Item -ItemType Directory -Force (Join-Path $Work 'out') | Out-Null
$V = Join-Path $Work 'vanilla-Assembly-CSharp.dll'
Copy-Item $vanilla $V
$Out = Join-Path $Work 'out\Assembly-CSharp.dll'
Copy-Item $V $Out
$base = Unresolved @($V, $Managed)
$E = Join-Path $Work 'enh'
New-Item -ItemType Directory -Force (Join-Path $E 'check') | Out-Null
$code = Invoke-Logged (Join-Path $Work 'enhanced-build.log') { & dotnet build -c Release -v q (Join-Path $Src 'enhanced') "-p:GameManaged=$Managed" "-p:GameAssembly=$V" -o (Join-Path $E 'bin') }
if ($code -ne 0) { throw "the mod could not be built, see $Work\enhanced-build.log" }
$Enh = Join-Path $E 'bin\HSAEnhanced.dll'
Port hook $Out $Enh $Out $Managed --without-hsa
Copy-Item $Out, $Enh (Join-Path $E 'check') -Force
$enow = Unresolved @((Join-Path $E 'check\Assembly-CSharp.dll'), (Join-Path $E 'check'), $Managed, (Split-Path $Speech))
$eadd = Unresolved @((Join-Path $E 'check\HSAEnhanced.dll'), (Join-Path $E 'check'), $Managed)
Log "unresolved: game $base, hooked $enow, mod $eadd"
if ($enow -gt $base -or $eadd -ne 0) { throw "the build has unresolved references, see $Work\check.log" }

# --- install ------------------------------------------------------------------------------
# the task: a game started right after an update (Battle.net does) gets the mod the moment it closes
if ($Auto -and (Get-Process Hearthstone -ErrorAction SilentlyContinue)) {
    Log 'Hearthstone is running: the new build is installed as soon as it closes'
    Request-Prompt
    try { while (Get-Process Hearthstone -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 } }
    finally { Clear-Prompt }
    Start-Sleep -Seconds 3                                  # the game lets go of its files
    # Battle.net changed the game again meanwhile: the next run builds for that version
    if ((Hash $GameAsm) -ne $current) { Log 'the game changed while waiting; building again on the next run'; exit 0 }
}
Log "== install into $Game"
if (Get-Process Hearthstone -ErrorAction SilentlyContinue) { throw 'Hearthstone was started during the build. Quit it and try again.' }
$Acc = Join-Path $Managed 'Accessibility'
New-Item -ItemType Directory -Force $Acc | Out-Null
Remove-OldSpeech $Managed
Copy-Item (Join-Path $PSScriptRoot 'prism\prism.dll') $Acc -Force
$licenses = Join-Path $Acc 'prism'                          # Prism's MPL-2.0 and third-party notices
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'prism\LICENSES'), (Join-Path $PSScriptRoot 'prism\NOTICE') $licenses -Recurse -Force
Copy-Item $Speech $Managed -Force
Copy-Item $Enh $Managed -Force
# what older versions installed for Hearthstone Access
Remove-Item (Join-Path $Managed 'HSACompat.dll') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $Game 'Accessibility') -Recurse -Force -ErrorAction SilentlyContinue
# Hearthstone Access's texts, which come with the mod (strings\<language>\ACCESSIBILITY.txt): the mod reads them
Get-ChildItem (Join-Path $Game 'Strings') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item (Join-Path $_.FullName 'ACCESSIBILITY.txt') -ErrorAction SilentlyContinue }
foreach ($d in Get-ChildItem -Directory (Join-Path $Src 'strings') -ErrorAction SilentlyContinue) {
    $f = Join-Path $d.FullName 'ACCESSIBILITY.txt'
    if (-not (Test-Path $f)) { continue }
    $dest = Join-Path $Game "Strings\$($d.Name)"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $f (Join-Path $dest 'ACCESSIBILITY.txt') -Force
}
# the game file last: until here the game still starts as it was
Copy-Item $Out $GameAsm -Force
Hash $GameAsm | Set-Content $InstalledHash
Hash $vanilla | Set-Content (Join-Path $Data 'built_for.sha256')
Clear-Prompt
Log "done: built for $(Read-Text (Join-Path $Data 'built_for.sha256'))"
