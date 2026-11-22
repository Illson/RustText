#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Measure')]
param(
    [Parameter(ParameterSetName = 'Measure')][string]$BaselineRef = 'v2.4.0',
    [Parameter(ParameterSetName = 'Measure')][string]$CandidateRef = 'HEAD',
    [Parameter(ParameterSetName = 'Measure')][ValidateRange(2, 100)][int]$Iterations = 6,
    [Parameter(ParameterSetName = 'Measure')][ValidateRange(500, 60000)][int]$MemorySampleMs = 2000,
    [Parameter(ParameterSetName = 'Measure')][int]$OrderSeed = 20261121,
    [Parameter(ParameterSetName = 'Measure')][string]$OutputDirectory,
    [Parameter(ParameterSetName = 'Measure')][switch]$Smoke,
    [Parameter(ParameterSetName = 'Measure')][switch]$CoreOnly,
    [Parameter(ParameterSetName = 'Render', Mandatory)][switch]$RenderFromRawJson,
    [Parameter(ParameterSetName = 'Render', Mandatory)][string]$RawJsonPath,
    [Parameter(ParameterSetName = 'Render', Mandatory)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($RenderFromRawJson) {
    . (Join-Path $PSScriptRoot 'Performance/Report.ps1')
    $raw = (Resolve-Path -LiteralPath $RawJsonPath).Path
    $report = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
    if (Test-Path -LiteralPath $report) { throw "Choose a new report path: $report" }
    $link = [IO.Path]::GetRelativePath((Split-Path -Parent $report), $raw).Replace('\', '/')
    $link = ($link.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    Write-PerformanceReport (Get-Content -LiteralPath $raw -Raw -Encoding utf8 | ConvertFrom-Json) $report $link
    return
}

if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Run the benchmark in Windows x64 PowerShell.'
}
if (-not $CoreOnly -and (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0)) {
    throw 'GUI scenarios require an interactive Windows session. Use -CoreOnly without a desktop.'
}
if ($Iterations % 2 -ne 0) { throw '-Iterations must be even to alternate the order of the paired samples.' }
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
function Resolve-PerformanceRef([string]$Ref) {
    $sha = & git -C $root rev-parse --verify --end-of-options "$Ref^{commit}"
    if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw "Cannot resolve Git commit: $Ref" }
    return $sha
}
$revisions = [ordered]@{ baseline = Resolve-PerformanceRef $BaselineRef; candidate = Resolve-PerformanceRef $CandidateRef }
if (-not $OutputDirectory) {
    $id = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $root "target/performance/$id"
}
$runRoot = [IO.Path]::TrimEndingDirectorySeparator(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))
$volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($runRoot))
$minimumFree = if ($CoreOnly -or $Smoke) { 1GB } else { 12GB }
if (-not $volume.IsReady -or $volume.AvailableFreeSpace -lt $minimumFree) {
    throw "Need at least $($minimumFree / 1GB) GiB free on $($volume.Name)."
}

# Create without -Force: never reuse an output directory supplied by the caller.
$null = New-Item -ItemType Directory -Path $runRoot
$workRoot = Join-Path $runRoot 'work'
$null = New-Item -ItemType Directory -Path $workRoot
Write-Host "Performance output: $runRoot"

function Save-PerformanceJson($Value, [string]$Path) {
    ConvertTo-Json -InputObject $Value -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-LoggedCommand([string]$Name, [string]$Command, [string[]]$Arguments) {
    $prefix = Join-Path $runRoot "logs/$Name"
    Save-PerformanceJson $Arguments "$prefix.args.json"
    Write-Host "$Name ($prefix.log)"
    & $Command @Arguments *> "$prefix.log"
    $exitCode = $LASTEXITCODE
    Set-Content -LiteralPath "$prefix.exitcode.txt" -Value $exitCode
    if ($exitCode -ne 0) { throw "$Name exited with code $exitCode; see $prefix.log." }
    "$prefix.log"
}

function Get-PerformanceFileIdentity([string]$Path) {
    $info = Get-Item -LiteralPath $Path
    [ordered]@{ name = $info.Name; bytes = $info.Length; sha256 = (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
}

function Get-PerformanceExecutable([string]$Path) {
    $file = Get-PerformanceFileIdentity $Path
    $reader = [Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.PEHeaders.CoffHeader.Machine -ne 'Amd64' -or $reader.PEHeaders.CorHeader) {
            throw "Expected Native AOT Windows x64 executable: $Path"
        }
    } finally { $reader.Dispose() }
    $file.version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion
    $file
}

function Remove-PerformanceDirectory([string]$Path) {
    if ((Split-Path -Parent $workRoot) -ne $runRoot -or (Split-Path -Leaf $workRoot) -cne 'work') {
        throw "Refusing to remove unexpected work directory: $workRoot"
    }
    $Path = [IO.Path]::GetFullPath($Path)
    if ($Path -ne $workRoot -and -not $Path.StartsWith("$workRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside the performance workspace: $Path"
    }
    for ($ancestor = [IO.DirectoryInfo]::new($Path); $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to clean through a reparse point: $($ancestor.FullName)"
        }
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        foreach ($child in (Get-ChildItem -LiteralPath ($pending.Pop()) -Force)) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean a work directory containing a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$data = [ordered]@{
    schema = 'rusttext-performance-v1'; status = 'running'; startedUtc = [datetime]::UtcNow.ToString('o')
    smoke = [bool]$Smoke; coreOnly = [bool]$CoreOnly; iterations = $Iterations
    memorySampleTargetMs = $MemorySampleMs; orderSeed = $OrderSeed; revisions = $revisions
    environment = $null; harness = @(); binaries = [ordered]@{}; guiFixtures = @(); coreFixtures = @()
    guiRuns = [Collections.Generic.List[object]]::new(); coreRuns = [Collections.Generic.List[object]]::new()
    error = $null; cleanupError = $null
}
$rawPath = Join-Path $runRoot 'raw.json'
try {
    Save-PerformanceJson $data $rawPath
    $null = New-Item -ItemType Directory -Path (Join-Path $runRoot 'logs')
    $fixtureRoot = Join-Path $workRoot 'fixtures'
    $null = New-Item -ItemType Directory -Path $fixtureRoot
    $data.environment = [ordered]@{
        os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
        cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
        physicalMemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate)
        volume = [ordered]@{ format = $volume.DriveFormat; freeBytes = $volume.AvailableFreeSpace }
        disks = @(Get-CimInstance Win32_DiskDrive | Select-Object Model, InterfaceType, MediaType, Size)
        powershell = $PSVersionTable.PSVersion.ToString(); dotnetSdk = $null
    }
    $harnessFiles = @('Measure-RustTextPerformance.ps1', 'Performance/Gui.ps1', 'Performance/WindowsProbe.cs',
        'Performance/Report.ps1', 'Performance/CoreBench/CoreBench.csproj', 'Performance/CoreBench/Baseline/Baseline.csproj',
        'Performance/CoreBench/Candidate/Candidate.csproj', 'Performance/CoreBench/CoreAdapter.props',
        'Performance/CoreBench/EditorCaretStub.cs', 'Performance/CoreBench/CoreAdapter.cs', 'Performance/CoreBench/Program.cs')
    $harness = Join-Path $workRoot 'harness'
    $data.harness = @(foreach ($file in $harnessFiles) {
        $dest = Join-Path $harness $file
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $dest
        [ordered]@{ path = $file; sha256 = (Get-PerformanceFileIdentity $dest).sha256 }
    })
    . (Join-Path $harness 'Performance/Report.ps1')
    $apps = @('baseline', 'candidate')
    $sources = @{}
    $executables = @{}
    foreach ($app in $apps) {
        $archive = Join-Path $workRoot "$app.zip"
        $source = $sources[$app] = Join-Path $workRoot "$app-source"
        $sha = $revisions[$app]
        $null = Invoke-LoggedCommand "$app-source" git @('-C', $root, 'archive', '--format=zip', "--output=$archive", $sha)
        Expand-Archive -LiteralPath $archive -DestinationPath $source
        Push-Location -LiteralPath $source
        try {
            $sdkLog = Invoke-LoggedCommand "$app-sdk" dotnet @('--version')
            $sdk = (Get-Content -LiteralPath $sdkLog -Raw).Trim()
            if ($sdk -notmatch '^11\.' -or ($data.environment.dotnetSdk -and $data.environment.dotnetSdk -ne $sdk)) {
                throw 'Both sources and the harness must use the same .NET 11 SDK.'
            }
            $data.environment.dotnetSdk = $sdk
            if (-not $CoreOnly) {
                $output = Join-Path $workRoot "$app-publish"
                $null = Invoke-LoggedCommand "$app-publish" dotnet @('publish', 'src/RustText/RustText.csproj',
                    '-c', 'Release', '-r', 'win-x64', "-p:SourceRevisionId=$sha", '-o', $output, '--nologo')
                $files = @(Get-ChildItem -LiteralPath $output -Force)
                if ($files.Count -ne 1 -or $files[0].Name -cne 'rusttext.exe' -or $files[0].PSIsContainer) {
                    throw "Expected just rusttext.exe from $app."
                }
                $executables[$app] = $files[0].FullName
                $data.binaries[$app] = Get-PerformanceExecutable $executables[$app]
                if ($data.binaries[$app].version -notlike "*+$sha") { throw "Unexpected $app EXE revision metadata." }
            }
        } finally { Pop-Location }
    }

    $coreProject = Join-Path $harness 'Performance/CoreBench/CoreBench.csproj'
    $coreOutput = Join-Path $workRoot 'core-publish'
    Push-Location -LiteralPath $sources.candidate
    try {
        $null = Invoke-LoggedCommand 'core-publish' dotnet @('publish', $coreProject, '-c', 'Release', '-r', 'win-x64',
            "-p:BaselineSource=$($sources.baseline)", "-p:CandidateSource=$($sources.candidate)", '-o', $coreOutput, '--nologo')
    } finally { Pop-Location }
    $coreExe = Join-Path $coreOutput 'CoreBench.exe'
    $data.binaries.core = Get-PerformanceExecutable $coreExe

    if (-not $CoreOnly) {
        . (Join-Path $harness 'Performance/Gui.ps1')
        $null = New-Item -ItemType Directory -Path (Join-Path $runRoot 'gui')
        $text = Join-Path $fixtureRoot 'editor.txt'
        $json = Join-Path $fixtureRoot 'records.json'
        $log = Join-Path $fixtureRoot 'events.log'
        $data.guiFixtures = @(
            New-PerformanceFixture $text $(if ($Smoke) { 64KB } else { 40MB }) '' "Editor plain text – русский текст and additional content for scrolling.`r`nLine without timestamps or level labels.`n" ''
            New-PerformanceFixture $json $(if ($Smoke) { 64KB } else { 10MB }) "[`n" "{`"id`":12345,`"description`":`"Обработка записи`",`"active`":true},`n" '{"id":0}]'
            New-PerformanceFixture $log $(if ($Smoke) { 2MB } else { 10GB }) '' "2026-11-21T00:30:00Z INFO Worker Сервис started`n2026-11-21T00:30:01Z WARN slow response, retry requested`r`n2026-11-21T00:30:02Z ERROR upstream reset`n" ''
        )
        [hashtable[]]$cases = @(
            @{ Name = 'empty-cold'; Cache = 'cold'; Path = $null },
            @{ Name = 'empty'; Cache = 'warm'; Path = $null },
            @{ Name = 'text'; Cache = 'warm'; Path = $text },
            @{ Name = 'json'; Cache = 'warm'; Path = $json },
            @{ Name = 'log'; Cache = 'warm'; Path = $log }
        )
        $warm = @{}
        foreach ($app in $apps) {
            $null = Invoke-PerformanceGuiSample @{ Name = 'warmup'; Cache = 'cold' } $app $executables[$app] 0 0 $MemorySampleMs ''
            $warm[$app] = Join-Path $workRoot "profiles/warmup-0-$app/native"
            $dlls = @(Get-ChildItem -LiteralPath $warm[$app] -Filter '*.dll' -Recurse -File)
            if ($dlls.Count -eq 0) { throw "No extracted DLLs in the $app warm profile." }
            $data.binaries[$app].extractedDllBytes = ($dlls | Measure-Object -Property Length -Sum).Sum
        }
        $random = [Random]::new($OrderSeed)
        for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
            $shuffled = $cases.Clone()
            $random.Shuffle[hashtable]($shuffled)
            $orderedApps = if ($iteration % 2) { $apps } else { @($apps[1], $apps[0]) }
            Write-Host "GUI block $iteration/$Iterations"
            foreach ($case in $shuffled) {
                for ($order = 1; $order -le 2; $order++) {
                    $app = $orderedApps[$order - 1]
                    $data.guiRuns.Add((Invoke-PerformanceGuiSample $case $app $executables[$app] $iteration $order $MemorySampleMs $warm[$app]))
                }
            }
            Save-PerformanceJson $data $rawPath
        }
    }

    $coreSize = if ($Smoke) { 2MB } else { 64MB }
    $coreLog = Invoke-LoggedCommand 'core-measure' $coreExe @("$Iterations", "$coreSize", (Join-Path $fixtureRoot 'core.log'))
    $culture = [Globalization.CultureInfo]::InvariantCulture
    foreach ($line in Get-Content -LiteralPath $coreLog -Encoding utf8) {
        $fields = $line.Split("`t")
        if ($fields[0] -ceq 'sample' -and $fields.Count -eq 7) {
            $data.coreRuns.Add([ordered]@{
                scenario = $fields[1]; app = $fields[2]; iteration = [int]$fields[3]; order = [int]$fields[4]
                ms = [double]::Parse($fields[5], $culture); managedBytes = [long]$fields[6]; status = 'ok'
            })
        } elseif ($fields[0] -ceq 'fixture' -and $fields.Count -eq 4 -and $fields[3] -match '^[a-f0-9]{64}$') {
            $data.coreFixtures += [ordered]@{ name = $fields[1]; bytes = [long]$fields[2]; sha256 = $fields[3].ToLowerInvariant() }
        } else { throw "Unexpected core benchmark output: $line" }
    }
    if ((($data.coreFixtures.name | Sort-Object) -join ',') -cne 'editor,json,log') { throw 'Incomplete core fixtures.' }
    foreach ($file in $data.harness) {
        if ((Get-PerformanceFileIdentity (Join-Path $harness $file.path)).sha256 -cne $file.sha256) {
            throw "Harness snapshot changed during the run: $($file.path)"
        }
    }
    $data.status = 'complete'
    Write-PerformanceReport $data (Join-Path $runRoot 'report.md')
} catch {
    $data.status = 'failed'
    $data.error = $_.Exception.Message
    throw
} finally {
    try { Remove-PerformanceDirectory $workRoot } catch {
        $data.cleanupError = $_.Exception.Message
        Write-Warning "Work cleanup failed: $($data.cleanupError)"
    }
    Save-PerformanceJson $data $rawPath
}
Write-Host "Performance comparison complete: $runRoot"
