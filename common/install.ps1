# common/install.ps1 -- build one .reg and import the Xfer menu (HKCU).
# Dot-source loads functions only; direct run performs the install.
Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$script:MenuName = "Xfer"
$script:KeyName = "Xfer"
$script:Launcher = Join-Path $PSScriptRoot "context-menu.vbs"
$script:XferScript = Join-Path (Split-Path $PSScriptRoot -Parent) "Xfer.ps1"
$script:MenuItems = @(
    [pscustomobject]@{ Id = "01-open"; Label = "Open"; Mode = "optical" }
)
$script:Contexts = @(
    "HKEY_CURRENT_USER\Software\Classes\*\shell",
    "HKEY_CURRENT_USER\Software\Classes\Directory\shell",
    "HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell",
    "HKEY_CURRENT_USER\Software\Classes\DesktopBackground\shell"
)

function ConvertTo-RegString {
    param([string]$Value)
    return ($Value -replace "\\", "\\" -replace '"', '\"')
}

function Assert-HkcuPath {
    param([string]$Path)
    if (-not $Path.StartsWith(
            "HKEY_CURRENT_USER\",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Only HKCU registry paths are allowed."
    }
}

function Invoke-RegExe {
    param([string[]]$CommandArguments)

    $previousErrorAction = $ErrorActionPreference
    $exitCode = 1
    $ErrorActionPreference = "Continue"
    try {
        & reg.exe @CommandArguments 2>&1 | Out-Null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    return $exitCode
}

function Get-MenuCommand {
    param([string]$Mode)
    $wscript = Join-Path $env:SystemRoot "System32\wscript.exe"
    return ('"{0}" "{1}" "optical" "%1"' -f $wscript, $script:Launcher)
}

function Get-AppCommand {
    $wscript = Join-Path $env:SystemRoot "System32\wscript.exe"
    return ('"{0}" "{1}" "app"' -f $wscript, $script:Launcher)
}

function Build-RegistryLines {
    param([bool]$Install)

    $lines = New-Object "System.Collections.Generic.List[string]"
    $lines.Add("Windows Registry Editor Version 5.00")
    $lines.Add("")

    if ($script:MenuItems.Count -gt 16) { throw "A submenu may contain at most 16 rows." }
    foreach ($context in $script:Contexts) {
        Assert-HkcuPath $context
        $key = $context + "\" + $script:KeyName
        $lines.Add("[-" + $key + "]")
        $lines.Add("")

        if ($Install) {
            $lines.Add("[" + $key + "]")
            $lines.Add('"MUIVerb"="' + (ConvertTo-RegString $script:MenuName) + '"')
            if ($context -in @("HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell", "HKEY_CURRENT_USER\Software\Classes\DesktopBackground\shell")) {
                $lines.Add("")
                $lines.Add("[" + $key + "\command]")
                $lines.Add('@="' + (ConvertTo-RegString (Get-AppCommand)) + '"')
                $lines.Add("")
            }
            else {
                $lines.Add('"subcommands"=""')
                $lines.Add("")

                foreach ($item in $script:MenuItems) {
                    $itemKey = $key + "\shell\" + $item.Id
                    $lines.Add("[" + $itemKey + "]")
                    $lines.Add('"MUIVerb"="' + (ConvertTo-RegString $item.Label) + '"')
                    $lines.Add("")
                    $lines.Add("[" + $itemKey + "\command]")
                    $lines.Add('@="' + (ConvertTo-RegString (Get-MenuCommand $item.Mode)) + '"')
                    $lines.Add("")
                }
            }
        }
    }

    return $lines.ToArray()
}

function Import-RegistryLines {
    param([string[]]$Lines, [string]$TemporaryRoot)

    $path = Join-Path $TemporaryRoot "Xfer.reg"
    [IO.File]::WriteAllLines($path, $Lines, [Text.Encoding]::Unicode)
    return (Invoke-RegExe -CommandArguments @("import", $path)) -eq 0
}

function Invoke-Install {
    $temporaryRoot = Join-Path $env:TEMP (
        "Xfer_install_" + [Guid]::NewGuid().ToString("N"))
    try {
        if (-not (Test-Path -LiteralPath $script:Launcher -PathType Leaf)) {
            throw "Context menu launcher not found: $script:Launcher"
        }
        if (-not (Test-Path -LiteralPath $script:XferScript -PathType Leaf)) {
            throw "Xfer launcher not found: $script:XferScript"
        }

        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        if (-not (Import-RegistryLines (Build-RegistryLines $true) $temporaryRoot)) {
            throw "Registry import failed."
        }

        Write-Host ""
        Write-Host '  Xfer was added to the right-click menu of files, folders, folder backgrounds and the desktop.' -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host ""
        Write-Host ('  Error: ' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }
    finally {
        $registryFile = Join-Path $temporaryRoot "Xfer.reg"
        if (Test-Path -LiteralPath $registryFile -PathType Leaf) {
            Remove-Item -LiteralPath $registryFile -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
            Remove-Item -LiteralPath $temporaryRoot -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($MyInvocation.InvocationName -ne ".") {
    if (-not (Invoke-Install)) { exit 1 }
    exit 0
}
