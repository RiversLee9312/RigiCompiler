namespace LatteCompiler.Tests
{
    /// <summary>
    /// S1 canonical symbol 打印测试（M37）：CanonicalSymbolPrinter 输出
    /// 逐条对照 BIL_STANDARD §5.2/§8.1/§20 的规范示例。
    /// </summary>
    public static class CanonicalSymbolPrinterTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("CanonicalSymbolPrinter");

            var graph = new SymbolGraph();
            var b = graph.Bootstrap;
            var com = new NamespaceSymbol("com");
            var comExample = new NamespaceSymbol("example", com);

            // 测试脚手架：com.example 域的三个类型（§8.1 示例的当事人）
            var service = new TypeSymbol("Service", TypeKind.Class, comExample, baseType: b.Object);
            var user = new TypeSymbol("User", TypeKind.Class, comExample, baseType: b.Object);
            var number = new TypeSymbol("Number", TypeKind.Class, comExample, baseType: b.Object);

            // ===== 类型（§5.2：命名空间::类名[.内部类名...]）=====
            TestHarness.Check("类型 canonical",
                CanonicalSymbolPrinter.PrintType(service), "com.example::Service");

            var inner = new TypeSymbol("Inner", TypeKind.Class, declaringType: service, baseType: b.Object);
            TestHarness.Check("嵌套类型 canonical",
                CanonicalSymbolPrinter.PrintType(inner), "com.example::Service.Inner");

            // ===== 字段（§8.1 示例）=====
            var nameField = new FieldSymbol("name", owner: service, fieldType: b.String);
            TestHarness.Check("实例字段（§8.1）",
                CanonicalSymbolPrinter.PrintField(nameField), "com.example::Service#name@.string");

            var countField = new FieldSymbol("instanceCount", owner: service, isStatic: true, fieldType: b.Int64);
            TestHarness.Check("静态字段（§8.3）",
                CanonicalSymbolPrinter.PrintField(countField), "com.example::Service#.static.instanceCount@.i64");

            // ===== 方法（§8.1 示例）=====
            var load = new MethodSymbol("load", MethodKind.Regular, owner: service, returnType: user);
            load.Parameters.Add(new ParameterSymbol("id", b.Int64));
            TestHarness.Check("实例方法（§8.1）",
                CanonicalSymbolPrinter.PrintMethod(load), "com.example::Service$load(id:.i64)@com.example::User");

            // ===== 运算符（§8.1 示例）=====
            var plus = new MethodSymbol("plus", MethodKind.Operator, owner: number, returnType: number);
            plus.Parameters.Add(new ParameterSymbol("another", number));
            TestHarness.Check("运算符（§8.1）",
                CanonicalSymbolPrinter.PrintMethod(plus),
                "com.example::Number$$plus(another:com.example::Number)@com.example::Number");

            // ===== getter / setter（§8.1 示例 + §5.2 对偶形态）=====
            var getName = new MethodSymbol("name", MethodKind.Getter, owner: service, returnType: b.String);
            TestHarness.Check("getter（§8.1）",
                CanonicalSymbolPrinter.PrintMethod(getName), "com.example::Service$.get.name@.string");

            var setName = new MethodSymbol("name", MethodKind.Setter, owner: service);
            setName.Parameters.Add(new ParameterSymbol("value", b.String));
            TestHarness.Check("setter（§5.2）",
                CanonicalSymbolPrinter.PrintMethod(setName), "com.example::Service$.set.name@.string");

            // ===== §20 黄金示例两个符号 =====
            // Array 定义模拟 core.latte 未来载入的形态（标准构造 .array）
            var arrayDef = new TypeSymbol("Array", TypeKind.Class, b.Core,
                baseType: b.Object, bilStandardConstructor: ".array");
            arrayDef.GenericParameters.Add(new GenericParameterSymbol("T"));
            var arrayOfString = graph.GetConstructedType(arrayDef, b.String);

            var app = new TypeSymbol("App", TypeKind.Class, comExample, baseType: b.Object);
            var main = new MethodSymbol("main", MethodKind.Regular, owner: app,
                isStatic: true, returnType: b.Int32);
            main.Parameters.Add(new ParameterSymbol("args", arrayOfString));
            TestHarness.Check("§20 entrypoint main",
                CanonicalSymbolPrinter.PrintMethod(main),
                "com.example::App$.static.main(args:.array<.string>)@.i32");

            var console = new TypeSymbol("Console", TypeKind.Class, b.Core, baseType: b.Object);
            var println = new MethodSymbol("println", MethodKind.Regular, owner: console, isStatic: true);
            println.Parameters.Add(new ParameterSymbol("value", b.String));
            TestHarness.Check("§20 println（void 返回 → .void）",
                CanonicalSymbolPrinter.PrintMethod(println),
                "core::Console$.static.println(value:.string)@.void");

            // ===== wrapper 隐藏字段（§5.3 示例）=====
            var coreLogging = new NamespaceSymbol("logging", b.Core);
            var logged = new TypeSymbol("Logged", TypeKind.Wrapper, coreLogging, baseType: b.Wrapper, isRich: true);
            var wrapperField = new FieldSymbol(".wrapper.core.logging::Logged", owner: service, fieldType: logged);
            TestHarness.Check("wrapper 隐藏字段（§5.3）",
                CanonicalSymbolPrinter.PrintField(wrapperField),
                "com.example::Service#.wrapper.core.logging::Logged@core.logging::Logged");

            // ===== 构造类型投影 =====
            TestHarness.Check("Nullable\\<i32\\> → .nullable",
                CanonicalSymbolPrinter.PrintType(graph.GetNullable(b.Int32)), ".nullable<.i32>");
            TestHarness.Check("构造 Box\\<i32\\> → 闭合泛型",
                CanonicalSymbolPrinter.PrintType(graph.GetConstructedType(b.BoxDefinition, b.Int32)),
                "core::Box<.i32>");
            TestHarness.Check("嵌套构造实参",
                CanonicalSymbolPrinter.PrintType(
                    graph.GetConstructedType(arrayDef, graph.GetNullable(b.Int32))),
                ".array<.nullable<.i32>>");
            TestHarness.Check("null 返回 → .void",
                CanonicalSymbolPrinter.PrintTypeReference(null), ".void");

            // ===== 基元别名（§6.2）=====
            TestHarness.Check("float → .f32", CanonicalSymbolPrinter.PrintType(b.Float), ".f32");
            TestHarness.Check("double → .f64", CanonicalSymbolPrinter.PrintType(b.Double), ".f64");
            TestHarness.Check("Any → .any", CanonicalSymbolPrinter.PrintType(b.Any), ".any");
            TestHarness.Check("ValueType → .valuetype",
                CanonicalSymbolPrinter.PrintType(b.ValueType), ".valuetype");

            // ===== 全局符号（无宿主）=====
            var globalFunc = new MethodSymbol("helper", MethodKind.Regular, ns: comExample, returnType: b.Int32);
            TestHarness.Check("全局函数",
                CanonicalSymbolPrinter.PrintMethod(globalFunc), "com.example::$helper()@.i32");
            var globalVar = new FieldSymbol("counter", ns: comExample, fieldType: b.Int32);
            TestHarness.Check("全局变量",
                CanonicalSymbolPrinter.PrintField(globalVar), "com.example::#counter@.i32");

            // ===== 泛型参数作实参（§7.5）=====
            var listDef = new TypeSymbol("List", TypeKind.Class, comExample, baseType: b.Object);
            var listT = new GenericParameterSymbol("T");
            listDef.GenericParameters.Add(listT);
            TestHarness.Check("泛型参数实参 → .generic 形态",
                CanonicalSymbolPrinter.PrintType(graph.GetConstructedType(listDef, listT)),
                "com.example::List<.generic<$.generic.T>>");

            return TestHarness.Summary("CanonicalSymbolPrinter");
        }
    }
}
