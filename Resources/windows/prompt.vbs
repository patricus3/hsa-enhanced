' Started every minute by the "Hearthstone Access prompt" task as the signed-in player. Runs
' prompt.ps1 without any window, and only when the rebuild task has left its marker (so nothing
' starts at all the rest of the time, and no console flashes over the game).
Option Explicit
Dim fso, shell, data, here
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
data = shell.ExpandEnvironmentStrings("%HSA_DATA_DIR%")
If data = "%HSA_DATA_DIR%" Or data = "" Then data = shell.ExpandEnvironmentStrings("%ProgramData%") & "\HearthstoneAccess"
If fso.FileExists(data & "\prompt\waiting.txt") Then
    here = fso.GetParentFolderName(WScript.ScriptFullName)
    shell.Run "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & here & "\prompt.ps1""", 0, False
End If
