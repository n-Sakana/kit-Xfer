' Keep this script ASCII: wscript.exe uses the system ANSI code page without a UTF-16 BOM.

Option Explicit

Dim shell
Dim fileSystem
Dim baseDir
Dim scriptPath
Dim command
Dim exitCode
Dim logDir

Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

baseDir = fileSystem.GetParentFolderName(WScript.ScriptFullName)
scriptPath = fileSystem.BuildPath(baseDir, "Xfer.ps1")
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File " _
    & Chr(34) & scriptPath & Chr(34)

shell.CurrentDirectory = baseDir

' Wait for Xfer so a failure before the desktop window opens can still be
' reported instead of disappearing with the hidden launcher.
exitCode = shell.Run(command, 0, True)

' Xfer.ps1 reserves exit code 3 for failures before the window can report
' them itself. Normal shutdown and replacement of a running Xfer stay quiet.
If exitCode = 3 Then
    logDir = fileSystem.BuildPath( _
        shell.ExpandEnvironmentStrings("%LOCALAPPDATA%"), "Xfer\logs")
    MsgBox _
        "Xfer could not start." & vbCrLf & vbCrLf & _
        "The reason was written to the newest log file in:" & vbCrLf & _
        logDir, _
        vbExclamation, "Xfer"
End If
