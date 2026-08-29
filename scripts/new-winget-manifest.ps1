[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$InstallerSha256,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$ReleaseDate = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$templateRoot = Join-Path $repository 'packaging\winget'
$versionRoot = Join-Path ([System.IO.Path]::GetFullPath($OutputDirectory)) "manifests\k\kibalab\VRCLI\$Version"
[System.IO.Directory]::CreateDirectory($versionRoot) | Out-Null

$tokens = @{
    '@VERSION@' = $Version
    '@SHA256@' = $InstallerSha256.ToUpperInvariant()
    '@RELEASE_DATE@' = $ReleaseDate
}

Get-ChildItem -LiteralPath $templateRoot -Filter '*.yaml.template' -File | ForEach-Object {
    $content = Get-Content -LiteralPath $_.FullName -Raw
    foreach ($token in $tokens.GetEnumerator()) {
        $content = $content.Replace($token.Key, $token.Value)
    }
    if ($content -match '@[A-Z_]+@') {
        throw "An unresolved token remains in $($_.Name)."
    }

    $name = $_.Name.Substring(0, $_.Name.Length - '.template'.Length)
    $path = Join-Path $versionRoot $name
    [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
}

Write-Output $versionRoot
