// ============================================================================
// nullable_equality.rg —— nullablefix 回归语料（SYNTAX §3.4 可空判等条）
// 同型 T? 的 ==/!= 语义 = nullness 短路 + 解包内层判等（双宿主对拍，
// NativeE2E「可空判等对拍」Case 复用本语料）。
//   ① 内层矩阵：String（引用内建内容判等）/ i32（标量）/ 自定义 class
//      （覆写 equals，引用内层）/ PlainStruct（覆写 equals 不覆写 hash）/
//      RichStruct（equals+hash 双覆写，core.fs.Path 同款富 struct）。
//   ② 值矩阵：双非空同值 / 双非空异值 / 单空（左右各一）/ 双空。
//   ③ 来源矩阵：局部 / 函数形参 / 字段。
//   ④ != 全组合取反。
//   ⑤ 开放泛型 T?：class/String/struct/i32 的双空、左右单空、
//      双非空同值/异值，== 与 != 分别断言；同一对象但 equals
//      主动返回 false 时不得用胖值身份短路绕过用户 operator。
//   ⑥ 缺陷 A：Any? = null 经 as Any 解包放行且结果 == null 为 true；
//      Map<String, Any> 非空解包路径行为不变。
// 修复前现状（双宿主各自错误）：native T?==T? 胖值位比——引用/堆盒
// struct 内层同值内容恒 false；VM 对覆写 hash 的 struct 内层崩「字段
// 访问目标不是对象：.nullable<...>」，无 hash override 的值类型落身份
// 哈希误判；Any?→Any 的 null 解包 VM 放行/native 抛 CastException。
// expect-output: nullable-equality-ok
// expect-exit: 0
// ============================================================================
import core.collections.*
import core.fs.*
import core.io.Console
import core.text.*

// 失败计数（0 = 通过）；失败时打印名字定位
func check(name: String, cond: bool): i32 {
    if (cond) { return 0 }
    Console.println("check-FAIL ${name}")
    return 1
}

// ---- 内层类型定义 ----

// 覆写 equals 不覆写 hash（值类型）——VM 曾落身份哈希误判等值内容
pub struct PlainStruct {
    pub var v: i32
    pub init(_ -> v) { }
    pub operator equals(other: PlainStruct): bool { return v == other.v }
}

// 覆写 equals 的 class（引用内层）
pub class EqClass {
    pub var v: i32
    pub init(_ -> v) { }
    pub operator equals(other: EqClass): bool { return v == other.v }
}

// 同一对象也不等：用户 equals 必须被调用，不能以胖值身份命中提前返回。
pub class NeverEqual {
    pub init() { }
    pub operator equals(other: NeverEqual): bool { return false }
}

// 字段来源载体
pub class BoxH\<T> {
    pub var item: T?
    pub init() { }
}

// ---- 各内层的 ==/!= 全组合（形参来源）----

func strEq(a: String?, b: String?): bool { return a == b }
func strNe(a: String?, b: String?): bool { return a != b }
func i32Eq(a: i32?, b: i32?): bool { return a == b }
func classEq(a: EqClass?, b: EqClass?): bool { return a == b }
func plainEq(a: PlainStruct?, b: PlainStruct?): bool { return a == b }
func pathEq(a: Path?, b: Path?): bool { return a == b }

func stringCases(): i32 {
    var r = 0
    var a: String? = "hello"
    var b: String? = "hello"
    var c: String? = "world"
    var n: String? = null
    r = r + check("str eq same", strEq(a, b))
    r = r + check("str ne same", not strNe(a, b))
    r = r + check("str eq diff", not strEq(a, c))
    r = r + check("str ne diff", strNe(a, c))
    r = r + check("str eq null-l", not strEq(n, a))
    r = r + check("str ne null-l", strNe(n, a))
    r = r + check("str eq null-r", not strEq(a, n))
    r = r + check("str eq null-null", strEq(n, n))
    r = r + check("str ne null-null", not strNe(n, n))
    // 局部来源直写
    r = r + check("str local eq same", a == b)
    r = r + check("str local ne same", not (a != b))
    r = r + check("str local eq null", not (a == n))
    r = r + check("str local eq nn", n == n)
    // 字段来源
    var h = new BoxH\<String>()
    h.item = "hello"
    r = r + check("str field eq same", h.item == b)
    r = r + check("str field ne diff", h.item != c)
    h.item = null
    r = r + check("str field eq null", h.item == n)
    return r
}

func i32Cases(): i32 {
    var r = 0
    var a: i32? = 7
    var b: i32? = 7
    var c: i32? = 8
    var n: i32? = null
    r = r + check("i32 eq same", i32Eq(a, b))
    r = r + check("i32 eq diff", not i32Eq(a, c))
    r = r + check("i32 eq null-l", not i32Eq(n, a))
    r = r + check("i32 eq null-null", i32Eq(n, n))
    r = r + check("i32 local ne diff", a != c)
    var h = new BoxH\<i32>()
    h.item = 7
    r = r + check("i32 field eq same", h.item == b)
    return r
}

func classCases(): i32 {
    var r = 0
    var a: EqClass? = new EqClass(1)
    var b: EqClass? = new EqClass(1)
    var c: EqClass? = new EqClass(2)
    var n: EqClass? = null
    r = r + check("class eq same", classEq(a, b))
    r = r + check("class eq diff", not classEq(a, c))
    r = r + check("class eq null-l", not classEq(n, a))
    r = r + check("class eq null-r", not classEq(a, n))
    r = r + check("class eq null-null", classEq(n, n))
    r = r + check("class ne diff", a != c)
    r = r + check("class ne null", n != a)
    return r
}

func plainStructCases(): i32 {
    var r = 0
    var a: PlainStruct? = new PlainStruct(7)
    var b: PlainStruct? = new PlainStruct(7)
    var c: PlainStruct? = new PlainStruct(8)
    var n: PlainStruct? = null
    r = r + check("plain eq same", plainEq(a, b))
    r = r + check("plain eq diff", not plainEq(a, c))
    r = r + check("plain eq null-l", not plainEq(n, a))
    r = r + check("plain eq null-null", plainEq(n, n))
    r = r + check("plain ne diff", a != c)
    var h = new BoxH\<PlainStruct>()
    h.item = new PlainStruct(7)
    r = r + check("plain field eq same", h.item == b)
    return r
}

func pathCases(): i32 {
    var r = 0
    var a: Path? = Path.of("C:\\a")
    var b: Path? = Path.of("C:\\a")
    var c: Path? = Path.of("C:\\b")
    var n: Path? = null
    // 富 struct（equals+hash 双覆写）：VM 修复前在此崩「字段访问目标
    // 不是对象：.nullable<...>」；native 修复前同值内容恒 false
    r = r + check("path eq same", pathEq(a, b))
    r = r + check("path ne same", not (a != b))
    r = r + check("path eq diff", not pathEq(a, c))
    r = r + check("path eq null-l", not pathEq(n, a))
    r = r + check("path eq null-r", not pathEq(a, n))
    r = r + check("path eq null-null", pathEq(n, n))
    r = r + check("path ne diff", a != c)
    r = r + check("path ne null", n != a)
    r = r + check("path a==null", not (a == null))
    r = r + check("path n==null", n == null)
    return r
}

// ---- 开放泛型 T?：静态类型是占位，双非空须经运行期 typeid 派发 ----

func genericEq\<T>(a: T?, b: T?): bool { return a == b }
func genericNe\<T>(a: T?, b: T?): bool { return a != b }

func genericCases\<T>(label: String, a: T?, same: T?, different: T?): i32 {
    var r = 0
    var n: T? = null
    r = r + check("${label} eq null-null", genericEq\<T>(n, n))
    r = r + check("${label} ne null-null", not genericNe\<T>(n, n))
    r = r + check("${label} eq null-left", not genericEq\<T>(n, a))
    r = r + check("${label} ne null-left", genericNe\<T>(n, a))
    r = r + check("${label} eq null-right", not genericEq\<T>(a, n))
    r = r + check("${label} ne null-right", genericNe\<T>(a, n))
    r = r + check("${label} eq nonnull-same", genericEq\<T>(a, same))
    r = r + check("${label} ne nonnull-same", not genericNe\<T>(a, same))
    r = r + check("${label} eq nonnull-different", not genericEq\<T>(a, different))
    r = r + check("${label} ne nonnull-different", genericNe\<T>(a, different))
    return r
}

func openGenericCases(): i32 {
    var r = 0
    r = r + genericCases\<EqClass>("generic class", new EqClass(7),
        new EqClass(7), new EqClass(8))
    // 分别生成字符串，避免字面量共享掩盖同值但不同身份的问题。
    var sb1 = new StringBuilder()
    var sb2 = new StringBuilder()
    sb1.append("open-generic")
    sb2.append("open-generic")
    r = r + genericCases\<String>("generic string", sb1.toString(),
        sb2.toString(), "different")
    r = r + genericCases\<PlainStruct>("generic struct", new PlainStruct(7),
        new PlainStruct(7), new PlainStruct(8))
    r = r + genericCases\<i32>("generic scalar", 7, 7, 8)
    var never: NeverEqual? = new NeverEqual()
    r = r + check("generic identity eq obeys operator",
        not genericEq\<NeverEqual>(never, never))
    r = r + check("generic identity ne obeys operator",
        genericNe\<NeverEqual>(never, never))
    return r
}

// ---- 缺陷 A：Any? → Any 的 null 解包放行 ----

func anyNullCases(): i32 {
    var r = 0
    var m = new Map\<String, Any>()
    m.set("k1", 42 as Any)
    m.set("k2", "text" as Any)
    var i = 0L
    while (i < m.count) {
        // 非空路径行为不变（MapEnumerator.current 的 as V 同形）
        const v: Any? = m.valueAtIndex(i)
        if (v == null) {
            r = r + check("any iter nonnull", false)
        }
        const a: Any = v as Any
        i = i + 1L
    }
    const miss: Any? = m.tryGet("nope")
    r = r + check("any miss null", miss == null)
    // null 经 as Any 放行且结果为 null（修复前 native 抛 CastException）
    var vn: Any? = null
    try {
        const an: Any = vn as Any
        r = r + check("any null cast result null", an == null)
    } catch (e: core.CastException) {
        r = r + check("any null cast rejected", false)
    }
    return r
}

pub func main(): i32 {
    var fails = 0
    fails = fails + stringCases()
    fails = fails + i32Cases()
    fails = fails + classCases()
    fails = fails + plainStructCases()
    fails = fails + pathCases()
    fails = fails + openGenericCases()
    fails = fails + anyNullCases()
    if (fails == 0) { Console.println("nullable-equality-ok") }
    return fails
}
