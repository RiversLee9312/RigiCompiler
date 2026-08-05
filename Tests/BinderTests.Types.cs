using System.Linq;

namespace LatteCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== cast（S7e，SYNTAX §3.5）=====
        private static void TestCast()
        {
            TestHarness.Section("P3 Cast");

            // as：结果类型即目标类型
            var (unit, bodies) = BindUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n");
            CheckNoErrors("as 无诊断", unit);
            TestHarness.Check("as 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Cast(Param(s,String), String))])");
            var cast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("as 结果类型即目标类型",
                cast.Type.Name == "String" && !cast.IsSafe);

            // 向下转换不做静态拒绝（as 失败是运行时 core.CastException）
            var (unit2, bodies2) = BindUnit(
                "open class Animal {\n" +
                "}\n" +
                "class Dog : Animal {\n" +
                "}\n" +
                "func d(a: Animal): Dog {\n" +
                "    return a as Dog\n" +
                "}\n");
            CheckNoErrors("向下转换无诊断", unit2);
            TestHarness.Check("向下转换形态", BoundDescribe.Body(BodyOf(bodies2, "d")),
                "Body(d, [], [Return(Cast(Param(a,Animal), Dog))])");

            // as?：结果类型 = Nullable<目标类型>
            var (unit3, bodies3) = BindUnit(
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("as? 无诊断", unit3);
            TestHarness.Check("as? 绑定形态", BoundDescribe.Body(BodyOf(bodies3, "g")),
                "Body(g, [], [Return(SafeCast(Param(s,String), String))])");
            var safeCast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies3, "g").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("as? 结果类型为 Nullable<String>（目标类型不包装）",
                safeCast.IsSafe && safeCast.Type.Name == "Nullable"
                && safeCast.TargetType.Name == "String");

            // 诊断：目标类型解析失败
            var (unit4, _) = BindUnit(
                "func h(s: String): i32 {\n" +
                "    return s as Nosuch\n" +
                "}\n");
            TestHarness.CheckSemanticError("cast 目标类型未定义", unit4.Diagnostics,
                "Unresolved type or namespace: 'Nosuch'");
        }

        // ===== 字符串插值（S7f，SYNTAX §3.8）=====
        private static void TestStringInterpolation()
        {
            TestHarness.Section("P3 String Interpolation");

            // 段序列绑定为左结合 + 链（String.Add intrinsic）
            var (unit, bodies) = BindUnit(
                "func f() {\n" +
                "    var x = \"a\"\n" +
                "    var y = \"b${x}c\"\n" +
                "}\n");
            CheckNoErrors("无诊断（插值拼接）", unit);
            TestHarness.Check("插值绑定为左结合 + 链", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: String, y: String], [Decl(x, String, = Str(\"a\",String)); " +
                "Decl(y, String, = Binary(Add, Binary(Add, Str(\"b\",String), Local(x,String), String), " +
                "Str(\"c\",String), String))])");

            // 非 String 段 → toString() 实例调用（Any 承诺）；单段无拼接
            var (unit2, bodies2) = BindUnit(
                "func f() {\n" +
                "    var n = 42\n" +
                "    var s = \"n=${n}\"\n" +
                "}\n");
            CheckNoErrors("无诊断（非 String 段）", unit2);
            TestHarness.Check("非 String 段包 toString 调用", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [n: i32, s: String], [Decl(n, i32, = Int(42,i32)); " +
                "Decl(s, String, = Binary(Add, Str(\"n=\",String), " +
                "InstCall(toString, Local(n,i32), [], String), String))])");

            // String 段直拼（不再包 toString）
            var (unit3, bodies3) = BindUnit(
                "func f(x: String): String { return \"${x}!\" }\n");
            CheckNoErrors("无诊断（String 段直拼）", unit3);
            TestHarness.Check("String 段直拼无 toString", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(Binary(Add, Param(x,String), Str(\"!\",String), String))])");

            // String 的 + 运算符随 S7f 开放（内建拼接，BIL §11.2）
            var (unit4, bodies4) = BindUnit(
                "func f(): String { return \"a\" + \"b\" }\n");
            CheckNoErrors("无诊断（String + 开放）", unit4);
            TestHarness.Check("String + 绑定形态", BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [], [Return(Binary(Add, Str(\"a\",String), Str(\"b\",String), String))])");

            // 显式 toString 调用（全类型承诺，含值类型）
            var (unit5, bodies5) = BindUnit(
                "func f() {\n" +
                "    var n = 1\n" +
                "    var s = n.toString()\n" +
                "}\n");
            CheckNoErrors("无诊断（显式 toString）", unit5);
            TestHarness.Check("值类型显式 toString 调用", BoundDescribe.Body(BodyOf(bodies5, "f")),
                "Body(f, [n: i32, s: String], [Decl(n, i32, = Int(1,i32)); " +
                "Decl(s, String, = InstCall(toString, Local(n,i32), [], String))])");

            // void 段：插值段必须产值
            var (unit6, _) = BindUnit(
                "func g() { }\n" +
                "func f(): String { return \"${g()}\" }\n");
            TestHarness.CheckSemanticError("void 插值段诊断", unit6.Diagnostics,
                "has no result (void) and cannot be used as a value");
        }

        // ===== 安全访问 `?.`（S7f，SYNTAX §3.4）=====
        private static void TestSafeAccess()
        {
            TestHarness.Section("P3 Safe Access (?.)");

            // 字段安全访问：结果包装 Nullable（成员 String → String?）
            var (unit, bodies) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("无诊断（字段安全访问）", unit);
            TestHarness.Check("字段安全访问绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(SafeAccess(Param(u,User?), " +
                "InstField(name, SafeReceiver(User), String), String?))])");

            // 方法安全访问 + 已可空成员不二次包装
            var (unit2, bodies2) = BindUnit(
                "class Box { pub var content: String?\n    pub init(_ -> content) { } }\n" +
                "func f(b: Box?): String? { return b?.content }\n" +
                "func g(b: Box?): i32 { return 1 }\n");
            CheckNoErrors("无诊断（可空成员安全访问）", unit2);
            TestHarness.Check("已可空成员结果不二次包装", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Return(SafeAccess(Param(b,Box?), " +
                "InstField(content, SafeReceiver(Box), String?), String?))])");

            // 链式：a?.b?.c（a: A?，A.b: B，B.c: String）
            var (unit3, bodies3) = BindUnit(
                "class B { pub var c: String\n    pub init(_ -> c) { } }\n" +
                "class A { pub var b: B\n    pub init(_ -> b) { } }\n" +
                "func f(a: A?): String? { return a?.b?.c }\n");
            CheckNoErrors("无诊断（链式安全访问）", unit3);
            TestHarness.Check("链式安全访问绑定形态", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(SafeAccess(SafeAccess(Param(a,A?), " +
                "InstField(b, SafeReceiver(A), B), B?), " +
                "InstField(c, SafeReceiver(B), String), String?))])");

            // 诊断：非空 receiver
            var (unit4, _) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User) { var x = u?.name }\n");
            TestHarness.CheckSemanticError("非空 receiver 拒绝", unit4.Diagnostics,
                "Safe access '?.' requires a nullable receiver");

            // 诊断：可空结果上的普通段（须逐段标注 ?.）
            var (unit5, _) = BindUnit(
                "class B { pub var c: String\n    pub init(_ -> c) { } }\n" +
                "class A { pub var b: B?\n    pub init(_ -> b) { } }\n" +
                "func f(a: A?) { var x = a?.b.c }\n");
            TestHarness.CheckSemanticError("可空结果普通段拒绝", unit5.Diagnostics,
                "cannot be accessed on nullable type");
        }

        // ===== if? 空值回退（S7f，SYNTAX §3.4）=====
        private static void TestNullFallback()
        {
            TestHarness.Section("P3 Null Fallback (if?)");

            var (unit, bodies) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("无诊断（if? 回退）", unit);
            TestHarness.Check("if? 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(NullFallback(Param(u,User?), " +
                "New(User, init, [Str(\"anon\",String)]), User))])");

            // 组合：`?.` 与 if?（name?.x if? fallback 形态）
            var (unit2, bodies2) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): String { return u?.name if? \"anon\" }\n");
            CheckNoErrors("无诊断（?. + if? 组合）", unit2);
            TestHarness.Check("组合绑定形态", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Return(NullFallback(SafeAccess(Param(u,User?), " +
                "InstField(name, SafeReceiver(User), String), String?), " +
                "Str(\"anon\",String), String))])");

            // 诊断：左操作数非可空
            var (unit3, _) = BindUnit(
                "func f(s: String): String { return s if? \"x\" }\n");
            TestHarness.CheckSemanticError("左操作数非可空拒绝", unit3.Diagnostics,
                "Operator 'if?' requires a nullable left operand");

            // 诊断：回退值类型不兼容
            var (unit4, _) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): User { return u if? 42 }\n");
            TestHarness.CheckSemanticError("回退值类型不兼容", unit4.Diagnostics,
                "Null fallback must be assignable to 'User'");
        }

        // ===== 解构声明（S7f，SYNTAX §18）=====
        private static void TestDestructuring()
        {
            TestHarness.Section("P3 Destructuring");

            // 闭环：Pair 子类构造 + 解构（基类泛型字段初始化走替换）
            var (unit, bodies) = BindUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n" +
                "        key = k\n" +
                "        value = v\n" +
                "    }\n" +
                "}\n" +
                "func f() {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "}\n");
            CheckNoErrors("无诊断（解构闭环）", unit);
            TestHarness.Check("解构绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [k: String, v: i32], [Destructuring([k: String ← key; v: i32 ← value], " +
                "New(Entry, init, [Str(\"a\",String), Int(1,i32)]))])");
            // 基类泛型字段（TKey/TValue）在子类 init 里按构造实参替换
            var entryInit = bodies.Single(b => b.Method.Name == "init"
                && b.Method.Owner?.Name == "Entry");
            TestHarness.Check("init 内基类字段替换赋值", BoundDescribe.Body(entryInit),
                "Body(init, [], [Assign(InstField(key, This(Entry), String), Param(k,String)); " +
                "Assign(InstField(value, This(Entry), i32), Param(v,i32))])");

            // 诊断：初始化器非 Pair 子类
            var (unit2, _) = BindUnitWithStdlib("func f() {\n    var (a, b) = 42\n}\n");
            TestHarness.CheckSemanticError("非 Pair 子类拒绝", unit2.Diagnostics,
                "Destructuring requires a subtype of core.Pair");

            // 诊断：名字数不等于 2
            var (unit3, _) = BindUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "func f() {\n    var (a, b, c) = new Entry(\"a\", 1)\n}\n");
            TestHarness.CheckSemanticError("名字数拒绝", unit3.Diagnostics,
                "requires exactly 2 names");

            // 诊断：名字重复
            var (unit4, _) = BindUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "func f() {\n    var (k, k) = new Entry(\"a\", 1)\n}\n");
            TestHarness.CheckSemanticError("名字重复拒绝", unit4.Diagnostics,
                "Duplicate local variable 'k'");
        }

        // ===== 类型谓词 is/supers/with（S8a，SYNTAX §3.5/§3.7）=====
        private static void TestTypeCheck()
        {
            TestHarness.Section("P3 Type Check (is/supers/with)");

            // is 静态形态：TargetType 即声明符号（引用相等），TargetValue null，Type 恒 bool
            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool { return d is Animal }\n");
            CheckNoErrors("无诊断（is 静态）", unit);
            TestHarness.Check("is 静态绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Is(Param(d,Dog), Animal))])");
            var animalType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Animal");
            var isExpr = (BoundTypeCheckExpression)((BoundReturnStatement)
                BodyOf(bodies, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("is 静态结构事实",
                isExpr.Kind == BoundTypeCheckKind.Is
                && ReferenceEquals(isExpr.TargetType, animalType)
                && isExpr.TargetValue == null
                && ReferenceEquals(isExpr.Type, unit.Symbols.Bootstrap.Bool));

            // supers 静态形态（同形态，Kind 不同）
            var (unit2, bodies2) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func g(d: Dog): bool { return d supers Animal }\n");
            CheckNoErrors("无诊断（supers 静态）", unit2);
            TestHarness.Check("supers 静态绑定形态", BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [Return(Supers(Param(d,Dog), Animal))])");
            var supersExpr = (BoundTypeCheckExpression)((BoundReturnStatement)
                BodyOf(bodies2, "g").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("supers 静态结构事实",
                supersExpr.Kind == BoundTypeCheckKind.Supers
                && ReferenceEquals(supersExpr.TargetType,
                    unit2.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Animal"))
                && supersExpr.TargetValue == null
                && ReferenceEquals(supersExpr.Type, unit2.Symbols.Bootstrap.Bool));

            // with 静态形态：目标为 wrapper 声明
            var (unit3, bodies3) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper Serializable { }\n" +
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func h(d: Dog): bool { return d with Serializable }\n");
            CheckNoErrors("无诊断（with 静态）", unit3);
            TestHarness.Check("with 静态绑定形态", BoundDescribe.Body(BodyOf(bodies3, "h")),
                "Body(h, [], [Return(With(Param(d,Dog), Serializable))])");
            var withExpr = (BoundTypeCheckExpression)((BoundReturnStatement)
                BodyOf(bodies3, "h").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("with 静态结构事实",
                withExpr.Kind == BoundTypeCheckKind.With
                && ReferenceEquals(withExpr.TargetType,
                    unit3.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Serializable"))
                && withExpr.TargetValue == null);

            // is 动态形态 + typeOf 值形态（Type\<T\> 值作右侧）
            var (unit4, bodies4) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func k(d: Dog): bool {\n" +
                "    var t = typeOf(d)\n" +
                "    return d is t\n" +
                "}\n");
            CheckNoErrors("无诊断（is 动态）", unit4);
            TestHarness.Check("is 动态 + typeOf 值形态绑定形态",
                BoundDescribe.Body(BodyOf(bodies4, "k")),
                "Body(k, [t: Type<Dog>], " +
                "[Decl(t, Type<Dog>, = TypeOf(Param(d,Dog), Type<Dog>)); " +
                "Return(Is(Param(d,Dog), dyn Local(t,Type<Dog>)))])");
            var dogType = unit4.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Dog");
            var typeOfExpr = (BoundTypeOfExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies4, "k").Body.Statements[0]).Initializer!;
            // typeOf 值形态：Operand 非 null / TargetType null，Type 为 Type\<Dog\> 构造
            TestHarness.CheckTrue("typeOf 值形态结构事实",
                typeOfExpr.Operand != null && typeOfExpr.TargetType == null
                && typeOfExpr.Type is TypeSymbol typeOfType
                && ReferenceEquals(typeOfType.ConstructedFrom,
                    unit4.Symbols.Bootstrap.TypeDefinition)
                && ReferenceEquals(typeOfType.TypeArguments![0], dogType));
            var dynIsExpr = (BoundTypeCheckExpression)((BoundReturnStatement)
                BodyOf(bodies4, "k").Body.Statements[1]).Value!;
            // is 动态形态：TargetValue 非 null 且其 Type 与 typeOf 结果同一构造实例
            TestHarness.CheckTrue("is 动态结构事实",
                dynIsExpr.Kind == BoundTypeCheckKind.Is
                && dynIsExpr.TargetType == null && dynIsExpr.TargetValue != null
                && ReferenceEquals(dynIsExpr.TargetValue!.Type, typeOfExpr.Type));

            // 诊断：with 右侧非 wrapper 类型
            var (unit5, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool { return d with Animal }\n");
            TestHarness.CheckSemanticError("with 非 wrapper 目标拒绝", unit5.Diagnostics,
                "'with' target must be a wrapper type: 'Animal'");

            // 诊断：动态右侧值非 Type\<T\>（i32 局部）
            var (unit6, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool {\n" +
                "    var x = 1\n" +
                "    return d is x\n" +
                "}\n");
            TestHarness.CheckSemanticError("右侧值非 Type\\<T\\> 拒绝", unit6.Diagnostics,
                "right side of 'is' must be a type");

            // 诊断：右侧类型/值两不沾
            var (unit7, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool { return d is noSuchThing }\n");
            TestHarness.CheckSemanticError("右侧两不沾拒绝", unit7.Diagnostics,
                "right side of 'is' must be a type");

            // 诊断：is .Case（前导点 enum case 匹配）归 S11
            var (unit8, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool { return d is .Failed }\n");
            TestHarness.CheckSemanticError("enum case is 归口 S11", unit8.Diagnostics,
                "enum case is pattern is not supported yet (S11)");
        }

        // ===== typeOf（S8a，SYNTAX §3.7；BIL §12.5）=====
        private static void TestTypeOf()
        {
            TestHarness.Section("P3 typeOf");

            // 类型形态：单段裸名未命中值，命中类型
            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func m(): Type\\<Animal> { return typeOf(Animal) }\n");
            CheckNoErrors("无诊断（typeOf 类型形态）", unit);
            TestHarness.Check("typeOf 类型形态绑定形态",
                BoundDescribe.Body(BodyOf(bodies, "m")),
                "Body(m, [], [Return(TypeOf(type Animal, Type<Animal>))])");
            var animalType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Animal");
            var typeOfTypeForm = (BoundTypeOfExpression)((BoundReturnStatement)
                BodyOf(bodies, "m").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("typeOf 类型形态结构事实",
                typeOfTypeForm.TargetType != null
                && ReferenceEquals(typeOfTypeForm.TargetType, animalType)
                && typeOfTypeForm.Operand == null
                && typeOfTypeForm.Type is TypeSymbol typeOfFormType
                && ReferenceEquals(typeOfFormType.ConstructedFrom,
                    unit.Symbols.Bootstrap.TypeDefinition)
                && ReferenceEquals(typeOfFormType.TypeArguments![0], animalType));

            // 诊断：值/类型都未命中 → BindPath 的未定义名诊断
            var (unit2, _) = BindUnit(
                "func m() { var t = typeOf(noSuchThing) }\n");
            TestHarness.CheckSemanticError("typeOf 两不沾拒绝", unit2.Diagnostics,
                "Undefined name: 'noSuchThing'");
        }

        // ===== S9a 泛型函数体放行（Semantic/Binding/ 16 处 gate 解开，
        // 类型参数按引用相等身份使用；泛型调用/泛型 new 仍归口 S9b/S9c）=====
        private static void TestGenericFunctionBody()
        {
            TestHarness.Section("P3 Generic Function Body (S9a)");

            // 1. 全放行形态：形参/返回类型为 T、局部 T 声明（含字段链定型）、
            //    is T 静态目标（泛型参数不做静态收窄）
            var (unit, bodies) = BindUnit(
                "pub open class Box\\<T> { pub var item: T\n    pub init(_ -> item) { } }\n" +
                "func use\\<T>(b: Box\\<T>): T {\n" +
                "    var y: T = b.item\n" +
                "    if (y is T) { return y }\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型函数体全放行）", unit);
            TestHarness.Check("泛型函数体绑定形态",
                BoundDescribe.Body(BodyOf(bodies, "use")),
                "Body(use, [y: T], " +
                "[Decl(y, T, = InstField(item, Param(b,Box<T>), T)); " +
                "If(Is(Local(y,T), T), [Return(Local(y,T))]); Return(Local(y,T))])");

            // 2. 泛型字段替换身份：Box<T> 实参为外层 T 时 item 类型 = 外层 T
            //    （引用相等——替换取构造实参本身而非声明参数，S9a 修
            //    SubstituteFieldType：实参可为泛型参数）
            var useMethod = bodies.Single(b => b.Method.Name == "use").Method;
            var outerT = useMethod.GenericParameters.Single(p => p.Name == "T");
            var decl = (BoundLocalDeclarationStatement)BodyOf(bodies, "use").Body.Statements[0];
            var fieldAccess = (BoundFieldAccessExpression)decl.Initializer!;
            TestHarness.CheckTrue("泛型字段替换身份（item 类型 = 外层 T）",
                ReferenceEquals(fieldAccess.Type, outerT));

            // 3. 语句位置泛型调用（S9b）：泛型实参被调用形态消费——非泛型
            //    方法带实参报「not a generic method」（不再静默丢弃实参）
            var (unit2, _) = BindUnit(
                "func foo(x: i32): i32 { return x }\n" +
                "func main() { foo\\<i32>(1) }\n");
            TestHarness.CheckSemanticError("语句位置泛型调用非泛型拒绝", unit2.Diagnostics,
                "'foo' is not a generic method");

            // 4. typeOf(T) 类型形态：T → Type<T> 构造（实参即外层 T 引用）
            var (unit3, bodies3) = BindUnit(
                "func f\\<T>(x: T): Type\\<T> { return typeOf(T) }\n");
            CheckNoErrors("无诊断（typeOf 类型形态泛型参数）", unit3);
            TestHarness.Check("typeOf(T) 绑定形态",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(TypeOf(type T, Type<T>))])");

            // 5. 泛型参数 new 归 S9c（静态 init 查找不可行）
            var (unit4, _) = BindUnit(
                "func f\\<T>(): T { return new T() }\n");
            TestHarness.CheckSemanticError("泛型参数 new 归 S9c", unit4.Diagnostics,
                "P3: constructing a generic type parameter is not supported yet (S9)");
        }
    }
}
