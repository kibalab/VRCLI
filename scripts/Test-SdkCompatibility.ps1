[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('World', 'Avatar')]
    [string] $ProjectType,

    [Parameter(Mandatory)]
    [string] $SdkVersion,

    [Parameter(Mandatory)]
    [string] $UnityPath,

    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable was not found: $UnityPath"
}
if (-not (Get-Command vpm -ErrorAction SilentlyContinue)) {
    throw 'The VRChat VPM CLI is not available on PATH.'
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workRoot = Join-Path $tempRoot ("vrcli-sdk-compat-" + [guid]::NewGuid().ToString('N'))
$resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRoot)
if (-not $resolvedWorkRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not ([System.IO.Path]::GetFileName($resolvedWorkRoot)).StartsWith('vrcli-sdk-compat-', [System.StringComparison]::Ordinal)) {
    throw "Refusing to use an unsafe compatibility workspace: $resolvedWorkRoot"
}
$projectName = "VRCLI-$ProjectType-$($SdkVersion.Replace('.', '-'))"
$projectPath = Join-Path $workRoot $projectName
$packageName = if ($ProjectType -eq 'World') { 'com.vrchat.worlds' } else { 'com.vrchat.avatars' }
$packageSpec = if ($SdkVersion -eq 'latest') { $packageName } else { "$packageName@$SdkVersion" }
$bridgeSource = Join-Path $RepositoryRoot 'Packages/com.kibalab.vrcli'
$bridgeDestination = Join-Path $projectPath 'Packages/com.kibalab.vrcli'

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    & vpm install templates
    if ($LASTEXITCODE -ne 0) { throw "VPM template installation failed with exit code $LASTEXITCODE." }

    & vpm new $projectName $ProjectType -p $workRoot
    if ($LASTEXITCODE -ne 0) { throw "VPM project creation failed with exit code $LASTEXITCODE." }

    & vpm add package $packageSpec -p $projectPath
    if ($LASTEXITCODE -ne 0) { throw "VPM could not install $packageSpec (exit code $LASTEXITCODE)." }

    Copy-Item -LiteralPath $bridgeSource -Destination $bridgeDestination -Recurse

    & $UnityPath -batchmode -quit -nographics -projectPath $projectPath -logFile -
    if ($LASTEXITCODE -ne 0) {
        throw "Unity compilation failed for $ProjectType SDK $SdkVersion (exit code $LASTEXITCODE)."
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedWorkRoot) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}
