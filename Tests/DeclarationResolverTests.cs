using System.Linq;

namespace RigiCompiler.Tests
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
            TestGenericVariance();
            TestArityLookup();
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
            TestProxyShapeChecking();
            TestProxyDispatchChains();
            TestDowngradeChains();
            TestNativeDeclarations();
            TestAccessibility();
            TestAccessorDeclarations();
            TestOverrideModifiers();
            TestDeclarationSiteAccess();
            TestConversionOperators();
            TestAsyncDeclarationGates();
            TestConstructedBaseTypeBackfill();
            TestExtDuplicateDetection();
            TestOverrideGenericArity();
            TestNamespaceNotAType();
            TestBareGenericDefinitionArity();
            TestInitMappingBuiltinField();
            TestExtNativeGates();
            TestNamespaceSegmentPriority();
            TestInterfaceFieldDeclaration();
            TestDuplicateInterfaceImplementation();
            TestStaticOperatorDeclaration();
            TestNamedImportResolution();
            TestEnumCaseStructure();
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

        private static void TestGenericVariance()
        {
            TestHarness.Section("P2 Generic Variance (§3.6)");

            var (ok, _) = ResolveUnit(
                "class Producer\\<out T> {\n" +
                "    pub const value: T\n" +
                "    pub func get(): T { return value }\n" +
                "}\n" +
                "class Consumer\\<in T> {\n" +
                "    pub func put(value: T) { }\n" +
                "}\n");
            CheckNoErrors("合法 covariant/contravariant 声明", ok);

            var (badOut, _) = ResolveUnit(
                "class Producer\\<out T> { pub func put(value: T) { } }\n");
            TestHarness.CheckSemanticError("out 不能出现在方法参数", badOut.Diagnostics,
                "covariant parameter 'T' cannot be used in parameter 'value' of method 'put'");

            var (badIn, _) = ResolveUnit(
                "class Consumer\\<in T> { pub func get(): T { return default } }\n");
            TestHarness.CheckSemanticError("in 不能出现在方法返回值", badIn.Diagnostics,
                "contravariant parameter 'T' cannot be used in return type of method 'get'");

            var (badFunction, _) = ResolveUnit(
                "func f\\<out T>(): T { return default }\n");
            TestHarness.CheckSemanticError("函数泛型参数不接受型变", badFunction.Diagnostics,
                "variance is only allowed on type declarations");
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

        // ===== 子任务 1：同名不同元数类型查找分流（S10，SYNTAX §15.3——
        // 裸名解析到非泛型声明、带实参解析到泛型声明；无对应元数时回退
        // 既有元数诊断路径）=====
        private static void TestArityLookup()
        {
            TestHarness.Section("P2 Type References (arity distinction)");

            var (unit, _) = ResolveUnit(
                "shared class Task { }\n" +
                "shared class Task\\<TResult> { }\n" +
                "var bare: Task\n" +
                "var generic: Task\\<i32>\n" +
                "var deep: Task\\<Task\\<String>>\n");
            var global = unit.Symbols.GlobalNamespace;

            CheckNoErrors("无诊断", unit);
            var bareField = global.Fields.Single(f => f.Name == "bare").FieldType;
            TestHarness.CheckTrue("裸名 Task 解析到非泛型声明",
                bareField is TypeSymbol bareType
                && ReferenceEquals(bareType, global.Types.Single(t => t.Name == "Task"
                    && t.GenericParameters.Count == 0)));
            var genericField = global.Fields.Single(f => f.Name == "generic").FieldType;
            TestHarness.CheckTrue("带实参 Task\\<i32> 解析到泛型声明构造",
                genericField is TypeSymbol genericType
                && genericType.ConstructedFrom != null
                && ReferenceEquals(genericType.ConstructedFrom,
                    global.Types.Single(t => t.Name == "Task" && t.GenericParameters.Count == 1))
                && genericType.TypeArguments!.Count == 1
                && ReferenceEquals(genericType.TypeArguments[0], unit.Symbols.Bootstrap.Int32));
            var deepField = global.Fields.Single(f => f.Name == "deep").FieldType;
            TestHarness.CheckTrue("嵌套 Task\\<Task\\<String>> 逐层正确构造",
                deepField is TypeSymbol deepType
                && deepType.ConstructedFrom != null
                && deepType.TypeArguments![0] is TypeSymbol inner
                && inner.ConstructedFrom?.Name == "Task"
                && ReferenceEquals(inner.TypeArguments![0], unit.Symbols.Bootstrap.String));

            // 带实参但只有非泛型声明 → 既有元数诊断
            var (unit2, _) = ResolveUnit("class Task { }\nvar x: Task\\<i32>\n");
            TestHarness.CheckSemanticError("仅非泛型声明带实参报元数诊断", unit2.Diagnostics,
                "'Task' expects 0 type argument(s), got 1");

            TestHarness.Blank();
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
            // MappedField 回写（§9.3 合成的凭据）：省类型/显式类型两分支引用相等
            TestHarness.CheckTrue("省类型映射 MappedField 落定（引用相等）",
                ReferenceEquals(init.Parameters[0].MappedField,
                    point.Fields.Single(f => f.Name == "x"))
                && ReferenceEquals(init.Parameters[1].MappedField,
                    point.Fields.Single(f => f.Name == "y")));
            TestHarness.CheckTrue("显式类型映射 MappedField 落定（引用相等）",
                ReferenceEquals(init2.Parameters[0].MappedField,
                    point.Fields.Single(f => f.Name == "x")));

            var (unit2, _) = ResolveUnit("class C { init(_ -> missing) }\n");
            TestHarness.CheckSemanticError("映射未知字段", unit2.Diagnostics,
                "Init parameter mapping targets unknown field: 'missing'");

            // 显式写类型的映射参数同样做字段存在性检查（两分支同规则）
            var (unit2b, _) = ResolveUnit("class C { init(v: i32 -> missing) }\n");
            TestHarness.CheckSemanticError("显式类型映射未知字段", unit2b.Diagnostics,
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
            // 非 rich enum struct 写 open：同因一报（不再叠加 non-rich struct 条）
            TestHarness.CheckTrue("enum struct open 同因一报",
                u8.Diagnostics.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) == 1,
                string.Join("; ", u8.Diagnostics.Diagnostics.Select(d => d.Message)));
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

            // ===== M80：ext 目标种类与闭包两闸门 =====
            // interface 禁字段（§11，同 ModifierChecker 成员口径；ext 注入同禁），
            // ext 方法不受限
            var (u5, _) = ResolveUnit(
                "interface IFly { func fly() }\n" +
                "ext var IFly.speed: i32\n" +
                "ext func IFly.swoop() { }\n");
            TestHarness.CheckSemanticError("ext 字段禁注 interface", u5.Diagnostics,
                "Extension field 'speed' cannot target interface 'IFly' (interfaces cannot declare fields)");
            TestHarness.CheckTrue("ext 方法注册 interface 不受限",
                u5.Symbols.GlobalNamespace.Types.Single(t => t.Name == "IFly")
                    .Methods.Any(m => m.Name == "swoop"));
            TestHarness.CheckTrue("被拒 ext 字段不注册（interface）",
                !u5.Symbols.GlobalNamespace.Types.Single(t => t.Name == "IFly")
                    .Fields.Any(f => f.Name == "speed"));

            // ext 实例字段注册后与目标体内声明同受 §3.1.1 闭包表约束
            var (u6, _) = ResolveUnit(
                "class Local { }\n" +
                "shared class S { }\n" +
                "ext var S.data: Local\n");
            TestHarness.CheckSemanticError("shared 目标拒 local object ext 字段", u6.Diagnostics,
                "'S' is shared and cannot hold local object field 'data'");
            TestHarness.CheckTrue("被拒 ext 字段不注册（shared）",
                !u6.Symbols.GlobalNamespace.Types.Single(t => t.Name == "S")
                    .Fields.Any(f => f.Name == "data"));

            var (u7, _) = ResolveUnit(
                "class Local { }\n" +
                "struct P { var x: i32 }\n" +
                "ext var P.tag: Local\n");
            TestHarness.CheckSemanticError("非 rich struct 拒 object ext 字段", u7.Diagnostics,
                "Non-rich struct 'P' cannot hold object field 'tag'");

            // 正例：shared 目标持 shared-safe 字段、非 rich struct 持纯值字段
            var (u8, _) = ResolveUnit(
                "shared class S { }\n" +
                "ext var S.count: i32\n" +
                "struct P { var x: i32 }\n" +
                "ext var P.id: i32\n");
            CheckNoErrors("无诊断（ext 闭包正例）", u8);

            // ===== M81 裁决落地：ext 目标泛型元数/歧义 + protected 禁令 =====
            // 裸名命中泛型定义 → 元数诊断（§4.4；与 ResolveSymbolPath 同款措辞）
            var (u9, _) = ResolveUnit(
                "class Box\\<T> { }\n" +
                "ext func Box.foo() { }\n");
            TestHarness.CheckSemanticError("ext 目标裸名命中泛型定义", u9.Diagnostics,
                "'Box' expects 1 type argument(s), got 0");
            TestHarness.CheckTrue("被拒 ext 方法不注册（裸名泛型）",
                !u9.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Box")
                    .Methods.Any(m => m.Name == "foo"));

            // 同名不同元数多命中 → 歧义
            var (u10, _) = ResolveUnit(
                "class Box { }\n" +
                "class Box\\<T> { }\n" +
                "ext func Box.bar() { }\n");
            TestHarness.CheckSemanticError("ext 目标同名不同元数歧义", u10.Diagnostics,
                "Ambiguous extension target: 'Box'");

            // 非泛型目标仍合法（对照）
            var (u11, _) = ResolveUnit(
                "class Box { }\n" +
                "ext func Box.ok() { }\n");
            CheckNoErrors("无诊断（ext 非泛型目标）", u11);

            // protected 不得挂 ext（顶层无 protected，§16.1）
            var (u12, _) = ResolveUnit(
                "class C { }\n" +
                "protected ext func C.p() { }\n");
            TestHarness.CheckSemanticError("ext 禁 protected", u12.Diagnostics,
                "'protected' cannot be applied to extension members");
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
                && ReferenceEquals(wrapped.AppliedWrappers[0].Wrapper, GlobalType(ok1, "EntityW"))
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
                "@EntityW\n" +
                "class Impl implements IW { }\n");
            CheckNoErrors("非 shared 实现者合法", ok5);

            // 注解名解析失败 / 非 wrapper
            var (u17, _) = ResolveUnit("@Missing\nclass C { }\n");
            TestHarness.CheckSemanticError("注解名未解析", u17.Diagnostics, "Unresolved type or namespace: 'Missing'");
            var (u18, _) = ResolveUnit("class NotWrapper { }\n@NotWrapper\nclass C { }\n");
            TestHarness.CheckSemanticError("注解名非 wrapper", u18.Diagnostics, "'NotWrapper' is not a wrapper type");

            // wrapper 继承闭包沿间接 interface 展开，子接口和实现者都必须显式重声明。
            var (u19, _) = ResolveUnit(WrapperPrelude +
                "@EntityW\ninterface IBase { }\n" +
                "interface IChild : IBase { }\n" +
                "class Impl implements IChild { }\n");
            TestHarness.CheckSemanticError("间接 interface wrapper 必须显式重声明", u19.Diagnostics,
                "Entity wrapper 'EntityW' inherited by 'IChild' must be explicitly redeclared");

            // getter/setter 的 wrapper 应用挂在字段声明上；override 访问器沿字段
            // 继承闭包检查显式重声明规则。
            var (u20, _) = ResolveUnit(WrapperPrelude +
                "open class Base {\n" +
                "    @ValueW\n" +
                "    pub var value: i32 {\n" +
                "        open get(value: _) { return value }\n" +
                "    }\n" +
                "}\n" +
                "class Child : Base {\n" +
                "    pub var value: i32 {\n" +
                "        override get(value: _) { return value }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("override getter wrapper 必须显式重声明", u20.Diagnostics,
                "Accessor wrapper 'ValueW' inherited by 'value' must be explicitly redeclared");

            var (ok6, _) = ResolveUnit(WrapperPrelude +
                "open class Base {\n" +
                "    @ValueW\n" +
                "    pub var value: i32 {\n" +
                "        open get(value: _) { return value }\n" +
                "    }\n" +
                "}\n" +
                "class Child : Base {\n" +
                "    @ValueW\n" +
                "    pub var value: i32 {\n" +
                "        override get(value: _) { return value }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("override getter wrapper 显式重声明合法", ok6);
        }

        // ===== S11a：proxy 声明侧形状校验（§14.2/§14.3/§14.4）=====
        private static void TestProxyShapeChecking()
        {
            TestHarness.Section("P2 Proxy Shape Checking (§14.2–§14.4)");

            // 泛型元数：Entity 至多一个（TTarget 角色），Value/Method 零个
            var (g1, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W\\<TTarget, TExtra> { }\n");
            TestHarness.CheckSemanticError("Entity wrapper 两个泛型参数", g1.Diagnostics,
                "Entity wrapper 'W' must declare at most one generic parameter (the TTarget role)");
            var (g2, _) = ResolveUnit(
                "@WrapperTarget(.Value)\nwrapper W\\<TValue> { }\n");
            TestHarness.CheckSemanticError("Value wrapper 泛型参数", g2.Diagnostics,
                "Value wrapper 'W' cannot declare generic parameters (§14.2)");
            var (g3, _) = ResolveUnit(
                "@WrapperTarget(.Method)\nwrapper W\\<TTarget> { }\n");
            TestHarness.CheckSemanticError("Method wrapper 泛型参数", g3.Diagnostics,
                "Method wrapper 'W' cannot declare generic parameters (§14.2)");
            var (g4, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W\\<TTarget> { }\n" +
                "@WrapperTarget(.Entity)\nwrapper W0 { }\n");
            CheckNoErrors("Entity wrapper 零/一个泛型参数均合法", g4);

            // 类别矩阵：proxy 形态 × wrapper 类别
            var (m1, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W { operator .proxy.get\\<T>(value: T): T { return value } }\n");
            TestHarness.CheckSemanticError("Value 形态挂 Entity", m1.Diagnostics,
                "Proxy '.proxy.get' is not allowed on Entity wrapper 'W'");
            var (m2, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W { operator .proxy.call\\<TReturn>(): TReturn { return default } }\n");
            TestHarness.CheckSemanticError("Method 形态挂 Entity", m2.Diagnostics,
                "Proxy '.proxy.call' is not allowed on Entity wrapper 'W'");
            var (m3, _) = ResolveUnit(
                "@WrapperTarget(.Value)\nwrapper W { operator .proxy.doSomething(arg: i32): String { return \"\" } }\n");
            TestHarness.CheckSemanticError("specific 方法挂 Value", m3.Diagnostics,
                "Proxy '.proxy.doSomething' is not allowed on Value wrapper 'W'");
            var (m4, _) = ResolveUnit(
                "@WrapperTarget(.Method)\nwrapper W { operator .proxy.get\\<T>(value: T): T { return value } }\n");
            TestHarness.CheckSemanticError("Value 形态挂 Method", m4.Diagnostics,
                "Proxy '.proxy.get' is not allowed on Method wrapper 'W'");
            var (m5, _) = ResolveUnit(
                "@WrapperTarget(.Method)\n" +
                "wrapper W { operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return default } }\n");
            TestHarness.CheckSemanticError("wildcard 挂 Method", m5.Diagnostics,
                "Proxy '.proxy.*' is not allowed on Method wrapper 'W'");

            // wildcard canonical shape
            var (w1, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper W { operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String): TReturn { return default } }\n");
            TestHarness.CheckSemanticError(".proxy.* 缺参数包", w1.Diagnostics,
                "Wildcard proxy '.proxy.*' must have the canonical shape");
            var (w2, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper W { operator .proxy.*\\<TNamedArgs, TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return default } }\n");
            TestHarness.CheckSemanticError(".proxy.* 泛型结构错", w2.Diagnostics,
                "Wildcard proxy '.proxy.*' must have the canonical shape");
            var (w3, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper W { operator .proxy.get.*\\<TValue>(value: TValue): TValue { return value } }\n");
            TestHarness.CheckSemanticError(".proxy.get.* 缺 symbol", w3.Diagnostics,
                "Wildcard proxy '.proxy.get.*' must have the canonical shape");
            var (w4, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper W { operator .proxy.set.*\\<TValue>(symbol: String, value: TValue): TValue { return value } }\n");
            TestHarness.CheckSemanticError(".proxy.set.* 带返回", w4.Diagnostics,
                "Wildcard proxy '.proxy.set.*' must have the canonical shape");

            // specific 形状
            var (s1, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W { operator .proxy.get.name(value: i32): i32 { return value } }\n");
            TestHarness.CheckSemanticError("specific getter 缺泛型参数", s1.Diagnostics,
                "Accessor proxy '.proxy.get.name' must declare exactly one generic parameter");
            var (s2, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W { operator .proxy.set.name\\<T>(value: T): T { return value } }\n");
            TestHarness.CheckSemanticError("specific setter 带返回", s2.Diagnostics,
                "Accessor proxy '.proxy.set.name' must declare exactly one generic parameter");
            var (s3, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W { operator .proxy.doSomething\\<T>(arg: i32): String { return \"\" } }\n");
            TestHarness.CheckSemanticError("specific 方法带泛型", s3.Diagnostics,
                "Specific proxy '.proxy.doSomething' cannot declare generic parameters (§14.2)");
            var (s4, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\nwrapper W\\<TTarget> { operator .proxy.opr.plus\\<T>(another: TTarget): TTarget { return default } }\n");
            TestHarness.CheckSemanticError("specific operator 带泛型", s4.Diagnostics,
                "Specific proxy '.proxy.opr.plus' cannot declare generic parameters (§14.2)");

            // .proxy.call 双形态（§14.4）
            var (c1, _) = ResolveUnit(
                "@WrapperTarget(.Method)\nwrapper W { operator .proxy.call(.name: i32, args: named Any...): Any { return default } }\n");
            TestHarness.CheckSemanticError(".proxy.call wildcard 形态错", c1.Diagnostics,
                "Wildcard '.proxy.call' must have shape (.name: String, args: named Any...): Any (§14.4)");
            var (c2, _) = ResolveUnit(
                "@WrapperTarget(.Method)\nwrapper W { operator .proxy.call(): Any { return default } }\n");
            TestHarness.CheckSemanticError(".proxy.call specific 缺泛型", c2.Diagnostics,
                "Specific '.proxy.call' must declare exactly one generic parameter used as the return type (§14.4)");

            // 正例：canonical 全形态（Entity 四 wildcard + specific 三类 + TTarget）
            var (ok, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    pub init(level: String = \"INFO\")\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "    operator .proxy.opr.plus(another: TTarget): TTarget { return inner(another) }\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField { return value }\n" +
                "    operator .proxy.set.name\\<TField>(value: TField) { inner(value) }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue { return value }\n" +
                "    operator .proxy.set.*\\<TValue>(symbol: String, value: TValue) { inner(symbol=symbol, value=value) }\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Timed {\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn { return inner() }\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any { return inner(.name, args) }\n" +
                "}\n");
            CheckNoErrors("canonical proxy 全形态合法（§14.2/§14.3/§14.4 示例）", ok);
        }

        // ===== S11a 后半：.wrapper. 隐藏字段合成 + Entity 派发链与特化符号 =====
        // M88：烘焙合成已删——保留应用登记与形状匹配诊断冒烟
        private static void TestProxyDispatchChains()
        {
            TestHarness.Section("P2 Proxy Match (M88, no synthesis)");
            const string src =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged\\<TTarget> {\n" +
                "    operator .proxy.doSomething(arg: i32): String { return inner(arg) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service {\n" +
                "    pub func doSomething(arg: i32): String { return \"x\" }\n" +
                "}\n";
            var (unit, _) = ResolveUnit(src);
            CheckNoErrors("被修饰类无诊断", unit);
            var service = GlobalType(unit, "Service");
            TestHarness.CheckTrue("AppliedWrappers 登记", service.AppliedWrappers.Count == 1);
            TestHarness.CheckTrue("无隐藏字段合成", !service.Fields.Any(f => f.Name.StartsWith(".wrapper.")));
            TestHarness.CheckTrue("无特化/原始体合成",
                !service.Methods.Any(m => m.Name.StartsWith(".proxy.") || m.Name.StartsWith(".wrapped.")));
            // 名中形状不符诊断（措辞保留）
            var (bad, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper W\\<TTarget> {\n" +
                "    operator .proxy.foo(arg: String): i32 { return 0 }\n" +
                "}\n" +
                "@W\n" +
                "pub class H { pub func foo(arg: i32): String { return \"x\" } }\n");
            TestHarness.CheckTrue("specific 形状不符诊断",
                bad.Diagnostics.Diagnostics.Any(d => d.Message.Contains("does not match the shape")));

            // #27⑦：specific variadic 形状正例（双方同为位置包 → 无形状不符）
            var (okVar, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Wv\\<TTarget> {\n" +
                "    operator .proxy.sum(nums: i32...): i32 { return inner(nums) }\n" +
                "}\n" +
                "@Wv\n" +
                "pub class Hv {\n" +
                "    pub func sum(nums: i32...): i32 { return 0 }\n" +
                "}\n");
            CheckNoErrors("specific variadic 同形无诊断", okVar);
            TestHarness.CheckTrue("无形状不符（variadic 正例）",
                !okVar.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("does not match the shape")));

            // #27⑦：普通参数 vs 包参数误判同形负例
            var (badVar, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Wb\\<TTarget> {\n" +
                "    operator .proxy.sum(nums: i32): i32 { return inner(nums) }\n" +
                "}\n" +
                "@Wb\n" +
                "pub class Hb {\n" +
                "    pub func sum(nums: i32...): i32 { return 0 }\n" +
                "}\n");
            TestHarness.CheckTrue("ordinary vs variadic 形状不符",
                badVar.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("does not match the shape")));

            var (badPackKind, _) = ResolveUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Wk\\<TTarget> {\n" +
                "    operator .proxy.sum(nums: i32...): i32 { return inner(nums) }\n" +
                "}\n" +
                "@Wk\n" +
                "pub class Hk {\n" +
                "    pub func sum(nums: named i32...): i32 { return 0 }\n" +
                "}\n");
            TestHarness.CheckTrue("positional vs named variadic 形状不符",
                badPackKind.Diagnostics.Diagnostics.Any(d =>
                    d.Message.Contains("does not match the shape")));
        }

        // M88：降级链合成已删——PrintDowngradeRequest 与资格判定冒烟
        private static void TestDowngradeChains()
        {
            TestHarness.Section("P2 Downgrade Eligibility (M88, no synthesis)");
            const string src =
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Audited {\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Audited\n" +
                "pub class Service { pub func known(): i32 { return 1 } }\n";
            var (unit, _) = ResolveUnit(src);
            CheckNoErrors("有 .proxy.* 应用无诊断", unit);
            var service = GlobalType(unit, "Service");
            TestHarness.CheckTrue("降级资格", ProxyMatching.HasMethodWildcardProxy(service));
            TestHarness.CheckTrue("无 router 合成", !service.Methods.Any(m => m.Name == "call???"));
            TestHarness.Check("PrintDowngradeRequest",
                "Service$fetch(.i32)@.any",
                CanonicalSymbolPrinter.PrintDowngradeRequest(service, "fetch",
                    Array.Empty<SemanticSymbol>(),
                    new (string?, SemanticSymbol)[] { (null, unit.Symbols.Bootstrap.Int32) }));
            TestHarness.Check("PrintDowngradeRequest 显式泛型",
                "Service$fetch<.i32,.string>(.i32)@.any",
                CanonicalSymbolPrinter.PrintDowngradeRequest(service, "fetch",
                    new SemanticSymbol[] { unit.Symbols.Bootstrap.Int32, unit.Symbols.Bootstrap.String },
                    new (string?, SemanticSymbol)[] { (null, unit.Symbols.Bootstrap.Int32) }));
        }

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

            // 组合禁忌：async / 同容器同名重载（V2.5 放行 generic+native）
            var (u4, _) = ResolveUnit("@NativeLibrary(\"rt\")\nasync native func f(): i32\n");
            TestHarness.CheckSemanticError("native × async", u4.Diagnostics,
                "Native function 'f' cannot be 'async'");
            var (u5, _) = ResolveUnit(
                "@NativeLibrary(\"rigi_rt\")\n@NativeSymbol(\"alloc_array\")\n" +
                "priv native func alloc_array\\<T>(size: i32): Array\\<T>\n");
            CheckNoErrors("native × 泛型（V2.5 放行）", u5);
            var alloc = u5.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "alloc_array");
            TestHarness.CheckTrue("泛型 native 标记位",
                alloc.IsNative && alloc.GenericParameters.Count == 1);
            var (u6, _) = ResolveUnit(
                "@NativeLibrary(\"rt\")\nnative func dup(x: i32)\n" +
                "func dup(x: i32, y: i32) { }\n");
            TestHarness.CheckSemanticError("native × 同容器重载", u6.Diagnostics,
                "Native function 'dup' cannot be overloaded");

            // 参数/返回类型白名单（参数限基本类型；返回类型 S10 放宽——
            // 基本类型或用户引用类型 class/interface，值类型仍拒绝）
            var (u7a, _) = ResolveUnit(
                "class User { }\n" +
                "@NativeLibrary(\"rt\")\nnative func f(u: User)\n");
            TestHarness.CheckSemanticError("参数为用户类型", u7a.Diagnostics,
                "Parameter 'u' of native function 'f' must be a primitive type");
            var (u7b, _) = ResolveUnit("@NativeLibrary(\"rt\")\nnative func g(): Object\n");
            TestHarness.CheckTrue("返回 Object（class 引用类型，S10 放宽）无诊断",
                !u7b.Diagnostics.HasErrors);
            var (u7b2, _) = ResolveUnit(
                "interface IUser { }\n" +
                "@NativeLibrary(\"rt\")\nnative func g(): IUser\n");
            TestHarness.CheckTrue("返回接口（引用类型，S10 放宽）无诊断",
                !u7b2.Diagnostics.HasErrors);
            var (u7b3, _) = ResolveUnit(
                "struct User { }\n" +
                "@NativeLibrary(\"rt\")\nnative func g(): User\n");
            TestHarness.CheckSemanticError("返回用户 struct（值类型，仍拒绝）", u7b3.Diagnostics,
                "Return type of native function 'g' must be a primitive type or a " +
                "user-declared reference type (class/interface)");
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
                "@NativeLibrary(\"rigi_rt\")\n" +
                "@NativeSymbol(\"print\")\n" +
                "priv static native func print(text: String)\n" +
                "}\n");
            CheckNoErrors("stdlib 形态无诊断", ok1);
            var print = NsOf(ok1, "core", "io").Types.Single(t => t.Name == "Console")
                .Methods.Single(m => m.Name == "print");
            TestHarness.CheckTrue("IsNative/IsStatic 标记位", print.IsNative && print.IsStatic);
            TestHarness.Check("NativeSymbol 取注解实参", print.NativeSymbol ?? "", "print");
            TestHarness.Check("NativeLibrary 取注解实参", print.NativeLibrary ?? "", "rigi_rt");

            // 正例：@NativeSymbol 缺省取函数名
            var (ok2, _) = ResolveUnit(
                "class C {\n" +
                "@NativeLibrary(\"rigi_rt\")\n" +
                "priv static native func printErr(text: String)\n" +
                "}\n");
            CheckNoErrors("缺省 @NativeSymbol 无诊断", ok2);
            var printErr = GlobalType(ok2, "C").Methods.Single(m => m.Name == "printErr");
            TestHarness.CheckTrue("IsNative 标记位", printErr.IsNative);
            TestHarness.Check("NativeSymbol 缺省取函数名", printErr.NativeSymbol ?? "", "printErr");
            TestHarness.Check("NativeLibrary 取注解实参", printErr.NativeLibrary ?? "", "rigi_rt");

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

        // ===== enum case 结构级检查（S11，§12：洞独占性 + 判别值落定 + 防御复核）=====
        private static void TestEnumCaseStructure()
        {
            TestHarness.Section("P2 Enum Case Structure");

            // 正例：固定实参 / 位置洞 / 具名洞 / 无括号与空括号 case（Arguments 空列表不区分）
            var (unit, _) = ResolveUnit(
                "enum struct Result {\n" +
                "    pub const code: i32\n" +
                "    pub init(_ -> code)\n" +
                "}[\n" +
                "    Ok(0),\n" +
                "    Positional(_),\n" +
                "    Failed(code = _),\n" +
                "    Plain,\n" +
                "    Empty()\n" +
                "]\n");
            CheckNoErrors("固定/位置洞/具名洞/无参形态全无诊断", unit);
            var result = GlobalType(unit, "Result");
            TestHarness.CheckTrue("case 全建壳（5 个，声明序）",
                result.Cases.Count == 5 && result.Cases[1].Name == "Positional" && result.Cases[3].Name == "Plain");
            TestHarness.CheckTrue("auto case 判别值保持 null（§12.4 编号归发射侧按声明序推导）",
                result.Cases.All(c => c.Discriminant == null));
            TestHarness.CheckTrue("模板槽留空（init 绑定归 P3 声明点）",
                result.Cases.All(c => c.ResolvedInit == null && c.HoleParameters == null));

            // 显式判别值落定
            var (unit2, _) = ResolveUnit(
                "enum struct Level {}[\n" +
                "    Low -> 1,\n" +
                "    High -> 100\n" +
                "]\n");
            CheckNoErrors("显式判别值无诊断", unit2);
            var level = GlobalType(unit2, "Level");
            TestHarness.CheckTrue("显式判别值落定符号（-> N 原文值）",
                level.Cases[0].Discriminant == 1 && level.Cases[1].Discriminant == 100);

            // 反例：洞嵌套在表达式内（someExpression(_) 形态，§12.1）
            var (unit3, _) = ResolveUnit(
                "enum struct Bad {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    static func wrap(x: i32): i32 { }\n" +
                "}[\n" +
                "    Nested(wrap(_)),\n" +
                "    Sum((1 + _))\n" +
                "]\n");
            TestHarness.CheckTrue("洞嵌套两条诊断（调用实参内 / 二元表达式内）",
                unit3.Diagnostics.Diagnostics.Count(d => d.Message ==
                    "Enum case hole '_' must occupy an entire argument position") == 2);
            CheckP2Error("洞必须独占实参位置（确为 P2）", unit3,
                "Enum case hole '_' must occupy an entire argument position");

            // 正例：switch pattern 的 `_` 不是模板洞（独立占位语义，不误伤）
            var (unit4, _) = ResolveUnit(
                "enum struct WithSwitch {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}[\n" +
                "    ViaSwitch(switch (0) { (_) -> { 1 } default -> { 2 } })\n" +
                "]\n");
            CheckNoErrors("switch pattern 占位 `_` 不误伤", unit4);

            // 防御复核直达（case 名重复 / 判别值重复与负值 Parser FinishEnumCases
            // 必拦，经 AST 变异绕过 Parser 构造，S8e ResolveMutated 先例）：
            // P1 名复核 + P2 名复核 + P2 判别值唯一复核
            var (unit5, _) = ResolveMutated(
                "enum struct Dup {}[\n" +
                "    Alpha -> 1,\n" +
                "    Beta -> 2\n" +
                "]\n",
                root =>
                {
                    var cases = ((EnumStructDeclarationASTNode)root.Declarations[0]).Cases;
                    cases[1].CaseName = "Alpha";
                    cases[1].DiscriminantValue = 1;
                });
            TestHarness.CheckSemanticError("P1 case 名复核（重复符号不进 Cases 表）",
                unit5.Diagnostics, "Duplicate enum case declaration: 'Alpha'");
            CheckP2Error("P2 case 名复核", unit5, "Duplicate enum case name: 'Alpha'");
            CheckP2Error("P2 判别值唯一复核", unit5, "Duplicate enum discriminant value: 1");

            var (unit6, _) = ResolveMutated(
                "enum struct Neg {}[\n" +
                "    Only -> 0\n" +
                "]\n",
                root =>
                {
                    ((EnumStructDeclarationASTNode)root.Declarations[0]).Cases[0].DiscriminantValue = -1;
                });
            CheckP2Error("P2 判别值非负复核", unit6, "Enum discriminant value must be non-negative");

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
                "Accessor modifier 'static' is not allowed here (access modifiers, open or override only)");

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

            // getter/setter 是独立的多态单元：open 与 override 分别登记并匹配。
            var (unit7, _) = ResolveUnit(
                "open class Base {\n" +
                "    pub var value: i32 {\n" +
                "        open get(value: _) { return value }\n" +
                "        open set(value: _) { }\n" +
                "    }\n" +
                "}\n" +
                "class Child : Base {\n" +
                "    pub var value: i32 {\n" +
                "        override get(value: _) { return value }\n" +
                "        override set(value: _) { }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("getter/setter open 与 override 合法", unit7);
            var child = GlobalType(unit7, "Child").Fields.Single(f => f.Name == "value");
            TestHarness.CheckTrue("getter 标记 override", child.Getter!.IsOverride);
            TestHarness.CheckTrue("setter 标记 override", child.Setter!.IsOverride);

            var (unit8, _) = ResolveUnit(
                "open class Base {\n" +
                "    pub var value: i32 {\n" +
                "        open get(value: _) { return value }\n" +
                "    }\n" +
                "}\n" +
                "class Child : Base {\n" +
                "    pub var value: i32 {\n" +
                "        get(value: _) { return value }\n" +
                "    }\n" +
                "}\n");
            CheckP2Error("getter 隐藏必须显式 override", unit8,
                "'value' getter hides an inherited accessor; declare it 'override'");

            var (unit9, _) = ResolveUnit(
                "var globalValue: i32 {\n" +
                "    open get(value: _) { return value }\n" +
                "}\n" +
                "class C {\n" +
                "    static var staticValue: i32 {\n" +
                "        override get(value: _) { return value }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("全局/静态访问器不能多态", unit9.Diagnostics,
                "'open'/'override' cannot be applied to global or static accessors");
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

        // ===== S8f：castTo/castFrom 声明形状（SYNTAX §3.5）=====
        private static void TestConversionOperators()
        {
            TestHarness.Section("P2 Conversion Operators (castTo/castFrom)");

            // 合法形态（spec §3.5 示例：泛型形态 + 非泛型形态）
            var (ok, _) = ResolveUnit(
                "class S { operator castTo\\<TTarget>(): TTarget { } }\n" +
                "class C { operator castFrom\\<TSource>(obj: TSource): C { } }\n" +
                "class S2 { operator castTo(): i32 { } }\n" +
                "class C2 { operator castFrom(obj: S2): C2 { } }\n");
            CheckNoErrors("合法 castTo/castFrom 声明无诊断", ok);

            // castTo 带参数：形状违反（名字分析需要零参数）
            var (u1, _) = ResolveUnit("class S { operator castTo(x: i32): i32 { } }\n");
            CheckP2Error("castTo 带参数", u1, "Operator 'castTo' must have no parameters");

            // castTo void 返回（转换没有产物）
            var (u2, _) = ResolveUnit("class S { operator castTo() { } }\n");
            CheckP2Error("castTo void", u2, "Operator 'castTo' must declare a return type");

            // castFrom 参数个数：0 与 2 都违反
            var (u3, _) = ResolveUnit("class C { operator castFrom(): C { } }\n");
            CheckP2Error("castFrom 零参数", u3,
                "Operator 'castFrom' must have exactly one parameter");
            var (u4, _) = ResolveUnit("class C { operator castFrom(a: i32, b: i32): C { } }\n");
            CheckP2Error("castFrom 两参数", u4,
                "Operator 'castFrom' must have exactly one parameter");

            // castFrom void 返回
            var (u5, _) = ResolveUnit("class C { operator castFrom(obj: i32) { } }\n");
            CheckP2Error("castFrom void", u5,
                "Operator 'castFrom' must declare a return type");

            // 普通函数同名不是转换运算符（名字分析只搜 operator），不检查
            var (u6, _) = ResolveUnit(
                "func castTo(x: i32): i32 { }\nfunc castFrom(y: i32): i32 { }\n");
            CheckNoErrors("普通函数同名无诊断", u6);
        }

        // ===== S8f：async 声明侧闸门 2/3/5 + async 仅函数（SYNTAX §4.5）=====
        private static void TestAsyncDeclarationGates()
        {
            TestHarness.Section("P2 Async Declaration Gates (§4.5)");

            // 合法：void / 共享安全参数与返回值 / Nullable\<T\> 按 T 推导 /
            // 无约束泛型参数 / shared 约束边界
            var (ok, _) = ResolveUnit(
                "shared class SharedUser { }\n" +
                "async func f() { }\n" +
                "async func ok(id: i32, name: String): SharedUser { }\n" +
                "async func nullable(s: String?): String? { }\n" +
                "async func generic\\<T>(x: T): T { }\n" +
                "shared class Base { }\n" +
                "async func constrained\\<T extends Base>(x: T): T { }\n");
            CheckNoErrors("合法 async 声明无诊断", ok);

            // 闸门 2：参数是 local object
            var (u1, _) = ResolveUnit(
                "class LocalUser { }\n" +
                "async func bad(u: LocalUser) { }\n");
            CheckP2Error("闸门 2 参数", u1,
                "Parameter 'u' of async function 'bad' must be a shared-safe type: 'LocalUser'");

            // 闸门 3：返回类型是 local object
            var (u2, _) = ResolveUnit(
                "class LocalUser { }\n" +
                "async func bad(): LocalUser { }\n");
            CheckP2Error("闸门 3 返回值", u2,
                "Return type 'LocalUser' of async function 'bad' must be a shared-safe type");

            // 闸门 5：泛型参数的约束边界非共享安全（typeid 与实际值一同跨边界）
            var (u3, _) = ResolveUnit(
                "class LocalUser { }\n" +
                "async func bad\\<T extends LocalUser>(x: T) { }\n");
            CheckP2Error("闸门 5 约束边界", u3,
                "Generic parameter 'T' of async function 'bad' must have a shared-safe " +
                "constraint bound: 'LocalUser'");

            // async 仅函数（§9.2）：init/operator/类型声明
            var (u4, _) = ResolveUnit("class C { async init() { } }\n");
            CheckP2Error("async init", u4, "'async' can only be applied to functions");
            var (u5, _) = ResolveUnit("class C { async operator plus(o: i32): i32 { } }\n");
            CheckP2Error("async operator", u5, "'async' can only be applied to functions");
            var (u6, _) = ResolveUnit("async class C { }\n");
            CheckP2Error("async 类型声明", u6, "'async' can only be applied to functions");
        }

        // ===== 构造类型 BaseType 回填（驻留早于定义基类解析的陈旧快照 +
        // 引用定义泛型参数的未代入快照统一重算；CreatesCycle 定义级比较）=====
        private static void TestConstructedBaseTypeBackfill()
        {
            TestHarness.Section("P2 Constructed BaseType Backfill");

            // 字段/参数类型引用（TypeReferenceResolver）先于继承解析：Sub\<i32\>
            // 驻留时 Sub 的显式基类尚未解析——回填后应为代入产物 Base\<i32\>（而非
            // 默认 Object 陈旧快照或 Base\<T-sub\> 未代入快照）
            var (unit, _) = ResolveUnit(
                "pub open class Base\\<T> { pub var x: T\npub init(_ -> x) { } }\n" +
                "pub class Sub\\<T> : Base\\<T> { }\n" +
                "func f(s: Sub\\<i32>) { }\n");
            CheckNoErrors("无诊断（泛型基类回填）", unit);
            var subI32 = (TypeSymbol)unit.Symbols.GlobalNamespace.Methods
                .Single(m => m.Name == "f").Parameters[0].Type!;
            TestHarness.CheckTrue("构造类型 BaseType 代入为 Base\\<i32\\>",
                subI32.BaseType is TypeSymbol { ConstructedFrom: not null } baseI32
                && baseI32.ConstructedFrom.Name == "Base"
                && ReferenceEquals(baseI32.TypeArguments![0], unit.Symbols.Bootstrap.Int32));

            // 嵌套构造基类逐实参代入：Mid\<i32\>.BaseType = Wrapper\<Box\<i32\>\>
            var (unit2, _) = ResolveUnit(
                "pub open class Box\\<T> { }\n" +
                "pub open class Wrapper\\<T> { }\n" +
                "pub class Mid\\<T> : Wrapper\\<Box\\<T>> { }\n" +
                "func g(m: Mid\\<i32>) { }\n");
            CheckNoErrors("无诊断（嵌套构造基类回填）", unit2);
            var midI32 = (TypeSymbol)unit2.Symbols.GlobalNamespace.Methods
                .Single(m => m.Name == "g").Parameters[0].Type!;
            TestHarness.CheckTrue("嵌套构造基类代入为 Wrapper\\<Box\\<i32\\>\\>",
                midI32.BaseType is TypeSymbol { ConstructedFrom: not null } wrapper
                && wrapper.ConstructedFrom.Name == "Wrapper"
                && wrapper.TypeArguments![0] is TypeSymbol { ConstructedFrom: not null } boxI32
                && boxI32.ConstructedFrom.Name == "Box"
                && ReferenceEquals(boxI32.TypeArguments![0], unit2.Symbols.Bootstrap.Int32));

            // 泛型循环继承：链上是构造实例，按定义级比较（修复前 ReferenceEquals
            // 比构造实例被骗过）
            var (unit3, _) = ResolveUnit(
                "pub open class A\\<T> : B\\<T> { }\n" +
                "pub open class B\\<T> : A\\<T> { }\n");
            CheckP2Error("泛型循环继承", unit3, "Circular inheritance involving");

            // 抽象成员跨构造基类（签名含 T）：修复前未代入快照使待实现视图停在
            // 定义级 T，Sub 的 foo(x: i32) 实现匹配不上 → 误报未实现
            var (unit4, _) = ResolveUnit(
                "pub abstract class Grand\\<T> { pub abstract func foo(x: T): i32\n }\n" +
                "pub abstract class Base\\<T> : Grand\\<T> { }\n" +
                "pub class Sub : Base\\<i32> {\n" +
                "    pub override func foo(x: i32): i32 { return x }\n" +
                "}\n");
            CheckNoErrors("跨构造基类抽象成员实现识别（两跳代入）", unit4);

            // 未实现仍报
            var (unit5, _) = ResolveUnit(
                "pub abstract class Grand\\<T> { pub abstract func foo(x: T): i32\n }\n" +
                "pub abstract class Base\\<T> : Grand\\<T> { }\n" +
                "pub class Sub : Base\\<i32> { }\n");
            CheckP2Error("跨构造基类抽象未实现仍报", unit5,
                "does not implement abstract member 'foo'");
        }

        // ===== ext 成员注册重复/遮蔽检测（注册时按字段同名/方法同签名落诊断，
        // 不注册——防止 P3 查找双候选静默遮蔽）=====
        private static void TestExtDuplicateDetection()
        {
            TestHarness.Section("P2 Extension Duplicate Detection");

            var (u1, _) = ResolveUnit(
                "pub ext var String.tag: i32\n" +
                "pub ext var String.tag: i32\n");
            CheckP2Error("ext 字段同名重复", u1,
                "Extension field 'tag' duplicates an existing member of type 'String'");

            var (u2, _) = ResolveUnit(
                "pub class C { pub var n: i32 }\n" +
                "pub ext var C.n: i32\n");
            CheckP2Error("ext 字段遮蔽既有字段", u2,
                "Extension field 'n' duplicates an existing member of type 'C'");

            var (u3, _) = ResolveUnit(
                "pub class C { pub func m(x: i32): i32 { return x } }\n" +
                "pub ext func C.m(x: i32): i32 { return 0 }\n");
            CheckP2Error("ext 方法同签名遮蔽", u3,
                "Extension method 'm' duplicates an existing member of type 'C'");

            var (u4, _) = ResolveUnit(
                "pub ext func String.poke(): i32 { return 1 }\n" +
                "pub ext func String.poke(): i32 { return 2 }\n");
            CheckP2Error("两个同签名 ext 方法互撞", u4,
                "Extension method 'poke' duplicates an existing member of type 'String'");

            // 合法：不同签名（参数类型引用不等）重载 + 不同名成员
            var (ok, _) = ResolveUnit(
                "pub class C { pub func m(x: i32): i32 { return x } }\n" +
                "pub ext func C.m(x: String): i32 { return 0 }\n" +
                "pub ext func C.other(x: i32): i32 { return x }\n");
            CheckNoErrors("合法 ext 重载无诊断", ok);
        }

        // ===== override 泛型元数（元数不同即不同派发契约，不是合法覆写目标）=====
        private static void TestOverrideGenericArity()
        {
            TestHarness.Section("P2 Override Generic Arity");

            var (u1, _) = ResolveUnit(
                "pub open class B { pub open func pick\\<U>(x: U): i32 { return 0 } }\n" +
                "pub class S : B { pub override func pick\\<U, V>(x: U): i32 { return 0 } }\n");
            CheckP2Error("override 元数不同", u1, "'pick': no inherited member to override");

            // 合法：同元数泛型覆写（两侧泛型参数按声明序同构）
            var (ok, _) = ResolveUnit(
                "pub open class B { pub open func pick\\<U>(x: U): i32 { return 0 } }\n" +
                "pub class S : B { pub override func pick\\<W>(x: W): i32 { return 0 } }\n");
            CheckNoErrors("同元数泛型覆写无诊断", ok);
        }

        // ===== 命名空间不是类型（类型引用落袋统一拦截）+ §15.2 裸命名空间
        // import 拒绝 =====
        private static void TestNamespaceNotAType()
        {
            TestHarness.Section("P2 Namespace Is Not A Type");

            var (u1, _) = ResolveUnit(
                "namespace a.b\n" +
                "func f(): a.b { }\n");
            CheckP2Error("命名空间作返回类型", u1, "'a.b' is not a type");

            var (u2, _) = ResolveUnit(
                "namespace ns\n" +
                "func g(): ns { }\n");
            CheckP2Error("单段命名空间作返回类型", u2, "'ns' is not a type");

            // §15.2 三种形态之外：具名 import 的目标必须是类型
            var (u3, _) = ResolveUnit(
                "namespace a.b\n" +
                "pub class C { }\n",
                "import a.b\n" +
                "func h() { }\n");
            CheckP2Error("裸命名空间 import", u3,
                "Import target 'a.b' is not a type (§15.2)");

            // 正例：具名 import 类型 / 通配 import 命名空间仍合法
            var (ok, _) = ResolveUnit(
                "namespace a.b\n" +
                "pub class C { }\n",
                "import a.b.C\n" +
                "import a.b.*\n" +
                "class D { var c: C }\n");
            CheckNoErrors("具名/通配 import 合法", ok);
        }

        // ===== 裸名命中带泛型参数的定义：未构造的泛型定义不能作类型，
        // 按 ApplyTypeArguments 同口径报元数错误 =====
        private static void TestBareGenericDefinitionArity()
        {
            TestHarness.Section("P2 Bare Generic Definition Arity");

            var (u1, _) = ResolveUnit(
                "class Only\\<T> { }\n" +
                "var x: Only\n");
            CheckP2Error("裸名命中泛型定义（字段类型）", u1,
                "'Only' expects 1 type argument(s), got 0");

            var (u2, _) = ResolveUnit(
                "class Only\\<T> { }\n" +
                "func f(p: Only) { }\n");
            CheckP2Error("裸名命中泛型定义（参数类型）", u2,
                "'Only' expects 1 type argument(s), got 0");

            // 同名不同元数共存（S10）：裸名精确命中非泛型声明，不回退误报
            var (ok, _) = ResolveUnit(
                "class Task { }\n" +
                "class Task\\<TResult> { }\n" +
                "func f(t: Task) { }\n");
            CheckNoErrors("同名不同元数裸名合法", ok);
        }

        // ===== init 映射继承的内建字段（bootstrap Exception 程序化携带
        // protected message 字段，SYNTAX §8.1——子类 init 直接赋值继承字段）=====
        private static void TestInitMappingBuiltinField()
        {
            TestHarness.Section("P2 Init Mapping To Builtin Field");

            var (ok, _) = ResolveUnit(
                "pub class E : Exception {\n" +
                "    pub init(_ -> message) { }\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n");
            CheckNoErrors("init 映射继承的内建字段无诊断", ok);
            var init = GlobalType(ok, "E").Methods.Single(m => m.Kind == MethodKind.Init);
            TestHarness.CheckTrue("映射参数类型沿用 message 字段类型",
                ReferenceEquals(init.Parameters[0].Type, ok.Symbols.Bootstrap.String));
        }

        // ===== ext native 成员闸门（ext 视同类型成员必 static；重载按目标
        // 类型成员表 + 同目标 pending ext 比对）=====
        private static void TestExtNativeGates()
        {
            TestHarness.Section("P2 Extension Native Gates");

            var (u1, _) = ResolveUnit(
                "@NativeLibrary(\"x\")\n" +
                "pub ext native func String.foo(): i32\n");
            CheckP2Error("ext native 实例形态拒绝", u1,
                "Native member function 'foo' must be 'static'");

            // 与目标类型既有成员同名（签名不同——不触发 ext 重复注册检测，
            // 但 native 禁一切重载）
            var (u2, _) = ResolveUnit(
                "pub class C { pub func m(x: i32): i32 { return x } }\n" +
                "@NativeLibrary(\"x\")\n" +
                "pub ext static native func C.m(): i32\n");
            CheckP2Error("ext native 与目标同名成员重载", u2,
                "Native function 'm' cannot be overloaded");

            // 合法：ext static native 无同名成员
            var (ok, _) = ResolveUnit(
                "@NativeLibrary(\"x\")\n" +
                "pub ext static native func String.bar(): i32\n");
            CheckNoErrors("合法 ext static native 无诊断", ok);
        }

        // ===== 路径中间段同名实体：类型优先于子命名空间（与首段
        // FindInNamespace 一致）=====
        private static void TestNamespaceSegmentPriority()
        {
            TestHarness.Section("P2 Namespace Segment Priority");

            // q.X 中间段：q 内类型 X 与子命名空间 X 同名——类型优先
            var (ok, _) = ResolveUnit(
                "namespace q\n" +
                "pub class X { }\n",
                "namespace q.X\n" +
                "pub class Y { }\n",
                "namespace other\n" +
                "class Z { var x: q.X }\n");
            CheckNoErrors("中间段同名类型优先于子命名空间", ok);
            var field = NsOf(ok, "other").Types.Single(t => t.Name == "Z")
                .Fields.Single(f => f.Name == "x");
            TestHarness.CheckTrue("解析到类型而非子命名空间",
                field.FieldType is TypeSymbol { Name: "X", Kind: TypeKind.Class });
        }

        // ===== interface 字段禁止（SYNTAX §11 接口成员只有函数——字段不参与
        // 闭包检查、不产生实现要求、实现类不继承，纯死声明，声明侧拒绝）=====
        private static void TestInterfaceFieldDeclaration()
        {
            TestHarness.Section("P2 Interface Field Declaration");

            var (u1, _) = ResolveUnit("pub interface I { var x: i32\nfunc f() }\n");
            CheckP2Error("interface 实例字段拒绝", u1, "'x': interfaces cannot declare fields");

            var (u2, _) = ResolveUnit("pub interface I { static var c: i32 }\n");
            CheckP2Error("interface 静态字段同禁", u2, "'c': interfaces cannot declare fields");

            // const 同禁（字段即拒绝，不区分 const/var）
            var (u3, _) = ResolveUnit("pub interface I { const k: i32 = 1 }\n");
            CheckP2Error("interface const 字段同禁", u3, "'k': interfaces cannot declare fields");

            // 正例：纯函数接口（含默认实现）无诊断
            var (ok, _) = ResolveUnit("pub interface I { func f(): i32\nfunc g() { } }\n");
            CheckNoErrors("纯函数接口无诊断", ok);
        }

        // ===== 重复 implements 诊断（定义级判定：`I, I` 与 `I\<i32\>,
        // I\<String\>` 同定义即重复——接口契约按定义派发，构造实参不产生
        // 新的实现要求；重复者不进 Interfaces 表）=====
        private static void TestDuplicateInterfaceImplementation()
        {
            TestHarness.Section("P2 Duplicate Interface Implementation");

            // 同名重复
            var (u1, _) = ResolveUnit(
                "pub interface I { }\n" +
                "pub class C implements I, I { }\n");
            CheckP2Error("同名接口重复 implements", u1, "'C': duplicate interface 'I'");
            TestHarness.CheckTrue("重复者不进 Interfaces 表",
                GlobalType(u1, "C").Interfaces.Count == 1);

            // 同定义不同构造（ConstructedFrom 归一后同一定义）
            var (u2, _) = ResolveUnit(
                "pub interface I\\<T> { }\n" +
                "pub class C implements I\\<i32>, I\\<String> { }\n");
            CheckP2Error("不同构造同定义重复", u2, "'C': duplicate interface 'I'");
            TestHarness.CheckTrue("不同构造重复者同样不进表",
                GlobalType(u2, "C").Interfaces.Count == 1);

            // interface 继承侧同规则
            var (u3, _) = ResolveUnit(
                "pub interface I { }\n" +
                "pub interface J : I, I { }\n");
            CheckP2Error("interface 重复继承接口", u3, "'J': duplicate interface 'I'");

            // 正例：不同定义构造共存无诊断
            var (ok, _) = ResolveUnit(
                "pub interface I\\<T> { }\n" +
                "pub interface J\\<T> { }\n" +
                "pub class C implements I\\<i32>, J\\<i32> { }\n");
            CheckNoErrors("不同定义接口共存无诊断", ok);
            TestHarness.CheckTrue("两接口都进表", GlobalType(ok, "C").Interfaces.Count == 2);
        }

        // ===== static operator 禁止（静态无多态：使用侧 FindInstanceOperators/
        // FindConversionOperator 只查实例方法，static operator 纯死声明）=====
        private static void TestStaticOperatorDeclaration()
        {
            TestHarness.Section("P2 Static Operator Declaration");

            var (u1, _) = ResolveUnit(
                "class C { static operator plus(other: C): C { } }\n");
            CheckP2Error("static 算术 operator 拒绝", u1, "'plus': operators cannot be 'static'");

            // castTo/castFrom 同禁（形状合法的 static 转换运算符仍是死声明）
            var (u2, _) = ResolveUnit(
                "class S { static operator castTo\\<TTarget>(): TTarget { } }\n");
            CheckP2Error("static castTo 拒绝", u2, "'castTo': operators cannot be 'static'");
            var (u3, _) = ResolveUnit(
                "class S { }\n" +
                "class C { static operator castFrom(obj: S): C { } }\n");
            CheckP2Error("static castFrom 拒绝", u3, "'castFrom': operators cannot be 'static'");

            // 正例：实例 operator（含形状合法的转换运算符）无诊断
            var (ok, _) = ResolveUnit(
                "class Vec { pub operator plus(other: Vec): Vec { return this } }\n" +
                "class S2 { operator castTo(): i32 { return 0 } }\n");
            CheckNoErrors("实例 operator 无诊断", ok);
        }

        // ===== 具名 import 同名多条目：失效条目跳过继续查找（不再「先者胜」
        // 毒化有效者）；两条都有效时报歧义；同一路径重复 import 豁免 =====
        private static void TestNamedImportResolution()
        {
            TestHarness.Section("P2 Named Import Resolution");

            // 失效 + 有效：跳过失效者解析到有效者（失效 import 自身由
            // ImportValidator 统一诊断一次，使用点不再报「未解析」）
            var (u1, _) = ResolveUnit(
                "namespace a\npub class Other { }\n",
                "namespace b\npub class Foo { }\n",
                "import a.Foo\n" +
                "import b.Foo\n" +
                "class C { var f: Foo }\n");
            TestHarness.CheckSemanticError("失效 import 统一诊断", u1.Diagnostics,
                "Unresolved import: 'a.Foo'");
            TestHarness.CheckTrue("有效者解析成功（引用相等 b.Foo）",
                ReferenceEquals(
                    GlobalType(u1, "C").Fields.Single(f => f.Name == "f").FieldType,
                    NsOf(u1, "b").Types.Single(t => t.Name == "Foo")));
            TestHarness.CheckTrue("使用点无「未解析」次生诊断",
                u1.Diagnostics.Diagnostics.Count(d =>
                    d.Message.Contains("Unresolved type or namespace")) == 0,
                string.Join("; ", u1.Diagnostics.Diagnostics.Select(d => d.Message)));

            // 双有效：歧义诊断（且只报一次，不再叠加其他诊断）
            var (u2, _) = ResolveUnit(
                "namespace a\npub class Foo { }\n",
                "namespace b\npub class Foo { }\n",
                "import a.Foo\n" +
                "import b.Foo\n" +
                "class C { var f: Foo }\n");
            CheckP2Error("双有效具名 import 歧义", u2, "Ambiguous import: 'Foo'");
            TestHarness.CheckTrue("歧义使用点毒化不叠加「未解析」",
                u2.Diagnostics.Diagnostics.Count(d =>
                    d.Message.Contains("Unresolved type or namespace")) == 0);

            // 同一路径重复 import：解析结果引用相等，豁免不误报
            var (ok, _) = ResolveUnit(
                "namespace a\npub class Foo { }\n",
                "import a.Foo\n" +
                "import a.Foo\n" +
                "class C { var f: Foo }\n");
            CheckNoErrors("同路径重复 import 豁免无诊断", ok);
            TestHarness.CheckTrue("同路径解析到 a.Foo",
                ReferenceEquals(
                    GlobalType(ok, "C").Fields.Single(f => f.Name == "f").FieldType,
                    NsOf(ok, "a").Types.Single(t => t.Name == "Foo")));
        }
    }
}
