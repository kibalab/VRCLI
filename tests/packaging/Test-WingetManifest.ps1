Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('vrcli-winget-test-' + [Guid]::NewGuid().ToString('N'))
$hash = '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF'

try {
    $manifestRoot = & (Join-Path $repository 'scripts\new-winget-manifest.ps1') `
        -Version '1.2.3' `
        -InstallerSha256 $hash `
        -OutputDirectory $temporaryRoot `
        -ReleaseDate '2026-08-29'

    $files = @(Get-ChildItem -LiteralPath $manifestRoot -Filter '*.yaml' -File)
    if ($files.Count -ne 5) {
        throw "Expected five WinGet manifests but found $($files.Count)."
    }
    $combined = $files | Get-Content -Raw
    if (($combined -join "`n") -match '@[A-Z_]+@') {
        throw 'A generated WinGet manifest contains an unresolved token.'
    }
    $installer = Get-Content -LiteralPath (Join-Path $manifestRoot 'kibalab.VRCLI.installer.yaml') -Raw
    foreach ($expected in @(
        'PackageIdentifier: kibalab.VRCLI',
        'PackageVersion: 1.2.3',
        "InstallerSha256: $hash",
        'VRCLI-1.2.3-win-x64-setup.exe',
        'ReleaseDate: 2026-08-29'
    )) {
        if (-not $installer.Contains($expected)) {
            throw "Generated installer manifest is missing: $expected"
        }
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($winget) {
        & $winget.Source validate $manifestRoot --disable-interactivity
        if ($LASTEXITCODE -ne 0) {
            throw "winget validate returned $LASTEXITCODE."
        }
    }
    else {
        Write-Warning 'winget.exe is unavailable; schema validation was skipped.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
