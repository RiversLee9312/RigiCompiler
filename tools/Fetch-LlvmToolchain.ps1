#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    开发机 LLVM 工具链获取脚本（Middleware 的 rigi_rt 现场编译与 lld 链接底座，MW1 起）。

.DESCRIPTION
    策略（MIDDLEWARE_ARCHITECTURE §2 定稿）：
    - CI（GitHub Actions）不跑本脚本：runner 自带 clang/lld（windows-latest:
      C:\Program Files\LLVM，ubuntu-latest: /usr/bin/clang + ld.lld），零下载、
      不耗额外 action 额度；rigi_rt 为普通 C 代码 + lld 链接，容忍 runner 预装版本漂移
      （「锁定 LLVM 20」约束的是 IR 侧的 LLVMSharp/libLLVM，不是这个 C 工具链）。
    - 开发机：PATH 上的 clang/lld 优先；需要钉版工具链或系统未装时跑本脚本——
      下载官方 llvm/llvm-project release（钉 20.1.2 + SHA256 校验），选择性提取
      clang / lld / clang 内建头文件，缓存到 tools/.llvm/<rid>/（gitignored）。
      缓存命中即跳过；重复跑幂等。

    MW1 接线时 native 驱动的工具链解析顺序：--toolchain <目录> → 环境变量
    RIGI_LLVM → 本脚本缓存 tools/.llvm/<rid>/ → PATH/系统安装。

.PARAMETER Rid
    目标 RID（win-x64 / linux-x64）；缺省按当前平台自动判定。

.PARAMETER ArchivePath
    复用本地已下载的 tar.xz（离线/测试）；缺省在线下载。

.PARAMETER Force
    忽略缓存强制重取。

.EXAMPLE
    pwsh tools/Fetch-LlvmToolchain.ps1
#>
[CmdletBinding()]
param(
    [string]$Rid = "",
    [string]$ArchivePath = "",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

$LlvmVersion = "20.1.2"
# 钉版官方 release（https://github.com/llvm/llvm-project/releases/tag/llvmorg-20.1.2）：
#   win-x64   clang+llvm-20.1.2-x86_64-pc-windows-msvc.tar.xz（约 896MB；
#             sha256 为首次获取后钉入的自校验值）
#   linux-x64 LLVM-20.1.2-Linux-X64.tar.xz（约 1.9GB；
#             sha256 取自官方 sigstore 证明 LLVM-20.1.2-Linux-X64.tar.xz.jsonl）
$Toolchains = @{
    "win-x64" = @{
        Url = "https://github.com/llvm/llvm-project/releases/download/llvmorg-20.1.2/clang+llvm-20.1.2-x86_64-pc-windows-msvc.tar.xz"
        Sha256 = "8e771a685cd718303ea0d632a8a95ad7b3cb17068f3952fbefa64a77290324d8"
        # 只取所需部件：clang 驱动 / lld-link（COFF flavor）/ clang 内建头文件资源目录。
        # bin/clang.exe 自包含静态链接，不依赖包内其他 DLL
        BinEntries = @("bin/clang.exe", "bin/lld-link.exe")
        OptionalBinEntries = @()
        ClangExe = "bin/clang.exe"
    }
    "linux-x64" = @{
        Url = "https://github.com/llvm/llvm-project/releases/download/llvmorg-20.1.2/LLVM-20.1.2-Linux-X64.tar.xz"
        Sha256 = "3a392f151375eeed4fd50c6b6f7c7203da37b373a57f220ae58ef62b8aade3cc"
        # bin/clang-20 / bin/lld 可能是主二进制的链接目标（硬链接/符号链接），
        # 缺失不致命（列为可选）；ld.lld 为 ELF flavor
        BinEntries = @("bin/clang", "bin/ld.lld")
        OptionalBinEntries = @("bin/clang-20", "bin/lld")
        ClangExe = "bin/clang"
    }
}

# ===== 平台判定 =====
if ($Rid -eq "") {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) { $Rid = "win-x64" }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)) { $Rid = "linux-x64" }
    else { throw "不支持的平台（仅 win-x64/linux-x64），请用 -Rid 显式指定" }
}
if (-not $Toolchains.ContainsKey($Rid)) { throw "未知 RID: $Rid（支持 win-x64/linux-x64）" }
$spec = $Toolchains[$Rid]

$repoRoot = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repoRoot "tools/.llvm/$Rid"
$marker = Join-Path $dest "VERSION.txt"
$clangPath = Join-Path $dest $spec.ClangExe

# 缓存命中：clang 在且版本标记一致 → 跳过
if (-not $Force -and (Test-Path $clangPath) -and (Test-Path $marker) -and
    ((Get-Content $marker -TotalCount 1) -eq "LLVM $LlvmVersion $Rid")) {
    Write-Host "已缓存 LLVM $LlvmVersion ($Rid)：$dest（-Force 可重取）"
    exit 0
}

New-Item -ItemType Directory -Force $dest | Out-Null

# ===== 获取压缩包 =====
$archive = $ArchivePath
$downloadedHere = $false
if ($archive -eq "") {
    $archive = Join-Path $dest "llvm-$LlvmVersion-$Rid.tar.xz"
    if (-not (Test-Path $archive)) {
        Write-Host "下载 $($spec.Url) ..."
        & curl -fSL --retry 3 -C - -o $archive $spec.Url
        if ($LASTEXITCODE -ne 0) { throw "下载失败（curl 退出码 $LASTEXITCODE）" }
        $downloadedHere = $true
    }
    else {
        Write-Host "复用已下载压缩包 $archive"
    }
}

# ===== SHA256 校验 =====
Write-Host "校验 SHA256 ..."
$actual = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
if ($actual -ne $spec.Sha256) {
    if ($downloadedHere) { Remove-Item $archive -ErrorAction SilentlyContinue }
    throw "SHA256 不匹配：期望 $($spec.Sha256)，实际 $actual"
}

# ===== 选择性提取 =====
# 包根目录名随发行形态变（win: clang+llvm-...；linux: LLVM-...）：读首条目名的
# 第一段路径组件自适应（首条目可能是子目录，如 LLVM-20.1.2-Linux-X64/share）
$firstEntry = (& tar -tf $archive | Select-Object -First 1).Trim()
$rootDir = ($firstEntry -split '/')[0]
if ([string]::IsNullOrWhiteSpace($rootDir)) { throw "无法读取压缩包根目录" }
Write-Host "选择性提取（根 $rootDir，约几分钟）..."
$stage = Join-Path $dest "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

$patterns = @()
foreach ($e in $spec.BinEntries + $spec.OptionalBinEntries) { $patterns += "$rootDir/$e" }
$clangMajor = $LlvmVersion.Split('.')[0]
$patterns += "$rootDir/lib/clang/$clangMajor/include/*"
# tar 通配差异：bsdtar（Windows）默认按 glob 匹配模式；GNU tar（Linux）需显式
# --wildcards 才按 glob 匹配，否则 "include/*" 被按字面名匹配而落空
$tarWildArgs = $IsLinux ? @("--wildcards") : @()
# 可选条目（符号链接目标等）缺失时 tar 告警；成败统一以提取后 clang 存在性判定
& tar -xf $archive -C $stage @tarWildArgs @patterns 2>$null | Out-Null

$srcRoot = Join-Path $stage $rootDir
foreach ($sub in @("bin", "lib")) {
    $src = Join-Path $srcRoot $sub
    if (Test-Path $src) {
        # 目标已存在（-Force 重建等）时先清除，否则 Move-Item 会把源嵌套成 bin/bin
        Remove-Item (Join-Path $dest $sub) -Recurse -Force -ErrorAction SilentlyContinue
        Move-Item $src (Join-Path $dest $sub) -Force
    }
}
Remove-Item -Recurse -Force $stage
if ($downloadedHere) { Remove-Item $archive -ErrorAction SilentlyContinue }

if (-not (Test-Path $clangPath)) { throw "提取失败：$clangPath 不存在" }

# ===== 版本标记与冒烟 =====
Set-Content -Path $marker -NoNewline -Value "LLVM $LlvmVersion $Rid"
Add-Content -Path $marker -Value "`n$($spec.Url)`nsha256:$($spec.Sha256)"
Write-Host "完成：$dest"
& $clangPath --version | Select-Object -First 1
