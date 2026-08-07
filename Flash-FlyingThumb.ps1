param(
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'Flying Thumb Firmware Installer'
$projectDirectory = $PSScriptRoot
$logDirectory = Join-Path $projectDirectory 'dist'
$logPath = Join-Path $logDirectory 'flash-log.txt'

function Write-Heading([string]$Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('=' * $Text.Length) -ForegroundColor DarkCyan
}

function Stop-WithMessage([string]$Message, [int]$Code = 1) {
    Write-Host ''
    Write-Host $Message -ForegroundColor Red
    Write-Host "A detailed log is available at:`n$logPath" -ForegroundColor Yellow
    exit $Code
}

New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
"Flying Thumb flash started $(Get-Date -Format o)" | Set-Content -LiteralPath $logPath -Encoding utf8

Clear-Host
Write-Heading 'Flying Thumb Firmware Installer'
Write-Host 'This installer builds and transfers the current Flying Thumb firmware.'
Write-Host 'It does not erase files stored on the microSD card.'

try {
    $pythonVersion = & py -3 --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Python 3 is not installed or is not available through the py launcher.' }
    $platformVersion = & py -3 -m platformio --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'PlatformIO is not installed.' }
    Add-Content -LiteralPath $logPath -Value $pythonVersion
    Add-Content -LiteralPath $logPath -Value $platformVersion
}
catch {
    Stop-WithMessage ("Required flashing tools were not found. `n`n" + $_.Exception.Message + "`n`nInstall Python 3 and PlatformIO, or ask Codex to prepare this computer.") 2
}

if ($CheckOnly) {
    Write-Host ''
    Write-Host 'Flashing tools and project files are ready.' -ForegroundColor Green
    exit 0
}

Write-Heading 'Put the LILYGO into download mode'
Write-Host '1. Unplug the LILYGO from this computer.'
Write-Host '2. Press and hold the button on the LILYGO.'
Write-Host '3. While holding the button, plug it directly into this computer.'
Write-Host '4. Wait two seconds, then release the button.'
Write-Host ''
Write-Host 'When that is done, press ENTER here.' -ForegroundColor Yellow
[void](Read-Host)

Write-Heading 'Looking for the LILYGO'
try {
    $ports = & py -3 -m platformio device list 2>&1
    Add-Content -LiteralPath $logPath -Value $ports
    Write-Host 'Starting transfer. The first run can take several minutes.'
}
catch {
    Stop-WithMessage 'The connected-device check failed. Unplug the board and repeat the button-hold insertion sequence.' 3
}

Write-Heading 'Building and transferring Flying Thumb'
$arguments = @('-3', '-m', 'platformio', 'run', '--project-dir', $projectDirectory, '--environment', 't-dongle-s3', '--target', 'upload')
& py @arguments 2>&1 | Tee-Object -FilePath $logPath -Append
$uploadResult = $LASTEXITCODE

if ($uploadResult -ne 0) {
    Write-Host ''
    Write-Host 'Transfer failed.' -ForegroundColor Red
    Write-Host 'Most often, the board was not in download mode or another program had its port open.' -ForegroundColor Yellow
    Write-Host 'Close serial-monitor programs, unplug the board, and run this installer again.'
    Stop-WithMessage 'Flying Thumb was not installed.' $uploadResult
}

Write-Heading 'Flying Thumb was installed successfully'
Write-Host '1. Unplug the LILYGO.'
Write-Host '2. Insert a FAT32 microSD card if one is not already installed.'
Write-Host '3. Plug the LILYGO back in normally without holding the button.'
Write-Host '4. Join the FlyingThumb-XXXX Wi-Fi network using password: flyingthumb'
Write-Host '5. Open http://192.168.4.1/settings.html to finish setup.'
Write-Host ''
Write-Host 'The transfer log was saved to:' -ForegroundColor DarkGray
Write-Host $logPath -ForegroundColor DarkGray
exit 0
