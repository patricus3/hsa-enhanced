# Runs as the signed-in player (the "Hearthstone Access prompt" task, started hidden through
# prompt.vbs every minute). The rebuild task runs as SYSTEM and cannot show anything on the
# desktop: when a game update removed the mod while Hearthstone runs, it leaves prompt\waiting.txt
# and starts this task at once. This asks the player whether to close Hearthstone so the mod goes back in now,
# and offers to start the game again once it is in. "No" is remembered for that update.
$ErrorActionPreference = 'Stop'
$Data = if ($env:HSA_DATA_DIR) { $env:HSA_DATA_DIR } else { Join-Path $env:ProgramData 'HearthstoneAccess' }
$waiting = Join-Path $Data 'prompt\waiting.txt'
$installedHash = Join-Path $Data 'installed.sha256'
$Title = 'Hearthstone Access'

Add-Type -Namespace HsaPrompt -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern int MessageBoxW(System.IntPtr hWnd, string text, string caption, uint type);
'@
# on top of the game, in front, a plain dialog screen readers read
$MB_YESNO = 0x4; $MB_OK = 0x0; $MB_ICONWARNING = 0x30; $MB_ICONINFO = 0x40
$MB_SYSTEMMODAL = 0x1000; $MB_SETFOREGROUND = 0x10000; $MB_TOPMOST = 0x40000
function Ask([string]$text, [uint32]$buttons, [uint32]$icon) {
    return [HsaPrompt.Native]::MessageBoxW([IntPtr]::Zero, $text, $Title, $buttons -bor $icon -bor $MB_SYSTEMMODAL -bor $MB_SETFOREGROUND -bor $MB_TOPMOST)
}
$IDYES = 6

function GameAssembly {
    $p = Get-Process Hearthstone -ErrorAction SilentlyContinue | Select-Object -First 1
    $dir = if ($p -and $p.Path) { Split-Path $p.Path } else { $null }
    if (-not $dir) {
        foreach ($c in (Join-Path ${env:ProgramFiles(x86)} 'Hearthstone'), (Join-Path $env:ProgramFiles 'Hearthstone')) { if (Test-Path $c) { $dir = $c; break } }
    }
    if (-not $dir) { return $null }
    return Join-Path $dir 'Hearthstone_Data\Managed\Assembly-CSharp.dll'
}

# the mod is in the game again (the rebuild task installed it)
function ModInPlace {
    $asm = GameAssembly
    if (-not $asm -or -not (Test-Path $asm) -or -not (Test-Path $installedHash)) { return $false }
    return (Get-FileHash -Algorithm SHA256 $asm).Hash.ToLowerInvariant() -eq (Get-Content $installedHash -Raw).Trim()
}

if (-not (Test-Path $waiting)) { exit 0 }
if (-not (Get-Process Hearthstone -ErrorAction SilentlyContinue)) { exit 0 }
if (ModInPlace) { exit 0 }                                  # a leftover marker: nothing to do

# asked about this update before and the answer was no
$id = (Get-Content $waiting -Raw).Trim()
$mine = Join-Path $env:LOCALAPPDATA 'HearthstoneAccess'
$asked = Join-Path $mine 'asked.txt'
if ((Test-Path $asked) -and (Get-Content $asked -Raw).Trim() -eq $id) { exit 0 }
New-Item -ItemType Directory -Force $mine | Out-Null
Set-Content $asked $id

$answer = Ask ("Hearthstone was updated, and the update removed Hearthstone Access. The game is running without it now.`n`n" +
               "It is being rebuilt for the new version right now (about a minute). Close Hearthstone and put Hearthstone Access back?`n`n" +
               "Yes: Hearthstone closes; as soon as the mod is back in you can start the game again with it.`n" +
               "No: keep playing; the mod goes back in when you close the game yourself.") $MB_YESNO $MB_ICONWARNING
if ($answer -ne $IDYES) { exit 0 }

# close the game the normal way first, then for good
foreach ($p in Get-Process Hearthstone -ErrorAction SilentlyContinue) { [void]$p.CloseMainWindow() }
$until = (Get-Date).AddSeconds(15)
while ((Get-Process Hearthstone -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
Get-Process Hearthstone -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# the rebuild task installs it within seconds of the game closing (once its build is done)
$until = (Get-Date).AddMinutes(5)
while (-not (ModInPlace) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
if (-not (ModInPlace)) {
    [void](Ask "Hearthstone is closed, but Hearthstone Access is not back in yet. It will be put back within a few minutes; the log is in $Data\logs." $MB_OK $MB_ICONWARNING)
    exit 0
}
$again = Ask 'Hearthstone Access is back in. Start Hearthstone now?' $MB_YESNO $MB_ICONINFO
if ($again -eq $IDYES) { Start-Process 'battlenet://WTCG' }
