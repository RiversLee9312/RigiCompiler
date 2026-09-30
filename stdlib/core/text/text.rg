// Rigi 标准库：core.text 字符串核心操作（STDLIB §4.3.1/§4.3.2 查找段/
// §4.3.3，施工块 3-2）。
//
// 覆盖（公共行为以 docs/STDLIB/04-text.md 为契约）：
//   - 位置与切片（§4.3.1）：slice / sliceCharacters / characterAt /
//     characters / toUtf8Span——位置为零基 i64 UTF-8 字节偏移（与
//     String.length 一致）；按标量操作使用明确的 *Characters/characterAt；
//     负/越界/求和溢出/切断编码序列抛 core.OutOfBoundException。
//   - 查找（§4.3.2 查找段）：indexOf / lastIndexOf / contains /
//     startsWith / endsWith——区分大小写、不自动规范化。
//   - 分割/替换/裁剪/拼接（§4.3.3）：split / replace / replaceFirst /
//     trim / trimStart / trimEnd / join——字面匹配、从左到右不重叠。
//   - Unicode 大小写映射（§4.3.2，施工块 3-3a）：toLower / toUpper——
//     无条件完整映射（固定 Unicode 17.0.0 数据，表在 case_data.rg，
//     双宿主同源；不应用 Final_Sigma 上下文条件与 lt/tr/az 地区条件）。
//
// 边界（本块不做）：StringBuilder 与编解码器（3-4）、parse/tryParse
//（3-5）、TextReader/TextWriter（3-6）、正则、Case Folding、规范化。
//
// 实现底座：两个 priv native 字节原语（C 宿主 rigi_rt/text.c，VM hook
// VmHooks.TextCopyOut/TextFromBytes，双宿主同语义）——String 侧没有
// 字节访问面，全部 API 经「拷出、校验、from_bytes 重建」通道实现。
// 切片保证边界合法，故重建通道的校验正常路径零开销担忧（§4.3.1）。
//
// 说明：String 保持不可变值语义（§4.3.1），全部结果串均为独立重建；
// 按标量随机定位不承诺 O(1)，顺序处理优先遍历（§4.3.1 明文）。
namespace core.text

// ===== 底层字节原语（§4.3.1 UTF-8 导出/重建通道；priv 不对用户可见）=====

// 把 src 的 UTF-8 字节段 [srcOffset, srcOffset+count) 拷入
// dest[destOffset .. destOffset+count)。成功返回 count（0 合法）；
// 源段越界返回 -1、目标段越界返回 -2（约定错误码——包装层换抛
// core.OutOfBoundException）。C 符号 rigi_text_copy_out（rigi_rt/text.c）。
@NativeLibrary("rigi_rt")
@NativeSymbol("text_copy_out")
priv native func rigi_text_copy_out(src: String, srcOffset: i64, dest: Span\<u8>, destOffset: i32, count: i32): i32

// 从 bytes[offset .. offset+count) 严格校验 UTF-8 后重建 String
//（切片结果的重建通道）。C 符号 rigi_text_from_bytes（rigi_rt/text.c）。
@NativeLibrary("rigi_rt")
@NativeSymbol("text_from_bytes")
priv native func rigi_text_from_bytes(bytes: Span\<u8>, offset: i32, count: i32): String

// ===== 内部助手（priv，仅本文件可用）=====

// 单个 Span 的 i32 容量上限（§4.3.1：String 位置保持 i64，单个 Span
// 仍受 i32 容量限制；超出直接结果容量明确失败，大内容由调用者分块
// 处理）——整串字节通道（toUtf8Span/遍历副本）同样受此约束。

// 判断字节是否为 UTF-8 续字节（10xxxxxx）：编译器保证 String 内编码
// 合法，标量边界判定只需区分「标量起始字节」与「续字节」
priv func textIsContinuationByte(b: u8): bool {
    return ((b & (192 as u8)) == (128 as u8))
}

// 读 src 指定字节偏移的单个字节（1 字节拷出通道；越界属调用方校验
// 缺位的防御路径，抛 core.OutOfBoundException）
priv func textByteAt(src: String, offset: i64): u8 {
    const one = core.collections.spanOf\<u8>(1)
    const n = rigi_text_copy_out(src, offset, one, 0, 1)
    if (n < 0) {
        throw new core.OutOfBoundException("text 内部字节读取失败（错误码 ${n}）")
    }
    return (one[0] if? (0 as u8))
}

// Span 指定偏移的单字节读取（界内前提由调用方保证）
priv func textByteAtSpan(bytes: Span\<u8>, offset: i64): u8 {
    return (bytes[(offset as i32)] if? (0 as u8))
}

// UTF-8 首字节 → 序列长度（编译器保证编码合法：1/2/3/4 字节序列）
priv func textUtf8SeqLen(bytes: Span\<u8>, pos: i64): i64 {
    const b: u8 = textByteAtSpan(bytes, pos)
    if (b < (128 as u8)) {
        return (1 as i64)
    }
    if (b < (224 as u8)) {
        return (2 as i64)
    }
    if (b < (240 as u8)) {
        return (3 as i64)
    }
    return (4 as i64)
}

// 从 bytes[pos .. pos+seqLen) 解码单个标量（串内编码合法为前提）
priv func textDecodeScalar(bytes: Span\<u8>, pos: i64, seqLen: i64): char {
    var cp: i64 = (0 as i64)
    var i: i64 = (0 as i64)
    while (i < seqLen) {
        const byte = (textByteAtSpan(bytes, (pos + i)) as i64)
        if (i == (0 as i64)) {
            // 首字节按序列长保留低位（1/2/3/4 字节分别 7/5/4/3 位）
            if (seqLen == (1 as i64)) {
                cp = byte
            } else if (seqLen == (2 as i64)) {
                cp = (byte & (31 as i64))
            } else if (seqLen == (3 as i64)) {
                cp = (byte & (15 as i64))
            } else {
                cp = (byte & (7 as i64))
            }
        } else {
            cp = ((cp << (6 as i64)) | (byte & (63 as i64)))
        }
        i = (i + (1 as i64))
    }
    return (cp as char)
}

// ===== §4.3.1 位置与切片 =====

// 切片核心：校验并重建 [start, start+count) 字节段（零基 i64 UTF-8
// 字节偏移，与 String.length 一致）。负参数/越界/求和溢出/切断编码
// 序列均抛 core.OutOfBoundException，不自动调整范围或边界（§4.3.1）；
// 合法边界上的零长度返回空串，末尾 length 是合法边界。
priv ext func String.sliceBytesChecked(start: i64, count: i64): String {
    // 范围校验：count > length - start 同时涵盖求和溢出——start ≥ 0
    //（已查）时 start+count 无回绕空间，越界必然落入本式
    if (start < (0 as i64)) {
        throw new core.OutOfBoundException("slice 起始偏移为负：start=${start}")
    }
    if (count < (0 as i64)) {
        throw new core.OutOfBoundException("slice 长度为负：count=${count}")
    }
    if (count > (this.length - start)) {
        throw new core.OutOfBoundException("slice 范围越界：start=${start} count=${count} length=${this.length}")
    }
    if (count > (2147483647 as i64)) {
        throw new core.OutOfBoundException("slice 结果超出单个 Span 的 i32 容量：count=${count}")
    }
    // 边界校验：start 与 end 必须落在标量起始字节（0 与 length 恒为
    // 合法边界；落在续字节上即切断编码序列）
    if ((start > (0 as i64)) and (start < this.length)) {
        if (textIsContinuationByte(textByteAt(this, start))) {
            throw new core.OutOfBoundException("slice 起始位置切断编码序列：start=${start}")
        }
    }
    const end = (start + count)
    if (end < this.length) {
        if (textIsContinuationByte(textByteAt(this, end))) {
            throw new core.OutOfBoundException("slice 结束位置切断编码序列：end=${end}")
        }
    }
    // 重建（§4.3.1 切片重建通道）：拷出段字节后经 text_from_bytes
    // 严格校验重建；段长 ≤ i32.Max 已在上方校验
    const seg = core.collections.spanOf\<u8>((count as i32))
    const n = rigi_text_copy_out(this, start, seg, 0, (count as i32))
    if (n < 0) {
        throw new core.OutOfBoundException("text 内部拷出失败（错误码 ${n}）")
    }
    return rigi_text_from_bytes(seg, 0, (count as i32))
}

// 单次扫描求标量区间 [start, start+count) 的字节偏移区间
// [b0, b1)（§4.3.1：按标量随机定位不承诺 O(1)，顺序处理优先）。
// start+count ≤ characterCount 已由调用方校验。
priv ext func String.scalarRangeToBytes(start: i64, count: i64): core.Pair\<i64, i64> {
    const len = this.length
    const all = this.toUtf8Span()
    var b0: i64 = (-1 as i64)
    var b1: i64 = (-1 as i64)
    var scalarIdx: i64 = (0 as i64)
    var pos: i64 = (0 as i64)
    while (pos < len) {
        if (scalarIdx == start) {
            b0 = pos
        }
        if (scalarIdx == (start + count)) {
            b1 = pos
        }
        scalarIdx = (scalarIdx + (1 as i64))
        pos = (pos + textUtf8SeqLen(all, pos))
    }
    // start / start+count 恰为 characterCount（末尾边界）时在扫描后落点
    if (scalarIdx == start) {
        b0 = len
    }
    if (scalarIdx == (start + count)) {
        b1 = len
    }
    if ((b0 < (0 as i64)) or (b1 < (0 as i64))) {
        throw new core.OutOfBoundException("sliceCharacters 标量索引越界：start=${start} count=${count} characterCount=${this.characterCount}")
    }
    return new core.Pair\<i64, i64>(b0, b1)
}

// slice(start, count)：返回字节范围 [start, start+count) 的 String
//（§4.3.1）
pub ext func String.slice(start: i64, count: i64): String {
    return this.sliceBytesChecked(start, count)
}

// slice(start)：取到末尾（§4.3.1）
pub ext func String.slice(start: i64): String {
    return this.sliceBytesChecked(start, (this.length - start))
}

// sliceCharacters(start, count)：按标量计数切片（§4.3.1），同规则
// 抛 core.OutOfBoundException
pub ext func String.sliceCharacters(start: i64, count: i64): String {
    if (start < (0 as i64)) {
        throw new core.OutOfBoundException("sliceCharacters 起始标量索引为负：start=${start}")
    }
    if (count < (0 as i64)) {
        throw new core.OutOfBoundException("sliceCharacters 标量数为负：count=${count}")
    }
    if (count > (this.characterCount - start)) {
        throw new core.OutOfBoundException("sliceCharacters 标量范围越界：start=${start} count=${count} characterCount=${this.characterCount}")
    }
    const range = this.scalarRangeToBytes(start, count)
    // 字节区间 [b0, b1) 天然标量对齐，经切片核心统一校验重建
    return this.sliceBytesChecked(range.key, (range.value - range.key))
}

// characterAt(index)：按标量索引取字符，越界返回 null（§4.3.1）
pub ext func String.characterAt(index: i64): char? {
    if ((index < (0 as i64)) or (index >= this.characterCount)) {
        return null
    }
    const range = this.scalarRangeToBytes(index, (1 as i64))
    const seqLen = (range.value - range.key)
    const seg = core.collections.spanOf\<u8>((seqLen as i32))
    const n = rigi_text_copy_out(this, range.key, seg, 0, (seqLen as i32))
    if (n < 0) {
        throw new core.OutOfBoundException("text 内部拷出失败（错误码 ${n}）")
    }
    return textDecodeScalar(seg, (0 as i64), seqLen)
}

// characters()：标量遍历（§4.3.1）——使用既有 IEnumerable/IEnumerator
// 协议，每次遍历有独立游标
pub ext func String.characters(): core.collections.IEnumerable\<char> {
    return new StringCharacterEnumerable(this)
}

// 标量可枚举体：仅持有源串引用，每次 iterate() 产生独立枚举器
priv class StringCharacterEnumerable implements core.collections.IEnumerable\<char> {
    priv const source: String

    pub init(_ -> source) {}

    pub override func iterate(): core.collections.IEnumerator\<char> {
        return new StringCharacterEnumerator(source)
    }
}

// 标量枚举器：每个枚举器持有整串 UTF-8 字节的独立副本与自己的字节
// 游标（独立游标 + 串不可变 ⇒ 互不干扰；顺序解码，无 O(1) 随机定位）
priv class StringCharacterEnumerator implements core.collections.IEnumerator\<char> {
    priv const bytes: Span\<u8>
    priv var pos: i64
    priv var started: bool
    priv var finished: bool
    priv var current_: char

    pub init(source: String) {
        bytes = source.toUtf8Span()
        pos = (0 as i64)
        started = false
        finished = false
        // DA：current_ 语义上由 moveNext 首步覆写，此处先赋哨兵
        current_ = (0 as char)
    }

    pub override func moveNext(): bool {
        if (finished) {
            return false
        }
        if (pos >= (bytes.length as i64)) {
            finished = true
            return false
        }
        const seqLen = textUtf8SeqLen(bytes, pos)
        current_ = textDecodeScalar(bytes, pos, seqLen)
        pos = (pos + seqLen)
        started = true
        return true
    }

    pub override func current(): char {
        if ((not started) or finished) {
            throw new core.NoSuchElementException("String 标量枚举器无当前元素（未开始或已结束）")
        }
        return current_
    }
}

// toUtf8Span()：UTF-8 导出（§4.3.1）——返回底层字节的独立副本，修改
// 缓冲区不影响原 String；从字节重建 String 必须经解码。单个 Span 受
// i32 容量限制，超出明确失败不窄化（§4.3.1）
pub ext func String.toUtf8Span(): Span\<u8> {
    const len = this.length
    if (len > (2147483647 as i64)) {
        throw new core.OutOfBoundException("String 长度超出单个 Span 的 i32 容量：${len}")
    }
    const all = core.collections.spanOf\<u8>((len as i32))
    const n = rigi_text_copy_out(this, (0 as i64), all, 0, (len as i32))
    if (n < 0) {
        throw new core.OutOfBoundException("text 内部拷出失败（错误码 ${n}）")
    }
    return all
}

// ===== 异常（§4.3.5 失败与格式边界：空分隔串等文本方法参数错误用普通
// core.text.TextArgumentException；切片范围与边界错误按 §4.3.1 用
// core.OutOfBoundException——沿用现有单异常传播模型）=====

pub open class TextArgumentException : core.RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

// ===== §4.3.2 查找 =====
// 位置为零基 i64 UTF-8 字节偏移；返回 i64?，未找到返回 null。匹配
// 区分大小写、不自动进行 Unicode 规范化（§4.3.2）；合法 UTF-8 下续
// 字节不与任何标量起始字节相等，字节级匹配点天然落在标量边界上。

// 前向查找首个匹配字节偏移（朴素匹配）；空 needle 在 from 处匹配
priv func textFindFrom(h: Span\<u8>, n: Span\<u8>, from: i64): i64? {
    const hn: i64 = (h.length as i64)
    const nn: i64 = (n.length as i64)
    var p: i64 = from
    while (p <= (hn - nn)) {
        if (textBytesEqual(h, p, n, (0 as i64), nn)) {
            const found: i64? = p
            return found
        }
        p = (p + (1 as i64))
    }
    return null
}

// 全串反向查找（§4.3.2：lastIndexOf 首版对整个字符串反向查找）；
// 空 needle 返回 length
priv func textFindLast(h: Span\<u8>, n: Span\<u8>): i64? {
    const hn: i64 = (h.length as i64)
    const nn: i64 = (n.length as i64)
    var p: i64 = (hn - nn)
    while (p >= (0 as i64)) {
        if (textBytesEqual(h, p, n, (0 as i64), nn)) {
            const found: i64? = p
            return found
        }
        p = (p - (1 as i64))
    }
    return null
}

// 字节段等值比较（界内前提由调用方保证）
priv func textBytesEqual(a: Span\<u8>, aOff: i64, b: Span\<u8>, bOff: i64, count: i64): bool {
    var i: i64 = (0 as i64)
    while (i < count) {
        if (textByteAtSpan(a, (aOff + i)) != textByteAtSpan(b, (bOff + i))) {
            return false
        }
        i = (i + (1 as i64))
    }
    return true
}

// indexOf(needle)：省略 start 时从 0 开始（§4.3.2）
pub ext func String.indexOf(needle: String): i64? {
    const h = this.toUtf8Span()
    const n = needle.toUtf8Span()
    return textFindFrom(h, n, (0 as i64))
}

// indexOf(needle, start)：从指定合法字节边界向后查找；非法 start
//（负值/越界/切断编码序列）仍报范围错误（§4.3.2）
pub ext func String.indexOf(needle: String, start: i64): i64? {
    if ((start < (0 as i64)) or (start > this.length)) {
        throw new core.OutOfBoundException("indexOf 起始偏移越界：start=${start} length=${this.length}")
    }
    if ((start > (0 as i64)) and (start < this.length)) {
        if (textIsContinuationByte(textByteAt(this, start))) {
            throw new core.OutOfBoundException("indexOf 起始位置切断编码序列：start=${start}")
        }
    }
    const h = this.toUtf8Span()
    const n = needle.toUtf8Span()
    return textFindFrom(h, n, start)
}

// lastIndexOf(needle)：全串反向查找，未找到返回 null（§4.3.2）
pub ext func String.lastIndexOf(needle: String): i64? {
    const h = this.toUtf8Span()
    const n = needle.toUtf8Span()
    return textFindLast(h, n)
}

// contains(needle)：是否包含（空 needle 恒 true，§4.3.2）
pub ext func String.contains(needle: String): bool {
    const hit = this.indexOf(needle)
    if (hit == null) {
        return false
    }
    return true
}

// startsWith(prefix)：前缀判断；空前缀恒 true（§4.3.2）
pub ext func String.startsWith(prefix: String): bool {
    const pl = prefix.length
    if (pl > this.length) {
        return false
    }
    if (pl == (0 as i64)) {
        return true
    }
    const h = this.toUtf8Span()
    const n = prefix.toUtf8Span()
    return textBytesEqual(h, (0 as i64), n, (0 as i64), pl)
}

// endsWith(suffix)：后缀判断；空后缀恒 true（§4.3.2）
pub ext func String.endsWith(suffix: String): bool {
    const sl = suffix.length
    if (sl > this.length) {
        return false
    }
    if (sl == (0 as i64)) {
        return true
    }
    const h = this.toUtf8Span()
    const n = suffix.toUtf8Span()
    return textBytesEqual(h, (this.length - sl), n, (0 as i64), sl)
}

// ===== §4.3.3 分割/替换/裁剪/拼接 =====

// split(separator)：按字面分隔串从左到右、不重叠地匹配，立即返回；
// 保留开头/中间/末尾空项，空输入得到单个空串（§4.3.3）
pub ext func String.split(separator: String): Array\<String> {
    return this.splitBounded(separator, (-1 as i64))
}

// split(separator, maxParts)：正数上限，达到上限后剩余文本作为最后
// 一项，1 表示不分割；maxParts ≤ 0 抛参数错误（§4.3.3）
pub ext func String.split(separator: String, maxParts: i32): Array\<String> {
    if (maxParts <= 0) {
        throw new TextArgumentException("split maxParts 必须为正数：${maxParts}")
    }
    return this.splitBounded(separator, (maxParts as i64))
}

// 分割核心：maxParts < 0 = 不设上限（默认只受结果容器容量限制，
// §4.3.3）。两遍法：先数不重叠匹配数定容量，再切段收集。
priv ext func String.splitBounded(separator: String, maxParts: i64): Array\<String> {
    if (separator.length == (0 as i64)) {
        throw new TextArgumentException("split 分隔串不能为空")
    }
    const h = this.toUtf8Span()
    const n = separator.toUtf8Span()
    const matches = textCountMatches(h, n)
    var limit: i64 = (matches + (1 as i64))
    if ((maxParts >= (0 as i64)) and (maxParts < limit)) {
        limit = maxParts
    }
    const parts = core.collections.arrayOf\<String>((limit as i32))
    var idx: i32 = 0
    var segStart: i64 = (0 as i64)
    var made: i64 = (0 as i64)
    while (made < (limit - (1 as i64))) {
        const hit: i64? = textFindFrom(h, n, segStart)
        if (hit == null) {
            break
        }
        const at: i64 = (hit if? (0 as i64))
        parts[idx] = this.sliceBytesChecked(segStart, (at - segStart))
        idx = (idx + 1)
        made = (made + (1 as i64))
        segStart = (at + (n.length as i64))
    }
    // 最后一段：达到上限时装剩余全文（§4.3.3），否则装到串尾
    parts[idx] = this.sliceBytesChecked(segStart, (this.length - segStart))
    return parts
}

// 统计不重叠匹配总数（从左到右；分隔串非空由 splitBounded 保证）
priv func textCountMatches(h: Span\<u8>, n: Span\<u8>): i64 {
    var count: i64 = (0 as i64)
    var p: i64 = (0 as i64)
    const nn: i64 = (n.length as i64)
    var scanning: bool = true
    while (scanning) {
        const hit: i64? = textFindFrom(h, n, p)
        if (hit == null) {
            scanning = false
        } else {
            count = (count + (1 as i64))
            p = (hit + nn)
        }
    }
    return count
}

// replace(old, replacement)：替换全部从左到右的不重叠匹配；
// replacement 按字面插入、可以为空、新插入文本不参与本次匹配；
// 无匹配时结果内容等于原串；空 old 报参数错误（§4.3.3）
pub ext func String.replace(old: String, replacement: String): String {
    return this.replaceCore(old, replacement, false)
}

// replaceFirst(old, replacement)：只替换首个匹配（§4.3.3）
pub ext func String.replaceFirst(old: String, replacement: String): String {
    return this.replaceCore(old, replacement, true)
}

// 替换核心：匹配在源串字节上继续（replacement 不参与本次匹配），
// 结果经内容拼接重建（保持 String 不可变值语义，§4.3.3）
priv ext func String.replaceCore(old: String, replacement: String, firstOnly: bool): String {
    if (old.length == (0 as i64)) {
        throw new TextArgumentException("replace 匹配串不能为空")
    }
    const h = this.toUtf8Span()
    const n = old.toUtf8Span()
    var acc = ""
    var segStart: i64 = (0 as i64)
    var scanning: bool = true
    while (scanning) {
        const hit: i64? = textFindFrom(h, n, segStart)
        if (hit == null) {
            scanning = false
        } else {
            const at: i64 = hit // null 守卫后 hit 已智能收窄为 i64（S9a）
            acc = "${acc}${this.sliceBytesChecked(segStart, (at - segStart))}${replacement}"
            segStart = (at + (n.length as i64))
            if (firstOnly) {
                scanning = false
            }
        }
    }
    // 尾段（无匹配时即整串——结果内容等于原串，§4.3.3）
    acc = "${acc}${this.sliceBytesChecked(segStart, (this.length - segStart))}"
    return acc
}

// Unicode 17.0.0 PropList.txt 的 White_Space=Yes 固定集合（§4.3.2：
// 大小写映射与空白属性统一固定使用 Unicode 17.0.0 数据，随库发布、
// 不依赖宿主 Unicode 版本）。含 NBSP(U+00A0) 与全角空格(U+3000)，
// 不含 U+200B 与 U+FEFF，不把所有不可见字符都当作空白。
priv func textIsWhiteSpaceScalar(cp: i64): bool {
    if ((cp >= (9 as i64)) and (cp <= (13 as i64))) {
        return true // U+0009..U+000D：TAB LF VT FF CR
    }
    if (cp == (32 as i64)) { return true } // U+0020 SPACE
    if (cp == (133 as i64)) { return true } // U+0085 NEL
    if (cp == (160 as i64)) { return true } // U+00A0 NBSP
    if (cp == (5760 as i64)) { return true } // U+1680 OGHAM SPACE MARK
    if ((cp >= (8192 as i64)) and (cp <= (8202 as i64))) {
        return true // U+2000..U+200A EN QUAD..HAIR SPACE
    }
    if (cp == (8232 as i64)) { return true } // U+2028 LINE SEPARATOR
    if (cp == (8233 as i64)) { return true } // U+2029 PARAGRAPH SEPARATOR
    if (cp == (8239 as i64)) { return true } // U+202F NNBSP
    if (cp == (8287 as i64)) { return true } // U+205F MMSP
    if (cp == (12288 as i64)) { return true } // U+3000 IDEOGRAPHIC SPACE
    return false
}

// trim()：删除两端 Unicode White_Space 标量，不删除内部空白（§4.3.3）
pub ext func String.trim(): String {
    const head = this.trimStart()
    return head.trimEnd()
}

// trimStart()：删除起始端 White_Space 标量（§4.3.3）
pub ext func String.trimStart(): String {
    const h = this.toUtf8Span()
    var pos: i64 = (0 as i64)
    while (pos < this.length) {
        const seqLen = textUtf8SeqLen(h, pos)
        const cp: i64 = (textDecodeScalar(h, pos, seqLen) as i64)
        if (not textIsWhiteSpaceScalar(cp)) {
            break
        }
        pos = (pos + seqLen)
    }
    return this.sliceBytesChecked(pos, (this.length - pos))
}

// trimEnd()：删除结束端 White_Space 标量（§4.3.3）
pub ext func String.trimEnd(): String {
    const h = this.toUtf8Span()
    var end: i64 = this.length
    while (end > (0 as i64)) {
        // 回退到当前标量的首字节（跳过续字节；合法 UTF-8 下不会越过 0）
        var lead: i64 = (end - (1 as i64))
        while (textIsContinuationByte(textByteAtSpan(h, lead))) {
            lead = (lead - (1 as i64))
        }
        const seqLen = textUtf8SeqLen(h, lead)
        const cp: i64 = (textDecodeScalar(h, lead, seqLen) as i64)
        if (not textIsWhiteSpaceScalar(cp)) {
            break
        }
        end = lead
    }
    return this.sliceBytesChecked((0 as i64), end)
}

// join(parts)（§4.3.3）：以接收者串为分隔符拼接 parts——保留空元素，
// 空序列得到空串；只遍历一次，枚举器异常照常传播，null 不当空串
pub ext func String.join(parts: core.collections.IEnumerable\<String>): String {
    var acc = ""
    const it = parts.iterate()
    var first = true
    while (it.moveNext()) {
        if (first) {
            first = false
        } else {
            acc = "${acc}${this}"
        }
        acc = "${acc}${it.current()}"
    }
    return acc
}

// ===== §4.3.2 Unicode 大小写映射（施工块 3-3a；数据表见 case_data.rg）=====

// toLower()：无条件完整小写映射（§4.3.2）。固定 Unicode 17.0.0 数据
//（随库发布，与 OS/宿主 Unicode 版本无关），逐标量查表拼接结果；映射
// 与周围字符、操作系统、系统语言和进程 locale 无关——不应用 Final_Sigma
// 等上下文条件（"ΟΣ" 与 "Σ" 的词尾 Σ 均映射为 σ 而非 ς）与 lt/tr/az
// 地区条件（"I" 恒映射为 "i"）。不自动归一化输出，不承诺先大写再小写
// 可以还原原字符串；没有映射的标量保持不变；允许一对多结果（返回
// String，不截断）。
pub ext func String.toLower(): String {
    return this.caseMapAll(false)
}

// toUpper()：无条件完整大写映射（§4.3.2），语义与 toLower 同口径
//（"straße" 的 ß 映射为两个标量 SS）
pub ext func String.toUpper(): String {
    return this.caseMapAll(true)
}

// 映射核心：单遍顺序扫描 UTF-8 标量，逐标量查 case_data.rg 的表
//（internal 入口 caseMapCount/caseMapTarget，Rigi 层完成映射——VM 与
// native 双宿主同源，无 C 侧副本），结果串经插值重建（String 不可变值
// 语义，与 §4.3.3 replace 拼接同口径）。查表未命中 = 无映射，标量保持
// 不变（§4.3.2）。
priv ext func String.caseMapAll(toUpper: bool): String {
    // 长度溢出守卫（§4.3.2：输出长度溢出明确报错，不截断映射结果）：
    // UCD 无条件映射单标量至多展开 3 标量 × 每标量至多 4 字节 = 12 字节；
    // 预检 length × 12 不溢出 i64（768614336404564650 = i64 上限 / 12），
    // 此后逐标量累计每步增量 ≤ 12，永不回绕
    if (this.length > (768614336404564650L as i64)) {
        throw new core.OutOfBoundException("toLower/toUpper 输出长度溢出：length=${this.length}")
    }
    const all = this.toUtf8Span()
    var acc = ""
    var pos: i64 = (0 as i64)
    while (pos < (all.length as i64)) {
        const seqLen = textUtf8SeqLen(all, pos)
        const cp: i64 = (textDecodeScalar(all, pos, seqLen) as i64)
        const n = caseMapCount(toUpper, cp)
        if (n == (0 as i64)) {
            // 无映射：原样保持（合法 UTF-8 内解码-重编码字节恒等，§4.3.1
            // 编译器保证串内编码合法）
            const ch: char = (cp as char)
            acc = "${acc}${ch}"
        } else {
            var i: i64 = (0 as i64)
            while (i < n) {
                const tc: char = (caseMapTarget(toUpper, cp, i) as char)
                acc = "${acc}${tc}"
                i = (i + (1 as i64))
            }
        }
        pos = (pos + seqLen)
    }
    return acc
}
