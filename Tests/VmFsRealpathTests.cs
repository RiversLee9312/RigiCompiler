using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>真实磁盘 fixture 校验 fs_realpath 对所有路径分量的解析。</summary>
    public static class VmFsRealpathTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);
        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "VmFsRealpath", Cases, sectionTitle: "VmFsRealpath");
        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestPlainAndUtf8Buffer", TestPlainAndUtf8Buffer),
            ("TestIntermediateAndTerminalLink", TestIntermediateAndTerminalLink),
            ("TestLinkBeforeDotDotLinux", TestLinkBeforeDotDotLinux),
            ("TestBrokenAndLoopLinks", TestBrokenAndLoopLinks),
        };

        private static VmDispatch NewDispatch()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 0 }\n");
            TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var context = new VmContext(module);
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            return context.Dispatch;
        }

        private static string NewRoot(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "rigi_realpath_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static (int Rc, string Text, int Length) Resolve(
            VmDispatch dispatch, string path, int capacity = 2048)
        {
            var output = new VmSpan(".u8", capacity, new VmU8(0), false);
            var meta = new VmSpan(".u8", 4, new VmU8(0), false);
            var rc = ((VmI32)dispatch.FsRealpath(new VmValue[]
            {
                new VmString(path), output, meta,
            })).Value;
            var length = ((VmU8)meta.Elements[0]).Value
                | (((VmU8)meta.Elements[1]).Value << 8)
                | (((VmU8)meta.Elements[2]).Value << 16)
                | (((VmU8)meta.Elements[3]).Value << 24);
            var text = rc == 0
                ? new UTF8Encoding(false, true).GetString(Enumerable.Range(0, length)
                    .Select(i => ((VmU8)output.Elements[i]).Value).ToArray())
                : "";
            return (rc, text, length);
        }

        private static void TestPlainAndUtf8Buffer()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("plain");
            try
            {
                var plain = Path.Combine(root, "文件_é.txt");
                File.WriteAllText(plain, "ok");
                var (rc, text, length) = Resolve(dispatch, plain);
                TestHarness.CheckTrue("普通非 ASCII 文件解析成功", rc == 0,
                    "rc=" + rc);
                TestHarness.CheckTrue("普通路径返回绝对真实路径",
                    text == plain, "actual=" + text);
                var (smallRc, _, required) = Resolve(dispatch, plain, 1);
                TestHarness.CheckTrue("容量不足回正数哨兵 2", smallRc == 2,
                    "rc=" + smallRc);
                TestHarness.CheckTrue("meta 是 UTF-8 字节长度",
                    length == Encoding.UTF8.GetByteCount(text)
                    && required == length && length > text.Length,
                    "length=" + length + " required=" + required);
                var (missing, _, _) = Resolve(dispatch,
                    Path.Combine(root, "absent"));
                TestHarness.CheckTrue("缺失目标归 NotFound", missing == -2,
                    "rc=" + missing);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static void TestIntermediateAndTerminalLink()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("links");
            try
            {
                var real = Path.Combine(root, "real");
                Directory.CreateDirectory(real);
                var child = Path.Combine(real, "a.txt");
                File.WriteAllText(child, "ok");
                var alias = Path.Combine(root, "alias");
                // Windows 普通 symlink 可能需开发者权限；目录 junction 是
                // 不依赖此权限的同一 reparse 跟随语义。两种皆不可用则 FAIL。
                try { Directory.CreateSymbolicLink(alias, real); }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException or NotSupportedException)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var result = System.Diagnostics.Process.Start(new
                            System.Diagnostics.ProcessStartInfo("cmd.exe")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                ArgumentList = { "/c", "mklink", "/J", alias, real },
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            });
                        result!.WaitForExit();
                        TestHarness.CheckTrue("symlink 或 junction fixture 可用",
                            result.ExitCode == 0, result.StandardError.ReadToEnd());
                        if (result.ExitCode != 0) { return; }
                    }
                    else
                    {
                        TestHarness.CheckTrue("Linux symlink fixture 可用", false,
                            ex.ToString());
                        return;
                    }
                }
                var (rc, text, _) = Resolve(dispatch, Path.Combine(alias, "a.txt"));
                TestHarness.CheckTrue("中间目录链接真实解析", rc == 0
                    && text == child, "rc=" + rc + " actual=" + text);
                var (dirRc, dirText, _) = Resolve(dispatch, alias);
                TestHarness.CheckTrue("末段目录链接真实解析", dirRc == 0
                    && dirText == real, "rc=" + dirRc + " actual=" + dirText);
                var fileAlias = Path.Combine(root, "filealias");
                try
                {
                    File.CreateSymbolicLink(fileAlias, child);
                    var (fileRc, fileText, _) = Resolve(dispatch, fileAlias);
                    TestHarness.CheckTrue("末段文件链接真实解析", fileRc == 0
                        && fileText == child, "rc=" + fileRc
                        + " actual=" + fileText);
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException or NotSupportedException)
                {
                    Console.WriteLine("  UNSUPPORTED 末段文件符号链接 fixture："
                        + ex.Message);
                }
            }
            finally
            {
                // junction 先单独移除链接条目：Windows recursive Delete
                // 遇到 reparse 目录会抛 UnauthorizedAccess，绝不递归目标。
                var alias = Path.Combine(root, "alias");
                if (Directory.Exists(alias)) { Directory.Delete(alias); }
                Directory.Delete(root, recursive: true);
            }
        }

        private static void TestLinkBeforeDotDotLinux()
        {
            if (!OperatingSystem.IsLinux())
            {
                Console.WriteLine("  SKIP 链接前 ..：仅 Linux 实盘链接解析，当前平台未验证");
                return;
            }
            var dispatch = NewDispatch();
            var root = NewRoot("dotdot");
            var link = Path.Combine(root, "link");
            try
            {
                var real = Path.Combine(root, "real");
                Directory.CreateDirectory(Path.Combine(real, "sub"));
                var marker = Path.Combine(real, "marker");
                File.WriteAllBytes(marker, new byte[] { 0x72, 0x65, 0x61, 0x6c });
                Directory.CreateSymbolicLink(link, "real/sub");
                var path = root + "/link/../marker";
                TestHarness.CheckTrue("词法折叠目标不存在", !File.Exists(Path.Combine(root, "marker")));
                // Path.Combine/GetFullPath 会先抹去 ..；原始文本必须原样送 VM 原语。
                var (rc, text, bytes) = Resolve(dispatch, path);
                TestHarness.CheckTrue("链接先于 .. 解析成功", rc == 0, "rc=" + rc);
                TestHarness.CheckTrue(".. 返回真实父目录的 marker", text == marker,
                    "expected=" + marker + " actual=" + text);
                TestHarness.CheckTrue("真实 marker 有独立字节内容",
                    File.ReadAllBytes(marker).SequenceEqual(new byte[] { 0x72, 0x65, 0x61, 0x6c })
                    && bytes == Encoding.UTF8.GetByteCount(marker));
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(root, recursive: true);
            }
        }

        private static void TestBrokenAndLoopLinks()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("errors");
            try
            {
                var broken = Path.Combine(root, "broken");
                var loop = Path.Combine(root, "loop");
                try
                {
                    File.CreateSymbolicLink(broken,
                        Path.Combine(root, "missing-target"));
                    File.CreateSymbolicLink(loop, loop);
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException or NotSupportedException)
                {
                    Console.WriteLine("  UNSUPPORTED 断链/循环符号链接 fixture："
                        + ex.Message);
                    return;
                }
                var (brokenRc, _, _) = Resolve(dispatch, broken);
                TestHarness.CheckTrue("断链归 NotFound", brokenRc == -2,
                    "rc=" + brokenRc);
                var (loopRc, _, _) = Resolve(dispatch, loop);
                TestHarness.CheckTrue("循环不伪装 NotFound", loopRc < 0
                    && loopRc != -2, "rc=" + loopRc);
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
