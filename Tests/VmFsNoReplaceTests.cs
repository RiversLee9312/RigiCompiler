using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// VM fs move(NoReplace) 系统保证机制测试（STDLIB §4.5.7）：直接驱动
    /// VmDispatch.FsRename 原语。契约要点：NoReplace 必须由系统操作保证
    ///（Linux renameat2(RENAME_NOREPLACE) / Windows MoveFileEx 不带
    /// REPLACE_EXISTING），不能用 exists + rename 模拟；支持文件、目录、
    /// 链接条目（不跟随末段链接）；跨文件系统报 CrossDevice 不退化复制
    /// 删除。用例覆盖：已存在文件/空目录/断链目标 → AlreadyExists 且两
    /// 侧条目未动；文件/目录/真实链接/断链源成功移动且条目身份保留；内
    /// 核级并发争用（双线程同目标）恰好一个成功、目标不被覆盖；跨文件系
    /// 统无目标复制、源未删。链接 fixture 由测试侧 File.CreateSymbolicLink
    /// 提供（无权限平台受控跳过对应用例），不需要 Rigi 链接创建公开 API。
    /// 归一码断言同 VmDispatch.MapFsError 表（17=AlreadyExists、18=
    /// CrossDevice、38=Unsupported），与 rigi_rt fs.c 同表。
    /// </summary>
    public static class VmFsNoReplaceTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Spec.Cases.Select((entry, index) => new TestInventory.Case(index, entry.Label));

        internal static ParallelSuiteRunner.SuiteSpec Spec => new(
            "VmFsNoReplace", Cases, sectionTitle: "VmFsNoReplace");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestNoReplaceExistingFile", TestNoReplaceExistingFile),
            ("TestNoReplaceExistingDir", TestNoReplaceExistingDir),
            ("TestNoReplaceExistingBrokenLink", TestNoReplaceExistingBrokenLink),
            ("TestMoveSuccessFile", TestMoveSuccessFile),
            ("TestMoveSuccessDir", TestMoveSuccessDir),
            ("TestMoveLinkEntries", TestMoveLinkEntries),
            ("TestConcurrentSameTarget", TestConcurrentSameTarget),
            ("TestCrossDeviceNoFallback", TestCrossDeviceNoFallback),
            ("TestReplaceFileOverwrite", TestReplaceFileOverwrite),
            ("TestReplaceDirExistingTarget", TestReplaceDirExistingTarget),
            ("TestReplaceLinkEntries", TestReplaceLinkEntries),
            ("TestReplaceDirTargetRace", TestReplaceDirTargetRace),
        };

        // 最小宿主模块（FsRename 不依赖模块内容，样板同 VmPrimitiveTests）
        private const string ModuleSource =
            "pub func main(): i32 { return 0 }\n";

        private static VmDispatch NewDispatch()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(ModuleSource);
            TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var context = new VmContext(module);
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            return context.Dispatch;
        }

        // fs_rename 原语直调：返回归一码（0 成功；< 0 = -归一码）
        private static int Rename(VmDispatch dispatch, string src, string dst,
            int replace)
        {
            var rc = dispatch.FsRename(new VmValue[]
            {
                new VmString(src), new VmString(dst), new VmI32(replace),
            });
            return ((VmI32)rc).Value;
        }

        private static string NewRoot(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "rigi_nrp_" + tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void WriteFile(string path, string content) =>
            File.WriteAllText(path, content);

        private static bool ContentIs(string path, string content) =>
            File.Exists(path) && File.ReadAllText(path) == content;

        // 符号链接 fixture（§4.5.3 链接创建入口后置，测试侧自带）：返回
        // 是否成功——Windows 无开发者模式/管理员权限时失败，用例受控跳过
        private static bool TryCreateSymbolicLink(string link, string target)
        {
            try
            {
                File.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException or PlatformNotSupportedException)
            {
                TestHarness.RecordSkip("  （符号链接不可用，受控跳过："
                    + ex.GetType().Name + "）");
                return false;
            }
        }

        private static bool IsWindows => OperatingSystem.IsWindows();
        private static bool IsLinux => OperatingSystem.IsLinux();

        // ===== 已存在文件目标：AlreadyExists 且两侧内容未动 =====
        private static void TestNoReplaceExistingFile()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("file");
            try
            {
                var src = Path.Combine(root, "src.bin");
                var dst = Path.Combine(root, "dst.bin");
                WriteFile(src, "SRC-CONTENT");
                WriteFile(dst, "DST-OLD");
                var rc = Rename(dispatch, src, dst, replace: 0);
                TestHarness.CheckTrue("已存在文件目标报 AlreadyExists(-17)",
                    rc == -17, "rc=" + rc);
                TestHarness.CheckTrue("源内容未动",
                    ContentIs(src, "SRC-CONTENT"));
                TestHarness.CheckTrue("目标内容未动",
                    ContentIs(dst, "DST-OLD"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 已存在目录目标：AlreadyExists 且两目录条目未动（目录
        // 不允许覆盖/合并，§4.5.7）。
        // 强判别例在前：目标为「空目录」——POSIX rename 对「目录→已存
        // 在空目录」会静默替换成功，NoReplace 必须由系统 flag 拦下报
        // AlreadyExists；旧 copy+delete 或普通覆盖 rename 在此形态下
        // 也会「成功」逃过断言，故空目标是最强的判别面。
        // 补充例：目标为非空目录（覆盖 rename 也应失败，断言条目未动）
        private static void TestNoReplaceExistingDir()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("dir");
            try
            {
                // ① 空目录目标（强判别：非空目标普通 rename 也拦得住，
                //    空目标只有系统不替换 flag 拦得住）
                var srcDir = Path.Combine(root, "srcdir");
                var emptyDst = Path.Combine(root, "emptydst");
                Directory.CreateDirectory(srcDir);
                Directory.CreateDirectory(emptyDst);
                WriteFile(Path.Combine(srcDir, "inner.txt"), "IN");
                var rc = Rename(dispatch, srcDir, emptyDst, replace: 0);
                TestHarness.CheckTrue("已存在空目录目标报 AlreadyExists(-17)"
                    + "（POSIX rename 可覆盖空目录，必须系统 flag 拦截）",
                    rc == -17, "rc=" + rc);
                TestHarness.CheckTrue("空目标仍存在且仍为空",
                    Directory.Exists(emptyDst)
                        && Directory.GetFileSystemEntries(emptyDst).Length == 0);
                TestHarness.CheckTrue("源目录与条目未动",
                    ContentIs(Path.Combine(srcDir, "inner.txt"), "IN"));

                // ② 非空目录目标（补充例）
                var srcDir2 = Path.Combine(root, "srcdir2");
                var nonEmptyDst = Path.Combine(root, "nonemptydst");
                Directory.CreateDirectory(srcDir2);
                Directory.CreateDirectory(nonEmptyDst);
                WriteFile(Path.Combine(srcDir2, "inner2.txt"), "IN2");
                WriteFile(Path.Combine(nonEmptyDst, "victim.txt"), "OLD");
                rc = Rename(dispatch, srcDir2, nonEmptyDst, replace: 0);
                TestHarness.CheckTrue("已存在非空目录目标报 AlreadyExists(-17)",
                    rc == -17, "rc=" + rc);
                TestHarness.CheckTrue("非空形态源目录未动",
                    ContentIs(Path.Combine(srcDir2, "inner2.txt"), "IN2"));
                TestHarness.CheckTrue("非空形态目标条目未动",
                    ContentIs(Path.Combine(nonEmptyDst, "victim.txt"), "OLD"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 已存在断链目标：AlreadyExists（任何已有条目含断链）且
        // 断链条目未动；Linux 走 renameat2 EEXIST，Windows 走 MoveFileEx
        // 不替换失败同码 =====
        private static void TestNoReplaceExistingBrokenLink()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("brk");
            try
            {
                var src = Path.Combine(root, "src.bin");
                var link = Path.Combine(root, "link");
                WriteFile(src, "SRC-CONTENT");
                if (!TryCreateSymbolicLink(link,
                        Path.Combine(root, "gone_target")))
                {
                    return; // 受控跳过（无链接权限平台）
                }
                var rc = Rename(dispatch, src, link, replace: 0);
                TestHarness.CheckTrue("已存在断链目标报 AlreadyExists(-17)",
                    rc == -17, "rc=" + rc);
                TestHarness.CheckTrue("源内容未动",
                    ContentIs(src, "SRC-CONTENT"));
                TestHarness.CheckTrue("断链条目未动（LinkTarget 保留）",
                    new FileInfo(link).LinkTarget
                        == Path.Combine(root, "gone_target"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 文件移动成功：源消失、内容一致 =====
        private static void TestMoveSuccessFile()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("mvf");
            try
            {
                var src = Path.Combine(root, "a.bin");
                var dst = Path.Combine(root, "b.bin");
                WriteFile(src, "PAYLOAD");
                var rc = Rename(dispatch, src, dst, replace: 0);
                TestHarness.CheckTrue("文件 NoReplace 移动成功", rc == 0,
                    "rc=" + rc);
                TestHarness.CheckTrue("源条目消失", !File.Exists(src));
                TestHarness.CheckTrue("目标内容一致",
                    ContentIs(dst, "PAYLOAD"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 目录移动成功：源消失、条目随迁 =====
        private static void TestMoveSuccessDir()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("mvd");
            try
            {
                var srcDir = Path.Combine(root, "srcdir");
                var dstDir = Path.Combine(root, "dstdir");
                Directory.CreateDirectory(srcDir);
                WriteFile(Path.Combine(srcDir, "inner.txt"), "IN-DIR");
                var rc = Rename(dispatch, srcDir, dstDir, replace: 0);
                TestHarness.CheckTrue("目录 NoReplace 移动成功", rc == 0,
                    "rc=" + rc);
                TestHarness.CheckTrue("源目录消失", !Directory.Exists(srcDir));
                TestHarness.CheckTrue("条目随迁",
                    ContentIs(Path.Combine(dstDir, "inner.txt"), "IN-DIR"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 链接条目移动（不跟随末段）：真实链接移动后仍指向原目
        // 标、目标文件内容不动；断链源也可移动且仍为断链 =====
        private static void TestMoveLinkEntries()
        {
            if (!IsLinux && IsWindows)
            {
                // Windows 同样支持链接条目移动（MoveFileEx 对重解析点操
                // 作条目本身），但无权限环境受控跳过——fixture 决定
            }
            var dispatch = NewDispatch();
            var root = NewRoot("lnk");
            try
            {
                // ① 真实链接：移动条目本身
                var target = Path.Combine(root, "target.txt");
                var link = Path.Combine(root, "alias");
                var moved = Path.Combine(root, "alias2");
                WriteFile(target, "TARGET-DATA");
                if (!TryCreateSymbolicLink(link, target))
                {
                    return; // 受控跳过
                }
                var rc = Rename(dispatch, link, moved, replace: 0);
                TestHarness.CheckTrue("链接条目移动成功", rc == 0, "rc=" + rc);
                TestHarness.CheckTrue("旧链接条目消失",
                    new FileInfo(link).LinkTarget == null);
                TestHarness.CheckTrue("移动后仍指向原目标（不跟随）",
                    new FileInfo(moved).LinkTarget == target);
                TestHarness.CheckTrue("链接目标文件内容未动",
                    ContentIs(target, "TARGET-DATA"));

                // ② 断链源：也可以移动（rename 系不跟随末段链接）
                var broken = Path.Combine(root, "broken");
                var brokenMoved = Path.Combine(root, "broken2");
                var gone = Path.Combine(root, "gone2");
                if (!TryCreateSymbolicLink(broken, gone))
                {
                    return; // 受控跳过
                }
                rc = Rename(dispatch, broken, brokenMoved, replace: 0);
                TestHarness.CheckTrue("断链源移动成功", rc == 0, "rc=" + rc);
                TestHarness.CheckTrue("断链移动后仍断链（目标保留）",
                    new FileInfo(brokenMoved).LinkTarget == gone);
                TestHarness.CheckTrue("缺失目标仍未被创建", !File.Exists(gone));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 内核级并发争用：双线程同目标，恰好一个成功、目标内容 =
        // 胜者源内容、败者源仍在。
        // 同步口径：Barrier 把两 worker 收到同一起跑点后同时释放（无屏
        // 障时按 Start 顺序大概率全串行，压不出窗口）。如实声明：起跑
        // 对齐不保证两次 rename 绝对重叠——本用例验证的是系统原语裁决
        // 下「恰一成一败、目标不被覆盖、败者源保留」的结果正确性，不
        // 证明实现自身原子性。worker 异常一律收集回父断言（不逃逸杀宿
        // 主）；Join 带超时防挂死，超时即 FAIL 并放弃后续轮次（worker
        // IsBackground，不阻塞进程退出）；8 轮不加长压力
        private static void TestConcurrentSameTarget()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("race");
            try
            {
                // 每轮新名字避免轮间残留；奇偶轮交换 A/B 先后消除「A 恒
                // 先启动」的隐含断言
                for (var round = 0; round < 8; round++)
                {
                    var aFirst = round % 2 == 0;
                    var srcA = Path.Combine(root, "srcA_" + round);
                    var srcB = Path.Combine(root, "srcB_" + round);
                    var dst = Path.Combine(root, "dst_" + round);
                    WriteFile(srcA, "WINNER-A");
                    WriteFile(srcB, "WINNER-B");
                    var first = aFirst ? srcA : srcB;
                    var second = aFirst ? srcB : srcA;
                    var firstContent = aFirst ? "WINNER-A" : "WINNER-B";
                    var secondContent = aFirst ? "WINNER-B" : "WINNER-A";
                    var rcFirst = int.MinValue;
                    var rcSecond = int.MinValue;
                    var errors = new List<string>();
                    void RecordWorker(string tag, Action body)
                    {
                        try
                        {
                            body();
                        }
                        catch (Exception ex)
                        {
                            lock (errors)
                            {
                                errors.Add(tag + "：" + ex.GetType().Name
                                    + " " + ex.Message);
                            }
                        }
                    }
                    var gate = new Barrier(participantCount: 2);
                    var t1 = new Thread(() => RecordWorker("first", () =>
                    {
                        gate.SignalAndWait(
                            TimeSpan.FromSeconds(30));
                        rcFirst = Rename(dispatch, first, dst, 0);
                    }));
                    var t2 = new Thread(() => RecordWorker("second", () =>
                    {
                        gate.SignalAndWait(
                            TimeSpan.FromSeconds(30));
                        rcSecond = Rename(dispatch, second, dst, 0);
                    }));
                    t1.IsBackground = true;
                    t2.IsBackground = true;
                    t1.Start();
                    t2.Start();
                    // Join 带超时：worker 挂死时 FAIL 收场，不无限等
                    var done1 = t1.Join(TimeSpan.FromSeconds(30));
                    var done2 = t2.Join(TimeSpan.FromSeconds(30));
                    if (!done1 || !done2)
                    {
                        TestHarness.CheckTrue(
                            $"争用轮 {round}：双 worker 30s 内退出（不无限等）",
                            false, "joined=" + done1 + "," + done2);
                        return;
                    }
                    TestHarness.CheckTrue(
                        $"争用轮 {round}：worker 无异常逃逸",
                        errors.Count == 0,
                        string.Join("; ", errors));
                    var pair = new[] { rcFirst, rcSecond };
                    TestHarness.CheckTrue(
                        $"争用轮 {round}：恰好一个成功一个 AlreadyExists",
                        pair.Count(rc => rc == 0) == 1
                            && pair.Count(rc => rc == -17) == 1,
                        "rcs=" + rcFirst + "," + rcSecond);
                    TestHarness.CheckTrue(
                        $"争用轮 {round}：目标为胜者内容未被覆盖",
                        ContentIs(dst, rcFirst == 0 ? firstContent
                            : secondContent),
                        "dst=" + File.ReadAllText(dst));
                    // 败者按实际胜负判定（起跑对齐不决定执行顺序）
                    var loser = rcFirst == 0 ? second : first;
                    var loserContent = rcFirst == 0 ? secondContent
                        : firstContent;
                    TestHarness.CheckTrue(
                        $"争用轮 {round}：败者源条目仍在",
                        ContentIs(loser, loserContent));
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // ===== 跨文件系统：CrossDevice 不退化复制删除（源未删、目标未
        // 建）。运行前用测试侧真实 stat 命令独立确认两个测试根的设备号
        //（不经被测 FsRename，不硬编内核 stat 内存布局）；只有确为同设
        // 备或 fixture 不可用才明确 SKIP（纯打印，不造通过断言）——绝
        // 不把 rc==0 当「同设备」放行：旧 copy+delete 退化跨设备成功也
        // 会走到那里。不同设备必须 rc==-18、源保留、目标未建；成功必
        // FAIL。st_dev 实证输出留日志 =====
        private static void TestCrossDeviceNoFallback()
        {
            if (!IsLinux)
            {
                TestHarness.RecordSkip("  SKIP TestCrossDeviceNoFallback：非 "
                    + "Linux 宿主（Windows 跨卷形态无独立定向，本用例不覆盖）");
                return;
            }
            const string shm = "/dev/shm";
            if (!Directory.Exists(shm))
            {
                TestHarness.RecordSkip("  SKIP TestCrossDeviceNoFallback：宿主无 "
                    + "/dev/shm，跨FS fixture 不可用，EXDEV 未测");
                return;
            }
            var dispatch = NewDispatch();
            var root = NewRoot("xdev");
            var shmSrc = Path.Combine(shm, "rigi_nrp_xdev_"
                + Guid.NewGuid().ToString("N"));
            try
            {
                WriteFile(shmSrc, "XDEV-PAYLOAD");
                string? errSrc = null;
                string? errDst = null;
                var devSrc = 0L;
                var devDst = 0L;
                var probedSrc = TryGetDeviceId(shmSrc, out devSrc,
                    out errSrc);
                var probedDst = probedSrc
                    && TryGetDeviceId(root, out devDst, out errDst);
                if (!probedSrc || !probedDst)
                {
                    TestHarness.RecordSkip("  SKIP TestCrossDeviceNoFallback：设"
                        + "备号探测不可用（" + (errSrc ?? errDst)
                        + "），EXDEV 未测");
                    return;
                }
                Console.WriteLine($"  st_dev 实证：src={devSrc} dst={devDst}"
                    + (devSrc != devDst ? "（跨设备）" : "（同设备）"));
                if (devSrc == devDst)
                {
                    // 确为同设备：EXDEV 语义本环境不可构造——明确 SKIP，
                    // 不造通过断言（也不给「成功当同设备」留口子）
                    TestHarness.RecordSkip("  SKIP TestCrossDeviceNoFallback：两"
                        + "测试根确为同设备（st_dev=" + devSrc
                        + "），EXDEV 不可构造未测");
                    return;
                }
                var dst = Path.Combine(root, "xdev_dst");
                var rc = Rename(dispatch, shmSrc, dst, replace: 0);
                // 跨设备必须 CrossDevice；rc==0 意味着发生了复制删除退化
                //（无论实现形态），必须 FAIL
                TestHarness.CheckTrue(
                    $"跨文件系统（st_dev {devSrc}≠{devDst}）必须报 "
                    + "CrossDevice(-18)，rc==0 即复制删除退化逃逸",
                    rc == -18, "rc=" + rc);
                TestHarness.CheckTrue("跨FS源未删（不复制删除）",
                    ContentIs(shmSrc, "XDEV-PAYLOAD"));
                TestHarness.CheckTrue("跨FS目标未创建（无复制）",
                    !File.Exists(dst));
                // 同一已确认跨设备 fixture 再验证 Replace：File.Move
                // (overwrite:true) 在 Unix 会复制+删除，必须由原始 rename
                // 的 EXDEV 拒绝；源及目标均仍保留原状。
                var replaceDst = Path.Combine(root, "xdev_replace_dst");
                WriteFile(replaceDst, "OLD-TARGET");
                var replaceRc = Rename(dispatch, shmSrc, replaceDst,
                    replace: 1);
                TestHarness.CheckTrue("跨FS Replace 必须报 CrossDevice(-18)",
                    replaceRc == -18, "rc=" + replaceRc);
                TestHarness.CheckTrue("跨FS Replace 源未删",
                    ContentIs(shmSrc, "XDEV-PAYLOAD"));
                TestHarness.CheckTrue("跨FS Replace 旧目标未改写",
                    ContentIs(replaceDst, "OLD-TARGET"));
            }
            finally
            {
                try
                {
                    if (File.Exists(shmSrc))
                    {
                        File.Delete(shmSrc);
                    }
                }
                catch
                {
                    // 清理尽力而为（失败不掩盖用例结果）
                }
                Directory.Delete(root, recursive: true);
            }
        }

        // 独立设备号探测：经 shell stat -c %d（coreutils 标准工具）取
        // 十进制 st_dev——测试侧真实命令输出，不经被测 FsRename，也不在
        // 托管侧硬编 struct stat 内存布局（跨架构布局有差异）。命令缺失
        // /超时/非数字输出一律返回 false 并带原因（fixture 不可用 → 上
        // 层明确 SKIP）
        private static bool TryGetDeviceId(string path, out long deviceId,
            out string? error)
        {
            deviceId = 0;
            error = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "stat",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("%d");
                psi.ArgumentList.Add(path);
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    error = "stat 进程未启动";
                    return false;
                }
                var output = proc.StandardOutput.ReadToEnd().Trim();
                var errText = proc.StandardError.ReadToEnd().Trim();
                if (!proc.WaitForExit(10_000))
                {
                    error = "stat 10s 未退出";
                    return false;
                }
                if (proc.ExitCode != 0)
                {
                    error = "stat 退出码 " + proc.ExitCode + " " + errText;
                    return false;
                }
                if (!long.TryParse(output, out deviceId))
                {
                    error = "stat 输出非数字：" + output;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + " " + ex.Message;
                return false;
            }
        }

        // ===== Replace 文件覆盖：源消失，目标内容被替换 =====
        private static void TestReplaceFileOverwrite()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("rep");
            try
            {
                var src = Path.Combine(root, "src.bin");
                var dst = Path.Combine(root, "dst.bin");
                WriteFile(src, "NEW-CONTENT");
                WriteFile(dst, "OLD-CONTENT");
                var rc = Rename(dispatch, src, dst, replace: 1);
                TestHarness.CheckTrue("Replace 覆盖文件成功", rc == 0,
                    "rc=" + rc);
                TestHarness.CheckTrue("Replace 源条目消失", !File.Exists(src));
                TestHarness.CheckTrue("Replace 目标内容被替换",
                    ContentIs(dst, "NEW-CONTENT"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // 稳定目录源 Replace：已有空目录不能被 POSIX rename 替换；
        // 已有文件和断链也必须留在原位置（区别目录目标错误分类）。
        private static void TestReplaceDirExistingTarget()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("repdir");
            try
            {
                foreach (var kind in new[] { "empty", "file", "broken" })
                {
                    var src = Path.Combine(root, "src_" + kind);
                    var dst = Path.Combine(root, "dst_" + kind);
                    Directory.CreateDirectory(src);
                    WriteFile(Path.Combine(src, "inner"), "SOURCE");
                    if (kind == "empty")
                    {
                        Directory.CreateDirectory(dst);
                    }
                    else if (kind == "file")
                    {
                        WriteFile(dst, "OLD");
                    }
                    else if (!TryCreateSymbolicLink(dst,
                        Path.Combine(root, "missing_" + kind)))
                    {
                        continue;
                    }
                    var rc = Rename(dispatch, src, dst, replace: 1);
                    var expected = kind == "empty" && IsLinux ? -21 : -17;
                    TestHarness.CheckTrue("目录 Replace 已存目标 " + kind,
                        rc == expected, "rc=" + rc);
                    TestHarness.CheckTrue("目录源内容未动 " + kind,
                        ContentIs(Path.Combine(src, "inner"), "SOURCE"));
                    TestHarness.CheckTrue("旧目标未动 " + kind,
                        kind == "empty" ? Directory.Exists(dst)
                            && Directory.GetFileSystemEntries(dst).Length == 0
                            : kind == "file" ? ContentIs(dst, "OLD")
                            : new FileInfo(dst).LinkTarget
                                == Path.Combine(root, "missing_" + kind));
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static void TestReplaceLinkEntries()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("replink");
            try
            {
                var src = Path.Combine(root, "source_link");
                var dst = Path.Combine(root, "old_link");
                var target = Path.Combine(root, "missing_new");
                if (!TryCreateSymbolicLink(src, target)
                    || !TryCreateSymbolicLink(dst,
                        Path.Combine(root, "missing_old")))
                {
                    return;
                }
                var rc = Rename(dispatch, src, dst, replace: 1);
                TestHarness.CheckTrue("Replace 链接条目覆盖成功", rc == 0,
                    "rc=" + rc);
                TestHarness.CheckTrue("Replace 仅移动链接不跟随末段",
                    new FileInfo(dst).LinkTarget == target
                        && new FileInfo(src).LinkTarget == null);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // 两线程经 Barrier 同时争用同一缺失目标：若均进入旧的
        // lstat(dst)+rename 窗口，旧实现可能覆盖先到的空目录。
        // 调度不保证每轮命中该窗口，故仅断言观察到的成功不会覆盖
        // 目录（不以碰运气命中旧 bug 作为通过条件）。
        private static void TestReplaceDirTargetRace()
        {
            var dispatch = NewDispatch();
            var root = NewRoot("reprace");
            try
            {
                for (var round = 0; round < 12; round++)
                {
                    var src = Path.Combine(root, "src_" + round);
                    var dst = Path.Combine(root, "dst_" + round);
                    Directory.CreateDirectory(src);
                    WriteFile(Path.Combine(src, "inner"), "SOURCE");
                    var gate = new Barrier(2);
                    var rc = int.MinValue;
                    Exception? moveError = null;
                    Exception? createError = null;
                    var mover = new Thread(() =>
                    {
                        try
                        {
                            gate.SignalAndWait(TimeSpan.FromSeconds(30));
                            rc = Rename(dispatch, src, dst, replace: 1);
                        }
                        catch (Exception ex) { moveError = ex; }
                    });
                    var creator = new Thread(() =>
                    {
                        try
                        {
                            gate.SignalAndWait(TimeSpan.FromSeconds(30));
                            Directory.CreateDirectory(dst);
                        }
                        catch (Exception ex) { createError = ex; }
                    });
                    mover.IsBackground = creator.IsBackground = true;
                    mover.Start();
                    creator.Start();
                    var done = mover.Join(TimeSpan.FromSeconds(30));
                    done &= creator.Join(TimeSpan.FromSeconds(30));
                    TestHarness.CheckTrue("Replace 目录争用线程完成 " + round,
                        done && moveError == null && createError == null,
                        $"done={done} move={moveError} create={createError}");
                    if (!done) { return; }
                    TestHarness.CheckTrue("Replace 目录争用结果 " + round,
                        rc == 0 || rc == -21 || (!IsLinux && rc == -17),
                        "rc=" + rc);
                    if (rc == -21 || rc == -17)
                    {
                        TestHarness.CheckTrue("Replace 失败保留目录源 " + round,
                            ContentIs(Path.Combine(src, "inner"), "SOURCE"));
                    }
                    TestHarness.CheckTrue("Replace 目标目录存在 " + round,
                        Directory.Exists(dst));
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
