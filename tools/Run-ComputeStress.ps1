# 可重复的真实 Compute 并行验证；屏障本身不挂起，迭代状态依赖运行时 Worker。
[CmdletBinding()]
param(
    [ValidateRange(1, 2147483647)][int]$Iterations = 2000000000,
    [ValidateRange(0, 254)][int]$Workers = 0,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 120,
    [switch]$UseExistingBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$previousWorkers = $env:RIGI_COMPUTE_WORKERS
$previousMemtrack = $env:RIGI_RT_MEMTRACK
Push-Location $repo
try {
    if ($Workers -gt 0) { $env:RIGI_COMPUTE_WORKERS = $Workers.ToString() }
    if (-not $UseExistingBuild) {
        & pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList 'build --no-restore' -TimeoutSeconds 180
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $stem = 'playground/compute_pool_stress'
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'stress/compute.rg')).Replace('100000', $Iterations.ToString())
    [IO.File]::WriteAllText((Join-Path $repo "$stem.rg"), $source)
    & pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList "run --no-build -- compile --file $stem.rg --emit-bil $stem.bil" -TimeoutSeconds 180
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $files = (Get-ChildItem "$stem*.bil" | ForEach-Object FullName) -join ' '
    & pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList "run --no-build -- native --file $files --out $stem.exe" -TimeoutSeconds 300
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $env:RIGI_RT_MEMTRACK = '1'
    $stamp = Get-Date -Format yyyyMMdd_HHmmssfff
    [IO.File]::WriteAllText((Join-Path $repo "$stem.$stamp.rg"), $source)
    & pwsh tools/Watch-Command.ps1 -Command (Join-Path $repo "$stem.exe") -TimeoutSeconds $TimeoutSeconds -StdoutPath "$stem.$stamp.stdout.txt" -StderrPath "$stem.$stamp.stderr.txt" -MetricsPath "$stem.$stamp.metrics.json" -FailOnStderr
    $code = $LASTEXITCODE
    $metrics = Get-Content "$stem.$stamp.metrics.json" -Raw | ConvertFrom-Json
    Write-Host ("exit={0}; wall={1:N3}s; CPU={2:N3}s; average cores={3:N2}; evidence={4}.{5}.*" -f $code, $metrics.elapsedSeconds, $metrics.cpuSeconds, ($metrics.cpuSeconds / $metrics.elapsedSeconds), $stem, $stamp)
    exit $code
} finally {
    $env:RIGI_COMPUTE_WORKERS = $previousWorkers
    $env:RIGI_RT_MEMTRACK = $previousMemtrack
    Pop-Location
}
