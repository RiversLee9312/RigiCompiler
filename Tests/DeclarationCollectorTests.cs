using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S2 P1 声明收集测试（M39）：符号壳建立（类型/成员/全局 + 泛型参数与参数）、
    /// 默认基类与 rich/shared 标记位、namespace 逐段驻留与跨文件合并、
    /// import 上下文登记、ext 待注册列表、重复声明诊断（累积不中断、
    /// 重载不误报）。符号比较一律引用相等（ReferenceEquals）。
    /// </summary>
    public static class DeclarationCollectorTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestBasics();
            TestTypeDeclarations();
            TestNestedTypes();
            TestNamespaces();
            TestImports();
            TestDuplicates();
            TestExtMembers();
            TestNamespaceDiagnostics();
            TestCrossFile();
            return TestHarness.Summary("DeclarationCollector");
        }

        // 多源文件经全管线解析后组成编译单元，执行 P1 收集
        private static (CompilationUnit Unit, DeclarationCollection Decls) CollectUnit(params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            return (unit, DeclarationCollector.Collect(unit));
        }

        private static TypeSymbol GlobalType(CompilationUnit unit, string name)
        {
            return unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == name);
        }

        // ===== 全局声明基本收集 =====
        private static void TestBasics()
        {
            TestHarness.Section("P1 Basics (globals)");

            var (unit, decls) = CollectUnit(
                "var counter: i32 = 0\n" +
                "const name: String = \"x\"\n" +
                "func add(a: i32, b: i32): i32 { return (a + b) }\n");
            var global = unit.Symbols.GlobalNamespace;

            TestHarness.CheckTrue("无诊断", !unit.Diagnostics.HasErrors);
            TestHarness.CheckTrue("全局变量 2 个",
                global.Fields.Count == 2 && global.Fields[0].Name == "counter" && global.Fields[1].Name == "name");
            TestHarness.CheckTrue("全局函数 1 个", global.Methods.Count == 1);
            var add = global.Methods[0];
            TestHarness.CheckTrue("函数名与 Kind", add.Name == "add" && add.Kind == MethodKind.Regular);
            TestHarness.CheckTrue("参数壳（名已收、类型留 P2）",
                add.Parameters.Count == 2 && add.Parameters[0].Name == "a" && add.Parameters[1].Name == "b"
                && add.Parameters[0].Type == null);
            TestHarness.CheckTrue("返回类型留 P2", add.ReturnType == null);
            TestHarness.CheckTrue("全局函数 Owner 为空、Namespace 承载",
                add.Owner == null && ReferenceEquals(add.Namespace, global));
            TestHarness.CheckTrue("全局字段非 static", !global.Fields[0].IsStatic);

            // AST → 符号映射
            var declNodes = unit.SourceFiles[0].Declarations;
            TestHarness.CheckTrue("SymbolOf(变量声明) 同一实例",
                ReferenceEquals(decls.SymbolOf(declNodes[0]), global.Fields[0]));
            TestHarness.CheckTrue("SymbolOf(函数声明) 同一实例",
                ReferenceEquals(decls.SymbolOf(declNodes[2]), add));
            TestHarness.CheckTrue("文件上下文默认全局命名空间",
                ReferenceEquals(decls.FileContextOf(unit.SourceFiles[0]).Namespace, global));
            TestHarness.CheckTrue("无 import 时 Imports 为空",
                decls.FileContextOf(unit.SourceFiles[0]).Imports.Count == 0);
            TestHarness.CheckTrue("ext 待注册列表为空", decls.PendingExtMembers.Count == 0);
        }

        // ===== 类型声明：Kind / 默认基类 / rich-shared 标记位 / 泛型参数 =====
        private static void TestTypeDeclarations()
        {
            TestHarness.Section("P1 Type Declarations");

            var (unit, decls) = CollectUnit(
                "class Animal { }\n" +
                "struct Point { var x: i32\nvar y: i32 }\n" +
                "interface IFly { func fly() }\n" +
                "enum struct Color {}[Red, Green]\n" +
                "pub wrapper Logged { }\n" +
                "shared class SharedBox { }\n" +
                "rich struct RichEntry { }\n");
            var b = unit.Symbols.Bootstrap;

            TestHarness.CheckTrue("无诊断", !unit.Diagnostics.HasErrors);
            TestHarness.CheckTrue("顶层类型 7 个", unit.Symbols.GlobalNamespace.Types.Count == 7);

            var animal = GlobalType(unit, "Animal");
            var point = GlobalType(unit, "Point");
            var iFly = GlobalType(unit, "IFly");
            var color = GlobalType(unit, "Color");
            var logged = GlobalType(unit, "Logged");

            TestHarness.CheckTrue("Kind 映射（class/struct/interface/enum struct/wrapper）",
                animal.Kind == TypeKind.Class && point.Kind == TypeKind.Struct
                && iFly.Kind == TypeKind.Interface && color.Kind == TypeKind.EnumStruct
                && logged.Kind == TypeKind.Wrapper);
            TestHarness.CheckTrue("class 默认基类 Object", ReferenceEquals(animal.BaseType, b.Object));
            TestHarness.CheckTrue("struct 默认基类 ValueType", ReferenceEquals(point.BaseType, b.ValueType));
            TestHarness.CheckTrue("enum struct 默认基类 Enum", ReferenceEquals(color.BaseType, b.Enum));
            TestHarness.CheckTrue("wrapper 隐式基类 Wrapper", ReferenceEquals(logged.BaseType, b.Wrapper));
            TestHarness.CheckTrue("interface 无基类（BaseInterfaces 归 P2）", iFly.BaseType == null);

            TestHarness.CheckTrue("struct 在 ValueType 分支", point.IsValueTypeBranch);
            TestHarness.CheckTrue("class 不在 ValueType 分支", !animal.IsValueTypeBranch);
            TestHarness.CheckTrue("wrapper 恒 rich（§14.9）", logged.IsRich);
            TestHarness.CheckTrue("class 非 rich", !animal.IsRich);
            TestHarness.CheckTrue("rich 标记位读出（不查合法性，归 P2）", GlobalType(unit, "RichEntry").IsRich);
            TestHarness.CheckTrue("shared 标记位读出", GlobalType(unit, "SharedBox").IsShared);

            TestHarness.CheckTrue("成员字段注册且 Owner 反指",
                point.Fields.Count == 2 && point.Fields[0].Name == "x" && ReferenceEquals(point.Fields[0].Owner, point));
            TestHarness.CheckTrue("enum case 不建壳（S11 增补）",
                color.Fields.Count == 0 && color.Methods.Count == 0);
            TestHarness.CheckTrue("接口方法壳", iFly.Methods.Count == 1 && iFly.Methods[0].Name == "fly");

            // init / operator / static / 泛型参数
            var (unit2, decls2) = CollectUnit(
                "class Vec {\n" +
                "    init(x: i32, y: i32)\n" +
                "    operator plus(other: Vec): Vec { }\n" +
                "    static func zero(): Vec { }\n" +
                "}\n" +
                "class Container\\<TElement> {\n" +
                "    func get\\<T>(key: TElement): T { }\n" +
                "}\n");
            var vec = GlobalType(unit2, "Vec");
            TestHarness.CheckTrue("init 壳（Kind/名）",
                vec.Methods.Any(m => m.Name == "init" && m.Kind == MethodKind.Init));
            TestHarness.CheckTrue("operator 壳",
                vec.Methods.Any(m => m.Name == "plus" && m.Kind == MethodKind.Operator));
            TestHarness.CheckTrue("static 标记位",
                vec.Methods.Any(m => m.Name == "zero" && m.IsStatic));
            TestHarness.CheckTrue("init 参数壳 2 个",
                vec.Methods.Single(m => m.Kind == MethodKind.Init).Parameters.Count == 2);

            var container = GlobalType(unit2, "Container");
            TestHarness.CheckTrue("类型泛型参数壳",
                container.GenericParameters.Count == 1 && container.GenericParameters[0].Name == "TElement");
            var get = container.Methods.Single(m => m.Name == "get");
            TestHarness.CheckTrue("函数泛型参数壳",
                get.GenericParameters.Count == 1 && get.GenericParameters[0].Name == "T");
            // 泛型参数节点的 AST→符号映射
            var containerDecl = (ClassDeclarationASTNode)unit2.SourceFiles[0].Declarations[1];
            TestHarness.CheckTrue("SymbolOf(泛型参数节点) 同一实例",
                ReferenceEquals(decls2.SymbolOf(containerDecl.GenericParameters!.Parameters[0]),
                    container.GenericParameters[0]));
        }

        // ===== 嵌套类型与 canonical 路径 =====
        private static void TestNestedTypes()
        {
            TestHarness.Section("P1 Nested Types");

            var (unit, decls) = CollectUnit(
                "namespace myapp\n" +
                "class Outer {\n" +
                "    class Inner {\n" +
                "        var v: i32\n" +
                "    }\n" +
                "}\n");
            var ns = decls.FileContextOf(unit.SourceFiles[0]).Namespace;

            TestHarness.CheckTrue("namespace 逐段驻留 FullName", ns.FullName == "myapp");
            TestHarness.CheckTrue("顶层类型不在全局命名空间",
                unit.Symbols.GlobalNamespace.Types.Count == 0 && ns.Types.Count == 1);
            var outer = ns.Types[0];
            TestHarness.CheckTrue("嵌套类型进 NestedTypes",
                outer.NestedTypes.Count == 1 && outer.NestedTypes[0].Name == "Inner");
            var inner = outer.NestedTypes[0];
            TestHarness.CheckTrue("DeclaringType 反指（引用相等）", ReferenceEquals(inner.DeclaringType, outer));
            TestHarness.CheckTrue("嵌套类型的命名空间随文件", ReferenceEquals(inner.Namespace, ns));
            TestHarness.CheckTrue("canonical：命名空间::外层.内层（BIL §5.2）",
                CanonicalSymbolPrinter.PrintType(inner) == "myapp::Outer.Inner");
            TestHarness.CheckTrue("嵌套成员照常收集", inner.Fields.Count == 1 && inner.Fields[0].Name == "v");
        }

        // ===== namespace 驻留与跨文件合并 =====
        private static void TestNamespaces()
        {
            TestHarness.Section("P1 Namespaces");

            var (unit, decls) = CollectUnit(
                "namespace com.example\nfunc f1() { }\n",
                "namespace com.example\nfunc f2() { }\n");
            var ns1 = decls.FileContextOf(unit.SourceFiles[0]).Namespace;
            var ns2 = decls.FileContextOf(unit.SourceFiles[1]).Namespace;

            TestHarness.CheckTrue("同路径 namespace 同一实例（驻留）", ReferenceEquals(ns1, ns2));
            TestHarness.CheckTrue("FullName 拼段", ns1.FullName == "com.example");
            TestHarness.CheckTrue("跨文件成员合并",
                ns1.Methods.Count == 2 && ns1.Methods.Any(m => m.Name == "f1") && ns1.Methods.Any(m => m.Name == "f2"));
            var global = unit.Symbols.GlobalNamespace;
            TestHarness.CheckTrue("全局命名空间为空名", global.Name.Length == 0 && global.FullName.Length == 0);
            TestHarness.CheckTrue("用户命名空间树与 bootstrap core 并列于全局下",
                global.ChildNamespaces.Any(n => n.Name == "com")
                && ReferenceEquals(global.ChildNamespaces.Single(n => n.Name == "core"), unit.Symbols.Bootstrap.Core));
            TestHarness.CheckTrue("逐段嵌套 com → example",
                global.ChildNamespaces.Single(n => n.Name == "com").ChildNamespaces.Single().Name == "example"
                && global.ChildNamespaces.Single(n => n.Name == "com").ChildNamespaces.Single().FullName == "com.example");
        }

        // ===== import 上下文登记 =====
        private static void TestImports()
        {
            TestHarness.Section("P1 Imports");

            var (unit, decls) = CollectUnit(
                "namespace myapp\n" +
                "import core.collections.List\n" +
                "import core.collections.{List, Map}\n" +
                "import core.utils.*\n");
            var ctx = decls.FileContextOf(unit.SourceFiles[0]);

            TestHarness.CheckTrue("import 登记（多导入 Parser 已展开）", ctx.Imports.Count == 4);
            TestHarness.CheckTrue("importAll 标记",
                !ctx.Imports[0].importAll && !ctx.Imports[1].importAll
                && !ctx.Imports[2].importAll && ctx.Imports[3].importAll);
            TestHarness.CheckTrue("import 路径末段名",
                ctx.Imports[0].symbolNode.symbol.elements[^1].name == "List"
                && ctx.Imports[2].symbolNode.symbol.elements[^1].name == "Map"
                && ctx.Imports[3].symbolNode.symbol.elements[^1].name == "utils");
            TestHarness.CheckTrue("import 路径前段保留",
                ctx.Imports[0].symbolNode.symbol.elements[0].name == "core"
                && ctx.Imports[0].symbolNode.symbol.elements[1].name == "collections");
            TestHarness.CheckTrue("文件命名空间不受 import 影响", ctx.Namespace.FullName == "myapp");
            TestHarness.CheckTrue("无诊断", !unit.Diagnostics.HasErrors);
        }

        // ===== 重复声明诊断（累积不中断、重载不误报）=====
        private static void TestDuplicates()
        {
            TestHarness.Section("P1 Duplicate Declarations");

            var (unit, decls) = CollectUnit(
                "class A { }\n" +
                "class A { }\n" +
                "func dup(x: i32) { }\n" +
                "func dup(x: i32) { }\n" +
                "var v: i32\n" +
                "var v: i32\n" +
                "func ok(x: i32) { }\n" +
                "func ok(x: String) { }\n" +
                "func after() { }\n");

            TestHarness.CheckTrue("三条 Error 诊断",
                unit.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 3);
            TestHarness.CheckSemanticError("同名类型冲突", unit.Diagnostics, "Duplicate type declaration: 'A'");
            TestHarness.CheckSemanticError("同签名方法冲突", unit.Diagnostics, "Duplicate method declaration: 'dup'");
            TestHarness.CheckSemanticError("同名变量冲突", unit.Diagnostics, "Duplicate variable declaration: 'v'");

            var global = unit.Symbols.GlobalNamespace;
            TestHarness.CheckTrue("重复类型只保留第一个", global.Types.Count(t => t.Name == "A") == 1);
            TestHarness.CheckTrue("重复声明不中断（后续声明照常收集）",
                global.Methods.Any(m => m.Name == "after"));
            TestHarness.CheckTrue("重载不误报（ok 两个实例都注册）",
                global.Methods.Count(m => m.Name == "ok") == 2);
            TestHarness.CheckTrue("重复方法只注册第一个",
                global.Methods.Count(m => m.Name == "dup") == 1);
            TestHarness.CheckTrue("重复变量只注册第一个",
                global.Fields.Count(f => f.Name == "v") == 1);
            // 重复声明的符号仍登记 AST→符号映射（与注册实例不同一）
            var secondA = unit.SourceFiles[0].Declarations[1];
            TestHarness.CheckTrue("重复声明的 SymbolOf 仍登记",
                decls.SymbolOf(secondA) is TypeSymbol dupA
                && !ReferenceEquals(dupA, global.Types.Single(t => t.Name == "A")));

            // 成员级重复 + 跨文件同名
            var (unit2, _) = CollectUnit(
                "class C {\n    var f: i32\n    var f: i32\n}\n",
                "class C { }\n");
            TestHarness.CheckSemanticError("成员字段同名冲突", unit2.Diagnostics,
                "Duplicate variable declaration: 'f'");
            TestHarness.CheckSemanticError("跨文件同名类型冲突", unit2.Diagnostics,
                "Duplicate type declaration: 'C'");
            TestHarness.CheckTrue("跨文件重复不中断（两文件各自符号仍映射）",
                unit2.Symbols.GlobalNamespace.Types.Count(t => t.Name == "C") == 1);
        }

        // ===== ext 扩展成员（§4.4：拆名登记，注册归 P2）=====
        private static void TestExtMembers()
        {
            TestHarness.Section("P1 ext Members");

            var (unit, decls) = CollectUnit(
                "pub ext func String.reversed(): String { }\n" +
                "pub ext var String.isEmpty: bool { get(_: _) { return (this.length == 0) } }\n");

            TestHarness.CheckTrue("无诊断", !unit.Diagnostics.HasErrors);
            TestHarness.CheckTrue("ext 待注册 2 个", decls.PendingExtMembers.Count == 2);
            var reversed = (MethodSymbol)decls.PendingExtMembers[0];
            TestHarness.CheckTrue("ext 函数拆名（成员名 + 目标路径原文）",
                reversed.Name == "reversed" && reversed.ExtTargetPath == "String");
            TestHarness.CheckTrue("ext 函数暂挂文件命名空间",
                reversed.Owner == null && ReferenceEquals(reversed.Namespace, unit.Symbols.GlobalNamespace));
            var isEmpty = (FieldSymbol)decls.PendingExtMembers[1];
            TestHarness.CheckTrue("ext 字段拆名",
                isEmpty.Name == "isEmpty" && isEmpty.ExtTargetPath == "String");
            TestHarness.CheckTrue("ext 成员不进声明容器表（P2 注册到目标类型）",
                unit.Symbols.GlobalNamespace.Methods.Count == 0 && unit.Symbols.GlobalNamespace.Fields.Count == 0);
            TestHarness.CheckTrue("ext 声明节点 SymbolOf 仍登记",
                ReferenceEquals(decls.SymbolOf(unit.SourceFiles[0].Declarations[0]), reversed));
        }

        // ===== namespace 唯一性与位置诊断（§15.1）=====
        private static void TestNamespaceDiagnostics()
        {
            TestHarness.Section("P1 Namespace Diagnostics");

            var (unit1, decls1) = CollectUnit("namespace a\nnamespace b\nclass A { }\n");
            TestHarness.CheckSemanticError("每文件至多一个 namespace", unit1.Diagnostics,
                "Multiple namespace declarations in one file");
            TestHarness.CheckTrue("第一个 namespace 生效",
                decls1.FileContextOf(unit1.SourceFiles[0]).Namespace.FullName == "a");

            var (unit2, _) = CollectUnit("class A { }\nnamespace late\n");
            TestHarness.CheckSemanticError("namespace 须先于类型/成员声明", unit2.Diagnostics,
                "Namespace declaration must precede all type and member declarations");
            TestHarness.CheckTrue("迟到 namespace 不生效（仍全局）",
                unit2.Symbols.GlobalNamespace.Types.Any(t => t.Name == "A"));
        }

        // ===== 跨文件前向引用可收集（同一符号图、实例唯一）=====
        private static void TestCrossFile()
        {
            TestHarness.Section("P1 Cross-file Collection");

            var (unit, _) = CollectUnit(
                "namespace app\nclass A { }\n",
                "namespace app\nclass B { var a: A }\n");
            var app = unit.Symbols.GlobalNamespace.ChildNamespaces.Single(n => n.Name == "app");

            TestHarness.CheckTrue("无诊断", !unit.Diagnostics.HasErrors);
            TestHarness.CheckTrue("跨文件类型同驻留一个命名空间",
                app.Types.Count == 2 && app.Types.Any(t => t.Name == "A") && app.Types.Any(t => t.Name == "B"));
            var bField = app.Types.Single(t => t.Name == "B").Fields.Single();
            TestHarness.CheckTrue("字段壳跨文件就位（类型引用解析归 P2）",
                bField.Name == "a" && bField.FieldType == null);
        }
    }
}
