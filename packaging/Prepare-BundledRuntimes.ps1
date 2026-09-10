param(
    [Parameter(Mandatory = $true)]
    [string[]] $PublishDirectories
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packages = @(
    [pscustomobject]@{
        Key = 'php'
        DisplayName = 'PHP FastCGI'
        Version = '8.5.10'
        Url = 'https://downloads.php.net/~windows/releases/archives/php-8.5.10-nts-Win32-vs17-x64.zip'
        ExpectedSha256 = '22ec430195984d233eb9e62c637a945bbcda06efca2f392d9d96d62c6acd34f8'
        ExpectedMd5 = $null
        ArchiveRoot = $null
        ExecutableRelativePath = 'php-cgi.exe'
    },
    [pscustomobject]@{
        Key = 'nginx'
        DisplayName = 'Nginx'
        Version = '1.31.5'
        Url = 'https://nginx.org/download/nginx-1.31.5.zip'
        ExpectedSha256 = '00ad32a2bf66cee0ec8eb194347e8e79917f47017ccd3ad4bebf5574fabe002c'
        ExpectedMd5 = $null
        ArchiveRoot = 'nginx-1.31.5'
        ExecutableRelativePath = 'nginx.exe'
    },
    [pscustomobject]@{
        Key = 'mysql'
        DisplayName = 'MySQL'
        Version = '8.4.11'
        Url = 'https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip'
        ExpectedSha256 = $null
        ExpectedMd5 = '2e833921898a9a030ea6bfe81bd811bc'
        ArchiveRoot = 'mysql-8.4.11-winx64'
        ExecutableRelativePath = 'bin\mysqld.exe'
    }
)

foreach ($publishDirectory in $PublishDirectories) {
    if (-not (Test-Path -LiteralPath $publishDirectory -PathType Container)) {
        throw "Publish directory does not exist: $publishDirectory"
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devbox-bundled-runtimes-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $manifest = @()

    foreach ($package in $packages) {
        Write-Host "Preparing $($package.DisplayName) $($package.Version)..."

        if ([string]::IsNullOrWhiteSpace($package.ExpectedSha256) -and [string]::IsNullOrWhiteSpace($package.ExpectedMd5)) {
            throw "No pinned source digest is configured for $($package.DisplayName) $($package.Version)."
        }

        $archivePath = Join-Path $tempRoot "$($package.Key)-$($package.Version).zip"
        $extractPath = Join-Path $tempRoot "$($package.Key)-extract"

        Invoke-WebRequest -Uri $package.Url -OutFile $archivePath -UseBasicParsing

        $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace($package.ExpectedSha256) -and
            $actualSha256 -ne $package.ExpectedSha256.ToLowerInvariant()) {
            throw "SHA-256 mismatch for $($package.DisplayName) $($package.Version). Expected $($package.ExpectedSha256), got $actualSha256."
        }

        $actualMd5 = (Get-FileHash -LiteralPath $archivePath -Algorithm MD5).Hash.ToLowerInvariant()
        if (-not [string]::IsNullOrWhiteSpace($package.ExpectedMd5) -and
            $actualMd5 -ne $package.ExpectedMd5.ToLowerInvariant()) {
            throw "MD5 mismatch for $($package.DisplayName) $($package.Version). Expected $($package.ExpectedMd5), got $actualMd5."
        }

        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
        $sourcePath = if ([string]::IsNullOrWhiteSpace($package.ArchiveRoot)) {
            $extractPath
        } else {
            Join-Path $extractPath $package.ArchiveRoot
        }

        if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
            throw "Expected archive root was not found for $($package.DisplayName): $sourcePath"
        }

        $sourceExecutable = Join-Path $sourcePath $package.ExecutableRelativePath
        if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
            throw "Expected runtime executable was not found for $($package.DisplayName): $sourceExecutable"
        }

        foreach ($publishDirectory in $PublishDirectories) {
            $runtimeRoot = Join-Path $publishDirectory (Join-Path 'runtime' $package.Key)
            $versionPath = Join-Path $runtimeRoot $package.Version

            if (Test-Path -LiteralPath $versionPath) {
                Remove-Item -LiteralPath $versionPath -Recurse -Force
            }

            New-Item -ItemType Directory -Path $versionPath -Force | Out-Null
            Get-ChildItem -LiteralPath $sourcePath -Force | Copy-Item -Destination $versionPath -Recurse -Force
            Set-Content -LiteralPath (Join-Path $versionPath '.devbox-version') -Value $package.Version -Encoding ascii -NoNewline
        }

        $sourceIntegrityAlgorithm = if (-not [string]::IsNullOrWhiteSpace($package.ExpectedSha256)) { 'SHA-256' } else { 'MD5' }
        $sourceIntegrityDigest = if (-not [string]::IsNullOrWhiteSpace($package.ExpectedSha256)) { $package.ExpectedSha256.ToLowerInvariant() } else { $package.ExpectedMd5.ToLowerInvariant() }

        $manifest += [ordered]@{
            key = $package.Key
            displayName = $package.DisplayName
            version = $package.Version
            sourceUrl = $package.Url
            sha256 = $actualSha256
            sourceIntegrityAlgorithm = $sourceIntegrityAlgorithm
            sourceIntegrityDigest = $sourceIntegrityDigest
            sourceIntegrityPinned = $true
            executableRelativePath = $package.ExecutableRelativePath.Replace('\', '/')
        }
    }

    foreach ($publishDirectory in $PublishDirectories) {
        $runtimeDirectory = Join-Path $publishDirectory 'runtime'
        New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
        $manifestPath = Join-Path $runtimeDirectory 'bundled-runtimes.json'
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        Write-Host "Bundled runtime manifest: $manifestPath"
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
