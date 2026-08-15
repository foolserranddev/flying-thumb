param([Parameter(Mandatory = $true)][string]$Tag)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    & "$PSScriptRoot\build-release.ps1"
    if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

    git rev-parse --verify "refs/tags/$Tag" *> $null
    if ($LASTEXITCODE -ne 0) {
        git tag $Tag
        if ($LASTEXITCODE -ne 0) { throw "Could not create tag $Tag." }
    }

    git push origin main
    if ($LASTEXITCODE -ne 0) { throw "Could not push main." }
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "Could not push tag $Tag." }

    $assets = @(
        "release/FlyingThumbManager.exe",
        "release/FlyingThumbManager-Windows.zip",
        "release/FlyingThumb-v2-wifi-update.bin",
        "release/FlyingThumb-v2-full.bin",
        "release/latest.json"
    )

    gh release view $Tag --json tagName *> $null
    if ($LASTEXITCODE -eq 0) {
        gh release upload $Tag @assets --clobber
        if ($LASTEXITCODE -ne 0) { throw "Could not upload release files." }
        gh release edit $Tag --latest
    }
    else {
        gh release create $Tag @assets --title $Tag --generate-notes --latest
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not publish release $Tag." }

    Write-Host "Published $Tag directly from the local verified build."
}
finally {
    Pop-Location
}
