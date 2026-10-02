# Hearthstone Access for Windows installer (unofficial build: speech through Prism, which
# finds your screen reader by itself; Black Market and menus built from what the game shows).
# Started by "Install Hearthstone access.bat" with administrator rights.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$Data = if ($env:HSA_DATA_DIR) { $env:HSA_DATA_DIR } else { Join-Path $env:ProgramData 'HearthstoneAccess' }
. (Join-Path $PSScriptRoot 'common.ps1')
$Task = 'Hearthstone Access rebuild'
$PromptTask = 'Hearthstone Access prompt'

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
    # --without-hsa: our own core only (no Hearthstone Access download; most screens are not ours yet)
    $standalone = $args -contains '--without-hsa'
    Set-Content (Join-Path $Data 'mode.txt') $(if ($standalone) { 'without-hsa' } else { 'hsa' })
    Log "mode: $(if ($standalone) { 'without Hearthstone Access' } else { 'with Hearthstone Access' })"

    Say 'Checking tools'
    Use-Dotnet

    $dl = Join-Path $Data 'downloads'
    if (-not $standalone) {
        Say 'Downloading Hearthstone Access from hearthstoneaccess.com and its source diff from GitHub'
        Update-Hsa | Out-Null
    }

    Say 'Building the mod for your game version (about a minute)'
    & (Join-Path $Src 'windows\rebuild.ps1') -Zip "$dl\hsa.zip"

    Say 'Installing the task that rebuilds the mod after a game update'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$Src\windows\rebuild.ps1`" -Auto"
    $triggers = @((New-ScheduledTaskTrigger -AtStartup),
                  (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)))
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 24)
    Register-ScheduledTask -TaskName $Task -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null

    # the rebuild task runs as SYSTEM and cannot show anything: this one runs as the signed-in player
    # and asks whether to close a game started without the mod (prompt.vbs starts nothing otherwise)
    Say 'Installing the task that offers to close Hearthstone when an update removed the mod'
    $pAction = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "//B //Nologo `"$Src\windows\prompt.vbs`""
    $pTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    $pPrincipal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited     # Users: whoever is signed in
    $pSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    Register-ScheduledTask -TaskName $PromptTask -Action $pAction -Trigger $pTrigger -Principal $pPrincipal -Settings $pSettings -Force | Out-Null

    Write-Host ''
    Log 'DONE. Hearthstone Access is installed.'
    Write-Host 'Start the game from Battle.net as usual. Speech goes to your screen reader (NVDA, JAWS and others),'
    Write-Host 'or to the Windows voices when no screen reader is running.'
    Write-Host 'After a game update the mod rebuilds itself within a minute or two; if the game is already'
    Write-Host 'running then, a message asks whether to close it now and put the mod back (or it goes back in'
    Write-Host 'as soon as you close the game yourself).'
    Write-Host 'To remove the mod, use "Uninstall Hearthstone access.bat".'
    exit 0
} catch {
    Write-Host ''
    Log "ERROR: $($_.Exception.Message)"
    Write-Host "Installation aborted. Logs: $Data\logs"
    exit 1
}
