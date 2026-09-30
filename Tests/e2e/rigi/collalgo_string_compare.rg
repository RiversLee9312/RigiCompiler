// core.collections compare(String, String) 端到端（STDLIB §4.2.3 比较辅助
// + §4.3.2 次序统一契约，施工块 3-3b）：Unicode 标量字典序 = 合法 UTF-8
// 的无符号字节字典序，前缀相同时短串在前。覆盖：基本序（a<b、b>a、相等）、
// 前缀规则（ab<abc、多标量串字节前缀）、空串最小；**标量序 vs UTF-16
// 码元序差异用例**：bmp = U+E000（BMP 私有区，UTF-16 码元 0xE000）vs
// astral = U+10000（补充平面，UTF-16 代理对首码元 0xD800）——UTF-16 序
// 会把补充平面排前（0xD800 < 0xE000），标量序必须 U+E000 排前
//（0xE000 < 0x10000）；sorted(list, compare) 对含补充平面字符的 String
// List 排序符合标量序（源 List 不受影响）；多标量串首标量字节即定序。
// expect-output: basic-ok
// expect-output: prefix-ok
// expect-output: empty-ok
// expect-output: scalar-order-ok
// expect-output: sorted lens: 1 1 3 4
// expect-output: sorted-ok
// expect-exit: 0
import core.io.Console
import core.collections.*

var step: i32 = 0
func require(value: bool) {
    step = (step + 1)
    if (value == false) { throw new core.RuntimeException("collalgo_string_compare 断言失败 " + step.toString()) }
}

pub func main(): i32 {
    // ── 基本序：内建 u8 序逐字节传递到标量序 ──
    require(compare("a", "b") is .LesserThanAnother)
    require(compare("b", "a") is .GreaterThanAnother)
    require(compare("a", "a") is .Equal)
    require(compare("ab", "ab") is .Equal)
    // 与 == 一致：相等串判 Equal（UTF-8 编码唯一，字节序相等即串相等）
    require((compare("中", "中") is .Equal) and ("中" == "中"))
    Console.println("basic-ok")

    // ── 前缀规则：公共前缀逐字节相等时短串在前（短者判 Lesser）──
    require(compare("ab", "abc") is .LesserThanAnother)
    require(compare("abc", "ab") is .GreaterThanAnother)
    // 多标量串同样按字节前缀裁决（中的 UTF-8 E4 B8 AD 是 中A 的前缀）
    require(compare("中", "中A") is .LesserThanAnother)
    Console.println("prefix-ok")

    // ── 空串：零公共字节直达长度裁决，与任何非空串比都最小 ──
    require(compare("", "") is .Equal)
    require(compare("", "a") is .LesserThanAnother)
    require(compare("a", "") is .GreaterThanAnother)
    Console.println("empty-ok")

    // ── 标量序 vs UTF-16 码元序差异用例（§4.3.2 的关键分歧面）──
    // bmp = U+E000（BMP 私有区，UTF-8 三字节 EE 80 80）
    // astral = U+10000（补充平面，UTF-8 四字节 F0 90 80 80）
    // UTF-16 码元序：0xD800 < 0xE000 → astral 排前（错误序）；
    // 标量序契约：0xE000 < 0x10000 → bmp 排前（本断言）
    const bmp = ""
    const astral = "𐀀"
    require((bmp.length == (3 as i64)) and (astral.length == (4 as i64)))
    require(compare(bmp, astral) is .LesserThanAnother)
    require(compare(astral, bmp) is .GreaterThanAnother)
    require(compare(bmp, bmp) is .Equal)
    // 首标量即定序：astral+"b" 与 bmp 首字节已分高下（F0 > EE）
    require(compare("𐀀b", bmp) is .GreaterThanAnother)
    Console.println("scalar-order-ok")

    // ── sorted 联用：含补充平面字符的 String List 按标量序排列 ──
    // 标量序期望：a(0x61) < b(0x62) < U+E000(0xE000) < U+10000(0x10000)
    const words = new List\<String>()
    words.add("b")
    words.add(astral)
    words.add("a")
    words.add(bmp)
    // compare 直接作实参传入：按 Func<ComparisonResult, String, String>
    // 目标类型决议到 compare(String, String) 重载（语言无函数名作值的
    // 形态，经 lambda 转发）
    const cmp = func{(x: String, y: String): core.ComparisonResult -> compare(x, y)}
    const ordered = sorted\<String>(words, cmp)
    require((ordered.length == (4 as i64)))
    require(((ordered.getAtIndex((0 as i64)) as String) == "a"))
    require(((ordered.getAtIndex((1 as i64)) as String) == "b"))
    require(((ordered.getAtIndex((2 as i64)) as String) == bmp))
    require(((ordered.getAtIndex((3 as i64)) as String) == astral))
    // 源 List 保持原序原内容（sorted 返回新 List，不修改输入）
    require(((words.getAtIndex((0 as i64)) as String) == "b"))
    require(((words.getAtIndex((3 as i64)) as String) == bmp))
    // 排序结果的 UTF-8 字节长度序（1 1 3 4）与标量序一致——可观察输出
    //（U+E000 是私有区不可见字符，不直打内容，以字节长度区分元素）
    var lens = "sorted lens:"
    var i: i64 = (0 as i64)
    while (i < ordered.length) {
        lens = (lens + (" " + ((ordered.getAtIndex(i) as String).length.toString())))
        i = (i + (1 as i64))
    }
    Console.println(lens)
    Console.println("sorted-ok")
    return 0
}
