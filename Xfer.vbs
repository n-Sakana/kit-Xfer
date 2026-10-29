' This file must stay pure ASCII. wscript.exe reads a .vbs as the system ANSI
' code page unless it starts with a UTF-16 byte order mark, so a non-ASCII
' character saved as UTF-8 makes the whole script fail to compile. Japanese
' wording therefore lives in README.md and in the log file, not here.

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

' Wait for Xfer so a failure before the WPF window opens can still be
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
