using System;
using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// stdlib 内嵌源载入测试（M43，Semantic/StdlibSources.cs）：
    /// 断言 stdlib/**/*.rg 确实经 EmbeddedResource 进入程序集，
    /// 且被当前 Lexer+Parser 完整接受。
    ///
    /// 覆盖（S10 起六源：.bootstrap.rg / core/Console.rg /
    /// core/collections.rg / core/coroutine.rg / core/disposable.rg /
    /// core/exceptions.rg，按逻辑名 Ordinal 排序）：
    /// 1. ParseAll() 返回恰好六棵 RootASTNode，Span.sourceName 为逻辑名
    ///    映射形（&lt;stdlib&gt;/ 前缀，含点开头文件名的反推）
    /// 2. 结构断言：.bootstrap 顶层 140 个声明（namespace core +
    ///    ext operator callable + core.Pair 泛型类 + ComparisonResult + lambda 对象模型
    ///    四家族 132 个 abstract class + Cell/ReadonlyCell，SYNTAX §5.2）；
    ///    Console（namespace core.io + pub class + 3 callable 成员，
    ///    native 双注解）；collections（namespace core.collections +
    ///    2 interface + 2 class + alloc_array/arrayOf/arrayOfElements +
    ///    span_alloc/spanOf/shared_span_alloc/sharedSpanOf）；
    ///    set（namespace core.collections + class Set + priv class
    ///    SetEnumerator，类头无序列化注解）；queue（namespace core.collections
    ///    + class Queue + priv class QueueEnumerator，类头无序列化注解）；
    ///    io/stream（施工块 2-1：namespace core.io + EndOfStreamException +
    ///    StreamWrapperOwnership + ISeekableStream + InputStream/OutputStream
    ///    双抽象基类，§4.4 流核心抽象；2-2 增 readAll 双重载）；
    ///    io/autobuffer（施工块 2-2：namespace core.io + AutoBuffer +
    ///    priv 输入/输出视图类，§4.4 动态扩容缓冲）；io/memory（施工块
    ///    2-2：namespace core.io + MemoryInputStream/MemoryOutputStream，
    ///    §4.4 普通内存流）；io/buffered（施工块 2-3：namespace core.io +
    ///    BufferedInputStream/BufferedOutputStream，§4.4 缓冲流包装器）；
    ///    io/stdstreams（施工块 2-4b1 标准输出/错误 + 2-4b2 标准输入：
    ///    namespace core.io + StandardStreams 三工厂 +
    ///    StdOutputStream/StdErrorStream + StdinWake + StdInputStream，
    ///    §4.4 标准流与控制台契约，借用包装对象可独立关闭）；
    ///    adapters（namespace core.collections + 2 个 pub func asEnumerable
    ///    重载 + 3 个 priv 借用枚举/可枚举类，类头无序列化注解）；
    ///    algorithms（namespace core.collections + 26 个顶层 func：原 10 通用
    ///    算法 + sorted/sortInPlace + compare 十一型重载（十标量 + String
    ///    标量字典序，块 3-3b）+ 3 个 priv 归并助手，无类型声明）；
    ///    coroutine（namespace core.coroutine + 15 类型 +
    ///    laneOfExecutor + 34 native 原语 + sleep 包装）；disposable（namespace core +
    ///    IDisposable 接口）；exceptions（namespace core + 5 异常子类）
    /// 3. Console 整棵 Root 的 AstDescribe 描述串精确比对
    /// </summary>
    public static class StdlibSourcesTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        internal static ParallelSuiteRunner.SuiteSpec Spec { get; } = new("StdlibSources",
        [
            (nameof(TestCountAndSourceName), TestCountAndSourceName),
            (nameof(TestBootstrapStructure), TestBootstrapStructure),
            (nameof(TestConsoleStructure), TestConsoleStructure),
            (nameof(TestCollectionsStructure), TestCollectionsStructure),
            (nameof(TestCoroutineStructure), TestCoroutineStructure),
            (nameof(TestDisposableStructure), TestDisposableStructure),
            (nameof(TestExceptionsStructure), TestExceptionsStructure),
            (nameof(TestFsPathStructure), TestFsPathStructure),
            (nameof(TestFsPrimitivesStructure), TestFsPrimitivesStructure),
            (nameof(TestGlobalExceptionsStructure), TestGlobalExceptionsStructure),
            (nameof(TestTimeStructure), TestTimeStructure),
            (nameof(TestMathStructure), TestMathStructure),
            (nameof(TestSerializationStructure), TestSerializationStructure),
            (nameof(TestMessagingStructure), TestMessagingStructure),
            (nameof(TestSetStructure), TestSetStructure),
            (nameof(TestQueueStructure), TestQueueStructure),
            (nameof(TestIoStreamStructure), TestIoStreamStructure),
            (nameof(TestIoStdStreamsStructure), TestIoStdStreamsStructure),
            (nameof(TestAdaptersStructure), TestAdaptersStructure),
            (nameof(TestAlgorithmsStructure), TestAlgorithmsStructure),
            (nameof(TestConsoleDescribe), TestConsoleDescribe),
        ], sectionTitle: "StdlibSources", memoryMiB: 2048);

        // ===== 1. 数量与 sourceName =====
        private static void TestCountAndSourceName()
        {
            TestHarness.Section("ParseAll: Count & SourceName");

            var roots = StdlibSources.ParseAll();
            TestHarness.CheckTrue("ParseAll 包含独立的内建声明源码",
                roots.Count == 38, $"实际 {roots.Count} 棵");
            var intrinsics = roots.Single(r => r.Span?.sourceName == "<stdlib>/.intrinsics.rg");
            TestHarness.CheckTrue("内建声明仅由受信任的载入器标记",
                intrinsics.IsCompilerLibrary && intrinsics.IsIntrinsicDeclarations);
            // 本节还核对其余资源的稳定排序；结构测试按资源名定位。
            roots = roots.Where(r => !r.IsIntrinsicDeclarations).ToArray();
            if (roots.Count < 24) { TestHarness.Blank(); return; }

            // 逻辑名 Ordinal 排序：'.'(0x2E) < 'c'；'C'(0x43) < 'a'(0x61) <
            // 'c'；adapters < algorithms（'d' < 'l'）< atomic（'d' < 't'）；
            // collections < coroutine（'l' < 'r'）；d < e < g < i（施工块
            // 2-1 core/io/stream.rg；2-2 增 core/io/ 内多文件排序：
            // autobuffer.rg < memory.rg < stream.rg，'a' < 'm' < 's'；
            // 2-3 增 core/io/buffered.rg：'a' < 'b' < 'm'；2-4b1 增
            // core/io/stdstreams.rg：'std' < 'str'（'d' < 'r'），故
            // autobuffer < buffered < memory < stdstreams < stream；
            // 3-6 增 core/io/text.rg：'t' > 's'，排 stream 后，故
            // autobuffer < buffered < memory < stdstreams < stream <
            // text）<
            // m < n < p < q < s < t；
            // serialization < set（'r' < 't'）< text（施工块 3-4 增
            // core/text/ 三文件：builder < case_data < text < utf8——
            // 'b' < 'c' < 't' < 'u'，施工块 3-2 增 core/text/text.rg、
            // 3-3a 增 core/text/case_data.rg、3-4 增 core/text/builder.rg
            // 与 core/text/utf8.rg；3-5a 增 core/text/parse.rg：'p' 在
            // 'c' 与 't' 之间，故 builder < case_data < parse < text <
            // utf8）< time；
            // 4-4 增 core/serialization/serializer.rg：与
            // core/serialization.rg 的公共前缀 "core/serialization" 之后
            // '.'(0x2E) < '/'(0x2F)，故 serializer.rg 紧随 serialization.rg
            // （index 22），其后 set/text/time 各顺延一位；
            // 5-2a 增 core/serialization/json.rg：'j'(0x6A) < 's'(0x73)，
            // 故 json.rg 排 serializer.rg 之前（index 22），serializer.rg
            // 顺延至 23，set/text/time 各顺延至 24..30；
            // 6-4 增 core/math.rg：'m'——在 io/（'i' < 'm'）之后、messaging
            // 之前（'a' < 'e'），故 math.rg 为 index 17，messaging/
            // native_rc/place/queue/serialization 族/set/text/time 各顺延
            // 一位（time 由 30 至 31）；
            // 7-1 增 core/fs/path.rg：'f'——在 exceptions（'e' < 'f'）之后、
            // global_exceptions 之前（'f' < 'g'），故 path.rg 为 index 10，
            // global_exceptions/io 族/math/messaging/native_rc/place/
            // queue/serialization 族/set/text 族/time 各顺延一位
            // （time 由 31 至 32）；
            // 7-2 增 core/fs/primitives.rg：'f'——与 core/fs/path.rg 同目录，
            // "path.rg" < "primitives.rg"（'a' < 'r'），故 primitives.rg
            // 紧随 path.rg（index 11），global_exceptions 及之后各顺延一位
            // （time 由 32 至 33）；
            // 7-3 增 core/fs/file.rg：'f'——同目录 "file.rg" < "path.rg"
            //（'f' < 'p'），故 file.rg 为 index 10、path.rg/primitives.rg
            // 各顺延至 11/12，global_exceptions 及之后各再顺延一位
            // （time 由 33 至 34）；
            // 7-4 增 core/fs/info.rg：'i'——同目录 "file.rg" < "info.rg"
            // < "path.rg"（'f' < 'i' < 'p'）< "primitives.rg"（'a' < 'r'），
            // 故 info.rg 为 index 11，path.rg/primitives.rg 各顺延至
            // 12/13，global_exceptions 及之后各再顺延一位（time 由 34
            // 至 35）；
            // 7-5 增 core/fs/directory.rg：'d'——同目录 "directory.rg" <
            // "file.rg"（'d' < 'f'），故 directory.rg 为 index 10，
            // file/info/path/primitives 各顺延至 11..14，global_exceptions
            // 及之后各再顺延一位（time 由 35 至 36）
            TestHarness.Check("sourceName[0]（点开头文件名反推）",
                roots[0].Span?.sourceName ?? "<null>", "<stdlib>/.bootstrap.rg");
            TestHarness.Check("sourceName[1]",
                roots[1].Span?.sourceName ?? "<null>", "<stdlib>/core/Console.rg");
            TestHarness.Check("sourceName[2]（施工块 1-6 借用适配器）",
                roots[2].Span?.sourceName ?? "<null>", "<stdlib>/core/adapters.rg");
            TestHarness.Check("sourceName[3]（施工块 1-7 通用集合算法）",
                roots[3].Span?.sourceName ?? "<null>", "<stdlib>/core/algorithms.rg");
            TestHarness.Check("sourceName[4]（Atomic）",
                roots[4].Span?.sourceName ?? "<null>", "<stdlib>/core/atomic.rg");
            TestHarness.Check("sourceName[5]（安全 Atomic 容器）",
                roots[5].Span?.sourceName ?? "<null>", "<stdlib>/core/atomic_collections.rg");
            TestHarness.Check("sourceName[24]（MW11d native_rc）",
                roots[24].Span?.sourceName ?? "<null>", "<stdlib>/core/native_rc.rg");
            TestHarness.Check("sourceName[25]（Place/Handle）",
                roots[25].Span?.sourceName ?? "<null>", "<stdlib>/core/place.rg");
            TestHarness.Check("sourceName[6]",
                roots[6].Span?.sourceName ?? "<null>", "<stdlib>/core/collections.rg");
            TestHarness.Check("sourceName[7]",
                roots[7].Span?.sourceName ?? "<null>", "<stdlib>/core/coroutine.rg");
            TestHarness.Check("sourceName[8]",
                roots[8].Span?.sourceName ?? "<null>", "<stdlib>/core/disposable.rg");
            TestHarness.Check("sourceName[9]",
                roots[9].Span?.sourceName ?? "<null>", "<stdlib>/core/exceptions.rg");
            TestHarness.Check("sourceName[10]（施工块 7-5 core.fs 目录读取+创建删除）",
                roots[10].Span?.sourceName ?? "<null>", "<stdlib>/core/fs/directory.rg");
            TestHarness.Check("sourceName[11]（施工块 7-3 core.fs 文件流）",
                roots[11].Span?.sourceName ?? "<null>", "<stdlib>/core/fs/file.rg");
            TestHarness.Check("sourceName[12]（施工块 7-4 core.fs 信息查询与链接）",
                roots[12].Span?.sourceName ?? "<null>", "<stdlib>/core/fs/info.rg");
            TestHarness.Check("sourceName[13]（施工块 7-1 core.fs Path 与错误骨架）",
                roots[13].Span?.sourceName ?? "<null>", "<stdlib>/core/fs/path.rg");
            TestHarness.Check("sourceName[14]（施工块 7-2 core.fs native 原语层）",
                roots[14].Span?.sourceName ?? "<null>", "<stdlib>/core/fs/primitives.rg");
            TestHarness.Check("sourceName[15]（MW12b 全局异常通道）",
                roots[15].Span?.sourceName ?? "<null>", "<stdlib>/core/global_exceptions.rg");
            TestHarness.Check("sourceName[16]（施工块 2-2 core.io AutoBuffer）",
                roots[16].Span?.sourceName ?? "<null>", "<stdlib>/core/io/autobuffer.rg");
            TestHarness.Check("sourceName[17]（施工块 2-3 core.io 缓冲流包装器）",
                roots[17].Span?.sourceName ?? "<null>", "<stdlib>/core/io/buffered.rg");
            TestHarness.Check("sourceName[18]（施工块 2-2 core.io 内存流）",
                roots[18].Span?.sourceName ?? "<null>", "<stdlib>/core/io/memory.rg");
            TestHarness.Check("sourceName[19]（施工块 2-4b1 core.io 标准流）",
                roots[19].Span?.sourceName ?? "<null>", "<stdlib>/core/io/stdstreams.rg");
            TestHarness.Check("sourceName[20]（施工块 2-1 core.io 流核心抽象）",
                roots[20].Span?.sourceName ?? "<null>", "<stdlib>/core/io/stream.rg");
            TestHarness.Check("sourceName[21]（施工块 3-6 core.io 文本流适配器）",
                roots[21].Span?.sourceName ?? "<null>", "<stdlib>/core/io/text.rg");
            TestHarness.Check("sourceName[22]（施工块 6-4 core.math）",
                roots[22].Span?.sourceName ?? "<null>", "<stdlib>/core/math.rg");
            TestHarness.Check("sourceName[23]（MW11d-C core.messaging）",
                roots[23].Span?.sourceName ?? "<null>", "<stdlib>/core/messaging.rg");
            TestHarness.Check("sourceName[26]（施工块 1-5 core.collections Queue）",
                roots[26].Span?.sourceName ?? "<null>", "<stdlib>/core/queue.rg");
            TestHarness.Check("sourceName[27]（MW11d core.serialization）",
                roots[27].Span?.sourceName ?? "<null>", "<stdlib>/core/serialization.rg");
            TestHarness.Check("sourceName[28]（施工块 5-2a core.serialization.json JsonSerializer 写侧）",
                roots[28].Span?.sourceName ?? "<null>", "<stdlib>/core/serialization/json.rg");
            TestHarness.Check("sourceName[29]（施工块 4-4 core.serialization Serializer 基类）",
                roots[29].Span?.sourceName ?? "<null>", "<stdlib>/core/serialization/serializer.rg");
            TestHarness.Check("sourceName[30]（施工块 1-4 core.collections Set）",
                roots[30].Span?.sourceName ?? "<null>", "<stdlib>/core/set.rg");
            TestHarness.Check("sourceName[31]（施工块 3-4 core.text StringBuilder）",
                roots[31].Span?.sourceName ?? "<null>", "<stdlib>/core/text/builder.rg");
            TestHarness.Check("sourceName[32]（施工块 3-3a core.text 大小写映射数据表）",
                roots[32].Span?.sourceName ?? "<null>", "<stdlib>/core/text/case_data.rg");
            TestHarness.Check("sourceName[33]（施工块 3-5a/3-5b core.text 整数+浮点解析）",
                roots[33].Span?.sourceName ?? "<null>", "<stdlib>/core/text/parse.rg");
            TestHarness.Check("sourceName[34]（施工块 3-2 core.text）",
                roots[34].Span?.sourceName ?? "<null>", "<stdlib>/core/text/text.rg");
            TestHarness.Check("sourceName[35]（施工块 3-4 core.text UTF-8 编解码）",
                roots[35].Span?.sourceName ?? "<null>", "<stdlib>/core/text/utf8.rg");
            TestHarness.Check("sourceName[36]（MW11c core.time）",
                roots[36].Span?.sourceName ?? "<null>", "<stdlib>/core/time.rg");

            TestHarness.Blank();
        }

        // ===== 2a. .bootstrap 结构：恰好 1 个 ext operator callable =====
        private static void TestBootstrapStructure()
        {
            TestHarness.Section("Structure: .bootstrap ext operator");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 1)
            {
                TestHarness.CheckTrue("ParseAll 至少 1 棵（结构断言前置）", false);
                TestHarness.Blank();
                return;
            }
            var root = roots[0];

            // lambda 对象模型（SYNTAX §5.2）：Func/Action/AsyncFunc/AsyncAction
            // 各 33 个元数变种 + Cell/ReadonlyCell，共 134 个 class 声明；
            // 末尾 any_to_string（§3.8 toString 机制的 priv 全局 native 触达点）；
            // any_hash（Map 键判等，同构 priv 全局 native，any_to_string 之前）；
            // String.length ext const 内建字段（VM 直读，同 Array.length 通道）
            TestHarness.CheckTrue("bootstrap 包含原有声明和 SerializationBase",
                root.Declarations.Count == 140, $"实际 {root.Declarations.Count}");
            TestHarness.CheckTrue("首声明是 namespace core",
                root.Declarations.Count > 0
                && root.Declarations[0] is NamespaceDeclarationASTNode,
                root.Declarations.Count > 0 ? root.Declarations[0].GetType().Name : "<none>");
            var fn = root.Declarations.Count > 1
                ? root.Declarations[1] as CallableDeclarationASTNode : null;
            TestHarness.CheckTrue("次声明是 callable（ext operator）", fn != null,
                root.Declarations.Count > 1 ? root.Declarations[1].GetType().Name : "<none>");
            if (fn == null) { TestHarness.Blank(); return; }

            TestHarness.Check("限定名（ext 目标.成员名）", fn.Name, "i32.EnumerateInRange");
            TestHarness.CheckTrue("带 ext 修饰符", fn.Modifiers.Contains(Keywords.EXT));
            TestHarness.CheckTrue("带 pub 修饰符", fn.Modifiers.Contains(Keywords.PUB));
            TestHarness.CheckTrue("Kind 是 Operator", fn.Kind == CallableKind.Operator);
            TestHarness.CheckTrue("有 Body（Rigi 自举实现）", fn.Body != null);

            // M52：core.Pair\<TKey, TValue\> 自举声明（SYNTAX §18 解构协议根）
            var intrinsicRoot = roots.Single(r => r.IsIntrinsicDeclarations);
            var pair = intrinsicRoot.Declarations.OfType<ClassDeclarationASTNode>()
                .SingleOrDefault(c => c.ClassName == "Pair");
            TestHarness.CheckTrue("内建源码声明 core.Pair", pair != null);
            if (pair != null)
            {
                TestHarness.CheckTrue("Pair 是 open 泛型类",
                    pair.Modifiers.Contains(Keywords.OPEN)
                    && pair.GenericParameters?.Parameters.Count == 2);
            }

            // lambda 对象模型基类族与 Cell（SYNTAX §5.2）：元数 0–32 预生成
            var classes = root.Declarations.OfType<ClassDeclarationASTNode>().ToList();
            TestHarness.CheckTrue("Func 族 33 个元数变种",
                classes.Count(c => c.ClassName == "Func") == 33,
                $"实际 {classes.Count(c => c.ClassName == "Func")}");
            TestHarness.CheckTrue("Action 族 33 个元数变种",
                classes.Count(c => c.ClassName == "Action") == 33,
                $"实际 {classes.Count(c => c.ClassName == "Action")}");
            TestHarness.CheckTrue("AsyncFunc 族 33 个元数变种",
                classes.Count(c => c.ClassName == "AsyncFunc") == 33,
                $"实际 {classes.Count(c => c.ClassName == "AsyncFunc")}");
            TestHarness.CheckTrue("AsyncAction 族 33 个元数变种",
                classes.Count(c => c.ClassName == "AsyncAction") == 33,
                $"实际 {classes.Count(c => c.ClassName == "AsyncAction")}");
            TestHarness.CheckTrue("Cell/ReadonlyCell 各 1 个",
                classes.Count(c => c.ClassName == "Cell") == 1
                    && classes.Count(c => c.ClassName == "ReadonlyCell") == 1);
            var asyncFunc = classes.FirstOrDefault(c => c.ClassName == "AsyncFunc"
                && c.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("AsyncFunc 是 shared abstract class",
                asyncFunc != null && asyncFunc.Modifiers.Contains(Keywords.SHARED)
                    && asyncFunc.Modifiers.Contains(Keywords.ABSTRACT));
            var func = classes.FirstOrDefault(c => c.ClassName == "Func"
                && c.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("Func 非 shared（abstract class）",
                func != null && !func.Modifiers.Contains(Keywords.SHARED)
                    && func.Modifiers.Contains(Keywords.ABSTRACT));

            // any_to_string（§3.8 toString 机制修订）：priv 全局
            // native（@NativeLibrary/@NativeSymbol 双注解、无体、参数 Any）
            var anyToString = intrinsicRoot.Declarations.OfType<CallableDeclarationASTNode>()
                .SingleOrDefault(method => method.Name == "any_to_string");
            TestHarness.CheckTrue("any_to_string 保持 priv native 全局声明",
                anyToString != null && anyToString.Name == "any_to_string"
                && anyToString.Modifiers.Contains(Keywords.PRIV)
                && anyToString.Modifiers.Contains(Keywords.NATIVE)
                && anyToString.Body == null
                && anyToString.Annotations.Count == 2,
                anyToString == null ? "<none>" : anyToString.Name);

            TestHarness.Blank();
        }

        // ===== 2b. Console 结构（namespace core.io + class Console）=====
        private static void TestConsoleStructure()
        {
            TestHarness.Section("Structure: namespace core.io + class Console");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 2)
            {
                TestHarness.CheckTrue("ParseAll 至少 2 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/Console.rg");

            TestHarness.CheckTrue("顶层恰好 2 个声明（namespace + class）",
                root.Declarations.Count == 2, $"实际 {root.Declarations.Count}");

            var ns = root.Declarations.Count > 0 ? root.Declarations[0] as NamespaceDeclarationASTNode : null;
            TestHarness.CheckTrue("首声明是 namespace", ns != null,
                root.Declarations.Count > 0 ? root.Declarations[0].GetType().Name : "<none>");
            if (ns != null)
            {
                TestHarness.Check("namespace 路径", AstDescribe.Symbol(ns.Name.symbol), "core.io");
            }

            var cls = root.Declarations.Count > 1 ? root.Declarations[1] as ClassDeclarationASTNode : null;
            TestHarness.CheckTrue("次声明是 class", cls != null,
                root.Declarations.Count > 1 ? root.Declarations[1].GetType().Name : "<none>");
            if (cls == null) { TestHarness.Blank(); return; }

            TestHarness.Check("class 名", cls.ClassName, "Console");
            TestHarness.CheckTrue("class 带 pub 修饰符", cls.Modifiers.Contains(Keywords.PUB));
            TestHarness.CheckTrue("Console 恰好 3 个 callable 成员",
                cls.Members.Count == 3 && cls.Members.All(m => m is CallableDeclarationASTNode),
                $"实际 {cls.Members.Count} 个成员");
            if (cls.Members.Count == 3)
            {
                CheckNativeMethod("print", cls.Members[0]);
                CheckNativeMethod("printErr", cls.Members[1]);
                CheckPrintln(cls.Members[2]);
            }

            TestHarness.Blank();
        }

        // native 无体方法：static + native 修饰符、Body 为 null、恰好两个注解
        private static void CheckNativeMethod(string label, ASTNode member)
        {
            var f = member as CallableDeclarationASTNode;
            TestHarness.CheckTrue($"{label} 是 callable", f != null, member.GetType().Name);
            if (f == null) return;

            TestHarness.Check($"{label} 方法名", f.Name, label);
            TestHarness.CheckTrue($"{label} 带 static 修饰符", f.Modifiers.Contains(Keywords.STATIC));
            TestHarness.CheckTrue($"{label} 带 native 修饰符", f.Modifiers.Contains(Keywords.NATIVE));
            TestHarness.CheckTrue($"{label} 无 Body（native 无体）", f.Body == null);
            TestHarness.CheckTrue($"{label} 恰好 2 个注解",
                f.Annotations.Count == 2, $"实际 {f.Annotations.Count}");
            if (f.Annotations.Count == 2)
            {
                TestHarness.Check($"{label} 注解名",
                    AstDescribe.Symbol(f.Annotations[0].Name.symbol) + ", " +
                    AstDescribe.Symbol(f.Annotations[1].Name.symbol),
                    "NativeLibrary, NativeSymbol");
            }
        }

        // println：Rigi 层包装（有 Body、无注解、无 native）
        private static void CheckPrintln(ASTNode member)
        {
            var f = member as CallableDeclarationASTNode;
            TestHarness.CheckTrue("println 是 callable", f != null, member.GetType().Name);
            if (f == null) return;

            TestHarness.Check("println 方法名", f.Name, "println");
            TestHarness.CheckTrue("println 带 static 修饰符", f.Modifiers.Contains(Keywords.STATIC));
            TestHarness.CheckTrue("println 不带 native 修饰符", !f.Modifiers.Contains(Keywords.NATIVE));
            TestHarness.CheckTrue("println 有 Body", f.Body != null);
            TestHarness.CheckTrue("println 无注解", f.Annotations.Count == 0);
        }

        // ===== 2c. collections 结构（namespace + 2 interface + 抽象基类 +
        // 2 具体类，S9f 泛型抽象基类模式）=====
        private static void TestCollectionsStructure()
        {
            TestHarness.Section("Structure: namespace core.collections");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 5)
            {
                TestHarness.CheckTrue("ParseAll 至少 5 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/collections.rg");

            // 顶层：namespace + IEnumerator/IEnumerable 接口 +
            // RangeEnumerator\<T\> 抽象基类 + RangeEnumeratorI32/RangeI32
            // 具体类（共 6 个声明，S9f）
            TestHarness.CheckTrue("顶层恰好 18 个声明（namespace + 2 interface + " +
                "abstract 基类 + 2 class + alloc_array/arrayOf/arrayOfElements + " +
                "span_alloc/spanOf/shared_span_alloc/sharedSpanOf + List/ListEnumerator + " +
                "Map/MapEnumerator/ListStorageEnumerator）",
                root.Declarations.Count == 18, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 17) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.collections",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.collections");
            TestHarness.CheckTrue("声明[1] 是 interface IEnumerator",
                root.Declarations[1] is InterfaceDeclarationASTNode iface1
                && iface1.InterfaceName == "IEnumerator");
            TestHarness.CheckTrue("声明[2] 是 interface IEnumerable",
                root.Declarations[2] is InterfaceDeclarationASTNode iface2
                && iface2.InterfaceName == "IEnumerable");
            TestHarness.CheckTrue("声明[3] 是 abstract class RangeEnumerator（泛型）",
                root.Declarations[3] is ClassDeclarationASTNode baseCls
                && baseCls.ClassName == "RangeEnumerator"
                && baseCls.Modifiers.Contains(Keywords.ABSTRACT)
                && baseCls.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[4] 是 class RangeEnumeratorI32",
                root.Declarations[4] is ClassDeclarationASTNode cls1
                && cls1.ClassName == "RangeEnumeratorI32");
            TestHarness.CheckTrue("声明[5] 是 class RangeI32",
                root.Declarations[5] is ClassDeclarationASTNode cls2
                && cls2.ClassName == "RangeI32");
            TestHarness.CheckTrue("声明[6] 是 alloc_array native",
                root.Declarations[6] is CallableDeclarationASTNode alloc
                && alloc.Name == "alloc_array"
                && alloc.Modifiers.Contains(Keywords.NATIVE)
                && alloc.Body == null
                && alloc.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[7] 是 arrayOf",
                root.Declarations[7] is CallableDeclarationASTNode arrayOf
                && arrayOf.Name == "arrayOf"
                && arrayOf.Body != null
                && arrayOf.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[8] 是 arrayOfElements",
                root.Declarations[8] is CallableDeclarationASTNode arrayOfElements
                && arrayOfElements.Name == "arrayOfElements"
                && arrayOfElements.Body != null
                && arrayOfElements.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[9] 是 span_alloc native",
                root.Declarations[9] is CallableDeclarationASTNode spanAlloc
                && spanAlloc.Name == "span_alloc"
                && spanAlloc.Modifiers.Contains(Keywords.NATIVE)
                && spanAlloc.Body == null
                && spanAlloc.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[10] 是 spanOf",
                root.Declarations[10] is CallableDeclarationASTNode spanOf
                && spanOf.Name == "spanOf"
                && spanOf.Body != null
                && spanOf.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[11] 是 shared_span_alloc native",
                root.Declarations[11] is CallableDeclarationASTNode sharedAlloc
                && sharedAlloc.Name == "shared_span_alloc"
                && sharedAlloc.Modifiers.Contains(Keywords.NATIVE)
                && sharedAlloc.Body == null
                && sharedAlloc.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[12] 是 sharedSpanOf",
                root.Declarations[12] is CallableDeclarationASTNode sharedSpanOf
                && sharedSpanOf.Name == "sharedSpanOf"
                && sharedSpanOf.Body != null
                && sharedSpanOf.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[13] 是 class List（一个泛型参数）",
                root.Declarations[13] is ClassDeclarationASTNode listCls
                && listCls.ClassName == "List"
                && listCls.GenericParameters?.Parameters.Count == 1
                && listCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init)
                && listCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "add")
                && listCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getAtIndex")
                && listCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "removeAt")
                && listCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("声明[14] 是 class ListEnumerator",
                root.Declarations[14] is ClassDeclarationASTNode listEnum
                && listEnum.ClassName == "ListEnumerator"
                && listEnum.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[15] 是 class Map（两个泛型参数）",
                root.Declarations[15] is ClassDeclarationASTNode mapCls
                && mapCls.ClassName == "Map"
                && mapCls.GenericParameters?.Parameters.Count == 2
                && mapCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init)
                && mapCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "set")
                && mapCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "tryGet")
                && mapCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "containsKey")
                && mapCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "remove")
                && mapCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("声明[16] 是 class MapEnumerator",
                root.Declarations[16] is ClassDeclarationASTNode mapEnum
                && mapEnum.ClassName == "MapEnumerator"
                && mapEnum.GenericParameters?.Parameters.Count == 2);

            // 接口方法无体（§11）；抽象基类有 abstract 方法；实现类成员带 override
            if (root.Declarations[1] is InterfaceDeclarationASTNode enumerator)
            {
                TestHarness.CheckTrue("IEnumerator 双成员均无 Body（接口无体方法）",
                    enumerator.Members.Count == 2
                    && enumerator.Members.All(m =>
                        m is CallableDeclarationASTNode { Body: null }));
            }
            if (root.Declarations[3] is ClassDeclarationASTNode baseClass)
            {
                var moveNext = baseClass.Members.OfType<CallableDeclarationASTNode>()
                    .FirstOrDefault(m => m.Name == "moveNext");
                TestHarness.CheckTrue("RangeEnumerator.moveNext 带 abstract + override",
                    moveNext != null
                    && moveNext.Modifiers.Contains(Keywords.ABSTRACT)
                    && moveNext.Modifiers.Contains(Keywords.OVERRIDE));
            }
            if (root.Declarations[5] is ClassDeclarationASTNode range)
            {
                var iterate = range.Members.OfType<CallableDeclarationASTNode>()
                    .FirstOrDefault(m => m.Name == "iterate");
                TestHarness.CheckTrue("RangeI32.iterate 带 override 修饰符",
                    iterate != null && iterate.Modifiers.Contains(Keywords.OVERRIDE));
            }

            TestHarness.Blank();
        }

        // ===== 2d. coroutine 结构（MW11c：15 类型 + 29 native + sleep）=====
        private static void TestCoroutineStructure()
        {
            TestHarness.Section("Structure: namespace core.coroutine");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 6)
            {
                TestHarness.CheckTrue("ParseAll 至少 6 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/coroutine.rg");
            // NativeRc 相关声明单独按名称断言，协程既有 API 保持顺序检查。
            var declarations = root.Declarations.Where(node => node switch
            {
                ClassDeclarationASTNode c => c.ClassName is not ("CoroutineHandle" or "CoroutineCarriage"),
                InterfaceDeclarationASTNode i => i.InterfaceName != "ICoroutineHandle",
                CallableDeclarationASTNode f => f.Name is not ("retainCoroutine" or "rigi_native_rc_retain" or "rigi_native_rc_release"),
                _ => true
            }).ToList();
            var handle = root.Declarations.OfType<ClassDeclarationASTNode>().Single(c => c.ClassName == "CoroutineHandle");
            var carrier = root.Declarations.OfType<ClassDeclarationASTNode>().Single(c => c.ClassName == "CoroutineCarriage");
            TestHarness.CheckTrue("CoroutineHandle 是 local，且实现 NativeRcHandle", !handle.Modifiers.Contains(Keywords.SHARED)
                && handle.BaseClass != null && AstDescribe.Type(handle.BaseClass).Contains("NativeRcHandle"));
            TestHarness.CheckTrue("CoroutineCarriage 是 shared 且只暴露 retain", carrier.Modifiers.Contains(Keywords.SHARED)
                && carrier.Members.OfType<CallableDeclarationASTNode>().Where(m => m.Kind != CallableKind.Init)
                    .All(m => m.Name == "retain"));


            // MW11c 顶层：namespace + 15 类型（Task/Task\<TReturn\> +
            // TaskState + Executor 族 4 + PollingAlarm/EventAlarm/
            // SleepAlarm + Mutex + Timer + CoroutineLocal + CoroutineCarriageQueue/
            // Dispatcher）+ laneOfExecutor 助手 + 32 个 rigi_ native
            // 原语 + sleep Rigi 包装（共 50 个声明）。棒5a：删
            // make_sleep_alarm；增 SleepAlarm/laneOfExecutor 与句柄
            // lane/current、alarm_wait、poll_*、failure_record/drop；
            // 其后增 coro_local_push/pop/get/inherit（§20.2）；
            // L8 增 event_create_sticky/event_signal（用户 EventAlarm
            // 默认底座两面，§19.3）；3b-β 曾增 shell_msg_try_take/
            // shell_handle_release 两面，3b-δ2 壳释放属主化随消息面
            // 一并删除（挂起栈消化为纯 native 内部面，无 Rigi 声明）
            TestHarness.CheckTrue("顶层恰好 50 个声明（namespace + 15 类型 + 34 func）",
                declarations.Count == 50, $"实际 {declarations.Count}");
            if (declarations.Count < 50) { TestHarness.Blank(); return; }

            var ns = declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.coroutine",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.coroutine");

            TestHarness.CheckTrue("声明[1] 是非泛型 class Task（具体 shared，非 abstract）",
                declarations[1] is ClassDeclarationASTNode task0
                && task0.ClassName == "Task"
                && (task0.GenericParameters == null
                    || task0.GenericParameters.Parameters.Count == 0)
                && task0.Modifiers.Contains(Keywords.SHARED)
                && !task0.Modifiers.Contains(Keywords.ABSTRACT)
                && task0.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Kind == CallableKind.Init) == 1
                && task0.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Name == "run") == 2);
            TestHarness.CheckTrue("声明[2] 是泛型 class Task（具体 shared，同名不同元数）",
                declarations[2] is ClassDeclarationASTNode task1
                && task1.ClassName == "Task"
                && task1.GenericParameters?.Parameters.Count == 1
                && task1.Modifiers.Contains(Keywords.SHARED)
                && !task1.Modifiers.Contains(Keywords.ABSTRACT));
            TestHarness.CheckTrue("声明[3] 是 TaskState enum struct（六 case）",
                declarations[3] is EnumStructDeclarationASTNode taskState
                && taskState.EnumName == "TaskState"
                && taskState.Cases.Count == 6
                && taskState.Cases.Select(c => c.CaseName).SequenceEqual(
                    new[] { "Created", "Runnable", "Suspended", "Completed",
                        "Failed", "Cancelled" }));
            TestHarness.CheckTrue("声明[4] 是 class Executor（abstract）",
                declarations[4] is ClassDeclarationASTNode exec
                && exec.ClassName == "Executor"
                && exec.Modifiers.Contains(Keywords.ABSTRACT));
            TestHarness.CheckTrue("声明[5..7] 是三个内置 Executor（pub shared singleton）",
                declarations[5] is ClassDeclarationASTNode mainExec
                && mainExec.ClassName == "MainExecutor"
                && declarations[6] is ClassDeclarationASTNode computeExec
                && computeExec.ClassName == "ComputeExecutor"
                && declarations[7] is ClassDeclarationASTNode ioExec
                && ioExec.ClassName == "IOExecutor"
                && new[] { mainExec, computeExec, ioExec }.All(e =>
                    e.Modifiers.Contains(Keywords.SINGLETON)
                    && e.Modifiers.Contains(Keywords.SHARED)
                    && !e.Modifiers.Contains(Keywords.ABSTRACT)));
            TestHarness.CheckTrue("声明[8] 是 PollingAlarm（abstract，含 isReady 抽象方法）",
                declarations[8] is ClassDeclarationASTNode alarm
                && alarm.ClassName == "PollingAlarm"
                && alarm.Modifiers.Contains(Keywords.ABSTRACT)
                && alarm.Members.Count == 1
                && alarm.Members[0] is CallableDeclarationASTNode ready
                && ready.Name == "isReady"
                && ready.Modifiers.Contains(Keywords.ABSTRACT)
                && ready.Body == null);
            TestHarness.CheckTrue("声明[9] 是 EventAlarm（abstract，L8 增 ensureHandle/signal 底座面）",
                declarations[9] is ClassDeclarationASTNode eventAlarm
                && eventAlarm.ClassName == "EventAlarm"
                && eventAlarm.Modifiers.Contains(Keywords.ABSTRACT)
                && eventAlarm.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "ensureHandle")
                && eventAlarm.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "signal"));
            TestHarness.CheckTrue("声明[10] 是 SleepAlarm : EventAlarm（priv shared）",
                declarations[10] is ClassDeclarationASTNode sleepAlarm
                && sleepAlarm.ClassName == "SleepAlarm"
                && sleepAlarm.Modifiers.Contains(Keywords.PRIV)
                && sleepAlarm.Modifiers.Contains(Keywords.SHARED)
                && sleepAlarm.BaseClass != null);
            TestHarness.CheckTrue("声明[11] 是 Mutex（具体 shared，嵌套 Lock + acquire/release/runSynchronously）",
                declarations[11] is ClassDeclarationASTNode mutex
                && mutex.ClassName == "Mutex"
                && mutex.Modifiers.Contains(Keywords.SHARED)
                && !mutex.Modifiers.Contains(Keywords.ABSTRACT)
                && mutex.Members.OfType<ClassDeclarationASTNode>()
                    .Any(nested => nested.ClassName == "Lock")
                && mutex.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "acquire")
                && mutex.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "release")
                && mutex.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Name == "runSynchronously") == 2);
            TestHarness.CheckTrue("声明[12] 是 Timer : EventAlarm（嵌套 RepeatOption 三 case）",
                declarations[12] is ClassDeclarationASTNode timer
                && timer.ClassName == "Timer"
                && timer.Modifiers.Contains(Keywords.SHARED)
                && timer.BaseClass != null
                && timer.Members.OfType<EnumStructDeclarationASTNode>()
                    .Any(nested => nested.EnumName == "RepeatOption"
                        && nested.Cases.Count == 3
                        && nested.Cases.Select(c => c.CaseName).SequenceEqual(
                            new[] { "NoRepeat", "Repeat", "InfiniteRepeat" })));
            TestHarness.CheckTrue("声明[13] 是泛型 class CoroutineLocal（具体 shared，withValue/get）",
                declarations[13] is ClassDeclarationASTNode coroutineLocal
                && coroutineLocal.ClassName == "CoroutineLocal"
                && coroutineLocal.GenericParameters?.Parameters.Count == 1
                && coroutineLocal.Modifiers.Contains(Keywords.SHARED)
                && !coroutineLocal.Modifiers.Contains(Keywords.ABSTRACT)
                && coroutineLocal.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Kind == CallableKind.Init) == 2
                && coroutineLocal.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "get")
                && coroutineLocal.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Name == "withValue") == 2);
            // 棒4a：§17.4 Rigi 世界调度逻辑（内部 API，均 priv）
            TestHarness.CheckTrue("声明[14] 是 CoroutineCarriageQueue（priv 内部环形队列）",
                declarations[14] is ClassDeclarationASTNode i64Queue
                && i64Queue.ClassName == "CoroutineCarriageQueue"
                && i64Queue.Modifiers.Contains(Keywords.PRIV));
            TestHarness.CheckTrue("声明[15] 是 Dispatcher（priv shared singleton）",
                declarations[15] is ClassDeclarationASTNode dispatcher
                && dispatcher.ClassName == "Dispatcher"
                && dispatcher.Modifiers.Contains(Keywords.PRIV)
                && dispatcher.Modifiers.Contains(Keywords.SHARED)
                && dispatcher.Modifiers.Contains(Keywords.SINGLETON));
            TestHarness.CheckTrue("声明[16] 是 laneOfExecutor 模块级助手（非 native，有体）",
                declarations[16] is CallableDeclarationASTNode laneOf
                && laneOf.Name == "laneOfExecutor"
                && !laneOf.Modifiers.Contains(Keywords.NATIVE)
                && laneOf.Modifiers.Contains(Keywords.PRIV)
                && laneOf.Body != null);

            // 声明[17..48]：§17.4 native 原语面（rigi_ 前缀，priv native；
            // 棒5a 增 coroutine_current/lane、alarm_wait、poll_*、
            // failure_record/drop；make_sleep_alarm 已删；其后增
            // coro_local_* 四面；L8 增 event_create_sticky/event_signal；
            // 3b-β 曾增 shell_msg_try_take/shell_handle_release 壳消息
            // 两面，3b-δ2 壳释放属主化已删除）
            string[] expectedNatives = {
                "rigi_worker_parallelism",
                "rigi_worker_create", "rigi_worker_destroy", "rigi_worker_enqueue",
                "rigi_worker_park", "rigi_coroutine_create", "rigi_coroutine_resume",
                "rigi_coroutine_destroy", "rigi_timer_create", "rigi_timer_cancel",
                "rigi_timer_destroy", "rigi_sync_mutex_create",
                "rigi_sync_mutex_acquire", "rigi_sync_mutex_release",
                "rigi_tls_current_context", "rigi_time_now",
                "rigi_coroutine_current", "rigi_coroutine_get_lane",
                "rigi_coroutine_set_lane", "rigi_alarm_wait",
                "rigi_event_create_sticky", "rigi_event_signal",
                "rigi_poll_arm", "rigi_poll_pending", "rigi_poll_schedule",
                "rigi_poll_clear", "rigi_failure_record", "rigi_failure_drop",
                "rigi_coro_local_push", "rigi_coro_local_pop",
                "rigi_coro_local_get", "rigi_coro_local_inherit" };
            for (int i = 0; i < expectedNatives.Length; i++)
            {
                var index = i + 17;
                if (declarations[index] is CallableDeclarationASTNode nativeFunc)
                {
                    TestHarness.CheckTrue($"声明[{index}] 是 {expectedNatives[i]} priv native",
                        nativeFunc.Name == expectedNatives[i]
                        && nativeFunc.Modifiers.Contains(Keywords.NATIVE)
                        && nativeFunc.Modifiers.Contains(Keywords.PRIV)
                        && nativeFunc.Body == null
                        && nativeFunc.Annotations.Count >= 1);
                }
                else
                {
                    TestHarness.CheckTrue($"声明[{index}] 是 {expectedNatives[i]} native",
                        false, declarations[index].GetType().Name);
                }
            }
            TestHarness.CheckTrue("声明[49] 是 sleep Rigi 包装（非 native，有体）",
                declarations[49] is CallableDeclarationASTNode sleep
                && sleep.Name == "sleep"
                && !sleep.Modifiers.Contains(Keywords.NATIVE)
                && sleep.Body != null
                && sleep.ReturnType != null);

            TestHarness.Blank();
        }

        // ===== 2e. disposable 结构（namespace core + IDisposable 接口）=====
        private static void TestDisposableStructure()
        {
            TestHarness.Section("Structure: namespace core + IDisposable");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 7)
            {
                TestHarness.CheckTrue("ParseAll 至少 7 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/disposable.rg");

            TestHarness.CheckTrue("顶层恰好 2 个声明（namespace + interface）",
                root.Declarations.Count == 2, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 2) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core");
            TestHarness.CheckTrue("次声明是 interface IDisposable（dispose 无体）",
                root.Declarations[1] is InterfaceDeclarationASTNode disposable
                && disposable.InterfaceName == "IDisposable"
                && disposable.Members.Count == 1
                && disposable.Members[0] is CallableDeclarationASTNode dispose
                && dispose.Name == "dispose"
                && dispose.Body == null);

            TestHarness.Blank();
        }

        // ===== 2f. exceptions 结构（namespace core + 6 异常子类）=====
        private static void TestExceptionsStructure()
        {
            TestHarness.Section("Structure: namespace core + 异常子类");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 8)
            {
                TestHarness.CheckTrue("ParseAll 至少 8 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/exceptions.rg");

            // 顶层：namespace + RuntimeException/IOException/CastException/
            // NoSuchMethodException/DividedByZeroException/OutOfBoundException/
            // IllegalStateException/NoSuchElementException/IllegalArgumentException
            // 9 个 open class（共 12 个声明；MW9b 增 OutOfBoundException，
            // MW11c 增 IllegalStateException，MW11d-B1 增 NoSuchElementException，
            // b4-1 增 IllegalArgumentException——Parcel 字段键契约 §4.6.3/D3）
            TestHarness.CheckTrue("顶层恰好 12 个声明（namespace + Exception + 10 class）",
                root.Declarations.Count == 12, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 12) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core");

            string[] expected = { "Exception", "RuntimeException", "ImmutablePlaceException", "IOException", "CastException",
                "NoSuchMethodException", "DividedByZeroException", "OutOfBoundException",
                "IllegalStateException", "NoSuchElementException", "IllegalArgumentException" };
            for (int i = 0; i < expected.Length; i++)
            {
                var index = i + 1;
                if (root.Declarations[index] is ClassDeclarationASTNode exceptionClass)
                {
                    var wantAbstract = expected[i] == "Exception";
                    if (expected[i] == "ImmutablePlaceException")
                    {
                        TestHarness.CheckTrue("ImmutablePlaceException 是非 open 类且有无参 init",
                            exceptionClass.ClassName == "ImmutablePlaceException"
                            && !exceptionClass.Modifiers.Contains(Keywords.OPEN)
                            && exceptionClass.Members.OfType<CallableDeclarationASTNode>()
                                .Any(m => m.Kind == CallableKind.Init && m.Parameters.Parameters.Count == 0));
                        continue;
                    }
                    var wantMod = wantAbstract ? Keywords.ABSTRACT : Keywords.OPEN;
                    var kindLabel = wantAbstract ? "abstract" : "open";
                    TestHarness.CheckTrue($"声明[{index}] 是 {kindLabel} class {expected[i]}",
                        exceptionClass.ClassName == expected[i]
                        && exceptionClass.Modifiers.Contains(wantMod));
                    if (!wantAbstract)
                    {
                        var init = exceptionClass.Members.OfType<CallableDeclarationASTNode>()
                            .FirstOrDefault(m => m.Kind == CallableKind.Init
                                && m.Parameters.Parameters.Count == 1);
                        TestHarness.CheckTrue($"{expected[i]} 自持 init（单 String 参数）",
                            init != null
                            && init.Parameters.Parameters.Count == 1
                            && init.Parameters.Parameters[0].Type != null);
                    }
                }
                else
                {
                    TestHarness.CheckTrue($"声明[{index}] 是 class {expected[i]}", false,
                        root.Declarations[index].GetType().Name);
                }
            }

            TestHarness.Blank();
        }

        // ===== 2g-1. fs 结构（施工块 7-1：namespace core.fs +
        // FileSystemErrorKind + FileSystemException + Serializable Path +
        // rigi_host_is_windows 私有原语，STDLIB §4.5.1/§4.5.2/§4.5.9）=====
        private static void TestFsPathStructure()
        {
            TestHarness.Section("Structure: namespace core.fs");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 14)
            {
                TestHarness.CheckTrue("ParseAll 至少 14 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/fs/path.rg");

            // 顶层：namespace + 错误分类枚举 + 消息助手 + 异常类 + 平台
            // 判定原语 + 6 个词法助手（fsIsWindows/fsSep/fsByteAt/
            // fsIsSepByte/FsParsedPath/fsSplitSegments/fsParse/fsBuildPrefix/
            // fsBuildAll/fsCheckNameWindows/fsIsReservedWindows/
            // fsValidateWindows/fsValidate）+ Path struct + fsAppendAll
            // （共 20 个声明，STDLIB §4.5.1/§4.5.2/§4.5.9 + D5）
            TestHarness.CheckTrue("顶层恰好 20 个声明（namespace + enum + 异常 + native + 14 助手/类 + Path）",
                root.Declarations.Count == 20, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 5) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.fs",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.fs");

            // 声明[1]：FileSystemErrorKind 17 case（§4.5.9 逐字清单）
            TestHarness.CheckTrue("声明[1] 是 enum struct FileSystemErrorKind（§4.5.9 十七类逐字）",
                root.Declarations[1] is EnumStructDeclarationASTNode errKind
                && errKind.EnumName == "FileSystemErrorKind"
                && errKind.Cases.Select(c => c.CaseName).SequenceEqual(
                    new[] { "InvalidPath", "InvalidNameEncoding", "NotFound",
                        "AlreadyExists", "PermissionDenied", "NotDirectory",
                        "IsDirectory", "WrongType", "DirectoryNotEmpty",
                        "ReadOnlyFileSystem", "NoSpace", "CrossDevice",
                        "TooManyLinks", "PathTooLong", "SharingViolation",
                        "Unsupported", "Other" }));

            // 声明[3]：FileSystemException（open，五字段 + 双 init +
            // getMessage 覆写）
            TestHarness.CheckTrue("声明[3] 是 open class FileSystemException : core.IOException（kind/operation/path/path2/nativeError + 双 init + getMessage 覆写）",
                root.Declarations[3] is ClassDeclarationASTNode fsEx
                && fsEx.ClassName == "FileSystemException"
                && fsEx.Modifiers.Contains(Keywords.OPEN)
                && fsEx.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "kind")
                && fsEx.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "operation")
                && fsEx.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "path")
                && fsEx.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "path2")
                && fsEx.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "nativeError")
                && fsEx.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Kind == CallableKind.Init) == 2
                && fsEx.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getMessage"
                        && m.Modifiers.Contains(Keywords.OVERRIDE)));

            // 声明[4]：rigi_host_is_windows priv native（返回 bool；
            // @NativeSymbol 键 host_is_windows → C 导出 rigi_host_is_windows）
            var hostWin = root.Declarations[4] as CallableDeclarationASTNode;
            TestHarness.CheckTrue("声明[4] 是 rigi_host_is_windows priv native（返回 bool）",
                hostWin != null
                && hostWin.Name == "rigi_host_is_windows"
                && hostWin.Modifiers.Contains(Keywords.NATIVE)
                && hostWin.Modifiers.Contains(Keywords.PRIV)
                && hostWin.Body == null
                && hostWin.ReturnType != null);
            TestHarness.CheckTrue("声明[4] 带 @NativeLibrary/@NativeSymbol 双注解",
                hostWin != null && hostWin.Annotations.Count == 2
                && AstDescribe.Symbol(hostWin.Annotations[0].Name.symbol) == "NativeLibrary"
                && AstDescribe.Symbol(hostWin.Annotations[1].Name.symbol) == "NativeSymbol",
                hostWin == null ? "<none>" : $"注解数 {hostWin.Annotations.Count}");
            TestHarness.CheckTrue("声明[4] NativeSymbol 实参是 host_is_windows",
                hostWin != null && hostWin.Annotations.Count == 2
                && hostWin.Annotations[1].Arguments.Count == 1
                && AstDescribe.Expr(hostWin.Annotations[1].Arguments[0].Value.Expression)
                    == "Str(\"host_is_windows\")",
                hostWin == null || hostWin.Annotations.Count < 1
                    ? "<none>"
                    : AstDescribe.Expr(hostWin.Annotations[1].Arguments[0].Value.Expression));

            // 声明[18]：Path struct（@Serializable；text 访问器 + of +
            // 词法操作族 + equals/hash/toString 覆写）——分条断言定位
            TestHarness.CheckTrue("声明[18] 是 struct Path",
                root.Declarations[18] is StructDeclarationASTNode path
                && path.StructName == "Path");
            if (root.Declarations[18] is StructDeclarationASTNode path2)
            {
                TestHarness.CheckTrue("Path 带 @core.serialization.Serializable 注解",
                    path2.Annotations.Any(a =>
                        AstDescribe.Symbol(a.Name.symbol)
                            == "core.serialization.Serializable"));
                TestHarness.CheckTrue("Path 含 text 访问器字段（get+set）",
                    path2.Members.OfType<VariableDeclarationASTNode>()
                        .Any(f => f.Name == "text" && f.Getter != null
                            && f.Setter != null));
                TestHarness.CheckTrue("Path 含 static of",
                    path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "of"
                            && m.Modifiers.Contains(Keywords.STATIC)));
                TestHarness.CheckTrue("Path 含词法操作族",
                    path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "normalizeLexically")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "join")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "joinAll")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "toAbsolute")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "relativeTo")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "root")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "parent")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "name")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "extension")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "nameWithoutExtension")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "isAbsolute"));
                TestHarness.CheckTrue("Path 含 equals/hash/toString",
                    path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "equals")
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "hash"
                            && m.Modifiers.Contains(Keywords.OVERRIDE))
                    && path2.Members.OfType<CallableDeclarationASTNode>()
                        .Any(m => m.Name == "toString"
                            && m.Modifiers.Contains(Keywords.OVERRIDE)));
            }

            TestHarness.Blank();
        }

        // ===== 2g-1b. fs/primitives 结构（施工块 7-2：namespace core.fs +
        // open 标志位常量族 + 错误映射/二进制助手 + FileHandle/FileCarriage
        // 句柄模型 + FsWake + native 原语面 + fsOpen/fsRead 包装）=====
        private static void TestFsPrimitivesStructure()
        {
            TestHarness.Section("Structure: namespace core.fs 原语层");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 15)
            {
                TestHarness.CheckTrue("ParseAll 至少 15 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/fs/primitives.rg");

            // 顶层：namespace + 6 个 open 标志位 const + fsErrKind/
            // fsMakeException/fsByteAt/fsReadI64Le 4 助手 + FileHandle/
            // FileCarriage/FsWake 3 类 + retainFile + 5 个 priv native
            //（native_rc_retain/release + fs_open/fs_read_start/
            // fs_read_take）+ fsOpen/fsRead 2 包装（阶段 1 共 22 个声明）
            // + 阶段 2 原语族追加（kind/seek 基准/权限 const 7 + 原语族
            // priv native 17 + fsReadI32Le/fsDecodeName/fsParseStat 3
            // 助手 + FsStatInfo/FsDirEntry 2 struct + DirHandle/
            // DirCarriage 2 类 + retainDir + 挂起写/flush/定位/长度/
            // 信息查询/创建删除/移动/目录枚举包装 15）——阶段 2 共 73
            // 个声明 + 7-6 追加（rigi_fs_same_file priv native +
            // fsSameIdentity 包装，系统文件身份比较，§4.5.7 自复制拒绝
            // 判定面）2 个——共 75 个声明
            //（STDLIB §4.5.6/§4.5.7/§4.5.9 + §3.2/§3.3）
            TestHarness.CheckTrue("顶层恰好 75 个声明（阶段 1 的 22 + 阶段 2 原语族 51 + 7-6 身份比较 2）",
                root.Declarations.Count == 75, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 5) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.fs",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.fs");

            // 声明[1..6]：open 标志位 const（FS_F_READ..FS_F_CREATE_NEW，
            // 与 rigi_rt fs.c RIGI_FS_F_* 逐位一致）
            TestHarness.CheckTrue("声明[1..6] 是 FS_F_* open 标志位 const",
                root.Declarations[1] is VariableDeclarationASTNode f1
                    && f1.Name == "FS_F_READ"
                && root.Declarations[6] is VariableDeclarationASTNode f6
                    && f6.Name == "FS_F_CREATE_NEW");

            // 声明[7]：fsErrKind（归一码 → FileSystemErrorKind 唯一映射落点）
            TestHarness.CheckTrue("声明[7] 是 fsErrKind 错误映射助手",
                root.Declarations[7] is CallableDeclarationASTNode kindFn
                && kindFn.Name == "fsErrKind");

            // 声明[11]：FileHandle（internal class : NativeRcHandle<FileCarriage>，
            // token/pathText 字段 + carry/dispose 覆写）
            TestHarness.CheckTrue("声明[11] 是 internal class FileHandle（NativeRcHandle 模型）",
                root.Declarations[11] is ClassDeclarationASTNode fh
                && fh.ClassName == "FileHandle"
                && fh.Modifiers.Contains(Keywords.INTERNAL)
                && fh.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "carry"
                        && m.Modifiers.Contains(Keywords.OVERRIDE))
                && fh.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "dispose"
                        && m.Modifiers.Contains(Keywords.OVERRIDE)));

            // 声明[14]：FsWake（internal shared class : core.coroutine.
            // EventAlarm，一次性粘滞事件身份）
            TestHarness.CheckTrue("声明[14] 是 internal shared class FsWake : EventAlarm",
                root.Declarations[14] is ClassDeclarationASTNode wake
                && wake.ClassName == "FsWake"
                && wake.Modifiers.Contains(Keywords.INTERNAL)
                && wake.Modifiers.Contains(Keywords.SHARED));

            // 声明[15..19]：priv native 原语面（native_rc_retain/release +
            // fs_open/fs_read_start/fs_read_take；@NativeLibrary("rigi_rt")
            // + @NativeSymbol 双注解）
            TestHarness.CheckTrue("声明[15..19] 是 priv native 原语面",
                root.Declarations[15] is CallableDeclarationASTNode n1
                    && n1.Name == "rigi_native_rc_retain"
                    && n1.Modifiers.Contains(Keywords.NATIVE)
                    && n1.Modifiers.Contains(Keywords.PRIV)
                && root.Declarations[17] is CallableDeclarationASTNode n3
                    && n3.Name == "rigi_fs_open"
                    && n3.Modifiers.Contains(Keywords.NATIVE)
                    && n3.Modifiers.Contains(Keywords.PRIV)
                && root.Declarations[19] is CallableDeclarationASTNode n5
                    && n5.Name == "rigi_fs_read_take"
                    && n5.Modifiers.Contains(Keywords.NATIVE)
                    && n5.Modifiers.Contains(Keywords.PRIV));

            // 声明[20..21]：fsOpen/fsRead internal 包装（挂起读是普通
            // func 内 yield，§3.1）
            TestHarness.CheckTrue("声明[20..21] 是 fsOpen/fsRead internal 包装",
                root.Declarations[20] is CallableDeclarationASTNode o1
                    && o1.Name == "fsOpen"
                    && o1.Modifiers.Contains(Keywords.INTERNAL)
                && root.Declarations[21] is CallableDeclarationASTNode o2
                    && o2.Name == "fsRead"
                    && o2.Modifiers.Contains(Keywords.INTERNAL));

            // 阶段 2 首批：声明[22..23] 是 FS_KIND_* kind 常量；末声明
            // 是 fsDirRead 目录枚举包装（阶段 2 原语族追加在尾部，前段
            // 序号不受影响）
            TestHarness.CheckTrue("声明[22..23] 是 FS_KIND_FILE/FS_KIND_DIRECTORY const（阶段 2 首批）",
                root.Declarations[22] is VariableDeclarationASTNode k1
                    && k1.Name == "FS_KIND_FILE"
                && root.Declarations[23] is VariableDeclarationASTNode k2
                    && k2.Name == "FS_KIND_DIRECTORY");
            // 末声明：7-6 追加的 fsSameIdentity 身份比较包装（其 priv
            // native rigi_fs_same_file 紧随其前；阶段 2 尾 fsDirRead 为
            // 倒数第三）
            TestHarness.CheckTrue("末声明是 fsSameIdentity internal 包装（7-6 尾）",
                root.Declarations[^1] is CallableDeclarationASTNode si
                    && si.Name == "fsSameIdentity"
                    && si.Modifiers.Contains(Keywords.INTERNAL));

            TestHarness.Blank();
        }

        // ===== 2g-2. global_exceptions 结构（MW12b：namespace core +
        // UndisposedResourceException + GlobalExceptionHandler）=====
        private static void TestGlobalExceptionsStructure()
        {
            TestHarness.Section("Structure: namespace core + 全局异常通道");
            var roots = StdlibSources.ParseAll();
            if (roots.Count < 9)
            {
                TestHarness.CheckTrue("ParseAll 至少 9 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/global_exceptions.rg");

            TestHarness.CheckTrue("顶层恰好 3 个声明（namespace + 2 class）",
                root.Declarations.Count == 3, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 3) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core");
            TestHarness.CheckTrue(
                "声明[1] 是 open class UndisposedResourceException（单一 init(resourceType) + getMessage 覆写）",
                root.Declarations[1] is ClassDeclarationASTNode undisposed
                && undisposed.ClassName == "UndisposedResourceException"
                && undisposed.Modifiers.Contains(Keywords.OPEN)
                && undisposed.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Kind == CallableKind.Init) == 1
                && undisposed.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getMessage"
                        && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue(
                "声明[2] 是 class GlobalExceptionHandler（register/dispatch 静态 + printErr/注册表三面 native）",
                root.Declarations[2] is ClassDeclarationASTNode handler
                && handler.ClassName == "GlobalExceptionHandler"
                && handler.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "register" && m.Modifiers.Contains(Keywords.STATIC))
                && handler.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "dispatch" && m.Modifiers.Contains(Keywords.STATIC))
                && handler.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Modifiers.Contains(Keywords.NATIVE)) == 4);

            TestHarness.Blank();
        }

        private static void TestTimeStructure()
        {
            TestHarness.Section("Structure: namespace core.time");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 14)
            {
                TestHarness.CheckTrue("ParseAll 至少 14 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/time.rg");

            // 顶层（块 6-1）：namespace + TimeStamp/TimeSpan/DateTime 3 个
            // struct + rigi_time_now native；块 6-2 追加 TimeParseException
            // + 11 个 priv 扫描/日历助手 + 4 个 ext 静态解析入口；块 6-3
            // 追加 rigi_monotonic_now_ns native + MonotonicInstant/
            // MonotonicClock/Stopwatch；UTC 六分量块追加 internal
            // DateTimeCivil 载体；datetime-clock-precision 块把 DateTime.now
            // 底座从毫秒 rigi_time_now 换成单次采样 rigi_time_now_parts
            // （out Span<u8> 12 字节小端）+ tmReadI64Le/tmReadI32Le 两个
            // priv 读取助手（共 28 个声明，RUNTIME §19.7/§17.4 +
            // STDLIB §4.9.2/§4.9.3/§4.9.4/§4.9.5）
            TestHarness.CheckTrue("顶层恰好 28 个声明（namespace + 3 struct + DateTimeCivil + 2 native + TimeParseException + 13 助手 + 4 ext 静态 + MonotonicInstant/MonotonicClock/Stopwatch）",
                root.Declarations.Count == 28, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 6) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.time",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.time");

            TestHarness.CheckTrue("声明[1] 是 TimeStamp struct（milliseconds + nanoseconds 访问器）",
                root.Declarations[1] is StructDeclarationASTNode timeStamp
                && timeStamp.StructName == "TimeStamp"
                && timeStamp.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "milliseconds")
                && timeStamp.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "nanoseconds" && f.Getter != null && f.Setter != null));
            TestHarness.CheckTrue("声明[2] 是 TimeSpan struct（fromMilliseconds + totalMilliseconds + 比较）",
                root.Declarations[2] is StructDeclarationASTNode timeSpan
                && timeSpan.StructName == "TimeSpan"
                && timeSpan.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "fromMilliseconds"
                        && m.Modifiers.Contains(Keywords.STATIC))
                && timeSpan.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "compareTo")
                && timeSpan.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "equals"));
            // UTC 六分量块追加的 civil-from-days 单一算法载体（internal，
            // 不对外承诺布局），挤在 DateTime 之前。
            TestHarness.CheckTrue("声明[3] 是 internal struct DateTimeCivil（year..millisecond 七分量）",
                root.Declarations[3] is StructDeclarationASTNode civil
                && civil.StructName == "DateTimeCivil"
                && civil.Modifiers.Contains(Keywords.INTERNAL)
                && civil.Members.OfType<VariableDeclarationASTNode>()
                    .Count(f => f.Name == "year" || f.Name == "month"
                        || f.Name == "day" || f.Name == "hour"
                        || f.Name == "minute" || f.Name == "second"
                        || f.Name == "millisecond") == 7);
            TestHarness.CheckTrue("声明[4] 是 DateTime struct（now minus compareTo equals）",
                root.Declarations[4] is StructDeclarationASTNode dateTime
                && dateTime.StructName == "DateTime"
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "now" && m.Modifiers.Contains(Keywords.STATIC))
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "minus")
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "compareTo")
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "equals"));
            // datetime-clock-precision 块：DateTime.now 改走单次采样专用
            // 原语 rigi_time_now_parts（12 字节小端 i64 ms + i32 ns 余量）；
            // 旧毫秒 rigi_time_now 声明已撤出本文件（core.coroutine 自留
            // 同符号声明，ABI 不动）。
            var timeNow = root.Declarations[5] as CallableDeclarationASTNode;
            TestHarness.CheckTrue("声明[5] 是 rigi_time_now_parts priv native（out Span<u8>）",
                timeNow != null
                && timeNow.Name == "rigi_time_now_parts"
                && timeNow.Modifiers.Contains(Keywords.NATIVE)
                && timeNow.Modifiers.Contains(Keywords.PRIV)
                && timeNow.Body == null
                && timeNow.Parameters.Parameters.Count == 1);
            // @NativeSymbol("time_now_parts") 必带：缺省符号经 rigi_rt
            // 前缀拼接会落空成 rigi_rigi_time_now_parts
            TestHarness.CheckTrue("声明[5] 带 @NativeLibrary/@NativeSymbol 双注解",
                timeNow != null && timeNow.Annotations.Count == 2
                && AstDescribe.Symbol(timeNow.Annotations[0].Name.symbol) == "NativeLibrary"
                && AstDescribe.Symbol(timeNow.Annotations[1].Name.symbol) == "NativeSymbol",
                timeNow == null ? "<none>" : $"注解数 {timeNow.Annotations.Count}");
            TestHarness.CheckTrue("声明[5] NativeSymbol 实参是 time_now_parts",
                timeNow != null && timeNow.Annotations.Count == 2
                && timeNow.Annotations[1].Arguments.Count == 1
                && AstDescribe.Expr(timeNow.Annotations[1].Arguments[0].Value.Expression)
                    == "Str(\"time_now_parts\")",
                timeNow == null || timeNow.Annotations.Count < 1
                    ? "<none>"
                    : AstDescribe.Expr(timeNow.Annotations[1].Arguments[0].Value.Expression));
            // 12 字节小端读取助手（priv，纯 Rigi 逐字节拼装，避免依赖
            // 宿主对齐/字节序假设）。
            TestHarness.CheckTrue("声明[6]/[7] 是 tmReadI64Le/tmReadI32Le priv 小端读取助手",
                root.Declarations[6] is CallableDeclarationASTNode rd64
                && rd64.Name == "tmReadI64Le"
                && rd64.Modifiers.Contains(Keywords.PRIV)
                && root.Declarations[7] is CallableDeclarationASTNode rd32
                && rd32.Name == "tmReadI32Le"
                && rd32.Modifiers.Contains(Keywords.PRIV));

            // ---- 块 6-2：文本格式层（§4.9.2 / §4.9.3 / §4.9.5，D6） ----
            TestHarness.CheckTrue("声明[8] 是 open class TimeParseException（isOutOfRange + position + getMessage 覆写）",
                root.Declarations[8] is ClassDeclarationASTNode timeParse
                && timeParse.ClassName == "TimeParseException"
                && timeParse.Modifiers.Contains(Keywords.OPEN)
                && timeParse.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "isOutOfRange")
                && timeParse.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "position")
                && timeParse.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getMessage"
                        && m.Modifiers.Contains(Keywords.OVERRIDE)));
            // 4 个 ext 静态解析入口（ext 成员 Name 为限定名 目标.成员；
            // 按名 + EXT/STATIC 修饰符，顺序无关）
            TestHarness.CheckTrue("含 DateTime.parse/tryParse 与 TimeSpan.parse/tryParse 4 个 ext 静态入口",
                root.Declarations.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "DateTime.parse" && m.Modifiers.Contains(Keywords.EXT)
                        && m.Modifiers.Contains(Keywords.STATIC))
                && root.Declarations.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "DateTime.tryParse" && m.Modifiers.Contains(Keywords.EXT)
                        && m.Modifiers.Contains(Keywords.STATIC))
                && root.Declarations.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "TimeSpan.parse" && m.Modifiers.Contains(Keywords.EXT)
                        && m.Modifiers.Contains(Keywords.STATIC))
                && root.Declarations.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "TimeSpan.tryParse" && m.Modifiers.Contains(Keywords.EXT)
                        && m.Modifiers.Contains(Keywords.STATIC)));
            // 两 struct 的 toString 文本输出（TimeSpan 覆写零参 ISO Duration；
            // DateTime 覆写零参 + 带偏移重载）
            TestHarness.CheckTrue("TimeSpan struct 含 toString 覆写（§4.9.3 Duration 输出）",
                root.Declarations[2] is StructDeclarationASTNode ts2
                && ts2.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "toString"
                        && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("DateTime struct 含 toString 覆写与带偏移重载（§4.9.2 RFC 3339 子集输出）",
                root.Declarations[4] is StructDeclarationASTNode dt2
                && dt2.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "toString"
                        && m.Modifiers.Contains(Keywords.OVERRIDE))
                && dt2.Members.OfType<CallableDeclarationASTNode>()
                    .Count(m => m.Name == "toString") == 2);

            // ---- 块 6-3：单调时钟层（§4.9.4 / §4.9.5） ----
            // 声明[24] rigi_monotonic_now_ns priv native（返回 i64；
            // @NativeSymbol 键 monotonic_now_ns → C 导出 rigi_monotonic_now_ns）
            var monoNow = root.Declarations[24] as CallableDeclarationASTNode;
            TestHarness.CheckTrue("声明[24] 是 rigi_monotonic_now_ns priv native（返回 i64）",
                monoNow != null
                && monoNow.Name == "rigi_monotonic_now_ns"
                && monoNow.Modifiers.Contains(Keywords.NATIVE)
                && monoNow.Modifiers.Contains(Keywords.PRIV)
                && monoNow.Body == null
                && monoNow.ReturnType != null);
            TestHarness.CheckTrue("声明[24] 带 @NativeLibrary/@NativeSymbol 双注解",
                monoNow != null && monoNow.Annotations.Count == 2
                && AstDescribe.Symbol(monoNow.Annotations[0].Name.symbol) == "NativeLibrary"
                && AstDescribe.Symbol(monoNow.Annotations[1].Name.symbol) == "NativeSymbol",
                monoNow == null ? "<none>" : $"注解数 {monoNow.Annotations.Count}");
            TestHarness.CheckTrue("声明[24] NativeSymbol 实参是 monotonic_now_ns",
                monoNow != null && monoNow.Annotations.Count == 2
                && monoNow.Annotations[1].Arguments.Count == 1
                && AstDescribe.Expr(monoNow.Annotations[1].Arguments[0].Value.Expression)
                    == "Str(\"monotonic_now_ns\")",
                monoNow == null || monoNow.Annotations.Count < 1
                    ? "<none>"
                    : AstDescribe.Expr(monoNow.Annotations[1].Arguments[0].Value.Expression));
            // 声明[25] MonotonicInstant struct（compareTo/equals/minus；无
            // Serializable——§4.9.5 不持久化序列化）
            TestHarness.CheckTrue("声明[25] 是 MonotonicInstant struct（compareTo + equals + minus，无 Serializable 注解）",
                root.Declarations[25] is StructDeclarationASTNode monoInstant
                && monoInstant.StructName == "MonotonicInstant"
                && !monoInstant.Annotations.Any(a =>
                    AstDescribe.Symbol(a.Name.symbol) == "Serializable")
                && monoInstant.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "compareTo")
                && monoInstant.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "equals")
                && monoInstant.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "minus"));
            // 声明[26] MonotonicClock class（static now）
            TestHarness.CheckTrue("声明[26] 是 MonotonicClock class（static now）",
                root.Declarations[26] is ClassDeclarationASTNode monoClock
                && monoClock.ClassName == "MonotonicClock"
                && monoClock.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "now" && m.Modifiers.Contains(Keywords.STATIC)));
            // 声明[27] Stopwatch class（start/stop/reset/restart + 只读
            // elapsed/isRunning）
            TestHarness.CheckTrue("声明[27] 是 Stopwatch class（start/stop/reset/restart + elapsed/isRunning）",
                root.Declarations[27] is ClassDeclarationASTNode stopwatch
                && stopwatch.ClassName == "Stopwatch"
                && stopwatch.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "start")
                && stopwatch.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "stop")
                && stopwatch.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "reset")
                && stopwatch.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "restart")
                && stopwatch.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "elapsed" && f.Getter != null && f.Setter == null)
                && stopwatch.Members.OfType<VariableDeclarationASTNode>()
                    .Any(f => f.Name == "isRunning" && f.Getter != null && f.Setter == null));

            TestHarness.Blank();
        }

        // ===== 2g-2. math 结构（施工块 6-4：namespace core.math + 常量 +
        // RoundingMode + 84 个 pub func + 36 个 priv native 原语；
        // 施工块 6-5 追加 Random local 类 + sys_random_u64 原语）=====
        private static void TestMathStructure()
        {
            TestHarness.Section("Structure: namespace core.math");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 18)
            {
                TestHarness.CheckTrue("ParseAll 至少 18 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/math.rg");

            // 顶层（块 6-4 阶段 1）：namespace + pi/e/tau 3 常量 +
            // RoundingMode enum struct + abs/min/max/clamp 全宽度重载
            //（各 10：8 整数 + float/double）+ isNaN/isInfinite/isFinite
            // 各 2 重载（共 50 个声明）；阶段 2 增 floor/ceil/trunc 各 2 +
            // round 4（+10）；阶段 3 增 sqrt/pow/exp/ln/log2/log10/sin/
            // cos/tan/asin/acos/atan/atan2 各 2（+26）+ 取整原语 10 +
            // 超越函数原语 26（123 个声明）；块 6-5 阶段 1 增
            // sys_random_u64 原语 + Random local 类（+2，共 125，
            // STDLIB §4.11.1–§4.11.5 / D7）
            TestHarness.CheckTrue("顶层恰好 125 个声明（namespace + 3 const + " +
                "RoundingMode + 82 func + 37 native + Random 类）",
                root.Declarations.Count == 125, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 5) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.math",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.math");

            // 常量：pub const pi/e/tau（double 类型标注）
            for (int i = 0; i < 3; i++)
            {
                var name = new[] { "pi", "e", "tau" }[i];
                TestHarness.CheckTrue($"声明[{i + 1}] 是 pub const {name}（double）",
                    root.Declarations[i + 1] is VariableDeclarationASTNode c
                    && c.Name == name
                    && c.Modifiers.Contains(Keywords.PUB));
            }

            TestHarness.CheckTrue("声明[4] 是 RoundingMode enum struct（ToEven/AwayFromZero）",
                root.Declarations[4] is EnumStructDeclarationASTNode mode
                && mode.EnumName == "RoundingMode"
                && mode.Cases.Count == 2
                && mode.Cases.Select(c => c.CaseName).SequenceEqual(
                    new[] { "ToEven", "AwayFromZero" }));

            // pub func：82 个有体非 native（abs/min/max/clamp 各 10 +
            // 分类×6 + 取整×10 + 超越×26）
            var funcs = root.Declarations.OfType<CallableDeclarationASTNode>()
                .Where(f => !f.Modifiers.Contains(Keywords.NATIVE)).ToList();
            TestHarness.CheckTrue("恰好 82 个 pub/priv 非 native 函数（有体）",
                funcs.Count == 82 && funcs.All(f => f.Body != null),
                $"实际 {funcs.Count}");
            // 关键重载按名 + 形态存在（顺序无关）
            TestHarness.CheckTrue("abs/min/max/clamp 全宽度重载齐（各 10）",
                funcs.Count(f => f.Name == "abs") == 10
                && funcs.Count(f => f.Name == "min") == 10
                && funcs.Count(f => f.Name == "max") == 10
                && funcs.Count(f => f.Name == "clamp") == 10);
            TestHarness.CheckTrue("取整族与 RoundingMode round 重载齐",
                funcs.Count(f => f.Name == "floor") == 2
                && funcs.Count(f => f.Name == "ceil") == 2
                && funcs.Count(f => f.Name == "trunc") == 2
                && funcs.Count(f => f.Name == "round") == 4);
            TestHarness.CheckTrue("超越函数重载齐（sqrt/pow/exp/ln/log2/log10/" +
                "sin/cos/tan/asin/acos/atan/atan2 各 2）",
                funcs.Count(f => f.Name == "sqrt") == 2
                && funcs.Count(f => f.Name == "pow") == 2
                && funcs.Count(f => f.Name == "exp") == 2
                && funcs.Count(f => f.Name == "ln") == 2
                && funcs.Count(f => f.Name == "log2") == 2
                && funcs.Count(f => f.Name == "log10") == 2
                && funcs.Count(f => f.Name == "sin") == 2
                && funcs.Count(f => f.Name == "cos") == 2
                && funcs.Count(f => f.Name == "tan") == 2
                && funcs.Count(f => f.Name == "asin") == 2
                && funcs.Count(f => f.Name == "acos") == 2
                && funcs.Count(f => f.Name == "atan") == 2
                && funcs.Count(f => f.Name == "atan2") == 2);
            TestHarness.CheckTrue("分类函数 isNaN/isInfinite/isFinite 各 2",
                funcs.Count(f => f.Name == "isNaN") == 2
                && funcs.Count(f => f.Name == "isInfinite") == 2
                && funcs.Count(f => f.Name == "isFinite") == 2);

            // priv native 原语：37 个（取整 10 + 超越 26 + 6-5 系统随机
            // 材料 1），无体、@NativeLibrary/@NativeSymbol 双注解
            var natives = root.Declarations.OfType<CallableDeclarationASTNode>()
                .Where(f => f.Modifiers.Contains(Keywords.NATIVE)).ToList();
            TestHarness.CheckTrue("恰好 37 个 priv native 原语（无体、双注解）",
                natives.Count == 37
                && natives.All(f => f.Modifiers.Contains(Keywords.PRIV)
                    && f.Body == null
                    && f.Annotations.Count == 2
                    && AstDescribe.Symbol(f.Annotations[0].Name.symbol) == "NativeLibrary"
                    && AstDescribe.Symbol(f.Annotations[1].Name.symbol) == "NativeSymbol"),
                $"实际 {natives.Count}");
            TestHarness.CheckTrue("pow 首版仅浮点重载（无整数 pow）",
                !funcs.Any(f => f.Name == "pow"
                    && f.Parameters.Parameters.Any(p =>
                        p.Type != null && AstDescribe.Type(p.Type).Contains("i32"))));

            // 施工块 6-5：Random local 类（末尾声明；成员方法为类内声明，
            // 不计入顶层 funcs/natives 数）
            var randomClass = root.Declarations.OfType<ClassDeclarationASTNode>()
                .SingleOrDefault(c => c.ClassName == "Random");
            TestHarness.CheckTrue("Random local 类存在（core.math，无 " +
                "shared/Serializable 标注）",
                randomClass != null
                && !randomClass.Modifiers.Contains(Keywords.SHARED)
                && !randomClass.Annotations.Any(a =>
                    AstDescribe.Symbol(a.Name.symbol) == "Serializable"));

            TestHarness.Blank();
        }

        // ===== 2h. serialization 结构与 core 的 SB 声明 =====
        private static void TestSerializationStructure()
        {
            TestHarness.Section("Structure: namespace core.serialization");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 12)
            {
                TestHarness.CheckTrue("ParseAll 至少 12 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/serialization.rg");

            TestHarness.CheckTrue("serialization 声明计数（固定 BMP 分类四表 + 查表函数）",
                root.Declarations.Count == 42, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 42) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.serialization",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.serialization");

            TestHarness.CheckTrue("声明[1] 是私有动态解码入口",
                root.Declarations[1] is CallableDeclarationASTNode decoder
                && decoder.Name == "decodeAnyValue" && decoder.Modifiers.Contains(Keywords.PRIV)
                && decoder.Body != null);
            TestHarness.CheckTrue("声明[2] 是私有 SerializationGraphContext",
                root.Declarations[2] is ClassDeclarationASTNode graph
                && graph.ClassName == "SerializationGraphContext"
                && graph.Modifiers.Contains(Keywords.PRIV));
            TestHarness.CheckTrue("SerializationBase 位于 core 自举源码且非 rich",
                roots[0].Declarations.OfType<WrapperDeclarationASTNode>().Any(baseW =>
                    baseW.WrapperName == "SerializationBase" && !baseW.Modifiers.Contains(Keywords.RICH)));
            TestHarness.CheckTrue("声明[3] 是 Serializable wrapper",
                root.Declarations[3] is WrapperDeclarationASTNode ser
                && ser.WrapperName == "Serializable");
            TestHarness.CheckTrue("声明[4] 是 Temporary wrapper（一个泛型参数）",
                root.Declarations[4] is WrapperDeclarationASTNode tmp
                && tmp.WrapperName == "Temporary"
                && tmp.GenericParameters?.Parameters.Count == 1);
            TestHarness.CheckTrue("声明[5] 是 class Parcel（@SerializationBase + iterate）",
                root.Declarations[5] is ClassDeclarationASTNode parcel
                && parcel.ClassName == "Parcel"
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init)
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getElement")
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "setElement")
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getDynamic")
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "setDynamic")
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "contains")
                && parcel.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("声明[6] 是 fromParcel",
                root.Declarations[6] is CallableDeclarationASTNode fromP
                && fromP.Name == "fromParcel");
            TestHarness.CheckTrue("声明[7] 是 deepCopy",
                root.Declarations[7] is CallableDeclarationASTNode deep
                && deep.Name == "deepCopy");

            // b4-2 动态访问面：Parcel 动态三面（类内成员，非顶层）+ 文件末尾
            // 的 SB 表示校验解析器（§4.6.1 / D3，顶层声明序锁定）。
            TestHarness.CheckTrue("声明[8..11] 是字段名 BMP 分类四表",
                root.Declarations.Skip(8).Take(4)
                    .OfType<VariableDeclarationASTNode>().Select(d => d.Name)
                    .SequenceEqual(new[] {
                        "identifierLetterStarts", "identifierLetterEnds",
                        "identifierDigitStarts", "identifierDigitEnds" }));
            TestHarness.CheckTrue("声明[12] 是 isLegalParcelFieldKey",
                root.Declarations[12] is CallableDeclarationASTNode legal
                && legal.Name == "isLegalParcelFieldKey"
                && legal.Modifiers.Contains(Keywords.PRIV));
            TestHarness.CheckTrue("声明[13] 是固定 BMP 查表函数",
                root.Declarations[13] is CallableDeclarationASTNode ranges
                && ranges.Name == "identifierInRanges"
                && ranges.Modifiers.Contains(Keywords.PRIV));
            TestHarness.CheckTrue("声明[14] 是 isSbRepresentable",
                root.Declarations[14] is CallableDeclarationASTNode sbRep
                && sbRep.Name == "isSbRepresentable"
                && sbRep.Modifiers.Contains(Keywords.PRIV));
            var former = root.Declarations.Skip(5).ToArray();
            TestHarness.CheckTrue("声明[15..19] 是 SB 名解析器助手",
                former[10] is CallableDeclarationASTNode b10
                && b10.Name == "sbNameByte"
                && former[11] is CallableDeclarationASTNode b11
                && b11.Name == "sbTokenEquals"
                && former[12] is CallableDeclarationASTNode b12
                && b12.Name == "isSbScalarToken"
                && former[13] is CallableDeclarationASTNode b13
                && b13.Name == "parseSbType"
                && former[14] is CallableDeclarationASTNode b14
                && b14.Name == "parseSbWrapped");
            // b4-2 B 面：格式层最小动态面（擦除 SB 视图）声明序锁定
            //（实现由序列化合成器填充，仿 decodeAnyValue 先例）。
            TestHarness.CheckTrue("声明[15..23] 是 sb 视图函数",
                former[15] is CallableDeclarationASTNode v15
                && v15.Name == "sbKind"
                && former[16] is CallableDeclarationASTNode v16
                && v16.Name == "sbLength"
                && former[17] is CallableDeclarationASTNode v17
                && v17.Name == "sbElementAt"
                && former[18] is CallableDeclarationASTNode v18
                && v18.Name == "sbKeyAt"
                && former[19] is CallableDeclarationASTNode v19
                && v19.Name == "sbValueAt"
                && former[20] is CallableDeclarationASTNode v20
                && v20.Name == "sbTypeName"
                && former[21] is CallableDeclarationASTNode v21
                && v21.Name == "sbBuildArray"
                && former[22] is CallableDeclarationASTNode v22
                && v22.Name == "sbBuildList"
                && former[23] is CallableDeclarationASTNode v23
                && v23.Name == "sbBuildMap");

            // b4-3 严格恢复契约（§4.6.3 / D3）：类型/字段集合不匹配的统一
            // 异常，追加在文件末尾（既有顶层声明序不变）。
            TestHarness.CheckTrue("声明[24] 是 SerializationException",
                former[24] is ClassDeclarationASTNode strictEx
                && strictEx.ClassName == "SerializationException");

            // b5-1a 通用字段反射（§4.6.3「反射与实现边界」）：FieldInfo /
            // EnumCaseInfo 元信息 DTO + typeNameOf / isSerializable /
            // fieldsOf / casesOf 查询面（体由序列化合成器填充，追加在
            // 文件末尾，既有顶层声明序不变）；b5-1b 追加 typeNameOf\<T>()
            // 无参重载（与 Type\<T> 值形态同一数据源）。
            TestHarness.CheckTrue("声明[25] 是 struct FieldInfo",
                former[25] is StructDeclarationASTNode fieldInfo
                && fieldInfo.StructName == "FieldInfo"
                && fieldInfo.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init));
            TestHarness.CheckTrue("声明[26] 是 class EnumCaseInfo",
                former[26] is ClassDeclarationASTNode caseInfo
                && caseInfo.ClassName == "EnumCaseInfo"
                && caseInfo.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init));
            TestHarness.CheckTrue("声明[27..33] 是反射查询函数",
                former[27] is CallableDeclarationASTNode r27
                && r27.Name == "typeNameOf"
                && former[28] is CallableDeclarationASTNode r28
                && r28.Name == "typeNameOf"
                && former[29] is CallableDeclarationASTNode r29
                && r29.Name == "isSerializable"
                && former[30] is CallableDeclarationASTNode r30
                && r30.Name == "fieldsOf"
                && former[31] is CallableDeclarationASTNode r31
                && r31.Name == "fieldsOf"
                && former[32] is CallableDeclarationASTNode r32
                && r32.Name == "casesOf"
                && former[33] is CallableDeclarationASTNode r33
                && r33.Name == "casesOf");
            // b5-2c 按名反射重载（§4.7.1 递归契约）：fieldsOf/casesOf/
            // isSerializable 的 String 形态，与泛型/Type\<T> 值形态同一
            // 候选集（体由序列化合成器经同一分发路径填充）。
            TestHarness.CheckTrue("声明[34..36] 是按名反射重载",
                former[34] is CallableDeclarationASTNode r34
                && r34.Name == "fieldsOf"
                && former[35] is CallableDeclarationASTNode r35
                && r35.Name == "casesOf"
                && former[36] is CallableDeclarationASTNode r36
                && r36.Name == "isSerializable");

            TestHarness.Blank();
        }

        // ===== 2i. messaging 结构（MW11d-C：namespace core.messaging）=====
        private static void TestMessagingStructure()
        {
            TestHarness.Section("Structure: namespace core.messaging");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 10)
            {
                TestHarness.CheckTrue("ParseAll 至少 10 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/messaging.rg");

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.messaging",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.messaging");
            var classes = root.Declarations.OfType<ClassDeclarationASTNode>().ToArray();
            TestHarness.CheckTrue("旧 Queue handle 与 Endpoint 已删除",
                !classes.Any(c => c.ClassName is "QueueHandle" or "QueueEndpoint")
                && !root.Declarations.OfType<EnumStructDeclarationASTNode>()
                    .Any(e => e.EnumName == "QueueHandleType"));
            TestHarness.CheckTrue("MessageQueue 私有对象封装全部 MQ 操作",
                classes.Any(c => c.ClassName == "MessageQueue" && c.Modifiers.Contains(Keywords.PRIV)
                    && new[] { "post", "next", "createReader", "closeReader", "close" }.All(name =>
                        c.Members.OfType<CallableDeclarationASTNode>()
                            .Any(m => m.Name == name && !m.Modifiers.Contains(Keywords.STATIC)))));
            TestHarness.CheckTrue("MQ 消息泛型显式声明 shared",
                classes.Where(c => c.GenericParameters?.Parameters.Count > 0)
                    .All(c => c.GenericParameters!.Parameters.All(p => p.RequiresSharedSafe)));

            TestHarness.Blank();
        }

        // ===== 2j. set 结构（施工块 1-4：namespace core.collections +
        // class Set + priv class SetEnumerator，§4.2.2/§4.2.4/§4.2.5）=====
        private static void TestSetStructure()
        {
            TestHarness.Section("Structure: namespace core.collections Set");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 16)
            {
                TestHarness.CheckTrue("ParseAll 至少 16 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/set.rg");

            // 顶层：namespace + class Set + priv class SetEnumerator（共 3 个声明）
            TestHarness.CheckTrue("顶层恰好 3 个声明（namespace + class Set + class SetEnumerator）",
                root.Declarations.Count == 3, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 3) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.collections",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.collections");

            TestHarness.CheckTrue("声明[1] 是 class Set（一个泛型参数，含 init/add/remove/" +
                "contains/clear/count/iterate override）",
                root.Declarations[1] is ClassDeclarationASTNode setCls
                && setCls.ClassName == "Set"
                && setCls.GenericParameters?.Parameters.Count == 1
                && setCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init)
                && setCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "add")
                && setCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "remove")
                && setCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "contains")
                && setCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "clear")
                && setCls.Members.OfType<VariableDeclarationASTNode>()
                    .Any(v => v.Name == "count" && v.Getter != null && v.Setter == null)
                && setCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            // §4.2.5：Set 不属于 SB、也不直接提供 Serializable——类头不得带注解
            TestHarness.CheckTrue("Set 类头无任何注解（§4.2.5 非 SB/Serializable）",
                root.Declarations[1] is ClassDeclarationASTNode setAnno
                && setAnno.Annotations.Count == 0,
                root.Declarations[1] is ClassDeclarationASTNode annoOf
                    ? $"实际 {annoOf.Annotations.Count} 个注解" : "<非类>");

            TestHarness.CheckTrue("声明[2] 是 priv class SetEnumerator（一个泛型参数）",
                root.Declarations[2] is ClassDeclarationASTNode setEnum
                && setEnum.ClassName == "SetEnumerator"
                && setEnum.GenericParameters?.Parameters.Count == 1
                && setEnum.Modifiers.Contains(Keywords.PRIV));

            TestHarness.Blank();
        }

        // ===== 2k. queue 结构（施工块 1-5：namespace core.collections +
        // class Queue + priv class QueueEnumerator，§4.2.2/§4.2.4/§4.2.5）=====
        private static void TestQueueStructure()
        {
            TestHarness.Section("Structure: namespace core.collections Queue");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 17)
            {
                TestHarness.CheckTrue("ParseAll 至少 17 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/queue.rg");

            // 顶层：namespace + class Queue + priv class QueueEnumerator（共 3 个声明）
            TestHarness.CheckTrue("顶层恰好 3 个声明（namespace + class Queue + class QueueEnumerator）",
                root.Declarations.Count == 3, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 3) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.collections",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.collections");

            TestHarness.CheckTrue("声明[1] 是 class Queue（一个泛型参数，含 init/enqueue/dequeue/" +
                "peek/tryDequeue/tryPeek/clear/count/iterate override）",
                root.Declarations[1] is ClassDeclarationASTNode queueCls
                && queueCls.ClassName == "Queue"
                && queueCls.GenericParameters?.Parameters.Count == 1
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init)
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "enqueue")
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "dequeue")
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "peek")
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "tryDequeue")
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "tryPeek")
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "clear")
                && queueCls.Members.OfType<VariableDeclarationASTNode>()
                    .Any(v => v.Name == "count" && v.Getter != null && v.Setter == null)
                && queueCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            // §4.2.5：Queue 不属于 SB、也不直接提供 Serializable——类头不得带注解
            TestHarness.CheckTrue("Queue 类头无任何注解（§4.2.5 非 SB/Serializable）",
                root.Declarations[1] is ClassDeclarationASTNode queueAnno
                && queueAnno.Annotations.Count == 0,
                root.Declarations[1] is ClassDeclarationASTNode annoOfQ
                    ? $"实际 {annoOfQ.Annotations.Count} 个注解" : "<非类>");

            TestHarness.CheckTrue("声明[2] 是 priv class QueueEnumerator（一个泛型参数）",
                root.Declarations[2] is ClassDeclarationASTNode queueEnum
                && queueEnum.ClassName == "QueueEnumerator"
                && queueEnum.GenericParameters?.Parameters.Count == 1
                && queueEnum.Modifiers.Contains(Keywords.PRIV));

            TestHarness.Blank();
        }

        // ===== 2m. io/stream 结构（施工块 2-1：namespace core.io 流核心
        // 抽象，§4.4/D1：EndOfStreamException + StreamWrapperOwnership +
        // ISeekableStream + InputStream/OutputStream 双抽象基类）=====
        private static void TestIoStreamStructure()
        {
            TestHarness.Section("Structure: namespace core.io Stream");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 20)
            {
                TestHarness.CheckTrue("ParseAll 至少 20 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/io/stream.rg");

            // 顶层：namespace + EndOfStreamException + StreamWrapperOwnership
            // + ISeekableStream + InputStream + OutputStream（共 6 个声明）
            TestHarness.CheckTrue("顶层恰好 6 个声明（namespace + 异常 + 枚举 + 接口 + 2 抽象类）",
                root.Declarations.Count == 6, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 6) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.io",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.io");

            // 声明[1]：EndOfStreamException : core.IOException——携带请求/
            // 实际已读字节数（requested/actual 字段）与单 init
            TestHarness.CheckTrue("声明[1] 是 class EndOfStreamException（继承 IOException，" +
                "requested/actual 字段 + init）",
                root.Declarations[1] is ClassDeclarationASTNode eos
                && eos.ClassName == "EndOfStreamException"
                && eos.BaseClass != null
                && AstDescribe.Type(eos.BaseClass).Contains("IOException")
                && eos.Members.OfType<VariableDeclarationASTNode>()
                    .Any(v => v.Name == "requested")
                && eos.Members.OfType<VariableDeclarationASTNode>()
                    .Any(v => v.Name == "actual")
                && eos.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Kind == CallableKind.Init));

            // 声明[2]：StreamWrapperOwnership 无载荷 enum struct（Borrowed/Owned）
            TestHarness.CheckTrue("声明[2] 是 enum struct StreamWrapperOwnership（两 case）",
                root.Declarations[2] is EnumStructDeclarationASTNode ownership
                && ownership.EnumName == "StreamWrapperOwnership"
                && ownership.Cases.Select(c => c.CaseName).SequenceEqual(
                    new[] { "Borrowed", "Owned" }));

            // 声明[3]：ISeekableStream 仅 seek/getPosition 双成员（无体）
            TestHarness.CheckTrue("声明[3] 是 interface ISeekableStream（seek/getPosition 无体）",
                root.Declarations[3] is InterfaceDeclarationASTNode seekable
                && seekable.InterfaceName == "ISeekableStream"
                && seekable.Members.Count == 2
                && seekable.Members.All(m => m is CallableDeclarationASTNode { Body: null })
                && seekable.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "seek")
                && seekable.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "getPosition"));

            // 声明[4]：abstract class InputStream implements core.IDisposable——
            // 抽象面 read/disposeCore；dispose 幂等支架非抽象有体；便利面
            // readExactly（两重载）/readByte/pipe
            var inputCls = root.Declarations[4] as ClassDeclarationASTNode;
            TestHarness.CheckTrue("声明[4] 是 abstract class InputStream（实现 core.IDisposable）",
                inputCls != null && inputCls.ClassName == "InputStream"
                && inputCls.Modifiers.Contains(Keywords.ABSTRACT)
                && inputCls.Interfaces.Any(t => AstDescribe.Type(t).Contains("IDisposable")));
            if (inputCls != null)
            {
                var inputCallables = inputCls.Members.OfType<CallableDeclarationASTNode>().ToList();
                TestHarness.CheckTrue("InputStream 抽象面：三参 read 与 disposeCore 均 abstract",
                    inputCallables.Any(m => m.Name == "read"
                        && m.Modifiers.Contains(Keywords.ABSTRACT)
                        && m.Parameters.Parameters.Count == 3)
                    && inputCallables.Any(m => m.Name == "disposeCore"
                        && m.Modifiers.Contains(Keywords.ABSTRACT)));
                TestHarness.CheckTrue("InputStream dispose 幂等支架（非抽象、有体）",
                    inputCallables.Any(m => m.Name == "dispose"
                        && !m.Modifiers.Contains(Keywords.ABSTRACT)
                        && m.Body != null));
                TestHarness.CheckTrue("InputStream 便利面：readExactly 双重载 + readByte + pipe + " +
                    "readAll 双重载（施工块 2-2）",
                    inputCallables.Count(m => m.Name == "readExactly") == 2
                    && inputCallables.Any(m => m.Name == "readByte")
                    && inputCallables.Any(m => m.Name == "pipe")
                    && inputCallables.Count(m => m.Name == "readAll") == 2);
            }

            // 声明[5]：abstract class OutputStream implements core.IDisposable——
            // 抽象面 write/flush/disposeCore；dispose 支架非抽象；便利面
            // write 重载/writeByte
            var outputCls = root.Declarations[5] as ClassDeclarationASTNode;
            TestHarness.CheckTrue("声明[5] 是 abstract class OutputStream（实现 core.IDisposable）",
                outputCls != null && outputCls.ClassName == "OutputStream"
                && outputCls.Modifiers.Contains(Keywords.ABSTRACT)
                && outputCls.Interfaces.Any(t => AstDescribe.Type(t).Contains("IDisposable")));
            if (outputCls != null)
            {
                var outputCallables = outputCls.Members.OfType<CallableDeclarationASTNode>().ToList();
                TestHarness.CheckTrue("OutputStream 抽象面：write/flush/disposeCore 均 abstract",
                    outputCallables.Any(m => m.Name == "write"
                        && m.Modifiers.Contains(Keywords.ABSTRACT)
                        && m.Parameters.Parameters.Count == 3)
                    && outputCallables.Any(m => m.Name == "flush"
                        && m.Modifiers.Contains(Keywords.ABSTRACT))
                    && outputCallables.Any(m => m.Name == "disposeCore"
                        && m.Modifiers.Contains(Keywords.ABSTRACT)));
                TestHarness.CheckTrue("OutputStream dispose 支架（非抽象有体）+ 便利面 write 重载/writeByte",
                    outputCallables.Any(m => m.Name == "dispose"
                        && !m.Modifiers.Contains(Keywords.ABSTRACT)
                        && m.Body != null)
                    && outputCallables.Count(m => m.Name == "write") == 2
                    && outputCallables.Any(m => m.Name == "writeByte"));
            }

            TestHarness.Blank();
        }

        // ===== 2o. io/stdstreams 结构（施工块 2-4b1 标准输出/错误 + 2-4b2
        // 标准输入：namespace core.io 标准流，§4.4 标准流与控制台契约：
        // StandardStreams 三 static 工厂 + StdOutputStream/StdErrorStream
        // priv 借用包装对象 + StdinWake 一次性唤醒事件（priv shared，
        // 继承 EventAlarm）+ StdInputStream priv 借用包装对象，均不实现
        // ISeekableStream——标准流不可定位）=====
        private static void TestIoStdStreamsStructure()
        {
            TestHarness.Section("Structure: namespace core.io StandardStreams");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 15)
            {
                TestHarness.CheckTrue("ParseAll 至少 15 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/io/stdstreams.rg");

            // 顶层：namespace + StandardStreams + StdOutputStream +
            // StdErrorStream + StdinWake + StdInputStream（共 6 个声明）
            TestHarness.CheckTrue("顶层恰好 6 个声明（namespace + 工厂 + 2 输出包装类" +
                " + 唤醒事件 + 输入包装类）",
                root.Declarations.Count == 6, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 6) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.io",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.io");

            // 声明[1]：StandardStreams 工厂——standardOutput/standardError/
            // standardInput 三 pub static 有体工厂（非 native）
            TestHarness.CheckTrue("声明[1] 是 class StandardStreams（三 pub static 工厂）",
                root.Declarations[1] is ClassDeclarationASTNode factory
                && factory.ClassName == "StandardStreams"
                && factory.Modifiers.Contains(Keywords.PUB)
                && factory.Members.OfType<CallableDeclarationASTNode>().Count(m =>
                    (m.Name == "standardOutput" || m.Name == "standardError"
                        || m.Name == "standardInput")
                    && m.Modifiers.Contains(Keywords.STATIC)
                    && !m.Modifiers.Contains(Keywords.NATIVE)
                    && m.Body != null) == 3);

            // 声明[2]/[3]：StdOutputStream/StdErrorStream priv 包装类
            CheckStdStreamClass(root.Declarations[2], "StdOutputStream",
                "stdoutWrite", "stdoutFlush");
            CheckStdStreamClass(root.Declarations[3], "StdErrorStream",
                "stderrWrite", "stderrFlush");

            // 声明[4]（2-4b2）：StdinWake 一次性唤醒事件——priv shared、
            // 继承 core.coroutine.EventAlarm、无成员（signal 即终态，
            // 触发来自宿主后台读线程，本类只承担可 yield 的事件身份）
            var wakeOk = root.Declarations[4] is ClassDeclarationASTNode wake
                && wake.ClassName == "StdinWake"
                && wake.Modifiers.Contains(Keywords.PRIV)
                && wake.Modifiers.Contains(Keywords.SHARED)
                && wake.BaseClass != null
                && AstDescribe.Type(wake.BaseClass).Contains("EventAlarm")
                && wake.Members.Count == 0;
            TestHarness.CheckTrue("声明[4] 是 priv shared class StdinWake（继承 EventAlarm，无成员）",
                wakeOk,
                root.Declarations[4] is ClassDeclarationASTNode w
                    ? $"成员 {w.Members.Count}" : "形状不符");

            // 声明[5]（2-4b2）：StdInputStream priv 包装类——形状见
            // CheckStdInputStreamClass
            CheckStdInputStreamClass(root.Declarations[5]);

            TestHarness.Blank();
        }

        // 标准流包装类形状：priv、继承 OutputStream、恰好 5 个 callable
        //（write/flush/disposeCore 三 override + 写/刷双 native 面——
        // static、无体、@NativeLibrary/@NativeSymbol 双注解）
        private static void CheckStdStreamClass(ASTNode node, string className,
            string writeNative, string flushNative)
        {
            TestHarness.CheckTrue($"声明是 priv class {className}（继承 OutputStream）",
                node is ClassDeclarationASTNode cls
                && cls.ClassName == className
                && cls.Modifiers.Contains(Keywords.PRIV)
                && cls.BaseClass != null
                && AstDescribe.Type(cls.BaseClass).Contains("OutputStream"));
            if (node is not ClassDeclarationASTNode stream) { return; }

            var callables = stream.Members.OfType<CallableDeclarationASTNode>().ToList();
            TestHarness.CheckTrue($"{className} 恰好 5 个 callable 成员",
                stream.Members.Count == 5 && callables.Count == 5,
                $"实际 {stream.Members.Count}");
            TestHarness.CheckTrue($"{className} write/flush/disposeCore 均 override",
                callables.Any(m => m.Name == "write"
                    && m.Modifiers.Contains(Keywords.OVERRIDE))
                && callables.Any(m => m.Name == "flush"
                    && m.Modifiers.Contains(Keywords.OVERRIDE))
                && callables.Any(m => m.Name == "disposeCore"
                    && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue($"{className} native 面是 {writeNative}/{flushNative}" +
                "（static、无体、双注解）",
                callables.Count(m => (m.Name == writeNative || m.Name == flushNative)
                    && m.Modifiers.Contains(Keywords.NATIVE)
                    && m.Modifiers.Contains(Keywords.STATIC)
                    && m.Body == null
                    && m.Annotations.Count == 2) == 2);
        }

        // 标准输入包装类形状（2-4b2）：priv、继承 InputStream、恰好 4 个
        // callable（read/disposeCore 两 override + 启动即返双 native 面
        // stdinReadStart/stdinReadTake——static、无体、@NativeLibrary/
        // @NativeSymbol 双注解）
        private static void CheckStdInputStreamClass(ASTNode node)
        {
            TestHarness.CheckTrue("声明是 priv class StdInputStream（继承 InputStream）",
                node is ClassDeclarationASTNode cls
                && cls.ClassName == "StdInputStream"
                && cls.Modifiers.Contains(Keywords.PRIV)
                && cls.BaseClass != null
                && AstDescribe.Type(cls.BaseClass).Contains("InputStream"));
            if (node is not ClassDeclarationASTNode stream) { return; }

            var callables = stream.Members.OfType<CallableDeclarationASTNode>().ToList();
            TestHarness.CheckTrue("StdInputStream 恰好 4 个 callable 成员",
                stream.Members.Count == 4 && callables.Count == 4,
                $"实际 {stream.Members.Count}");
            TestHarness.CheckTrue("StdInputStream read/disposeCore 均 override",
                callables.Any(m => m.Name == "read"
                    && m.Modifiers.Contains(Keywords.OVERRIDE))
                && callables.Any(m => m.Name == "disposeCore"
                    && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("StdInputStream native 面是 stdinReadStart/" +
                "stdinReadTake（static、无体、双注解）",
                callables.Count(m => (m.Name == "stdinReadStart" || m.Name == "stdinReadTake")
                    && m.Modifiers.Contains(Keywords.NATIVE)
                    && m.Modifiers.Contains(Keywords.STATIC)
                    && m.Body == null
                    && m.Annotations.Count == 2) == 2);
        }

        // ===== 2l. adapters 结构（施工块 1-6：namespace core.collections +
        // 2 个 pub func asEnumerable 重载 + 3 个 priv 借用枚举/可枚举类，
        // §4.2.1 第二段/§4.2.4 末段）=====
        private static void TestAdaptersStructure()
        {
            TestHarness.Section("Structure: namespace core.collections Adapters");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 18)
            {
                TestHarness.CheckTrue("ParseAll 至少 18 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/adapters.rg");

            // 顶层：namespace + 2 个 asEnumerable 重载（Array/Span 形参）+
            // ArrayEnumerable/SpanEnumerable/SpanEnumerator（共 6 个声明）
            TestHarness.CheckTrue("顶层恰好 6 个声明（namespace + 2 个 asEnumerable 重载 + " +
                "3 个 priv 类）",
                root.Declarations.Count == 6, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 6) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.collections",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.collections");

            // 声明[1]/[2] 是同名重载 asEnumerable（各 1 个泛型参数、有体）；
            // Span 版（声明[2]）泛型参数带 ValueType 约束（§4.2.1）
            TestHarness.CheckTrue("声明[1] 是 pub func asEnumerable（Array 形参，1 泛型参数）",
                root.Declarations[1] is CallableDeclarationASTNode arrFn
                && arrFn.Name == "asEnumerable"
                && arrFn.GenericParameters?.Parameters.Count == 1
                && arrFn.Body != null
                && arrFn.Modifiers.Contains(Keywords.PUB));
            TestHarness.CheckTrue("声明[2] 是 pub func asEnumerable 重载（Span 形参，" +
                "1 泛型参数带 ValueType 约束）",
                root.Declarations[2] is CallableDeclarationASTNode spanFn
                && spanFn.Name == "asEnumerable"
                && spanFn.GenericParameters?.Parameters.Count == 1
                && AstDescribe.Generics(spanFn.GenericParameters) == "\\<T, T extends ValueType>"
                && spanFn.Body != null
                && spanFn.Modifiers.Contains(Keywords.PUB));

            // 声明[3]/[4]/[5] 是 priv 借用类（各 1 个泛型参数；类头无序列化
            // 注解——适配器是借用视图，§4.2.4 末段不加修改计数）
            TestHarness.CheckTrue("声明[3] 是 priv class ArrayEnumerable（1 泛型参数，" +
                "类头无注解，含 iterate override）",
                root.Declarations[3] is ClassDeclarationASTNode arrCls
                && arrCls.ClassName == "ArrayEnumerable"
                && arrCls.GenericParameters?.Parameters.Count == 1
                && arrCls.Modifiers.Contains(Keywords.PRIV)
                && arrCls.Annotations.Count == 0
                && arrCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("声明[4] 是 priv class SpanEnumerable（1 泛型参数带 " +
                "ValueType 约束，类头无注解，含 iterate override）",
                root.Declarations[4] is ClassDeclarationASTNode spanCls
                && spanCls.ClassName == "SpanEnumerable"
                && spanCls.GenericParameters?.Parameters.Count == 1
                && AstDescribe.Generics(spanCls.GenericParameters) == "\\<T, T extends ValueType>"
                && spanCls.Modifiers.Contains(Keywords.PRIV)
                && spanCls.Annotations.Count == 0
                && spanCls.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("声明[5] 是 priv class SpanEnumerator（1 泛型参数带 " +
                "ValueType 约束，类头无注解，含 moveNext/current）",
                root.Declarations[5] is ClassDeclarationASTNode spanEnum
                && spanEnum.ClassName == "SpanEnumerator"
                && spanEnum.GenericParameters?.Parameters.Count == 1
                && AstDescribe.Generics(spanEnum.GenericParameters) == "\\<T, T extends ValueType>"
                && spanEnum.Modifiers.Contains(Keywords.PRIV)
                && spanEnum.Annotations.Count == 0
                && spanEnum.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "moveNext")
                && spanEnum.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "current"));

            TestHarness.Blank();
        }

        // ===== 2n. algorithms 结构：namespace core.collections 顶层算法函数 =====
        private static void TestAlgorithmsStructure()
        {
            TestHarness.Section("Structure: namespace core.collections Algorithms");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 19)
            {
                TestHarness.CheckTrue("ParseAll 至少 19 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots.Single(r => r.Span?.sourceName == "<stdlib>/core/algorithms.rg");

            // 顶层：namespace + 26 个 func（原 10 通用算法 + 2 排序 + 11
            // compare 重载（十标量 + String）+ 3 priv 归并助手，共 27 个
            // 声明；§4.2.1 第一段：通用算法是顶层泛型函数，不注入扩展成员
            // ——无任何类/接口声明；String 重载为 §4.3.2 标量字典序实现）
            TestHarness.CheckTrue("顶层恰好 27 个声明（namespace + 26 个算法/比较函数）",
                root.Declarations.Count == 27, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 27) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.collections",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.collections");

            // 声明[1..26] 按文件序：原 10 通用算法（map/filter/fold/any/all/
            // contains/find/findIndex/toArray/toList）、排序两函数（sorted/
            // sortInPlace，各 1 个泛型参数）、compare 十一型重载（i8..u64/
            // float/double/String，非泛型：泛型参数 0 个）、3 个 priv 归并
            // 助手（各 1 个泛型参数）。既有/排序/比较函数 pub、有体（立即
            // 求值实现）；助手 priv。Func 首参是返回类型。
            var expected = new[]
            {
                ("map", 2, true), ("filter", 1, true), ("fold", 2, true),
                ("any", 1, true), ("all", 1, true), ("contains", 1, true),
                ("find", 1, true), ("findIndex", 1, true), ("toArray", 1, true),
                ("toList", 1, true),
                ("sorted", 1, true), ("sortInPlace", 1, true),
                ("compare", 0, true), ("compare", 0, true), ("compare", 0, true),
                ("compare", 0, true), ("compare", 0, true), ("compare", 0, true),
                ("compare", 0, true), ("compare", 0, true), ("compare", 0, true),
                ("compare", 0, true), ("compare", 0, true),
                ("mergeRun", 1, false), ("mergeSortBuffer", 1, false),
                ("notAfter", 1, false)
            };
            var funcs = root.Declarations.Skip(1)
                .OfType<CallableDeclarationASTNode>().ToList();
            TestHarness.CheckTrue("声明[1..26] 按序是原 10 算法 + sorted/sortInPlace + " +
                "compare×11 + 归并助手×3（pub/priv、有体、泛型参数个数吻合）",
                funcs.Count == expected.Length
                && funcs.Zip(expected, (f, e) => (f, e)).All(p =>
                    p.f.Name == p.e.Item1
                    && (p.f.GenericParameters?.Parameters.Count ?? 0) == p.e.Item2
                    && p.f.Body != null
                    && p.f.Modifiers.Contains(Keywords.PUB) == p.e.Item3));

            TestHarness.Blank();
        }

        // ===== 3. Console 描述串精确比对 =====
        private static void TestConsoleDescribe()
        {
            TestHarness.Section("AstDescribe Snapshot (Console)");

            var roots = StdlibSources.ParseAll();
            if (roots.Count < 2)
            {
                TestHarness.CheckTrue("ParseAll 至少 2 棵（快照前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }

            TestHarness.Check("Console Root 描述串", AstDescribe.Root(roots.Single(
                r => r.Span?.sourceName == "<stdlib>/core/Console.rg")),
                "namespace core.io; pub class Console {" +
                @"@NativeLibrary(Str(""rigi_rt"")) @NativeSymbol(Str(""print"")) priv static native func print(text: String), " +
                @"@NativeLibrary(Str(""rigi_rt"")) @NativeSymbol(Str(""printErr"")) priv static native func printErr(text: String), " +
                "pub static func println(text: String) {}}");

            TestHarness.Blank();
        }
    }
}
