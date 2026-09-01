#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    libuv 静态库获取脚本（Middleware 协程 Alarm 族事件底座，MW11b 起）。

.DESCRIPTION
    策略（MIDDLEWARE_ARCHITECTURE §2 定稿，渠道②：钉版源码 + 本地构建静态库 + 缓存）：
    - libuv 官方无预编译二进制（dist.libuv.org 的 dist tarball 是 autotools
      形态、无顶层 CMakeLists.txt，弃用；改用 GitHub tag 归档源码）。源码构建
      零外部依赖，仅需 cmake ≥3.9 + C 编译器。
    - CI（GitHub Actions）每 job 跑本脚本现场构建（runner 预装 cmake + clang）；
      开发机首次构建后缓存到 tools/.libuv/<rid>/（gitignored），缓存命中即跳过，
      重复跑幂等；-Force 强制重建。

    缓存布局（LibuvResolver 的解析契约）：
      tools/.libuv/<rid>/lib/uv_a.lib      （win-x64 静态库）
      tools/.libuv/<rid>/lib/libuv_a.a     （linux-x64 静态库）
      tools/.libuv/<rid>/include/          （libuv 公开头文件全集：uv.h 及子头）
      tools/.libuv/<rid>/VERSION.txt       （版本/RID 标记，缓存命中判定用）

    native --out 链接时由 LibuvResolver 解析顺序：--libuv-dir <目录> → 环境变量
    RIGI_LIBUV → 本脚本缓存 tools/.libuv/<rid>/ → 编译器 exe 旁 .libuv/<rid>/。

.PARAMETER Rid
    目标 RID（win-x64 / linux-x64）；缺省按当前平台自动判定。

.PARAMETER ArchivePath
    复用本地已下载的 tarball（离线/测试）；缺省在线下载。

.PARAMETER Force
    忽略缓存强制重建。

.EXAMPLE
    pwsh tools/Fetch-Libuv.ps1
#>
[CmdletBinding()]
param(
    [string]$Rid = "",
    [string]$ArchivePath = "",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

$LibuvVersion = "1.52.1"
# 钉版 GitHub 源码 tarball（https://github.com/libuv/libuv/releases/tag/v1.52.1）：
#   v1.52.1.tar.gz（约 1.5MB；双平台同一份源码）。注意不用 dist.libuv.org 的
#   *-dist.tar.gz——那是 autotools dist 形态，不含顶层 CMakeLists.txt。
#   sha256 为首次获取后钉入的自校验值（2026-08 钉取；GitHub tag 归档无官方
#   校验值发布渠道，dist.libuv.org 的 GPG .sign 只覆盖 dist tarball 且
#   Windows 侧不易验签，按 Fetch-LlvmToolchain.ps1 win 先例以 SHA256 为准）
$LibuvUrl = "https://github.com/libuv/libuv/archive/refs/tags/v$LibuvVersion.tar.gz"
$LibuvSha256 = "478baf2599bfbc882c355288c9cb6f92e0e7dda435fa04031fa5b607cf3f414c"
# 静态库产物：cmake 目标 uv_a，OUTPUT_NAME 为 "uv"，win 侧再加 PREFIX "lib"——
# 即 cmake 实际产出 win = libuv.lib、linux = libuv.a（与 autotools 形态的
# uv_a.lib/libuv_a.a 命名不同）。缓存布局统一沿用设计契约名 uv_a.lib/libuv_a.a，
# 拷贝时改名；BuiltNames 为构建目录内的候选文件名
$StaticLibNames = @{
    "win-x64" = "uv_a.lib"
    "linux-x64" = "libuv_a.a"
}
$BuiltLibNames = @{
    "win-x64" = @("libuv.lib", "uv_a.lib")
    "linux-x64" = @("libuv.a", "libuv_a.a")
}

# ===== 平台判定 =====
if ($Rid -eq "") {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) { $Rid = "win-x64" }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)) { $Rid = "linux-x64" }
    else { throw "不支持的平台（仅 win-x64/linux-x64），请用 -Rid 显式指定" }
}
if (-not $StaticLibNames.ContainsKey($Rid)) { throw "未知 RID: $Rid（支持 win-x64/linux-x64）" }
$libName = $StaticLibNames[$Rid]

$repoRoot = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repoRoot "tools/.libuv/$Rid"
$marker = Join-Path $dest "VERSION.txt"
$destLib = Join-Path $dest "lib/$libName"
$destInclude = Join-Path $dest "include"

# 缓存命中：静态库 + include/uv.h + 版本标记一致 → 跳过
if (-not $Force -and (Test-Path $destLib) -and (Test-Path (Join-Path $destInclude "uv.h")) -and
    (Test-Path $marker) -and
    ((Get-Content $marker -TotalCount 1) -eq "libuv $LibuvVersion $Rid")) {
    Write-Host "已缓存 libuv $LibuvVersion ($Rid)：$dest（-Force 可重建）"
    exit 0
}

New-Item -ItemType Directory -Force $dest | Out-Null

# ===== 获取压缩包 =====
$archive = $ArchivePath
$downloadedHere = $false
if ($archive -eq "") {
        $archive = Join-Path $dest "libuv-v$LibuvVersion.tar.gz"
    if (-not (Test-Path $archive)) {
        Write-Host "下载 $LibuvUrl ..."
        & curl -fSL --retry 3 -C - -o $archive $LibuvUrl
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
if ($actual -ne $LibuvSha256) {
    if ($downloadedHere) { Remove-Item $archive -ErrorAction SilentlyContinue }
    throw "SHA256 不匹配：期望 $LibuvSha256，实际 $actual"
}

# ===== 提取源码 =====
# tarball 根目录名固定为 libuv-v<版本>；读首条目自适应以防打包形态变化
$firstEntry = (& tar -tf $archive | Select-Object -First 1).Trim()
$rootDir = ($firstEntry -split '/')[0]
if ([string]::IsNullOrWhiteSpace($rootDir)) { throw "无法读取压缩包根目录" }
$stage = Join-Path $dest "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host "提取源码（根 $rootDir）..."
& tar -xf $archive -C $stage
$srcDir = Join-Path $stage $rootDir
if (-not (Test-Path (Join-Path $srcDir "CMakeLists.txt"))) { throw "提取失败：$srcDir 无 CMakeLists.txt" }

# ===== cmake 构建静态库 =====
# cmake 最小配置：-DBUILD_TESTING=OFF + Release；产物只取静态库目标 uv_a
# （win = uv_a.lib；linux = libuv_a.a）。生成器选择：
#   win   优先 tools/.llvm 的 clang-cl + Ninja（与项目工具链一致），缺则退 VS 生成器
#   linux 用 clang（缺则 cc），Ninja 缺省（cmake 自带 Unix Makefiles 兜底）
$buildDir = Join-Path $stage "build"
New-Item -ItemType Directory -Force $buildDir | Out-Null
$cmakeArgs = @("-S", $srcDir, "-B", $buildDir,
    "-DBUILD_TESTING=OFF", "-DCMAKE_BUILD_TYPE=Release")
if ($Rid -eq "win-x64") {
    $llvmClangCl = Join-Path $repoRoot "tools/.llvm/$Rid/bin/clang-cl.exe"
    $ninjaExe = Get-Command ninja -ErrorAction SilentlyContinue
    if ((Test-Path $llvmClangCl) -and $ninjaExe) {
        Write-Host "生成器：Ninja + tools/.llvm clang-cl"
        $cmakeArgs += @("-G", "Ninja", "-DCMAKE_C_COMPILER=$llvmClangCl")
    }
    else {
        Write-Host "生成器：Visual Studio（tools/.llvm 无 clang-cl 或 PATH 无 ninja，回退）"
        $cmakeArgs += @("-A", "x64")
    }
}
else {
    $cc = (Get-Command clang -ErrorAction SilentlyContinue) ? "clang" : "cc"
    Write-Host "生成器：cmake 缺省 + $cc"
    $cmakeArgs += @("-DCMAKE_C_COMPILER=$cc")
}
Write-Host "cmake 配置 ..."
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw "cmake 配置失败（退出码 $LASTEXITCODE）" }
Write-Host "cmake 构建静态库目标 uv_a（约一两分钟）..."
& cmake --build $buildDir --config Release --target uv_a
if ($LASTEXITCODE -ne 0) { throw "cmake 构建失败（退出码 $LASTEXITCODE）" }

# 产物定位：VS 多配置生成器落在 build/Release/，单配置落在 build/；
# cmake 实际产出名（libuv.lib/libuv.a）与缓存契约名（uv_a.lib/libuv_a.a）不同
$builtLib = $null
foreach ($name in $BuiltLibNames[$Rid]) {
    $builtLib = Get-ChildItem -Recurse -Filter $name $buildDir | Select-Object -First 1
    if ($builtLib) { break }
}
if (-not $builtLib) { throw "构建产物 $($BuiltLibNames[$Rid] -join '/') 未在 $buildDir 下找到" }

# ===== 组装缓存布局 =====
Remove-Item (Join-Path $dest "lib") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $dest "lib") | Out-Null
Copy-Item $builtLib.FullName $destLib
Remove-Item $destInclude -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $destInclude | Out-Null
Copy-Item -Recurse (Join-Path $srcDir "include/*") $destInclude
if (-not (Test-Path (Join-Path $destInclude "uv.h"))) { throw "include 组装失败：uv.h 缺失" }

Remove-Item -Recurse -Force $stage
if ($downloadedHere) { Remove-Item $archive -ErrorAction SilentlyContinue }

# ===== 版本标记与冒烟 =====
Set-Content -Path $marker -NoNewline -Value "libuv $LibuvVersion $Rid"
Add-Content -Path $marker -Value "`n$LibuvUrl`nsha256:$LibuvSha256"
Write-Host "完成：$dest"
Write-Host "静态库：$destLib（$([math]::Round((Get-Item $destLib).Length / 1KB)) KB）"
