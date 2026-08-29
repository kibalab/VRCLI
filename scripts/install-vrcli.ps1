[CmdletBinding()]
param(
    [ValidatePattern('^(latest|\d+\.\d+\.\d+)$')]
    [string]$Version = 'latest',

    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\VRCLI'),

    [string]$SourceArchive,

    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256,

    [switch]$NoPathUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SafeInstallDirectory {
    param([string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $driveRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd('\')
    $blocked = @(
        $driveRoot,
        [System.IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\'),
        [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\'),
        [System.IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
    )

    if ($blocked -contains $fullPath) {
        throw "Refusing to use a broad install directory: $fullPath"
    }

    return $fullPath
}

function Add-UserPathEntry {
    param([string]$Directory)

    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $current) {
        $current = ''
    }
    $entries = @($current -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $exists = $entries | Where-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd('\') -eq $Directory
    }

    if (-not $exists) {
        $updated = (@($entries) + $Directory) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    }

    if (-not (($env:Path -split ';') -contains $Directory)) {
        $env:Path = $env:Path.TrimEnd(';') + ';' + $Directory
    }
}

$installRoot = Get-SafeInstallDirectory $InstallDirectory
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vrcli-install-' + [Guid]::NewGuid().ToString('N'))
$archivePath = $SourceArchive
$resolvedVersion = $Version

try {
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

    if ([string]::IsNullOrWhiteSpace($archivePath)) {
        if ($Version -eq 'latest') {
            $release = Invoke-RestMethod `
                -Uri 'https://api.github.com/repos/kibalab/VRCLI/releases/latest' `
                -Headers @{ Accept = 'application/vnd.github+json' } `
                -UserAgent 'kibalab.VRCLI-installer'
            $resolvedVersion = $release.tag_name.TrimStart('v')
        }

        $archiveName = "VRCLI-$resolvedVersion-win-x64.zip"
        $baseUrl = "https://github.com/kibalab/VRCLI/releases/download/v$resolvedVersion"
        $archivePath = Join-Path $temporaryRoot $archiveName
        $checksumPath = Join-Path $temporaryRoot 'SHA256SUMS.txt'
        Invoke-WebRequest -Uri "$baseUrl/$archiveName" -OutFile $archivePath -UserAgent 'kibalab.VRCLI-installer'
        Invoke-WebRequest -Uri "$baseUrl/SHA256SUMS.txt" -OutFile $checksumPath -UserAgent 'kibalab.VRCLI-installer'

        $checksumLine = Get-Content -LiteralPath $checksumPath | Where-Object {
            $_ -match ('\s' + [regex]::Escape($archiveName) + '$')
        } | Select-Object -First 1
        if (-not $checksumLine) {
            throw "$archiveName is missing from SHA256SUMS.txt."
        }
        $ExpectedSha256 = ($checksumLine -split '\s+')[0]
    }
    elseif ([string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        throw '-ExpectedSha256 is required with -SourceArchive.'
    }

    $archivePath = (Resolve-Path -LiteralPath $archivePath).Path
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedSha256) {
        throw "Archive checksum mismatch. Expected $ExpectedSha256 but received $actualHash."
    }

    $expandedRoot = Join-Path $temporaryRoot 'expanded'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $expandedRoot
    if (-not (Test-Path -LiteralPath (Join-Path $expandedRoot 'VRCLI.exe') -PathType Leaf)) {
        throw 'The archive does not contain VRCLI.exe.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $expandedRoot 'UnityBridge\package.json') -PathType Leaf)) {
        throw 'The archive does not contain the Unity bridge.'
    }

    $parent = Split-Path -Parent $installRoot
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    $staging = Join-Path $parent ('.vrcli-staging-' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('.vrcli-backup-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $expandedRoot -Destination $staging -Recurse

    try {
        if (Test-Path -LiteralPath $installRoot) {
            [System.IO.Directory]::Move($installRoot, $backup)
        }
        [System.IO.Directory]::Move($staging, $installRoot)
        if (Test-Path -LiteralPath $backup) {
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    }
    catch {
        if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $installRoot)) {
            [System.IO.Directory]::Move($backup, $installRoot)
        }
        throw
    }

    if (-not $NoPathUpdate) {
        Add-UserPathEntry $installRoot
    }

    Write-Host "VRCLI $resolvedVersion installed in $installRoot"
    if (-not $NoPathUpdate) {
        Write-Host "Open a new terminal, then run: vrcli --help"
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
