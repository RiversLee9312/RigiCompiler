// Rigi 标准库：core.text StringBuilder（STDLIB §4.3.4，施工块 3-4）。
//
// 覆盖（公共行为以 docs/STDLIB/04-text.md §4.3.4 为契约）：
//   - append(String) / append(char) / clear() / length / toString()；
//     length 是 i64 UTF-8 字节数（与 String.length 同口径），只读属性；
//     其他类型由调用者先调用既有 toString 再 append。
//
// 语义裁决（§4.3.4 明文）：
//   - 构造过程避免反复复制已累计内容：分块持有（每次 append 只挂一个
//     段引用，O(1)），toString 时一次性拼装——累计内容在构造期零复制。
//   - toString 生成独立结果但不清空 builder；后续 append 或 clear 不
//     改变已有结果（String 不可变值语义，§4.3.1）。
//   - local 类型（非 shared）：不支持同实例并发使用。
//   - 不因管理内存而要求 IDisposable；首版不提供随机插入、删除或格式
//     模板语言。
//   - 容量与结果长度溢出明确抛 core.OutOfBoundException，不截断内容
//     （累计总长溢出 i64、结果超出单个 Span 的 i32 容量两道守卫；
//     §4.3.1「单个 Span 仍受 i32 容量限制，超出明确失败」）。
//
// 实现底座：触达 text.rg 同款两个字节原语（String→字节拷出、字节→
// String 严格重建，rigi_rt/text.c；@NativeSymbol 必带防符号拼接落空）。
// 结果串经重建原语独立生成，天然满足「后续 append/clear 不改变已有
// 结果」。
namespace core.text

// String 的 UTF-8 字节段拷出（C 符号 text_copy_out，rigi_rt/text.c）。
// 与 core/text/text.rg 触达同一 native 符号的本文件私有声明：Rigi 级
// 函数名加 builder 中缀避免同命名空间同名（native 不可重载），VM 与
// native 两侧均按 @NativeSymbol 连接，不受本层命名影响
@NativeLibrary("rigi_rt")
@NativeSymbol("text_copy_out")
priv native func rigi_text_builder_copy_out(src: String, srcOffset: i64, dest: Span\<u8>, destOffset: i32, count: i32): i32

// 从字节段严格校验 UTF-8 后重建 String（C 符号 text_from_bytes，
// rigi_rt/text.c；本块拼接结果字节全部来自合法 String，重建零失败）
@NativeLibrary("rigi_rt")
@NativeSymbol("text_from_bytes")
priv native func rigi_text_builder_from_bytes(bytes: Span\<u8>, offset: i32, count: i32): String

// StringBuilder：String/char 追加与一次性拼装（§4.3.4）。local 类型——
// 不支持同实例并发；跨协程传递实例由调用者自行保证互斥。
pub class StringBuilder {
    // 分块持有已追加内容：每段是一个不可变 String 引用，append 不复制
    // 段内容、更不复制累计内容（§4.3.4 避免反复复制）
    priv var chunks: core.collections.List\<String>
    // 累计 UTF-8 字节数（length 属性直读；i64）
    priv var byteLength: i64

    pub init() {
        chunks = new core.collections.List\<String>()
        byteLength = (0 as i64)
    }

    // append(text)：追加一个 String 段（§4.3.4）。累计长度溢出 i64 前
    // 明确报错，不截断不回绕（§4.3.4 容量与结果长度溢出明确报错）
    pub func append(text: String) {
        // i64 上限减法恒不溢出（byteLength ≥ 0）；i64.Max 字面量取
        // text.rg caseMapAll 同款 L 后缀形态
        if (text.length > ((9223372036854775807L as i64) - byteLength)) {
            throw new core.OutOfBoundException("StringBuilder 累计长度溢出：length=${byteLength} + ${text.length}")
        }
        chunks.add(text)
        byteLength = (byteLength + text.length)
    }

    // append(ch)：追加单个 char（§4.3.4）。char 是 32 位标量（§4.3.1），
    // 经插值转为 1–4 字节 UTF-8 段后走 String 通道
    pub func append(ch: char) {
        append("${ch}")
    }

    // clear()：清空已累计内容，builder 可继续使用；容量保留不缩水
    //（List.clear 语义）；length 回零（§4.3.4）
    pub func clear() {
        chunks.clear()
        byteLength = (0 as i64)
    }

    // length：已累计内容的 UTF-8 字节数（i64，只读属性；与 String.length
    // 同口径，§4.3.4）
    pub var length: i64 {
        pub get(_: _) { return byteLength }
    }

    // toString()：拼装独立结果串，不清空 builder——后续 append 或 clear
    // 不改变已有结果（§4.3.4）。单遍拼装：拷出各段字节到结果缓冲后经
    // 严格重建通道生成新 String（累计内容仅在此时复制一次）
    pub override func toString(): String {
        // 结果超出单个 Span 的 i32 容量明确报错，不截断（§4.3.1/§4.3.4）
        if (byteLength > (2147483647 as i64)) {
            throw new core.OutOfBoundException("StringBuilder 结果超出单个 Span 的 i32 容量：${byteLength}")
        }
        const total: i32 = (byteLength as i32)
        if (total == 0) {
            return ""
        }
        const buf = core.collections.spanOf\<u8>(total)
        var off: i32 = 0
        var i: i64 = (0 as i64)
        while (i < chunks.length) {
            const chunk = (chunks.getAtIndex(i) if? "")
            const cl: i32 = (chunk.length as i32)
            if (cl > 0) {
                const n = rigi_text_builder_copy_out(chunk, (0 as i64), buf, off, cl)
                if (n < 0) {
                    throw new core.OutOfBoundException("StringBuilder 内部拷出失败（错误码 ${n}）")
                }
                off = (off + cl)
            }
            i = (i + (1 as i64))
        }
        return rigi_text_builder_from_bytes(buf, 0, total)
    }
}
