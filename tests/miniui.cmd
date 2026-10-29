@echo off
rem Loads the P/Invoke bridge in Windows PowerShell. Encoded messages avoid cmd.exe code-page differences.
setlocal
set "MINIUI_ARGV=%*"
set "MINIUI_DIR=%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -STA -Command "$b = $env:MINIUI_DIR + 'MiniUI.Bridge.dll'; if (-not [IO.File]::Exists($b)) { [Console]::Error.WriteLine([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('RU5WX01JU1NJTkc6IE1pbmlVSS5CcmlkZ2UuZGxsIGlzIG1pc3NpbmcgYmVzaWRlIHRoaXMgbGF1bmNoZXIuIEV4dHJhY3QgdGhlIGNvbXBsZXRlIGRpc3RyaWJ1dGlvbiBaSVAu'))); exit 3 }; if ([int][Security.Policy.Zone]::CreateFromUrl('file://' + $b).SecurityZone -ge 3) { [Console]::Error.WriteLine([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('RU5WX0JMT0NLRUQ6IFdpbmRvd3MgYmxvY2tlZCBNaW5pVUkuQnJpZGdlLmRsbCBiZWNhdXNlIGl0IHdhcyBkb3dubG9hZGVkLiBVbmJsb2NrIHRoZSBkaXN0cmlidXRpb24gWklQIGluIFByb3BlcnRpZXMsIHRoZW4gZXh0cmFjdCBpdCBhZ2Fpbi4='))); exit 3 }; if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') { [Console]::Error.WriteLine([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('RU5WX1BPTElDWTogUG93ZXJTaGVsbCBsYW5ndWFnZSByZXN0cmljdGlvbnMgcHJldmVudCBNaW5pVUkgZnJvbSBzdGFydGluZy4gQXNrIHlvdXIgYWRtaW5pc3RyYXRvciB0byBhbGxvdyBpdC4='))); exit 3 }; [void][Reflection.Assembly]::LoadFrom($b); exit [MiniUI.Bridge]::RunArgs($env:MINIUI_ARGV)"
exit /b %errorlevel%
