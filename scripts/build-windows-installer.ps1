[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$PublishDirectory,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($command) { $command.Source }),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    throw 'Inno Setup 6 compiler (ISCC.exe) was not found.'
}

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = (Resolve-Path $PublishDirectory).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$definition = Join-Path $repository 'packaging\windows\VRCLI.iss'

if (-not (Test-Path -LiteralPath (Join-Path $source 'VRCLI.exe') -PathType Leaf)) {
    throw 'VRCLI.exe is missing from the publish directory.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'UnityBridge\package.json') -PathType Leaf)) {
    throw 'The Unity bridge is missing from the publish directory.'
}

[System.IO.Directory]::CreateDirectory($output) | Out-Null
& $IsccPath "/DAppVersion=$Version" "/DSourceDir=$source" "/DOutputDir=$output" $definition |
    ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe returned $LASTEXITCODE."
}

$installer = Join-Path $output "VRCLI-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'Inno Setup did not create the expected installer.'
}
Write-Output $installer
