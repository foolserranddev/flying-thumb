param([string]$DriveLetter = 'E', [string]$ExpectedSerial = '441BF6ED9278')
$ErrorActionPreference = 'Stop'
$letter = $DriveLetter.TrimEnd(':','\').ToUpperInvariant()
if ($letter -notmatch '^[A-Z]$') { throw 'Supply one drive letter.' }
$partition = Get-Partition -DriveLetter $letter
$disk = Get-Disk -Number $partition.DiskNumber
if ($disk.BusType -ne 'USB' -or $disk.SerialNumber.Trim() -ne $ExpectedSerial) { throw 'Volume is not the expected Flying Thumb USB device.' }
$volumeRoot = [IO.Path]::GetFullPath($letter + ':\')
$testRoot = [IO.Path]::GetFullPath((Join-Path $volumeRoot ('.FlyingThumb-test-' + [Guid]::NewGuid().ToString('N'))))
if (!$testRoot.StartsWith($volumeRoot + '.FlyingThumb-test-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test directory.' }
try {
    $nested = Join-Path $testRoot 'Nested folder\Another folder'
    [IO.Directory]::CreateDirectory($nested) | Out-Null
    $source = Join-Path $nested 'Test file with spaces.bin'
    $renamed = Join-Path $nested 'Renamed file.bin'
    $payload = [byte[]]::new(262144)
    [Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $file = [IO.FileStream]::new($source, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $file.Write($payload, 0, $payload.Length); $file.Flush($true) } finally { $file.Dispose() }
    $writeMs = $watch.ElapsedMilliseconds
    $watch.Restart()
    $readback = [IO.File]::ReadAllBytes($source)
    $readMs = $watch.ElapsedMilliseconds
    $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload))
    $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readback))
    if ($actualHash -ne $expectedHash) { throw 'USB file readback differs from the written payload.' }
    [IO.File]::Move($source, $renamed)
    if (![IO.File]::Exists($renamed) -or [IO.File]::Exists($source)) { throw 'USB rename failed.' }
    [IO.File]::Delete($renamed)
    [PSCustomObject]@{Result='PASS'; Serial=$ExpectedSerial; Bytes=$payload.Length; WriteMs=$writeMs; ReadMs=$readMs; ReadMayBeCached=$true; NestedFolders='PASS'; Rename='PASS'; ReadbackHash=$actualHash}
}
finally {
    if ([IO.Directory]::Exists($testRoot)) {
        $resolved = [IO.Path]::GetFullPath($testRoot)
        if (!$resolved.StartsWith($volumeRoot + '.FlyingThumb-test-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target is outside the test directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
