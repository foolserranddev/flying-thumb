param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Repository = "foolserranddev/flying-thumb",
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Push-Location $root
try {
    if (!$SkipBuild) {
        & "$PSScriptRoot\build-release.ps1"
        if ($LASTEXITCODE -ne 0) { throw "Release build failed." }
    }

    $existingTag = git tag --list $Tag
    if ([string]::IsNullOrWhiteSpace($existingTag)) {
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

    $publishedReleases = $(gh release list --repo $Repository --limit 100 --json tagName) | ConvertFrom-Json
    $publishedTags = @($publishedReleases | ForEach-Object { $_.tagName })
    if ($publishedTags -contains $Tag) {
        gh release upload $Tag @assets --clobber --repo $Repository
        if ($LASTEXITCODE -ne 0) { throw "Could not upload release files." }
        gh release edit $Tag --latest --repo $Repository
    }
    else {
        gh release create $Tag @assets --title $Tag --generate-notes --latest --repo $Repository
    }
    if ($LASTEXITCODE -ne 0) { throw "Could not publish release $Tag." }

    Write-Host "Published $Tag directly from the local verified build."
}
finally {
    Pop-Location
}
