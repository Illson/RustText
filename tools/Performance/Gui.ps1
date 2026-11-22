Add-Type -Path (Join-Path $PSScriptRoot 'WindowsProbe.cs')

function New-PerformanceFixture([string]$Path, [long]$TargetBytes, [string]$Prefix, [string]$Record, [string]$Suffix) {
    $encoding = [Text.UTF8Encoding]::new($false)
    $start = $encoding.GetBytes($Prefix)
    $end = $encoding.GetBytes($Suffix)
    $unit = $encoding.GetByteCount($Record)
    $unitsPerBlock = [int][Math]::Floor(1MB / $unit)
    $block = $encoding.GetBytes($Record * $unitsPerBlock)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $stream.Write($start)
        $remaining = [long][Math]::Floor(($TargetBytes - $start.Length - $end.Length) / $unit)
        while ($remaining -gt 0) {
            $units = [int][Math]::Min($unitsPerBlock, $remaining)
            $stream.Write($block, 0, $units * $unit)
            $remaining -= $units
        }
        $stream.Write($end)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
    Get-PerformanceFileIdentity $Path
}

function Invoke-PerformanceGuiSample($Case, [string]$App, [string]$Exe, [int]$Iteration, [int]$Order,
                                    [int]$SampleMs, [string]$WarmNative) {
    $id = "$($Case.Name)-$Iteration-$App"
    $profile = Join-Path $workRoot "profiles/$id"
    $settings = Join-Path $profile 'dotnet-demo'
    $null = New-Item -ItemType Directory -Path $settings
    [IO.File]::WriteAllText((Join-Path $settings 'settings.json'),
        '{"open_large_files_as_log":true,"auto_check_updates":false,"auto_check_updates_choice_made":true}')
    if ($Case.Cache -eq 'warm') {
        Copy-Item -LiteralPath $WarmNative -Destination (Join-Path $profile 'native') -Recurse
    }

    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path -Parent $Exe
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -like 'RUSTTEXT_*') { $null = $start.Environment.Remove($key) }
    }
    $start.Environment['RUSTTEXT_PROFILE_DIR'] = $profile
    $start.Environment['RUSTTEXT_MEASURE'] = '1'
    $expectedTitle = 'RustText'
    if ($Case.Path) {
        $start.ArgumentList.Add($Case.Path)
        $expectedTitle = "$([IO.Path]::GetFileName($Case.Path)) — RustText"
    }

    $sample = [ordered]@{
        scenario = $Case.Name; app = $App; iteration = $Iteration; order = $Order; cache = $Case.Cache
        startedUtc = [datetime]::UtcNow.ToString('o'); status = 'failed'; windowMs = $null; responsiveMs = $null
        readyMs = $null; logMarkerMs = $null; memorySampleMs = $null; workingSetBytes = $null
        peakWorkingSetBytes = $null; privateBytes = $null; cpuMs = $null; title = ''; exitCode = $null
        killed = $false; stdout = ''; stderr = ''; error = $null
    }
    $process = $stdout = $stderr = $null
    $window = [IntPtr]::Zero
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $stdout = [RustTextPerformanceOutput]::new($process.StandardOutput, $clock)
        $stderr = [RustTextPerformanceOutput]::new($process.StandardError, $clock)
        while (-not $process.HasExited -and $clock.ElapsedMilliseconds -lt [Math]::Max(15000, $SampleMs + 5000)) {
            if ($null -eq $sample.memorySampleMs -and $clock.ElapsedMilliseconds -ge $SampleMs) {
                $process.Refresh()
                $sample.memorySampleMs = $clock.Elapsed.TotalMilliseconds
                $sample.workingSetBytes = $process.WorkingSet64
                $sample.peakWorkingSetBytes = $process.PeakWorkingSet64
                $sample.privateBytes = $process.PrivateMemorySize64
                $sample.cpuMs = $process.TotalProcessorTime.TotalMilliseconds
            }
            if ($window -eq [IntPtr]::Zero) {
                $window = [RustTextPerformanceWindows]::VisibleWindow($process.Id)
                if ($window -ne [IntPtr]::Zero) { $sample.windowMs = $clock.Elapsed.TotalMilliseconds }
            }
            if ($window -ne [IntPtr]::Zero) {
                if ($null -eq $sample.responsiveMs -and [RustTextPerformanceWindows]::Responds($window)) {
                    $sample.responsiveMs = $clock.Elapsed.TotalMilliseconds
                }
                $sample.title = [RustTextPerformanceWindows]::Title($window)
                if ($null -eq $sample.readyMs -and $sample.title -ceq $expectedTitle -and
                    ($Case.Name -ne 'log' -or $null -ne $stdout.MarkerMs)) {
                    $sample.readyMs = $clock.Elapsed.TotalMilliseconds
                }
            }
            if ($null -ne $sample.memorySampleMs -and $null -ne $sample.responsiveMs -and $null -ne $sample.readyMs) { break }
            Start-Sleep -Milliseconds 5
        }
        if ($process.HasExited -or $null -eq $sample.memorySampleMs -or $null -eq $sample.responsiveMs) {
            throw 'Process exited early or window/memory observations timed out.'
        }
        if ($sample.memorySampleMs - $SampleMs -gt 100) { throw 'Memory sample missed its 100 ms sampling window.' }
        if ($null -eq $sample.readyMs -or $sample.readyMs -gt $sample.memorySampleMs) {
            throw "Expected '$expectedTitle' in the correct mode before the memory sample."
        }
        $sample.status = 'ok'
    } catch {
        $sample.error = $_.Exception.Message
    } finally {
        try {
            if ($process) {
                if (-not $process.HasExited) {
                    if ($window -ne [IntPtr]::Zero) { [RustTextPerformanceWindows]::Close($window, $process.Id) }
                    if ($window -eq [IntPtr]::Zero -or -not $process.WaitForExit(1500)) {
                        $sample.killed = $true
                        try { $process.Kill($true) } catch { if (-not $process.HasExited) { throw } }
                        if (-not $process.WaitForExit(3000)) { throw 'Process did not exit after termination.' }
                    }
                }
                $sample.exitCode = $process.ExitCode
                if ($stdout) {
                    if (-not $stdout.Finish(3000)) { throw 'Timed out draining standard output.' }
                    $sample.stdout = $stdout.Text
                    $sample.logMarkerMs = $stdout.MarkerMs
                }
                if ($stderr) {
                    if (-not $stderr.Finish(3000)) { throw 'Timed out draining standard error.' }
                    $sample.stderr = $stderr.Text
                }
                if ($sample.killed -or $sample.exitCode -ne 0) { throw 'Process failed to shut down normally.' }
                if ($Case.Name -ne 'log' -and $null -ne $sample.logMarkerMs) { throw 'Unexpected log view.' }
            }
        } catch {
            $sample.status = 'failed'
            if (-not $sample.error) { $sample.error = $_.Exception.Message }
        } finally {
            if ($process) { $process.Dispose() }
            Save-PerformanceJson $sample (Join-Path $runRoot "gui/$id.json")
        }
    }
    if ($sample.status -ne 'ok') { throw "${id}: $($sample.error) See gui/$id.json." }
    if ($Iteration -ne 0) { Remove-PerformanceDirectory $profile }
    return $sample
}
