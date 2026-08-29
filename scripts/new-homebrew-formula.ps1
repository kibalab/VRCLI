[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$Arm64Sha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$X64Sha256,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$Arm64Url = "https://github.com/kibalab/VRCLI/releases/download/v$Version/VRCLI-$Version-osx-arm64.tar.gz",

    [string]$X64Url = "https://github.com/kibalab/VRCLI/releases/download/v$Version/VRCLI-$Version-osx-x64.tar.gz"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$template = Join-Path $repository 'packaging\homebrew\vrcli.rb.template'
$destination = [System.IO.Path]::GetFullPath($OutputPath)
$parent = Split-Path -Parent $destination
if ($parent) {
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
}

$tokens = @{
    '@VERSION@' = $Version
    '@ARM64_SHA256@' = $Arm64Sha256.ToLowerInvariant()
    '@X64_SHA256@' = $X64Sha256.ToLowerInvariant()
    '@ARM64_URL@' = $Arm64Url
    '@X64_URL@' = $X64Url
}

$content = Get-Content -LiteralPath $template -Raw
foreach ($token in $tokens.GetEnumerator()) {
    $content = $content.Replace($token.Key, $token.Value)
}
if ($content -match '@[A-Z0-9_]+@') {
    throw 'An unresolved token remains in the Homebrew Formula.'
}

[System.IO.File]::WriteAllText($destination, $content, [System.Text.UTF8Encoding]::new($false))
Write-Output $destination
