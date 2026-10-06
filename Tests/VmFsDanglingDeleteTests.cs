using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    /// <summary>断链文件符号链接删除：宿主只创建 fixture，删除必须经 VM/Rigi 原语。</summary>
    public static class VmFsDanglingDeleteTests
    {



        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static TestSuiteData Spec => new(
            "VmFsDanglingDelete", Cases, sectionTitle: "VmFsDanglingDelete");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestPrimitiveUnlinkDanglingFileLink", TestPrimitiveUnlinkDanglingFileLink),
            ("TestPrimitiveCreateNewDanglingFileLink", TestPrimitiveCreateNewDanglingFileLink),
        };

        // 唯一目录内只有测试创建的四个文件链接；目标从未创建。
        internal static bool WithFixture(Action<string> run)
        {
            var root = Path.Combine(Path.GetTempPath(),
                "rigi_fs_dangling_delete_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                if (!IsFixtureSupported(root)) return false;
                var missing = Path.Combine(root, "missing.txt");
                foreach (var name in new[] { "primitive", "delete", "ifexists", "remove" })
                {
                    var link = Path.Combine(root, name);
                    try
                    {
                        File.CreateSymbolicLink(link, missing); // 文件链接，不是目录链接
                    }
                    catch (Exception ex) when (OperatingSystem.IsWindows()
                        && (ex is UnauthorizedAccessException
                            || ex is PlatformNotSupportedException
                            || ex is IOException io
                                && (io.HResult & 0xffff) == 1314))
                    {
                        CaseAssertions.RecordSkip("  UNSUPPORTED Windows 文件符号链接创建权限："
                            + ex.GetType().Name + " (非 PASS；VM/native 删除未测)");
                        return false;
                    }
                    CaseAssertions.CheckTrue("宿主创建文件断链条目 " + name,
                        new FileInfo(link).LinkTarget == missing);
                }
                CaseAssertions.CheckTrue("断链目标从未创建", !File.Exists(missing)
                    && !Directory.Exists(missing));
                run(root);
                CaseAssertions.CheckTrue("删除过程未创建/误删目标", !File.Exists(missing)
                    && !Directory.Exists(missing));
                return true;
            }
            finally
            {
                // 仅删除本次自建的 GUID 根目录，绝不碰共享 playground 或其他目录。
                Directory.Delete(root, recursive: true);
            }
        }

        private static bool IsFixtureSupported(string root)
        {
            if (!OperatingSystem.IsLinux()) return true;
            var exit = ExternalProcess.Run("findmnt", ["-no", "FSTYPE", "-T", root],
                out var output, out var error, timeoutMilliseconds: 10_000, closeStdin: true);
            if (exit != 0) throw new InvalidOperationException("fixture FS 探测失败：" + error);
            var fsType = output.Trim();
            if (fsType != "ext4")
            {
                CaseAssertions.RecordSkip("  UNSUPPORTED Linux fixture FS=" + fsType
                    + "（要求 ext4，不将 DrvFs 判为通过）");
                return false;
            }
            Console.WriteLine("  fixture 文件系统：" + fsType);
            return true;
        }

        // 每次调用自建独占文件断链，不依赖 checkout/CWD 中预先存在的目录。
        // 原目标从未存在；Linux 仍要求实际 fixture 位于 ext4。
        internal static bool WithCreateNewFixture(Action<string, string> run)
        {
            var root = Path.Combine(Path.GetTempPath(),
                "rigi_fs_createnew_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                if (!IsFixtureSupported(root)) return false;
                var target = Path.Combine(root, "never-created.txt");
                var link = Path.Combine(root, "dangling.txt");
                try
                {
                    File.CreateSymbolicLink(link, target);
                }
                catch (Exception ex) when (OperatingSystem.IsWindows()
                    && (ex is UnauthorizedAccessException
                        || ex is PlatformNotSupportedException
                        || ex is IOException io && (io.HResult & 0xffff) == 1314))
                {
                    CaseAssertions.RecordSkip("  UNSUPPORTED Windows 文件符号链接权限："
                        + ex.GetType().Name + "（非 PASS）");
                    return false;
                }
                CaseAssertions.CheckTrue("CreateNew fixture 文件断链有效",
                    new FileInfo(link).LinkTarget == target);
                CaseAssertions.CheckTrue("CreateNew 原目标从未创建",
                    !File.Exists(target) && !Directory.Exists(target));
                run(link, target);
                CaseAssertions.CheckTrue("CreateNew 链接条目保留",
                    new FileInfo(link).LinkTarget == target);
                CaseAssertions.CheckTrue("CreateNew 未生成断链目标",
                    !File.Exists(target) && !Directory.Exists(target));
                return true;
            }
            finally
            {
                // 仅删除本次自建 GUID 目录，不依赖或清理共享 playground。
                Directory.Delete(root, recursive: true);
            }
        }

        private static void TestPrimitiveCreateNewDanglingFileLink()
        {
            WithCreateNewFixture((link, _) =>
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "pub func main(): i32 { return 0 }\n");
                CaseAssertions.CheckTrue("CreateNew 原语宿主全管线无诊断",
                    !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
                if (unit.Diagnostics.HasErrors) return;
                var context = new VmContext(module);
                context.InitializeSingletons();
                context.InvokeGlobalInitializers();
                var output = new VmSpan(".u8", 8, new VmU8(0), isShared: false);
                var rc = ((VmI32)context.Dispatch.FsOpen(new VmValue[]
                {
                    new VmString(link), new VmI32(34), new VmI32(438), output,
                })).Value;
                CaseAssertions.CheckTrue("Rigi VM fs_open(34) 已存在 = -17",
                    rc == -17, "rc=" + rc);
            });
        }

        // 两种调用各有独立 fixture；Rigi 异常 kind 与退出码双重断言。
        internal static string CreateNewPublicSource(string link)
        {
            var path = link.Replace('\\', '/');
            return $$"""
                import core.fs.*
                import core.io.Console
                pub func main(): i32 {
                    const p = Path.of("{{path}}")
                    const already: FileSystemErrorKind = .AlreadyExists
                    var ok = false
                    try {
                        const output = File.openWrite(p, .CreateNew)
                        output.dispose()
                    } catch (e: FileSystemException) {
                        ok = e.kind == already
                    }
                    if (ok) {
                        Console.println("fs-createnew-dangling-ok")
                        return 0
                    }
                    Console.println("fs-createnew-dangling-FAIL")
                    return 1
                }
                """;
        }

        internal static string CreateNewPrimitiveSource(string link)
        {
            var path = link.Replace('\\', '/');
            return $$"""
                import core.collections.*
                import core.io.Console
                @NativeLibrary("rigi_rt")
                @NativeSymbol("fs_open")
                native func probeFsOpen(path: String, flags: i32, mode: i32,
                    out: Span\<u8>): i32
                pub func main(): i32 {
                    const output = spanOf\<u8>(8)
                    const rc = probeFsOpen("{{path}}", 34, 438, output)
                    if (rc == (0 - 17)) {
                        Console.println("fs-createnew-primitive-ok")
                        return 0
                    }
                    Console.println("fs-createnew-primitive-FAIL")
                    return 1
                }
                """;
        }

        private static void TestPrimitiveUnlinkDanglingFileLink()
        {
            WithFixture(root =>
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "pub func main(): i32 { return 0 }\n");
                CaseAssertions.CheckTrue("原语宿主全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
                if (unit.Diagnostics.HasErrors) return;
                var context = new VmContext(module);
                context.InitializeSingletons();
                context.InvokeGlobalInitializers();
                var link = Path.Combine(root, "primitive");
                var info = new VmSpan(".u8", 48, new VmU8(0), isShared: false);
                int Lstat() => ((VmI32)context.Dispatch.FsLstat(
                    new VmValue[] { new VmString(link), info })).Value;
                int Unlink() => ((VmI32)context.Dispatch.FsUnlink(
                    new VmValue[] { new VmString(link) })).Value;
                var rc = Lstat();
                CaseAssertions.CheckTrue("fs_lstat 识别断链条目", rc == 0,
                    "rc=" + rc);
                if (rc == 0)
                {
                    // FsStatInfo 首 4 字节为小端 kind；2 = Link。
                    var kind = ((VmU8)info.Elements[0]).Value;
                    CaseAssertions.CheckTrue("fs_lstat 判为 Link", kind == 2,
                        "kind=" + kind);
                }
                rc = Unlink();
                CaseAssertions.CheckTrue("fs_unlink 删除断链成功", rc == 0,
                    "rc=" + rc);
                CaseAssertions.CheckTrue("fs_unlink 后仅链接条目消失",
                    new FileInfo(link).LinkTarget == null);
                rc = Lstat();
                CaseAssertions.CheckTrue("fs_lstat 删除后 NotFound", rc == -2,
                    "rc=" + rc);
                rc = Unlink();
                CaseAssertions.CheckTrue("fs_unlink 再删 NotFound", rc == -2,
                    "rc=" + rc);
                foreach (var name in new[] { "delete", "ifexists", "remove" })
                    CaseAssertions.CheckTrue("其它链接条目未动 " + name,
                        new FileInfo(Path.Combine(root, name)).LinkTarget
                            == Path.Combine(root, "missing.txt"));
            });
        }

        // 同一源码经全管线编译，在各自新建的宿主文件断链 fixture 上执行。
        internal static string PublicSource(string root)
        {
            var path = root.Replace('\\', '/');
            return $$"""
                import core.fs.*
                import core.io.Console
                pub func main(): i32 {
                    const root = "{{path}}"
                    const missing = Path.of("${root}/missing.txt")
                    const a = Path.of("${root}/delete")
                    const b = Path.of("${root}/ifexists")
                    const c = Path.of("${root}/remove")
                    const linkKind: FileKind = .Link
                    var ok = true
                    // 末段 lstat 必须识别断链；随后每种公共删除各自操作一个条目。
                    if (fsLstat(a.text).kind != FS_KIND_LINK) { ok = false }
                    if (getInfo(a, false).kind != linkKind) { ok = false }
                    if (fsLstat(b.text).kind != FS_KIND_LINK) { ok = false }
                    if (fsLstat(c.text).kind != FS_KIND_LINK) { ok = false }
                    File.delete(a)
                    if (exists(a, false)) { ok = false }
                    if (not File.deleteIfExists(b)) { ok = false }
                    if (File.deleteIfExists(b)) { ok = false }
                    if (exists(b, false)) { ok = false }
                    if (fsLstat(c.text).kind != FS_KIND_LINK) { ok = false }
                    removeLink(c)
                    if (exists(c, false)) { ok = false }
                    if (exists(missing, false)) { ok = false }
                    if (ok) {
                        Console.println("fs-dangling-delete-ok")
                        return 0
                    }
                    Console.println("fs-dangling-delete-FAIL")
                    return 1
                }
                """;
        }

        internal static void CheckPublicLinksGone(string root)
        {
            foreach (var name in new[] { "delete", "ifexists", "remove" })
                CaseAssertions.CheckTrue("公共删除后链接条目消失 " + name,
                    new FileInfo(Path.Combine(root, name)).LinkTarget == null);
            CaseAssertions.CheckTrue("公共删除未触及原语用链接",
                new FileInfo(Path.Combine(root, "primitive")).LinkTarget
                    == Path.Combine(root, "missing.txt"));
        }
    }

    public static partial class NativeE2ETests
    {
        // 真实 native 编译、VM 和 native 分别运行，不能让 VM 删后的 fixture
        // 充当 native 的输入；每半场均由宿主 API 重新创建文件符号链接。
        private static void RunDanglingCreateNewCase()
        {
            const string label = "文件断链 CreateNew VM/native";
            var dir = Path.Combine(Environment.CurrentDirectory, "playground",
                "fs_createnew_dangling_20260927", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                VmFsDanglingDeleteTests.WithCreateNewFixture((vmLink, _) =>
                {
                    var module = EmitNativeSource(
                        VmFsDanglingDeleteTests.CreateNewPublicSource(vmLink), label);
                    var vm = BilVm.Run(BilReader.Read(BilWriter.Write(module)),
                        maxSteps: 20_000_000);
                    CaseAssertions.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                        vm.Exception?.Message ?? "");
                    CaseAssertions.Check(label + "：VM stdout", vm.Stdout,
                        "fs-createnew-dangling-ok\n");
                    CaseAssertions.CheckTrue(label + "：VM 退出码 0",
                        vm.ReturnValue is VmI32 { Value: 0 });
                });
                // 原语与公共入口分别走 VM/native，各使用独立新建断链。
                RunCreateNewNativeFixture(dir, label, "primitive",
                    VmFsDanglingDeleteTests.CreateNewPrimitiveSource,
                    "fs-createnew-primitive-ok\n");
                VmFsDanglingDeleteTests.WithCreateNewFixture((vmLink, _) =>
                {
                    var module = EmitNativeSource(
                        VmFsDanglingDeleteTests.CreateNewPrimitiveSource(vmLink), label);
                    var vm = BilVm.Run(BilReader.Read(BilWriter.Write(module)),
                        maxSteps: 20_000_000);
                    CaseAssertions.CheckTrue(label + "：原语 VM 无异常",
                        vm.Exception == null, vm.Exception?.Message ?? "");
                    CaseAssertions.Check(label + "：原语 VM stdout", vm.Stdout,
                        "fs-createnew-primitive-ok\n");
                    CaseAssertions.CheckTrue(label + "：原语 VM 退出码 0",
                        vm.ReturnValue is VmI32 { Value: 0 });
                });
                RunCreateNewNativeFixture(dir, label, "public",
                    VmFsDanglingDeleteTests.CreateNewPublicSource,
                    "fs-createnew-dangling-ok\n");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        private static void RunCreateNewNativeFixture(string dir, string label,
            string stage, Func<string, string> sourceForLink, string expected)
        {
            VmFsDanglingDeleteTests.WithCreateNewFixture((nativeLink, _) =>
            {
                var module = EmitNativeSource(sourceForLink(nativeLink), label);
                var bilPath = Path.Combine(dir, stage + ".bil");
                File.WriteAllText(bilPath, BilWriter.Write(module), new UTF8Encoding(false));
                var exePath = Path.Combine(dir, stage
                    + (OperatingSystem.IsWindows() ? ".exe" : ""));
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                CaseAssertions.CheckTrue(label + "：" + stage + " native 编译链接成功",
                    compiled.Code == 0, compiled.Err);
                if (compiled.Code != 0) return;
                var exit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var stdout, out var stderr, environment: MemtrackEnv,
                    closeStdin: true);
                CaseAssertions.Check(label + "：" + stage + " native stdout",
                    NormalizeNewlines(stdout), expected);
                CaseAssertions.CheckTrue(label + "：" + stage + " native 退出码 0",
                    exit == 0, $"exit={exit} stderr={stderr}");
                CaseAssertions.Check(label + "：" + stage + " native 无诊断", stderr, "");
            });
        }

        private static void RunDanglingDeleteCase()
        {
            const string label = "文件断链删除 VM/native";
            var dir = Path.Combine(Path.GetTempPath(),
                "rigi_fs_dangling_build_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // fixture 的绝对路径只注入自身测试源码；每半场独立建链接。
                var available = VmFsDanglingDeleteTests.WithFixture(vmRoot =>
                {
                    var source = VmFsDanglingDeleteTests.PublicSource(vmRoot);
                    var module = EmitNativeSource(source, label);
                    var text = BilWriter.Write(module);
                    var vm = BilVm.Run(BilReader.Read(text), maxSteps: 20_000_000);
                    CaseAssertions.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                        vm.Exception?.Message ?? "");
                    CaseAssertions.Check(label + "：VM stdout", vm.Stdout,
                        "fs-dangling-delete-ok\n");
                    CaseAssertions.CheckTrue(label + "：VM 退出码 0",
                        vm.ReturnValue is VmI32 { Value: 0 });
                    VmFsDanglingDeleteTests.CheckPublicLinksGone(vmRoot);
                });
                if (!available) return; // 权限/文件系统不支持，native 不得算 PASS
                // native 独立 fixture：重新编译嵌入自身根的 Rigi 源码。
                VmFsDanglingDeleteTests.WithFixture(nativeRoot =>
                {
                    var nativeModule = EmitNativeSource(
                        VmFsDanglingDeleteTests.PublicSource(nativeRoot), label);
                    var bilPath = Path.Combine(dir, "case.bil");
                    File.WriteAllText(bilPath, BilWriter.Write(nativeModule),
                        new UTF8Encoding(false));
                    var exePath = Path.Combine(dir,
                        OperatingSystem.IsWindows() ? "case.exe" : "case");
                    var compiled = RunNative("native", "--file", bilPath,
                        "--out", exePath);
                    CaseAssertions.CheckTrue(label + "：native 编译链接成功",
                        compiled.Code == 0, compiled.Err);
                    if (compiled.Code != 0) return;
                    var exit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                        out var stdout, out var stderr, environment: MemtrackEnv,
                        closeStdin: true);
                    CaseAssertions.Check(label + "：native stdout",
                        NormalizeNewlines(stdout), "fs-dangling-delete-ok\n");
                    CaseAssertions.CheckTrue(label + "：native 退出码 0",
                        exit == 0, $"exit={exit} stderr={stderr}");
                    CaseAssertions.Check(label + "：native 无诊断", stderr, "");
                    VmFsDanglingDeleteTests.CheckPublicLinksGone(nativeRoot);
                });
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
