#Requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../tools/Performance/Gui.ps1')

$directory = [IO.Directory]::CreateTempSubdirectory('rusttext-readiness-')
try {
    $recent = Join-Path $directory.FullName 'recent-files.json'
    $expected = Join-Path $directory.FullName 'events.log'
    $other = Join-Path $directory.FullName 'other/events.log'
    function Assert-OpenedMode($Entries, $Mode) {
        if ($null -ne $Entries) {
            $json = ConvertTo-Json -InputObject $Entries -Depth 5
            [IO.File]::WriteAllText($recent, $json)
        }
        $actual = Get-PerformanceOpenedMode $recent $expected
        if ($actual -cne $Mode) { throw "Expected '$Mode', got '$actual' from $json" }
    }

    Assert-OpenedMode $null $null
    Assert-OpenedMode @() $null
    Assert-OpenedMode @(@{ path = $other; view_as_log = $false; line_count = 2; log = $null }) $null
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $false; line_count = $null; log = $null }) $null
    Assert-OpenedMode @(@{ path = $expected.ToUpperInvariant(); view_as_log = $false; line_count = 2; log = $null }) 'editor'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $true; log = @{} }) $null
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $true; log = @{ edit_pages = $true } }) 'paged-editor'
    Assert-OpenedMode @(@{ path = $expected; view_as_log = $true; log = @{ edit_pages = $false } }) 'log'
    Write-Host 'Performance document readiness checks passed.'
} finally {
    if ([IO.File]::Exists($recent)) { [IO.File]::Delete($recent) }
    $directory.Delete()
}
