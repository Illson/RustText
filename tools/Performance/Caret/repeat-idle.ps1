$ErrorActionPreference = 'Stop'
$task = Split-Path -Parent $PSScriptRoot
$work = Join-Path $task 'headless-work'
$output = Join-Path $task 'repeat-idle'
$null = New-Item -ItemType Directory -Path $output
$env:TMP = $env:TEMP = Join-Path $task 'tmp'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_GCGen0MaxBudget = '400000'
$env:DOTNET_GCRegionSize = '100000'
$env:RUSTTEXT_CARET_BENCH_SMOKE = '1'
for ($pair = 1; $pair -le 6; $pair++) {
    $appOrder = if ($pair % 2) { @('candidate','baseline') } else { @('baseline','candidate') }
    for ($i = 0; $i -lt 2; $i++) {
        $app = $appOrder[$i]
        $label = "$pair-$($i + 1)-$app"
        $env:RUSTTEXT_CARET_BENCH_APP = $app
        $env:RUSTTEXT_CARET_BENCH_OUT = Join-Path $output "$label.json"
        $assembly = Join-Path $work "$app/tests/RustText.Tests/bin/Release/net11.0-windows/RustText.Tests.dll"
        Write-Host $label
        & dotnet test $assembly --filter FullyQualifiedName~CaretPerformanceProbe --nologo *> (Join-Path $output "$label.log")
        if ($LASTEXITCODE -or -not (Test-Path -LiteralPath $env:RUSTTEXT_CARET_BENCH_OUT)) { throw "Invalid repeat: $label" }
    }
}
