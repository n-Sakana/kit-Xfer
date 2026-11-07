@echo off
rem Builds Xfer's ABI v2 native component; MiniUI is bundled in runtime\windows-x64.
rem Needs the .NET 9 SDK and the Visual Studio C++ build tools (Native AOT). Users of the folder need neither.
setlocal
cd /d "%~dp0..\.."
if not exist runtime\windows-x64\MiniUI.Engine.dll (echo Bundled MiniUI.Engine.dll is missing from runtime\windows-x64. & exit /b 1)
if not exist runtime\windows-x64\MiniUI.Bridge.dll (echo Bundled MiniUI.Bridge.dll is missing from runtime\windows-x64. & exit /b 1)
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
dotnet publish native\Xfer.Native.csproj -c Release -r win-x64 -nologo -v q -o artifacts\native-win-x64 || exit /b 1
if not exist ui\app mkdir ui\app
copy /y artifacts\native-win-x64\miniui-app.dll ui\app\miniui-app.dll >nul || exit /b 1
echo built: ui\app\miniui-app.dll
