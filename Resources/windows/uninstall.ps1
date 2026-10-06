# Hearthstone Access Enhanced for Windows uninstaller: removes the rebuild and prompt tasks and the mod files and
# puts the game's own Assembly-CSharp.dll back. Started by "Uninstall Hearthstone access.bat".
$ErrorActionPreference = 'Stop'
$Data = if ($env:HSA_DATA_DIR) { $env:HSA_DATA_DIR } else { Join-Path $env:ProgramData 'HearthstoneAccess' }
. (Join-Path $PSScriptRoot 'common.ps1')

try {
    Write-Host '== Hearthstone Access for Windows - uninstall'
    if (Get-Process Hearthstone -ErrorAction SilentlyContinue) { throw 'Hearthstone is running. Quit the game and run this again.' }

    Unregister-ScheduledTask -TaskName 'Hearthstone Access rebuild' -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'Hearthstone Access prompt' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'Rebuild and prompt tasks removed.'

    $Game = Find-Game
    $Managed = Join-Path $Game 'Hearthstone_Data\Managed'
    $GameAsm = Join-Path $Managed 'Assembly-CSharp.dll'
    $installed = Join-Path $Data 'installed.sha256'
    $vanilla = Join-Path $Data 'vanilla\Assembly-CSharp.dll'
    $current = (Get-FileHash -Algorithm SHA256 $GameAsm).Hash.ToLowerInvariant()
    if ((Test-Path $installed) -and $current -eq (Get-Content $installed -Raw).Trim()) {
        if (Test-Path $vanilla) { Copy-Item $vanilla $GameAsm -Force; Write-Host "The game's own Assembly-CSharp.dll is back." }
        else { Write-Host 'The original Assembly-CSharp.dll backup is missing: repair the game in Battle.net (Options > Scan and Repair).' }
    }

    foreach ($f in 'HSAPrism.dll', 'HSACompat.dll', 'HSAEnhanced.dll') { Remove-Item (Join-Path $Managed $f) -Force -ErrorAction SilentlyContinue }
    Remove-OldSpeech $Managed
    Remove-Item (Join-Path $Managed 'Accessibility') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $Game 'Accessibility') -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $Game 'Strings') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item (Join-Path $_.FullName 'ACCESSIBILITY.txt') -Force -ErrorAction SilentlyContinue }
    Write-Host 'Mod files removed.'

    # everything else goes; a .NET SDK the installer put there stays (it may serve other tools)
    Get-ChildItem $Data -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'dotnet' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $Data 'dotnet')) { Write-Host ".NET in $Data\dotnet stays (you can delete that folder by hand)." }
    else { Remove-Item $Data -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host ''
    Write-Host 'DONE. Hearthstone Access has been removed.'
    exit 0
} catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
}
