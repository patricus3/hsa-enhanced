# Hearthstone Access for Windows installer (unofficial build: speech through Prism, which
# finds your screen reader by itself; Black Market and menus built from what the game shows).
# Started by "Install Hearthstone access.bat" with administrator rights.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$Data = if ($env:HSA_DATA_DIR) { $env:HSA_DATA_DIR } else { Join-Path $env:ProgramData 'HearthstoneAccess' }
. (Join-Path $PSScriptRoot 'common.ps1')
$Task = 'Hearthstone Access rebuild'

function Say([string]$t) { Write-Host ''; Log "== $t" }

try {
    Say 'Hearthstone Access for Windows - installation'
    $Game = Find-Game
    Log "Hearthstone: $Game"
    if (Get-Process Hearthstone -ErrorAction SilentlyContinue) { throw 'Hearthstone is running. Quit the game and run the installer again.' }

    Say 'Copying mod files'
    $Src = Join-Path $Data 'src'
    New-Item -ItemType Directory -Force $Src, (Join-Path $Data 'downloads') | Out-Null
    $from = Split-Path -Parent $PSScriptRoot
    & robocopy $from $Src /MIR /XD bin obj work downloads /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Could not copy the mod files.' }

    # --use-hsa-menus: Hearthstone Access's own menus instead of the enhanced menu system
    $menus = if ($args -contains '--use-hsa-menus') { 'hsa' } else { 'enhanced' }
    Set-Content (Join-Path $Data 'menus.txt') $menus
    Log "menus: $menus"

    Say 'Checking tools'
    Use-Dotnet

    Say 'Downloading Hearthstone Access from hearthstoneaccess.com and its source diff from GitHub'
    Update-Hsa | Out-Null
    $dl = Join-Path $Data 'downloads'

    Say 'Building the mod for your game version (about a minute)'
    & (Join-Path $Src 'windows\rebuild.ps1') -Zip "$dl\hsa.zip"

    Say 'Installing the task that rebuilds the mod after a game update'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Src\windows\rebuild.ps1`" -Auto"
    $triggers = @((New-ScheduledTaskTrigger -AtStartup),
                  (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)))
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
    Register-ScheduledTask -TaskName $Task -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null

    Write-Host ''
    Log 'DONE. Hearthstone Access is installed.'
    Write-Host 'Start the game from Battle.net as usual. Speech goes to your screen reader (NVDA, JAWS and others),'
    Write-Host 'or to the Windows voices when no screen reader is running.'
    Write-Host 'After a game update the mod rebuilds itself within a few minutes while the game is closed.'
    Write-Host 'To remove the mod, use "Uninstall Hearthstone access.bat".'
    exit 0
} catch {
    Write-Host ''
    Log "ERROR: $($_.Exception.Message)"
    Write-Host "Installation aborted. Logs: $Data\logs"
    exit 1
}
