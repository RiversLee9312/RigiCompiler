using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    // LowererTests 值组：cast 恒等/字符串插值与子类型 cast 物化/?. 安全调用/
    // if? 空值回退/解构脱糖/is 类型谓词/typeOf。

    public static partial class LowererTests
    {
        // ===== cast 恒等降级（S7e，BIL §12.1/§12.2）=====
        private static void TestCastLowering()
        {
            var (unit, bound, lowered) = LowerUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n" +
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("无诊断（cast）", unit);
            TestHarness.Check("as 降级形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Return(Cast(Param(s,String), String, String))])");
            TestHarness.Check("as? 降级形态", LoweredDescribe.Body(BodyOf(lowered, "g")),
                "Body(g, [], [Return(SafeCast(Param(s,String), String, String?))])");
            // Origin 回指引用相等（恒等降级）
            var boundCast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)bound.Single(b => b.Method.Name == "f").Body).Statements[0]).Value!;
            var loweredCast = (LoweredCastExpression)((LoweredReturnStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("cast Origin 回指 Bound 节点",
                ReferenceEquals(loweredCast.Origin, boundCast));
        }

        // ===== 字符串插值与子类型 cast 物化（S7f，BIL §6.5）=====
        // 插值经 P3 绑定为 toString/+ 链后恒等降级；值类型 receiver 调
        // Any 承诺的 toString 时物化装箱 cast（§12.1 内建引用视图转换）
        private static void TestInterpolationLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "func f() {\n" +
                "    var n = 42\n" +
                "    var s = \"n=${n}\"\n" +
                "}\n");
            CheckNoErrors("无诊断（插值降级）", unit);
            TestHarness.Check("值类型段 toString 的 receiver 装箱 cast",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [n: i32, s: String], [Decl(n, i32, = Int(42,i32)); " +
                "Decl(s, String, = Binary(Add, Str(\"n=\",String), " +
                "InstCall(toString, Cast(Local(n,i32), Any, Any), [], String), String))])");

            // return 位置的子类型 cast 物化（实现/派生 → 声明类型视图）
            var (unit2, _, lowered2) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): Animal { return d }\n");
            CheckNoErrors("无诊断（return cast 物化）", unit2);
            TestHarness.Check("return 子类型 cast 物化",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [], [Return(Cast(Param(d,Dog), Animal, Animal))])");

            // 局部声明初始化位置的子类型 cast 物化（值类型 → 接口装箱）
            var (unit3, _, lowered3) = LowerUnit(
                "interface Greeter { func greet(): String }\n" +
                "class Bot implements Greeter { pub func greet(): String { return \"hi\" } }\n" +
                "func f() {\n" +
                "    var g: Greeter = new Bot()\n" +
                "}\n");
            CheckNoErrors("无诊断（初始化 cast 物化）", unit3);
            TestHarness.Check("局部初始化子类型 cast 物化",
                LoweredDescribe.Body(BodyOf(lowered3, "f")),
                "Body(f, [g: Greeter], [Decl(g, Greeter, = Cast(New(Bot, []), Greeter, Greeter))])");
        }

        // ===== `?.` 脱糖（S7f，SYNTAX §3.4；BIL §3.4）=====
        private static void TestSafeAccessLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("无诊断（?. 降级）", unit);
            // 物化 receiver + null 检查 + 非空分支 unwrap/成员/wrap
            TestHarness.Check("?. 脱糖形态（字段）",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: User?, .s1: String?], [" +
                "Assign(Local(.s0,User?), Param(u,User?)); " +
                "Assign(Local(.s1,String?), Const(null,String?)); " +
                "If(Binary(CmpNe, Local(.s0,User?), Const(null,User?), bool), " +
                "[Assign(Local(.s1,String?), " +
                "Cast(InstField(name, Cast(Local(.s0,User?), User, User), String), String?, String?))]); " +
                "Return(Local(.s1,String?))])");

            // 链式：a?.b?.c——内层安全访问的 receiver 是外层占位（unwrap cast 复用）
            var (unit2, _, lowered2) = LowerUnit(
                "class B { pub var c: String\n    pub init(_ -> c) { } }\n" +
                "class A { pub var b: B\n    pub init(_ -> b) { } }\n" +
                "func f(a: A?): String? { return a?.b?.c }\n");
            CheckNoErrors("无诊断（链式 ?. 降级）", unit2);
            TestHarness.Check("链式 ?. 脱糖形态",
                LoweredDescribe.Body(BodyOf(lowered2, "f")),
                "Body(f, [.s0: A?, .s1: B?, .s2: B?, .s3: String?], [" +
                "Assign(Local(.s0,A?), Param(a,A?)); " +
                "Assign(Local(.s1,B?), Const(null,B?)); " +
                "If(Binary(CmpNe, Local(.s0,A?), Const(null,A?), bool), " +
                "[Assign(Local(.s1,B?), " +
                "Cast(InstField(b, Cast(Local(.s0,A?), A, A), B), B?, B?))]); " +
                "Assign(Local(.s2,B?), Local(.s1,B?)); " +
                "Assign(Local(.s3,String?), Const(null,String?)); " +
                "If(Binary(CmpNe, Local(.s2,B?), Const(null,B?), bool), " +
                "[Assign(Local(.s3,String?), " +
                "Cast(InstField(c, Cast(Local(.s2,B?), B, B), String), String?, String?))]); " +
                "Return(Local(.s3,String?))])");
        }

        // ===== if? 脱糖（S7f，SYNTAX §3.4）=====
        private static void TestNullFallbackLowering()
        {
            var (unit, _, lowered) = LowerUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("无诊断（if? 降级）", unit);
            // 非空分支 unwrap，空分支求回退值（延迟求值由 if 结构保证）
            TestHarness.Check("if? 脱糖形态",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [.s0: User?, .s1: User], [" +
                "Assign(Local(.s0,User?), Param(u,User?)); " +
                "If(Binary(CmpNe, Local(.s0,User?), Const(null,User?), bool), " +
                "[Assign(Local(.s1,User), Cast(Local(.s0,User?), User, User))], " +
                "[Assign(Local(.s1,User), New(User, init, [Str(\"anon\",String)]))]); " +
                "Return(Local(.s1,User))])");
        }

        // ===== 解构脱糖（S7f，SYNTAX §18；BIL §3.4 精确字段读取）=====
        private static void TestDestructuringLowering()
        {
            var (unit, _, lowered) = LowerUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "func f() {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "}\n");
            CheckNoErrors("无诊断（解构降级）", unit);
            // pair 物化一次 + 逐字段读取（分量类型 = 构造实参）
            TestHarness.Check("解构脱糖形态",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [k: String, v: i32, .s0: Entry], [" +
                "[Assign(Local(.s0,Entry), New(Entry, init, [Str(\"a\",String), Int(1,i32)])); " +
                "Decl(k, String, = InstField(key, Local(.s0,Entry), String)); " +
                "Decl(v, i32, = InstField(value, Local(.s0,Entry), i32))]])");
        }

        // ===== 类型谓词 is 降级（S8a，恒等降级无脱糖；BIL §12.3 直接对应）=====
        private static void TestTypeCheckLowering()
        {
            // is 静态：恒等降级 + Origin 回指 + 目标类型符号透传
            var (unit, bound, lowered) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): bool { return d is Animal }\n");
            CheckNoErrors("无诊断（is 静态降级）", unit);
            TestHarness.Check("is 静态降级形态", LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Return(Is(Param(d,Dog), Animal))])");
            var boundIs = (BoundTypeCheckExpression)((BoundReturnStatement)
                bound.Single(b => b.Method.Name == "f").Body.Statements[0]).Value!;
            var loweredIs = (LoweredTypeCheckExpression)((LoweredReturnStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("is Origin 回指 Bound 节点",
                ReferenceEquals(loweredIs.Origin, boundIs));
            TestHarness.CheckTrue("is 目标类型符号透传（静态无 TargetValue）",
                ReferenceEquals(loweredIs.TargetType, boundIs.TargetType)
                && loweredIs.TargetValue == null);

            // is 动态：TargetValue 递归降级（Lowered 新节点，Origin 回指 Bound 值节点）
            var (unit2, bound2, lowered2) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func k(d: Dog): bool {\n" +
                "    var t = typeOf(d)\n" +
                "    return d is t\n" +
                "}\n");
            CheckNoErrors("无诊断（is 动态降级）", unit2);
            TestHarness.Check("is 动态降级形态", LoweredDescribe.Body(BodyOf(lowered2, "k")),
                "Body(k, [t: Type<Dog>], " +
                "[Decl(t, Type<Dog>, = TypeOf(Param(d,Dog), Type<Dog>)); " +
                "Return(Is(Param(d,Dog), dyn Local(t,Type<Dog>)))])");
            var boundDynIs = (BoundTypeCheckExpression)((BoundReturnStatement)
                bound2.Single(b => b.Method.Name == "k").Body.Statements[1]).Value!;
            var loweredDynIs = (LoweredTypeCheckExpression)((LoweredReturnStatement)
                BodyOf(lowered2, "k").Body.Statements[1]).Value!;
            TestHarness.CheckTrue("动态 is Origin 回指 + TargetValue 递归降级",
                ReferenceEquals(loweredDynIs.Origin, boundDynIs)
                && loweredDynIs.TargetValue != null
                && !ReferenceEquals(loweredDynIs.TargetValue, boundDynIs.TargetValue)
                && ReferenceEquals(loweredDynIs.TargetValue!.Origin, boundDynIs.TargetValue!));
        }

        // ===== typeOf 降级（S8a，恒等降级无脱糖；BIL §12.5 直接对应）=====
        private static void TestTypeOfLowering()
        {
            // 值形态：Operand 递归降级 + Origin 回指
            var (unit, bound, lowered) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(d: Dog): Type\\<Dog> { return typeOf(d) }\n");
            CheckNoErrors("无诊断（typeOf 值形态降级）", unit);
            TestHarness.Check("typeOf 值形态降级形态",
                LoweredDescribe.Body(BodyOf(lowered, "f")),
                "Body(f, [], [Return(TypeOf(Param(d,Dog), Type<Dog>))])");
            var boundTypeOf = (BoundTypeOfExpression)((BoundReturnStatement)
                bound.Single(b => b.Method.Name == "f").Body.Statements[0]).Value!;
            var loweredTypeOf = (LoweredTypeOfExpression)((LoweredReturnStatement)
                BodyOf(lowered, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("typeOf 值形态 Origin 回指 + Operand 递归降级",
                ReferenceEquals(loweredTypeOf.Origin, boundTypeOf)
                && loweredTypeOf.Operand != null
                && ReferenceEquals(loweredTypeOf.Operand!.Origin, boundTypeOf.Operand!)
                && loweredTypeOf.TargetType == null);

            // 类型形态：TargetType 符号透传
            var (unit2, bound2, lowered2) = LowerUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func m(): Type\\<Animal> { return typeOf(Animal) }\n");
            CheckNoErrors("无诊断（typeOf 类型形态降级）", unit2);
            TestHarness.Check("typeOf 类型形态降级形态",
                LoweredDescribe.Body(BodyOf(lowered2, "m")),
                "Body(m, [], [Return(TypeOf(type Animal, Type<Animal>))])");
            var boundTypeForm = (BoundTypeOfExpression)((BoundReturnStatement)
                bound2.Single(b => b.Method.Name == "m").Body.Statements[0]).Value!;
            var loweredTypeForm = (LoweredTypeOfExpression)((LoweredReturnStatement)
                BodyOf(lowered2, "m").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("typeOf 类型形态 Origin 回指 + 目标类型符号透传",
                ReferenceEquals(loweredTypeForm.Origin, boundTypeForm)
                && ReferenceEquals(loweredTypeForm.TargetType, boundTypeForm.TargetType)
                && loweredTypeForm.Operand == null);
        }
    }
}
