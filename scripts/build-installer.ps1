param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = 'Stop'
$compiler = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
if (!$compiler) {
    $compiler = @(
        "$env:ProgramFiles\NSIS\makensis.exe",
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$compiler) { throw 'Install NSIS or put makensis.exe on PATH.' }
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
& $compiler "/DManagerVersion=$Version" "/DPackageDir=$package" "/DOutputFile=$output\FlyingThumbManager-Setup.exe" "$PSScriptRoot\FlyingThumbManager.nsi"
if ($LASTEXITCODE -ne 0) { throw "Installer compiler failed ($LASTEXITCODE)." }
