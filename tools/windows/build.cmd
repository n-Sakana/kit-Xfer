@echo off
rem Builds Xfer's launcher and native component using the MiniUI DLLs bundled in runtime\windows-x64.
rem Needs the .NET 9 SDK and the Visual Studio C++ build tools (Native AOT). Users of the folder need neither.
setlocal
cd /d "%~dp0..\.."
if not exist runtime\windows-x64\MiniUI.Engine.dll (echo Bundled MiniUI.Engine.dll is missing from runtime\windows-x64. & exit /b 1)
if not exist runtime\windows-x64\MiniUI.Bridge.dll (echo Bundled MiniUI.Bridge.dll is missing from runtime\windows-x64. & exit /b 1)
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
dotnet publish native\Xfer.Native.csproj -c Release -r win-x64 -nologo -v q -o artifacts\native-win-x64 || exit /b 1
dotnet build launcher\Xfer.Launcher.csproj -c Release -nologo -v q -o artifacts\launcher-win-x64 || exit /b 1
copy /y artifacts\launcher-win-x64\Xfer.Launcher.dll runtime\windows-x64\ >nul || exit /b 1
if exist runtime\windows-x64\Xfer.Native.dll del runtime\windows-x64\Xfer.Native.dll
copy /y artifacts\native-win-x64\Xfer.Native.dll ui\MiniUI.App.dll >nul || exit /b 1
echo built: runtime\windows-x64 and ui\MiniUI.App.dll
dir /b runtime\windows-x64
