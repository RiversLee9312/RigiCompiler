using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>fs_stat/fs_lstat 的真实 birth、不可得哨兵及纳秒精度。</summary>
    public static class VmFsBirthTimeTests
    {


        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static TestSuiteData Spec => new(
            "VmFsBirthTime", Cases, sectionTitle: "VmFsBirthTime");
        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestRegularBirthAndPrecision", TestRegularBirthAndPrecision),
            ("TestLinuxLinkAndProcNoBirth", TestLinuxLinkAndProcNoBirth),
        };

        private static VmDispatch NewDispatch()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 { return 0 }\n");
            CaseAssertions.CheckTrue("编译 fixture 无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var context = new VmContext(module);
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            return context.Dispatch;
        }

        private static (int Rc, int Kind, long MtimeMs, int MtimeNs,
            long BirthMs, int BirthNs) Query(VmDispatch vm, string path,
                bool follow = true)
        {
            var output = new VmSpan(".u8", 48, new VmU8(0), false);
            var args = new VmValue[] { new VmString(path), output };
            var rc = ((VmI32)(follow ? vm.FsStat(args) : vm.FsLstat(args))).Value;
            var bytes = Enumerable.Range(0, 48).Select(i =>
                ((VmU8)output.Elements[i]).Value).ToArray();
            return (rc, BitConverter.ToInt32(bytes, 0),
                BitConverter.ToInt64(bytes, 12), BitConverter.ToInt32(bytes, 20),
                BitConverter.ToInt64(bytes, 36), BitConverter.ToInt32(bytes, 44));
        }

        private static void Check(string label, bool ok, string detail) =>
            CaseAssertions.CheckTrue(label, ok, detail);

        private static void TestRegularBirthAndPrecision()
        {
            var vm = NewDispatch();
            var root = Path.Combine(Path.GetTempPath(),
                "rigi_birth_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "birth.bin");
                File.WriteAllBytes(path, new byte[] { 42 });
                // 时间明显早于新建文件；保留 100ns 粒度的亚毫秒量。
                var chosen = DateTime.UnixEpoch.AddTicks(
                    1_700_000_000L * TimeSpan.TicksPerSecond + 1_234_567);
                File.SetLastWriteTimeUtc(path, chosen);
                var result = Query(vm, path);
                Check("普通文件主体成功", result.Rc == 0 && result.Kind == 0,
                    $"rc={result.Rc} kind={result.Kind}");
                if (OperatingSystem.IsLinux()
                    && RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    var expected = StatxBirth(path, false);
                    Check("ext4 fixture 有真实 birth", expected.HasValue,
                        "statx 未返回 STATX_BTIME；请使用 ext4 /tmp 或 /root");
                    if (!expected.HasValue) { return; }
                    var (ms, ns) = expected.Value;
                    Check("VM birth 毫秒/纳秒与独立 statx 一致",
                        result.BirthMs == ms && result.BirthNs == ns,
                        $"vm={result.BirthMs}/{result.BirthNs} statx={ms}/{ns}");
                    Check("birth 与人为设定 mtime 不同",
                        result.BirthMs != result.MtimeMs,
                        $"birth={result.BirthMs} mtime={result.MtimeMs}");
                    Check("mtime 原始 100ns 余量",
                        result.MtimeMs == 1_700_000_000_123L
                        && result.MtimeNs == 456_700,
                        $"actual={result.MtimeMs}/{result.MtimeNs}");
                    Console.WriteLine($"  Linux statx birth={ms}/{ns} mtime="
                        + $"{result.MtimeMs}/{result.MtimeNs}");
                }
                else if (OperatingSystem.IsWindows())
                {
                    Check("Windows birth 非空", result.BirthMs != long.MinValue,
                        $"birth={result.BirthMs}/{result.BirthNs}");
                    Check("Windows mtime 可得粒度与 FILETIME 一致",
                        result.MtimeMs == 1_700_000_000_123L
                        && result.MtimeNs == 456_700,
                        $"mtime={result.MtimeMs}/{result.MtimeNs}");
                }
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static void TestLinuxLinkAndProcNoBirth()
        {
            if (!OperatingSystem.IsLinux()
                || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                CaseAssertions.RecordSkip("  SKIP Linux-only link/proc birth fixture");
                return;
            }
            var vm = NewDispatch();
            var root = Path.Combine(Path.GetTempPath(),
                "rigi_birth_link_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var target = Path.Combine(root, "target");
                File.WriteAllText(target, "target");
                var link = Path.Combine(root, "alias");
                File.CreateSymbolicLink(link, target);
                var own = Query(vm, link, follow: false);
                var followed = Query(vm, link);
                var wantOwn = StatxBirth(link, noFollow: true);
                var wantTarget = StatxBirth(target, noFollow: false);
                Check("链接与目标 statx birth 均可取得",
                    wantOwn.HasValue && wantTarget.HasValue,
                    $"link={wantOwn} target={wantTarget}");
                if (!wantOwn.HasValue || !wantTarget.HasValue) { return; }
                Check("lstat 不跟随末段链接，逐纳秒对拍",
                    own.Rc == 0 && own.Kind == 2
                    && own.BirthMs == wantOwn.Value.Ms
                    && own.BirthNs == wantOwn.Value.Ns,
                    $"rc={own.Rc} kind={own.Kind} birth={own.BirthMs}/{own.BirthNs}");
                Check("stat 跟随末段链接，逐纳秒对拍",
                    followed.Rc == 0 && followed.Kind == 0
                    && followed.BirthMs == wantTarget.Value.Ms
                    && followed.BirthNs == wantTarget.Value.Ns,
                    $"rc={followed.Rc} kind={followed.Kind} birth="
                    + $"{followed.BirthMs}/{followed.BirthNs}");
                var relative = Path.GetRelativePath(Environment.CurrentDirectory,
                    link);
                var relativeOwn = Query(vm, relative, follow: false);
                Check("相对路径同一 cwd 基准、不跟随末段",
                    relativeOwn.Rc == 0 && relativeOwn.Kind == 2
                    && relativeOwn.BirthMs == wantOwn.Value.Ms
                    && relativeOwn.BirthNs == wantOwn.Value.Ns,
                    $"relative={relative} rc={relativeOwn.Rc} birth="
                    + $"{relativeOwn.BirthMs}/{relativeOwn.BirthNs}");
                var proc = Query(vm, "/proc/self/stat");
                var procStatx = StatxBirth("/proc/self/stat", false);
                Check("procfs fixture 无 STATX_BTIME", !procStatx.HasValue,
                    $"unexpected birth={procStatx}");
                Check("procfs 主体成功但 birth=null 哨兵",
                    proc.Rc == 0 && proc.Kind == 0
                    && proc.BirthMs == long.MinValue && proc.BirthNs == 0,
                    $"rc={proc.Rc} kind={proc.Kind} birth="
                    + $"{proc.BirthMs}/{proc.BirthNs}");
                // 断链：lstat 主体仍成功，stat 跟随时必须保留主体错误。
                File.Delete(target);
                var dangling = Query(vm, link, follow: false);
                var missing = Query(vm, link);
                Check("断链 lstat 主体成功", dangling.Rc == 0
                    && dangling.Kind == 2, $"rc={dangling.Rc}");
                Check("断链 stat 返回 NotFound", missing.Rc == -2,
                    $"rc={missing.Rc}");
            }
            finally
            {
                File.Delete(Path.Combine(root, "alias"));
                Directory.Delete(root, recursive: true);
            }
        }

        // 独立 statx 期望：测试侧裸 256B 缓冲，避免复用生产镜像与转换。
        private static (long Ms, int Ns)? StatxBirth(string path, bool noFollow)
        {
            var ptr = Marshal.AllocHGlobal(256);
            try
            {
                if (statx(-100, path, noFollow ? 0x100 : 0, 0x800, ptr) != 0
                    || (Marshal.ReadInt32(ptr) & 0x800) == 0) { return null; }
                var sec = Marshal.ReadInt64(ptr, 80);
                var nanos = (uint)Marshal.ReadInt32(ptr, 88);
                Check("statx 纳秒字段有效", nanos < 1_000_000_000,
                    $"nanos={nanos}");
                return (checked(sec * 1000 + nanos / 1_000_000),
                    (int)(nanos % 1_000_000));
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        [DllImport("libc.so.6", EntryPoint = "statx", SetLastError = true)]
        private static extern int statx(int dirfd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags, uint mask, IntPtr buffer);
    }
}
