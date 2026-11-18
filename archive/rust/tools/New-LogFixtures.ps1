[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\target\qa-logs')
)

$ErrorActionPreference = 'Stop'
$targetDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
$bytesPerGiB = [long]1GB
$recordBytes = 128
$recordsPerBlock = 512
$blockBytes = $recordBytes * $recordsPerBlock
$writeBytes = 4MB
$repetitionsPerWrite = [int]($writeBytes / $blockBytes)

if (-not ('RustTextFixtureNative' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class RustTextFixtureNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint highPart);

    public static ulong GetAllocatedFileSize(string path)
    {
        uint highPart;
        uint lowPart = GetCompressedFileSizeW(path, out highPart);
        if (lowPart == uint.MaxValue)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0) throw new System.ComponentModel.Win32Exception(error);
        }
        return ((ulong)highPart << 32) | lowPart;
    }
}
"@
}

function New-LogBlock([string]$FinalMarker) {
    $block = [byte[]]::new($blockBytes)
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    for ($recordIndex = 0; $recordIndex -lt $recordsPerBlock; $recordIndex++) {
        if ($recordIndex -eq ($recordsPerBlock - 1) -and $FinalMarker) {
            $line = "9999999999|FINAL|$FinalMarker|последняя строка RustText|"
        }
        else {
            $line = ('{0:D10}|INFO|Событие обработки|file=Журнал.txt|строка={0:D10}|ok=true|' -f $recordIndex)
        }
        $lineBytes = $utf8.GetBytes($line)
        if ($lineBytes.Length -ge ($recordBytes - 1)) {
            throw "Log line exceeds the fixed record size: $line"
        }
        $offset = $recordIndex * $recordBytes
        for ($index = 0; $index -lt ($recordBytes - 1); $index++) {
            $block[$offset + $index] = 32
        }
        [System.Buffer]::BlockCopy($lineBytes, 0, $block, $offset, $lineBytes.Length)
        $block[$offset + $recordBytes - 1] = 10
    }
    return ,$block
}

function New-WriteBuffer([byte[]]$Block) {
    $buffer = [byte[]]::new($writeBytes)
    for ($index = 0; $index -lt $repetitionsPerWrite; $index++) {
        [System.Buffer]::BlockCopy($Block, 0, $buffer, $index * $blockBytes, $blockBytes)
    }
    return ,$buffer
}

$targets = @(
    [pscustomobject]@{ GiB = 10; Name = 'rusttext-log-10GiB.txt'; Marker = 'EOF_MARKER_10_GIB' },
    [pscustomobject]@{ GiB = 20; Name = 'rusttext-log-20GiB.txt'; Marker = 'EOF_MARKER_20_GIB' }
)
$results = foreach ($target in $targets) {
    $path = Join-Path $targetDirectory $target.Name
    $targetBytes = [long]$target.GiB * $bytesPerGiB
    if (($targetBytes % $writeBytes) -ne 0) {
        throw "Target size must be divisible by the write buffer size: $($target.GiB) GiB"
    }
    $fullWrites = [long]($targetBytes / $writeBytes)
    $ordinaryBlock = New-LogBlock ''
    $finalBlock = New-LogBlock $target.Marker
    $ordinaryBuffer = New-WriteBuffer $ordinaryBlock
    $finalBuffer = New-WriteBuffer $ordinaryBlock
    [System.Buffer]::BlockCopy($finalBlock, 0, $finalBuffer, $finalBuffer.Length - $blockBytes, $blockBytes)

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $stream = [System.IO.FileStream]::new(
        $path,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::Read,
        1MB,
        [System.IO.FileOptions]::SequentialScan
    )
    try {
        for ($writeIndex = [long]0; $writeIndex -lt ($fullWrites - 1); $writeIndex++) {
            $stream.Write($ordinaryBuffer, 0, $ordinaryBuffer.Length)
        }
        $stream.Write($finalBuffer, 0, $finalBuffer.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
        $watch.Stop()
    }

    $fileInfo = Get-Item -LiteralPath $path -Force
    $actualBytes = $fileInfo.Length
    if ($actualBytes -ne $targetBytes) {
        throw "Unexpected fixture size for $path. Expected $targetBytes bytes, got $actualBytes bytes."
    }
    if (($fileInfo.Attributes -band [System.IO.FileAttributes]::SparseFile) -ne 0) {
        throw "Fixture is marked sparse: $path"
    }
    $allocatedBytes = [RustTextFixtureNative]::GetAllocatedFileSize($path)
    if ($allocatedBytes -ne $targetBytes) {
        throw "Fixture allocation differs from its logical size. Expected $targetBytes bytes, got $allocatedBytes bytes."
    }
    $markerBytes = [System.Text.Encoding]::UTF8.GetBytes($target.Marker)
    $tail = [byte[]]::new(4096)
    $reader = [System.IO.File]::OpenRead($path)
    try {
        [void]$reader.Seek(-$tail.Length, [System.IO.SeekOrigin]::End)
        [void]$reader.ReadExactly($tail)
    }
    finally {
        $reader.Dispose()
    }
    $tailText = [System.Text.Encoding]::UTF8.GetString($tail)
    if (-not $tailText.Contains($target.Marker)) {
        throw "Final marker not found near end of $path"
    }

    $result = [pscustomobject]@{
        Path = $path
        LogicalBytes = $actualBytes
        AllocatedBytes = $allocatedBytes
        FinalMarker = $target.Marker
        FinalLine = '9999999999|FINAL|<marker>|последняя строка RustText|'
        Lines = [long]($targetBytes / $recordBytes)
        Seconds = [math]::Round($watch.Elapsed.TotalSeconds, 2)
    }
    Write-Host ("Wrote {0} GiB in {1:N2} seconds: {2}" -f $target.GiB, $result.Seconds, $path)
    $result
}

$results | Select-Object Path, LogicalBytes, AllocatedBytes, Lines, Seconds, FinalMarker | Format-List
