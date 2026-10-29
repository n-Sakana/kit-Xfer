# uninstall.ps1 -- choose and remove one of the current user's right-click registrations.
# The kit's shared uninstaller: the same file in every app, apart from the app section below.
# Direct run shows the list; -KeyName removes one verb without asking (for tests and scripts);
# dot-sourcing loads the functions only.
[CmdletBinding()]
param([switch]$ListOnly, [string]$KeyName = '')
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ---- App section ----
# BeforeRemove / AfterRemove run only when the chosen registration is this app's own verb.
$script:OwnKey = 'Xfer'
$script:BeforeRemove = $null
$script:AfterRemove = $null
# ---- End of app section ----

$script:AppRoot = Split-Path $PSScriptRoot -Parent
$script:Contexts = @(
    [pscustomobject]@{ Id = 'file'; Label = 'File'; Path = 'Software\Classes\*\shell' },
    [pscustomobject]@{ Id = 'folder'; Label = 'Folder'; Path = 'Software\Classes\Directory\shell' },
    [pscustomobject]@{ Id = 'background'; Label = 'Folder background'; Path = 'Software\Classes\Directory\Background\shell' },
    [pscustomobject]@{ Id = 'desktop'; Label = 'Desktop'; Path = 'Software\Classes\DesktopBackground\shell' }
)

# reg.exe writes errors to stderr, which $ErrorActionPreference = 'Stop' would turn into a raw
# PowerShell error; callers check the exit code instead.
function Invoke-RegExe([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & reg.exe @Arguments 2>&1 | Out-Null; $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
}

function Read-RegistryString($Key, [string]$Name) {
    $value = $Key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($null -eq $value) { return $null }
    [string]$value
}

function Add-CommandValues($Key, [System.Collections.ArrayList]$Values) {
    foreach ($name in $Key.GetSubKeyNames()) {
        $sub = $Key.OpenSubKey($name)
        if ($null -eq $sub) { continue }
        try {
            if ($name -eq 'command') {
                $command = Read-RegistryString $sub ''
                if ($command -and $Values -notcontains $command) { [void]$Values.Add($command) }
            }
            Add-CommandValues $sub $Values
        }
        finally { $sub.Dispose() }
    }
}

# Absolute file paths a command runs (the executable and quoted script paths), so copies in
# different folders can be told apart.
function Get-CommandPaths([string[]]$Commands) {
    $paths = New-Object System.Collections.ArrayList
    foreach ($command in $Commands) {
        foreach ($match in [regex]::Matches($command, '"(?<p>[A-Za-z]:\\[^"]+)"|^\s*(?<p>[A-Za-z]:\\\S+)')) {
            $path = [Environment]::ExpandEnvironmentVariables($match.Groups['p'].Value)
            if ($paths -notcontains $path) { [void]$paths.Add($path) }
        }
    }
    ,$paths.ToArray()
}

# One entry per verb name, across the four HKCU contexts.
function Get-Registrations {
    $groups = @{}
    foreach ($context in $script:Contexts) {
        $root = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($context.Path)
        if ($null -eq $root) { continue }
        try {
            foreach ($name in $root.GetSubKeyNames()) {
                $key = $root.OpenSubKey($name)
                if ($null -eq $key) { continue }
                try {
                    if (-not $groups.ContainsKey($name)) {
                        $groups[$name] = [pscustomobject]@{
                            KeyName = $name; DisplayName = $name
                            Contexts = New-Object System.Collections.ArrayList
                            Paths = New-Object System.Collections.ArrayList
                            Commands = New-Object System.Collections.ArrayList
                            CommandPaths = @()
                        }
                    }
                    $group = $groups[$name]
                    $label = Read-RegistryString $key 'MUIVerb'
                    if (-not [string]::IsNullOrWhiteSpace($label)) { $group.DisplayName = $label }
                    [void]$group.Contexts.Add($context.Label)
                    [void]$group.Paths.Add($context.Path + '\' + $name)
                    Add-CommandValues $key $group.Commands
                }
                finally { $key.Dispose() }
            }
        }
        finally { $root.Dispose() }
    }
    foreach ($group in $groups.Values) { $group.CommandPaths = Get-CommandPaths @($group.Commands) }
    @($groups.Values | Sort-Object DisplayName, KeyName)
}

# Only a verb directly under one of the four context keys may be exported or deleted.
function Assert-RegistrationPath([string]$Path) {
    foreach ($context in $script:Contexts) {
        $prefix = $context.Path + '\'
        if ($Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $rest = $Path.Substring($prefix.Length)
            if ($rest -and $rest.IndexOf('\') -lt 0) { return }
        }
    }
    throw ('Not a right-click menu entry: HKCU\' + $Path)
}

function Show-Registrations([object[]]$Registrations, [int]$Cursor) {
    if (-not [Console]::IsOutputRedirected) { try { [Console]::Clear() } catch { } }
    Write-Host ''
    Write-Host '  Remove a right-click menu entry' -ForegroundColor Cyan
    Write-Host ''
    if ($Registrations.Count -eq 0) {
        Write-Host '  No entries are registered.'
        return
    }
    for ($i = 0; $i -lt $Registrations.Count; $i++) {
        if ($i -eq $Cursor) { Write-Host ('  > ' + $Registrations[$i].DisplayName) -ForegroundColor Yellow }
        else { Write-Host ('    ' + $Registrations[$i].DisplayName) -ForegroundColor Gray }
    }
    # The detail block is padded to the tallest entry, so the hint row stays put while moving.
    $tallest = 0
    foreach ($r in $Registrations) { $tallest = [Math]::Max($tallest, [Math]::Max(1, $r.CommandPaths.Count)) }
    $current = $Registrations[$Cursor]
    Write-Host ''
    Write-Host ('  Key       ' + $current.KeyName)
    Write-Host ('  Where     ' + ($current.Contexts -join ', '))
    $paths = @($current.CommandPaths)
    if ($paths.Count -eq 0) { $paths = @('-') }
    for ($i = 0; $i -lt $tallest; $i++) {
        $head = if ($i -eq 0) { '  Command   ' } else { '            ' }
        if ($i -lt $paths.Count) { Write-Host ($head + $paths[$i]) } else { Write-Host '' }
    }
    Write-Host ''
    Write-Host '  Up/Down + Enter: remove   Esc: cancel' -ForegroundColor DarkGray
}

# Returns the chosen registration, or $null on Esc.
function Select-Registration([object[]]$Registrations) {
    $cursor = 0
    while ($true) {
        Show-Registrations $Registrations $cursor
        $key = [Console]::ReadKey($true)
        switch ($key.Key) {
            'UpArrow' { $cursor = ($cursor + $Registrations.Count - 1) % $Registrations.Count }
            'DownArrow' { $cursor = ($cursor + 1) % $Registrations.Count }
            'Escape' { return $null }
            'Enter' { return $Registrations[$cursor] }
        }
    }
}

# Exports every key of the registration before anything is deleted. Returns the backup folder.
function Backup-Registration($Registration, [string]$Root) {
    $folder = Join-Path $Root (Get-Date -Format 'yyyyMMdd-HHmmssfff')
    [void](New-Item -ItemType Directory -Path $folder -Force)
    $manifest = New-Object System.Collections.Generic.List[string]
    $manifest.Add('Right-click menu removal backup (current user)')
    $manifest.Add('Created: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))
    $manifest.Add('Name: ' + $Registration.DisplayName)
    $manifest.Add('Key: ' + $Registration.KeyName)
    $safeName = $Registration.KeyName
    foreach ($c in [IO.Path]::GetInvalidFileNameChars()) { $safeName = $safeName.Replace([string]$c, '_') }
    foreach ($path in $Registration.Paths) {
        Assert-RegistrationPath $path
        $context = @($script:Contexts | Where-Object { $path.StartsWith($_.Path + '\', [StringComparison]::OrdinalIgnoreCase) })[0]
        $file = Join-Path $folder ('{0}_{1}.reg' -f $safeName, $context.Id)
        $exitCode = Invoke-RegExe @('export', ('HKCU\' + $path), $file, '/y')
        if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw ('Could not back up: HKCU\' + $path)
        }
        $manifest.Add('  HKCU\' + $path + ' -> ' + $file)
    }
    [IO.File]::WriteAllLines((Join-Path $folder 'manifest.txt'), $manifest.ToArray(), (New-Object Text.UTF8Encoding($true)))
    $folder
}

function Remove-Registration($Registration) {
    foreach ($path in $Registration.Paths) {
        Assert-RegistrationPath $path
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($path, $false)
        $left = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path)
        if ($null -ne $left) { $left.Dispose(); throw ('Could not remove: HKCU\' + $path) }
    }
}

function Send-MenuChanged {
    if (-not ('KitMenuNotify' -as [type])) {
        Add-Type 'using System; using System.Runtime.InteropServices; public static class KitMenuNotify { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, IntPtr a, IntPtr b); }'
    }
    [KitMenuNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)   # SHCNE_ASSOCCHANGED
}

# Backs up, removes and reports one registration.
function Invoke-Removal($Registration) {
    $own = [string]::Equals($Registration.KeyName, $script:OwnKey, [StringComparison]::OrdinalIgnoreCase)
    if ($own -and $null -ne $script:BeforeRemove) { & $script:BeforeRemove }
    $backup = Backup-Registration $Registration (Join-Path $script:AppRoot 'output\context-menu-backups')
    Remove-Registration $Registration
    Send-MenuChanged
    Write-Host ('  Removed: ' + $Registration.DisplayName + ' (' + ($Registration.Contexts -join ', ') + ')') -ForegroundColor Green
    Write-Host ('  Backup: ' + $backup)
    if ($own -and $null -ne $script:AfterRemove) { & $script:AfterRemove }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $registrations = @(Get-Registrations)
        if ($KeyName) {
            $selected = @($registrations | Where-Object { [string]::Equals($_.KeyName, $KeyName, [StringComparison]::OrdinalIgnoreCase) })
            Write-Host ''
            if ($selected.Count -eq 0) { Write-Host ('  Not registered: ' + $KeyName) -ForegroundColor Green; exit 0 }
            Invoke-Removal $selected[0]
            exit 0
        }
        if ($ListOnly -or $registrations.Count -eq 0) { Show-Registrations $registrations 0; exit 0 }
        $selected = Select-Registration $registrations
        Write-Host ''
        if ($null -eq $selected) {
            Write-Host '  Nothing was removed.' -ForegroundColor Green
            exit 0
        }
        Invoke-Removal $selected
        exit 0
    }
    catch {
        Write-Host ''
        Write-Host ('  Error: ' + $_.Exception.Message) -ForegroundColor Red
        exit 1
    }
}
