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
    ///    coroutine（namespace core.coroutine + 15 类型 +
    ///    laneOfExecutor + 29 native 原语 + sleep 包装）；disposable（namespace core +
    ///    IDisposable 接口）；exceptions（namespace core + 5 异常子类）
    /// 3. Console 整棵 Root 的 AstDescribe 描述串精确比对
    /// </summary>
    public static class StdlibSourcesTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();

            TestCountAndSourceName();
            TestBootstrapStructure();
            TestConsoleStructure();
            TestCollectionsStructure();
            TestCoroutineStructure();
            TestDisposableStructure();
            TestExceptionsStructure();
            TestGlobalExceptionsStructure();
            TestTimeStructure();
            TestSerializationStructure();
            TestMessagingStructure();
            TestConsoleDescribe();

            return TestHarness.Summary("StdlibSources");
        }

        // ===== 1. 数量与 sourceName =====
        private static void TestCountAndSourceName()
        {
            TestHarness.Section("ParseAll: Count & SourceName");

            var roots = StdlibSources.ParseAll();
            TestHarness.CheckTrue("ParseAll 返回恰好 13 棵 RootASTNode",
                roots.Count == 13, $"实际 {roots.Count} 棵");
            if (roots.Count < 13) { TestHarness.Blank(); return; }

            // 逻辑名 Ordinal 排序：'.'(0x2E) < 'c'；'C'(0x43) < 'c'(0x63)；
            // collections < coroutine（'l' < 'r'）；d < e < g < m < s < t
            TestHarness.Check("sourceName[0]（点开头文件名反推）",
                roots[0].Span?.sourceName ?? "<null>", "<stdlib>/.bootstrap.rg");
            TestHarness.Check("sourceName[1]",
                roots[1].Span?.sourceName ?? "<null>", "<stdlib>/core/Console.rg");
            TestHarness.Check("sourceName[2]（Atomic）",
                roots[2].Span?.sourceName ?? "<null>", "<stdlib>/core/atomic.rg");
            TestHarness.Check("sourceName[3]（安全 Atomic 容器）",
                roots[3].Span?.sourceName ?? "<null>", "<stdlib>/core/atomic_collections.rg");
            TestHarness.Check("sourceName[10]（Place/Handle）",
                roots[10].Span?.sourceName ?? "<null>", "<stdlib>/core/place.rg");
            TestHarness.Check("sourceName[4]",
                roots[4].Span?.sourceName ?? "<null>", "<stdlib>/core/collections.rg");
            TestHarness.Check("sourceName[5]",
                roots[5].Span?.sourceName ?? "<null>", "<stdlib>/core/coroutine.rg");
            TestHarness.Check("sourceName[6]",
                roots[6].Span?.sourceName ?? "<null>", "<stdlib>/core/disposable.rg");
            TestHarness.Check("sourceName[7]",
                roots[7].Span?.sourceName ?? "<null>", "<stdlib>/core/exceptions.rg");
            TestHarness.Check("sourceName[8]（MW12b 全局异常通道）",
                roots[8].Span?.sourceName ?? "<null>", "<stdlib>/core/global_exceptions.rg");
            TestHarness.Check("sourceName[9]（MW11d-C core.messaging）",
                roots[9].Span?.sourceName ?? "<null>", "<stdlib>/core/messaging.rg");
            TestHarness.Check("sourceName[11]（MW11d core.serialization）",
                roots[11].Span?.sourceName ?? "<null>", "<stdlib>/core/serialization.rg");
            TestHarness.Check("sourceName[12]（MW11c core.time）",
                roots[12].Span?.sourceName ?? "<null>", "<stdlib>/core/time.rg");

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
            TestHarness.CheckTrue("顶层恰好 141 个声明（namespace + ext operator + Pair + ComparisonResult + 134 callable/Cell + String.length + any_hash + any_to_string）",
                root.Declarations.Count == 141, $"实际 {root.Declarations.Count}");
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
            var pair = root.Declarations.Count > 2
                ? root.Declarations[2] as ClassDeclarationASTNode : null;
            TestHarness.CheckTrue("第三声明是 class（core.Pair）", pair != null,
                root.Declarations.Count > 2 ? root.Declarations[2].GetType().Name : "<none>");
            if (pair != null)
            {
                TestHarness.CheckTrue("Pair 是 open 泛型类",
                    pair.Modifiers.Contains(Keywords.OPEN)
                    && pair.GenericParameters?.Parameters.Count == 2);
            }

            // lambda 对象模型基类族与 Cell（SYNTAX §5.2）：元数 0–32 预生成
            var classes = root.Declarations.OfType<ClassDeclarationASTNode>().Skip(1).ToList();
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

            // any_to_string（§3.8 toString 机制修订）：末尾声明，priv 全局
            // native（@NativeLibrary/@NativeSymbol 双注解、无体、参数 Any）
            var anyToString = root.Declarations[root.Declarations.Count - 1]
                as CallableDeclarationASTNode;
            TestHarness.CheckTrue("末声明是 any_to_string（priv native 全局）",
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
            var root = roots[1];

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
            var root = roots[4];

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
            var root = roots[5];

            // MW11c 顶层：namespace + 15 类型（Task/Task\<TReturn\> +
            // TaskState + Executor 族 4 + PollingAlarm/EventAlarm/
            // SleepAlarm + Mutex + Timer + CoroutineLocal + I64Queue/
            // Dispatcher）+ laneOfExecutor 助手 + 32 个 rigi_ native
            // 原语 + sleep Rigi 包装（共 50 个声明）。棒5a：删
            // make_sleep_alarm；增 SleepAlarm/laneOfExecutor 与句柄
            // lane/current、alarm_wait、poll_*、failure_record/drop；
            // 其后增 coro_local_push/pop/get/inherit（§20.2）；
            // L8 增 event_create_sticky/event_signal（用户 EventAlarm
            // 默认底座两面，§19.3）
            TestHarness.CheckTrue("顶层恰好 50 个声明（namespace + 15 类型 + 34 func）",
                root.Declarations.Count == 50, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 50) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.coroutine",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.coroutine");

            TestHarness.CheckTrue("声明[1] 是非泛型 class Task（具体 shared，非 abstract）",
                root.Declarations[1] is ClassDeclarationASTNode task0
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
                root.Declarations[2] is ClassDeclarationASTNode task1
                && task1.ClassName == "Task"
                && task1.GenericParameters?.Parameters.Count == 1
                && task1.Modifiers.Contains(Keywords.SHARED)
                && !task1.Modifiers.Contains(Keywords.ABSTRACT));
            TestHarness.CheckTrue("声明[3] 是 TaskState enum struct（六 case）",
                root.Declarations[3] is EnumStructDeclarationASTNode taskState
                && taskState.EnumName == "TaskState"
                && taskState.Cases.Count == 6
                && taskState.Cases.Select(c => c.CaseName).SequenceEqual(
                    new[] { "Created", "Runnable", "Suspended", "Completed",
                        "Failed", "Cancelled" }));
            TestHarness.CheckTrue("声明[4] 是 class Executor（abstract）",
                root.Declarations[4] is ClassDeclarationASTNode exec
                && exec.ClassName == "Executor"
                && exec.Modifiers.Contains(Keywords.ABSTRACT));
            TestHarness.CheckTrue("声明[5..7] 是三个内置 Executor（pub shared singleton）",
                root.Declarations[5] is ClassDeclarationASTNode mainExec
                && mainExec.ClassName == "MainExecutor"
                && root.Declarations[6] is ClassDeclarationASTNode computeExec
                && computeExec.ClassName == "ComputeExecutor"
                && root.Declarations[7] is ClassDeclarationASTNode ioExec
                && ioExec.ClassName == "IOExecutor"
                && new[] { mainExec, computeExec, ioExec }.All(e =>
                    e.Modifiers.Contains(Keywords.SINGLETON)
                    && e.Modifiers.Contains(Keywords.SHARED)
                    && !e.Modifiers.Contains(Keywords.ABSTRACT)));
            TestHarness.CheckTrue("声明[8] 是 PollingAlarm（abstract，含 isReady 抽象方法）",
                root.Declarations[8] is ClassDeclarationASTNode alarm
                && alarm.ClassName == "PollingAlarm"
                && alarm.Modifiers.Contains(Keywords.ABSTRACT)
                && alarm.Members.Count == 1
                && alarm.Members[0] is CallableDeclarationASTNode ready
                && ready.Name == "isReady"
                && ready.Modifiers.Contains(Keywords.ABSTRACT)
                && ready.Body == null);
            TestHarness.CheckTrue("声明[9] 是 EventAlarm（abstract，L8 增 ensureHandle/signal 底座面）",
                root.Declarations[9] is ClassDeclarationASTNode eventAlarm
                && eventAlarm.ClassName == "EventAlarm"
                && eventAlarm.Modifiers.Contains(Keywords.ABSTRACT)
                && eventAlarm.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "ensureHandle")
                && eventAlarm.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "signal"));
            TestHarness.CheckTrue("声明[10] 是 SleepAlarm : EventAlarm（priv shared）",
                root.Declarations[10] is ClassDeclarationASTNode sleepAlarm
                && sleepAlarm.ClassName == "SleepAlarm"
                && sleepAlarm.Modifiers.Contains(Keywords.PRIV)
                && sleepAlarm.Modifiers.Contains(Keywords.SHARED)
                && sleepAlarm.BaseClass != null);
            TestHarness.CheckTrue("声明[11] 是 Mutex（具体 shared，嵌套 Lock + acquire/release/runSynchronously）",
                root.Declarations[11] is ClassDeclarationASTNode mutex
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
                root.Declarations[12] is ClassDeclarationASTNode timer
                && timer.ClassName == "Timer"
                && timer.Modifiers.Contains(Keywords.SHARED)
                && timer.BaseClass != null
                && timer.Members.OfType<EnumStructDeclarationASTNode>()
                    .Any(nested => nested.EnumName == "RepeatOption"
                        && nested.Cases.Count == 3
                        && nested.Cases.Select(c => c.CaseName).SequenceEqual(
                            new[] { "NoRepeat", "Repeat", "InfiniteRepeat" })));
            TestHarness.CheckTrue("声明[13] 是泛型 class CoroutineLocal（具体 shared，withValue/get）",
                root.Declarations[13] is ClassDeclarationASTNode coroutineLocal
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
            TestHarness.CheckTrue("声明[14] 是 I64Queue（priv 内部环形队列）",
                root.Declarations[14] is ClassDeclarationASTNode i64Queue
                && i64Queue.ClassName == "I64Queue"
                && i64Queue.Modifiers.Contains(Keywords.PRIV));
            TestHarness.CheckTrue("声明[15] 是 Dispatcher（priv shared singleton）",
                root.Declarations[15] is ClassDeclarationASTNode dispatcher
                && dispatcher.ClassName == "Dispatcher"
                && dispatcher.Modifiers.Contains(Keywords.PRIV)
                && dispatcher.Modifiers.Contains(Keywords.SHARED)
                && dispatcher.Modifiers.Contains(Keywords.SINGLETON));
            TestHarness.CheckTrue("声明[16] 是 laneOfExecutor 模块级助手（非 native，有体）",
                root.Declarations[16] is CallableDeclarationASTNode laneOf
                && laneOf.Name == "laneOfExecutor"
                && !laneOf.Modifiers.Contains(Keywords.NATIVE)
                && laneOf.Modifiers.Contains(Keywords.PRIV)
                && laneOf.Body != null);

            // 声明[17..48]：§17.4 native 原语面（rigi_ 前缀，priv native；
            // 棒5a 增 coroutine_current/lane、alarm_wait、poll_*、
            // failure_record/drop；make_sleep_alarm 已删；其后增
            // coro_local_* 四面；L8 增 event_create_sticky/event_signal）
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
                if (root.Declarations[index] is CallableDeclarationASTNode nativeFunc)
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
                        false, root.Declarations[index].GetType().Name);
                }
            }
            TestHarness.CheckTrue("声明[49] 是 sleep Rigi 包装（非 native，有体）",
                root.Declarations[49] is CallableDeclarationASTNode sleep
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
            var root = roots[6];

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
            var root = roots[7];

            // 顶层：namespace + RuntimeException/IOException/CastException/
            // NoSuchMethodException/DividedByZeroException/OutOfBoundException/
            // IllegalStateException/NoSuchElementException 8 个 open class
            // （共 10 个声明；MW9b 增 OutOfBoundException，MW11c 增
            // IllegalStateException，MW11d-B1 增 NoSuchElementException）
            TestHarness.CheckTrue("顶层恰好 11 个声明（namespace + Exception + 9 class）",
                root.Declarations.Count == 11, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 11) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core");

            string[] expected = { "Exception", "RuntimeException", "ImmutablePlaceException", "IOException", "CastException",
                "NoSuchMethodException", "DividedByZeroException", "OutOfBoundException",
                "IllegalStateException", "NoSuchElementException" };
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
                            .FirstOrDefault(m => m.Kind == CallableKind.Init);
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

        // ===== 2g. time 结构（MW11c：namespace core.time + 3 struct + native）=====
        // ===== 2g. global_exceptions 结构（MW12b：namespace core +
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
            var root = roots[8];

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
            if (roots.Count < 13)
            {
                TestHarness.CheckTrue("ParseAll 至少 13 棵（结构断言前置）", false,
                    $"实际 {roots.Count} 棵");
                TestHarness.Blank();
                return;
            }
            var root = roots[12];

            // 顶层：namespace + TimeStamp/TimeSpan/DateTime 3 个 struct
            // + rigi_time_now native（共 5 个声明，RUNTIME §19.7/§17.4）
            TestHarness.CheckTrue("顶层恰好 5 个声明（namespace + 3 struct + native）",
                root.Declarations.Count == 5, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 5) { TestHarness.Blank(); return; }

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
            TestHarness.CheckTrue("声明[3] 是 DateTime struct（now minus compareTo equals）",
                root.Declarations[3] is StructDeclarationASTNode dateTime
                && dateTime.StructName == "DateTime"
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "now" && m.Modifiers.Contains(Keywords.STATIC))
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "minus")
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "compareTo")
                && dateTime.Members.OfType<CallableDeclarationASTNode>()
                    .Any(m => m.Name == "equals"));
            var timeNow = root.Declarations[4] as CallableDeclarationASTNode;
            TestHarness.CheckTrue("声明[4] 是 rigi_time_now priv native（返回 i64）",
                timeNow != null
                && timeNow.Name == "rigi_time_now"
                && timeNow.Modifiers.Contains(Keywords.NATIVE)
                && timeNow.Modifiers.Contains(Keywords.PRIV)
                && timeNow.Body == null
                && timeNow.ReturnType != null);
            // @NativeSymbol("time_now") 必带（coroutine.rg 同符号声明同
            // 口径）：缺省符号经 rigi_rt 前缀拼接落空成 rigi_rigi_time_now
            TestHarness.CheckTrue("声明[4] 带 @NativeLibrary/@NativeSymbol 双注解",
                timeNow != null && timeNow.Annotations.Count == 2
                && AstDescribe.Symbol(timeNow.Annotations[0].Name.symbol) == "NativeLibrary"
                && AstDescribe.Symbol(timeNow.Annotations[1].Name.symbol) == "NativeSymbol",
                timeNow == null ? "<none>" : $"注解数 {timeNow.Annotations.Count}");
            TestHarness.CheckTrue("声明[4] NativeSymbol 实参是 time_now",
                timeNow != null && timeNow.Annotations.Count == 2
                && timeNow.Annotations[1].Arguments.Count == 1
                && AstDescribe.Expr(timeNow.Annotations[1].Arguments[0].Value.Expression)
                    == "Str(\"time_now\")",
                timeNow == null || timeNow.Annotations.Count < 1
                    ? "<none>"
                    : AstDescribe.Expr(timeNow.Annotations[1].Arguments[0].Value.Expression));

            TestHarness.Blank();
        }

        // ===== 2h. serialization 结构（MW11d：namespace core.serialization + 3 wrapper）=====
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
            var root = roots[11];

            TestHarness.CheckTrue("顶层恰好 8 个声明（namespace + 私有上下文 + 3 wrapper + Parcel + fromParcel + deepCopy）",
                root.Declarations.Count == 8, $"实际 {root.Declarations.Count}");
            if (root.Declarations.Count < 8) { TestHarness.Blank(); return; }

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.serialization",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.serialization");

            TestHarness.CheckTrue("声明[1] 是私有 SerializationGraphContext",
                root.Declarations[1] is ClassDeclarationASTNode graph
                && graph.ClassName == "SerializationGraphContext"
                && graph.Modifiers.Contains(Keywords.PRIV));
            TestHarness.CheckTrue("声明[2] 是 SerializationBase wrapper",
                root.Declarations[2] is WrapperDeclarationASTNode baseW
                && baseW.WrapperName == "SerializationBase");
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
                    .Any(m => m.Name == "iterate" && m.Modifiers.Contains(Keywords.OVERRIDE)));
            TestHarness.CheckTrue("声明[6] 是 fromParcel",
                root.Declarations[6] is CallableDeclarationASTNode fromP
                && fromP.Name == "fromParcel");
            TestHarness.CheckTrue("声明[7] 是 deepCopy",
                root.Declarations[7] is CallableDeclarationASTNode deep
                && deep.Name == "deepCopy");

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
            var root = roots[9];

            var ns = root.Declarations[0] as NamespaceDeclarationASTNode;
            TestHarness.CheckTrue("首声明是 namespace core.messaging",
                ns != null && AstDescribe.Symbol(ns.Name.symbol) == "core.messaging");
            TestHarness.CheckTrue("含 QueueHandleType enum struct（三 case）",
                root.Declarations.OfType<EnumStructDeclarationASTNode>()
                    .Any(e => e.EnumName == "QueueHandleType" && e.Cases.Count == 3
                        && e.Cases.Select(c => c.CaseName).SequenceEqual(
                            new[] { "Reader", "Owner", "Sender" })));
            // MW11d-C：QueueHandle/QueueItem 落地为 shared class——设计
            // 定型于 native 尚无泛型值类型构造的时期（G1 已补齐该能力，
            // 见 Middleware GenericAbi/ConstructedLayout）；capability
            // 语义由 id 承载，与对象身份无关，维持 class 形态不变
            TestHarness.CheckTrue("含 QueueHandle / QueueItem class",
                root.Declarations.OfType<ClassDeclarationASTNode>()
                    .Any(s => s.ClassName == "QueueHandle")
                && root.Declarations.OfType<ClassDeclarationASTNode>()
                    .Any(s => s.ClassName == "QueueItem"));
            TestHarness.CheckTrue("含 MessageQueue class（create_queue/post/next）",
                root.Declarations.OfType<ClassDeclarationASTNode>()
                    .Any(c => c.ClassName == "MessageQueue"
                        && c.Members.OfType<CallableDeclarationASTNode>()
                            .Any(m => m.Name == "create_queue" && m.Modifiers.Contains(Keywords.STATIC))
                        && c.Members.OfType<CallableDeclarationASTNode>()
                            .Any(m => m.Name == "post")
                        && c.Members.OfType<CallableDeclarationASTNode>()
                            .Any(m => m.Name == "next")));

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

            TestHarness.Check("Console Root 描述串", AstDescribe.Root(roots[1]),
                "namespace core.io; pub class Console {" +
                @"@NativeLibrary(Str(""rigi_rt"")) @NativeSymbol(Str(""print"")) priv static native func print(text: String), " +
                @"@NativeLibrary(Str(""rigi_rt"")) @NativeSymbol(Str(""printErr"")) priv static native func printErr(text: String), " +
                "pub static func println(text: String) {}}");

            TestHarness.Blank();
        }
    }
}
