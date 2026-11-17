[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$ReadmePath,
    [string]$VerificationPath,
    [string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageName = 'RustText'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $repositoryRoot 'target\release\rusttext.exe'
}
if (-not $ReadmePath) {
    $ReadmePath = Join-Path $repositoryRoot 'README.md'
}
if (-not $VerificationPath) {
    $VerificationPath = Join-Path $repositoryRoot 'docs\verification.md'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'target\distribution'
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$entries = [ordered]@{
    'rusttext.exe' = [IO.Path]::GetFullPath($ExecutablePath)
    'README.md' = [IO.Path]::GetFullPath($ReadmePath)
    'docs/verification.md' = [IO.Path]::GetFullPath($VerificationPath)
    'licenses/gpui-base-APACHE.txt' = Join-Path $repositoryRoot 'vendor/gpui-base/LICENSE-APACHE'
}
foreach ($file in $entries.Values) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required package input was not found: $file"
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$archivePath = Join-Path $OutputDirectory "$PackageName.zip"
$checksumPath = "$archivePath.sha256"
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) {
    throw "Refusing to overwrite an existing package or checksum: $archivePath"
}

$nonce = [Guid]::NewGuid().ToString('N')
$temporaryArchive = Join-Path $OutputDirectory ".$PackageName.$nonce.tmp"
$temporaryChecksum = "$temporaryArchive.sha256"
$archiveCommitted = $false

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $entry.Value,
                $entry.Key,
                [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $temporaryChecksum,
        "$hash  $PackageName.zip`n",
        [Text.UTF8Encoding]::new($false)
    )

    # File.Move without overwrite protects an artifact created by another run.
    [IO.File]::Move($temporaryArchive, $archivePath)
    $archiveCommitted = $true
    [IO.File]::Move($temporaryChecksum, $checksumPath)
}
catch {
    if ($archiveCommitted -and (Test-Path -LiteralPath $archivePath)) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    throw
}
finally {
    foreach ($temporaryPath in @($temporaryArchive, $temporaryChecksum)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

Write-Output "Package: $archivePath"
Write-Output "SHA-256: $checksumPath ($hash)"
