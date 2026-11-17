#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Full', 'Targeted', 'Build', 'Release')]
    [string]$Mode = 'Full',
    [string[]]$TestFilter = @(),
    [ValidateRange(1, 64)]
    [int]$Jobs = 2,
    [ValidateRange(1, 64)]
    [int]$CpuCount = 2,
    [string]$CacheDirectory
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'Проверка рассчитана на Windows x64.'
}
if ($Mode -eq 'Targeted' -and ($TestFilter.Count -eq 0 -or ($TestFilter | Where-Object { [string]::IsNullOrWhiteSpace($_) }))) {
    throw 'Для Targeted укажите непустой -TestFilter.'
}
if ($Mode -ne 'Targeted' -and $TestFilter.Count -gt 0) {
    throw '-TestFilter применяется только с -Mode Targeted.'
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$process = Get-Process -Id $PID
$previousPriority = $process.PriorityClass
$previousAffinity = $process.ProcessorAffinity
$previousJobs = $env:CARGO_BUILD_JOBS
$previousNativeJobs = $env:CMAKE_BUILD_PARALLEL_LEVEL
$previousCache = $env:CARGO_TARGET_DIR
$mask = 0L
$selected = 0
for ($bit = 0; $bit -lt [IntPtr]::Size * 8 -and $selected -lt $CpuCount; $bit++) {
    $candidate = 1L -shl $bit
    if (($previousAffinity.ToInt64() -band $candidate) -ne 0) {
        $mask = $mask -bor $candidate
        $selected++
    }
}

function Invoke-Cargo([string[]]$CargoArguments) {
    & cargo @CargoArguments
    if ($LASTEXITCODE -ne 0) {
        throw "cargo $($CargoArguments -join ' ') завершился с кодом $LASTEXITCODE"
    }
}

Push-Location -LiteralPath $root
try {
    $process.PriorityClass = 'BelowNormal'
    $process.ProcessorAffinity = [IntPtr]$mask
    $env:CARGO_BUILD_JOBS = $Jobs.ToString()
    $env:CMAKE_BUILD_PARALLEL_LEVEL = $Jobs.ToString()
    if ($CacheDirectory) {
        $env:CARGO_TARGET_DIR = [IO.Path]::GetFullPath($CacheDirectory, $root)
    }
    Write-Host "Режим $Mode, процессов Cargo: $Jobs, логических CPU: $selected, приоритет BelowNormal"
    switch ($Mode) {
        'Full' {
            Invoke-Cargo -CargoArguments @('fmt', '--all', '--', '--check')
            Invoke-Cargo -CargoArguments @('test', '--locked', '--', "--test-threads=$Jobs")
            Invoke-Cargo -CargoArguments @('clippy', '--locked', '--all-targets', '--', '-D', 'warnings')
        }
        'Targeted' {
            foreach ($filter in $TestFilter) {
                Invoke-Cargo -CargoArguments @('test', '--locked', $filter, '--', "--test-threads=$Jobs")
            }
        }
        'Build' { Invoke-Cargo -CargoArguments @('build', '--locked') }
        'Release' {
            Invoke-Cargo -CargoArguments @('build', '--release', '--locked', '--target', 'x86_64-pc-windows-msvc')
        }
    }
} finally {
    $env:CARGO_BUILD_JOBS = $previousJobs
    $env:CMAKE_BUILD_PARALLEL_LEVEL = $previousNativeJobs
    $env:CARGO_TARGET_DIR = $previousCache
    $process.ProcessorAffinity = $previousAffinity
    $process.PriorityClass = $previousPriority
    Pop-Location
}
