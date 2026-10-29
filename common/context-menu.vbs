' common/context-menu.vbs -- open or activate Xfer without a console.
Option Explicit

Dim shell
Dim fileSystem
Dim scriptDir
Dim scriptPath
Dim command
Dim exitCode
Dim mode

If WScript.Arguments.Count < 1 Or WScript.Arguments.Count > 2 Then
    WScript.Quit 2
End If

mode = WScript.Arguments(0)
Select Case mode
    Case "app"
        If WScript.Arguments.Count <> 1 Then
            WScript.Quit 2
        End If
    Case "optical"
        If WScript.Arguments.Count <> 2 Then
            WScript.Quit 2
        End If
    Case Else
        WScript.Quit 2
End Select

Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

scriptDir = fileSystem.GetParentFolderName(WScript.ScriptFullName)
scriptPath = fileSystem.BuildPath(scriptDir, "open-context.ps1")
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File " _
    & Chr(34) & scriptPath & Chr(34) _
    & " -Mode " & Chr(34) & mode & Chr(34)
If mode = "optical" Then
    command = command _
        & " -Target " & Chr(34) & WScript.Arguments(1) & Chr(34)
End If

exitCode = shell.Run(command, 0, True)
If exitCode <> 0 Then
    MsgBox _
        "Xfer could not open.", _
        vbExclamation, "Xfer"
End If
