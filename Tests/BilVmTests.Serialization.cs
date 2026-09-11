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

        // Map 键判等缺陷回归锚（Any.hash 裁定）：两个同类实例不重载 hash——
        // 默认 toString 同为类型名，旧 toString 判等下互相覆盖（count==1）；
        // 新判等 hash（对象默认身份哈希）不同 → 两键共存且各自可查
        private static void TestMapObjectKeyIdentityNotMerged()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub class Key { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<Key, i32>()\n" +
                "    const k1 = new Key()\n" +
                "    const k2 = new Key()\n" +
                "    m.set(k1, 1)\n" +
                "    m.set(k2, 2)\n" +
                "    if (((m.count == 2L) and ((m.tryGet(k1) if? 0) == 1)) and ((m.tryGet(k2) if? 0) == 2)) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Map 对象键身份判等不互相覆盖", result);
            TestHarness.Check("Map 对象键 stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Map 对象键 main 返回 0", result, 0);
        }

        // String/标量键内容判等语义不变：内容 hash 一致（默认 equals =
        // 双虚调 hash 比较，equals-or-hash 链）→ 合并
        private static void TestMapContentKeysStillMerge()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<String, i32>()\n" +
                "    m.set(\"k\", 1)\n" +
                "    m.set(\"k\" + \"\", 2)\n" +
                "    var n = new Map\\<i32, String>()\n" +
                "    n.set(7, \"seven\")\n" +
                "    n.set(7, \"SEVEN\")\n" +
                "    n.set(8, \"eight\")\n" +
                "    if ((((m.count == 1L) and ((m.tryGet(\"k\") if? 0) == 2)) and ((n.tryGet(7) if? \"\") == \"SEVEN\")) and ((n.tryGet(8) if? \"\") == \"eight\")) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Map String/标量键内容合并语义不变", result);
            TestHarness.Check("Map 内容键 stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Map 内容键 main 返回 0", result, 0);
        }

        // 自定义类 override hash(): i64 → 按用户哈希判等（equals-or-hash 链：
        // 未声明 equals 的键走默认 equals = 双虚调 hash 比较，同哈希即同键）
        private static void TestMapCustomHashKeySemantics()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub class Badge {\n" +
                "    pub var code: i32\n" +
                "    pub init(v: i32) { code = v }\n" +
                "    pub override func hash(): i64 { return (code as i64) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<Badge, i32>()\n" +
                "    m.set(new Badge(5), 1)\n" +
                "    m.set(new Badge(5), 2)\n" +
                "    m.set(new Badge(6), 3)\n" +
                "    if (((m.count == 2L) and ((m.tryGet(new Badge(5)) if? 0) == 2)) and ((m.tryGet(new Badge(6)) if? 0) == 3)) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Map 自定义 hash 按用户哈希判等", result);
            TestHarness.Check("Map 自定义 hash stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Map 自定义 hash main 返回 0", result, 0);
        }

        // 键类型声明 operator equals（不 override hash）→ 按 equals 判等
        //（equals-or-hash 链：运行期最派生的 equals 优先于默认 hash 比较；
        // 同字段 equals=true 合并 count 不增，equals=false 不合并；String/
        // 标量键内容判等回归断言同例）
        private static void TestMapCustomEqualsKeySemantics()
        {
            var result = Run(
                "import core.collections.*\n" +
                "pub class Tag {\n" +
                "    pub var id: i32\n" +
                "    pub init(v: i32) { id = v }\n" +
                "    pub operator equals(other: Tag): bool { return (id == other.id) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Map\\<Tag, i32>()\n" +
                "    m.set(new Tag(5), 1)\n" +
                "    m.set(new Tag(5), 2)\n" +
                "    m.set(new Tag(6), 3)\n" +
                "    var s = new Map\\<String, i32>()\n" +
                "    s.set(\"k\", 1)\n" +
                "    s.set(\"k\", 9)\n" +
                "    var n = new Map\\<i32, i32>()\n" +
                "    n.set(7, 1)\n" +
                "    n.set(7, 7)\n" +
                "    if (((((m.count == 2L) and ((m.tryGet(new Tag(5)) if? 0) == 2)) and ((m.tryGet(new Tag(6)) if? 0) == 3)) and (s.count == 1L)) and (n.count == 1L)) {\n" +
                "        core.io.Console.println(\"ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    core.io.Console.println(\"FAIL\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("Map 自定义 equals 键按 equals 判等", result);
            TestHarness.Check("Map 自定义 equals 键 stdout 精确 ok", result.Stdout, "ok\n");
            CheckI32("Map 自定义 equals 键 main 返回 0", result, 0);
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

        // review-20260910 #04：@Serializable + 带 .proxy.* 的有状态 Entity
        // wrapper 共存时，重建协议方法（..init.serializable / ..decode.graph）
        // 不得绕 Entity wrapper 方法链——否则链在 wrapper 安装完成前读实例
        private static void TestSerializableWithStatefulEntityWrapperDeepCopy()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub shared wrapper Audited\\<TTarget> {\n" +
                "    pub var events: i64\n" +
                "    pub init(level: i32 = 0) { events = 0L }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn { return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs) }\n" +
                "}\n" +
                "@Serializable\n" +
                "@Audited(1)\n" +
                "pub class Station {\n" +
                "    pub var name: String\n" +
                "    pub init(_ -> name)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const copy = deepCopy\\<Station>(new Station(\"A\"))\n" +
                "    if (copy.name != \"A\") { return 1 }\n" +
                "    if (copy:Audited.events != 0L) { return 2 }\n" +
                "    const p2 = copy:Serializable.toParcel()\n" +
                "    const copy2 = fromParcel\\<Station>(p2)\n" +
                "    if (copy2:Audited.events != 0L) { return 3 }\n" +
                "    return 42\n" +
                "}\n");
            CheckOk("#04 序列化+有状态 Entity wrapper 深复制", result);
            CheckI32("#04 main 返回 42", result, 42);
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

        // @SerializationBase 是独立能力，不向宿主合成 Serializable 代理。
        private static void TestSerializationBaseImpliesSerializable()
        {
            var result = Run(
                "func identity\\<T with SerializationBase>(value: T): T { return value }\n" +
                "pub func main(): i32 {\n" +
                "    var src = new core.collections.List\\<i32>()\n" +
                "    src.add(7)\n" +
                "    var copy = identity(src)\n" +
                "    if ((copy.getAtIndex(0L) as i32) == 7) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("@SerializationBase 独立能力运行", result);
            CheckI32("base-only main 返回 42", result, 42);
        }

        private static void TestSerializationDepthBudget()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class DepthNode {\n" +
                "    pub var next: DepthNode?\n" +
                "    pub init() { next = null }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const root = new DepthNode()\n" +
                "    var tail = root\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 300) {\n" +
                "        const next = new DepthNode()\n" +
                "        tail.next = next\n" +
                "        tail = next\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    try {\n" +
                "        const copy = deepCopy\\<DepthNode>(root)\n" +
                "    } catch (e: core.IllegalStateException) { return 42 }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("序列化深度预算抛语言异常", result);
            CheckI32("序列化深度预算可捕获", result, 42);
        }

        private static void TestSerializationModeMismatch()
        {
            var result = Run(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class ModeNode {\n" +
                "    pub var next: ModeNode?\n" +
                "    pub init() { next = null }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const root = new ModeNode()\n" +
                "    root.next = new ModeNode()\n" +
                "    const wire = root:Serializable.toParcel(true)\n" +
                "    try {\n" +
                "        const copy = fromParcel\\<ModeNode>(wire, false)\n" +
                "    } catch (e: core.IllegalStateException) { return 42 }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("序列化模式错配抛语言异常", result);
            CheckI32("图 wire 当树读取被拒", result, 42);
        }
    }
}
