using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ControlFlow 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestIfElse()
        {
            var thenBranch = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    if (x == 1) {\n" +
                "        return 2\n" +
                "    } else {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n");
            CheckOk("if then", thenBranch);
            CheckI32("if then 返回 2", thenBranch, 2);
            var elseBranch = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    if (x == 1) {\n" +
                "        return 2\n" +
                "    } else {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n");
            CheckOk("if else", elseBranch);
            CheckI32("if else 返回 3", elseBranch, 3);
            var noElse = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    if (false) { x = 9 }\n" +
                "    return x\n" +
                "}\n");
            CheckOk("if 无 else", noElse);
            CheckI32("if 无 else 不改 x", noElse, 1);
            var expr = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    return if ((x > 0)) { 4 } else { 5 }\n" +
                "}\n");
            CheckOk("if 表达式", expr);
            CheckI32("if 表达式 then", expr, 4);
            var seq = Run(
                "pub func main(): i32 {\n" +
                "    seq {\n" +
                "        core.io.Console.println(\"a\")\n" +
                "    }\n" +
                "    core.io.Console.println(\"b\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("call blk seq", seq);
            CaseAssertions.Check("seq stdout", seq.Stdout, "a\nb\n");
        }

        private static void TestWhileAndDoWhile()
        {
            var whileLoop = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    var n: i32 = 0\n" +
                "    while (i < 4) {\n" +
                "        n = (n + i)\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("while", whileLoop);
            CheckI32("while 0+1+2+3", whileLoop, 6);
            var zero = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    while (false) { n = 1 }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("while 零次", zero);
            CheckI32("while false 不进体", zero, 0);
            var doWhile = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    do {\n" +
                "        i = (i + 1)\n" +
                "    } while (i < 3)\n" +
                "    return i\n" +
                "}\n");
            CheckOk("do-while", doWhile);
            CheckI32("do-while 至少一次到 3", doWhile, 3);
            var doOnce = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    do { i = 7 } while (false)\n" +
                "    return i\n" +
                "}\n");
            CheckOk("do-while 一次", doOnce);
            CheckI32("loop.rev body 至少一次", doOnce, 7);
        }

        private static void TestForRangeAndBreakContinue()
        {
            var range = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 4) {\n" +
                "        n = (n + i)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("for 范围", range);
            CheckI32("for [0,4) 求和", range, 6);
            var brk = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        i = (i + 1)\n" +
                "        n = (n + 1)\n" +
                "        if (i == 3) { break }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("break", brk);
            CheckI32("break 在 3", brk, 3);
            var cont = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 4) {\n" +
                "        i = (i + 1)\n" +
                "        if (i == 2) { continue }\n" +
                "        n = (n + i)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("continue", cont);
            CheckI32("continue 跳过 2", cont, 8);
        }

        // 范围循环经约束：动态派发命中类型自己的 EnumerateInRange
        private static void TestForRangeConstraintDispatch()
        {
            var viaI32 = Run(
                "pub func sum\\<T extends i32>(a: T, b: T): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return sum(1, 5)\n" +
                "}\n");
            CheckOk("T extends i32 范围循环", viaI32);
            CheckI32("sum(1,5)=10（[1,5)）", viaI32, 10);

            var viaStep = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func count\\<T extends Step>(a: T, b: T): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in a to b) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return count(new Step(1), new Step(3))\n" +
                "}\n");
            CheckOk("T extends Step 自定义枚举", viaStep);
            CheckI32("count(Step(1),Step(3))=15（[1,6)）", viaStep, 15);

            var direct = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to new Step(3)) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("直接自定义类型范围循环", direct);
            CheckI32("Step [1,6) 求和 15", direct, 15);

            var stepToI32 = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to 5) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("Step to i32 不同型两端", stepToI32);
            CheckI32("Step(1) to 5 = 10（[1,5)）", stepToI32, 10);

            var overloadI32 = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to 5) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("重载选 end: i32", overloadI32);
            CheckI32("Step(1) to 5 走 i32 重载 = 10", overloadI32, 10);

            var overloadStep = Run(
                "pub open class Step {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator EnumerateInRange(end: i32): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, end)\n" +
                "    }\n" +
                "    pub operator EnumerateInRange(end: Step): core.collections.IEnumerable\\<i32> {\n" +
                "        return new core.collections.RangeI32(this.v, (end.v * 2))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    for (i in new Step(1) to new Step(3)) {\n" +
                "        total = (total + i)\n" +
                "    }\n" +
                "    return total\n" +
                "}\n");
            CheckOk("重载选 end: Step", overloadStep);
            CheckI32("Step(1) to Step(3) 走 Step 重载 = 15", overloadStep, 15);
        }

        // 具名导入泛型类型定义后实例化（§15.2）
        private static void TestNamedImportGenericType()
        {
            var result = Run(
                "import core.Pair\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Pair\\<i32, String>(7, \"ok\")\n" +
                "    return p.key\n" +
                "}\n");
            CheckOk("import core.Pair 后实例化", result);
            CheckI32("Pair.key == 7", result, 7);
        }

        private static void TestNamedBreakContinue()
        {
            var namedBreak = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 3) named outer {\n" +
                "        for (j in 0 to 3) {\n" +
                "            n = (n + 1)\n" +
                "            break@outer\n" +
                "        }\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("named break", namedBreak);
            CheckI32("break@outer 一次", namedBreak, 1);
            var namedContinue = Run(
                "pub func main(): i32 {\n" +
                "    var n: i32 = 0\n" +
                "    for (i in 0 to 3) named outer {\n" +
                "        n = (n + 1)\n" +
                "        for (j in 0 to 3) {\n" +
                "            continue@outer\n" +
                "        }\n" +
                "        n = (n + 100)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("named continue", namedContinue);
            CheckI32("continue@outer 跳过 100", namedContinue, 3);
        }

        private static void TestSwitchStatementAndExpression()
        {
            var stmt = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 2\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch 语句", stmt);
            CheckI32("switch 命中 2", stmt, 20);
            var def = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 9\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 10 }\n" +
                "        (2) -> { return 20 }\n" +
                "        default -> { return 7 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch default", def);
            CheckI32("switch 走 default", def, 7);
            var expr = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 2\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 10 }\n" +
                "        (2) -> { 20 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch 表达式", expr);
            CheckI32("switch 表达式 20", expr, 20);
            var pattern = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 12\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 1 }\n" +
                "        (_ > 10) -> { 2 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckOk("switch pattern", pattern);
            CheckI32("pattern _ > 10", pattern, 2);
        }

        private static void TestConditionalExpectedTypeMaterialization()
        {
            var implicitPick = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub class Cat : Animal { pub init() {} }\n" +
                "pub func pick(flag: bool): Animal {\n" +
                "    return if (flag) { new Dog() } else { new Cat() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(true)\n" +
                "    if (a is Dog) { return 1 } else { return 2 }\n" +
                "}\n");
            CheckOk("隐式 if 公共基类", implicitPick);
            CheckI32("隐式 if 命中 Dog", implicitPick, 1);
            var explicitPick = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub class Cat : Animal { pub init() {} }\n" +
                "pub func pick(flag: bool): Animal {\n" +
                "    return if (flag) { return@_ new Dog() } else { return@_ new Cat() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(false)\n" +
                "    if (a is Cat) { return 3 } else { return 4 }\n" +
                "}\n");
            CheckOk("显式 return@ if 公共基类", explicitPick);
            CheckI32("显式 if 命中 Cat", explicitPick, 3);
            var switchPick = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub class Cat : Animal { pub init() {} }\n" +
                "pub func pick(flag: bool): Animal {\n" +
                "    return switch (flag) {\n" +
                "        (true) -> { new Dog() }\n" +
                "        default -> { new Cat() }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(true)\n" +
                "    if (a is Dog) { return 5 } else { return 6 }\n" +
                "}\n");
            CheckOk("隐式 switch 公共基类", switchPick);
            CheckI32("隐式 switch 命中 Dog", switchPick, 5);
            var nullable = Run(
                "pub func pick(flag: bool): String? {\n" +
                "    return if (flag) { null } else { \"x\" }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = pick(true)\n" +
                "    var b = pick(false)\n" +
                "    if (a == null) {\n" +
                "        if (b == null) { return 0 } else { return 8 }\n" +
                "    }\n" +
                "    return 9\n" +
                "}\n");
            CheckOk("隐式 if 可空", nullable);
            CheckI32("null / \"x\" 物化", nullable, 8);
        }

    }
}
