# Pure registry-plan tests: safe on Linux; does not import a registry file.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
if (-not $env:SystemRoot) { $env:SystemRoot = '/Windows' }
. (Join-Path $root 'common/install.ps1')
$lines = @(Build-RegistryLines $true)
$text = $lines -join "`n"
Assert-True ($script:MenuName -eq 'Xfer' -and $script:KeyName -eq 'Xfer') 'Application name mismatch.'
Assert-True ($script:Contexts.Count -eq 4) 'Expected four contexts.'
Assert-True ($script:MenuItems.Count -eq 1) 'Unexpected option count.'
foreach ($context in $script:Contexts) {
    $key = $context + '\Xfer'
    Assert-True ($lines -contains ('[' + $key + ']')) ('Missing top-level app: ' + $key)
    Assert-True ($context.StartsWith('HKEY_CURRENT_USER\')) 'Only HKCU is permitted.'
    if ($context -match 'Background\\shell$') {
        Assert-True ($lines -contains ('[' + $key + '\command]')) 'Background must open the app.'
        Assert-True (-not $text.Contains('[' + $key + '\shell\')) 'Background must not pass a selection.'
    } else {
        foreach ($item in $script:MenuItems) {
            Assert-True ($lines -contains ('[' + $key + '\shell\' + $item.Id + '\command]')) 'Missing option.'
            Assert-True ((Get-MenuCommand $item.Mode).Contains('"%1"')) 'Selected path must be quoted.'
        }
        Assert-True ($script:MenuItems.Count -le 16) 'Submenu limit exceeded.'
    }
}
Assert-True ((Get-AppCommand) -notmatch '%[1Vv]') 'Background must not infer a target.'
Assert-True ($text -notmatch 'HKLM|HKEY_LOCAL_MACHINE|runas') 'Unexpected machine-wide key or elevation.'
$removeLines = @(Build-RegistryLines $false)
Assert-True (@($removeLines | Where-Object { $_.StartsWith('[-HKEY_CURRENT_USER\') }).Count -eq 4) 'Removal misses a context.'
foreach ($context in $script:Contexts) {
    Assert-True ($removeLines -contains ('[-' + $context + '\Xfer]')) 'Removal targets another application.'
}
# The kit's shared uninstaller: dot-sourcing loads its functions without touching the registry.
$installContexts = @($script:Contexts)
$uninstall = [IO.File]::ReadAllText((Join-Path $root 'common/uninstall.ps1'))
. (Join-Path $root 'common/uninstall.ps1')
Assert-True ($script:OwnKey -eq 'Xfer') 'Uninstall must know its own key.'
Assert-True (@($script:Contexts).Count -eq $installContexts.Count) 'Uninstall must look at the four contexts.'
foreach ($context in $script:Contexts) {
    Assert-True ($installContexts -contains ('HKEY_CURRENT_USER\' + $context.Path)) ('Uninstall looks outside the install contexts: ' + $context.Path)
}
Assert-RegistrationPath 'Software\Classes\Directory\shell\Xfer'
foreach ($outside in @('Software\Microsoft\Windows\CurrentVersion\Run\Xfer', 'Software\Classes\Directory\shell\Xfer\command', 'Software\Classes\Directory\shell')) {
    $refused = $false
    try { Assert-RegistrationPath $outside } catch { $refused = $true }
    Assert-True $refused ('Uninstall must refuse ' + $outside)
}
Assert-True ($uninstall -notmatch 'HKLM|HKEY_LOCAL_MACHINE|LocalMachine') 'Uninstall must stay in HKCU.'
$removal = $uninstall.Substring($uninstall.IndexOf('function Invoke-Removal'))
Assert-True ($removal.IndexOf('Backup-Registration') -ge 0 -and $removal.IndexOf('Backup-Registration') -lt $removal.IndexOf('Remove-Registration')) 'Uninstall must back up before removing.'
$originalItems = $script:MenuItems
try {
    $script:MenuItems = @(1..17)
    $rejected = $false
    try { Build-RegistryLines $true | Out-Null } catch { $rejected = $true }
    Assert-True $rejected 'Installer must reject more than 16 options.'
} finally { $script:MenuItems = $originalItems }
foreach ($path in @((Get-ChildItem -LiteralPath $root -Filter '*.ps1').FullName) + @((Get-ChildItem -LiteralPath (Join-Path $root 'common') -Filter '*.ps1').FullName)) {
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) ('PowerShell parse failed: ' + $path + ': ' + $errors)
}
Write-Output 'Xfer_DESKTOP_PLAN_OK (no Windows registry or Explorer execution)'
