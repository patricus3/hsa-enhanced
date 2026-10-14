' Started every minute by the "Hearthstone Access prompt" task as the signed-in player. Runs
' prompt.ps1 without a window; it checks for a release update daily and handles rebuild prompts.
Option Explicit
Dim fso, shell, data, here
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
data = shell.ExpandEnvironmentStrings("%HSA_DATA_DIR%")
If data = "%HSA_DATA_DIR%" Or data = "" Then data = shell.ExpandEnvironmentStrings("%ProgramData%") & "\HearthstoneAccess"
here = fso.GetParentFolderName(WScript.ScriptFullName)
shell.Run "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & here & "\prompt.ps1""", 0, False
