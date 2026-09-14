#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    mimalloc 静态库获取脚本（rigi_rt track 台账的底层分配面，GC Phase 2 定案）。

.DESCRIPTION
    策略（GC_OPTIMIZATION_PLAN §2.4 定稿，钉版预取官方预编译包 + 提取 + 缓存）：
    - mimalloc 官方 release assets 自 v3.x 起附带预编译产物（v1/v2/v3 三代 ABI
      同发，各含 windows/linux/macos 的 x64/arm64 包，包内含静态库）。本脚本
      取 **v2 ABI 代**（rigi_rt 只用 mi_malloc_aligned/mi_free 等稳定 v2 面），
      下载官方预编译包后提取静态库与头文件——与 libuv 先例（无官方预编译、
      现场 cmake 构建）不同，无需构建工具链，CI/开发机秒级完成。
    - CI（GitHub Actions）每 job 跑本脚本预取（须在 publish 之前：NativeE2E
      的 --out 用例链接时经 MimallocResolver 命中缓存）；开发机首次预取后
      缓存到 tools/.mimalloc/<rid>/（gitignored），缓存命中即跳过，重复跑
      幂等；-Force 强制重建。

    缓存布局（MimallocResolver 的解析契约）：
      tools/.mimalloc/<rid>/lib/mimalloc.lib    （win-x64 静态库，release 变体）
      tools/.mimalloc/<rid>/lib/libmimalloc.a   （linux-x64 静态库，release 变体）
      tools/.mimalloc/<rid>/include/            （mimalloc.h 及附属头，扁平落盘）
      tools/.mimalloc/<rid>/VERSION.txt         （版本/RID 标记，缓存命中判定用）

    native --out 链接时由 MimallocResolver 解析顺序：--mimalloc-dir <目录> →
    环境变量 RIGI_MIMALLOC → 本脚本缓存 tools/.mimalloc/<rid>/ → 编译器 exe
    旁 .mimalloc/<rid>/。缺失时编译期明确拒绝（track 台账是 rigi_rt 对象分配
    唯一收口，无 mimalloc 符号必然链接失败）。

.PARAMETER Rid
    目标 RID（win-x64 / linux-x64）；缺省按当前平台自动判定。

.PARAMETER ArchivePath
    复用本地已下载的 tarball（离线/测试）；缺省在线下载。

.PARAMETER Force
    忽略缓存强制重建。

.EXAMPLE
    pwsh tools/Fetch-Mimalloc.ps1
#>
[CmdletBinding()]
param(
    [string]$Rid = "",
    [string]$ArchivePath = "",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

# 钉版（GC Phase 2，2026-09 钉取）：载体 release v3.5.1（2026-09-01 发布，
# https://github.com/microsoft/mimalloc/releases/tag/v3.5.1，非 pre-release，
# 该 release 同发 v1.15.1/v2.5.1/v3.5.1 三代 ABI 的预编译包），取其 **v2 ABI
# 代 mimalloc-v2.5.1**——rigi_rt 的分配面是 mi_malloc_aligned/mi_free 等稳定
# v2 API，且 v2 线与 GC_OPTIMIZATION_PLAN §2.4 定稿时的 v2.x 行为一致。
#   win-x64 包内静态库：lib/mimalloc-2.5/mimalloc.lib（release，非 debug/secure）
#   linux-x64 包内静态库：lib/mimalloc-2.5/libmimalloc.a（release，非 debug/secure）
# SHA256 为首次获取后钉入的自校验值（GitHub release assets 无官方校验值发布
# 渠道，按 Fetch-Libuv.ps1 先例以 SHA256 为准）。
$ReleaseTag = "v3.5.1"
$LibVersion = "2.5.1"
$Sha256ByRid = @{
    "win-x64" = "9e04ed1c78a53576d669a29cbc348880f4aac06c3ef4ca15cf05f315a953aaa9"
    "linux-x64" = "1837dffe754f2f8080ee34d8b8f75adb3270a7bf627036e4e5c93caca4e0a600"
}
# 包内静态库文件名（basename 精确匹配，天然排除 -debug/-secure/-dll 变体）
$StaticLibName = @{
    "win-x64" = "mimalloc.lib"
    "linux-x64" = "libmimalloc.a"
}

# ===== 平台判定 =====
if ($Rid -eq "") {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) { $Rid = "win-x64" }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)) { $Rid = "linux-x64" }
    else { throw "不支持的平台（仅 win-x64/linux-x64），请用 -Rid 显式指定" }
}
if (-not $Sha256ByRid.ContainsKey($Rid)) { throw "未知 RID: $Rid（支持 win-x64/linux-x64）" }
$libName = $StaticLibName[$Rid]

$repoRoot = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repoRoot "tools/.mimalloc/$Rid"
$marker = Join-Path $dest "VERSION.txt"
$destLib = Join-Path $dest "lib/$libName"
$destInclude = Join-Path $dest "include"

# 缓存命中：静态库 + include/mimalloc.h + 版本标记一致 → 跳过
if (-not $Force -and (Test-Path $destLib) -and (Test-Path (Join-Path $destInclude "mimalloc.h")) -and
    (Test-Path $marker) -and
    ((Get-Content $marker -TotalCount 1) -eq "mimalloc v$LibVersion $Rid")) {
    Write-Host "已缓存 mimalloc v$LibVersion ($Rid)：$dest（-Force 可重建）"
    exit 0
}

New-Item -ItemType Directory -Force $dest | Out-Null

# ===== 获取压缩包 =====
$url = "https://github.com/microsoft/mimalloc/releases/download/$ReleaseTag/" +
    "mimalloc-v$LibVersion-$Rid.tar.gz"
$archive = $ArchivePath
$downloadedHere = $false
if ($archive -eq "") {
    $archive = Join-Path $dest "mimalloc-v$LibVersion-$Rid.tar.gz"
    if (-not (Test-Path $archive)) {
        Write-Host "下载 $url ..."
        & curl -fSL --retry 3 -C - -o $archive $url
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
if ($actual -ne $Sha256ByRid[$Rid]) {
    if ($downloadedHere) { Remove-Item $archive -ErrorAction SilentlyContinue }
    throw "SHA256 不匹配：期望 $($Sha256ByRid[$Rid])，实际 $actual"
}

# ===== 逐成员提取（不整包解压：linux 包含符号链接，Windows bsdtar 解到 NTFS
# 会报 symlink 错误退出码 2；本脚本只需要静态库 + 头文件两个成员族） =====
# tar 解析：优先 Windows 自带 bsdtar（System32，接受 Windows 盘符路径）；从
# Git Bash 会话调本脚本时 PATH 上的 /usr/bin/tar 是 MSYS GNU tar，会把
# `-C C:\...` 当远程主机（`C:` 触发 remote 语义）而失败，故不能裸用 PATH tar
$tarExe = Join-Path $env:SystemRoot "System32/tar.exe"
if (-not (Test-Path $tarExe)) { $tarExe = "tar" }
Write-Host "提取静态库与头文件（逐成员，tar = $tarExe）..."
$entries = & $tarExe -tzf $archive
if ($LASTEXITCODE -ne 0) { throw "无法读取压缩包成员清单（tar 退出码 $LASTEXITCODE）" }

# 静态库成员：lib/ 子目录下 basename 精确等于契约名（排除 debug/secure/dll 变体）
$libEntry = $entries | Where-Object {
    (Split-Path ($_ -replace '\\', '/') -Leaf) -eq $libName -and
    ($_ -replace '\\', '/') -match '(^|/)lib/' -and
    ($_ -replace '\\', '/') -notmatch 'debug|secure|dll'
} | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($libEntry)) {
    throw "压缩包内未找到静态库成员 $libName"
}

# 头文件成员：include/ 子目录下全部 mimalloc*.h（mimalloc.h + 附属头）
$hdrEntries = $entries | Where-Object {
    ($_ -replace '\\', '/') -match '(^|/)include/' -and
    (Split-Path ($_ -replace '\\', '/') -Leaf) -like 'mimalloc*.h'
}

# 提取到临时 stage 目录（tar 保留包内路径），随后按缓存契约布局拷贝
$stage = Join-Path $dest "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
& $tarExe -xzf $archive -C $stage @($libEntry) @($hdrEntries)
if ($LASTEXITCODE -ne 0) { throw "成员提取失败（tar 退出码 $LASTEXITCODE）" }

# ===== 组装缓存布局 =====
Remove-Item (Join-Path $dest "lib") -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $dest "lib") | Out-Null
$stagedLib = Get-ChildItem -Recurse -Filter $libName $stage | Select-Object -First 1
if (-not $stagedLib) { throw "stage 内未找到 $libName" }
Copy-Item $stagedLib.FullName $destLib

Remove-Item $destInclude -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $destInclude | Out-Null
$stagedHdrs = Get-ChildItem -Recurse -Filter "mimalloc*.h" $stage
if (-not ($stagedHdrs | Where-Object Name -eq "mimalloc.h")) {
    throw "include 组装失败：mimalloc.h 缺失"
}
foreach ($hdr in $stagedHdrs) { Copy-Item $hdr.FullName $destInclude }
Remove-Item -Recurse -Force $stage
if ($downloadedHere) { Remove-Item $archive -ErrorAction SilentlyContinue }

# ===== 版本标记 =====
Set-Content -Path $marker -NoNewline -Value "mimalloc v$LibVersion $Rid"
Add-Content -Path $marker -Value "`n$url`nsha256:$($Sha256ByRid[$Rid])"
Write-Host "完成：$dest"
Write-Host "静态库：$destLib（$([math]::Round((Get-Item $destLib).Length / 1KB)) KB）"
