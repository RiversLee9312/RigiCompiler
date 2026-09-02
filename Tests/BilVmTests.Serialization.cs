namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Temporary 懒恢复：读时未物化调 resume、写后读走缓存、resume 捕获 this。
        private static void TestTemporaryLazyRestore()
        {
            var result = Run(
                "import core.serialization.Temporary\n" +
                "pub class Host {\n" +
                "    pub var hits: i32 = 0\n" +
                "    pub var path: String\n" +
                "    @Temporary((func{ (): String -> {\n" +
                "        hits = (hits + 1)\n" +
                "        return@_ path\n" +
                "    }} as core.Func\\<String>))\n" +
                "    pub var cached: String = \"\"\n" +
                "    pub init(_ -> path)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Host(\"hello\")\n" +
                "    var a = h.cached\n" +
                "    var b = h.cached\n" +
                "    h.cached = \"world\"\n" +
                "    var c = h.cached\n" +
                "    if ((((a == \"hello\") and (b == \"hello\")) and (c == \"world\")) and (h.hits == 1)) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    } else {\n" +
                "        core.io.Console.println(\"FAIL\")\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckOk("Temporary 懒恢复读/写后读/resume 捕获 this", result);
            TestHarness.Check("Temporary stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Temporary main 返回 0", result, 0);
        }

        // MW11d-B1：List 增删读越界 null、for-each
        private static void TestListAddGetRemoveIterate()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var xs = new List\\<i32>()\n" +
                "    xs.add(10)\n" +
                "    xs.add(20)\n" +
                "    xs.add(30)\n" +
                "    const a = xs.getAtIndex(1L)\n" +
                "    xs.removeAt(0L)\n" +
                "    const b = xs.getAtIndex(0L)\n" +
                "    const miss = xs.getAtIndex(99L)\n" +
                "    const neg = xs.getAtIndex(-1L)\n" +
                "    var sum = 0\n" +
                "    for (x in xs) { sum = (sum + x) }\n" +
                "    if (((((a if? 0) == 20) and ((b if? 0) == 20)) and (miss == null)) and (neg == null)) {\n" +
                "        if ((xs.length == 2L) and (sum == 50)) {\n" +
                "            core.io.Console.println(\"ok\")\n" +
                "            return 0\n" +
                "        }\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("List 增删读越界 null / 枚举", result);
            TestHarness.Check("List stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("List main 返回 0", result, 0);
        }

        // MW11d-B1：Map set/tryGet/containsKey/remove/枚举
        private static void TestMapSetGetRemoveIterate()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<String, i32>()\n" +
                "    m.set(\"a\", 1)\n" +
                "    m.set(\"b\", 2)\n" +
                "    m.set(\"a\", 9)\n" +
                "    const v = m.tryGet(\"a\")\n" +
                "    const miss = m.tryGet(\"z\")\n" +
                "    const has = m.containsKey(\"b\")\n" +
                "    const gone = m.remove(\"b\")\n" +
                "    const still = m.containsKey(\"b\")\n" +
                "    var n = 0\n" +
                "    for (p in m) { n = (n + 1) }\n" +
                "    if ((((((v if? 0) == 9) and (miss == null)) and has) and gone) and (not still)) {\n" +
                "        if ((m.count == 1L) and (n == 1)) {\n" +
                "            core.io.Console.println(\"ok\")\n" +
                "            return 0\n" +
                "        }\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Map set/tryGet/containsKey/remove/枚举", result);
            TestHarness.Check("Map stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Map main 返回 0", result, 0);
        }

        // MW11d-B1：Parcel set/get/嵌套/枚举/absent 抛 / 存 null 读 null
        private static void TestParcelSetGetNestedAbsentNull()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Parcel(\"Demo\")\n" +
                "    p.setElement\\<i32>(\"n\", 7)\n" +
                "    p.setElement\\<String>(\"s\", \"hi\")\n" +
                "    p.setElement\\<i32>(\"z\", null)\n" +
                "    var nested = new Parcel(\"Inner\")\n" +
                "    nested.setElement\\<i32>(\"k\", 1)\n" +
                "    p.setElement\\<Parcel>(\"child\", nested)\n" +
                "    const n = p.getElement\\<i32>(\"n\")\n" +
                "    const s = p.getElement\\<String>(\"s\")\n" +
                "    const z = p.getElement\\<i32>(\"z\")\n" +
                "    const child = p.getElement\\<Parcel>(\"child\")\n" +
                "    const ck = (child as Parcel).getElement\\<i32>(\"k\")\n" +
                "    var threw = 0\n" +
                "    try {\n" +
                "        const unused = p.getElement\\<i32>(\"absent\")\n" +
                "    } catch (e: core.NoSuchElementException) {\n" +
                "        threw = 1\n" +
                "    }\n" +
                "    var seen = 0\n" +
                "    for (pair in p) { seen = (seen + 1) }\n" +
                "    if ((((((n if? 0) == 7) and ((s if? \"\") == \"hi\")) and (z == null)) and ((ck if? 0) == 1)) and (threw == 1)) {\n" +
                "        if ((p.typeName == \"Demo\") and (seen == 4)) {\n" +
                "            core.io.Console.println(\"ok\")\n" +
                "            return 0\n" +
                "        }\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Parcel set/get/嵌套/枚举/absent/存 null", result);
            TestHarness.Check("Parcel stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Parcel main 返回 0", result, 0);
        }

        private static void TestSerializableScalarDeepCopy()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Mix {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub var f: double = 0.0\n" +
                "    pub var b: bool = false\n" +
                "    pub var c: char = 'x'\n" +
                "    pub var s: String = \"\"\n" +
                "    pub init(_ -> n, _ -> f, _ -> b, _ -> c, _ -> s)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Mix(1, 2.5, true, 'a', \"hi\")\n" +
                "    var copy = deepCopy\\<Mix>(src)\n" +
                "    src.n = 9\n" +
                "    src.s = \"bye\"\n" +
                "    if (((((copy.n == 1) and (copy.f == 2.5)) and (copy.b == true)) and (copy.c == 'a')) and (copy.s == \"hi\")) {\n" +
                "        if ((src.n == 9) and (src.s == \"bye\")) {\n" +
                "            return 42\n" +
                "        }\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("平铺标量深复制往返且互不影响", result);
            CheckI32("平铺标量 main 返回 42", result, 42);
        }

        private static void TestSerializableNestedIndependent()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Inner {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "@Serializable\n" +
                "pub class Outer {\n" +
                "    pub var a: Inner\n" +
                "    pub var b: Inner\n" +
                "    pub init(x: Inner) { a = x\n        b = x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var kid = new Inner(7)\n" +
                "    var src = new Outer(kid)\n" +
                "    var copy = deepCopy\\<Outer>(src)\n" +
                "    copy.a.n = 3\n" +
                "    if (((src.a.n == 7) and (src.b.n == 7)) and (copy.b.n == 7)) {\n" +
                "        if (copy.a.n == 3) {\n" +
                "            return 42\n" +
                "        }\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("嵌套对象图往返后兄弟字段独立", result);
            CheckI32("嵌套对象 main 返回 42", result, 42);
        }

        private static void TestSerializableCollectionsSnapshot()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "import core.collections.*\n" +
                "@Serializable\n" +
                "pub class Box {\n" +
                "    pub var nums: Array\\<i32>\n" +
                "    pub var names: List\\<String> = new List\\<String>()\n" +
                "    pub var ages: Map\\<String, i32> = new Map\\<String, i32>()\n" +
                "    pub init(_ -> nums)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var nums = arrayOfElements\\<i32>(1, 2)\n" +
                "    var src = new Box(nums)\n" +
                "    src.names.add(\"a\")\n" +
                "    src.ages.set(\"k\", 4)\n" +
                "    var copy = deepCopy\\<Box>(src)\n" +
                "    src.nums[0] = 99\n" +
                "    src.names.add(\"b\")\n" +
                "    src.ages.set(\"k\", 5)\n" +
                "    const cn = copy.nums[0]\n" +
                "    const cl = copy.names.getAtIndex(0L)\n" +
                "    const cm = copy.ages.tryGet(\"k\")\n" +
                "    if (((((cn if? 0) == 1) and ((cl if? \"\") == \"a\")) and ((cm if? 0) == 4)) and (copy.names.length == 1L)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("集合字段往返快照不变量", result);
            CheckI32("集合字段 main 返回 42", result, 42);
        }

        private static void TestSerializableTemporaryResume()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Host {\n" +
                "    pub var n: i32 = 0\n" +
                "    @Temporary((func{ (): i32 -> {\n" +
                "        return@_ (n * 2)\n" +
                "    }} as core.Func\\<i32>))\n" +
                "    pub var derived: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Host(5)\n" +
                "    const d0 = src.derived\n" +
                "    var copy = deepCopy\\<Host>(src)\n" +
                "    src.n = 9\n" +
                "    src.derived = 1\n" +
                "    const d1 = copy.derived\n" +
                "    if (((d0 == 10) and (copy.n == 5)) and (d1 == 10)) {\n" +
                "        if (src.derived == 1) {\n" +
                "            return 42\n" +
                "        }\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Temporary 往返后新实例 resume 基于新 this", result);
            CheckI32("Temporary 往返 main 返回 42", result, 42);
        }

        private static void TestSerializableGenericClone()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Marked {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "func clone\\<T with Serializable>(x: T): T {\n" +
                "    return deepCopy\\<T>(x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Marked(11)\n" +
                "    var copy = clone\\<Marked>(src)\n" +
                "    src.n = 0\n" +
                "    if (copy.n == 11) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("with Serializable 泛型 clone/deepCopy", result);
            CheckI32("泛型 clone main 返回 42", result, 42);
        }
    }
}
