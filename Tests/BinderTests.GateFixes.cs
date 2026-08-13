using System.Linq;

namespace RigiCompiler.Tests
{
    // P3 闸门/驱动/重载修复批次（review 确认问题）的回归测试：
    // 1. async 闸门 2 对可变参数包按 SYNTAX §4.5 判定「展开后的每一个
    //    实参类型」（包自身 Array\<Any\> 打包形态不参与）+ 闸门 5 覆盖
    //    泛型可变包推导类型（GenericPack 槽）；
    // 2. 泛型 backing 字段的自动访问器体合成（getter return value /
    //    setter 隐含赋值）与 getter 全路径 return 检查（口径 != null）；
    // 3. 显式泛型实参逐候选约束检查（§3.6——首拒次收调用成功、全部
    //    拒绝才诊断且不逐候选重复）；
    // 4. 歧义诊断只列真正平局的 winners 子集 + 空泛型包 Syntax 非空契约。
    public static partial class BinderTests
    {
        private static void TestGateFixes()
        {
            TestAsyncGateVariadicPacks();
            TestGenericBackingAccessors();
            TestPerCandidateConstraints();
            TestAmbiguityWinnersAndPackSyntax();
        }

        // ===== async 闸门 2/5 可变参数包（§4.5）=====
        private static void TestAsyncGateVariadicPacks()
        {
            TestHarness.Section("P3 Async Gates Variadic Pack Fixes (§4.5)");

            // 正例：位置值包元素共享安全（i32）→ 无诊断
            // （修复前包整体按 Array\<Any\> 判定，任何带包 async 调用误报）
            var (unit, _) = BindUnit(
                "async func log(nums: i32...) { }\n" +
                "func f() { log(1, 2) }\n");
            CheckNoErrors("位置值包元素共享安全无诊断", unit);

            // 正例：空值包（log()）同不误报
            var (unit2, _) = BindUnit(
                "async func log(nums: i32...) { }\n" +
                "func f() { log() }\n");
            CheckNoErrors("空值包无诊断", unit2);

            // 正例：具名值包元素共享安全
            var (unit3, _) = BindUnit(
                "async func log(options: named i32...) { }\n" +
                "func f() { log(a = 1, b = 2) }\n");
            CheckNoErrors("具名值包元素共享安全无诊断", unit3);

            // 反例：泛型可变包实参/推导类型非共享安全——闸门 2（展开
            // 实参值）与闸门 5（推导泛型实参，GenericPack 槽）双双命中
            // （修复前闸门 5 对包推导类型漏查、闸门 2 对包整体误判）
            var (unit4, _) = BindUnit(
                "class LocalUser { }\n" +
                "async func collect\\<TArgs...>(xs: TArgs...) { }\n" +
                "func f() { collect(new LocalUser()) }\n");
            TestHarness.CheckSemanticError("闸门 2 包展开实参非共享安全", unit4.Diagnostics,
                "argument of async function 'collect' must be a shared-safe type: 'LocalUser'");
            TestHarness.CheckSemanticError("闸门 5 包推导类型非共享安全", unit4.Diagnostics,
                "type argument of async function 'collect' must be a shared-safe type: 'LocalUser'");

            // 正例：泛型可变包推导类型共享安全（i32/String）
            var (unit5, _) = BindUnit(
                "async func collect\\<TArgs...>(xs: TArgs...) { }\n" +
                "func f() { collect(1, \"s\") }\n");
            CheckNoErrors("泛型包共享安全推导类型无诊断", unit5);
        }

        // ===== 泛型 backing 字段访问器（§9.4.1 + S9a 值层契约）=====
        private static void TestGenericBackingAccessors()
        {
            TestHarness.Section("P3 Generic Backing Accessors");

            // 正例：泛型 backing 自动访问器端到端（P1–P3）——自动 getter
            // 合成 return value（backing 读，类型 T）、自动 setter 体首
            // 合成隐含赋值 backing = value（修复前两处均按 is TypeSymbol
            // 跳过，泛型 backing 合成空体/丢赋值）
            var (unit, bodies) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T {\n" +
                "        pub get\n" +
                "        pub set\n" +
                "    }\n" +
                "    pub init(_ -> item) { }\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型 backing 自动访问器）", unit);
            var getterBody = bodies.Single(b => b.Method.Kind == MethodKind.Getter
                && b.Method.Name == "item");
            TestHarness.Check("自动 getter 合成体（return value，T）",
                BoundDescribe.Body(getterBody),
                "Body(item, [], [Return(InstField(item, This(Box), T))])");
            var setterBody = bodies.Single(b => b.Method.Kind == MethodKind.Setter
                && b.Method.Name == "item");
            TestHarness.Check("自动 setter 合成体（隐含赋值，T）",
                BoundDescribe.Body(setterBody),
                "Body(item, [], [Assign(InstField(item, This(Box), T), Param(value,T))])");

            // 反例：返回类型为泛型参数 T 的空体 computed getter（(_: _)
            // 形态）→ 全路径 return 检查（修复前 ReturnType is TypeSymbol
            // 判定跳过检查，空体漏诊断；口径与 BindBody 的 != null 统一）
            var (unit2, _) = BindUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T {\n" +
                "        pub get(_: _) { }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("返回 T 的空体 getter 报缺 return", unit2.Diagnostics,
                "Function 'item' must return a value on all code paths");
        }

        // ===== 显式泛型实参逐候选约束检查（§3.6）=====
        private static void TestPerCandidateConstraints()
        {
            TestHarness.Section("P3 Per-candidate Constraint Checks (§3.6)");

            // 首拒次收：首候选（声明序在前）约束拒绝 i32，次候选无约束
            // 接受——调用成功并命中次候选（修复前只对 matching[0] 检查，
            // 整调用误拒）
            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "func pick\\<T extends Animal>(x: T): i32 { return 1 }\n" +
                "func pick\\<T>(x: T, y: i32 = 0): i32 { return 2 }\n" +
                "func main() { var v = pick\\<i32>(5) }\n");
            CheckNoErrors("无诊断（首拒次收调用成功）", unit);
            var call = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("命中无约束的次候选（2 形参 + 显式泛型实参）",
                call.Method.Parameters.Count == 2 && call.TypeArguments.Count == 1);

            // 全部候选约束拒绝：对首候选回放一次诊断（不逐候选重复）
            var (unit2, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func pick\\<T extends Animal>(x: T): i32 { return 1 }\n" +
                "func pick\\<T extends Dog>(x: T, y: i32 = 0): i32 { return 2 }\n" +
                "func main() { var v = pick\\<i32>(5) }\n");
            TestHarness.CheckSemanticError("全部候选约束拒绝即诊断", unit2.Diagnostics,
                "Type argument 'i32' does not satisfy the 'Extends Animal' constraint of 'T'");
            TestHarness.CheckTrue("约束诊断不逐候选重复",
                unit2.Diagnostics.Diagnostics.Count(d => d.Message.Contains("constraint")) == 1);

            // 约束满足的多候选正常 ranking（平局打破：填充默认值更少者胜）
            var (unit3, bodies3) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func pick\\<T extends Animal>(x: T): i32 { return 1 }\n" +
                "func pick\\<T>(x: T, y: i32 = 0): i32 { return 2 }\n" +
                "func main() { var v = pick\\<Dog>(new Dog()) }\n");
            CheckNoErrors("无诊断（约束满足多候选）", unit3);
            var call3 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies3, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("填充默认值更少的候选胜出（1 形参）",
                call3.Method.Parameters.Count == 1);
        }

        // ===== 歧义诊断 winners 子集 + 空泛型包 Syntax 契约 =====
        private static void TestAmbiguityWinnersAndPackSyntax()
        {
            TestHarness.Section("P3 Ambiguity Winners & Generic Pack Syntax");

            // 歧义消息只列真正平局的 winners（修复前列全部 applicable，
            // 被占优淘汰的 m(Base, Base) 误导定位）
            var (unit, _) = BindUnit(
                "open class Base { }\n" +
                "open class Mid : Base { }\n" +
                "class Leaf : Mid { }\n" +
                "func m(a: Mid, b: Base) { }\n" +
                "func m(a: Base, b: Mid) { }\n" +
                "func m(a: Base, b: Base) { }\n" +
                "func f(l: Leaf) { m(l, l) }\n");
            var ambiguous = unit.Diagnostics.Diagnostics
                .Single(d => d.Message.Contains("ambiguous"));
            TestHarness.CheckTrue("歧义消息列出平局 winners",
                ambiguous.Message.Contains("m(Mid, Base)")
                && ambiguous.Message.Contains("m(Base, Mid)"));
            TestHarness.CheckTrue("歧义消息不含被占优候选",
                !ambiguous.Message.Contains("m(Base, Base)"));

            // 空泛型包推导产物的 Syntax 非空契约（修复前空包以 null! 占位；
            // 以调用节点承载）
            var (unit2, bodies2) = BindUnit(
                "func collect\\<TArgs...>(xs: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = collect() }\n");
            CheckNoErrors("无诊断（空泛型包）", unit2);
            var call2 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bodies2, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("空包 Syntax 以调用节点承载",
                call2.GenericPack != null
                && ReferenceEquals(call2.GenericPack.Syntax, call2.Syntax)
                && call2.GenericPack.TypeArguments.Count == 0);
        }
    }
}
