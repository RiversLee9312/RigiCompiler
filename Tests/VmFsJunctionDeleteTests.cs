using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    /// <summary>Windows junction 删除：每次调用使用独立 GUID 根和非空目标。</summary>
    public static class VmFsJunctionDeleteTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);
        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);
        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static ParallelSuiteRunner.SuiteSpec Spec => new(
            "VmFsJunctionDelete", Cases, sectionTitle: "VmFsJunctionDelete");
        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestPrimitiveJunctionUnlink", TestPrimitiveJunctionUnlink),
            ("TestPublicJunctionDelete", TestPublicJunctionDelete),
            ("TestPublicJunctionReplace", TestPublicJunctionReplace),
        };

        private static readonly byte[] Sentinel = Encoding.UTF8.GetBytes(
            "junction-target-must-survive-20260928\n");
        // fixture 仅在本请求的 TMP 根下创建 GUID 子目录；不依赖仓库/playground。
        private static string Parent => Path.Combine(Path.GetTempPath(), "junction-fixtures");

        [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool RemoveJunction(string path);

        // 不清理目标；只允许对自身创建、仍指向本次目标的 alias 调用非递归 API。
        internal static void WithFixture(Action<string> run)
        {
            if (!OperatingSystem.IsWindows())
            {
                TestHarness.RecordSkip("  UNSUPPORTED 非 Windows（junction 未测）");
                return;
            }
            // 不创建或清理共享父目录；仅拥有其下本次 GUID fixture。
            var parent = Path.GetDirectoryName(Parent)!;
            if (!Directory.Exists(parent)
                || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("fixture 父目录缺失或非真目录：" + parent);
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            if (!IsAbsent(root))
                throw new InvalidOperationException("GUID 根已经存在：" + root);
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
                || Directory.GetFileSystemEntries(root).Length != 0)
                throw new InvalidOperationException("刚创建的 GUID 根非空，保留现场：" + root);
            var target = Path.Combine(root, "target");
            var sentinel = Path.Combine(target, "sentinel.bin");
            var aliases = new[] { "primitive", "delete", "remove", "directory" }
                .Select(name => Path.Combine(root, name)).ToArray();
            var owned = new List<string>();
            try
            {
                Directory.CreateDirectory(target);
                File.WriteAllBytes(sentinel, Sentinel);
                foreach (var alias in aliases)
                {
                    if (File.Exists(alias) || Directory.Exists(alias))
                        throw new InvalidOperationException("自有 GUID 根中 alias 已存在：" + alias);
                    using var proc = Process.Start(new ProcessStartInfo("cmd.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        ArgumentList = { "/c", "mklink", "/J", alias, target },
                    }) ?? throw new InvalidOperationException("无法启动 mklink");
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(10_000) || proc.ExitCode != 0)
                    {
                        // 若 mklink 已创建但输出/退出异常，仍仅在自有身份确认后清理。
                        if (IsOwnedAlias(alias, target)) owned.Add(alias);
                        throw new InvalidOperationException("mklink /J 失败：" + stdout + stderr);
                    }
                    // 验明归属后才纳入清理列表；验证失败时保留现场，绝不冒险删除。
                    if (!IsOwnedAlias(alias, target))
                        throw new InvalidOperationException("junction 身份验证失败：" + alias);
                    owned.Add(alias);
                }
                run(root);
            }
            finally
            {
                try
                {
                    foreach (var alias in owned)
                    {
                        if (!Directory.Exists(alias) && !File.Exists(alias)) continue;
                        if (!IsOwnedAlias(alias, target))
                            throw new InvalidOperationException("alias 归属变化，保留现场：" + alias);
                        if (!RemoveJunction(alias))
                            throw new IOException("非递归移除自有 junction 失败：" + alias,
                                Marshal.GetHRForLastWin32Error());
                    }
                }
                finally
                {
                    // target/sentinel 连同 GUID 根保留为证据；从不递归删除 alias/target。
                    TestHarness.CheckTrue("非空目标 sentinel 原字节保留 " + root,
                        File.Exists(sentinel)
                        && File.ReadAllBytes(sentinel).SequenceEqual(Sentinel));
                }
            }
        }

        private static bool IsOwnedAlias(string alias, string target)
        {
            if ((File.GetAttributes(alias) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) return false;
            var link = new DirectoryInfo(alias).LinkTarget;
            return link != null && string.Equals(Path.GetFullPath(link),
                Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }

        internal static void CheckAlias(string root, string name, bool present)
        {
            var alias = Path.Combine(root, name);
            var target = Path.Combine(root, "target");
            TestHarness.CheckTrue(name + (present ? " 仍为自有 junction" : " 链接条目消失"),
                present ? IsOwnedAlias(alias, target) : !Directory.Exists(alias)
                    && !File.Exists(alias));
            var sentinel = Path.Combine(target, "sentinel.bin");
            TestHarness.CheckTrue(name + " 未触及非空目标原字节",
                File.Exists(sentinel) && File.ReadAllBytes(sentinel).SequenceEqual(Sentinel));
        }

        private static void TestPrimitiveJunctionUnlink()
        {
            WithFixture(root =>
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "pub func main(): i32 { return 0 }\n");
                TestHarness.CheckTrue("原语宿主全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
                if (unit.Diagnostics.HasErrors) return;
                var context = new VmContext(module);
                context.InitializeSingletons();
                context.InvokeGlobalInitializers();
                var path = Path.Combine(root, "primitive");
                var info = new VmSpan(".u8", 48, new VmU8(0), isShared: false);
                var rc = ((VmI32)context.Dispatch.FsLstat(new VmValue[]
                    { new VmString(path), info })).Value;
                TestHarness.CheckTrue("fs_lstat junction 成功且 Link", rc == 0
                    && ((VmU8)info.Elements[0]).Value == 2, "rc=" + rc);
                rc = ((VmI32)context.Dispatch.FsUnlink(new VmValue[]
                    { new VmString(path) })).Value;
                TestHarness.CheckTrue("VM fs_unlink junction 成功", rc == 0,
                    "rc=" + rc);
                CheckAlias(root, "primitive", present: false);
                CheckAlias(root, "directory", present: true);
            });
        }

        internal static string PublicSource(string root)
        {
            var path = root.Replace('\\', '/');
            return $$"""
                import core.fs.*
                import core.io.Console
                pub func main(): i32 {
                    const root = "{{path}}"
                    const a = Path.of("${root}/delete")
                    const b = Path.of("${root}/remove")
                    const c = Path.of("${root}/directory")
                    const linkKind: FileKind = .Link
                    const notDir: FileSystemErrorKind = .NotDirectory
                    var ok = true
                    if (getInfo(a, false).kind != linkKind) { ok = false }
                    if (getInfo(b, false).kind != linkKind) { ok = false }
                    File.delete(a)
                    if (exists(a, false)) { ok = false }
                    removeLink(b)
                    if (exists(b, false)) { ok = false }
                    try {
                        Directory.delete(c)
                        ok = false
                    } catch (e: FileSystemException) {
                        if (e.kind != notDir) { ok = false }
                    }
                    if (not exists(c, false)) { ok = false }
                    if (ok) {
                        Console.println("junction-delete-ok")
                        return 0
                    }
                    Console.println("junction-delete-FAIL")
                    return 1
                }
                """;
        }

        internal static string PrimitiveSource(string root)
        {
            var path = Path.Combine(root, "primitive").Replace('\\', '/');
            return $$"""
                import core.collections.*
                import core.io.Console
                @NativeLibrary("rigi_rt")
                @NativeSymbol("fs_lstat")
                native func probeLstat(path: String, out: Span\<u8>): i32
                @NativeLibrary("rigi_rt")
                @NativeSymbol("fs_unlink")
                native func probeUnlink(path: String): i32
                pub func main(): i32 {
                    const info = spanOf\<u8>(48)
                    const path = "{{path}}"
                    const before = probeLstat(path, info)
                    const deleted = probeUnlink(path)
                    var ok = before == 0
                    if (deleted != 0) { ok = false }
                    if (ok) {
                        Console.println("junction-primitive-ok")
                        return 0
                    }
                    Console.println("junction-primitive-FAIL")
                    return 1
                }
                """;
        }

        private static void TestPublicJunctionDelete()
        {
            WithFixture(root =>
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(PublicSource(root));
                TestHarness.CheckTrue("公共面全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
                if (unit.Diagnostics.HasErrors) return;
                var vm = BilVm.Run(BilReader.Read(BilWriter.Write(module)),
                    maxSteps: 20_000_000);
                TestHarness.CheckTrue("VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                TestHarness.Check("VM stdout", vm.Stdout, "junction-delete-ok\n");
                TestHarness.CheckTrue("VM 退出码 0", vm.ReturnValue is VmI32 { Value: 0 });
                CheckAlias(root, "delete", present: false);
                CheckAlias(root, "remove", present: false);
                CheckAlias(root, "directory", present: true);
                CheckAlias(root, "primitive", present: true);
            });
        }

        internal sealed class MoveFixture
        {
            internal required string Root;
            internal required string Target;
            internal required string SentinelPath;
            internal required string AliasNoReplace;
            internal required string AliasReplace;
            internal required string SourceNoReplace;
            internal required string SourceReplace;
            internal required string EmptyDirectory;
            internal required string NonemptyDirectory;
            internal required string NonemptySentinel;
            internal required string EmptySource;
            internal required string NonemptySource;
            internal required byte[] SourceBytes;
            internal required byte[] SentinelBytes;
        }

        // Windows mount point：按句柄查 tag=0xA0000003，另核替代路径仅指本
        // GUID root 的 target；不能靠 Directory.Exists 把任意目录当自有链接。
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenReparse(
            string path, uint access, uint share, IntPtr security, uint creation,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
            SetLastError = true)]
        private static extern bool ReadReparseTag(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass,
            out AttributeTag tag, uint size);

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag
        {
            public uint Attributes;
            public uint Tag;
        }

        private static bool IsOwnedMountPoint(string alias, string target)
        {
            var attrs = File.GetAttributes(alias);
            if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != (FileAttributes.Directory | FileAttributes.ReparsePoint))
                return false;
            using var handle = OpenReparse(alias, 0x80 /* READ_ATTRIBUTES */,
                7 /* SHARE_READ|WRITE|DELETE */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 | 0x00200000 /* BACKUP_SEMANTICS|OPEN_REPARSE_POINT */,
                IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("读取 junction 句柄失败",
                Marshal.GetHRForLastWin32Error());
            if (!ReadReparseTag(handle, 9 /* FileAttributeTagInfo */,
                out var tag, 8)) throw new IOException("读取 reparse tag 失败",
                    Marshal.GetHRForLastWin32Error());
            var destination = new DirectoryInfo(alias).LinkTarget;
            return (tag.Attributes & 0x410 /* DIRECTORY|REPARSE */) == 0x410
                && tag.Tag == 0xA0000003 /* MOUNT_POINT */
                && destination != null
                && string.Equals(Path.GetFullPath(destination),
                    Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAbsent(string path)
        {
            try { _ = File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }

        // 六个操作面共用一个新建空 GUID 根；只有所有子项仍属本 fixture
        // 才可清理。绝不递归删除 alias，也不遍历 alias 内部。
        internal static void WithMoveFixture(Action<MoveFixture> run)
        {
            if (!OperatingSystem.IsWindows())
            {
                TestHarness.RecordSkip("  UNSUPPORTED 非 Windows（junction move 未测）");
                return;
            }
            // playground/ 含已跟踪文件，干净 checkout 即存在；不依赖
            // 上次本地探针生成的 gitignored 子目录，也不清理其他内容。
            var parent = Path.GetDirectoryName(Parent)!;
            if (!Directory.Exists(parent)
                || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("fixture 父目录缺失或非真目录：" + parent);
            var root = Path.Combine(parent, "formal-move-" + Guid.NewGuid().ToString("N"));
            if (!IsAbsent(root)) throw new InvalidOperationException("GUID 根已存在：" + root);
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
                || Directory.GetFileSystemEntries(root).Length != 0)
                throw new InvalidOperationException("刚创建的 GUID 根非空，保留现场：" + root);
            var target = Path.Combine(root, "target-" + Guid.NewGuid().ToString("N"));
            var sentinel = Path.Combine(target, "sentinel-" + Guid.NewGuid().ToString("N"));
            var nonempty = Path.Combine(root, "nonempty-" + Guid.NewGuid().ToString("N"));
            var fixture = new MoveFixture
            {
                Root = root,
                Target = target,
                SentinelPath = sentinel,
                AliasNoReplace = Path.Combine(root, "alias-nr-" + Guid.NewGuid().ToString("N")),
                AliasReplace = Path.Combine(root, "alias-r-" + Guid.NewGuid().ToString("N")),
                SourceNoReplace = Path.Combine(root, "src-nr-" + Guid.NewGuid().ToString("N")),
                SourceReplace = Path.Combine(root, "src-r-" + Guid.NewGuid().ToString("N")),
                EmptyDirectory = Path.Combine(root, "empty-" + Guid.NewGuid().ToString("N")),
                NonemptyDirectory = nonempty,
                NonemptySentinel = Path.Combine(nonempty, "inner-" + Guid.NewGuid().ToString("N")),
                EmptySource = Path.Combine(root, "src-empty-" + Guid.NewGuid().ToString("N")),
                NonemptySource = Path.Combine(root, "src-nonempty-" + Guid.NewGuid().ToString("N")),
                SourceBytes = Encoding.UTF8.GetBytes("junction-move-source-" + Guid.NewGuid().ToString("N")),
                SentinelBytes = Encoding.UTF8.GetBytes("junction-target-sentinel-" + Guid.NewGuid().ToString("N")),
            };
            bool created = false;
            bool completed = false;
            try
            {
                Directory.CreateDirectory(target);
                File.WriteAllBytes(sentinel, fixture.SentinelBytes);
                Directory.CreateDirectory(fixture.EmptyDirectory);
                Directory.CreateDirectory(nonempty);
                File.WriteAllBytes(fixture.NonemptySentinel, fixture.SentinelBytes);
                foreach (var source in MoveSources(fixture))
                    File.WriteAllBytes(source, fixture.SourceBytes);
                foreach (var alias in MoveAliases(fixture))
                {
                    if (!IsAbsent(alias)) throw new InvalidOperationException("alias 已存在：" + alias);
                    using var process = Process.Start(new ProcessStartInfo("cmd.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        ArgumentList = { "/c", "mklink", "/J", alias, target },
                    }) ?? throw new InvalidOperationException("mklink 启动失败");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(10_000))
                        throw new TimeoutException("mklink 超时，保留现场：" + root);
                    if (process.ExitCode != 0)
                        throw new IOException("mklink 失败：" + stdout.Result + stderr.Result);
                    if (!IsOwnedMountPoint(alias, target))
                        throw new InvalidOperationException("alias 归属不符，保留现场：" + alias);
                }
                created = true;
                run(fixture);
                completed = true;
            }
            finally
            {
                // 构造未完成/判别失败则保留现场；成功后清理仍逐项验身份。
                if (created && completed) CleanupMoveFixture(fixture);
            }
        }

        private static string[] MoveSources(MoveFixture f) => new[]
        {
            f.SourceNoReplace, f.SourceReplace, f.EmptySource, f.NonemptySource,
        };

        private static string[] MoveAliases(MoveFixture f) => new[]
        {
            f.AliasNoReplace, f.AliasReplace,
        };

        private static void CleanupMoveFixture(MoveFixture f)
        {
            var remainingSources = MoveSources(f).Where(s => !IsAbsent(s)).ToArray();
            var expected = new[] { f.Target, f.EmptyDirectory,
                f.NonemptyDirectory, f.AliasNoReplace, f.AliasReplace }
                .Concat(remainingSources).ToArray();
            var actual = Directory.GetFileSystemEntries(f.Root);
            if ((File.GetAttributes(f.Root) & FileAttributes.ReparsePoint) != 0
                || actual.Length != expected.Length
                || !expected.All(actual.Contains)
                || !IsOwnedMountPoint(f.AliasNoReplace, f.Target)
                || !File.ReadAllBytes(f.SentinelPath).SequenceEqual(f.SentinelBytes)
                || !File.ReadAllBytes(f.NonemptySentinel).SequenceEqual(f.SentinelBytes)
                || Directory.GetFileSystemEntries(f.Target).Length != 1
                || Directory.GetFileSystemEntries(f.NonemptyDirectory).Length != 1
                || Directory.GetFileSystemEntries(f.EmptyDirectory).Length != 0)
                throw new InvalidOperationException("清理前 fixture 身份/字节变化，保留：" + f.Root);
            if (!IsAbsent(f.SourceReplace)
                && (File.GetAttributes(f.AliasReplace) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                throw new InvalidOperationException("Replace 源与 alias 同时存在，保留：" + f.Root);
            foreach (var source in remainingSources)
            {
                if ((File.GetAttributes(source) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || !File.ReadAllBytes(source).SequenceEqual(f.SourceBytes))
                    throw new InvalidOperationException("源已换型，保留：" + f.Root);
            }
            if (IsOwnedMountPoint(f.AliasReplace, f.Target))
            {
                // 未替换：只能用非递归 RemoveDirectoryW 删除已验明 junction。
                if (!RemoveJunction(f.AliasReplace))
                    throw new IOException("移除自有 junction 失败", Marshal.GetHRForLastWin32Error());
            }
            else if ((File.GetAttributes(f.AliasReplace) &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                && File.ReadAllBytes(f.AliasReplace).SequenceEqual(f.SourceBytes))
            {
                // 已替换：普通文件仅 File.Delete；绝不对 alias 递归删除。
                File.Delete(f.AliasReplace);
            }
            else throw new InvalidOperationException("alias 身份未知，保留：" + f.Root);
            if (!IsAbsent(f.AliasReplace))
                throw new InvalidOperationException("alias 尚在，保留：" + f.Root);
            if (!IsOwnedMountPoint(f.AliasNoReplace, f.Target)
                || !RemoveJunction(f.AliasNoReplace))
                throw new InvalidOperationException("NoReplace alias 归属变更/移除失败：" + f.Root);
            if (!IsAbsent(f.AliasNoReplace))
                throw new InvalidOperationException("NoReplace alias 尚在，保留：" + f.Root);
            foreach (var source in remainingSources) File.Delete(source);
            if (!File.ReadAllBytes(f.SentinelPath).SequenceEqual(f.SentinelBytes))
                throw new InvalidOperationException("target sentinel 变化，保留：" + f.Root);
            File.Delete(f.SentinelPath);
            File.Delete(f.NonemptySentinel);
            Directory.Delete(f.Target, recursive: false);
            Directory.Delete(f.EmptyDirectory, recursive: false);
            Directory.Delete(f.NonemptyDirectory, recursive: false);
            Directory.Delete(f.Root, recursive: false);
        }

        internal static string MoveSource(MoveFixture f)
        {
            static string Q(string path) => path.Replace('\\', '/');
            return $$"""
                import core.fs.*
                import core.io.Console
                pub func main(): i32 {
                    var ok = true
                    const already: FileSystemErrorKind = .AlreadyExists
                    const isdir: FileSystemErrorKind = .IsDirectory
                    try {
                        move(Path.of("{{Q(f.SourceNoReplace)}}"), Path.of("{{Q(f.AliasNoReplace)}}"), .NoReplace)
                        ok = false
                    } catch (e: FileSystemException) {
                        if (e.kind != already) { ok = false }
                    }
                    if (not exists(Path.of("{{Q(f.SourceNoReplace)}}"), false)) { ok = false }
                    const linkKind: FileKind = .Link
                    const fileKind: FileKind = .File
                    if (getInfo(Path.of("{{Q(f.AliasNoReplace)}}"), false).kind != linkKind) { ok = false }
                    move(Path.of("{{Q(f.SourceReplace)}}"), Path.of("{{Q(f.AliasReplace)}}"), .Replace)
                    if (exists(Path.of("{{Q(f.SourceReplace)}}"), false)) { ok = false }
                    if (getInfo(Path.of("{{Q(f.AliasReplace)}}"), false).kind != fileKind) { ok = false }
                    try {
                        move(Path.of("{{Q(f.EmptySource)}}"), Path.of("{{Q(f.EmptyDirectory)}}"), .Replace)
                        ok = false
                    } catch (e: FileSystemException) {
                        if (e.kind != isdir) { ok = false }
                    }
                    try {
                        move(Path.of("{{Q(f.NonemptySource)}}"), Path.of("{{Q(f.NonemptyDirectory)}}"), .Replace)
                        ok = false
                    } catch (e: FileSystemException) {
                        if (e.kind != isdir) { ok = false }
                    }
                    if (ok) {
                        Console.println("junction-move-ok")
                        return 0
                    }
                    Console.println("junction-move-FAIL")
                    return 1
                }
                """;
        }

        internal static void RemoveMoveNativeOutputs(MoveFixture f)
        {
            // Native 编译产物仅准位于本次新建 root，完成后需核名和字节
            // 独立移除；真实目标/sentinel 的身份验证仍交 CleanupMoveFixture。
            var outputPaths = new[] { Path.Combine(f.Root, "move.bil"),
                Path.Combine(f.Root, "move.exe") };
            var expected = new[] { f.Target, f.EmptyDirectory,
                f.NonemptyDirectory, f.AliasNoReplace, f.AliasReplace }
                .Concat(MoveSources(f).Where(s => !IsAbsent(s)))
                .Concat(outputPaths).ToArray();
            var actual = Directory.GetFileSystemEntries(f.Root);
            if (actual.Length != expected.Length || !expected.All(actual.Contains))
                throw new InvalidOperationException("native root 条目不符，保留：" + f.Root);
            foreach (var path in outputPaths)
            {
                var attr = File.GetAttributes(path);
                if ((attr & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || !File.ReadAllBytes(path).Any())
                    throw new InvalidOperationException("native 产物归属不符，保留：" + path);
            }
            foreach (var path in outputPaths) File.Delete(path);
        }

        internal static void CheckMoveResult(MoveFixture f)
        {
            // stdout/退出码外仍独立按真实文件字节及末段链接身份判别。
            TestHarness.CheckTrue("NoReplace 源原字节留存",
                File.ReadAllBytes(f.SourceNoReplace).SequenceEqual(f.SourceBytes));
            TestHarness.CheckTrue("NoReplace junction tag 与目标留存",
                IsOwnedMountPoint(f.AliasNoReplace, f.Target));
            TestHarness.CheckTrue("Replace 源消失", IsAbsent(f.SourceReplace));
            TestHarness.CheckTrue("Replace alias 是原字节普通文件",
                (File.GetAttributes(f.AliasReplace) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                && File.ReadAllBytes(f.AliasReplace).SequenceEqual(f.SourceBytes));
            TestHarness.CheckTrue("原目标 sentinel 原字节留存",
                File.ReadAllBytes(f.SentinelPath).SequenceEqual(f.SentinelBytes));
            TestHarness.CheckTrue("空目录未覆盖且源原字节留存",
                (File.GetAttributes(f.EmptyDirectory) & FileAttributes.Directory) != 0
                && Directory.GetFileSystemEntries(f.EmptyDirectory).Length == 0
                && File.ReadAllBytes(f.EmptySource).SequenceEqual(f.SourceBytes));
            TestHarness.CheckTrue("非空目录未覆盖且源/内层 sentinel 原字节留存",
                (File.GetAttributes(f.NonemptyDirectory) & FileAttributes.Directory) != 0
                && File.ReadAllBytes(f.NonemptySentinel).SequenceEqual(f.SentinelBytes)
                && File.ReadAllBytes(f.NonemptySource).SequenceEqual(f.SourceBytes));
            if (!IsOwnedMountPoint(f.AliasNoReplace, f.Target)
                || !IsAbsent(f.SourceReplace)
                || (File.GetAttributes(f.AliasReplace) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !File.ReadAllBytes(f.AliasReplace).SequenceEqual(f.SourceBytes)
                || MoveSources(f).Where(s => s != f.SourceReplace).Any(s =>
                    (File.GetAttributes(s) & (FileAttributes.Directory
                        | FileAttributes.ReparsePoint)) != 0
                    || !File.ReadAllBytes(s).SequenceEqual(f.SourceBytes))
                || !File.ReadAllBytes(f.SentinelPath).SequenceEqual(f.SentinelBytes)
                || !File.ReadAllBytes(f.NonemptySentinel).SequenceEqual(f.SentinelBytes)
                || (File.GetAttributes(f.EmptyDirectory) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory
                || Directory.GetFileSystemEntries(f.EmptyDirectory).Length != 0
                || (File.GetAttributes(f.NonemptyDirectory) &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory
                || Directory.GetFileSystemEntries(f.NonemptyDirectory).Length != 1)
                throw new InvalidOperationException("junction move 身份/原字节不符，保留 fixture：" + f.Root);
        }

        private static void TestPublicJunctionReplace()
        {
            WithMoveFixture(fixture =>
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    MoveSource(fixture));
                TestHarness.CheckTrue("junction move 公共面编译零诊断",
                    !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
                if (unit.Diagnostics.HasErrors) return;
                var vm = BilVm.Run(BilReader.Read(BilWriter.Write(module)),
                    maxSteps: 5_000_000);
                TestHarness.CheckTrue("junction move VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                TestHarness.Check("junction move VM 独立 stdout", vm.Stdout,
                    "junction-move-ok\n");
                TestHarness.CheckTrue("junction move VM 退出码 0",
                    vm.ReturnValue is VmI32 { Value: 0 });
                CheckMoveResult(fixture);
                if (vm.Exception != null || vm.Stdout != "junction-move-ok\n"
                    || vm.ReturnValue is not VmI32 { Value: 0 })
                    throw new InvalidOperationException("公共 move VM 判别失败，保留 fixture");
            });
        }
    }

    public static partial class NativeE2ETests
    {
        private static void RunJunctionReplaceNativeCase()
        {
            if (!OperatingSystem.IsWindows())
            {
                TestHarness.RecordSkip("  SKIP Windows junction Replace：非 Windows 宿主");
                return;
            }
            VmFsJunctionDeleteTests.WithMoveFixture(fixture =>
            {
                const string label = "Windows junction Replace 原生公共 move 与原字节";
                var module = EmitNativeSource(
                    VmFsJunctionDeleteTests.MoveSource(fixture), label);
                var bilPath = Path.Combine(fixture.Root, "move.bil");
                var exePath = Path.Combine(fixture.Root, "move.exe");
                File.WriteAllText(bilPath, BilWriter.Write(module),
                    new UTF8Encoding(false));
                var compiled = RunNative("native", "--file", bilPath,
                    "--out", exePath);
                TestHarness.CheckTrue(label + " 编译链接成功",
                    compiled.Code == 0, compiled.Err);
                if (compiled.Code != 0) return; // 失败现场不删。
                var exit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var stdout, out var stderr, environment: MemtrackEnv,
                    closeStdin: true);
                TestHarness.Check(label + " 独立 stdout",
                    NormalizeNewlines(stdout), "junction-move-ok\n");
                TestHarness.CheckTrue(label + " 独立退出码 0", exit == 0,
                    $"exit={exit} stderr={stderr}");
                TestHarness.Check(label + " 无诊断/泄漏", stderr, "");
                VmFsJunctionDeleteTests.CheckMoveResult(fixture);
                if (exit != 0 || NormalizeNewlines(stdout) != "junction-move-ok\n"
                    || stderr.Length != 0)
                    throw new InvalidOperationException("native 独立结果失败，保留 fixture");
                VmFsJunctionDeleteTests.RemoveMoveNativeOutputs(fixture);
            });
        }

        private static void RunJunctionDeleteCase()
        {
            if (!OperatingSystem.IsWindows())
            {
                TestHarness.RecordSkip("  UNSUPPORTED 非 Windows（junction 未测）");
                return;
            }
            // 两种入口各用独立 GUID 根；构建产物只放入独占 playground。
            RunJunctionNativeFixture(VmFsJunctionDeleteTests.PublicSource,
                "junction-delete-ok\n", "public", "delete", "remove");
            RunJunctionNativeFixture(VmFsJunctionDeleteTests.PrimitiveSource,
                "junction-primitive-ok\n", "primitive", "primitive");
        }

        private static void RunJunctionNativeFixture(Func<string, string> source,
            string expected, string stage, params string[] deleted)
        {
            VmFsJunctionDeleteTests.WithFixture(root =>
            {
                const string label = "junction 删除 VM/native";
                var module = EmitNativeSource(source(root), label + "：" + stage);
                var bilPath = Path.Combine(root, stage + ".bil");
                File.WriteAllText(bilPath, BilWriter.Write(module), new UTF8Encoding(false));
                var exePath = Path.Combine(root, stage + ".exe");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：" + stage + " native 编译链接成功",
                    compiled.Code == 0, compiled.Err);
                if (compiled.Code != 0) return;
                var exit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var stdout, out var stderr, environment: MemtrackEnv,
                    closeStdin: true);
                TestHarness.Check(label + "：" + stage + " stdout",
                    NormalizeNewlines(stdout), expected);
                TestHarness.CheckTrue(label + "：" + stage + " 退出码 0", exit == 0,
                    $"exit={exit} stderr={stderr}");
                TestHarness.Check(label + "：" + stage + " 无诊断", stderr, "");
                foreach (var name in new[] { "primitive", "delete", "remove", "directory" })
                    VmFsJunctionDeleteTests.CheckAlias(root, name,
                        present: !deleted.Contains(name));
            });
        }
    }
}
