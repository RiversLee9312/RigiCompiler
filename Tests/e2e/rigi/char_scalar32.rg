// char 32 位 Unicode 标量端到端（STDLIB §4.3.1 契约）：补充平面字面量、
// 整数→char 转换值域检查、比较运算、Span 读写、char_to_string 输出、
// @Serializable 序列化往返与 String UTF-8 长度语义对照
// expect-output: emoji scalar: 128512
// expect-output: han scalar: 20013
// expect-output: a char: A
// expect-output: lt: true
// expect-output: sp0: 128512
// expect-output: sp eq: true
// expect-output: oob caught
// expect-output: surrogate caught
// expect-output: neg caught
// expect-output: max: 1114111 zero: 0
// expect-output: copy eq: true
// expect-output: back eq: true
// expect-output: bmp eq: true
// expect-output: emoji str len: 4
// expect-output: emoji str cc: 1
// expect-output: han str len: 3
// expect-exit: 0
import core.io.Console
import core.collections.*
import core.serialization.*

@Serializable()
class CharBox {
    pub var c: char
    pub init(v: char) { c = v }
}

pub func main(): i32 {
    // 补充平面字面量（源码 UTF-8 代理对到达，词法层合成单标量）与 BMP 字面量
    const emoji: char = '😀'
    const han: char = '中'
    Console.println("emoji scalar: ${(emoji as i32)}")
    Console.println("han scalar: ${(han as i32)}")
    // 整数 → char 合法值转换与插值输出（char_to_string 通道）
    const a: char = (65 as char)
    Console.println("a char: ${a}")
    Console.println("lt: ${('A' < 'B')}")
    // Span<char> 读写保值（Span 槽为 Nullable<char>，读出经 if? 拆包）
    var sp = spanOf\<char>(2)
    sp[0] = '😀'
    sp[1] = '中'
    Console.println("sp0: ${(sp[0] as i32)}")
    Console.println("sp eq: ${((sp[0] if? 'x') == '😀')}")
    // 整数 → char 越界（>0x10FFFF）、代理区（0xD800）、负值均拒绝，
    // cast 失败路径抛 core.CastException
    try {
        var bad = (0x110000 as char)
        Console.println("oob NOT caught")
    } catch (e: core.CastException) {
        Console.println("oob caught")
    }
    try {
        var bad2 = (0xD800 as char)
        Console.println("surrogate NOT caught")
    } catch (e: core.CastException) {
        Console.println("surrogate caught")
    }
    try {
        var bad3 = ((-1) as char)
        Console.println("neg NOT caught")
    } catch (e: core.CastException) {
        Console.println("neg caught")
    }
    // 合法边界：U+10FFFF 与 U+0000
    var maxc = (0x10FFFF as char)
    var zero = (0 as char)
    Console.println("max: ${(maxc as i32)} zero: ${(zero as i32)}")
    // char 字段序列化往返：deepCopy 与 toParcel/fromParcel 后值相等
    const box = new CharBox('😀')
    const copy = box:Serializable.deepCopy()
    Console.println("copy eq: ${(copy.c == '😀')}")
    const wire = box:Serializable.toParcel()
    const back = fromParcel\<CharBox>(wire)
    Console.println("back eq: ${(back.c == '😀')}")
    const box2 = new CharBox('中')
    const back2 = fromParcel\<CharBox>(box2:Serializable.toParcel())
    Console.println("bmp eq: ${(back2.c == '中')}")
    // 字符串长度语义对照：'😀' 是单 char，但 String "😀" 是 UTF-8——
    // length 为字节数 4，characterCount 为标量数 1；'中' 为 3 与 1
    Console.println("emoji str len: ${"😀".length}")
    Console.println("emoji str cc: ${"😀".characterCount}")
    Console.println("han str len: ${"中".length}")
    return 0
}
