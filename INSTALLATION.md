# NativeAOT Release 打包、安装与使用

本文面向安装编译器的用户，也说明维护者如何从源码生成安装包。当前项目配置与 CI 验证的目标是 **Windows x64（`win-x64`）**和 **Linux x64（`linux-x64`，glibc 环境）**；不要把这两个包用于 macOS、ARM64 或 musl/Alpine。

## 1. 命令名称与参数分隔符

安装后运行 `rigic help`、`rigic help module`；PowerShell 在尚未加入 PATH 时使用 `.\rigic.exe help`。发布版是原生程序，不需要 `dotnet run`，也不要求安装 .NET SDK 或 .NET Runtime。

源码开发时，`dotnet run help` 与 `dotnet run -- help` 都有效。独立的 `--` 是 **.NET CLI 参数分隔符**，在启动程序前被消耗；后面的参数传给 RigiCompiler。为避免 `--file` 等参数与 .NET CLI 自身选项冲突，带编译器选项的开发示例保留这个分隔符。`dotnet run --help` 显示的是 .NET 的帮助。

RigiCompiler 的模块运行还支持自己的程序参数分隔符：

```powershell
rigic module --run --root app -- first "two words"
# 源码开发：第一个 -- 属于 dotnet，第二个属于 RigiCompiler。
dotnet run -- module --run --root app -- first "two words"
```

参数转交规则见 [Microsoft 的 dotnet run 文档](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-run)。

## 2. 安装包包含什么

以完整发布目录为交付单位，保留其相对目录结构：

- `rigic.exe`（Windows）或 `rigic`（Linux）：NativeAOT 编译器。
- 与该发布产物配套的 `libLLVM.dll` / `libLLVM.so*` 等原生资产：原样保留 `dotnet publish` 输出，不要从其他版本拼接，也不要只复制可执行文件。
- `Tests/`、`tools/stress/` 和 `rigi_rt/` 等发布内容：测试语料、压力源、C fixture 头文件和 Native 库产品的公开头契约。标准库及运行时 C 源本身已内嵌；保留完整目录还能在安装后运行发布验收。
- `LICENSE`、`NOTICE`、`README.md`、本指南、`DEVELOPMENT.md` 与 `docs/`：由下面的打包步骤补入，包内文档导航可离线阅读（外部网站链接仍需联网）。
- 为 Native 编译准备的 `.libuv/<rid>/include、lib` 和 `.mimalloc/<rid>/include、lib`：下面推荐的完整包会补入；单独 `dotnet publish` 不会自动复制 `tools/` 下的这些缓存。

执行帮助、语法/语义编译、VM 模块运行不需要 clang、libuv 或 mimalloc。**把 Rigi 程序编译为原生产物**还需要 clang/lld、目标系统的 C 开发环境，以及 libuv/mimalloc 静态库。Native 模块工作流还需要静态库归档器，因为 executable 模块也可能构建标准库等静态依赖。NativeAOT 描述的是编译器自己的发布方式；它不会把这些外部构建工具自动打进安装包。

Windows 的 libLLVM 还依赖 x64 Visual C++ v14 运行库（VCRUNTIME140、VCRUNTIME140_1、MSVCP140 和 UCRT）；缺失时按 [Microsoft VC++ Redistributable 指南](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170) 安装匹配的 x64 运行库。它和 .NET Runtime 是不同依赖。

Linux 包还依赖构建时要求的系统原生库；在较新发行版发布的二进制不能据此保证可在旧发行版运行。发布者应选择支持范围内最旧的构建环境，记录构建发行版与架构，并在目标系统验证。`ldd ./rigic` 和 `ldd ./libLLVM.so`（按实际文件名）可检查缺失依赖。NativeAOT 的运行与构建前提见 [Microsoft 的部署文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。

现有 CI 执行发布与测试，没有上传安装包或创建 GitHub Release 的步骤。以下是从源码制作包的操作，不假设已有可下载的官方发行资产。

## 3. 从源码制作 Windows x64 包

在包含 `RigiCompiler.csproj` 的仓库根操作。准备 .NET 10 SDK（版本规则见 `global.json`）、Visual Studio 2022 或更新版本的“使用 C++ 的桌面开发”工作负载及默认组件；依赖获取脚本使用 PowerShell 7，libuv 构建还需 CMake，脚本根据环境使用 Ninja/clang-cl 或 VS 生成器。

以下示例生成新的发布目录；`local-<时间>` 是本地包标识，不是项目的语义版本号。每条命令失败后先处理错误，不继续打包。生成的 `dist/` 安装包是分发产物，不提交到源码仓库；也可将归档目标改到仓库外的目录。

```powershell
$ErrorActionPreference = 'Stop'
$buildId = "local-$(Get-Date -Format yyyyMMdd-HHmmss)"
$packageName = "rigic-win-x64-$buildId"
$packageDir = Join-Path (Join-Path (Get-Location) 'publish') $packageName
if (Test-Path -LiteralPath $packageDir) { throw '发布目录必须是新的目录' }

pwsh -File tools/Fetch-LlvmToolchain.ps1 -Rid win-x64
if ($LASTEXITCODE -ne 0) { throw 'LLVM 工具链获取失败' }
pwsh -File tools/Fetch-Libuv.ps1 -Rid win-x64
if ($LASTEXITCODE -ne 0) { throw 'libuv 获取失败' }
pwsh -File tools/Fetch-Mimalloc.ps1 -Rid win-x64
if ($LASTEXITCODE -ne 0) { throw 'mimalloc 获取失败' }
dotnet publish RigiCompiler.csproj -c Release -r win-x64 -o $packageDir
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT 发布失败' }

Copy-Item -LiteralPath 'LICENSE','NOTICE','README.md','INSTALLATION.md','DEVELOPMENT.md' -Destination $packageDir
Copy-Item -LiteralPath 'docs' -Destination $packageDir -Recurse
New-Item -ItemType Directory -Path (Join-Path $packageDir '.libuv'),(Join-Path $packageDir '.mimalloc') | Out-Null
Copy-Item -LiteralPath 'tools/.libuv/win-x64' -Destination (Join-Path $packageDir '.libuv') -Recurse
Copy-Item -LiteralPath 'tools/.mimalloc/win-x64' -Destination (Join-Path $packageDir '.mimalloc') -Recurse

& (Join-Path $packageDir 'rigic.exe') help
if ($LASTEXITCODE -ne 0) { throw '发布产物帮助检查失败' }
New-Item -ItemType Directory -Path 'dist' -Force | Out-Null
$archive = Join-Path (Join-Path (Get-Location) 'dist') "$packageName.zip"
# .NET ZIP API 包含 .libuv/.mimalloc；避免 Compress-Archive 忽略隐藏内容。
[System.IO.Compression.ZipFile]::CreateFromDirectory($packageDir, $archive)
Get-FileHash -LiteralPath $archive -Algorithm SHA256
```

现成 clang/lld 满足要求时可以跳过 LLVM 获取脚本。已有同 RID 的有效 libuv/mimalloc 缓存时可复用，并省略相应获取脚本；仍按下面的布局完整复制到安装包。外部 clang 与进程内 libLLVM 是不同依赖：后者由 NuGet 发布资产提供，不能用 PATH 上的 clang 替代。

此包包含 libuv/mimalloc，仍要求安装端提供 clang/lld 与 Windows C SDK/CRT。若还要附带已获取的工具链，在压缩前把 `tools/.llvm/win-x64` 整目录复制到包内 `tools/.llvm/win-x64`；保持 `bin/` 和 clang 内建头文件等内容，且核对第三方许可证。这不会替代 Windows C SDK/CRT。

## 4. 从源码制作 Linux x64 包

在 Linux 本机或 WSL Linux 环境发布；不能在 Windows 上交叉生成 Linux NativeAOT 编译器。准备 .NET 10 SDK、PowerShell 7、clang、lld、C/C++ 开发环境、zlib 开发包及 CMake。Ubuntu/Debian 的构建依赖可用：

```bash
sudo apt-get update
sudo apt-get install -y build-essential clang lld zlib1g-dev cmake
```

.NET SDK 与 PowerShell 7 按目标发行版各自的官方安装方式准备。已有同 RID 的有效 libuv/mimalloc 缓存时可省略获取脚本；PowerShell 7 仅在需要运行这些脚本时使用。然后从仓库根执行：

```bash
# 在子 shell 中遇错退出，避免失败后继续生成不完整包。
(
set -eu
build_id="local-$(date +%Y%m%d-%H%M%S)"
package_name="rigic-linux-x64-$build_id"
package_dir="publish/$package_name"
[ ! -e "$package_dir" ]

pwsh -File tools/Fetch-Libuv.ps1 -Rid linux-x64
pwsh -File tools/Fetch-Mimalloc.ps1 -Rid linux-x64
dotnet publish RigiCompiler.csproj -c Release -r linux-x64 -o "$package_dir"
cp LICENSE NOTICE README.md INSTALLATION.md DEVELOPMENT.md "$package_dir/"
cp -a docs "$package_dir/"
mkdir -p "$package_dir/.libuv" "$package_dir/.mimalloc"
cp -a tools/.libuv/linux-x64 "$package_dir/.libuv/"
cp -a tools/.mimalloc/linux-x64 "$package_dir/.mimalloc/"
"$package_dir/rigic" help

mkdir -p dist
tar -czf "dist/$package_name.tar.gz" -C publish "$package_name"
sha256sum "dist/$package_name.tar.gz"
)
```

系统工具链不可用时，可先执行 `pwsh -File tools/Fetch-LlvmToolchain.ps1 -Rid linux-x64`。如果一起分发，压缩前复制到包内 `tools/.llvm/linux-x64`，保留完整工具链布局及许可证。tar 会保留可执行权限与点目录。

发布者应在仓库外解压包做验收，避免解析器向上找到源码缓存而掩盖漏打包。`help` 只检查启动，不会验证 Native 链接能力：还应分别运行下一节的 VM 和 Native 示例。全量发布验收使用 `RIGI_TEST_CORPUS_ONLY_OUTPUT=1`、`RIGI_TEST_RIGIC=<包内编译器绝对路径>` 与 `rigic test --all`；完整双平台门禁见 [DEVELOPMENT.md](DEVELOPMENT.md#23-发布release--nativeaot)。WSL 全量测试的工作目录应位于 Linux 原生文件系统。

分发时记录源码提交/是否有未提交修改、SDK、RID、构建系统和工具链版本，附上压缩包 SHA-256；本地时间包名不能替代这些来源信息。附带第三方二进制与工具链时保留相应许可/归属文件，参见项目 `NOTICE`。

## 5. 安装到自己的电脑

解压的是上面制作的完整包；已有其他来源的包时，以其真实文件名与顶层目录为准，先核对发行者提供的 SHA-256。选择与系统/CPU 相符的 RID。下列命令只改用户目录和用户 PATH，无需管理员权限。

### Windows / PowerShell

```powershell
$archive = Join-Path $HOME 'Downloads/rigic-win-x64-local-20261004-120000.zip'
Get-FileHash -LiteralPath $archive -Algorithm SHA256
$installDir = Join-Path $env:LOCALAPPDATA 'Programs/RigiCompiler/local-20261004-120000'
if (Test-Path -LiteralPath $installDir) { throw '请选择新的安装目录' }
# Windows ZIP 示例不含顶层包名目录，直接解压即得到 rigic.exe。
Expand-Archive -LiteralPath $archive -DestinationPath $installDir
& (Join-Path $installDir 'rigic.exe') help
if ($LASTEXITCODE -ne 0) { throw '安装检查失败' }

# 写入用户 PATH，保留已有条目；当前会话也立即可用。
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $installDir) {
    $updatedPath = if ([string]::IsNullOrEmpty($userPath)) { $installDir } else { "$installDir;$userPath" }
    [Environment]::SetEnvironmentVariable('Path', $updatedPath, 'User')
}
$env:Path = "$installDir;$env:Path"
Get-Command rigic
rigic help
rigic help module
```

文件名中的时间是示例，替换成自己的包标识。也可在 Windows“环境变量”界面把实际安装目录加入用户 PATH，并重新打开终端。不使用 `setx PATH` 拼接整条进程 PATH。

### Linux / Bash

```bash
# 替换为自己的压缩包名称。
sha256sum "$HOME/Downloads/rigic-linux-x64-local-20261004-120000.tar.gz"
mkdir -p "$HOME/.local/opt/rigic" "$HOME/.local/bin"
# tar 示例含顶层包名目录；确认同名目录尚不存在后解压。
tar --keep-old-files -xzf "$HOME/Downloads/rigic-linux-x64-local-20261004-120000.tar.gz" -C "$HOME/.local/opt/rigic"
install_dir="$HOME/.local/opt/rigic/rigic-linux-x64-local-20261004-120000"
chmod +x "$install_dir/rigic"
"$install_dir/rigic" help
# 初次安装；已有 rigic 链接时，升级流程见下文。
ln -s "$install_dir/rigic" "$HOME/.local/bin/rigic"
export PATH="$HOME/.local/bin:$PATH"
command -v rigic
rigic help
```

若 shell 没有自动加入 `~/.local/bin`，把 `export PATH="$HOME/.local/bin:$PATH"` 添加一次到实际使用的 shell 配置文件（Bash 登录 shell 通常为 `~/.profile`，交互非登录 shell 通常为 `~/.bashrc`），重新打开终端。保留安装目录整体，链接指向其中的可执行文件。

## 6. 第一次使用与 Native 工具链

在自己的工作目录创建模块：

```powershell
rigic help
rigic help module
rigic module --init --root hello-rigi
rigic module --run --root hello-rigi
```

模板生成 `module.yaml` 与 `source/main.rg`，默认 debug profile 使用 VM，输出 `hello module`。编辑源码后再次运行即可。已存在的配置/入口源码不会被初始化操作覆盖。

生成并运行 Native 程序：

```powershell
rigic module --run --root hello-rigi --profile release
```

这里的 `release` 是模板中的 profile 名；其 `target: native` 决定 Native 编译，并非切换已安装编译器的 .NET 构建配置。Native 编译环境需要：

- clang 与 lld；Windows 还需要 C SDK/CRT（可安装 VS C++ Build Tools 的相应组件），Linux 需要系统 C 开发包。clang/lld 可在 PATH，或用 `RIGI_LLVM` 指向含 `bin/clang(.exe)` 的工具链根。包内 `tools/.llvm/<rid>` 也是解析器支持的位置，单独 `<安装目录>/.llvm` 不会被自动识别。
- libuv 与 mimalloc 的完整头文件/静态库布局。上面的完整包已放在可执行文件旁的 `.libuv/<rid>` 与 `.mimalloc/<rid>`，通常无需环境变量；外置依赖可设置 `RIGI_LIBUV` / `RIGI_MIMALLOC` 指向各自的 `<rid>` 目录。

显式位置示例（使用自己已安装的真实路径，目录根不是 `bin`/`lib`）：

```powershell
# Windows，当前 PowerShell 会话；需要持久化时配置用户环境变量。
$env:RIGI_LLVM = 'C:\Tools\LLVM'
# 包外依赖示例；包内依赖自动发现时不必设置。
$env:RIGI_LIBUV = 'C:\Tools\rigi-deps\.libuv\win-x64'
$env:RIGI_MIMALLOC = 'C:\Tools\rigi-deps\.mimalloc\win-x64'
```

```bash
# Linux，当前会话；按真实安装位置修改。
export RIGI_LLVM="$HOME/.local/opt/llvm"
export RIGI_LIBUV="$HOME/.local/opt/rigi-deps/.libuv/linux-x64"
export RIGI_MIMALLOC="$HOME/.local/opt/rigi-deps/.mimalloc/linux-x64"
```

`module --publish` 和 `module --bundle` 即使选择 VM profile 也会发布真实 Native 产品，因此同样需要以上依赖。归档器也用于 executable 模块的标准库/静态依赖构建，不仅用于直接发布静态库；用 `RIGI_AR` 指定其可执行文件，或使系统的 `llvm-lib.exe`/`lib.exe`（Windows）或 `llvm-ar`/`ar`（Linux）可被解析。LLVM 获取脚本当前不获取归档器。

Windows 若不在 VS 开发者终端中运行，可用下面的方式定位已有 C++ 工具链的 `lib.exe`；也可以直接将 `RIGI_AR` 设置为已知归档器的绝对路径：

```powershell
$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsInstallPath = & $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsInstallPath) { throw '未找到已安装的 VS C++ 工具链' }
$archivers = Get-ChildItem -LiteralPath (Join-Path $vsInstallPath 'VC/Tools/MSVC') -Directory | ForEach-Object {
    Get-Item -LiteralPath (Join-Path $_.FullName 'bin/Hostx64/x64/lib.exe') -ErrorAction SilentlyContinue
}
$archiver = $archivers | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $archiver) { throw '未找到 x64 lib.exe' }
$env:RIGI_AR = $archiver.FullName
rigic module --run --root hello-rigi --profile release
```

低级 `native` 命令另有 `--toolchain`、`--libuv-dir`、`--mimalloc-dir` 显式选项；模块命令没有这些选项，使用环境变量或自动发现布局。缺少必需 libuv/mimalloc 时 Native 编译明确拒绝。完整搜索优先序与链接要求见 [工具链专题](docs/compiler/middleware/TOOLCHAIN_AND_CACHE.md)。

`rigic module --publish/--bundle/--install` 用于 **Rigi 模块产品与依赖包**，不用于安装编译器本身。模块 ZIP 的契约、缓存、信任及 hook 见 [开发指南](DEVELOPMENT.md)。

## 7. 升级、卸载与常见问题

升级时解压到新的版本目录，先用绝对路径检查 `help` 和自己的模块，再把 PATH 或用户 bin 链接指向新目录；保留旧目录便于切回。不混合不同发布版本的 rigic、libLLVM 与静态依赖。当前没有独立 version 子命令或 `rigic --version` 选项，用安装目录/包标识及发布来源记录识别版本。

卸载时从用户 PATH 移除该版本目录，或删除自己创建的 `~/.local/bin/rigic` 链接，然后删除对应安装目录；自己的模块、源码与依赖目录不属于编译器安装目录。

- 找不到 `rigic`：检查 `Get-Command rigic` / `command -v rigic`，确认 PATH 指向含可执行文件的目录；PowerShell 当前目录执行要写 `.\rigic.exe`。
- libLLVM 加载失败：恢复完整、同 RID 的发布目录及系统原生依赖，Windows 核对 VC++ x64 运行库；PATH 中的 clang 不能补齐丢失的 libLLVM。
- 缺少 clang/lld、libuv 或 mimalloc：配置本节的工具链和依赖布局，确认静态库与目标 RID 匹配；帮助/VM 成功并不证明 Native 链接环境完整。
- Linux 提示 `GLIBC_*`、共享库缺失或格式错误：核对包的构建发行版、运行系统和架构，不把不同 RID 的包混用。
- 修改 PATH 后仍运行旧版：重新打开终端，检查真实解析路径；Bash 可执行 `hash -r` 清除命令缓存。
