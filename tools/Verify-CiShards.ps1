#Requires -Version 7.0
<# .SYNOPSIS
核对本轮所有独立 runner 的 JSON/TRX；只读证据，不再执行测试或构建。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsRoot,
    [Parameter(Mandatory)][string]$ExpectedSha,
    [Parameter(Mandatory)][string]$ExpectedRunId,
    [Parameter(Mandatory)][string]$ExpectedAttempt,
    [ValidateRange(1, 256)][int]$ShardCount = 16,
    [string[]]$Rids = @('win-x64', 'linux-x64')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Evidence([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "CI 全量证据无效：$Message" }
}
function Get-Ordered([string[]]$Values) {
    [string[]]$copy = @()
    if ($null -ne $Values) { $copy = [string[]]$Values.Clone() }
    [Array]::Sort($copy, [StringComparer]::Ordinal)
    return ,$copy
}
function Assert-SameIds([string[]]$Actual, [string[]]$Expected, [string]$Context) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $Actual) { Assert-Evidence (-not [string]::IsNullOrWhiteSpace($id) -and $seen.Add($id)) "$Context 出现空或重复身份" }
    $actualSorted = Get-Ordered $Actual
    $expectedSorted = Get-Ordered $Expected
    Assert-Evidence ($actualSorted.Length -eq $expectedSorted.Length) "$Context 数量不同"
    for ($position = 0; $position -lt $actualSorted.Length; $position++) {
        Assert-Evidence ($actualSorted[$position] -ceq $expectedSorted[$position]) "$Context 身份缺失或被替换"
    }
}
function Read-Json([string]$Path) {
    Assert-Evidence (Test-Path -LiteralPath $Path -PathType Leaf) "缺少 $Path"
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 100
}
function Get-DirectoryDigest($Inventory) {
    $rows = @($Inventory.frameworkCases)
    $byId = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $rows) {
        Assert-Evidence ($row.inputCount -is [long] -or $row.inputCount -is [int]) '输入数不是整数'
        Assert-Evidence ($row.inputCount -gt 0 -and -not $byId.ContainsKey($row.id)) '目录重复 ID 或输入数无效'
        $byId.Add($row.id, $row)
    }
    $lines = foreach ($id in (Get-Ordered ([string[]]@($byId.Keys)))) {
        $row = $byId[$id]
        "$($row.id)`t$($row.suite)`t$($row.inputCount)"
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))).ToLowerInvariant()
}
function Get-ShardIds($Inventory, [int]$Index, [int]$Count) {
    $suites = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in $Inventory.frameworkCases) {
        if (-not $suites.ContainsKey($row.suite)) { $suites.Add($row.suite, [Collections.Generic.List[string]]::new()) }
        $suites[$row.suite].Add($row.id)
    }
    $ids = [Collections.Generic.List[string]]::new()
    foreach ($suite in $suites.Values) {
        $sorted = Get-Ordered ([string[]]$suite.ToArray())
        for ($position = $Index; $position -lt $sorted.Length; $position += $Count) { $ids.Add($sorted[$position]) }
    }
    return ,$ids.ToArray()
}
function Get-BudgetKey($Budget) {
    Assert-Evidence ($Budget.semanticsSeeds -eq 3000 -and $Budget.stressSeeds -eq 600 -and $Budget.lexerSeeds -eq 6000) 'CI fuzz 默认预算被缩减或改变'
    return (@($Budget.semanticsSeeds, $Budget.stressSeeds, $Budget.lexerSeeds) + (Get-Ordered ([string[]]@($Budget.gates)))) -join '|'
}
function Assert-Identity($Identity, [string]$Rid, [int]$Index, [string]$Digest) {
    Assert-Evidence ($Identity.sha -ceq $ExpectedSha -and $Identity.runId -ceq $ExpectedRunId -and $Identity.attempt -ceq $ExpectedAttempt) 'SHA/run/attempt 不属于本轮'
    Assert-Evidence ($Identity.rid -ceq $Rid -and $Identity.shardIndex -eq $Index -and $Identity.shardCount -eq $ShardCount) 'RID/分片身份错误'
    Assert-Evidence ($Identity.directoryDigest -ceq $Digest) '完整目录摘要不同'
}
function Read-Trx([string]$Path) {
    Assert-Evidence (Test-Path -LiteralPath $Path -PathType Leaf) "缺少新鲜 TRX：$Path"
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader); return $document }
    finally { $reader.Dispose() }
}

Assert-Evidence (Test-Path -LiteralPath $ResultsRoot -PathType Container) '没有下载到分片证据目录'
Assert-Evidence (-not [string]::IsNullOrWhiteSpace($ExpectedSha) -and $ExpectedRunId -match '^\d+$' -and $ExpectedAttempt -match '^\d+$') '预期来源身份无效'
$files = @(Get-ChildItem -LiteralPath $ResultsRoot -Filter 'shard-manifest.json' -File -Recurse)
Assert-Evidence ($files.Count -eq $Rids.Count * $ShardCount) '分片数量不足或存在多余证据'
$shards = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($file in $files) {
    $manifest = Read-Json $file.FullName
    Assert-Evidence ($manifest.schemaVersion -eq 1 -and $manifest.status -ceq 'complete' -and $manifest.exitCode -eq 0) '分片失败、取消或未完成'
    Assert-Evidence ($Rids -ccontains $manifest.rid -and $manifest.shardIndex -ge 0 -and $manifest.shardIndex -lt $ShardCount) '未知 RID/片号'
    $key = "$($manifest.rid)/$($manifest.shardIndex)"
    Assert-Evidence (-not $shards.ContainsKey($key)) '同一片重复上传'
    $shards.Add($key, @{ manifest = $manifest; directory = $file.DirectoryName })
}

foreach ($rid in $Rids) {
    Assert-Evidence ($shards.ContainsKey("$rid/0")) "$rid 缺少片 0"
    # 预期全集来自发布宿主在执行前独立导出的完整 inventory，绝不用结果并集代替。
    $canonical = Read-Json (Join-Path $shards["$rid/0"].directory 'inventory.json')
    Assert-Evidence ($canonical.schemaVersion -eq 1) '完整目录版本无效'
    $allIds = [string[]]@($canonical.frameworkCases | ForEach-Object { $_.id })
    Assert-SameIds $allIds $allIds "$rid 完整目录"
    $contracts = [string[]]@($canonical.frameworkContracts)
    Assert-SameIds $contracts $contracts "$rid 契约目录"
    $generic = 'RigiCompiler.TUnitTests.GenericDiscoveryTests.ClosedGeneric<int>'
    Assert-Evidence ($contracts.Length -eq 36 -and $contracts -ccontains $generic) '完整 36 契约/闭合泛型目录缺失'
    $digest = Get-DirectoryDigest $canonical
    $budgetKey = Get-BudgetKey $shards["$rid/0"].manifest.budgets
    foreach ($suite in @(@{ name = 'SemanticsFuzz'; count = 3000 }, @{ name = 'StressFuzz'; count = 600 }, @{ name = 'LexerFuzz'; count = 6000 })) {
        $inputs = ($canonical.frameworkCases | Where-Object { $_.suite -ceq $suite.name -and $_.granularity -ceq 'seed-batch' } | Measure-Object inputCount -Sum).Sum
        Assert-Evidence ($inputs -eq $suite.count) "目录遗漏 $($suite.name) seed 输入"
    }
    $union = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [long]$assertions = 0
    for ($index = 0; $index -lt $ShardCount; $index++) {
        Assert-Evidence ($shards.ContainsKey("$rid/$index")) "$rid 缺少片 $index"
        $entry = $shards["$rid/$index"]; $manifest = $entry.manifest
        $inventory = Read-Json (Join-Path $entry.directory 'inventory.json')
        Assert-Evidence ((Get-DirectoryDigest $inventory) -ceq $digest) "$rid/$index 目录不一致"
        Assert-SameIds ([string[]]@($inventory.frameworkContracts)) $contracts "$rid/$index 完整契约目录"
        Assert-Identity $manifest $rid $index $digest
        Assert-Evidence ((Get-BudgetKey $manifest.budgets) -ceq $budgetKey) '各片预算或慢门控不同'
        $expected = Get-ShardIds $canonical $index $ShardCount
        Assert-Evidence ($expected.Length -gt 0) '出现空片'
        Assert-SameIds ([string[]]@($manifest.expectedProviderIds)) $expected "$rid/$index 预期分区"
        [string[]]$expectedContracts = @()
        if ($index -eq 0) { $expectedContracts = $contracts }
        Assert-SameIds ([string[]]@($manifest.expectedContracts)) ([string[]]$expectedContracts) "$rid/$index 预期契约"
        $journal = Read-Json (Join-Path $entry.directory 'case-results.json')
        Assert-Evidence ($journal.schemaVersion -eq 1 -and $journal.compilerCaseRows -eq $expected.Length -and $journal.failures -eq 0) 'journal 行数或失败数错误'
        Assert-Identity $journal.ci $rid $index $digest
        Assert-Evidence ((Get-BudgetKey $journal.ci.budgets) -ceq $budgetKey) 'journal 预算身份错误'
        Assert-SameIds ([string[]]@($journal.cases | ForEach-Object { $_.id })) $expected "$rid/$index 实际 provider"
        $caseById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        [long]$caseAssertions = 0
        foreach ($case in $journal.cases) {
            Assert-Evidence ($case.status -cin @('Pass', 'Skip') -and $case.failures -eq 0 -and $case.assertions -ge 0) 'provider 失败、取消或计数无效'
            Assert-Evidence (($case.status -ceq 'Pass' -and $case.assertions -gt 0) -or ($case.status -ceq 'Skip' -and -not [string]::IsNullOrWhiteSpace($case.skipReason))) 'provider Pass/Skip 证据无效'
            Assert-Evidence ($union.Add($case.id)) 'provider 跨片重复执行'
            $caseById.Add($case.id, $case)
            $caseAssertions += $case.assertions
        }
        Assert-Evidence ($journal.assertions -eq $caseAssertions) '内部断言汇总错误'
        $assertions += $caseAssertions
        Assert-Evidence ($manifest.trxFile -match ('^rigi-shard-' + $index + '-\d+\.trx$')) 'TRX 文件名错误或越出本片目录'
        $trx = Read-Trx (Join-Path $entry.directory $manifest.trxFile)
        $rows = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        $definitions = @($trx.SelectNodes("//*[local-name()='UnitTest']"))
        $rowCount = $expected.Length + @($expectedContracts).Count
        Assert-Evidence ($rows.Count -eq $rowCount -and $definitions.Count -eq $rowCount) 'TRX 缺少完成行或存在多余行'
        $resultById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        foreach ($row in $rows) {
            $id = $row.GetAttribute('testId')
            Assert-Evidence (-not [string]::IsNullOrWhiteSpace($id) -and -not $resultById.ContainsKey($id)) 'TRX 完成行重复或没有身份'
            Assert-Evidence ($row.GetAttribute('outcome') -cin @('Passed', 'NotExecuted')) 'TRX 失败、取消或未完成'
            $resultById.Add($id, $row)
        }
        $definitionIds = [string[]]@($definitions | ForEach-Object { $_.GetAttribute('id') })
        Assert-SameIds $definitionIds ([string[]]@($resultById.Keys)) 'TRX 定义与完成行'
        $actualContracts = [Collections.Generic.List[string]]::new()
        $actualProviders = [Collections.Generic.List[string]]::new()
        foreach ($definition in $definitions) {
            $methods = @($definition.SelectNodes("./*[local-name()='TestMethod']"))
            Assert-Evidence ($methods.Count -eq 1) 'TRX TestMethod 定义无效'
            $method = $methods[0]
            if ($method.GetAttribute('className') -cne 'RigiCompiler.TUnitTests.CompilerCaseTests') {
                $identity = $method.GetAttribute('className') + '.' + $method.GetAttribute('name')
                $actualContracts.Add($identity)
                if ($identity -ceq $generic) { Assert-Evidence ($resultById[$definition.GetAttribute('id')].GetAttribute('outcome') -ceq 'Passed') '闭合泛型未实际通过' }
            } else {
                # TUnit DataRow DisplayName 是稳定 ID；把 TRX 与逐 ID journal 双向关联，不能只信行数。
                $providerId = $definition.GetAttribute('name')
                Assert-Evidence ($caseById.ContainsKey($providerId)) 'TRX provider 身份未出现在实际 journal'
                $actualProviders.Add($providerId)
                $expectedOutcome = if ($caseById[$providerId].status -ceq 'Pass') { 'Passed' } else { 'NotExecuted' }
                Assert-Evidence ($resultById[$definition.GetAttribute('id')].GetAttribute('outcome') -ceq $expectedOutcome) 'TRX 与 journal 状态不一致'
            }
        }
        Assert-SameIds $actualProviders.ToArray() $expected "$rid/$index TRX provider 身份"
        Assert-SameIds $actualContracts.ToArray() ([string[]]$expectedContracts) "$rid/$index 实际契约"
    }
    Assert-SameIds ([string[]]@($union)) $allIds "$rid 全目录并集"
    Write-Host "$rid 全量证明通过：$($allIds.Length) provider + $($contracts.Length) 契约，$assertions 条内部断言，$ShardCount 个互斥分片。"
}
