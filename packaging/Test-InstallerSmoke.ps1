[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstallerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$testRoot = Join-Path $env:RUNNER_TEMP ("devbox-installer-smoke-" + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $testRoot 'DevBox'
$runtimeRoot = Join-Path $testRoot 'runtime-root'
$logDir = Join-Path $testRoot 'logs'

New-Item -ItemType Directory -Force $testRoot, $logDir | Out-Null

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,
        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$Description failed with exit code $($process.ExitCode)."
    }
}

function Invoke-Setup {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LogName
    )

    $setupLog = Join-Path $logDir $LogName
    Invoke-CheckedProcess -FilePath $installer -Description "DevBox Setup ($LogName)" -Arguments @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        "/DIR=`"$installDir`"",
        '/MERGETASKS="!desktopicon,!startmenuicon,!launchafterinstall"',
        "/LOG=`"$setupLog`""
    )

    $gui = Join-Path $installDir 'DevBox.exe'
    $cli = Join-Path $installDir 'cli\devbox.exe'
    $uninstaller = Join-Path $installDir 'unins000.exe'
    foreach ($required in @($gui, $cli, $uninstaller)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Installer did not create required file: $required"
        }
    }
}

function Invoke-InstalledCliHelp {
    $cli = Join-Path $installDir 'cli\devbox.exe'
    $oldRoot = $env:DEVBOX_ROOT
    try {
        $env:DEVBOX_ROOT = $runtimeRoot
        & $cli --help | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Installed CLI --help failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        $env:DEVBOX_ROOT = $oldRoot
    }
}

function Wait-UntilMissing {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [int] $TimeoutSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ((Test-Path -LiteralPath $Path) -and ([DateTime]::UtcNow -lt $deadline)) {
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path -LiteralPath $Path) {
        throw "Path was not removed within $TimeoutSeconds seconds: $Path"
    }
}

function Invoke-Uninstall {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "DevBox uninstaller was not found: $uninstaller"
    }

    Invoke-CheckedProcess -FilePath $uninstaller -Description 'DevBox uninstall' -Arguments @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART'
    )

    Wait-UntilMissing -Path (Join-Path $installDir 'DevBox.exe')
}

try {
    # Clean installation.
    Invoke-Setup -LogName 'install.log'
    Invoke-InstalledCliHelp

    # User-owned content must survive a same-version reinstall, while DevBox-owned
    # downloaded runtime/temp payloads must be removed by the reinstall path.
    $userProject = Join-Path $installDir 'www\user-project'
    New-Item -ItemType Directory -Force $userProject | Out-Null
    $userSentinel = Join-Path $userProject 'keep.txt'
    Set-Content -LiteralPath $userSentinel -Value 'preserve-user-project' -NoNewline

    $runtimeSentinel = Join-Path $installDir 'runtime\smoke\remove.txt'
    New-Item -ItemType Directory -Force (Split-Path -Parent $runtimeSentinel) | Out-Null
    Set-Content -LiteralPath $runtimeSentinel -Value 'remove-runtime' -NoNewline

    $tempRuntimeSentinel = Join-Path $installDir 'tmp\runtimes\smoke\remove.txt'
    New-Item -ItemType Directory -Force (Split-Path -Parent $tempRuntimeSentinel) | Out-Null
    Set-Content -LiteralPath $tempRuntimeSentinel -Value 'remove-temp-runtime' -NoNewline

    # Running the same-version installer selects the installer maintenance
    # "Reinstall" path by default. This exercises its uninstaller + managed cleanup
    # + reinstall transaction rather than merely copying files over the old install.
    Invoke-Setup -LogName 'reinstall.log'

    if (-not (Test-Path -LiteralPath $userSentinel -PathType Leaf)) {
        throw 'Same-version reinstall removed user-owned project content.'
    }
    if ((Get-Content -LiteralPath $userSentinel -Raw) -ne 'preserve-user-project') {
        throw 'Same-version reinstall modified user-owned project content.'
    }
    if (Test-Path -LiteralPath $runtimeSentinel) {
        throw 'Same-version reinstall did not remove DevBox-owned runtime payloads.'
    }
    if (Test-Path -LiteralPath $tempRuntimeSentinel) {
        throw 'Same-version reinstall did not remove DevBox-owned temporary runtime payloads.'
    }

    Invoke-InstalledCliHelp

    # Final uninstall must remove the application and DevBox-owned modules while
    # preserving user-owned project files left inside the selected DevBox root.
    Invoke-Uninstall

    if (-not (Test-Path -LiteralPath $userSentinel -PathType Leaf)) {
        throw 'Uninstall removed user-owned project content.'
    }
    if (Test-Path -LiteralPath (Join-Path $installDir 'DevBox.exe')) {
        throw 'Uninstall left DevBox.exe behind.'
    }
}
finally {
    $uninstaller = Join-Path $installDir 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait | Out-Null
        }
        catch {
            Write-Warning "Cleanup uninstall failed: $($_.Exception.Message)"
        }
    }

    try {
        if (Test-Path -LiteralPath $testRoot) {
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        }
    }
    catch {
        Write-Warning "Smoke-test cleanup failed: $($_.Exception.Message)"
    }
}
