param(
    [string]$App = (Join-Path $PSScriptRoot 'release-ui-v0.3.2\JFWorkbench.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'verification\v0.3.2-ui')
)
$ErrorActionPreference = 'Stop'
$App = [IO.Path]::GetFullPath($App)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $App -PathType Leaf)) { throw 'Build the UI release first.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cases = [Collections.Generic.List[object]]::new()
foreach ($dpi in @(144, 96, 192)) {
    foreach ($size in @('1100x720', '1440x900', 'maximized')) {
        $cases.Add([pscustomobject]@{ dpi=$dpi; size=$size; state='default'; scale='1.0' })
    }
}
foreach ($state in @('expanded', 'status', 'logs', 'focus', 'keyboard')) {
    $cases.Add([pscustomobject]@{ dpi=144; size='1440x900'; state=$state; scale='1.0' })
}
foreach ($scale in @('0.9', '1.5')) {
    $cases.Add([pscustomobject]@{ dpi=144; size='1440x900'; state='default'; scale=$scale })
}
$results = [Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $name = 'dpi-' + $case.dpi + '-' + $case.size + '-' + $case.state + '-ui-' + $case.scale
    $screenshot = Join-Path $OutputDirectory ($name + '.png')
    $arguments = '--demo --ui-review --review-dpi ' + $case.dpi + ' --review-size ' + $case.size +
        ' --review-state ' + $case.state + ' --ui-scale ' + $case.scale + ' --screenshot "' + $screenshot + '"'
    $process = Start-Process -FilePath $App -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw ('UI capture did not exit; not terminating it: ' + $name) }
    if ($process.ExitCode -ne 0) { throw ('UI capture failed: ' + $name + ', exit ' + $process.ExitCode) }
    $evidence = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($screenshot, '.json')) -Raw | ConvertFrom-Json
    if (-not $evidence.success -or $evidence.gatewayContacted -or $evidence.browserOpened) { throw ('Invalid offline evidence: ' + $name) }
    $results.Add($evidence)
    [pscustomobject]@{capture=$name;kind=$evidence.evidenceKind;webPercent=$evidence.visiblePercent}
}
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'ui-measurements.json') -Encoding UTF8

