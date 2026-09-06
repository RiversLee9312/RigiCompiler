# 可重跑的 opt-in 原生 MQ 压力入口；不加入 test --all。
[CmdletBinding()]
param(
    [ValidateRange(1, 1000000)][int]$PerSender = 62500,
    [int]$TimeoutSeconds = 1800,
    [ValidateRange(1, 1800)][int]$NoProgressSeconds = 60,
    [ValidateRange(0, 254)][int]$ComputeWorkers = 0,
    [switch]$ComputeOnly,
    [switch]$Resources,
    [switch]$UseExistingBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$previousMemtrack = $env:RIGI_RT_MEMTRACK
$previousWorkers = $env:RIGI_COMPUTE_WORKERS
Push-Location $repo
try {
    if ($ComputeWorkers -gt 0) { $env:RIGI_COMPUTE_WORKERS = $ComputeWorkers.ToString() }
    if (-not $UseExistingBuild) {
        # 标准库与 C 运行时是内嵌资源，必须先更新程序集。
        & pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList 'build --no-restore' -TimeoutSeconds 180
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $sourceName = if ($Resources) { 'mq_resources' } else { 'mq' }
    $stem = if ($Resources) { 'pure_mq_resources_run' } else { 'pure_mq_stress_run' }
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "stress/$sourceName.rg"))
    if (-not $Resources) {
        $quantities = @{ '1000000' = ([long]$PerSender * 16).ToString(); '250000' = ([long]$PerSender * 4).ToString(); '62500' = $PerSender.ToString() }
        $quantities['12500'] = ([Math]::Max(1, [Math]::Min(1000, [int]($PerSender / 10)))).ToString()
        $source = [regex]::Replace($source, '\b(1000000|250000|62500|12500)\b', { param($match) $quantities[$match.Value] })
    }
    if ($ComputeOnly) { $source = $source.Replace("new IOExecutor()", "new ComputeExecutor()") }
    [IO.File]::WriteAllText((Join-Path $repo "playground/$stem.rg"), $source)
    & pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList "run --no-build -- compile --file playground/$stem.rg --emit-bil playground/$stem.bil" -TimeoutSeconds 180
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $bilFiles = @("playground/$stem.bil")
    foreach ($namespace in @('core', 'core.io', 'core.collections', 'core.coroutine', 'core.messaging', 'core.serialization', 'core.time')) {
        $bilFiles += "playground/$stem.$namespace.bil"
    }
    $nativeArgs = 'run --no-build -- native --file ' + ($bilFiles -join ' ') + " --out playground/$stem.exe"
    & pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList $nativeArgs -TimeoutSeconds 300
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    # 看门狗直接启动最终 exe，计时与 RSS 不包含编译器。
    $env:RIGI_RT_MEMTRACK = '1'
    $stamp = Get-Date -Format yyyyMMdd_HHmmssfff
    $evidence = "playground/$stem.$stamp"
    [IO.File]::WriteAllText((Join-Path $repo "$evidence.rg"), $source)
    $idleLimit = if ($Resources) { 0 } else { $NoProgressSeconds }
    & pwsh tools/Watch-Command.ps1 -Command (Join-Path $repo "playground/$stem.exe") -TimeoutSeconds $TimeoutSeconds -NoProgressSeconds $idleLimit -StdoutPath "$evidence.stdout.txt" -MetricsPath "$evidence.metrics.json" -StderrPath "$evidence.stderr.txt" -FailOnStderr
    $runCode = $LASTEXITCODE
    Write-Host "evidence=$evidence.*"
    $metrics = Get-Content -LiteralPath "$evidence.metrics.json" -Raw | ConvertFrom-Json
    Write-Host ("exit={0}; seconds={1:N3}; peak RSS bytes={2}" -f $runCode, $metrics.elapsedSeconds, $metrics.peakRssBytes)
    if ((-not $Resources) -and ($runCode -eq 0)) {
        Write-Host ("posts={0}; deliveries={1}; deliveries/s={2:N0}" -f ($PerSender * 4), ($PerSender * 16), (($PerSender * 16) / $metrics.elapsedSeconds))
    }
    exit $runCode
} finally {
    $env:RIGI_RT_MEMTRACK = $previousMemtrack
    $env:RIGI_COMPUTE_WORKERS = $previousWorkers
    Pop-Location
}
