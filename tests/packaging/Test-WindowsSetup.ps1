param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vrcli-setup-test-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $temporaryRoot 'output'
$installRoot = Join-Path $temporaryRoot 'installed'

try {
    $installer = & (Join-Path $repository 'scripts\build-windows-installer.ps1') `
        -Version '0.0.0' `
        -PublishDirectory $PublishDirectory `
        -OutputDirectory $output

    $install = Start-Process -FilePath $installer -Wait -PassThru -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        "/DIR=$installRoot",
        '/TASKS=""'
    )
    if ($install.ExitCode -ne 0) {
        throw "The installer returned $($install.ExitCode)."
    }

    $executable = Join-Path $installRoot 'VRCLI.exe'
    & $executable --help | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "The installed executable returned $LASTEXITCODE."
    }

    $uninstaller = Join-Path $installRoot 'unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -Wait -PassThru -ArgumentList @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART'
    )
    if ($uninstall.ExitCode -ne 0) {
        throw "The uninstaller returned $($uninstall.ExitCode)."
    }
    if (Test-Path -LiteralPath $installRoot) {
        throw 'The uninstaller did not remove the installation.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
