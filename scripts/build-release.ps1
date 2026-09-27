param([string]$OutputDirectory = "release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root $OutputDirectory
$managerPublish = Join-Path $root "work\release-manager"
$firmwareBuild = Join-Path $root ".pio\build\t-dongle-s3"
$screenlessBuild = Join-Path $root ".pio\build\t-dongle-s3-screenless"
New-Item -ItemType Directory -Force -Path $output,$managerPublish | Out-Null

Push-Location $root
try {
    python -m platformio run -e t-dongle-s3 -e t-dongle-s3-screenless
    if ($LASTEXITCODE -ne 0) { throw "Firmware build failed." }
    dotnet publish manager\FlyingThumbManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $managerPublish
    if ($LASTEXITCODE -ne 0) { throw "Manager build failed." }

    $bootApp = Join-Path $env:USERPROFILE ".platformio\packages\framework-arduinoespressif32\tools\partitions\boot_app0.bin"
    $fullImage = Join-Path $output "FlyingThumb-v2-full.bin"
    $screenlessFullImage = Join-Path $output "FlyingThumb-v2-screenless-full.bin"
    python -m esptool --chip esp32s3 merge_bin -o $fullImage 0x0 "$firmwareBuild\bootloader.bin" 0x8000 "$firmwareBuild\partitions.bin" 0xe000 $bootApp 0x10000 "$firmwareBuild\firmware.bin"
    if ($LASTEXITCODE -ne 0) { throw "Recovery image packaging failed." }
    python -m esptool --chip esp32s3 merge_bin -o $screenlessFullImage 0x0 "$screenlessBuild\bootloader.bin" 0x8000 "$screenlessBuild\partitions.bin" 0xe000 $bootApp 0x10000 "$screenlessBuild\firmware.bin"
    if ($LASTEXITCODE -ne 0) { throw "Screenless recovery image packaging failed." }

    $managerExe = Join-Path $output "FlyingThumbManager.exe"
    $wifiImage = Join-Path $output "FlyingThumb-v2-wifi-update.bin"
    $screenlessWifiImage = Join-Path $output "FlyingThumb-v2-screenless-wifi-update.bin"
    Copy-Item -LiteralPath "$managerPublish\FlyingThumbManager.exe" -Destination $managerExe -Force
    Copy-Item -LiteralPath "$firmwareBuild\firmware.bin" -Destination $wifiImage -Force
    Copy-Item -LiteralPath "$screenlessBuild\firmware.bin" -Destination $screenlessWifiImage -Force

    $managerVersion = ([xml](Get-Content manager\FlyingThumbManager.csproj -Raw)).Project.PropertyGroup.Version
    $firmwareVersion = [regex]::Match((Get-Content src\fileserver.cpp -Raw), 'FIRMWARE_VERSION_BASE\[\]="([^"]+)"').Groups[1].Value
    $package = Join-Path $root "work\manager-package"
    New-Item -ItemType Directory -Force -Path $package,"$package\assets" | Out-Null
    Copy-Item -LiteralPath $managerExe -Destination "$package\FlyingThumbManager.exe" -Force
    Copy-Item -LiteralPath $wifiImage -Destination "$package\FlyingThumb-v2-wifi-update.bin" -Force
    Copy-Item -LiteralPath $fullImage -Destination "$package\FlyingThumb-v2-full.bin" -Force
    Copy-Item -LiteralPath $screenlessWifiImage -Destination "$package\FlyingThumb-v2-screenless-wifi-update.bin" -Force
    Copy-Item -LiteralPath $screenlessFullImage -Destination "$package\FlyingThumb-v2-screenless-full.bin" -Force
    $firmwareVersion | Set-Content -LiteralPath "$package\firmware-version.txt" -Encoding ascii
    $firmwareVersion | Set-Content -LiteralPath "$package\firmware-screenless-version.txt" -Encoding ascii
    Copy-Item -LiteralPath "manager\assets\flying-thumb.png" -Destination "$package\assets\flying-thumb.png" -Force
    $flasherCandidates = @("dist\manager\FlyingThumbEsptool.exe", "work\esptool\FlyingThumbEsptool.exe", "work\esptool\dist\FlyingThumbEsptool.exe")
    $flasher = $flasherCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (!$flasher) { throw "The USB recovery helper has not been built." }
    Copy-Item -LiteralPath $flasher -Destination "$package\FlyingThumbEsptool.exe" -Force
    Copy-Item -LiteralPath $flasher -Destination "$output\FlyingThumbEsptool.exe" -Force
    "Run FlyingThumbManager.exe. Use Drives > Install / Recover a Drive via USB for first installation, or Help > Check for Updates for existing drives." | Set-Content -LiteralPath "$package\README.txt" -Encoding utf8
    Compress-Archive -Path "$package\*" -DestinationPath "$output\FlyingThumbManager-Windows.zip" -Force

    $base = "https://github.com/foolserranddev/flying-thumb/releases/latest/download"
    $manifest = [ordered]@{
        schema = 1
        manager = [ordered]@{ version = $managerVersion; url = "$base/FlyingThumbManager.exe"; sha256 = (Get-FileHash $managerExe -Algorithm SHA256).Hash }
        flasher = [ordered]@{ version = $managerVersion; url = "$base/FlyingThumbEsptool.exe"; sha256 = (Get-FileHash "$output\FlyingThumbEsptool.exe" -Algorithm SHA256).Hash }
        firmware = [ordered]@{ version = $firmwareVersion; url = "$base/FlyingThumb-v2-wifi-update.bin"; sha256 = (Get-FileHash $wifiImage -Algorithm SHA256).Hash }
        recovery = [ordered]@{ version = $firmwareVersion; url = "$base/FlyingThumb-v2-full.bin"; sha256 = (Get-FileHash $fullImage -Algorithm SHA256).Hash }
        firmwareScreenless = [ordered]@{ version = $firmwareVersion; url = "$base/FlyingThumb-v2-screenless-wifi-update.bin"; sha256 = (Get-FileHash $screenlessWifiImage -Algorithm SHA256).Hash }
        recoveryScreenless = [ordered]@{ version = $firmwareVersion; url = "$base/FlyingThumb-v2-screenless-full.bin"; sha256 = (Get-FileHash $screenlessFullImage -Algorithm SHA256).Hash }
        notes = "Adds automatic firmware selection and color-coded connection status for external-antenna drives without a screen."
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$output\latest.json" -Encoding utf8
    Get-ChildItem -LiteralPath $output | Select-Object Name,Length,LastWriteTime
}
finally { Pop-Location }
