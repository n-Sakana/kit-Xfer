# common/open-context.ps1 -- reuse a running Xfer or start it from Explorer.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("app", "optical")]
    [string]$Mode,
    [string]$Target
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
$script:XferRoot = Split-Path $PSScriptRoot -Parent
$script:StatusUri = "http://127.0.0.1:18422/api/status"
$script:ActivateUri = "http://127.0.0.1:18422/api/activate"

function Get-HttpStatusCode {
    param($ErrorRecord)
    if ($null -eq $ErrorRecord -or $null -eq $ErrorRecord.Exception -or
        $null -eq $ErrorRecord.Exception.Response) {
        return 0
    }

    try {
        return [int]$ErrorRecord.Exception.Response.StatusCode
    }
    catch {
        return 0
    }
}

function Get-RunningXfer {
    try {
        $status = Invoke-RestMethod -Method Get -Uri $script:StatusUri -TimeoutSec 2
        if ($null -eq $status -or $status.application -ne "Xfer" -or $status.device -isnot [string] -or
            $status.platform -isnot [string] -or $status.version -isnot [string] -or
            $null -eq $status.capabilities -or [int]$status.processId -lt 1) {
            return $null
        }
        return $status
    }
    catch {
        return $null
    }
}

function Invoke-RunningXfer {
    param($Status, [string]$Mode, [string]$Path)

    $request = @{}
    if ($Mode -eq "optical") {
        $request["mode"] = "optical"
        $request["path"] = $Path
    }
    $body = ConvertTo-Json $request -Compress
    try {
        $result = Invoke-RestMethod `
            -Method Post `
            -Uri $script:ActivateUri `
            -ContentType "application/json; charset=utf-8" `
            -Headers @{ "X-Xfer-Process-Id" = [string]$Status.processId } `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) `
            -TimeoutSec 5
        return $null -ne $result -and [bool]$result.activated
    }
    catch {
        $statusCode = Get-HttpStatusCode $_
        if ($statusCode -eq 404 -or $statusCode -eq 409 -or
            $statusCode -eq 503) {
            return $false
        }
        throw
    }
}

try {
    $targetPath = $null
    if ($Mode -eq "optical") {
        if ([string]::IsNullOrWhiteSpace($Target)) {
            throw "The transfer target is missing."
        }
        # A quoted root path ending in '\' can arrive with the final slash changed
        # to '"'. Windows paths cannot contain '"', so restoring it is unambiguous.
        if ($Target.EndsWith('"')) {
            $Target = $Target.Substring(0, $Target.Length - 1) + "\"
        }

        $targetPath = [IO.Path]::GetFullPath($Target)
        if (-not (Test-Path -LiteralPath $targetPath)) {
            throw "Selected item not found: $targetPath"
        }
    }

    $running = Get-RunningXfer
    if ($null -ne $running) {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            if (Invoke-RunningXfer $running $Mode $targetPath) {
                # Started by the click in Explorer, this process may give the foreground to the Xfer window;
                # Xfer asks for it too, but Windows refuses that to a window in the background.
                try {
                    Add-Type -AssemblyName Microsoft.VisualBasic
                    [Microsoft.VisualBasic.Interaction]::AppActivate([int]$running.processId)
                }
                catch {
                }
                exit 0
            }
            Start-Sleep -Milliseconds 250
            $latest = Get-RunningXfer
            if ($null -ne $latest) {
                $running = $latest
            }
        }
        throw "Could not activate the running Xfer desktop window."
    }

    $XferScript = Join-Path $script:XferRoot "Xfer.ps1"
    if ($Mode -eq "app") {
        & $XferScript
    }
    else {
        & $XferScript --path $targetPath --mode optical
    }
    exit $LASTEXITCODE
}
catch {
    try {
        $localData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)
        $logDirectory = Join-Path $localData "Xfer\logs"
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        $logPath = Join-Path $logDirectory (
            "Xfer_" + (Get-Date -Format "yyyyMMdd") + ".log")
        $message = "[" + (Get-Date -Format "HH:mm:ss") +
            "] [ERROR] context menu: " + $_.Exception.ToString()
        [IO.File]::AppendAllText(
            $logPath,
            $message + [Environment]::NewLine,
            (New-Object Text.UTF8Encoding($false)))
    }
    catch {
    }
    exit 1
}
