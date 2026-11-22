$guiScenarios = @('empty-cold', 'empty', 'text', 'json', 'log')
$coreScenarios = @('log-index', 'literal-miss', 'literal-late-hit', 'literal-dense-first32', 'regex-literal-miss',
    'regex-no-literal-miss', 'regex-no-literal-late-hit', 'json-format-string', 'editor-find-all', 'editor-replace-all-string')

function Format-PerformanceRange([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $mid = [int][Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2 }
    $culture = [Globalization.CultureInfo]::InvariantCulture
    '{0} [{1}–{2}]' -f $median.ToString('0.000', $culture), $sorted[0].ToString('0.000', $culture),
        $sorted[-1].ToString('0.000', $culture)
}

function Format-PerformancePairs($Rows, [string]$Metric, [int]$Iterations, [double]$Unit) {
    $baseValues = [Collections.Generic.List[double]]::new()
    $candidateValues = [Collections.Generic.List[double]]::new()
    $ratios = [Collections.Generic.List[double]]::new()
    if ($Rows.Count -ne 2 * $Iterations) { throw "Incorrect number of samples for $Metric." }
    for ($i = 1; $i -le $Iterations; $i++) {
        $pair = @($Rows | Where-Object iteration -eq $i)
        $base = @($pair | Where-Object app -ceq 'baseline')
        $candidate = @($pair | Where-Object app -ceq 'candidate')
        $baseOrder = if ($i % 2) { 1 } else { 2 }
        if ($pair.Count -ne 2 -or $base.Count -ne 1 -or $candidate.Count -ne 1 -or
            $base[0].order -ne $baseOrder -or $candidate[0].order -ne (3 - $baseOrder) -or
            $base[0].status -ne 'ok' -or $candidate[0].status -ne 'ok') {
            throw "Invalid pair for $($Rows[0].scenario), iteration $i."
        }
        $b = $base[0].$Metric
        $c = $candidate[0].$Metric
        if ($null -eq $b -or $null -eq $c -or -not [double]::IsFinite($b) -or -not [double]::IsFinite($c) -or
            $b -lt 0 -or $c -lt 0) {
            throw "Invalid or missing $Metric."
        }
        $baseValues.Add($b / $Unit)
        $candidateValues.Add($c / $Unit)
        if ($b -gt 0) { $ratios.Add($c / $b) }
    }
    $ratio = if ($ratios.Count -eq $Iterations) { Format-PerformanceRange $ratios } else { '–' }
    "$(Format-PerformanceRange $baseValues) | $(Format-PerformanceRange $candidateValues) | $ratio"
}

function Write-PerformanceReport($Data, [string]$Path, [string]$RawLink = 'raw.json') {
    $iterations = $Data.iterations
    if ($Data.schema -cne 'rusttext-performance-v1' -or $Data.status -cne 'complete' -or
        $iterations -lt 2 -or $iterations -gt 100 -or $iterations % 2 -ne 0) {
        throw 'Unsupported or incomplete performance data.'
    }
    $expectedGui = if ($Data.coreOnly) { 0 } else { $guiScenarios.Count * $iterations * 2 }
    if ($Data.guiRuns.Count -ne $expectedGui -or $Data.coreRuns.Count -ne $coreScenarios.Count * $iterations * 2) {
        throw 'Incomplete or unexpected set of performance scenarios.'
    }

    $table = '| Сценарий | Метрика | База | Кандидат | Кандидат / база |'
    $divider = '| --- | --- | ---: | ---: | ---: |'
    $mode = if ($Data.smoke) { 'Проверочный прогон с малыми входами.' } else { 'Полные размеры входов.' }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]@(
        '# Парные замеры RustText',
        '',
        "$mode Windows x64, .NET SDK $($Data.environment.dotnetSdk), $iterations пар на сценарий.",
        '',
        "- База: ``$($Data.revisions.baseline)``. Кандидат: ``$($Data.revisions.candidate)``.",
        "- Сырые наблюдения, окружение и идентификаторы входов: [JSON]($RawLink).",
        '',
        'В ячейках медиана [минимум–максимум]; последний столбец рассчитан из отношений внутри пар.',
        ''
    ))
    if (-not $Data.coreOnly) {
        $lines.AddRange([string[]]@(
            '## Окно',
            '',
            "Размер EXE: $($Data.binaries.baseline.bytes) / $($Data.binaries.candidate.bytes) байт (база / кандидат); извлечённые DLL: $($Data.binaries.baseline.extractedDllBytes) / $($Data.binaries.candidate.extractedDllBytes) байт.",
            "Видимое окно и ответ на ``WM_NULL`` измерены снаружи; срез памяти — в возрасте около $($Data.memorySampleTargetMs) мс. Cold означает пустой кэш DLL приложения, а не кэш ОС.",
            '',
            $table,
            $divider
        ))
        foreach ($scenario in $guiScenarios) {
            $rows = @($Data.guiRuns | Where-Object scenario -ceq $scenario)
            foreach ($metric in 'windowMs', 'responsiveMs', 'workingSetBytes', 'privateBytes', 'peakWorkingSetBytes') {
                $unit = if ($metric.EndsWith('Bytes')) { 1MB } else { 1 }
                $suffix = if ($unit -eq 1) { 'мс' } else { 'МиБ' }
                $lines.Add("| $scenario | $metric, $suffix | $(Format-PerformancePairs $rows $metric $iterations $unit) |")
            }
        }
        $lines.Add('')
    }
    $lines.AddRange([string[]]@(
        '## Ядро',
        '',
        'Выход каждой операции проверен, включая прогрев. Аллокации учитывают только управляемую память измеряемого потока.',
        '',
        $table,
        $divider
    ))
    foreach ($scenario in $coreScenarios) {
        $rows = @($Data.coreRuns | Where-Object scenario -ceq $scenario)
        $lines.Add("| $scenario | мс | $(Format-PerformancePairs $rows 'ms' $iterations 1) |")
        $lines.Add("| $scenario | аллокации, байт | $(Format-PerformancePairs $rows 'managedBytes' $iterations 1) |")
    }
    [IO.File]::WriteAllLines($Path, $lines)
}
