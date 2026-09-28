#Requires -Version 5.1
<#
.SYNOPSIS
    Setup's DecodeCheck step: does the GPU that drives the TV decode the video formats in 4K?

.DESCRIPTION
    Runs tools\Test-HwDecode.ps1 -Json -NoPlayback, as the launcher's Settings > Display check
    does: what the graphics driver says it decodes; no clip is played. Prints the GPU and each
    format, and fails with a message that stands on its own (the wizard shows it): the Microsoft
    Basic Display Adapter when a graphics chip has no driver, else the formats not decoded.
    Skipped in a virtual machine (no hardware decoder to check).
#>
param([string]$Tool = (Join-Path $PSScriptRoot '..\tools\Test-HwDecode.ps1'))

. "$PSScriptRoot\Common.ps1"

if (-not (Test-Path -LiteralPath $Tool)) { Write-Skipped 'tools\Test-HwDecode.ps1 not found'; return }
if (Test-VirtualMachine) { Write-Skipped 'virtual machine: no hardware video decoder to check'; return }

$report = (& $Tool -Json -NoPlayback | Out-String) | ConvertFrom-Json
$gpu = $report.adapter
if ($gpu.name) {
    Write-Host "  GPU: $($gpu.name), driver $($gpu.driverVersion)$(if ($gpu.drivesTv) { ', drives the TV' })"
    if (@($gpu.otherAdapters).Count) { Write-Host "  Also in the box (not checked): $(@($gpu.otherAdapters) -join ', ')" }
}
foreach ($codec in @($report.codecs)) {
    $what = if ($codec.pass) { '4K' } elseif (-not $codec.driver) { 'not in the driver' } elseif (-not $codec.uhd) { 'not in 4K' } else { "no $($codec.format) output" }
    Write-Host ('    {0,-16} {1}' -f $codec.name, $what)
}
if ($report.cause) { throw $report.cause }
if ($gpu.error) { throw "The decoding check could not run: $($gpu.error)" }
$failed = @($report.codecs | Where-Object { -not $_.pass } | ForEach-Object { $_.name })
if ($failed.Count) { throw "Not hardware decoded in 4K by $($gpu.name): $($failed -join ', ')" }
Write-Same "all $(@($report.codecs).Count) formats hardware decoded in 4K"
