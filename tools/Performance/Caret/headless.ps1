param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$task = Split-Path -Parent $PSScriptRoot
$root = (git rev-parse --show-toplevel).Trim()
$work = Join-Path $task 'headless-work'
$results = Join-Path $task 'headless-observations'
$logs = Join-Path $task 'headless-logs'
if ($CheckOnly) { $results += '-smoke'; $logs += '-smoke' }
$null = New-Item -ItemType Directory -Path $work,$results,$logs -Force
$env:TMP = $env:TEMP = Join-Path $task 'tmp'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_GCGen0MaxBudget = '400000'
$env:DOTNET_GCRegionSize = '100000'
$apps = [ordered]@{
    'baseline' = 'f7185c29398be1142ed8b988311bcfa1faf04af2'
    'candidate' = '5b04f5369dc1f411038bbdc476e1be684465abd1'
}
function Log([string]$Label, [string]$Exe, [string[]]$ArgsArray) {
    $path = Join-Path $logs "$Label.log"
    Write-Host $Label
    & $Exe @ArgsArray *> $path
    if ($LASTEXITCODE) { throw "$Label failed ($LASTEXITCODE), see $path" }
}
foreach ($app in $apps.Keys) {
    $sha = $apps[$app]
    $archive = Join-Path $work "$app.zip"
    $source = Join-Path $work $app
    if (-not (Test-Path -LiteralPath $source)) {
        Log "$app-source" git @('-C', $root, 'archive', '--format=zip', "--output=$archive", $sha)
        Expand-Archive -LiteralPath $archive -DestinationPath $source
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CaretPerformanceProbe.cs') -Destination (Join-Path $source 'tests/RustText.Tests')
    Push-Location $source
    try {
        Log "$app-build" dotnet @('build', 'tests/RustText.Tests/RustText.Tests.csproj', '-c', 'Release',
            "-p:SourceRevisionId=$sha", '-p:PublishAot=false', '--nologo')
    } finally { Pop-Location }
}
function Take-Sample([string]$App, [int]$Pair, [int]$Order) {
    $label = "$Pair-$Order-$App"
    $env:RUSTTEXT_CARET_BENCH_APP = $App
    $env:RUSTTEXT_CARET_BENCH_OUT = Join-Path $results "$label.json"
    $assembly = Join-Path $work "$App/tests/RustText.Tests/bin/Release/net11.0-windows/RustText.Tests.dll"
    Log $label dotnet @('test', $assembly, '--filter', 'FullyQualifiedName~CaretPerformanceProbe', '--nologo')
    if (-not (Test-Path -LiteralPath $env:RUSTTEXT_CARET_BENCH_OUT)) { throw "No checked data for $label." }
}
$env:RUSTTEXT_CARET_BENCH_SMOKE = '1'
Take-Sample baseline 0 1
Take-Sample candidate 0 2
if ($CheckOnly) { return }
$env:RUSTTEXT_CARET_BENCH_SMOKE = '0'
for ($pair = 1; $pair -le 6; $pair++) {
    if ($pair % 2) { Take-Sample baseline $pair 1; Take-Sample candidate $pair 2 }
    else { Take-Sample candidate $pair 1; Take-Sample baseline $pair 2 }
}
