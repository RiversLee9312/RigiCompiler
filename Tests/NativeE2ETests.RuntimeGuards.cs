using System.IO;
using System.Runtime.CompilerServices;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        private const string GcDebtSource = """
            @NativeLibrary("gc_debt_test")
            @NativeSymbol("gc_debt_test")
            priv native func probe(): i32
            pub func main(): i32 {
                const result = probe()
                if (result != 0) { return result }
                core.io.Console.println("gc-debt-ok")
                return 0
            }
            """;

        private static string GcDebtC([CallerFilePath] string path = "")
        {
            var header = TestCorpusPaths.Resolve("rigi_rt/macrogc.h", path).Replace('\\', '/');
            // 只测试账本计量：region 内放入虚拟的大体积候选，离开前全部摘除。
            // collector 永远不会扫描这些栈对象，不必实际分配数 GiB 才测到溢出。
            return "#include \"" + header + "\"\n" + """
                extern int64_t rigi_gc_debt_bytes(void);
                int32_t gc_debt_test(void) {
                    static const uint16_t refs[] = { 0 };
                    RigiTypeSheet sheet = { 0 };
                    int bad = 0;
                    sheet.typeSize = UINT32_C(1) << 30;
                    sheet.refMapSize = 1;
                    sheet.refMap = refs;
                    for (int round = 0; round < 512; ++round) {
                        RigiObjectHeader objects[4] = { 0 };
                        rigi_region_enter();
                        int64_t baseline = rigi_gc_debt_bytes();
                        for (int i = 0; i < 4; ++i) {
                            objects[i].typeId = &sheet;
                            objects[i].rc = 2;
                            rigi_gc_note_release(&objects[i], &sheet);
                        }
                        rigi_gc_note_release(&objects[0], &sheet);
                        bad |= rigi_gc_debt_bytes() != baseline + (INT64_C(4) << 30);
                        /* 非尾元素摘除会 swap-remove，随后各对象索引仍须正确。 */
                        const int order[] = { 1, 3, 0, 2 };
                        for (int i = 0; i < 4; ++i) {
                            rigi_gc_forget_candidate(&objects[order[i]]);
                            bad |= rigi_gc_debt_bytes() != baseline + ((int64_t)(3 - i) << 30);
                        }
                        rigi_region_exit();
                    }
                    return bad;
                }
                """;
        }
    }
}
