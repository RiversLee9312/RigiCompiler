using System;
using System.Collections.Generic;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// NativeE2E suite-args 选择语义的解析测试（纯函数断言，不启动任何
    /// 编译/运行）。锁定三选一判定：list / 数字 from-to 区间 / 按 Label
    /// 子串过滤，以及数字开头参数保持原区间转发语义（含不完整区间的
    /// 用法报错路径不被按名分支吞掉）。
    /// </summary>
    public static class NativeE2EArgsParseTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("NativeE2E suite-args 解析");

            var list = (IReadOnlyList<string>)new string[] { "list" };
            var parsedList = NativeE2ETests.ParseRunArgs(list);
            TestHarness.CheckTrue("list 判定",
                parsedList.Kind == NativeE2ETests.NativeE2eRunKind.List);

            var range = (IReadOnlyList<string>)new string[] { "5", "7" };
            var parsedRange = NativeE2ETests.ParseRunArgs(range);
            TestHarness.CheckTrue("区间判定",
                parsedRange.Kind == NativeE2ETests.NativeE2eRunKind.Range
                && parsedRange.From == 5 && parsedRange.To == 7);

            // 单个数字：不完整区间仍走区间通道（由 runner 打印用法并报
            // 非 0，与历史行为一致），不被按名分支吞掉。
            var single = (IReadOnlyList<string>)new string[] { "5" };
            var parsedSingle = NativeE2ETests.ParseRunArgs(single);
            TestHarness.CheckTrue("单数字区间转发",
                parsedSingle.Kind == NativeE2ETests.NativeE2eRunKind.Range
                && parsedSingle.From == 5 && parsedSingle.To == 5);

            // 首参数字、次参非数字：同样保持区间转发（不吞为按名）。
            var mixed = (IReadOnlyList<string>)new string[] { "5", "abc" };
            var parsedMixed = NativeE2ETests.ParseRunArgs(mixed);
            TestHarness.CheckTrue("数字开头保持区间转发",
                parsedMixed.Kind == NativeE2ETests.NativeE2eRunKind.Range);

            // 空参数：保持原样转发（runner 打印用法并报非 0）。
            var empty = (IReadOnlyList<string>)Array.Empty<string>();
            var parsedEmpty = NativeE2ETests.ParseRunArgs(empty);
            TestHarness.CheckTrue("空参数区间转发",
                parsedEmpty.Kind == NativeE2ETests.NativeE2eRunKind.Range);

            var filter = (IReadOnlyList<string>)new string[] { "Parcel", "json-顶层" };
            var parsedFilter = NativeE2ETests.ParseRunArgs(filter);
            TestHarness.CheckTrue("按名判定与过滤器保留",
                parsedFilter.Kind == NativeE2ETests.NativeE2eRunKind.NameFilter
                && parsedFilter.Filters.Count == 2);

            return TestHarness.Summary("NativeE2EArgs");
        }
    }
}
