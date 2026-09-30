import core.collections.*
import core.io.*
import core.serialization.*
import core.text.*
// expect-output: serializer-base-ok
// expect-exit: 0
// 施工块 4-4：Serializer 抽象基类（§4.6.2 两层职责 / D3）语料。
// 用户自定义 ToySerializer : Serializer（契约要求抽象类可由用户继承
// 实现），自定义二进制标签格式：用 Parcel 动态访问 + SB 视图
// （sbKind/sbLength/sbElementAt）遍历写出/读回。
// 覆盖：
//   - 抽象方法可继承实现并可被基类便利方法复用（serialize/deserialize
//     走 ToySerializer 的 read/write）；
//   - 借用语义：传入的 ProbeOutputStream/ProbeInputStream 在调用后
//     未关闭、未 flush（探测计数），失败后亦不关闭不刷新；
//   - serializeToString/deserializeFromString 往返（嵌套对象、
//     List/Map 字段、null 字段）；
//   - null 根值：格式层 write(null)/read 走通，deserialize 遇 null
//     根值抛 SerializationException；
//   - 严格性衔接：字段集合不匹配的 Parcel 表示 / 非 Parcel 根值 →
//     deserialize 内部严格 fromParcel/表示核验抛 SerializationException；
//   - 同一实例顺序复用。
// 基类无 loopedRefEnabled/编码参数为注释级契约（本文件不重开这些
// 选择，不做运行时断言）。
//
// wire 形状注记（b4-4 探测实测，树模式）：Parcel 业务字段里集合字段
// 的统一载荷是 .array<.any> 有序序列——List 为元素序列，Map 为摊平
// 的键值交替序列（§4.6.3 统一键值条目序列），可空标量元素经内部
// NullSentinel 对象占位。格式层只 travers 该表示原样入线、读回按
// 同形状重建（Array<Any> 载荷），集合的声明类型重建属 fromParcel
// 的解码职责（「由合法的集合载荷重建对应集合属于解码」，§4.6.3）。
// 本语料集合不含 null 元素（载荷内 NullSentinel 是 Parcel 内部哨兵，
// 用户格式层不可构造，也不需要在业务语料中复现）。

// ── 业务语料类型 ──

@Serializable
class ToyInner {
    pub var flag: bool
    pub var label: String
    pub init(_ -> flag, _ -> label)
}

@Serializable
class ToyRecord {
    pub var id: i32
    pub var name: String
    pub var note: String?
    pub var scores: List\<i32>
    pub var lookup: Map\<String, i32>
    pub var child: ToyInner
    pub init(_ -> id, _ -> name, _ -> note, _ -> scores, _ -> lookup, _ -> child)
}

func makeRecord(): ToyRecord {
    const scores = new List\<i32>()
    scores.add((5 as i32))
    scores.add((6 as i32))
    const lookup = new Map\<String, i32>()
    lookup.set("x", (9 as i32))
    lookup.set("y", (8 as i32))
    return new ToyRecord((1 as i32), "toy", null, scores, lookup,
        new ToyInner(true, "child"))
}

func checkRecord(back: ToyRecord): i32 {
    if (back.id != (1 as i32)) { return 1 }
    if (back.name != "toy") { return 2 }
    if (back.note != null) { return 3 }
    if (back.scores.length != (2 as i64)) { return 4 }
    if ((back.scores.getAtIndex((0 as i64)) if? (0 as i32)) != (5 as i32)) { return 5 }
    if ((back.scores.getAtIndex((1 as i64)) if? (0 as i32)) != (6 as i32)) { return 6 }
    if (back.lookup.count != (2 as i64)) { return 7 }
    if ((back.lookup.tryGet("x") if? (0 as i32)) != (9 as i32)) { return 8 }
    if ((back.lookup.tryGet("y") if? (0 as i32)) != (8 as i32)) { return 9 }
    if (not back.child.flag) { return 10 }
    if (back.child.label != "child") { return 11 }
    return 0
}

// ── 探测流：借用语义计数 ──

// 探测输出流：字节收进 List\<u8>；write/flush/disposeCore 计数；
// failAtWrite 指定第 N 次 write 抛 core.IOException（1 基，0 不注入）
class ProbeOutputStream : OutputStream {
    pub const sink: List\<u8>
    pub var writeCalls: i32 = 0
    pub var flushCalls: i32 = 0
    pub var failAtWrite: i32 = 0
    pub var disposeRuns: i32 = 0

    pub init() {
        sink = new List\<u8>()
    }

    pub override func write(buffer: Span\<u8>, offset: i32, count: i32) {
        writeCalls = (writeCalls + 1)
        if ((failAtWrite > 0) and (writeCalls == failAtWrite)) {
            throw new core.IOException("探测故障：第 ${writeCalls} 次 write")
        }
        ensureOpen()
        checkRange(buffer, offset, count)
        var i: i32 = 0
        while (i < count) {
            sink.add((buffer[offset + i] as u8))
            i = (i + 1)
        }
    }

    pub override func flush() {
        flushCalls = (flushCalls + 1)
        ensureOpen()
    }

    protected override func disposeCore() {
        disposeRuns = (disposeRuns + 1)
    }
}

// 探测输入流：固定字节快照；read/disposeCore 计数
class ProbeInputStream : InputStream {
    pub var readCalls: i32 = 0
    pub var disposeRuns: i32 = 0
    priv var data: Array\<u8>
    priv var pos: i32

    pub init(source: List\<u8>) {
        data = arrayOf\<u8>((source.length as i32))
        var i: i64 = (0 as i64)
        while (i < source.length) {
            data[(i as i32)] = (source.getAtIndex(i) if? (0 as u8))
            i = (i + (1 as i64))
        }
        pos = 0
    }

    pub override func read(buffer: Span\<u8>, offset: i32, count: i32): i32 {
        readCalls = (readCalls + 1)
        ensureOpen()
        checkRange(buffer, offset, count)
        if (count == 0) {
            return 0
        }
        if (pos >= data.length) {
            return 0
        }
        var want: i32 = count
        if (want > (data.length - pos)) {
            want = (data.length - pos)
        }
        var i: i32 = 0
        while (i < want) {
            buffer[offset + i] = (data[pos + i] as u8)
            i = (i + 1)
        }
        pos = (pos + want)
        return want
    }

    protected override func disposeCore() {
        disposeRuns = (disposeRuns + 1)
    }
}

// ── ToySerializer：用户继承 Serializer 的自定义格式 ──
// wire 标签（单字节）：0=null；1=bool（1 字节 0/1）；2=i32（4 字节
// 小端）；3=String（4 字节 UTF-8 长度 + 字节）；4=Parcel（String
// typeName + 4 字节字段数 + 键值对）；8=集合载荷数组（4 字节元素数 +
// 元素序列；List 为元素序、Map 为摊平键值交替序，与 Parcel 业务表里
// 的统一载荷形状一致）。只识别 SB 表示；受控元数据（..value/..case/
// ..id/..ref/..data）与内部 NullSentinel 不作为业务成员入线（本语料
// 集合无 null 元素，不触发哨兵形态）。

class ToySerializer : Serializer {
    // 写出：null 走标签 0；非 null 按 SB 类别分发（sbKind 类别码
    // 1..17，§4.6.1）。Parcel 业务字段的集合载荷以 Array 形态出现
    // （kind=15），经 sbLength/sbElementAt 原样遍历
    pub override func write(value: Any?, output: OutputStream) {
        if (value == null) {
            output.writeByte((0 as u8))
            return
        }
        encodeNonNull(value, output)
    }

    // null 收窄后路径：独立为非可空 Any 形参——native 后端对可空 Any
    // 槽直接取 typeof 有收窄缺陷（b4-2 实测），同 Parcel.setDynamic
    // 分拆先例
    priv func encodeNonNull(value: Any, output: OutputStream) {
        const kind = sbKind(value)
        if (kind == (1 as i32)) {
            // bool
            output.writeByte((1 as u8))
            if (value as bool) {
                output.writeByte((1 as u8))
            } else {
                output.writeByte((0 as u8))
            }
            return
        }
        if (kind == (7 as i32)) {
            // i32
            output.writeByte((2 as u8))
            writeI32((value as i32), output)
            return
        }
        if (kind == (13 as i32)) {
            // String
            output.writeByte((3 as u8))
            writeString(value as String, output)
            return
        }
        if (kind == (14 as i32)) {
            // Parcel：typeName 原样入线（严格 fromParcel 依此核验目标
            // 类型）；字段经公开动态面遍历（keyAtIndex/valueAtIndex），
            // 集合字段值即统一载荷数组（kind=15）递归走标签 8
            const parcel = value as Parcel
            output.writeByte((4 as u8))
            writeString(parcel.typeName, output)
            const count = parcel.elementCount()
            writeI32((count as i32), output)
            var i: i64 = (0 as i64)
            while (i < count) {
                writeString((parcel.keyAtIndex(i) if? ""), output)
                write(parcel.valueAtIndex(i), output)
                i = (i + (1 as i64))
            }
            return
        }
        if (kind == (15 as i32)) {
            // 集合载荷数组（List 元素序 / Map 摊平键值交替序）
            output.writeByte((8 as u8))
            const count = sbLength(value)
            writeI32((count as i32), output)
            var i: i64 = (0 as i64)
            while (i < count) {
                write(sbElementAt(value, i), output)
                i = (i + (1 as i64))
            }
            return
        }
        throw new core.IllegalStateException(
            "ToySerializer 不支持的 SB 类别：${kind}")
    }

    pub override func read(input: InputStream): Any? {
        const reader = new ToyReader(input.readAll())
        return decodeValue(reader)
    }

    // 读回：按标签递归构造 SB 表示；Parcel 经动态写入面 setDynamic
    // 重建（字段键合法性与 SB 值检查原样生效）；载荷数组重建为
    // Array<Any>（与 wire 表示同形状），集合声明类型重建留给严格
    // fromParcel 的解码
    priv func decodeValue(reader: ToyReader): Any? {
        const tag = reader.readByte()
        if (tag == (0 as u8)) { return null }
        if (tag == (1 as u8)) { return (reader.readByte() != (0 as u8)) }
        if (tag == (2 as u8)) { return reader.readI32() }
        if (tag == (3 as u8)) { return reader.readString() }
        if (tag == (4 as u8)) {
            const typeName = reader.readString()
            const count = reader.readI32()
            const parcel = new Parcel(typeName)
            var i: i32 = 0
            while (i < count) {
                const key = reader.readString()
                const fieldValue = decodeValue(reader)
                if (fieldValue is Array\<Any>) {
                    // 集合载荷数组：动态写入面的 SB 值检查不收 .array<.any>
                    // （.any 非叶子表成员），改走类型化 setElement——与
                    // 合成器落载荷的内部通道同款
                    parcel.setElement\<Array\<Any>>(key, fieldValue as Array\<Any>)
                } else {
                    parcel.setDynamic(key, fieldValue)
                }
                i = (i + 1)
            }
            return parcel
        }
        if (tag == (8 as u8)) {
            const count = reader.readI32()
            const elements = arrayOf\<Any>(count)
            var i: i32 = 0
            while (i < count) {
                const element = decodeValue(reader)
                if (element == null) {
                    throw new core.IllegalStateException(
                        "ToySerializer：集合载荷不支持 null 元素")
                }
                elements[i] = element
                i = (i + 1)
            }
            return elements
        }
        throw new core.IllegalStateException(
            "ToySerializer 格式错误：未知标签 ${tag}")
    }

    priv func writeI32(value: i32, output: OutputStream) {
        // 小端 4 字节；经 i64 中转做移位，规避 i32 移位边界差异
        const w = value as i64
        output.writeByte((w & (255 as i64)) as u8)
        output.writeByte(((w >> (8 as i64)) & (255 as i64)) as u8)
        output.writeByte(((w >> (16 as i64)) & (255 as i64)) as u8)
        output.writeByte(((w >> (24 as i64)) & (255 as i64)) as u8)
    }

    priv func writeString(text: String, output: OutputStream) {
        const bytes = text.toUtf8Span()
        writeI32(bytes.length, output)
        var i: i32 = 0
        while (i < bytes.length) {
            output.writeByte((bytes[i] as u8))
            i = (i + 1)
        }
    }
}

// 字节读游标：整体缓冲 + 位置；越界抛 EndOfStreamException（格式
// 错误的一种）；String 段经严格 Utf8Decoder 解码
class ToyReader {
    priv var bytes: Span\<u8>
    priv var pos: i32

    pub init(_ -> bytes) {
        pos = 0
    }

    pub func readByte(): u8 {
        if (pos >= bytes.length) {
            throw new EndOfStreamException((1 as i32), (0 as i32))
        }
        const b = (bytes[pos] as u8)
        pos = (pos + 1)
        return b
    }

    pub func readI32(): i32 {
        // 小端 4 字节；经 i64 装配后回 i32（移位语义与 writeI32 对称）
        var w: i64 = (0 as i64)
        var k: i64 = (0 as i64)
        while (k < (4 as i64)) {
            const b = (readByte() as i64)
            w = (w | (b << (k * (8 as i64))))
            k = (k + (1 as i64))
        }
        return (w as i32)
    }

    pub func readString(): String {
        const len = readI32()
        const buf = spanOf\<u8>(len)
        var i: i32 = 0
        while (i < len) {
            buf[i] = readByte()
            i = (i + 1)
        }
        const decoder = new Utf8Decoder()
        return decoder.decode(buf, true)
    }
}

pub func main(): i32 {
    const ser = new ToySerializer()
    const rec = makeRecord()

    // ── 1. 对象便利层往返：serialize/deserialize 走 ToySerializer 的
    //    抽象 write/read 实现 ──
    const outStream = new ProbeOutputStream()
    ser.serialize\<ToyRecord>(rec, outStream)
    // 借用语义：成功返回后调用者流未关闭、未被 flush；内容已交出
    if (outStream.disposeRuns != 0) { return 21 }
    if (outStream.flushCalls != 0) { return 22 }
    if (outStream.sink.length == (0 as i64)) { return 23 }
    // 最终刷新时机由调用者决定
    outStream.flush()
    if (outStream.flushCalls != 1) { return 24 }

    const inStream = new ProbeInputStream(outStream.sink)
    const back = ser.deserialize\<ToyRecord>(inStream)
    if (inStream.disposeRuns != 0) { return 25 }
    if (inStream.readCalls == 0) { return 26 }
    const checkedBack = checkRecord(back)
    if (checkedBack != 0) { return (30 + checkedBack) }

    // ── 2. String/内存缓冲区便利入口往返（基类默认 UTF-8 组合实现；
    //    内部临时流由入口清理，与调用者无关）──
    const text = ser.serializeToString\<ToyRecord>(rec)
    if (text.length == (0 as i64)) { return 41 }
    const back2 = ser.deserializeFromString\<ToyRecord>(text)
    const checked2 = checkRecord(back2)
    if (checked2 != 0) { return (50 + checked2) }

    // ── 3. null 根值：格式层 write(null)/read 走通；对象入口报错 ──
    const nullOut = new MemoryOutputStream()
    ser.write(null, nullOut)
    const nullBytes = nullOut.toSpan()
    const nullBack = ser.read(new MemoryInputStream(nullBytes))
    if (nullBack != null) { return 61 }
    var threw = false
    try {
        const ignored = ser.deserialize\<ToyRecord>(new MemoryInputStream(nullBytes))
    } catch (e: SerializationException) {
        threw = true
    }
    if (not threw) { return 62 }

    // ── 4. 严格性衔接（deserialize 内部严格规则生效）──
    // 4a. 字段集合不匹配：合法 typeName 的空字段 Parcel 表示
    const partial = new Parcel(rec:Serializable.toParcel().typeName)
    const partialOut = new MemoryOutputStream()
    ser.write(partial, partialOut)
    threw = false
    try {
        const ignored2 = ser.deserialize\<ToyRecord>(
            new MemoryInputStream(partialOut.toSpan()))
    } catch (e: SerializationException) {
        threw = true
    }
    if (not threw) { return 63 }
    // 4b. 非 Parcel 根值：对象入口表示核验报错
    const scalarOut = new MemoryOutputStream()
    ser.write((42 as i32), scalarOut)
    threw = false
    try {
        const ignored3 = ser.deserialize\<ToyRecord>(
            new MemoryInputStream(scalarOut.toSpan()))
    } catch (e: SerializationException) {
        threw = true
    }
    if (not threw) { return 64 }

    // ── 5. 失败路径借用：格式层 write 抛错后调用者流仍未关闭、未
    //    刷新，处置权归调用者 ──
    const failOut = new ProbeOutputStream()
    failOut.failAtWrite = 1
    threw = false
    try {
        ser.serialize\<ToyRecord>(rec, failOut)
    } catch (e: core.IOException) {
        threw = true
    }
    if (not threw) { return 65 }
    if (failOut.disposeRuns != 0) { return 66 }
    if (failOut.flushCalls != 0) { return 67 }
    failOut.dispose()
    if (failOut.disposeRuns != 1) { return 68 }

    // ── 6. 同一实例顺序复用（每次调用独立解析状态由子类保证）──
    const again = ser.deserializeFromString\<ToyRecord>(
        ser.serializeToString\<ToyRecord>(rec))
    const checkedAgain = checkRecord(again)
    if (checkedAgain != 0) { return (70 + checkedAgain) }

    core.io.Console.println("serializer-base-ok")
    return 0
}
