[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$exe = Get-Item -LiteralPath $Executable -Force
if ($exe.PSIsContainer -or $exe.Name -ine 'rusttext.exe' -or $exe.Length -le 0 -or $exe.Length -gt 256MB -or
    ($exe.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Provide a regular rusttext.exe no larger than 256 MiB.'
}
$reader = [System.Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead($exe.FullName))
try {
    if ($reader.PEHeaders.CoffHeader.Machine -ne 'Amd64' -or $reader.PEHeaders.PEHeader.Magic -ne 'PE32Plus' -or
        $reader.PEHeaders.CorHeader) {
        throw 'The package requires a native Windows x64 rusttext.exe.'
    }
} finally {
    $reader.Dispose()
}

$output = [IO.Path]::TrimEndingDirectorySeparator(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
)
if (Test-Path -LiteralPath $output) {
    throw "Use a new output directory: $output"
}
$parent = [IO.Path]::GetDirectoryName($output)
[void][IO.Directory]::CreateDirectory($parent)
$stage = Join-Path $parent ('.rusttext-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
$archiveNames = @('RustText.NET-win-x64.zip', 'RustText.zip')

try {
    $primary = Join-Path $stage $archiveNames[0]
    $archive = [IO.Compression.ZipFile]::Open($primary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $exe.FullName, 'rusttext.exe', [IO.Compression.CompressionLevel]::Optimal
        )
    } finally {
        $archive.Dispose()
    }
    if ((Get-Item -LiteralPath $primary).Length -gt 128MB) {
        throw 'The ZIP exceeds the 128 MiB limit of both update clients.'
    }

    Copy-Item -LiteralPath $primary -Destination (Join-Path $stage $archiveNames[1])
    $hash = (Get-FileHash -LiteralPath $primary -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($name in $archiveNames) {
        "$hash  $name" | Set-Content -LiteralPath (Join-Path $stage "$name.sha256") -Encoding ascii
    }
    [IO.Directory]::Move($stage, $output)
} finally {
    if (Test-Path -LiteralPath $stage) {
        if ((Get-Item -LiteralPath $stage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The staging directory is a reparse point: $stage"
        }
        foreach ($name in $archiveNames) {
            foreach ($file in $name, "$name.sha256") {
                $path = Join-Path $stage $file
                if (Test-Path -LiteralPath $path -PathType Leaf) {
                    Remove-Item -LiteralPath $path -Force
                }
            }
        }
        [IO.Directory]::Delete($stage)
    }
}

Get-ChildItem -LiteralPath $output -File | Select-Object FullName, Length
