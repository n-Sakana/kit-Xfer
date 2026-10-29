[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$XferArguments
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$startupFailureExitCode = 3
$script:launcherRun = [Guid]::NewGuid().ToString('N')
$script:launcherClock = [Diagnostics.Stopwatch]::StartNew()
$script:launcherSequence = 0
$script:launcherStage = 'launcher.arguments'

function Write-XferLauncherLog {
    param(
        [string]$Event,
        [hashtable]$Data = @{}
    )

    try {
        $localData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData,
            [Environment+SpecialFolderOption]::DoNotVerify)
        if (-not $env:Xfer_LOG_DIR -and [string]::IsNullOrWhiteSpace($localData)) {
            return
        }

        $logDirectory = if ($env:Xfer_LOG_DIR) { $env:Xfer_LOG_DIR } else { Join-Path $localData 'Xfer\logs' }
        if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }

        $logPath = Join-Path $logDirectory 'launcher.jsonl'
        if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).Length -ge 2MB) {
            Move-Item -LiteralPath $logPath -Destination ($logPath + '.previous') -Force
        }
        $script:launcherSequence++
        $line = @{ schema = 1; utc = [DateTime]::UtcNow.ToString('O'); monoMs = $script:launcherClock.ElapsedMilliseconds
            run = $script:launcherRun; seq = $script:launcherSequence; route = 'launcher'; event = $Event
            correlation = [string]$PID; data = $Data } | ConvertTo-Json -Compress -Depth 4
        [IO.File]::AppendAllText(
            $logPath,
            $line + [Environment]::NewLine,
            (New-Object Text.UTF8Encoding($false)))
    }
    catch {
    }
}

function Get-XferRuntimeKey {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        return 'netfx48-win'
    }

    $platform = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') { 'win' } else { 'unix' }
    return 'net{0}-{1}' -f [Environment]::Version.Major, $platform
}

function Get-XferCacheBase {
    $cacheBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($cacheBase)) {
        return $cacheBase
    }

    if (-not [string]::IsNullOrWhiteSpace($env:XDG_CACHE_HOME)) {
        return $env:XDG_CACHE_HOME
    }

    $profileFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($profileFolder)) {
        throw 'Xfer could not find a per-user cache folder.'
    }

    return Join-Path $profileFolder '.cache'
}

function Get-XferSourceHash {
    param(
        [System.IO.FileInfo[]]$SourceFiles,
        [string]$RuntimeKey
    )

    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $prefix = "ferry-add-type-cache-v1" + [char]0 + $RuntimeKey + [char]0
        $header = [Text.Encoding]::UTF8.GetBytes($prefix)
        [void]$hash.TransformBlock($header, 0, $header.Length, $header, 0)

        foreach ($file in $SourceFiles) {
            $name = [Text.Encoding]::UTF8.GetBytes($file.Name + [char]0)
            [void]$hash.TransformBlock($name, 0, $name.Length, $name, 0)

            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            if ($bytes.Length -gt 0) {
                [void]$hash.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
            }
        }

        $empty = New-Object byte[] 0
        [void]$hash.TransformFinalBlock($empty, 0, 0)
        return ([BitConverter]::ToString($hash.Hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-XferCombinedSource {
    param([System.IO.FileInfo[]]$SourceFiles)

    $combined = ($SourceFiles | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    }) -join [Environment]::NewLine

    $usingPattern = '(?m)^\s*using\s+[A-Za-z_][A-Za-z0-9_.]*\s*;\s*$'
    $usings = [regex]::Matches($combined, $usingPattern) |
        ForEach-Object { $_.Value.Trim() } |
        Sort-Object -Unique
    $body = [regex]::Replace($combined, $usingPattern, '')
    return ($usings -join [Environment]::NewLine) +
        [Environment]::NewLine + [Environment]::NewLine + $body
}

# The Xfer window: MiniUI's engine (runtime\windows-x64) draws ui\index.html, and ui\MiniUI.App.dll beside it
# runs Xfer (local server for the phone, QR, camera, file dialogs). Nothing is compiled here.
function Invoke-XferWindow {
    param([string[]]$Arguments)

    $script:launcherStage = 'native.check_runtime'
    $runtime = Join-Path $PSScriptRoot 'runtime\windows-x64'
    $ui = Join-Path $PSScriptRoot 'ui'
    foreach ($file in @('runtime\windows-x64\MiniUI.Bridge.dll', 'runtime\windows-x64\MiniUI.Engine.dll', 'ui\MiniUI.App.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $file) -PathType Leaf)) {
            throw "Xfer could not find $file."
        }
    }

    # Windows PowerShell does not load a .NET assembly marked as downloaded (Mark of the Web, as in a copy
    # extracted from a downloaded zip). As the previous launcher did for WebView2, Xfer then uses its own
    # copy of the two MiniUI files without the mark.
    $bridge = Join-Path $runtime 'MiniUI.Bridge.dll'
    $zone = 0
    try { $zone = [int][Security.Policy.Zone]::CreateFromUrl('file://' + $bridge).SecurityZone } catch { }
    if ($zone -ge 3) {
        $script:launcherStage = 'native.unmarked_runtime'
        $runtime = Get-XferUnmarkedRuntime -Runtime $runtime
        $bridge = Join-Path $runtime 'MiniUI.Bridge.dll'
    }

    $script:launcherStage = 'native.state_directory'
    $stateDirectory = Join-Path ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)) 'Xfer\miniui'
    [void](New-Item -ItemType Directory -Path $stateDirectory -Force)

    $script:launcherStage = 'native.load_bridge'
    [void][Reflection.Assembly]::LoadFrom($bridge)
    $appArguments = @('--home', $PSScriptRoot) + @($Arguments)
    $json = '{"root":' + (ConvertTo-Json -InputObject ([string]$ui) -Compress) +
        ',"stateDir":' + (ConvertTo-Json -InputObject ([string]$stateDirectory) -Compress) +
        ',"title":"Xfer","width":1180,"height":800,"arguments":[' +
        ((@($appArguments) | ForEach-Object { ConvertTo-Json -InputObject ([string]$_) -Compress }) -join ',') + ']}'
    $script:launcherStage = 'native.run_bridge'
    try {
        return [MiniUI.Bridge]::Run((Join-Path $ui 'index.html'), $json)
    }
    finally {
        # Precompiled with the distribution. Do not add compilation to ordinary startup or exit.
        try {
            $shutdownBridge = Join-Path $runtime 'Xfer.Launcher.dll'
            if (Test-Path -LiteralPath $shutdownBridge -PathType Leaf) {
                [void][Reflection.Assembly]::LoadFrom($shutdownBridge)
                $shutdownCode = [Xfer.Launcher.NativeShutdown]::Finish((Join-Path $ui 'MiniUI.App.dll'), 2000)
                Write-XferLauncherLog 'launcher.native_shutdown' @{ code = $shutdownCode; site = 'Invoke-XferWindow.finally' }
            }
            else {
                Write-XferLauncherLog 'launcher.native_shutdown' @{ code = -3; site = 'Invoke-XferWindow.finally' }
            }
        }
        catch {
            Write-XferLauncherLog 'launcher.native_shutdown.failed' @{ type = $_.Exception.GetType().Name; code = $_.Exception.HResult
                site = 'Invoke-XferWindow.finally'; line = $_.InvocationInfo.ScriptLineNumber }
        }
    }
}

# Runtime DLLs rewritten under %LOCALAPPDATA%\Xfer\runtime: a file written from
# bytes has no zone mark (Copy-Item would copy the mark). One folder per set of files.
function Get-XferUnmarkedRuntime {
    param([string]$Runtime)

    $names = @('MiniUI.Bridge.dll', 'MiniUI.Engine.dll')
    if (Test-Path -LiteralPath (Join-Path $Runtime 'Xfer.Launcher.dll') -PathType Leaf) { $names += 'Xfer.Launcher.dll' }
    $key = ($names | ForEach-Object {
        $item = Get-Item -LiteralPath (Join-Path $Runtime $_)
        '{0}:{1}:{2}' -f $_, $item.Length, $item.LastWriteTimeUtc.Ticks
    }) -join '|'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $id = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key)))).Replace('-', '').Substring(0, 16).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    $target = Join-Path (Get-XferCacheBase) ('Xfer\runtime\' + $id)
    $ready = Join-Path $target 'complete'
    if (-not (Test-Path -LiteralPath $ready -PathType Leaf)) {
        [void](New-Item -ItemType Directory -Path $target -Force)
        foreach ($name in $names) {
            $source = Join-Path $Runtime $name
            $copy = Join-Path $target $name
            try {
                [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes($source))
            }
            catch [IO.IOException] {
                # Another Xfer started at the same moment and already uses the copy.
                if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
                    (Get-Item -LiteralPath $copy).Length -ne (Get-Item -LiteralPath $source).Length) {
                    throw
                }
            }
        }
        [IO.File]::WriteAllText($ready, $key, [Text.Encoding]::ASCII)
    }
    return $target
}

# --no-browser runs only the local HTTP server: the C# sources are compiled by this PowerShell.
function Invoke-XferServer {
    param([string[]]$Arguments)

    $script:launcherStage = 'server.sources'
    $isWindowsPowerShell = $PSVersionTable.PSEdition -eq 'Desktop'
    $sourceDirectory = Join-Path $PSScriptRoot 'src'
    $sourceFiles = if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
        @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File -Recurse | Sort-Object FullName)
    }
    else {
        @()
    }
    if ($sourceFiles.Count -eq 0) {
        throw "No C# source files were found under src."
    }

    $webDirectory = Join-Path $PSScriptRoot 'web'
    $webFiles = if (Test-Path -LiteralPath $webDirectory -PathType Container) {
        @(Get-ChildItem -LiteralPath $webDirectory -File | Sort-Object Name)
    }
    else {
        @()
    }
    $libraryDirectory = Join-Path $PSScriptRoot 'lib'
    $zxingPath = Join-Path $libraryDirectory 'zxing.dll'
    if (-not (Test-Path -LiteralPath $zxingPath -PathType Leaf)) {
        throw "Xfer could not find lib\zxing.dll."
    }

    $script:launcherStage = 'server.cache'
    $buildFiles = @($sourceFiles) + @($webFiles) + @(Get-Item -LiteralPath $zxingPath)
    $runtimeKey = Get-XferRuntimeKey
    $sourceHash = Get-XferSourceHash -SourceFiles $buildFiles -RuntimeKey $runtimeKey
    $cacheRoot = Join-Path (Get-XferCacheBase) 'Xfer\add-type-cache-v1'
    $cacheDirectory = Join-Path (Join-Path $cacheRoot $runtimeKey) $sourceHash
    $assemblyPath = Join-Path $cacheDirectory 'Xfer.Host.dll'
    $readyPath = Join-Path $cacheDirectory 'complete'

    if (-not ((Test-Path -LiteralPath $assemblyPath -PathType Leaf) -and
              (Test-Path -LiteralPath $readyPath -PathType Leaf))) {
        [void](New-Item -ItemType Directory -Path $cacheDirectory -Force)
        $temporaryAssembly = Join-Path $cacheDirectory ("Xfer.Host.{0}.tmp.dll" -f $PID)

        try {
            $script:launcherStage = 'server.compile'
            Write-Host ("Compiling Xfer C# for {0}..." -f $runtimeKey)
            $source = Get-XferCombinedSource -SourceFiles $sourceFiles
            if ($isWindowsPowerShell) {
                Add-Type -AssemblyName System.IO.Compression
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                $references = @(
                    'System.Drawing'
                    'System.IO.Compression'
                    'System.IO.Compression.FileSystem'
                    'System.Xml'
                    $zxingPath
                )
                Add-Type -TypeDefinition $source `
                    -OutputAssembly $temporaryAssembly `
                    -OutputType Library `
                    -ReferencedAssemblies $references
            }
            else {
                $referenceDirectory = Join-Path $PSHOME 'ref'
                if (-not (Test-Path -LiteralPath $referenceDirectory -PathType Container)) {
                    throw "PowerShell reference assemblies were not found: $referenceDirectory"
                }
                $frameworkAssemblies = @(
                    Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' -File |
                        Select-Object -ExpandProperty FullName)
                $coreSource = "#define NET9_0_OR_GREATER`n" + $source
                Add-Type -TypeDefinition $coreSource `
                    -OutputAssembly $temporaryAssembly `
                    -OutputType Library `
                    -ReferencedAssemblies @($frameworkAssemblies + $zxingPath)
            }

            $script:launcherStage = 'server.save_assembly'
            if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
                Remove-Item -LiteralPath $assemblyPath -Force
            }
            Move-Item -LiteralPath $temporaryAssembly -Destination $assemblyPath
            [IO.File]::WriteAllText($readyPath, $sourceHash, [Text.Encoding]::ASCII)
        }
        finally {
            if (Test-Path -LiteralPath $temporaryAssembly -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryAssembly -Force
            }
        }
    }
    else {
        Write-Host ("Using cached Xfer C# for {0}." -f $runtimeKey)
    }

    $script:launcherStage = 'server.load_dependency'
    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($zxingPath))
    $script:launcherStage = 'server.load_assembly'
    [void][Reflection.Assembly]::LoadFrom($assemblyPath)
    $env:Xfer_BUILD_ID = $sourceHash
    $script:launcherStage = 'server.run'
    return [Xfer.Program]::Run($PSScriptRoot, [string[]]$Arguments)
}

Write-XferLauncherLog 'launcher.start' @{ psVersion = $PSVersionTable.PSVersion.ToString(); pid = $PID }
try {
    $arguments = @($XferArguments | Where-Object { $null -ne $_ })
    $onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    if (-not $onWindows -or $arguments -contains '--no-browser' -or $arguments -contains '--help' -or $arguments -contains '-h') {
        $serverExit = Invoke-XferServer -Arguments $arguments
        Write-XferLauncherLog 'launcher.exit' @{ code = $serverExit }
        exit $serverExit
    }

    $exitCode = Invoke-XferWindow -Arguments $arguments
    if ($exitCode -ne 0) {
        Write-XferLauncherLog 'launcher.failed' @{ code = $exitCode; stage = $script:launcherStage; site = 'Invoke-XferWindow' }
        exit $startupFailureExitCode
    }
    Write-XferLauncherLog 'launcher.exit' @{ code = 0 }
    exit 0
}
catch {
    $location = ''
    if ($null -ne $_.InvocationInfo) {
        $location = ' (' + $_.InvocationInfo.ScriptName + ':' +
            $_.InvocationInfo.ScriptLineNumber + ')'
    }
    $detail = 'launcher failed' + $location + ' ' + $_.Exception.ToString()
    Write-XferLauncherLog 'launcher.failed' @{ type = $_.Exception.GetType().Name; code = $_.Exception.HResult; stage = $script:launcherStage
        site = [IO.Path]::GetFileName($_.InvocationInfo.ScriptName); line = $_.InvocationInfo.ScriptLineNumber
        causeType = $_.Exception.GetBaseException().GetType().Name; causeCode = $_.Exception.GetBaseException().HResult }
    [Console]::Error.WriteLine('Xfer: ' + $detail)
    exit $startupFailureExitCode
}
