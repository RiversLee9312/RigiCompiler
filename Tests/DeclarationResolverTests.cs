using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S3 P2 声明解析测试（M40）：七个子任务各自独立测试组——
    /// 类型引用解析（含泛型构造驻留、T?、泛型参数、import/namespace/core 名字解析、
    /// ErrorType 毒化）、init 参数映射、继承/implements 图与循环继承、修饰符合法性、
    /// native 函数声明（§4.6 全规则与内建注解 @NativeLibrary/@NativeSymbol）、
    /// rich/shared 单向传染与字段闭包（§3.1.1 闭包表逐行合法+非法）、共享安全闸门、
    /// 泛型约束声明侧、ext 注册与 wrapper 目标矩阵（§14.9 A–D 逐行合法+非法 +
    /// interface 实现者传染）。符号比较一律引用相等（ReferenceEquals）。
    /// </summary>
    public static class DeclarationResolverTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestTypeReferences();
            TestGenericAndNullable();
            TestNameLookupContexts();
            TestInitMapping();
            TestInheritance();
            TestModifierLegality();
            TestContagion();
            TestFieldClosures();
            TestSharedSafetyGates();
            TestGenericConstraints();
            TestExtRegistration();
            TestWrapperApplications();
            TestNativeDeclarations();
            TestAccessibility();
            TestAccessorDeclarations();
            TestOverrideModifiers();
            TestDeclarationSiteAccess();
            TestFreeze();
            return TestHarness.Summary("DeclarationResolver");
        }

        // 多源文件经全管线解析后组成编译单元，执行 P1 + P2
        private static (CompilationUnit Unit, DeclarationCollection Decls) ResolveUnit(params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, decls);
        }

        // 无诊断断言（失败时附带诊断袋内容）
        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => $"{d.Phase}: {d.Message}")));
        }

        private static TypeSymbol GlobalType(CompilationUnit unit, string name)
        {
            return unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == name);
        }

        private static NamespaceSymbol NsOf(CompilationUnit unit, params string[] segments)
        {
            var ns = unit.Symbols.GlobalNamespace;
            foreach (var segment in segments)
            {
                ns = ns.ChildNamespaces.Single(n => n.Name == segment);
            }
            return ns;
        }

        // ===== 子任务 1：类型引用解析（基本类型 / 用户类型 / 参数与返回 / 失败毒化）=====
        private static void TestTypeReferences()
        {
            TestHarness.Section("P2 Type References (basics)");

            var (unit, _) = ResolveUnit(
                "var a: i32\n" +
                "var b: String\n" +
                "shared class User { }\n" +
                "var u: User\n" +
                "class Holder { var o: Object }\n" +
                "func add(x: i32, y: i32): i32 { return (x + y) }\n");
            var b = unit.Symbols.Bootstrap;
            var global = unit.Symbols.GlobalNamespace;

            CheckNoErrors("无诊断", unit);
            TestHarness.CheckTrue("i32 字段解析到 bootstrap",
                ReferenceEquals(global.Fields.Single(f => f.Name == "a").FieldType, b.Int32));
            TestHarness.CheckTrue("String 字段解析到 bootstrap",
                ReferenceEquals(global.Fields.Single(f => f.Name == "b").FieldType, b.String));
            var user = GlobalType(unit, "User");
            TestHarness.CheckTrue("用户类型解析同一引用",
                ReferenceEquals(global.Fields.Single(f => f.Name == "u").FieldType, user));
            TestHarness.CheckTrue("Object 裸名经 core 隐式解析",
                ReferenceEquals(GlobalType(unit, "Holder").Fields.Single(f => f.Name == "o").FieldType, b.Object));
            var add = global.Methods.Single(m => m.Name == "add");
            TestHarness.CheckTrue("参数类型解析",
                ReferenceEquals(add.Parameters[0].Type, b.Int32) && ReferenceEquals(add.Parameters[1].Type, b.Int32));
            TestHarness.CheckTrue("返回类型解析", ReferenceEquals(add.ReturnType, b.Int32));

            // 解析失败 → ErrorType 毒化（后续检查静默）
            var (unit2, _) = ResolveUnit("var x: NoSuchType\n");
            TestHarness.CheckSemanticError("未知名诊断", unit2.Diagnostics, "Unresolved type or namespace: 'NoSuchType'");
            var poisoned = unit2.Symbols.GlobalNamespace.Fields.Single(f => f.Name == "x").FieldType;
            TestHarness.CheckTrue("失败绑 ErrorType 毒化（编译单元单例）",
                poisoned is ErrorTypeSymbol && ReferenceEquals(poisoned, unit2.Symbols.ErrorType));

            // 毒化传播：泛型实参失败只报实参一处，整体静默毒化
            var (unit3, _) = ResolveUnit("class Box\\<T> { }\nvar x: Box\\<Nope>\n");
            TestHarness.CheckTrue("毒化传播不添新诊断",
                unit3.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1);
            TestHarness.CheckSemanticError("实参未知名", unit3.Diagnostics, "Unresolved type or namespace: 'Nope'");
            TestHarness.CheckTrue("构造整体毒化",
                unit3.Symbols.GlobalNamespace.Fields.Single(f => f.Name == "x").FieldType is ErrorTypeSymbol);

            // 泛型实参数目不匹配
            var (unit4, _) = ResolveUnit("class Box\\<T> { }\nvar x: Box\\<i32, i32>\n");
            TestHarness.CheckSemanticError("实参数目不匹配", unit4.Diagnostics,
                "'Box' expects 1 type argument(s), got 2");
            var (unit5, _) = ResolveUnit("var x: i32\\<i32>\n");
            TestHarness.CheckSemanticError("非泛型类型带实参", unit5.Diagnostics,
                "'i32' expects 0 type argument(s), got 1");
        }

        // ===== 子任务 1：泛型构造驻留、T?、泛型参数引用 =====
        private static void TestGenericAndNullable()
        {
            TestHarness.Section("P2 Type References (generics / nullable)");

            var (unit, _) = ResolveUnit(
                "shared class Box\\<T> { var item: T }\n" +
                "var a: Box\\<i32>\n" +
                "var b2: Box\\<i32>\n" +
                "var c: Box\\<String>\n" +
                "var n: i32?\n" +
                "var n2: Nullable\\<i32>\n" +
                "var deep: Box\\<Box\\<i32>>\n");
            var b = unit.Symbols.Bootstrap;
            var global = unit.Symbols.GlobalNamespace;

            CheckNoErrors("无诊断", unit);
            var box = GlobalType(unit, "Box");
            var boxI32a = (TypeSymbol)global.Fields.Single(f => f.Name == "a").FieldType!;
            TestHarness.CheckTrue("构造类型驻留同一引用",
                ReferenceEquals(boxI32a, global.Fields.Single(f => f.Name == "b2").FieldType));
            TestHarness.CheckTrue("不同实参不同实例",
                !ReferenceEquals(boxI32a, global.Fields.Single(f => f.Name == "c").FieldType));
            TestHarness.CheckTrue("ConstructedFrom 指泛型定义", ReferenceEquals(boxI32a.ConstructedFrom, box));
            TestHarness.CheckTrue("实参列表", boxI32a.TypeArguments!.Count == 1
                && ReferenceEquals(boxI32a.TypeArguments[0], b.Int32));
            TestHarness.CheckTrue("泛型参数字段 → GenericParameterSymbol 同一引用",
                ReferenceEquals(box.Fields.Single(f => f.Name == "item").FieldType, box.GenericParameters[0]));

            var nullableI32 = (TypeSymbol)global.Fields.Single(f => f.Name == "n").FieldType!;
            TestHarness.CheckTrue("T? → Nullable\\<T\\> 构造",
                ReferenceEquals(nullableI32.ConstructedFrom, b.NullableDefinition)
                && ReferenceEquals(nullableI32.TypeArguments![0], b.Int32));
            TestHarness.CheckTrue("i32? 与 Nullable\\<i32> 同实例",
                ReferenceEquals(nullableI32, global.Fields.Single(f => f.Name == "n2").FieldType));

            var deep = (TypeSymbol)global.Fields.Single(f => f.Name == "deep").FieldType!;
            TestHarness.CheckTrue("嵌套构造 Box\\<Box\\<i32>>",
                ReferenceEquals(deep.ConstructedFrom, box)
                && deep.TypeArguments![0] is TypeSymbol inner && ReferenceEquals(inner, boxI32a));
        }

        // ===== 子任务 1：名字解析上下文（namespace / 嵌套 / import / core 路径）=====
        private static void TestNameLookupContexts()
        {
            TestHarness.Section("P2 Name Lookup Contexts");

            var (unit, _) = ResolveUnit(
                "namespace app.models\n" +
                "pub class User { }\n" +
                "class Order { var buyer: User }\n",
                "namespace app.services\n" +
                "import app.models.User\n" +
                "class UserService { var user: User }\n",
                "namespace app.more\n" +
                "import app.models.*\n" +
                "class Repo { var u: User }\n");
            CheckNoErrors("无诊断（跨文件/具名/通配）", unit);
            var models = NsOf(unit, "app", "models");
            var user = models.Types.Single(t => t.Name == "User");
            TestHarness.CheckTrue("同命名空间互见",
                ReferenceEquals(models.Types.Single(t => t.Name == "Order").Fields[0].FieldType, user));
            TestHarness.CheckTrue("具名 import 解析",
                ReferenceEquals(NsOf(unit, "app", "services").Types.Single(t => t.Name == "UserService")
                    .Fields[0].FieldType, user));
            TestHarness.CheckTrue("通配 import 解析",
                ReferenceEquals(NsOf(unit, "app", "more").Types.Single(t => t.Name == "Repo")
                    .Fields[0].FieldType, user));

            // 嵌套类型：成员引用同类型嵌套 + 外层泛型参数在嵌套内可见
            var (unit2, _) = ResolveUnit(
                "class Outer\\<T> {\n" +
                "    class Inner { var x: T }\n" +
                "    var slot: Inner\n" +
                "}\n");
            CheckNoErrors("无诊断（嵌套）", unit2);
            var outer = GlobalType(unit2, "Outer");
            var inner = outer.NestedTypes.Single(t => t.Name == "Inner");
            TestHarness.CheckTrue("成员引用同类型嵌套",
                ReferenceEquals(outer.Fields.Single(f => f.Name == "slot").FieldType, inner));
            TestHarness.CheckTrue("外层泛型参数在嵌套内可见",
                ReferenceEquals(inner.Fields.Single(f => f.Name == "x").FieldType, outer.GenericParameters[0]));

            // core 全路径
            var (unit3, _) = ResolveUnit("var o: core.Object\n");
            TestHarness.CheckTrue("core 全路径解析",
                ReferenceEquals(unit3.Symbols.GlobalNamespace.Fields[0].FieldType, unit3.Symbols.Bootstrap.Object));

            // import 未解析 → 诊断（消费侧静默）
            var (unit4, _) = ResolveUnit("import no.such.Thing\nvar x: Thing\n");
            TestHarness.CheckSemanticError("未解析 import", unit4.Diagnostics, "Unresolved import: 'no.such.Thing'");

            // 通配 import 的容器必须是命名空间或类型（未解析同样诊断）
            var (unit5, _) = ResolveUnit("import no.such.*\nvar x: i32\n");
            TestHarness.CheckSemanticError("未解析通配 import", unit5.Diagnostics, "Unresolved import: 'no.such'");
        }

        // ===== 子任务 1 附属：init 参数映射解析（§9.3 前端遗留义务）=====
        private static void TestInitMapping()
        {
            TestHarness.Section("P2 Init Parameter Mapping");

            var (unit, _) = ResolveUnit(
                "class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: String\n" +
                "    init(_ -> x, _ -> y)\n" +
                "    init(horizontal: i32 -> x)\n" +
                "}\n");
            var b = unit.Symbols.Bootstrap;
            CheckNoErrors("无诊断", unit);
            var point = GlobalType(unit, "Point");
            var init = point.Methods.First(m => m.Kind == MethodKind.Init);
            TestHarness.CheckTrue("`_ -> x` 参数名取字段名（P1 语法替换）",
                init.Parameters[0].Name == "x" && init.Parameters[1].Name == "y");
            TestHarness.CheckTrue("省略类型沿用字段类型",
                ReferenceEquals(init.Parameters[0].Type, b.Int32)
                && ReferenceEquals(init.Parameters[1].Type, b.String));
            var init2 = point.Methods.Where(m => m.Kind == MethodKind.Init).Skip(1).First();
            TestHarness.CheckTrue("显式类型映射按标注解析",
                ReferenceEquals(init2.Parameters[0].Type, b.Int32));

            var (unit2, _) = ResolveUnit("class C { init(_ -> missing) }\n");
            TestHarness.CheckSemanticError("映射未知字段", unit2.Diagnostics,
                "Init parameter mapping targets unknown field: 'missing'");

            // 映射到无类型标注字段：P2 无法定型（类型推断归 P3），声明侧报错
            var (unit3, _) = ResolveUnit("class C { var x = 1\ninit(_ -> x) }\n");
            TestHarness.CheckSemanticError("映射无标注字段", unit3.Diagnostics,
                "requires field 'x' to have a type annotation");
        }

        // ===== 子任务 2：继承 / implements 图 + 循环继承 =====
        private static void TestInheritance()
        {
            TestHarness.Section("P2 Inheritance Graph");

            var (unit, _) = ResolveUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "abstract class Shape { }\n" +
                "class Circle : Shape { }\n" +
                "interface Comparable { }\n" +
                "interface Named { }\n" +
                "interface Pet : Named { }\n" +
                "class Cat : Animal implements Comparable, Named { }\n" +
                "open rich struct BaseEntry { }\n" +
                "rich struct Entry : BaseEntry { }\n");
            CheckNoErrors("无诊断（合法继承全形态）", unit);
            var animal = GlobalType(unit, "Animal");
            TestHarness.CheckTrue("class 基类覆盖默认 Object",
                ReferenceEquals(GlobalType(unit, "Dog").BaseType, animal));
            TestHarness.CheckTrue("abstract 基类可继承",
                ReferenceEquals(GlobalType(unit, "Circle").BaseType, GlobalType(unit, "Shape")));
            var cat = GlobalType(unit, "Cat");
            TestHarness.CheckTrue("implements 双接口",
                cat.Interfaces.Count == 2
                && ReferenceEquals(cat.Interfaces[0], GlobalType(unit, "Comparable"))
                && ReferenceEquals(cat.Interfaces[1], GlobalType(unit, "Named")));
            TestHarness.CheckTrue("interface 继承进 Interfaces",
                GlobalType(unit, "Pet").Interfaces.Count == 1
                && ReferenceEquals(GlobalType(unit, "Pet").Interfaces[0], GlobalType(unit, "Named")));
            TestHarness.CheckTrue("struct 基类覆盖",
                ReferenceEquals(GlobalType(unit, "Entry").BaseType, GlobalType(unit, "BaseEntry")));

            var (u2, _) = ResolveUnit("class A { }\nclass B : A { }\n");
            TestHarness.CheckSemanticError("基类非 open/abstract", u2.Diagnostics,
                "base class 'A' is not open or abstract");
            var (u3, _) = ResolveUnit("interface I { }\nclass A : I { }\n");
            TestHarness.CheckSemanticError("class 以 interface 为基类", u3.Diagnostics,
                "a class can only inherit from a class");
            var (u4, _) = ResolveUnit("struct S { }\nopen class A { }\nclass C : S { }\n");
            TestHarness.CheckSemanticError("class 继承 struct", u4.Diagnostics,
                "a class can only inherit from a class");
            var (u5, _) = ResolveUnit("open class A { }\nstruct S : A { }\n");
            TestHarness.CheckSemanticError("struct 继承 class", u5.Diagnostics,
                "a struct can only inherit from a struct");
            var (u6, _) = ResolveUnit("struct Base { }\nstruct S : Base { }\n");
            TestHarness.CheckSemanticError("基 struct 非 open rich", u6.Diagnostics,
                "base struct 'Base' must be an open rich struct");
            var (u7, _) = ResolveUnit("interface I { }\nstruct S implements I { }\n");
            TestHarness.CheckSemanticError("struct implements", u7.Diagnostics,
                "structs cannot implement interfaces");
            var (u8, _) = ResolveUnit("class C { }\nclass D implements C { }\n");
            TestHarness.CheckSemanticError("implements 非接口", u8.Diagnostics, "must be an interface");
            var (u9, _) = ResolveUnit("open class A : B { }\nopen class B : A { }\n");
            TestHarness.CheckSemanticError("class 循环继承", u9.Diagnostics, "Circular inheritance involving 'B'");
            var (u10, _) = ResolveUnit("interface A : B { }\ninterface B : A { }\n");
            TestHarness.CheckSemanticError("interface 循环继承", u10.Diagnostics,
                "Circular interface inheritance involving 'B'");
        }

        // ===== 子任务 3：修饰符合法性 =====
        private static void TestModifierLegality()
        {
            TestHarness.Section("P2 Modifier Legality");

            var (u1, _) = ResolveUnit("rich class C { }\n");
            TestHarness.CheckSemanticError("rich class", u1.Diagnostics,
                "'rich' can only be applied to struct/enum struct");
            var (u2, _) = ResolveUnit("rich interface I { }\n");
            TestHarness.CheckSemanticError("rich interface", u2.Diagnostics, "'rich' cannot be applied to interface");
            var (u3, _) = ResolveUnit("@WrapperTarget(.Entity)\nrich wrapper W { }\n");
            TestHarness.CheckSemanticError("wrapper 显式 rich", u3.Diagnostics,
                "'rich' is implied by the wrapper declaration and must not be written");
            var (u4, _) = ResolveUnit("shared interface I { }\n");
            TestHarness.CheckSemanticError("shared interface", u4.Diagnostics,
                "'shared' cannot be applied to interface");
            var (u5, _) = ResolveUnit("shared struct S { }\n");
            TestHarness.CheckSemanticError("shared 非 rich struct", u5.Diagnostics,
                "'shared' struct must also be 'rich'");
            var (u6, _) = ResolveUnit("open struct S { }\n");
            TestHarness.CheckSemanticError("非 rich struct open", u6.Diagnostics, "non-rich struct cannot be 'open'");
            var (u7, _) = ResolveUnit("abstract struct S { }\n");
            TestHarness.CheckSemanticError("非 rich struct abstract", u7.Diagnostics,
                "non-rich struct cannot be 'abstract'");
            var (u8, _) = ResolveUnit("open enum struct E {}[A]\n");
            TestHarness.CheckSemanticError("enum struct open", u8.Diagnostics, "enum struct cannot be 'open'");
            var (u9, _) = ResolveUnit("abstract enum struct E {}[A]\n");
            TestHarness.CheckSemanticError("enum struct abstract", u9.Diagnostics,
                "enum struct cannot be 'abstract'");
            var (u10, _) = ResolveUnit("open abstract class C { }\n");
            TestHarness.CheckSemanticError("open×abstract", u10.Diagnostics,
                "'open' and 'abstract' are mutually exclusive");
            var (u11, _) = ResolveUnit("singleton struct S { }\n");
            TestHarness.CheckSemanticError("singleton struct", u11.Diagnostics,
                "'singleton' can only be applied to class");
            var (u12, _) = ResolveUnit("singleton class C { }\n");
            TestHarness.CheckSemanticError("singleton 非 shared", u12.Diagnostics, "singleton class must also be 'shared'");
            var (u13, _) = ResolveUnit("abstract singleton shared class C { }\n");
            TestHarness.CheckSemanticError("abstract×singleton", u13.Diagnostics,
                "'abstract' and 'singleton' are mutually exclusive");
            var (u14, _) = ResolveUnit("@WrapperTarget(.Entity)\nopen wrapper W { }\n");
            TestHarness.CheckSemanticError("wrapper open", u14.Diagnostics, "wrapper cannot be 'open' or 'abstract'");
            var (u14b, _) = ResolveUnit("open interface I { }\n");
            TestHarness.CheckSemanticError("interface open", u14b.Diagnostics, "'open' cannot be applied to interface");
            var (u15, _) = ResolveUnit("pub priv class C { }\n");
            TestHarness.CheckSemanticError("访问修饰符互斥", u15.Diagnostics,
                "Access modifiers are mutually exclusive");
            var (u15b, _) = ResolveUnit("pub pub class C { }\n");
            TestHarness.CheckSemanticError("重复修饰符", u15b.Diagnostics, "Duplicate modifier 'pub'");
            var (u16, _) = ResolveUnit("async var x: i32\n");
            TestHarness.CheckSemanticError("async 字段", u16.Diagnostics, "'async' can only be applied to functions");
            var (u17, _) = ResolveUnit("ext func foo() { }\n");
            TestHarness.CheckSemanticError("ext 无限定名", u17.Diagnostics, "'ext' declaration requires a qualified name");
            var (u18, _) = ResolveUnit("class C { ext func String.foo() { } }\n");
            TestHarness.CheckSemanticError("成员位置 ext", u18.Diagnostics,
                "'ext' can only be applied to global declarations");

            // 合法形态对照
            var (ok, _) = ResolveUnit(
                "shared rich struct S { }\n" +
                "open rich struct S2 { }\n" +
                "shared singleton class C { }\n" +
                "abstract class C2 { }\n" +
                "async func f() { }\n");
            CheckNoErrors("合法修饰符组合无诊断", ok);
        }

        // ===== 子任务 4a：rich/shared 单向传染 =====
        private static void TestContagion()
        {
            TestHarness.Section("P2 Rich/Shared Contagion");

            var (u1, _) = ResolveUnit("open shared class SBase { }\nclass Bad : SBase { }\n");
            TestHarness.CheckSemanticError("shared 基类 ⇒ 子类必须 shared", u1.Diagnostics, "must also be 'shared'");
            var (u2, _) = ResolveUnit("open rich struct RBase { }\nstruct Bad : RBase { }\n");
            TestHarness.CheckSemanticError("rich 基类 ⇒ 子类必须 rich", u2.Diagnostics, "must also be 'rich'");

            // 反向可收紧：非 shared 基类 + shared 子类（闭包合法时）合法
            var (u3, _) = ResolveUnit("open class LBase { }\nshared class Good : LBase { }\n");
            CheckNoErrors("反向收紧（无字段）合法", u3);

            // 反向收紧由继承字段闭包兜底：shared 子类继承 local object 字段被拒
            var (u4, _) = ResolveUnit(
                "class LocalObj { }\n" +
                "open class LBase { var o: LocalObj }\n" +
                "shared class Bad : LBase { }\n");
            TestHarness.CheckSemanticError("继承字段闭包兜底", u4.Diagnostics,
                "is shared and cannot hold local object field 'o'");
        }

        // ===== 子任务 4b：字段闭包表（§3.1.1 七行，逐行合法 + 非法）=====
        private const string ClosurePrelude =
            "class LocalUser { }\n" +
            "shared class SharedUser { }\n" +
            "struct Point { var x: i32 }\n" +
            "rich struct RichEntry { var u: LocalUser }\n" +
            "shared rich struct SharedEntry { var u: SharedUser }\n";

        private static void TestFieldClosures()
        {
            TestHarness.Section("P2 Field Closure Table (§3.1.1)");

            // 全部合法持有者行（闭包表每行的允许列）
            var (ok, _) = ResolveUnit(ClosurePrelude +
                "struct OkPlain { var p: Point }\n" +                                  // 非 rich struct ← 非 rich VT
                "rich struct OkRich { var u: LocalUser\nvar e: SharedEntry }\n" +      // rich struct ← local object / 所有 VT
                "shared rich struct OkSharedRich {\n" +
                "    var u: SharedUser\nvar p: Point\nvar e: SharedEntry }\n" +        // shared rich struct 允许列
                "@WrapperTarget(.Entity)\nwrapper OkWrapper { var u: LocalUser\nvar p: Point }\n" +
                "@WrapperTarget(.Entity)\nshared wrapper OkSharedWrapper { var u: SharedUser\nvar p: Point }\n" +
                "class OkLocalClass { var u: LocalUser\nvar e: RichEntry }\n" +        // local class 允许列
                "shared class OkSharedClass {\n" +
                "    var u: SharedUser\nvar p: Point\nvar e: SharedEntry\nvar n: SharedUser? }\n");
            CheckNoErrors("闭包表允许列全部合法（七行）", ok);

            // 非 rich struct 行
            var (u1, _) = ResolveUnit(ClosurePrelude + "struct Bad { var u: LocalUser }\n");
            TestHarness.CheckSemanticError("非 rich struct 持 local object", u1.Diagnostics,
                "Non-rich struct 'Bad' cannot hold object field 'u'");
            var (u2, _) = ResolveUnit(ClosurePrelude + "struct Bad { var u: SharedUser }\n");
            TestHarness.CheckSemanticError("非 rich struct 持 shared object 同禁", u2.Diagnostics,
                "Non-rich struct 'Bad' cannot hold object field 'u'");
            var (u3, _) = ResolveUnit(ClosurePrelude + "struct Bad { var e: RichEntry }\n");
            TestHarness.CheckSemanticError("非 rich struct 内嵌 rich", u3.Diagnostics,
                "Non-rich struct 'Bad' cannot embed rich value type field 'e'");

            // shared rich struct 行
            var (u4, _) = ResolveUnit(ClosurePrelude + "shared rich struct Bad { var u: LocalUser }\n");
            TestHarness.CheckSemanticError("shared rich struct 持 local object", u4.Diagnostics,
                "is shared and cannot hold local object field 'u'");
            var (u5, _) = ResolveUnit(ClosurePrelude + "shared rich struct Bad { var e: RichEntry }\n");
            TestHarness.CheckSemanticError("shared rich struct 内嵌 local rich", u5.Diagnostics,
                "is shared and cannot embed non-shared rich value type field 'e'");

            // shared wrapper 行
            var (u6, _) = ResolveUnit(ClosurePrelude +
                "@WrapperTarget(.Entity)\nshared wrapper Bad { var u: LocalUser }\n");
            TestHarness.CheckSemanticError("shared wrapper 持 local object", u6.Diagnostics,
                "is shared and cannot hold local object field 'u'");

            // shared class 行
            var (u7, _) = ResolveUnit(ClosurePrelude + "shared class Bad { var u: LocalUser }\n");
            TestHarness.CheckSemanticError("shared class 持 local object", u7.Diagnostics,
                "is shared and cannot hold local object field 'u'");
            var (u8, _) = ResolveUnit(ClosurePrelude + "shared class Bad { var u: LocalUser? }\n");
            TestHarness.CheckSemanticError("shared class 持 Nullable\\<local>", u8.Diagnostics,
                "is shared and cannot hold local object field 'u'");
            var (u9, _) = ResolveUnit(ClosurePrelude + "shared class Bad { var e: RichEntry }\n");
            TestHarness.CheckSemanticError("shared class 内嵌 local rich", u9.Diagnostics,
                "is shared and cannot embed non-shared rich value type field 'e'");

            // 泛型实参所展开的字段（§3.1.1 递归）
            // 注：Pair 必须是 struct——直接分类先放行（值类型内嵌合法），才能测到实参展开拦截
            var (u10, _) = ResolveUnit(ClosurePrelude +
                "struct Pair\\<T> { var first: T }\n" +
                "struct OkG { var p: Pair\\<i32> }\n");
            CheckNoErrors("泛型定义处泛型参数字段跳过 + 实参 i32 展开合法", u10);
            var (u11, _) = ResolveUnit(ClosurePrelude +
                "struct Pair\\<T> { var first: T }\n" +
                "struct BadG { var p: Pair\\<LocalUser> }\n");
            TestHarness.CheckSemanticError("非 rich struct 泛型实参展开 local object", u11.Diagnostics,
                "Non-rich struct 'BadG' cannot hold object field 'p' (via generic argument of 'Pair')");
        }

        // ===== 子任务 5：共享安全闸门（§3.1.1 闸门 1）=====
        private static void TestSharedSafetyGates()
        {
            TestHarness.Section("P2 Shared-Safety Gates");

            var (ok, _) = ResolveUnit(
                "shared class SharedUser { }\n" +
                "var g1: i32\n" +
                "const g2: String = \"x\"\n" +
                "var g3: SharedUser\n" +
                "var g4: SharedUser?\n" +
                "class C { static var s: SharedUser\nvar inst: LocalUser }\n" +
                "class LocalUser { }\n" +
                "class C2 { var inst2: LocalUser }\n");
            CheckNoErrors("共享安全类型过闸门（含实例字段不查）", ok);

            var (u1, _) = ResolveUnit("class LocalUser { }\nvar g: LocalUser\n");
            TestHarness.CheckSemanticError("全局变量持 local object", u1.Diagnostics,
                "Global or static field 'g' must have a shared-safe type");
            var (u2, _) = ResolveUnit("class LocalUser { }\nclass C { static var s: LocalUser }\n");
            TestHarness.CheckSemanticError("静态字段持 local object", u2.Diagnostics,
                "Global or static field 's' must have a shared-safe type");
            var (u3, _) = ResolveUnit("class LocalUser { }\nvar g: LocalUser?\n");
            TestHarness.CheckSemanticError("全局 Nullable\\<local>", u3.Diagnostics,
                "Global or static field 'g' must have a shared-safe type");
        }

        // ===== 子任务 6：泛型约束声明侧检查 =====
        private static void TestGenericConstraints()
        {
            TestHarness.Section("P2 Generic Constraints (declaration side)");

            var (unit, _) = ResolveUnit(
                "interface Comparable { }\n" +
                "class Container\\<TElement extends Comparable> { }\n" +
                "func process\\<TItem extends Comparable>(x: TItem) { }\n");
            CheckNoErrors("无诊断（extends）", unit);
            var comparable = GlobalType(unit, "Comparable");
            var tElement = GlobalType(unit, "Container").GenericParameters[0];
            TestHarness.CheckTrue("类型泛型参数约束填充",
                tElement.Constraints.Count == 1
                && tElement.Constraints[0].Kind == GenericConstraintKind.Extends
                && ReferenceEquals(tElement.Constraints[0].Bound, comparable));
            var process = unit.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "process");
            TestHarness.CheckTrue("函数泛型参数约束填充",
                process.GenericParameters[0].Constraints.Count == 1
                && ReferenceEquals(process.GenericParameters[0].Constraints[0].Bound, comparable));

            // supers 约束（§3.6 位置可交换形态）
            var (u2, _) = ResolveUnit("open class Base { }\nfunc f\\<TItem supers Base>(x: TItem) { }\n");
            CheckNoErrors("supers 约束合法", u2);
            var tItem = u2.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "f").GenericParameters[0];
            TestHarness.CheckTrue("supers 约束填充",
                tItem.Constraints.Count == 1 && tItem.Constraints[0].Kind == GenericConstraintKind.Supers
                && ReferenceEquals(tItem.Constraints[0].Bound, GlobalType(u2, "Base")));

            // with 约束：边界必须是 wrapper 类型
            var (u3, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper Serializable { }\n" +
                "func dump\\<TItem with Serializable>(x: TItem) { }\n");
            CheckNoErrors("with wrapper 合法", u3);
            var (u4, _) = ResolveUnit("class NotWrapper { }\nfunc dump\\<TItem with NotWrapper>(x: TItem) { }\n");
            TestHarness.CheckSemanticError("with 非 wrapper", u4.Diagnostics,
                "'with' constraint bound of 'TItem' must be a wrapper type");

            // 约束 Target 必须是本声明的泛型参数（路径形态 Target 不是裸参数名）
            var (u5, _) = ResolveUnit("func f\\<TItem, core.String extends Comparable>(x: TItem) { }\n" +
                "interface Comparable { }\n");
            TestHarness.CheckSemanticError("Target 非泛型参数", u5.Diagnostics,
                "Constraint target must be a generic parameter of this declaration");

            // Bound 解析失败：约束不填充、诊断只在解析处
            var (u6, _) = ResolveUnit("func f\\<TItem extends Missing>(x: TItem) { }\n");
            TestHarness.CheckSemanticError("Bound 未解析", u6.Diagnostics, "Unresolved type or namespace: 'Missing'");
            var gp = u6.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "f").GenericParameters[0];
            TestHarness.CheckTrue("Bound 失败约束不填充（毒化）", gp.Constraints.Count == 0);
        }

        // ===== 子任务 7b：ext 成员注册 =====
        private static void TestExtRegistration()
        {
            TestHarness.Section("P2 Extension Registration");

            var (unit, _) = ResolveUnit(
                "ext func String.poke() { }\n" +
                "ext var String.isEmpty: bool\n" +
                "ext var String.data: LocalUser\n" +
                "class LocalUser { }\n");
            var b = unit.Symbols.Bootstrap;
            CheckNoErrors("无诊断（ext 注册）", unit);
            var stringType = b.String;
            var poke = stringType.Methods.Single(m => m.Name == "poke");
            TestHarness.CheckTrue("ext 方法注册到目标类型", ReferenceEquals(poke.Owner, stringType));
            TestHarness.CheckTrue("ext 方法 canonical 带目标前缀",
                CanonicalSymbolPrinter.Print(poke).StartsWith("core::String$"));
            var isEmpty = stringType.Fields.Single(f => f.Name == "isEmpty");
            TestHarness.CheckTrue("ext 字段注册 + 类型解析",
                ReferenceEquals(isEmpty.FieldType, b.Bool) && ReferenceEquals(isEmpty.Owner, stringType));
            // ext 实例成员不受全局/静态闸门约束（注册后是目标类型实例字段）
            TestHarness.CheckTrue("ext 实例字段持 local object 不触闸门",
                stringType.Fields.Any(f => f.Name == "data") && !unit.Diagnostics.HasErrors);

            var (u2, _) = ResolveUnit("ext func Missing.foo() { }\n");
            TestHarness.CheckSemanticError("ext 目标未解析", u2.Diagnostics, "Unresolved extension target: 'Missing'");
            var (u3, _) = ResolveUnit("namespace a.b\next func a.b.foo() { }\n");
            TestHarness.CheckSemanticError("ext 目标非类型", u3.Diagnostics, "Extension target 'a.b' must be a type");
            var (u4, _) = ResolveUnit("class Local { }\next static var Local.hook: Local\n");
            TestHarness.CheckSemanticError("ext 静态字段受闸门", u4.Diagnostics,
                "Global or static field 'hook' must have a shared-safe type");
        }

        // ===== 子任务 7a/7c：wrapper 适用性与目标矩阵（§14.9）=====
        private const string WrapperPrelude =
            "@WrapperTarget(.Entity)\nwrapper EntityW { }\n" +
            "@WrapperTarget(.Value)\nwrapper ValueW { }\n" +
            "@WrapperTarget(.Method)\nwrapper MethodW { }\n" +
            "@WrapperTarget(.Entity)\nshared wrapper SharedEntityW { }\n" +
            "@WrapperTarget(.Method)\nshared wrapper SharedMethodW { }\n";

        private static void TestWrapperApplications()
        {
            TestHarness.Section("P2 Wrapper Target Matrix (§14.9)");

            // @WrapperTarget 声明侧
            var (u1, _) = ResolveUnit("wrapper NoTarget { }\n");
            TestHarness.CheckSemanticError("wrapper 缺 @WrapperTarget", u1.Diagnostics,
                "requires @WrapperTarget(.Entity/.Value/.Method)");
            var (u2, _) = ResolveUnit("@WrapperTarget(.Entity)\nclass C { }\n");
            TestHarness.CheckSemanticError("@WrapperTarget 挂非 wrapper", u2.Diagnostics,
                "@WrapperTarget can only be applied to wrapper declarations");
            var (u3, _) = ResolveUnit("@WrapperTarget(.Nope)\nwrapper W { }\n");
            TestHarness.CheckSemanticError("@WrapperTarget 实参非法", u3.Diagnostics,
                "@WrapperTarget expects .Entity, .Value or .Method");

            // 类别匹配（互斥三分类）
            var (u4, _) = ResolveUnit(WrapperPrelude + "@EntityW\nvar x: i32\n");
            TestHarness.CheckSemanticError("Entity wrapper 挂变量", u4.Diagnostics,
                "Entity wrapper 'EntityW' can only be applied to type declarations");
            var (u5, _) = ResolveUnit(WrapperPrelude + "@ValueW\nclass C { }\n");
            TestHarness.CheckSemanticError("Value wrapper 挂类型", u5.Diagnostics,
                "Value wrapper 'ValueW' can only be applied to fields and variables");
            var (u6, _) = ResolveUnit(WrapperPrelude + "@MethodW\nclass C { }\n");
            TestHarness.CheckSemanticError("Method wrapper 挂类型", u6.Diagnostics,
                "Method wrapper 'MethodW' can only be applied to methods");

            // 宿主可内嵌性 + 矩阵 D（类型目标）
            var (u7, _) = ResolveUnit(WrapperPrelude + "@EntityW\nstruct S { }\n");
            TestHarness.CheckSemanticError("非 rich struct 不能被修饰", u7.Diagnostics,
                "Non-rich struct 'S' cannot be wrapped");
            var (u8, _) = ResolveUnit(WrapperPrelude + "@EntityW\nshared class C { }\n");
            TestHarness.CheckSemanticError("矩阵 D：非 shared wrapper 挂 shared 类型", u8.Diagnostics,
                "Non-shared wrapper 'EntityW' cannot wrap shared type 'C'");
            var (ok1, _) = ResolveUnit(WrapperPrelude +
                "@EntityW\nclass C { }\n" +
                "@EntityW\nrich struct S { }\n" +
                "@EntityW\ninterface I { }\n" +
                "@SharedEntityW\nshared class C2 { }\n");
            TestHarness.CheckTrue("矩阵 D 合法集（class/rich struct/interface + shared wrapper 挂 shared）",
                !ok1.Diagnostics.HasErrors);
            var wrapped = GlobalType(ok1, "C");
            TestHarness.CheckTrue("AppliedWrappers 记录（引用相等 + 目标类别）",
                wrapped.AppliedWrappers.Count == 1
                && ReferenceEquals(wrapped.AppliedWrappers[0], GlobalType(ok1, "EntityW"))
                && GlobalType(ok1, "EntityW").WrapperTarget == WrapperTargetKind.Entity);

            // 矩阵 A（方法目标）
            var (u9, _) = ResolveUnit(WrapperPrelude + "@MethodW\nfunc g() { }\n");
            TestHarness.CheckSemanticError("矩阵 A：非 shared wrapper 挂全局方法", u9.Diagnostics,
                "cannot wrap global or static method 'g'");
            var (u10, _) = ResolveUnit(WrapperPrelude +
                "shared class C { @MethodW\nfunc m() { } }\n");
            TestHarness.CheckSemanticError("矩阵 A：非 shared wrapper 挂 shared 类型方法", u10.Diagnostics,
                "cannot wrap method 'm' of shared type 'C'");
            var (u11, _) = ResolveUnit(WrapperPrelude +
                "class C { @MethodW\nstatic func m() { } }\n");
            TestHarness.CheckSemanticError("矩阵 A：非 shared wrapper 挂静态方法", u11.Diagnostics,
                "cannot wrap global or static method 'm'");
            var (u12, _) = ResolveUnit(WrapperPrelude +
                "struct S { @MethodW\nfunc m() { } }\n");
            TestHarness.CheckSemanticError("非 rich struct 实例方法不能挂 Method wrapper", u12.Diagnostics,
                "Instance method 'm' of non-rich struct 'S' cannot be wrapped");
            var (ok2, _) = ResolveUnit(WrapperPrelude +
                "@SharedMethodW\nfunc g() { }\n" +
                "class C { @MethodW\nfunc m() { } }\n");
            TestHarness.CheckTrue("矩阵 A 合法集（shared wrapper 挂全局 + 非 shared 挂 local 实例）",
                !ok2.Diagnostics.HasErrors);

            // 矩阵 B（字段目标）
            var (u13, _) = ResolveUnit(WrapperPrelude + "@ValueW\nvar gf: i32\n");
            TestHarness.CheckSemanticError("矩阵 B：非 shared wrapper 挂全局字段", u13.Diagnostics,
                "cannot wrap global or static field 'gf'");
            var (u14, _) = ResolveUnit(WrapperPrelude +
                "shared class C { @ValueW\nvar f: i32 }\n");
            TestHarness.CheckSemanticError("矩阵 B：非 shared wrapper 挂 shared 类型字段", u14.Diagnostics,
                "cannot wrap field 'f' of shared type 'C'");
            var (u15, _) = ResolveUnit(WrapperPrelude +
                "struct S { @ValueW\nvar f: i32 }\n");
            TestHarness.CheckSemanticError("非 rich struct 字段不能挂 Value wrapper", u15.Diagnostics,
                "Instance field 'f' of non-rich struct 'S' cannot be wrapped");
            var (ok3, _) = ResolveUnit(WrapperPrelude +
                "class C { @ValueW\nvar f: i32 }\n" +
                "shared class C2 { @ValueW\nvar f: i32 }\n");
            TestHarness.CheckSemanticError("矩阵 B 复查：shared 类型字段非法", ok3.Diagnostics,
                "cannot wrap field 'f' of shared type 'C2'");
            var (ok4, _) = ResolveUnit(WrapperPrelude + "class C { @ValueW\nvar f: i32 }\n");
            CheckNoErrors("矩阵 B 合法（非 shared 目标实例字段）", ok4);

            // interface 实现者传染（§14.9：在实现者声明处检查）
            var (u16, _) = ResolveUnit(WrapperPrelude +
                "@EntityW\ninterface IW { }\n" +
                "shared class Impl implements IW { }\n");
            TestHarness.CheckSemanticError("shared 实现者 × 非 shared wrapper interface", u16.Diagnostics,
                "Shared type 'Impl' cannot implement interface 'IW' wrapped by non-shared wrapper 'EntityW'");
            var (ok5, _) = ResolveUnit(WrapperPrelude +
                "@EntityW\ninterface IW { }\n" +
                "class Impl implements IW { }\n");
            CheckNoErrors("非 shared 实现者合法", ok5);

            // 注解名解析失败 / 非 wrapper
            var (u17, _) = ResolveUnit("@Missing\nclass C { }\n");
            TestHarness.CheckSemanticError("注解名未解析", u17.Diagnostics, "Unresolved type or namespace: 'Missing'");
            var (u18, _) = ResolveUnit("class NotWrapper { }\n@NotWrapper\nclass C { }\n");
            TestHarness.CheckSemanticError("注解名非 wrapper", u18.Diagnostics, "'NotWrapper' is not a wrapper type");
        }

        // ===== 子任务 3b：native 函数声明（§4.6）=====
        private static void TestNativeDeclarations()
        {
            TestHarness.Section("P2 Native Functions (§4.6)");

            // 形态规则：无体 / 成员必 static / 仅函数（init/operator/类型/字段均拒绝）
            var (u1, _) = ResolveUnit("@NativeLibrary(\"rt\")\nnative func f(): i32 { return 1 }\n");
            TestHarness.CheckSemanticError("native 带函数体", u1.Diagnostics,
                "Native function 'f' must not have a body");
            var (u2, _) = ResolveUnit("class C { @NativeLibrary(\"rt\")\nnative func f(): i32 }\n");
            TestHarness.CheckSemanticError("成员 native 非 static", u2.Diagnostics,
                "Native member function 'f' must be 'static'");
            var (u3a, _) = ResolveUnit("class C { native init() }\n");
            TestHarness.CheckSemanticError("native init", u3a.Diagnostics,
                "'native' can only be applied to functions");
            var (u3b, _) = ResolveUnit("class C { native operator plus(other: C): C }\n");
            TestHarness.CheckSemanticError("native operator", u3b.Diagnostics,
                "'native' can only be applied to functions");
            var (u3c, _) = ResolveUnit("native class C { }\n");
            TestHarness.CheckSemanticError("native 类型", u3c.Diagnostics,
                "'native' can only be applied to functions");
            var (u3d, _) = ResolveUnit("native var x: i32\n");
            TestHarness.CheckSemanticError("native 变量", u3d.Diagnostics,
                "'native' can only be applied to functions");

            // 组合禁忌：async / 泛型参数列表 / 同容器同名重载
            var (u4, _) = ResolveUnit("@NativeLibrary(\"rt\")\nasync native func f(): i32\n");
            TestHarness.CheckSemanticError("native × async", u4.Diagnostics,
                "Native function 'f' cannot be 'async'");
            var (u5, _) = ResolveUnit("@NativeLibrary(\"rt\")\nnative func f\\<T>(x: i32)\n");
            TestHarness.CheckSemanticError("native × 泛型参数列表", u5.Diagnostics,
                "Native function 'f' cannot declare generic parameters");
            var (u6, _) = ResolveUnit(
                "@NativeLibrary(\"rt\")\nnative func dup(x: i32)\n" +
                "func dup(x: i32, y: i32) { }\n");
            TestHarness.CheckSemanticError("native × 同容器重载", u6.Diagnostics,
                "Native function 'dup' cannot be overloaded");

            // 参数/返回类型白名单（用户类型 / Object / Nullable 构造均拒绝）
            var (u7a, _) = ResolveUnit(
                "class User { }\n" +
                "@NativeLibrary(\"rt\")\nnative func f(u: User)\n");
            TestHarness.CheckSemanticError("参数为用户类型", u7a.Diagnostics,
                "Parameter 'u' of native function 'f' must be a primitive type");
            var (u7b, _) = ResolveUnit("@NativeLibrary(\"rt\")\nnative func g(): Object\n");
            TestHarness.CheckSemanticError("返回 Object", u7b.Diagnostics,
                "Return type of native function 'g' must be a primitive type");
            var (u7c, _) = ResolveUnit("@NativeLibrary(\"rt\")\nnative func h(x: i32?)\n");
            TestHarness.CheckSemanticError("参数为 Nullable 构造", u7c.Diagnostics,
                "Parameter 'x' of native function 'h' must be a primitive type");

            // 内建注解：@NativeLibrary 必填；实参必须恰好一个字符串字面量
            var (u8, _) = ResolveUnit("native func f(x: i32)\n");
            TestHarness.CheckSemanticError("缺 @NativeLibrary", u8.Diagnostics,
                "Native function 'f' requires @NativeLibrary");
            var (u9a, _) = ResolveUnit("@NativeLibrary\nnative func f(x: i32)\n");
            TestHarness.CheckSemanticError("@NativeLibrary 无实参", u9a.Diagnostics,
                "@NativeLibrary expects exactly one string literal argument");
            var (u9b, _) = ResolveUnit("@NativeLibrary(42)\nnative func f(x: i32)\n");
            TestHarness.CheckSemanticError("@NativeLibrary 非字符串实参", u9b.Diagnostics,
                "@NativeLibrary expects exactly one string literal argument");
            var (u9c, _) = ResolveUnit(
                "@NativeLibrary(\"rt\")\n@NativeSymbol()\nnative func f(x: i32)\n");
            TestHarness.CheckSemanticError("@NativeSymbol 空实参", u9c.Diagnostics,
                "@NativeSymbol expects exactly one string literal argument");

            // 内建注解只允许出现在 native 函数声明上（且不得被 wrapper 检查误伤）
            var (u10a, _) = ResolveUnit("@NativeLibrary(\"rt\")\nfunc f(): i32 { return 1 }\n");
            TestHarness.CheckSemanticError("@NativeLibrary 挂普通函数", u10a.Diagnostics,
                "@NativeLibrary can only be applied to native functions");
            TestHarness.CheckTrue("内建注解豁免 wrapper 解析（仅此一条诊断）",
                u10a.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1);
            var (u10b, _) = ResolveUnit("@NativeSymbol(\"x\")\nclass C { }\n");
            TestHarness.CheckSemanticError("@NativeSymbol 挂类型", u10b.Diagnostics,
                "@NativeSymbol can only be applied to native functions");

            // 正例：stdlib 形态（§4.6 示例，命名空间 + 类成员 + 双注解）
            var (ok1, _) = ResolveUnit(
                "namespace core.io\n" +
                "pub class Console {\n" +
                "@NativeLibrary(\"latte_rt\")\n" +
                "@NativeSymbol(\"print\")\n" +
                "priv static native func print(text: String)\n" +
                "}\n");
            CheckNoErrors("stdlib 形态无诊断", ok1);
            var print = NsOf(ok1, "core", "io").Types.Single(t => t.Name == "Console")
                .Methods.Single(m => m.Name == "print");
            TestHarness.CheckTrue("IsNative/IsStatic 标记位", print.IsNative && print.IsStatic);
            TestHarness.Check("NativeSymbol 取注解实参", print.NativeSymbol ?? "", "print");
            TestHarness.Check("NativeLibrary 取注解实参", print.NativeLibrary ?? "", "latte_rt");

            // 正例：@NativeSymbol 缺省取函数名
            var (ok2, _) = ResolveUnit(
                "class C {\n" +
                "@NativeLibrary(\"latte_rt\")\n" +
                "priv static native func printErr(text: String)\n" +
                "}\n");
            CheckNoErrors("缺省 @NativeSymbol 无诊断", ok2);
            var printErr = GlobalType(ok2, "C").Methods.Single(m => m.Name == "printErr");
            TestHarness.CheckTrue("IsNative 标记位", printErr.IsNative);
            TestHarness.Check("NativeSymbol 缺省取函数名", printErr.NativeSymbol ?? "", "printErr");
            TestHarness.Check("NativeLibrary 取注解实参", printErr.NativeLibrary ?? "", "latte_rt");

            // 正例：全局 native 函数（无需 static）+ 白名单全形态 + void 返回 + 路径形态注解
            var (ok3, _) = ResolveUnit(
                "@NativeLibrary(\"rt\")\n" +
                "native func conv(a: i8, b: u64, c: float, d: double, e: bool, f: char, g: String): i64\n" +
                "@core.NativeLibrary(\"rt\")\n" +
                "native func poke(x: i32)\n");
            CheckNoErrors("全局 native + 白名单全形态无诊断", ok3);
            var conv = ok3.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "conv");
            TestHarness.CheckTrue("全局函数 IsNative 且无 static 要求", conv.IsNative && !conv.IsStatic);
            TestHarness.Check("全局函数 NativeSymbol 缺省", conv.NativeSymbol ?? "", "conv");
            var poke = ok3.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "poke");
            TestHarness.Check("路径形态注解同样生效", poke.NativeLibrary ?? "", "rt");
        }

        // ===== P2 收尾：符号图冻结 =====
        // ===== 访问级别写符号（SYNTAX §16；BIL 发射与 S8 使用点访问控制消费）=====
        private static void TestAccessibility()
        {
            TestHarness.Section("P2 Accessibility");

            var (unit, _) = ResolveUnit(
                "pub class A { pub var x: i32\n protected func f() {}\n internal var y: i32 }\n" +
                "class B { var z: i32 }\n" +
                "pub func g() {}\n" +
                "func h() {}\n" +
                "internal var v: i32\n");
            CheckNoErrors("无诊断", unit);

            var a = GlobalType(unit, "A");
            TestHarness.CheckTrue("pub class => Public", a.Accessibility == Accessibility.Public);
            TestHarness.CheckTrue("pub 字段 => Public",
                a.Fields.Single(f => f.Name == "x").Accessibility == Accessibility.Public);
            TestHarness.CheckTrue("protected 方法 => Protected",
                a.Methods.Single(m => m.Name == "f").Accessibility == Accessibility.Protected);
            TestHarness.CheckTrue("internal 字段 => Internal",
                a.Fields.Single(f => f.Name == "y").Accessibility == Accessibility.Internal);
            TestHarness.CheckTrue("无修饰符 class => Private（默认）",
                GlobalType(unit, "B").Accessibility == Accessibility.Private);
            TestHarness.CheckTrue("无修饰符字段 => Private（默认）",
                GlobalType(unit, "B").Fields.Single(f => f.Name == "z").Accessibility == Accessibility.Private);

            var global = unit.Symbols.GlobalNamespace;
            TestHarness.CheckTrue("pub 全局函数 => Public",
                global.Methods.Single(m => m.Name == "g").Accessibility == Accessibility.Public);
            TestHarness.CheckTrue("无修饰符全局函数 => Private（默认）",
                global.Methods.Single(m => m.Name == "h").Accessibility == Accessibility.Private);
            TestHarness.CheckTrue("internal 全局变量 => Internal",
                global.Fields.Single(f => f.Name == "v").Accessibility == Accessibility.Internal);

            TestHarness.Blank();
        }

        private static void TestFreeze()
        {
            TestHarness.Section("P2 Freeze");
            var (unit, _) = ResolveUnit("var x: i32\n");
            TestHarness.CheckTrue("P2 结束 Freeze", unit.Symbols.IsFrozen);
        }

        // P2 阶段断言（S8e 声明侧用例）：消息命中且诊断确为 P2 落袋
        private static void CheckP2Error(string label, CompilationUnit unit,
            string expectedMessagePart)
        {
            TestHarness.CheckSemanticError(label, unit.Diagnostics, expectedMessagePart);
            TestHarness.CheckTrue(label + "（确为 P2 阶段）",
                unit.Diagnostics.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error
                    && d.Phase == DiagnosticPhase.P2 && d.Message.Contains(expectedMessagePart)));
        }

        // 解析合法源后手工变异 AST 再进 P1+P2（S8e：parser 层不可达形态的
        // P2 防御检查直达——ASTIntegrityValidator 只在 Parse 内运行，变异安全）
        private static (CompilationUnit Unit, DeclarationCollection Decls) ResolveMutated(
            string source, Action<RootASTNode> mutate)
        {
            var root = TestHarness.ParseRoot(source);
            mutate(root);
            var unit = new CompilationUnit(new[] { root });
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, decls);
        }

        // 取单类单成员源的唯一字段声明节点（ResolveMutated 改造目标定位）
        private static VariableDeclarationASTNode FirstMemberVariable(RootASTNode root)
        {
            return (VariableDeclarationASTNode)
                ((ClassDeclarationASTNode)root.Declarations[0]).Members[0];
        }

        // ===== S8e：访问器声明侧（SYNTAX §9.4/§9.4.1——修饰符白名单/
        // const+set/无体 computed/类型标注/可见性落定）=====
        private static void TestAccessorDeclarations()
        {
            TestHarness.Section("P2 Accessor Declarations");

            // 访问器修饰符白名单：仅访问级别（static 上访问器即错误）
            var (unit, _) = ResolveUnit(
                "pub class C {\n" +
                "    pub var v: i32 {\n" +
                "        static get(value: _) { return value }\n" +
                "    }\n" +
                "}\n");
            CheckP2Error("访问器修饰符白名单（static get）", unit,
                "'static' is not allowed here (access modifiers only");

            // const 字段不得声明 setter
            var (unit2, _) = ResolveUnit(
                "pub class C {\n" +
                "    const v: i32 {\n" +
                "        get(value: _) { return value }\n" +
                "        set(value: _) { }\n" +
                "    }\n" +
                "}\n");
            CheckP2Error("const 字段声明 setter", unit2,
                "Const field 'v' cannot declare a setter");

            // 无体 + 计算形态：编译器无法生成计算实现（getter/setter 同形各一）。
            // 注：parser 层要求 (_: _) 必带体，该形态从源码不可达——解析合法的
            // 自动访问器后手工改造 HasBackingField=false 直达 P2 防御检查
            var (unit3, _) = ResolveMutated(
                "pub class C {\n" +
                "    pub var v: i32 {\n" +
                "        get\n" +
                "    }\n" +
                "}\n",
                root => FirstMemberVariable(root).Getter!.HasBackingField = false);
            CheckP2Error("无体 computed getter", unit3,
                "Computed getter of 'v' must have a body");
            var (unit4, _) = ResolveMutated(
                "pub class C {\n" +
                "    pub var w: i32 {\n" +
                "        set\n" +
                "    }\n" +
                "}\n",
                root => FirstMemberVariable(root).Setter!.HasBackingField = false);
            CheckP2Error("无体 computed setter", unit4,
                "Computed setter of 'w' must have a body");

            // 带访问器字段必须有类型标注（无标注字段的类型推断归 P3，不共存）
            var (unit5, _) = ResolveUnit(
                "pub class C {\n" +
                "    var v {\n" +
                "        get(value: _) { return 1 }\n" +
                "    }\n" +
                "}\n");
            CheckP2Error("无类型标注带访问器", unit5,
                "Field 'v' with accessors requires a type annotation");

            // 正例：可见性落定 = 访问器显式修饰 ?? 字段声明级别（§9.4.1）
            var (unit6, _) = ResolveUnit(
                "namespace app\n" +
                "pub var a: i32 {\n" +
                "    get(value: _) { return value }\n" +
                "    set(value: _) { }\n" +
                "}\n" +
                "internal var b: i32 {\n" +
                "    pub get(value: _) { return value }\n" +
                "    priv set(value: _) { }\n" +
                "}\n");
            CheckNoErrors("无诊断（访问器可见性落定）", unit6);
            var appFields = NsOf(unit6, "app").Fields;
            var a = appFields.Single(f => f.Name == "a");
            TestHarness.CheckTrue("无显式修饰 getter 取字段级别（pub）",
                a.Getter!.Accessibility == Accessibility.Public);
            TestHarness.CheckTrue("无显式修饰 setter 取字段级别（pub）",
                a.Setter!.Accessibility == Accessibility.Public);
            var b = appFields.Single(f => f.Name == "b");
            TestHarness.CheckTrue("显式 pub getter（字段 internal 不传染）",
                b.Getter!.Accessibility == Accessibility.Public);
            TestHarness.CheckTrue("显式 priv setter",
                b.Setter!.Accessibility == Accessibility.Private);
        }

        // ===== S8e：override/open/abstract 修饰符位置（SYNTAX §9.2.1，
        // ModifierChecker 成员侧负例补充）=====
        private static void TestOverrideModifiers()
        {
            TestHarness.Section("P2 Override Modifiers");

            // 字段写 override：open/abstract/override 仅普通成员方法
            var (unit, _) = ResolveUnit(
                "pub class C {\n" +
                "    override var v: i32\n" +
                "}\n");
            CheckP2Error("字段写 override", unit,
                "'open'/'abstract'/'override' can only be applied to member methods");

            // static 方法写 override（静态无多态）
            var (unit2, _) = ResolveUnit(
                "pub class C {\n" +
                "    static override func m(): i32 { return 1 }\n" +
                "}\n");
            CheckP2Error("static + override", unit2,
                "'open'/'abstract'/'override' cannot be applied to static methods");

            // 接口成员天然可覆写：接口内写 open 为冗余错误
            var (unit3, _) = ResolveUnit(
                "pub interface I {\n" +
                "    open func m(): i32\n" +
                "}\n");
            CheckP2Error("接口内 open 冗余", unit3,
                "'open'/'abstract' is redundant on interface members");

            // open × abstract 互斥（成员级，同类型级规则）
            var (unit4, _) = ResolveUnit(
                "pub abstract class C {\n" +
                "    open abstract func m(): i32\n" +
                "}\n");
            CheckP2Error("open × abstract 互斥（成员）", unit4,
                "'open' and 'abstract' are mutually exclusive");

            // abstract 方法不得带体
            var (unit5, _) = ResolveUnit(
                "pub abstract class C {\n" +
                "    abstract func m(): i32 { return 1 }\n" +
                "}\n");
            CheckP2Error("abstract 带体", unit5,
                "'m': abstract method cannot have a body");
        }

        // ===== S8e：声明侧访问控制（SYNTAX §16.1——类型引用/继承/接口
        // 均为使用点，跨文件 priv 类型即拒绝；诊断确为 P2 阶段落袋）=====
        private static void TestDeclarationSiteAccess()
        {
            TestHarness.Section("P2 Declaration-Site Access");

            // 跨文件 priv 类型作字段类型
            var (unit, _) = ResolveUnit(
                "class Hidden { }\n",
                "pub class C {\n" +
                "    var h: Hidden\n" +
                "}\n");
            CheckP2Error("priv 类型作字段类型（跨文件）", unit,
                "'Hidden' is inaccessible due to its accessibility level");

            // 跨文件 priv 类型作基类
            var (unit2, _) = ResolveUnit(
                "class Hidden { }\n",
                "pub class C : Hidden { }\n");
            CheckP2Error("priv 类型作基类（跨文件）", unit2,
                "'Hidden' is inaccessible due to its accessibility level");

            // 跨文件 priv 接口作 implements
            var (unit3, _) = ResolveUnit(
                "interface IHidden { }\n",
                "pub class C implements IHidden { }\n");
            CheckP2Error("priv 接口作 implements（跨文件）", unit3,
                "'IHidden' is inaccessible due to its accessibility level");
        }
    }
}
