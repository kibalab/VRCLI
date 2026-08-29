[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\VRCLI'),
    [switch]$NoPathUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installRoot = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$driveRoot = [System.IO.Path]::GetPathRoot($installRoot).TrimEnd('\')
$blocked = @(
    $driveRoot,
    [System.IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\'),
    [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\'),
    [System.IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
)
if ($blocked -contains $installRoot) {
    throw "Refusing to remove a broad directory: $installRoot"
}

if (-not $NoPathUpdate) {
    $current = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $current) {
        $current = ''
    }
    $entries = @($current -split ';' | Where-Object {
        if ([string]::IsNullOrWhiteSpace($_)) {
            return $false
        }
        [System.IO.Path]::GetFullPath($_).TrimEnd('\') -ne $installRoot
    })
    [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
}

if ((Get-Location).Path.StartsWith($installRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Set-Location ([System.IO.Path]::GetTempPath())
}
if (Test-Path -LiteralPath $installRoot) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

Write-Host "VRCLI was removed from $installRoot"
