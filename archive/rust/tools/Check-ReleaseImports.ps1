#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$dumpbin = & $vswhere -latest -products '*' -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe'
if ($LASTEXITCODE -ne 0 -or -not $dumpbin) {
    throw 'DUMPBIN was not found. Install Visual Studio C++ x64 build tools.'
}

$imports = & ($dumpbin | Select-Object -First 1) /IMPORTS:icuuc.dll $executable
if ($LASTEXITCODE -ne 0) {
    throw "DUMPBIN failed to inspect $executable (exit code $LASTEXITCODE)."
}
if ($imports -match '^\s*icuuc\.dll\s*$') {
    throw "Unexpected import of icuuc.dll in $executable"
}
Write-Output "No icuuc.dll import in $executable"
