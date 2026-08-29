param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishRoot = (Resolve-Path $PublishDirectory).Path
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vrcli-installer-test-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $temporaryRoot 'payload'
$archive = Join-Path $temporaryRoot 'VRCLI-test-win-x64.zip'
$installRoot = Join-Path $temporaryRoot 'installed'

try {
    Copy-Item -LiteralPath $publishRoot -Destination $payload -Recurse
    Copy-Item -LiteralPath (Join-Path $repository 'scripts\install-vrcli.ps1') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $repository 'scripts\uninstall-vrcli.ps1') -Destination $payload
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash

    & (Join-Path $repository 'scripts\install-vrcli.ps1') `
        -SourceArchive $archive `
        -ExpectedSha256 $hash `
        -InstallDirectory $installRoot `
        -NoPathUpdate

    $executable = Join-Path $installRoot 'VRCLI.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw 'The installed executable is missing.'
    }
    & $executable --help | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The installed executable returned $LASTEXITCODE."
    }

    & (Join-Path $installRoot 'uninstall-vrcli.ps1') `
        -InstallDirectory $installRoot `
        -NoPathUpdate
    if (Test-Path -LiteralPath $installRoot) {
        throw 'The uninstall script did not remove the install directory.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
