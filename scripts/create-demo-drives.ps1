$ErrorActionPreference = "Stop"
$root = Join-Path (Split-Path -Parent $PSScriptRoot) "demo-drives"
$drives = @{
    "Cutting-Table" = @{ id = "demo-cutting"; name = "Cutting Table" }
    "Embroidery-1" = @{ id = "demo-embroidery"; name = "Embroidery-1" }
    "LongArm-1" = @{ id = "demo-longarm"; name = "LongArm-1" }
}
foreach ($entry in $drives.GetEnumerator()) {
    $folder = Join-Path $root $entry.Key
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $entry.Value | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder ".flyingthumb-demo.json") -Encoding utf8
    "Flying Thumb demo drive: $($entry.Value.name)" | Set-Content -LiteralPath (Join-Path $folder "README.txt") -Encoding utf8
}
"Cutting instructions for the demo shop." | Set-Content -LiteralPath (Join-Path $root "Cutting-Table\Cutting Guide.txt") -Encoding utf8
"Feathered star embroidery pattern notes." | Set-Content -LiteralPath (Join-Path $root "Embroidery-1\Feathered Star Pattern.txt") -Encoding utf8
"Feathered star pattern for the longarm." | Set-Content -LiteralPath (Join-Path $root "LongArm-1\Feathered Star Pattern.txt") -Encoding utf8
"Floral applique demo." | Set-Content -LiteralPath (Join-Path $root "Embroidery-1\Floral Applique.txt") -Encoding utf8
"LongArm maintenance checklist." | Set-Content -LiteralPath (Join-Path $root "LongArm-1\LongArm Maintenance.txt") -Encoding utf8
"Shared note - cutting table copy is intentionally longer for conflict testing." | Set-Content -LiteralPath (Join-Path $root "Cutting-Table\Shared Shop Notes.txt") -Encoding utf8
"Shared note." | Set-Content -LiteralPath (Join-Path $root "Embroidery-1\Shared Shop Notes.txt") -Encoding utf8
"Shared note." | Set-Content -LiteralPath (Join-Path $root "LongArm-1\Shared Shop Notes.txt") -Encoding utf8
Write-Host "Created safe demo drives at $root"