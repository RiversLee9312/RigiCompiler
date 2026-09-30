// Rigi 标准库：core.serialization.json JsonSerializer 写侧 + 读侧
// （STDLIB §4.7：写侧施工块 5-2a；读侧词法/语法解析 + read 动态 SB
// 读取 + readAs 目标类型引导属施工块 5-2b，均在本文件）。
//
// 覆盖（公共行为以 docs/STDLIB/08-json.md §4.7 为契约）：
//   - JsonSerializer : core.serialization.Serializer（§4.6.2 两层职责；
//     本类实现抽象 write，read 为 5-2b 占位体）；
//   - 类型信息两形式（§4.7.2）：write(value, output) 默认保留类型信息；
//     write(value, output, keepTypeInfo: bool) 重载切换互操作表示
//     （§4.7.4）。此选项属 JSON，不加到 Serializer 基类；
//   - 保留类型信息：Rigi 对象附加 String 成员 .rigi.type-identifier（值 =
//     Parcel.typeName 一致的完整规范类型名，含闭合泛型实参，从类型元
//     信息取得，不调用业务对象 toString）；Serializable 枚举加
//     .rigi.enum-case（case 名），实例字段按普通字段输出；
//   - Map 表示（§4.7.3）：保留类型信息时所有 Map 用外层对象——
//     .rigi.type-identifier 声明完整 Map 类型 + content 键值对数组，
//     条目按插入序、每项恰好 key/value 两成员；String 与非 String 键
//     同形；空 Map content 为 []；
//   - 互操作输出（§4.7.4，keepTypeInfo=false 递归作用于整个输出）：
//     普通对象只输出业务字段；String 键 Map 输出普通 JSON 对象；非
//     String 键 Map 报错（含空 Map 与嵌套）；无载荷枚举输出 case 名
//     字符串；带载荷枚举输出普通对象（成员 case 载 case 名）；枚举
//     载荷字段与 case 成员名冲突时报错；
//   - 数字/字符/容器（§4.7.5）：整数按值输出；浮点输出能恢复原值的
//     最短表示（复用既有 toString 最短往返），保留负零，NaN/正负无穷
//     报错（不替换成 null 或字符串）；char 输出含一个 Unicode 标量的
//     JSON 字符串（支持补充平面）；Array/List 输出数组；bool/null 对应
//     JSON 布尔/空值；
//   - 文本格式/编码（§4.7.6 写侧）：默认紧凑、可配置基础缩进、不附
//     末尾换行；元数据在前、业务字段按 Parcel 迭代序、Map 按插入序；
//     字节输出默认 UTF-8（复用 core.text 编码器），不生成 BOM；字符串
//     转义按 JSON 规范最小转义集（\" \\ \b \f \n \r \t）+ \u00XX 兜底，
//     其余标量原样直出——补充平面经 UTF-8 直出（本实现的选择；与
//     5-2b 读取面约定：读取面同时接受 UTF-8 直出与 \u 代理对，保证
//     往返一致）；
//   - 限制：容器嵌套深度默认上限 256（根对象/数组深度 1，标量根 0），
//     可配置更小上限；每次调用独立写出状态，同实例可顺序复用、不支持
//     并发或重入；
//   - 引用（§4.7.2）：JSON 不支持环形引用——遍历发现当前路径上的真环
//     立即失败；已有显式图模式 Parcel（meta 含 ..id/..ref/..data）即使
//     无环也拒绝；无环重复引用按每条路径展开为独立副本，不保留别名；
//     写入失败可能已输出部分字节，不回滚（本实现先完整构建文本再一次性
//     编码交出：表示/限制错误在未写字节前抛出；底层 I/O 失败的 partial
//     字节不回滚）；
//   - 借用/flush（§4.6.2）：传入流始终借用——不关闭、不主动 flush，
//     内部不包装调用者流。
//
// Parcel 集合字段内部使用 .array<.any> wire（Map 为交替键值槽），
// 它不是 JSON 业务表示。写侧按字段反射声明递归识别集合载荷：Map
// 输出 §4.7.3/§4.7.4 的对象形态；Array/List 仍为数组，其元素也按
// 声明递归处理。带 ..value 元数据的集合 envelope 同样使用其类型名
// 引导写出，不把交替槽序列直接暴露给调用者。
// wire null 哨兵记录（可空集合元素/槽位经 EncodeOptionalElement 的
// 内部 null 表示，Parcel 受控面判别）在写侧还原为 JSON null、读侧
// 按声明可空性造形为同一记录（§4.7.1/§4.7.5）——格式边界不泄漏
// 哨兵文本，默认与互操作两种模式同口径。
namespace core.serialization.json

/**
 * JSON 专用异常（§4.7.6）：语法、表示、数值范围及 JSON 资源限制错误。
 * 读取面（5-2b）错误携带从本次输入起点计的零基字节偏移；写出错误
 * 没有虚构的输入偏移，offset 置 -1。
 */
pub open class JsonException : core.RuntimeException {
    // 错误原因（人类可读描述）。
    pub const reason: String
    // 零基字节偏移；写出侧固定为 -1（无虚构输入偏移）。
    pub const offset: i64

    // 写出侧错误：无输入偏移。
    pub init(reason: String) {
        message = reason
        this.reason = reason
        offset = (0 as i64) - (1 as i64)
    }

    // 读取侧错误：携带输入偏移（5-2b 使用）。
    pub init(reason: String, offset: i64) {
        message = "${reason}（输入偏移 ${offset}）"
        this.reason = reason
        this.offset = offset
    }

    pub override func getMessage(): String { return message }
}

/**
 * JSON 序列化器（§4.7）：Serializer 抽象基类的 JSON 格式实现（写侧 +
 * 读侧）。实例构造选项承载缩进、深度上限、编码与读侧大小限制配置
 * （§4.7.6）。
 */
pub class JsonSerializer : core.serialization.Serializer {
    // 基础缩进文本（每级容器重复一次）；空串 = 紧凑格式（默认）。
    priv const indentText: String
    // 容器嵌套深度上限（根对象/数组深度 1，标量根 0）。
    priv const depthLimit: i32
    // 编码配置（校验并记录；core.text 当前只承载 UTF-8 编解码器，
    // 写出固定经 Utf8Encoder，扩展编码时在此接入，§4.7.6）。
    priv const encodingName: String
    // 读侧限制（§4.7.6：可配置总输入字节数、字符串和数字 token 大小
    // 限制；0 = 不启用该上限，受实际容器容量约束）。
    priv const maxTotalBytes: i64
    priv const maxStringBytes: i64
    priv const maxNumberBytes: i64

    // 构造选项（§4.7.6：配置由构造选项承载）：
    //   indent   —— 基础缩进（默认空串 = 紧凑格式）；
    //   maxDepth —— 容器嵌套深度上限（默认 256，允许更小）；
    //   encoding —— 字节编码名（默认 "utf-8"；core.text 现仅 UTF-8，
    //                其他拼写报错，不静默退回）；
    //   maxTotalBytes  —— 读侧总输入字节数上限（0 = 不启用）；
    //   maxStringBytes —— 读侧字符串 token 字节上限（0 = 不启用）；
    //   maxNumberBytes —— 读侧数字 token 字节上限（0 = 不启用）。
    pub init(indent: String = "", maxDepth: i32 = 256, encoding: String = "utf-8",
            maxTotalBytes: i64 = 0L, maxStringBytes: i64 = 0L,
            maxNumberBytes: i64 = 0L) {
        if (maxDepth < 1) {
            throw new core.IllegalArgumentException(
                "JsonSerializer 深度上限必须 >= 1：${maxDepth}")
        }
        const normalized = encoding.toLower()
        if ((normalized != "utf-8") and (normalized != "utf8")) {
            throw new core.IllegalArgumentException(
                "JsonSerializer 不支持的编码：${encoding}（core.text 当前仅承载 UTF-8）")
        }
        if (maxTotalBytes < (0 as i64)) {
            throw new core.IllegalArgumentException(
                "JsonSerializer 总输入字节上限不能为负：${maxTotalBytes}")
        }
        if (maxStringBytes < (0 as i64)) {
            throw new core.IllegalArgumentException(
                "JsonSerializer 字符串 token 上限不能为负：${maxStringBytes}")
        }
        if (maxNumberBytes < (0 as i64)) {
            throw new core.IllegalArgumentException(
                "JsonSerializer 数字 token 上限不能为负：${maxNumberBytes}")
        }
        indentText = indent
        depthLimit = maxDepth
        encodingName = normalized
        this.maxTotalBytes = maxTotalBytes
        this.maxStringBytes = maxStringBytes
        this.maxNumberBytes = maxNumberBytes
    }

    // ── 抽象格式层（Serializer 基类契约）：读取面（施工块 5-2b）──

    // 读取一个完整 JSON 文档（§4.7.6：严格单文档；拒绝空文档、第二根值、
    // 注释、尾随逗号、重复成员名；深度/大小限制在解析期强制）并建立 SB
    // 通用表示（§4.7.1/§4.7.5 无目标规则：对象→Map<String, Any?> 或
    // 带类型标识的 Parcel；数组→Array\<Any?>；数字→i64/u64/double；
    // null 为实际 null；Map 包装按 §4.7.3 解释回活 Map）。传入流借用
    // （§4.6.2/§4.4）：只读、不关闭、不刷新；底层 I/O 异常原样传播，
    // 不伪装成 JSON 语法错误。
    pub override func read(input: core.io.InputStream): Any? {
        const node = parseNode(input)
        return buildDynamic(node)
    }

    // readAs：类型引导读取（§4.7.1）。用 T 的类型元信息（反射
    // typeNameOf/fieldsOf/casesOf）按目标声明构造对应 SB 值——i32 字段
    // 的 JSON 3 直接读成 i32（不经 i64 中转）；集合按元素/键值声明构造；
    // 枚举按 case 声明读取两形态（§4.7.4）；自定义对象按反射字段构造
    // Parcel 后经严格 fromParcel\<T> 恢复。非可空目标遇 null 报错；字段
    // 缺失/多余/类型不匹配由严格恢复（§4.6.3）报错。传入流借用。
    // 嵌套成员递归同一判断（§4.7.1「递归到每个成员时重复这一判断」，
    // 块 5-2c）：buildWire 遇自定义对象/枚举声明经按名反射
    // （FieldInfo.typeName → fieldsOf/casesOf 按名重载）取得嵌套类型
    // 元信息后递归走类型引导构造（buildNestedParcel）。
    pub func readAs\<T with Serializable>(input: core.io.InputStream): T {
        return readAsWith\<T>(core.serialization.typeNameOf\<T>(),
            core.serialization.fieldsOf\<T>(), tryCasesOf\<T>(), input)
    }

    // readAs 的 Type\<T> 值形态（§4.7.1：也应支持调用者已有的
    // Type\<T> 值作为目标类型信息；所指实际类型须在 T 的边界内并具备
    // Serializable）。语义与无参形态同一数据源。
    pub func readAs\<T with Serializable>(typeValue: Type\<T>,
            input: core.io.InputStream): T {
        return readAsWith\<T>(core.serialization.typeNameOf(typeValue),
            core.serialization.fieldsOf(typeValue), tryCasesValue\<T>(typeValue),
            input)
    }

    // readAs 公共实现：反射元信息（目标规范名/字段闭包/枚举 case 表）
    // 由两个公共入口分别以无参与 Type\<T> 形态收集，本方法只做
    // 解析 + 类型引导构造 + 严格恢复。枚举目标（cases 非空）经枚举
    // 两形态构建 Parcel；对象目标按反射字段声明逐成员构造 Parcel，
    // 容器目标若元素/键值声明含自定义对象（含可空内层与嵌套容器内
    // 层）则走 envelope 通道：构造匹配目标的 SB envelope wire 后交给
    // 已知静态 T 的严格 fromParcel\<T> 恢复（§4.6.3；wb-5-2d）。其余
    // 目标（标量/纯标量或 Any 元素容器）直接构造 SB 值后 as T。
    priv func readAsWith\<T with Serializable>(targetName: String,
            fields: Array\<core.serialization.FieldInfo>,
            cases: Array\<core.serialization.EnumCaseInfo>,
            input: core.io.InputStream): T {
        const node = parseNode(input)
        if (cases.length > (0 as i32)) {
            const enumParcel = buildEnumParcel(node, targetName, cases)
            return core.serialization.fromParcel\<T>(enumParcel)
        }
        const spec = parseTypeSpec(targetName)
        if (spec.kind == (5 as i32)) {
            const parcel = buildObjectTop(node, targetName, fields)
            return core.serialization.fromParcel\<T>(parcel)
        }
        if (((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) or
                (spec.kind == (4 as i32))) {
            if (specNeedsRecovery(spec)) {
                const parcel = buildContainerEnvelope(node, spec, targetName)
                return core.serialization.fromParcel\<T>(parcel)
            }
        }
        const value = buildValue(node, spec)
        if (value == null) {
            throw new JsonException("非可空目标遇到 null", node.offset)
        }
        return castAsTarget\<T>(value)
    }

    // readAs 的顶层收窄 cast：native 后端对「可空 Any 槽」的 cast 有
    // 收窄缺陷（b4-2 实测：VM cast 宽松，native rigi_try_cast 严格），
    // null 收窄后的值以非可空 Any 形参传递则双宿主一致——同 Parcel
    // .setDynamic 的 putDynamicChecked 分拆先例。
    priv func castAsTarget\<T with Serializable>(value: Any): T {
        return (value as T)
    }

    // casesOf 的宽容入口：非枚举/未登记目标的 casesOf 抛
    // IllegalArgumentException（反射查询未登记的类型），捕获后视为
    // 非枚举（cases 空数组）——枚举判定只能靠反射 API 的异常面。
    priv func tryCasesOf\<T with Serializable>():
            Array\<core.serialization.EnumCaseInfo> {
        try {
            return core.serialization.casesOf\<T>()
        } catch (e: core.IllegalArgumentException) {
            return core.collections.arrayOf\<core.serialization.EnumCaseInfo>(0)
        }
    }

    priv func tryCasesValue\<T with Serializable>(typeValue: Type\<T>):
            Array\<core.serialization.EnumCaseInfo> {
        try {
            return core.serialization.casesOf(typeValue)
        } catch (e: core.IllegalArgumentException) {
            return core.collections.arrayOf\<core.serialization.EnumCaseInfo>(0)
        }
    }

    // 读侧公共入口：逐块读入全部字节（4 KiB 有界缓冲循环；maxTotalBytes
    // 限制在收集期强制），再经 JsonParser 解析为 JsonNode 树（§4.7.6
    // 「首版逐块读取字节…反序列化仍可能持有完整树」）。借用边界：只读、
    // 不关闭、不刷新传入流；读入中途 I/O 异常原样传播。
    priv func parseNode(input: core.io.InputStream): JsonNode {
        const bytes = readAllBytes(input)
        const parser = new JsonParser(bytes, depthLimit, maxStringBytes,
            maxNumberBytes)
        return parser.parseDocument()
    }

    // 逐块收集输入字节到单个快照数组；maxTotalBytes > 0 时累计超限即
    // 抛 JsonException（JSON 资源限制错误族，§4.7.6），不返回截断结果。
    priv func readAllBytes(input: core.io.InputStream): Array\<u8> {
        const acc = new core.collections.List\<u8>()
        const buf = core.collections.spanOf\<u8>(4096)
        while (true) {
            const n = input.read(buf, 0, 4096)
            if (n == 0) {
                break
            }
            var i: i32 = 0
            while (i < n) {
                acc.add((buf[i] as u8))
                i = (i + 1)
            }
            if ((maxTotalBytes > (0 as i64)) and
                    (acc.length > maxTotalBytes)) {
                throw new JsonException(
                    "总输入字节数超过限制 ${maxTotalBytes}", maxTotalBytes)
            }
        }
        const total = (acc.length as i32)
        const arr = core.collections.arrayOf\<u8>(total)
        var k: i32 = 0
        while (k < total) {
            arr[k] = (acc.getAtIndex(k as i64) as u8)
            k = (k + 1)
        }
        return arr
    }

    // 两参数 write：默认保留类型信息（§4.7.2）。
    pub override func write(value: Any?, output: core.io.OutputStream) {
        writeWithMode(value, output, true)
    }

    // 三参数重载：keepTypeInfo=false 使用 §4.7.4 互操作表示。此选项属
    // JSON，不加到 Serializer 基类（§4.7.2）。
    pub func write(value: Any?, output: core.io.OutputStream, keepTypeInfo: bool) {
        writeWithMode(value, output, keepTypeInfo)
    }

    // 写侧公共路径：完整构建 JSON 文本 → UTF-8 编码 → 一次性交给
    // 调用者流。借用边界（§4.6.2）：不关闭、不主动 flush output；
    // 表示/限制错误（JsonException）在写出任何字节前抛出；底层 I/O
    // 异常原样传播，其 partial 字节不回滚（§4.7.2）。
    priv func writeWithMode(value: Any?, output: core.io.OutputStream,
            keepTypeInfo: bool) {
        const builder = new core.text.StringBuilder()
        const writer = new JsonTextWriter(builder, indentText, depthLimit,
            keepTypeInfo)
        writer.writeRoot(value)
        const encoder = new core.text.Utf8Encoder()
        const bytes = encoder.encode(builder.toString())
        output.write(bytes, (0 as i32), bytes.length)
    }
}

// JSON 文本写出器：SB 通用表示 → JSON 文本。每次 write 调用新建
// （独立写出状态，同实例可顺序复用，不支持并发或重入）。
// 环检测沿用 SerializationGraphContext 的 Place 身份表先例：只保存
// 当前路径（活动路径），无环重复引用自然按路径展开为独立副本。
//
// 容器写出统一记账模式（depth = 当前容器层级，根容器 = 1）：
//   enterContainer() 深度守卫 + depth+1；开括号；各成员/元素经
//   newlineIndent(depth) 写在本级；闭合括号前（仅非空）newlineIndent
//   (depth-1) 写在父级（不改 depth）；闭括号；finally 外 depth-1。
priv class JsonTextWriter {
    priv var sb: core.text.StringBuilder
    // 基础缩进文本（空串 = 紧凑）。
    priv const indentText: String
    // 深度上限（根容器深度 1）。
    priv const depthLimit: i32
    // 类型信息保留开关（§4.7.2 / §4.7.4）。
    priv const keepTypeInfo: bool
    // 当前容器深度（标量根 = 0，进入容器递增）。
    priv var depth: i32
    // 活动路径上的对象身份表（Place 持有，leave 时释放）。
    priv var path: core.collections.List\<core.Place\<Object>>

    pub init(_ -> sb, _ -> indentText, _ -> depthLimit, _ -> keepTypeInfo) {
        depth = 0
        path = new core.collections.List\<core.Place\<Object>>()
    }

    // 根值入口：支持完整单值根（含标量/null，§4.7）。
    pub func writeRoot(value: Any?) {
        writeValue(value)
        if (path.length != (0 as i64)) {
            throw new core.IllegalStateException(
                "JsonTextWriter 路径表未清空（内部状态错误）")
        }
    }

    // ── 值分发（sbKind 类别码，§4.6.1）──

    priv func writeValue(value: Any?) {
        if (value == null) {
            sb.append("null")
            return
        }
        writeNonNull(value)
    }

    // null 收窄后路径：独立为非可空 Any 形参（native 后端对可空 Any
    // 槽直接取 typeof 存在收窄缺陷，同 Parcel.setDynamic 分拆先例）。
    priv func writeNonNull(value: Any) {
        const kind = core.serialization.sbKind(value)
        if (kind == (1 as i32)) {
            if (value as bool) { sb.append("true") } else { sb.append("false") }
            return
        }
        if (kind == (2 as i32)) {
            // char：含恰一个 Unicode 标量的 JSON 字符串（补充平面
            // 经 UTF-8 直出，见 writeString）。
            writeString("${value as char}")
            return
        }
        if (kind == (3 as i32)) {
            sb.append("${value as i8}")
            return
        }
        if (kind == (4 as i32)) {
            sb.append("${value as u8}")
            return
        }
        if (kind == (5 as i32)) {
            sb.append("${value as i16}")
            return
        }
        if (kind == (6 as i32)) {
            sb.append("${value as u16}")
            return
        }
        if (kind == (7 as i32)) {
            sb.append("${value as i32}")
            return
        }
        if (kind == (8 as i32)) {
            sb.append("${value as u32}")
            return
        }
        if (kind == (9 as i32)) {
            sb.append("${value as i64}")
            return
        }
        if (kind == (10 as i32)) {
            sb.append("${value as u64}")
            return
        }
        if (kind == (11 as i32)) {
            writeFloatText("${value as float}")
            return
        }
        if (kind == (12 as i32)) {
            writeFloatText("${value as double}")
            return
        }
        if (kind == (13 as i32)) {
            writeString(value as String)
            return
        }
        if (kind == (14 as i32)) {
            // wire null 哨兵记录（可空集合元素/槽位经 toParcel 的内部
            // 表示）还原为 JSON null：格式边界不泄漏内部哨兵文本，
            // 默认与互操作两种模式同口径（§4.7.5 null 对应空值）。
            const p = (value as core.serialization.Parcel)
            if (p.isNullSentinelWire()) {
                sb.append("null")
                return
            }
            writeParcel(p)
            return
        }
        if ((kind == (15 as i32)) or (kind == (16 as i32))) {
            writeSequence(value)
            return
        }
        if (kind == (17 as i32)) {
            writeMap(value)
            return
        }
        throw new JsonException(
            "不是格式支持的 SB 表示（sbKind=${kind}）")
    }

    // 浮点文本：既有 toString 即最短往返表示（§4.7.5）。NaN/正负无穷
    // 报错，不替换成 null 或字符串；负零保留（toString 输出 "-0"）。
    priv func writeFloatText(text: String) {
        if (((text == "NaN") or (text == "Infinity")) or (text == "-Infinity")) {
            throw new JsonException(
                "JSON 不支持浮点 ${text}（NaN/正负无穷报错，§4.7.5）")
        }
        sb.append(text)
    }

    // ── 容器与对象 ──

    // Array/List → JSON 数组；无声明的活容器沿原动态分发路径。
    priv func writeSequence(value: Any) {
        writeSequenceWithElement(value, null)
    }

    // 已编码 Array<Any> 的元素由可信字段/容器声明引导；不可按数组长度
    // 或偶数项形状推断 Map，否则普通业务数组会被误转。
    priv func writeSequenceWithElement(value: Any, elem: JsonTypeSpec?) {
        enterContainer()
        const tracked = enterNode(value)
        try {
            sb.append("[")
            const count = core.serialization.sbLength(value)
            if (count > (0 as i64)) {
                var i: i64 = (0 as i64)
                while (i < count) {
                    if (i > (0 as i64)) { sb.append(",") }
                    newlineIndent(depth)
                    if (elem == null) {
                        writeValue(core.serialization.sbElementAt(value, i))
                    } else {
                        writeDeclaredValue(core.serialization.sbElementAt(value, i),
                            (elem as JsonTypeSpec))
                    }
                    i = (i + (1 as i64))
                }
                newlineIndent((depth - 1))
            }
            sb.append("]")
        } finally(e) {
            leaveNode(tracked)
        }
        depth = (depth - 1)
    }

    // 编译器合成的 wire 按反射声明解释；非集合值仍由既有分发处理，
    // 特别是 null 哨兵必须经过 writeNonNull 的专用还原路径。
    priv func writeDeclaredValue(value: Any?, spec: JsonTypeSpec) {
        if (value == null) {
            writeValue(value)
            return
        }
        const actual = value as Any
        if (core.serialization.sbKind(actual) == (14 as i32)) {
            const parcel = actual as core.serialization.Parcel
            if (parcel.isNullSentinelWire()) {
                writeValue(value)
                return
            }
            // 顶层容器与嵌套容器的元素可经 Parcel envelope 承载
            // ..value；由其实际类型名引导，不把 Parcel 强转为数组。
            if (((spec.kind == (4 as i32)) or (spec.kind == (2 as i32))) or
                    (spec.kind == (3 as i32))) {
                if (normalizeTypeName(parcel.typeName) !=
                        normalizeTypeName(spec.fullName)) {
                    throw new JsonException("集合 envelope 与声明类型不匹配")
                }
                writeParcel(parcel)
                return
            }
        }
        if (spec.kind == (6 as i32)) {
            writeDeclaredValue(value, specOr(spec.elem))
        } else if (spec.kind == (4 as i32)) {
            writeMapWire(actual, spec)
        } else if ((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) {
            if (core.serialization.sbKind(actual) != (15 as i32)) {
                throw new JsonException("集合声明的内部载荷不是 Array<Any>")
            }
            writeSequenceWithElement(actual, spec.elem)
        } else {
            writeValue(value)
        }
    }

    // Map wire 为交替键值槽，只能由声明为 Map 的通道调用；保留外部
    // content 条目次序与 Map 键/值递归声明，不物化伪造的业务 Map。
    priv func writeMapWire(value: Any, spec: JsonTypeSpec) {
        if (core.serialization.sbKind(value) != (15 as i32)) {
            throw new JsonException("Map 声明的内部载荷不是 Array<Any>")
        }
        const count = core.serialization.sbLength(value)
        if ((count % (2 as i64)) != (0 as i64)) {
            throw new JsonException("Map 内部键值载荷长度非偶")
        }
        const keySpec = specOr(spec.key)
        if ((not keepTypeInfo) and
                ((keySpec.kind != (1 as i32)) or
                 (keySpec.scalarKind != (13 as i32)))) {
            throw new JsonException("互操作输出不支持非 String 键 Map")
        }
        enterContainer()
        const tracked = enterNode(value)
        try {
            sb.append("{")
            if (keepTypeInfo) {
                writeMemberPrefix(".rigi.type-identifier")
                writeString(normalizeTypeName(spec.fullName))
                sb.append(",")
                writeMemberPrefix("content")
                enterContainer()
                sb.append("[")
                var i: i64 = (0 as i64)
                while (i < count) {
                    if (i > (0 as i64)) { sb.append(",") }
                    newlineIndent(depth)
                    enterContainer()
                    sb.append("{")
                    writeMemberPrefix("key")
                    writeDeclaredValue(core.serialization.sbElementAt(value, i), keySpec)
                    sb.append(",")
                    writeMemberPrefix("value")
                    writeDeclaredValue(core.serialization.sbElementAt(value,
                        (i + (1 as i64))), specOr(spec.value))
                    newlineIndent((depth - 1))
                    sb.append("}")
                    depth = (depth - 1)
                    i = (i + (2 as i64))
                }
                if (count > (0 as i64)) { newlineIndent((depth - 1)) }
                sb.append("]")
                depth = (depth - 1)
            } else {
                var i: i64 = (0 as i64)
                while (i < count) {
                    if (i > (0 as i64)) { sb.append(",") }
                    const key = core.serialization.sbElementAt(value, i)
                    if (key == null) {
                        throw new JsonException("互操作 Map 的键必须为 String")
                    }
                    const keyValue = key as Any
                    var text = ""
                    if (core.serialization.sbKind(keyValue) == (13 as i32)) {
                        text = keyValue as String
                    } else if (core.serialization.sbKind(keyValue) == (14 as i32)) {
                        // 集合 envelope 的标量键是受控 ..value 记录；
                        // 仅声明 String 且记录确属 String 时才解包。
                        const record = keyValue as core.serialization.Parcel
                        if (normalizeTypeName(record.typeName) != ".string") {
                            throw new JsonException("互操作 Map 的键记录不是 String")
                        }
                        if ((record.metaElementCount() != (1 as i64)) or
                                ((record.metaKeyAtIndex((0 as i64)) if? "") != "..value")) {
                            throw new JsonException("互操作 Map 的键记录缺少标量载荷")
                        }
                        const raw = record.metaValueAtIndex((0 as i64))
                        if ((raw == null) or
                                (core.serialization.sbKind(raw as Any) != (13 as i32))) {
                            throw new JsonException("互操作 Map 的键记录载荷不是 String")
                        }
                        text = raw as String
                    } else {
                        throw new JsonException("互操作 Map 的键必须为 String")
                    }
                    writeMemberPrefix(text)
                    writeDeclaredValue(core.serialization.sbElementAt(value,
                        (i + (1 as i64))), specOr(spec.value))
                    i = (i + (2 as i64))
                }
            }
            newlineIndent((depth - 1))
            sb.append("}")
        } finally(e) {
            leaveNode(tracked)
        }
        depth = (depth - 1)
    }

    // Map 分发：保留类型信息 → §4.7.3 外层对象形式；互操作 → §4.7.4。
    priv func writeMap(value: Any) {
        if (keepTypeInfo) {
            writeMapTyped(value)
        } else {
            writeMapInterop(value)
        }
    }

    // §4.7.3 保留类型信息时的 Map 表示：外层对象 =
    //   { ".rigi.type-identifier": <完整 Map 类型名>,
    //     "content": [ {"key": k, "value": v}, ... ] }
    // 条目按插入序、每项恰好 key/value 两成员；条目是格式结构，不另加
    // Rigi 类型标识；String 与非 String 键同形；空 Map content 为 []。
    priv func writeMapTyped(value: Any) {
        enterContainer()
        const tracked = enterNode(value)
        try {
            sb.append("{")
            writeMemberPrefix(".rigi.type-identifier")
            writeString(typeIdentifierOf(value))
            sb.append(",")
            writeMemberPrefix("content")
            writeMapContent(value)
            newlineIndent((depth - 1))
            sb.append("}")
        } finally(e) {
            leaveNode(tracked)
        }
        depth = (depth - 1)
    }

    // Map content 键值对数组（格式结构，仍是 JSON 容器，计深度）：
    // [ {"key": k, "value": v}, ... ]，空 Map 为 []。
    priv func writeMapContent(value: Any) {
        enterContainer()
        sb.append("[")
        const count = core.serialization.sbLength(value)
        if (count > (0 as i64)) {
            var i: i64 = (0 as i64)
            while (i < count) {
                if (i > (0 as i64)) { sb.append(",") }
                newlineIndent(depth)
                enterContainer()
                sb.append("{")
                writeMemberPrefix("key")
                writeValue(core.serialization.sbKeyAt(value, i))
                sb.append(",")
                writeMemberPrefix("value")
                writeValue(core.serialization.sbValueAt(value, i))
                newlineIndent((depth - 1))
                sb.append("}")
                depth = (depth - 1)
                i = (i + (1 as i64))
            }
            newlineIndent((depth - 1))
        }
        sb.append("]")
        depth = (depth - 1)
    }

    // §4.7.4 互操作 Map 表示：仅支持 String 键 Map——输出普通 JSON
    // 对象，键直接作成员名（业务键原样保留，含 .rigi.type-identifier），
    // 值递归应用同一选项。非 String 键 Map 报错（不把键转字符串、不回退
    // 键值对数组；此边界同样适用于空 Map 与嵌套 Map）。键类型判定取自
    // 类型元信息（sbTypeName 的键实参），空 Map 也按声明键类型报错。
    priv func writeMapInterop(value: Any) {
        if (not mapKeyIsString(value)) {
            throw new JsonException(
                "互操作输出（keepTypeInfo=false）不支持非 String 键 Map，且不把键转为字符串（§4.7.4）")
        }
        enterContainer()
        const tracked = enterNode(value)
        try {
            sb.append("{")
            const count = core.serialization.sbLength(value)
            if (count > (0 as i64)) {
                var i: i64 = (0 as i64)
                var first = true
                while (i < count) {
                    if (not first) { sb.append(",") }
                    first = false
                    writeMemberPrefix(
                        core.serialization.sbKeyAt(value, i) as String)
                    writeValue(core.serialization.sbValueAt(value, i))
                    i = (i + (1 as i64))
                }
                newlineIndent((depth - 1))
            }
            sb.append("}")
        } finally(e) {
            leaveNode(tracked)
        }
        depth = (depth - 1)
    }

    // Parcel → 对象/枚举/envelope 解包（meta 扫描先于形态分发）。
    // 引用语义（§4.7.2）：当前路径真环立即失败；图模式 Parcel 即使无环
    // 也拒绝；无环重复引用按路径展开为独立副本。
    // 结构注记：try 体内不用 return（分支统一 if/else 收尾），规避
    // native lowering 的 return-through-finally 值槽复用风险；路径成员
    // 资格覆盖整个写出（含子值递归，环检测依赖）。
    priv func writeParcel(parcel: core.serialization.Parcel) {
        const tracked = enterNode(parcel)
        var enumCase: String? = null
        var hasScalarPayload = false
        var scalarPayload: Any? = null
        try {
            const metaCount = parcel.metaElementCount()
            var i: i64 = (0 as i64)
            while (i < metaCount) {
                const key = (parcel.metaKeyAtIndex(i) if? "")
                if (((key == "..id") or (key == "..ref")) or (key == "..data")) {
                    throw new JsonException(
                        "JSON 不支持图模式 Parcel（发现元数据 ${key}），即使无环也拒绝（§4.7.2）")
                }
                if (key == "..case") {
                    enumCase = (parcel.metaValueAtIndex(i) as String)
                }
                if (key == "..value") {
                    // SerializationBase 宿主（如 Parcel 自身）的标量/集合
                    // 载荷 envelope：解包输出载荷本身（不输出受控元数据）。
                    hasScalarPayload = true
                    scalarPayload = parcel.metaValueAtIndex(i)
                }
                i = (i + (1 as i64))
            }
            if (enumCase != null) {
                writeEnumParcel(parcel, enumCase)
            } else if (hasScalarPayload) {
                const spec = parseTypeSpec(parcel.typeName)
                if (((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) or
                        (spec.kind == (4 as i32))) {
                    writeDeclaredValue(scalarPayload, spec)
                } else {
                    writeValue(scalarPayload)
                }
            } else {
                writeObjectParcel(parcel)
            }
        } finally(e) {
            leaveNode(tracked)
        }
    }

    // 枚举 Parcel（meta ..case = case 名；实例字段按普通字段输出）。
    priv func writeEnumParcel(parcel: core.serialization.Parcel,
            enumCase: String?) {
        const caseName = (enumCase if? "")
        if (keepTypeInfo) {
            // §4.7.2：.rigi.type-identifier + .rigi.enum-case + 实例字段。
            enterContainer()
            sb.append("{")
            writeMemberPrefix(".rigi.type-identifier")
            writeString(normalizeTypeName(parcel.typeName))
            sb.append(",")
            writeMemberPrefix(".rigi.enum-case")
            writeString(caseName)
            writeParcelFields(parcel, true)
            newlineIndent((depth - 1))
            sb.append("}")
            depth = (depth - 1)
            return
        }
        // §4.7.4 互操作：无载荷枚举输出 case 名字符串；带载荷枚举输出
        // 普通对象（成员 case 载 case 名 + 其余载荷字段）。
        if (parcel.elementCount() == (0 as i64)) {
            writeString(caseName)
            return
        }
        if (parcel.contains("case")) {
            throw new JsonException(
                "枚举载荷字段与互操作输出的 case 成员名冲突，不覆盖或改名（§4.7.4）")
        }
        enterContainer()
        sb.append("{")
        writeMemberPrefix("case")
        writeString(caseName)
        writeParcelFields(parcel, true)
        newlineIndent((depth - 1))
        sb.append("}")
        depth = (depth - 1)
    }

    // 普通对象 Parcel：保留类型信息时元数据在前（.rigi.type-identifier），
    // 业务字段按 Parcel 迭代顺序输出。
    priv func writeObjectParcel(parcel: core.serialization.Parcel) {
        enterContainer()
        sb.append("{")
        var wroteAny = false
        if (keepTypeInfo) {
            writeMemberPrefix(".rigi.type-identifier")
            writeString(normalizeTypeName(parcel.typeName))
            wroteAny = true
        }
        writeParcelFields(parcel, wroteAny)
        if ((parcel.elementCount() > (0 as i64)) or wroteAny) {
            newlineIndent((depth - 1))
        }
        sb.append("}")
        depth = (depth - 1)
    }

    // 业务字段写出：只用实际 Parcel.typeName 的通用字段反射闭包识别
    // 合成集合 wire；未登记类型的手工 Parcel 无可靠字段声明，按原本
    // 动态 SB 值输出，不能凭偶数数组猜成 Map。
    priv func writeParcelFields(parcel: core.serialization.Parcel,
            hasAny: bool) {
        const count = parcel.elementCount()
        var fields = core.collections.arrayOf\<core.serialization.FieldInfo>(0)
        try {
            fields = core.serialization.fieldsOf(parcel.typeName)
        } catch (e: core.IllegalArgumentException) {
            // 动态 Parcel 的未知类型无字段闭包：仍可输出普通 SB 字段。
        }
        var i: i64 = (0 as i64)
        var first = (not hasAny)
        while (i < count) {
            if (first) { first = false } else { sb.append(",") }
            const name = (parcel.keyAtIndex(i) if? "")
            writeMemberPrefix(name)
            var found = false
            var j: i32 = 0
            while (j < fields.length) {
                const fi = (fields[j] as core.serialization.FieldInfo)
                if (fi.name == name) {
                    found = true
                    const spec = parseTypeSpec(fi.typeName)
                    if (fi.nullable) {
                        writeDeclaredValue(parcel.valueAtIndex(i),
                            newNullableSpec(spec))
                    } else {
                        writeDeclaredValue(parcel.valueAtIndex(i), spec)
                    }
                    break
                }
                j = (j + 1)
            }
            if (not found) { writeValue(parcel.valueAtIndex(i)) }
            i = (i + (1 as i64))
        }
    }

    // ── 结构助手 ──

    // 进入 JSON 容器前调用的深度守卫（§4.7.6：默认上限 256，可配置更小）。
    priv func enterContainer() {
        if (depth >= depthLimit) {
            throw new JsonException(
                "JSON 容器嵌套深度超过上限 ${depthLimit}（§4.7.6）")
        }
        depth = (depth + 1)
    }

    // 成员前缀：换行 + 本级缩进 + "name":（紧凑模式无空白）。
    priv func writeMemberPrefix(name: String) {
        newlineIndent(depth)
        writeString(name)
        if (isPretty()) {
            sb.append(": ")
        } else {
            sb.append(":")
        }
    }

    // 换行 + level 级缩进（紧凑模式为无操作）。
    priv func newlineIndent(level: i32) {
        if (not isPretty()) { return }
        sb.append("\n")
        var i: i32 = 0
        while (i < level) {
            sb.append(indentText)
            i = (i + 1)
        }
    }

    priv func isPretty(): bool { return indentText != "" }

    // ── 环检测（活动路径身份表，SerializationGraphContext.enter 先例）──

    // 值类型不参与对象身份；引用类型登记当前路径并查重。返回 true
    // 表示已登记（leaveNode 必须配对调用）。
    priv func enterNode(value: Any): bool {
        if (value is ValueType) { return false }
        const reference = value as Object
        const candidate = placeOf reference
        var i: i64 = (0 as i64)
        while (i < path.length) {
            if (candidate == (path.getAtIndex(i) as core.Place\<Object>)) {
                candidate.dispose()
                throw new JsonException(
                    "JSON 不支持环形引用：当前路径检测到重复对象（§4.7.2）")
            }
            i = (i + (1 as i64))
        }
        path.add(candidate)
        return true
    }

    priv func leaveNode(tracked: bool) {
        if (not tracked) { return }
        const last = (path.length - (1 as i64))
        if (last < (0 as i64)) {
            throw new core.IllegalStateException(
                "JsonTextWriter 路径表下溢（内部状态错误）")
        }
        (path.getAtIndex(last) as core.Place\<Object>).dispose()
        path.removeAt(last)
    }

    // ── 字符串转义 ──

    // JSON 字符串：最小转义集（\" \\ \b \f \n \r \t）+ \u00XX 兜底其余
    // C0 控制字符；其余标量原样直出（UTF-8 多字节序列整体保留——补充
    // 平面经 UTF-8 直出，本实现的选择；5-2b 读取面同时接受 UTF-8 直出
    // 与 \u 代理对，保证往返一致）。
    priv func writeString(text: String) {
        sb.append("\"")
        const count = text.characterCount
        var i: i64 = (0 as i64)
        while (i < count) {
            const ch = (text.characterAt(i) if? ' ')
            const c = (ch as i64)
            if (c == (34 as i64)) {
                sb.append("\\\"")
            } else if (c == (92 as i64)) {
                sb.append("\\\\")
            } else if (c == (8 as i64)) {
                sb.append("\\b")
            } else if (c == (9 as i64)) {
                sb.append("\\t")
            } else if (c == (10 as i64)) {
                sb.append("\\n")
            } else if (c == (12 as i64)) {
                sb.append("\\f")
            } else if (c == (13 as i64)) {
                sb.append("\\r")
            } else if (c < (32 as i64)) {
                sb.append("\\u00")
                sb.append(hexDigit((c >> (4 as i64)) & (15 as i64)))
                sb.append(hexDigit(c & (15 as i64)))
            } else {
                sb.append(ch)
            }
            i = (i + (1 as i64))
        }
        sb.append("\"")
    }

    priv func hexDigit(v: i64): char {
        if (v < (10 as i64)) {
            return ((48 as i64) + v) as char
        }
        return ((87 as i64) + v) as char
    }

    // 类型标识规范化（.rigi.type-identifier 值，§4.7.2）：Parcel.typeName
    // 的闭合泛型实参在 native 为 canonical 拼写（core::i32 / core::
    // String），VM 为 BIL 别名形（.i32 / .string）——对拍实测分裂。契约
    // 要求标识与 Parcel.typeName 一致的完整规范类型名（前置成果口径：
    // 恒 VM BIL 规范拼写、含闭合泛型实参），故把 native canonical 标量
    // 实参 token 归一到别名形。
    // 实现取舍：单遍扫描（非多次 String.replace 链）——VM 路径经
    // contains 前置判定零分配直接返回；native 路径逐字符拷贝并仅替换
    // 完整 token（token 后续字符须非标识符字符，防止误匹配）。
    priv func normalizeTypeName(typeName: String): String {
        if (not typeName.contains("core::")) {
            return typeName
        }
        const count = typeName.characterCount
        const out = new core.text.StringBuilder()
        var i: i64 = (0 as i64)
        while (i < count) {
            const ch = (typeName.characterAt(i) if? ' ')
            if ((ch == 'c') and matchesLiteralAt(typeName, i, "core::")) {
                const tokenLen = scalarAliasTokenLength(typeName, (i + (6 as i64)))
                if (tokenLen > (0 as i64)) {
                    out.append(".")
                    const alias = aliasBodyOf(typeName, (i + (6 as i64)), tokenLen)
                    var a: i64 = (0 as i64)
                    while (a < alias.characterCount) {
                        out.append((alias.characterAt(a) if? ' '))
                        a = (a + (1 as i64))
                    }
                    i = ((i + (6 as i64)) + tokenLen)
                    continue
                }
            }
            out.append(ch)
            i = (i + (1 as i64))
        }
        return out.toString()
    }

    // typeName[pos..] 是否以 literal 开头（逐字符比对，越界即 false）。
    priv func matchesLiteralAt(typeName: String, pos: i64, literal: String): bool {
        const n = literal.characterCount
        if (pos > (typeName.characterCount - n)) { return false }
        var k: i64 = (0 as i64)
        while (k < n) {
            if ((typeName.characterAt((pos + k)) if? ' ') !=
                    (literal.characterAt(k) if? ' ')) {
                return false
            }
            k = (k + (1 as i64))
        }
        return true
    }

    // canonical 标量名对应的别名体（不含点号）：String→string、
    // double→f64、float→f32、bool→bool、char→char；数值宽度名
    // （i8/u8/.../u64）本身即小写别名，原样返回。
    priv func aliasBodyOf(typeName: String, pos: i64, tokenLen: i64): String {
        if ((tokenLen == (6 as i64)) and matchesLiteralAt(typeName, pos, "String")) {
            return "string"
        }
        if ((tokenLen == (6 as i64)) and matchesLiteralAt(typeName, pos, "double")) {
            return "f64"
        }
        if ((tokenLen == (5 as i64)) and matchesLiteralAt(typeName, pos, "float")) {
            return "f32"
        }
        if ((tokenLen == (4 as i64)) and matchesLiteralAt(typeName, pos, "bool")) {
            return "bool"
        }
        if ((tokenLen == (4 as i64)) and matchesLiteralAt(typeName, pos, "char")) {
            return "char"
        }
        // 数值宽度：从原串逐字符拷贝（恒小写）。
        var body = ""
        var k: i64 = (0 as i64)
        while (k < tokenLen) {
            body = "${body}${(typeName.characterAt((pos + k)) if? ' ')}"
            k = (k + (1 as i64))
        }
        return body
    }

    // pos 起的标识符若是 canonical 标量名（String/double/float/bool/
    // char/i8..u64）返回其长度，否则返回 0（token 后续字符须非标识符
    // 字符，防止 "core::i32x" 误判）。
    priv func scalarAliasTokenLength(typeName: String, pos: i64): i64 {
        const candidates = core.collections.arrayOfElements\<String>("String", "double", "float",
            "bool", "char", "i16", "u16", "i32", "u32", "i64", "u64", "i8", "u8")
        var c: i32 = 0
        while (c < candidates.length) {
            const lit = (candidates[c] if? "")
            if (matchesLiteralAt(typeName, pos, lit)) {
                const after = (pos + lit.characterCount)
                if (after >= typeName.characterCount) {
                    return lit.characterCount
                }
                const tail = (typeName.characterAt(after) if? ' ')
                const tl = (tail as i64)
                const lower = ((tl >= (97 as i64)) and (tl <= (122 as i64)))
                const upper = ((tl >= (65 as i64)) and (tl <= (90 as i64)))
                const digit = ((tl >= (48 as i64)) and (tl <= (57 as i64)))
                if (((not lower) and (not upper)) and ((not digit) and
                        (tl != (95 as i64)))) {
                    return lit.characterCount
                }
            }
            c = (c + 1)
        }
        return (0 as i64)
    }

    // Map 的完整规范类型名（.rigi.type-identifier 值，§4.7.2/§4.7.3）：
    // 与 Parcel.typeName 一致的 VM BIL 规范拼写（标量 .i32、容器
    // core.collections::Map<.string, .i32>）——经 sbTypeName 取得后归一
    // （sbTypeName 两宿主拼写不同：VM BIL 别名形 / native canonical 形；
    // 不走 typeNameOf(typeOf(...))：native 对 getid.var 得来的闭合泛型
    // Type 视图存在胖引用 ABI 缺陷（5-2a 对拍实测 fatal），见
    // normalizeTypeName）。
    priv func typeIdentifierOf(value: Any): String {
        return normalizeTypeName(core.serialization.sbTypeName(value))
    }

    // Map 键类型判别：取完整类型名的顶层键实参 token（depth-aware：
    // 键自身可能是泛型，如 .array<.i32>；尾部空格去除），与 String 的
    // 两种宿主拼写（.string / core::String）原地逐字符比对——不物化子
    // 串。空 Map 也按声明键类型判别（§4.7.4 非 String 键报错边界含空
    // Map）。
    priv func mapKeyIsString(value: Any): bool {
        const name = typeIdentifierOf(value)
        return keyTokenEquals(name, ".string") or keyTokenEquals(name, "core::String")
    }

    // "Map<KEY, VALUE>" 形类型名的顶层键实参 token 与 literal 等值比对。
    priv func keyTokenEquals(typeName: String, literal: String): bool {
        const count = typeName.characterCount
        var lt: i64 = (0 as i64) - (1 as i64)
        var i: i64 = (0 as i64)
        while (i < count) {
            if ((typeName.characterAt(i) if? ' ') == '<') {
                lt = i
                break
            }
            i = (i + (1 as i64))
        }
        if (lt < (0 as i64)) { return false }
        var level: i64 = (0 as i64)
        var end: i64 = count
        i = lt
        while (i < count) {
            const ch = (typeName.characterAt(i) if? ' ')
            if (ch == '<') { level = (level + (1 as i64)) }
            if (ch == '>') { level = (level - (1 as i64)) }
            if ((ch == ',') and (level == (1 as i64))) {
                end = i
                break
            }
            i = (i + (1 as i64))
        }
        while (end > (lt + (1 as i64))) {
            if ((typeName.characterAt((end - (1 as i64))) if? ' ') == ' ') {
                end = (end - (1 as i64))
            } else {
                break
            }
        }
        const start = (lt + (1 as i64))
        const tokenLen = (end - start)
        if (tokenLen != literal.characterCount) { return false }
        var k: i64 = (0 as i64)
        while (k < tokenLen) {
            if ((typeName.characterAt((start + k)) if? ' ') !=
                    (literal.characterAt(k) if? ' ')) {
                return false
            }
            k = (k + (1 as i64))
        }
        return true
    }
}

// ════════════════════════════════════════════════════════════════════
// 读取面（施工块 5-2b）：词法/语法解析 + SB 重建 + readAs 类型引导
// ════════════════════════════════════════════════════════════════════
//
// 结构总览（全部为 priv，同文件内部实现细节，不承诺为用户面）：
//   JsonNode/JsonMember —— 语法树节点（数字存 token 原文与整数词法
//     标志，值构造期再按目标精度求值；每个节点记 token 起点字节
//     偏移，供 JsonException 精确报告）；
//   JsonParser —— 字节级递归下降解析（严格 RFC 8259 子集：单文档、
//     无注释、无尾随逗号、成员名转义还原后判重、UTF-8 严格解码、
//     BOM 仅限开头一个、深度/token 大小限制）；
//   JsonTypeSpec —— 目标类型名字符串的结构化解析（VM BIL 别名形与
//     native canonical 形双拼写并收，同 parseSbType 先例）；
//   build* 一族 —— JsonNode → SB 值的构造（动态规则 / SB 精确形态 /
//     Parcel 字段 wire 载荷形态三通道）。

// ── 语法树节点 ──

// JSON 对象成员（保序）。
priv class JsonMember {
    pub const name: String
    pub const value: JsonNode

    pub init(_ -> name, _ -> value) { }
}

// JSON 语法树节点。kind：0=null、1=bool、2=number、3=string、
// 4=array、5=object。number 节点用 text 存 token 原文、isInteger 标记
// 整数词法（构造期再按目标精度求值——动态规则 i64→u64→double，目标
// 规则按声明宽度/精度直接解析）。所有节点记录 token 起点字节偏移
// （含已消费 BOM——偏移直接在输入字节数组上计）。
priv class JsonNode {
    pub var kind: i32
    pub var boolValue: bool
    pub var text: String
    pub var isInteger: bool
    pub var offset: i64
    pub var elements: core.collections.List\<JsonNode>
    pub var members: core.collections.List\<JsonMember>

    pub init() {
        kind = 0
        boolValue = false
        text = ""
        isInteger = true
        offset = (0 as i64)
        elements = new core.collections.List\<JsonNode>()
        members = new core.collections.List\<JsonMember>()
    }
}

// ── 字节级解析器 ──

// 严格 RFC 8259 子集解析器（§4.7.6）。直接在输入字节数组上工作：
// 结构字符按 ASCII 判定；字符串内的多字节序列按 core.text
// Utf8Decoder 同款严格规则内联解码（逐标量内联以保字节偏移精确，
// 非法编码/代理区/越界一律报错，不用替换字符掩盖）；字符串与数字
// token 大小限制、容器深度限制在扫描期强制。每次 read 调用新建
// （独立解析状态，同实例可顺序复用，不支持并发或重入）。
priv class JsonParser {
    priv var bytes: Array\<u8>
    priv var len: i64
    priv var pos: i64
    priv const depthLimit: i32
    priv const stringLimit: i64
    priv const numberLimit: i64

    pub init(_ -> bytes, _ -> depthLimit, _ -> stringLimit,
            _ -> numberLimit) {
        len = (bytes.length as i64)
        pos = (0 as i64)
    }

    // 文档入口：BOM → 根值 → 空白至 EOF。拒绝空文档与第二根值。
    pub func parseDocument(): JsonNode {
        skipBom()
        skipWs()
        if (pos >= len) {
            throw new JsonException("空文档：缺少 JSON 根值", pos)
        }
        const node = parseValue((0 as i32))
        skipWs()
        if (pos < len) {
            throw new JsonException(
                "根值后存在多余内容（一个完整文档后只允许空白直到 EOF）",
                pos)
        }
        return node
    }

    // 值分发。depth = 已打开的容器层数（根容器由调用方以 0 进入，
    // 对象/数组入口 +1 后校验）。
    priv func parseValue(depth: i32): JsonNode {
        const start = pos
        const b = peek()
        if (b == (123 as i64)) {
            pos = (pos + (1 as i64))
            return parseObject(start, (depth + 1))
        }
        if (b == (91 as i64)) {
            pos = (pos + (1 as i64))
            return parseArray(start, (depth + 1))
        }
        if (b == (34 as i64)) {
            const node = new JsonNode()
            node.kind = (3 as i32)
            node.offset = start
            node.text = parseStringToken()
            return node
        }
        if (b == (116 as i64)) {
            expectLiteral("true")
            const node = new JsonNode()
            node.kind = (1 as i32)
            node.boolValue = true
            node.offset = start
            return node
        }
        if (b == (102 as i64)) {
            expectLiteral("false")
            const node = new JsonNode()
            node.kind = (1 as i32)
            node.boolValue = false
            node.offset = start
            return node
        }
        if (b == (110 as i64)) {
            expectLiteral("null")
            const node = new JsonNode()
            node.kind = (0 as i32)
            node.offset = start
            return node
        }
        if ((b == (45 as i64)) or isDigit(b)) {
            return parseNumber(start)
        }
        if (b < (0 as i64)) {
            throw new JsonException("意外的输入结束（值不完整）", pos)
        }
        throw new JsonException("意外的字符（不是合法的 JSON 值起点）", pos)
    }

    // 对象：'{' ws [ member *( ',' member ) ] '}'。成员名经完整字符串
    // 解析（转义还原后判重——"a" 与 "\u0061" 重复即报错，§4.7.6）；
    // 拒绝尾随逗号与未闭合。
    priv func parseObject(start: i64, depth: i32): JsonNode {
        if (depth > depthLimit) {
            throw new JsonException(
                "JSON 容器嵌套深度超过上限 ${depthLimit}", start)
        }
        const node = new JsonNode()
        node.kind = (5 as i32)
        node.offset = start
        skipWs()
        var b = peek()
        if (b == (125 as i64)) {
            pos = (pos + (1 as i64))
            return node
        }
        while (true) {
            if (b != (34 as i64)) {
                throw new JsonException("对象的成员名必须是字符串", pos)
            }
            const nameStart = pos
            const name = parseStringToken()
            if (findMemberIndex(node, name) >= (0 as i64)) {
                throw new JsonException(
                    "JSON 对象出现重复成员名（转义还原后比较）", nameStart)
            }
            skipWs()
            if (peek() != (58 as i64)) {
                throw new JsonException("成员名后缺少 ':'", pos)
            }
            pos = (pos + (1 as i64))
            skipWs()
            const value = parseValue(depth)
            node.members.add(new JsonMember(name, value))
            skipWs()
            b = peek()
            if (b == (44 as i64)) {
                pos = (pos + (1 as i64))
                skipWs()
                b = peek()
                if (b == (125 as i64)) {
                    throw new JsonException("对象尾随逗号（拒绝 ',' 后接 '}'）",
                        pos)
                }
                continue
            }
            if (b == (125 as i64)) {
                pos = (pos + (1 as i64))
                return node
            }
            if (b < (0 as i64)) {
                throw new JsonException("对象未闭合（输入结束）", pos)
            }
            throw new JsonException("对象成员后缺少 ',' 或 '}'", pos)
        }
        // 不可达（循环内全部分支返回或抛出）：满足全路径返回值判定。
        throw new core.IllegalStateException("JsonParser 对象解析内部状态错误")
    }

    // 数组：'[' ws [ value *( ',' value ) ] ']'。拒绝尾随逗号。
    priv func parseArray(start: i64, depth: i32): JsonNode {
        if (depth > depthLimit) {
            throw new JsonException(
                "JSON 容器嵌套深度超过上限 ${depthLimit}", start)
        }
        const node = new JsonNode()
        node.kind = (4 as i32)
        node.offset = start
        skipWs()
        var b = peek()
        if (b == (93 as i64)) {
            pos = (pos + (1 as i64))
            return node
        }
        while (true) {
            const value = parseValue(depth)
            node.elements.add(value)
            skipWs()
            b = peek()
            if (b == (44 as i64)) {
                pos = (pos + (1 as i64))
                skipWs()
                b = peek()
                if (b == (93 as i64)) {
                    throw new JsonException("数组尾随逗号（拒绝 ',' 后接 ']'）",
                        pos)
                }
                continue
            }
            if (b == (93 as i64)) {
                pos = (pos + (1 as i64))
                return node
            }
            if (b < (0 as i64)) {
                throw new JsonException("数组未闭合（输入结束）", pos)
            }
            throw new JsonException("数组元素后缺少 ',' 或 ']'", pos)
        }
        // 不可达（循环内全部分支返回或抛出）：满足全路径返回值判定。
        throw new core.IllegalStateException("JsonParser 数组解析内部状态错误")
    }

    // 字符串 token（调用前提：peek == '"'）：转义还原、\uXXXX 代理
    // 对合并为补充平面标量、非法转义/未配对代理/裸控制字符报错、
    // 多字节序列严格 UTF-8 解码。返回还原后的 String。
    priv func parseStringToken(): String {
        pos = (pos + (1 as i64))
        const contentStart = pos
        const sb = new core.text.StringBuilder()
        while (true) {
            if ((stringLimit > (0 as i64)) and
                    ((pos - contentStart) > stringLimit)) {
                throw new JsonException("字符串 token 超过大小限制",
                    contentStart)
            }
            if (pos >= len) {
                throw new JsonException("字符串未闭合（输入结束）", pos)
            }
            const b = byteAt(pos)
            if (b == (34 as i64)) {
                pos = (pos + (1 as i64))
                break
            }
            if (b == (92 as i64)) {
                pos = (pos + (1 as i64))
                parseEscapeInto(sb)
            } else if (b < (32 as i64)) {
                throw new JsonException(
                    "字符串内控制字符必须转义（RFC 8259）", pos)
            } else if (b < (128 as i64)) {
                sb.append((b as char))
                pos = (pos + (1 as i64))
            } else {
                const scalar = decodeUtf8Scalar()
                sb.append(scalar.key)
                pos = (pos + scalar.value)
            }
        }
        return sb.toString()
    }

    // 转义序列（调用前提：pos 在反斜杠之后）：\" \\ \/ \b \f \n \r \t
    // \uXXXX；代理对合并；非法转义与未配对代理报错。
    priv func parseEscapeInto(sb: core.text.StringBuilder) {
        if (pos >= len) {
            throw new JsonException("转义序列不完整（输入结束）", pos)
        }
        const e = byteAt(pos)
        if (e == (34 as i64)) {
            sb.append((34 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (92 as i64)) {
            sb.append((92 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (47 as i64)) {
            sb.append((47 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (98 as i64)) {
            sb.append((8 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (102 as i64)) {
            sb.append((12 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (110 as i64)) {
            sb.append((10 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (114 as i64)) {
            sb.append((13 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (116 as i64)) {
            sb.append((9 as char))
            pos = (pos + (1 as i64))
            return
        }
        if (e == (117 as i64)) {
            pos = (pos + (1 as i64))
            parseUnicodeEscapeInto(sb)
            return
        }
        throw new JsonException("非法转义序列（只允许引号、反斜杠、斜杠与 b/f/n/r/t/u 转义）",
            (pos - (1 as i64)))
    }

    // \uXXXX（pos 在 'u' 之后）：读 4 位十六进制；高代理要求紧跟
    // \uYYYY 低代理合并为标量；孤立低代理或未配对高代理报错。
    priv func parseUnicodeEscapeInto(sb: core.text.StringBuilder) {
        const unit = readHex4()
        if ((unit >= (55296 as i64)) and (unit <= (56319 as i64))) {
            // 高代理：必须紧跟 \uYYYY 低代理
            if ((pos + (1 as i64)) >= len) {
                throw new JsonException("未配对代理值（高代理后无低代理）", pos)
            }
            if ((byteAt(pos) != (92 as i64)) or
                    (byteAt((pos + (1 as i64))) != (117 as i64))) {
                throw new JsonException("未配对代理值（高代理后必须是反斜杠 u 低代理）",
                    pos)
            }
            pos = (pos + (2 as i64))
            const unit2 = readHex4()
            if ((unit2 < (56320 as i64)) or (unit2 > (57343 as i64))) {
                throw new JsonException("未配对代理值（高代理后不是低代理）", pos)
            }
            const cp: i64 = ((65536 as i64) +
                (((unit - (55296 as i64)) << (10 as i64)) |
                    (unit2 - (56320 as i64))))
            sb.append((cp as char))
            return
        }
        if ((unit >= (56320 as i64)) and (unit <= (57343 as i64))) {
            throw new JsonException("未配对代理值（孤立低代理）",
                (pos - (4 as i64)))
        }
        sb.append((unit as char))
    }

    // 读 4 个十六进制数字（pos 在首位；越界/非十六进制报错）。
    priv func readHex4(): i64 {
        if ((pos + (4 as i64)) > len) {
            throw new JsonException("反斜杠 u 转义的十六进制位不完整", pos)
        }
        var v: i64 = (0 as i64)
        var k: i64 = (0 as i64)
        while (k < (4 as i64)) {
            const d = hexValue(byteAt((pos + k)))
            if (d < (0 as i64)) {
                throw new JsonException("反斜杠 u 转义含非法十六进制位",
                    (pos + k))
            }
            v = ((v << (4 as i64)) | d)
            k = (k + (1 as i64))
        }
        pos = (pos + (4 as i64))
        return v
    }

    // 单个多字节 UTF-8 标量解码（调用前提：byteAt(pos) >= 128）。
    // 规则与 core.text.Utf8Decoder 严格模式一致：首字节范围约束 +
    // 续字节范围约束排除过长编码/代理区/越界（237 上限 159 排除代理
    // 区编码——代理值只能经 \u 转义通道出现）。返回 (标量, 字节数)。
    priv func decodeUtf8Scalar(): core.Pair\<char, i64> {
        const start = pos
        const b0 = byteAt(start)
        if ((b0 < (194 as i64)) or (b0 > (244 as i64))) {
            throw new JsonException("字符串内非法 UTF-8 序列首字节", start)
        }
        var need: i64 = (1 as i64)
        var firstLo: i64 = (128 as i64)
        var firstHi: i64 = (191 as i64)
        var mask: i64 = (31 as i64)
        if (b0 < (224 as i64)) {
            need = (1 as i64)
        } else if (b0 < (240 as i64)) {
            need = (2 as i64)
            mask = (15 as i64)
            if (b0 == (224 as i64)) {
                firstLo = (160 as i64)
            }
            if (b0 == (237 as i64)) {
                firstHi = (159 as i64)
            }
        } else {
            need = (3 as i64)
            mask = (7 as i64)
            if (b0 == (240 as i64)) {
                firstLo = (144 as i64)
            }
            if (b0 == (244 as i64)) {
                firstHi = (143 as i64)
            }
        }
        var cp: i64 = (b0 & mask)
        var k: i64 = (1 as i64)
        while (k <= need) {
            if ((start + k) >= len) {
                throw new JsonException("字符串内 UTF-8 序列截断", start)
            }
            const cb = byteAt((start + k))
            var lo = firstLo
            var hi = firstHi
            if (k != (1 as i64)) {
                lo = (128 as i64)
                hi = (191 as i64)
            }
            if ((cb < lo) or (cb > hi)) {
                throw new JsonException("字符串内 UTF-8 续字节越界（序列非法）",
                    (start + k))
            }
            cp = ((cp << (6 as i64)) | (cb & (63 as i64)))
            k = (k + (1 as i64))
        }
        return new core.Pair\<char, i64>((cp as char), (need + (1 as i64)))
    }

    // 数字 token（调用前提：peek 是 '-' 或数字）：严格 RFC 8259 词法
    // ——可选负号；整数部分 = 0 或 [1-9][0-9]*（前导零即报错，与通用
    // 整数前缀解析不同，JSON 有自己的数字语法）；可选小数（点后至少
    // 一位）；可选指数（e/E + 可选符号 + 至少一位）。token 原文保留
    // 供构造期按目标精度求值（完整消费 + 值域检查由 core.text 解析
    // 承担——词法先按 RFC 切出完整 token，杜绝非法尾部被放行）。
    priv func parseNumber(start: i64): JsonNode {
        const sb = new core.text.StringBuilder()
        if (peek() == (45 as i64)) {
            sb.append((45 as char))
            pos = (pos + (1 as i64))
        }
        const b = peek()
        if (b < (0 as i64)) {
            throw new JsonException("数字 token 不完整（输入结束）", pos)
        }
        if (b == (48 as i64)) {
            sb.append((48 as char))
            pos = (pos + (1 as i64))
            const nx = peek()
            if ((nx >= (48 as i64)) and (nx <= (57 as i64))) {
                throw new JsonException(
                    "数字整数部分前导零非法（RFC 8259：0 后不能直接跟数字）",
                    pos)
            }
        } else if ((b >= (49 as i64)) and (b <= (57 as i64))) {
            while (isDigit(peek())) {
                sb.append((byteAt(pos) as char))
                pos = (pos + (1 as i64))
            }
        } else {
            throw new JsonException("数字缺少整数部分", pos)
        }
        var isInt = true
        if (peek() == (46 as i64)) {
            isInt = false
            sb.append((46 as char))
            pos = (pos + (1 as i64))
            if (not isDigit(peek())) {
                throw new JsonException("小数点后缺少数字", pos)
            }
            while (isDigit(peek())) {
                sb.append((byteAt(pos) as char))
                pos = (pos + (1 as i64))
            }
        }
        const eb = peek()
        if ((eb == (101 as i64)) or (eb == (69 as i64))) {
            isInt = false
            sb.append((eb as char))
            pos = (pos + (1 as i64))
            const sb2 = peek()
            if ((sb2 == (43 as i64)) or (sb2 == (45 as i64))) {
                sb.append((sb2 as char))
                pos = (pos + (1 as i64))
            }
            if (not isDigit(peek())) {
                throw new JsonException("指数部分缺少数字", pos)
            }
            while (isDigit(peek())) {
                sb.append((byteAt(pos) as char))
                pos = (pos + (1 as i64))
            }
        }
        if ((numberLimit > (0 as i64)) and
                ((pos - start) > numberLimit)) {
            throw new JsonException("数字 token 超过大小限制", start)
        }
        const node = new JsonNode()
        node.kind = (2 as i32)
        node.offset = start
        node.text = sb.toString()
        node.isInteger = isInt
        return node
    }

    // ── 基础扫描助手 ──

    // 开头允许一个 UTF-8 BOM（EF BB BF）；输出不生成 BOM（写侧），
    // 不按 BOM 猜其他编码；第二个 BOM 在流中即非法字节（结构位置
    // 报错）。偏移含已消费 BOM——pos 直接在输入字节上推进。
    priv func skipBom() {
        if ((((len >= (3 as i64)) and
                (byteAt((0 as i64)) == (239 as i64))) and
                (byteAt((1 as i64)) == (187 as i64))) and
                (byteAt((2 as i64)) == (191 as i64))) {
            pos = (3 as i64)
        }
    }

    // JSON 空白：空格 / 制表 / 换行 / 回车。
    priv func skipWs() {
        while (pos < len) {
            const b = byteAt(pos)
            if (((b == (32 as i64)) or (b == (9 as i64))) or
                    ((b == (10 as i64)) or (b == (13 as i64)))) {
                pos = (pos + (1 as i64))
            } else {
                break
            }
        }
    }

    // 字面量匹配（true/false/null）：逐字节比对，失配即语法错误。
    priv func expectLiteral(literal: String) {
        const start = pos
        const n = literal.characterCount
        var k: i64 = (0 as i64)
        while (k < n) {
            if ((pos >= len) or
                    (byteAt(pos) != ((literal.characterAt(k) if? ' ') as i64))) {
                throw new JsonException("非法字面量（应为 ${literal}）",
                    (start + k))
            }
            pos = (pos + (1 as i64))
            k = (k + (1 as i64))
        }
    }

    // 当前字节（i64；EOF = -1）。
    priv func peek(): i64 {
        if (pos >= len) {
            return (0 as i64) - (1 as i64)
        }
        return byteAt(pos)
    }

    priv func byteAt(i: i64): i64 {
        return ((bytes[(i as i32)] if? (0 as u8)) as i64)
    }

    priv func isDigit(b: i64): bool {
        return ((b >= (48 as i64)) and (b <= (57 as i64)))
    }

    priv func hexValue(b: i64): i64 {
        if ((b >= (48 as i64)) and (b <= (57 as i64))) {
            return (b - (48 as i64))
        }
        if ((b >= (97 as i64)) and (b <= (102 as i64))) {
            return ((b - (97 as i64)) + (10 as i64))
        }
        if ((b >= (65 as i64)) and (b <= (70 as i64))) {
            return ((b - (65 as i64)) + (10 as i64))
        }
        return (0 as i64) - (1 as i64)
    }

    // 成员名查重（线性扫描；名字已转义还原）。
    priv func findMemberIndex(node: JsonNode, name: String): i64 {
        var i: i64 = (0 as i64)
        while (i < node.members.length) {
            const m = (node.members.getAtIndex(i) as JsonMember)
            if (m.name == name) {
                return i
            }
            i = (i + (1 as i64))
        }
        return (0 as i64) - (1 as i64)
    }
}

// ── 目标类型名结构化（JsonTypeSpec）──

// 目标类型声明的结构化表示（readAs 递归与 Map content 键值类型共用）。
// kind：0=any（动态规则）、1=标量（scalarKind = sbKind 类别码）、
// 2=List、3=Array、4=Map、5=自定义对象（rawName 为完整类型名）、
// 6=可空包装（elem = 内层规格）。rawName/elemRaw/keyRaw/valueRaw 保
// 留原文拼写（sbBuild* 的实参名同时接受两种宿主拼写，原样透传）。
// fullName = 本规格的完整容器名原文（含闭合实参，如
// "core.collections::List<NdPoint>"）：顶层容器 envelope 通道的
// wire typeName 与嵌套容器的子 envelope 名都取它（与 typeNameOf 的
// 字面量同源同拼写，严格恢复的 typeName 分发按它命中候选）。
priv class JsonTypeSpec {
    pub var kind: i32
    pub var scalarKind: i32
    pub var nullable: bool
    pub var rawName: String
    pub var fullName: String
    pub var elem: JsonTypeSpec? = null
    pub var elemRaw: String
    pub var key: JsonTypeSpec? = null
    pub var keyRaw: String
    pub var value: JsonTypeSpec? = null
    pub var valueRaw: String

    pub init() {
        kind = (0 as i32)
        scalarKind = (0 as i32)
        nullable = false
        rawName = ""
        fullName = ""
        elemRaw = ""
        keyRaw = ""
        valueRaw = ""
    }
}

// 解析目标类型名（容错入口）：完整消费且结构合法 → 对应规格；否则
// 按自定义对象处理（rawName = 完整名字——用户类型/未知拼写由严格
// fromParcel 的名称分发承担判定）。
priv func parseTypeSpec(name: String): JsonTypeSpec {
    const trimmed = name.trim()
    const r = parseSpecAt(trimmed, (0 as i64))
    if (r.value != trimmed.characterCount) {
        return newObjectSpec(trimmed)
    }
    return r.key
}

priv func newObjectSpec(name: String): JsonTypeSpec {
    const spec = new JsonTypeSpec()
    spec.kind = (5 as i32)
    spec.rawName = name
    return spec
}

priv func newNullableSpec(innerSpec: JsonTypeSpec): JsonTypeSpec {
    const spec = new JsonTypeSpec()
    spec.kind = (6 as i32)
    spec.nullable = true
    spec.elem = innerSpec
    return spec
}

// 可空规格槽的解包（调用前提：kind 已判定为对应容器/可空；空槽只在
// 未初始化的兜底路径出现，回退 any 规格）。
priv func specOr(p: JsonTypeSpec?): JsonTypeSpec {
    return (p if? (new JsonTypeSpec()))
}

// 类型名递归解析：返回 (规格, 消费后位置)。失败回退 object 规格并
// 消费到末尾（parseTypeSpec 以「未完整消费」再兜底一次）。
priv func parseSpecAt(name: String, start: i64):
        core.Pair\<JsonTypeSpec, i64> {
    const n = name.characterCount
    var pos = start
    while ((pos < n) and ((name.characterAt(pos) if? ' ') == ' ')) {
        pos = (pos + (1 as i64))
    }
    const tokStart = pos
    while (pos < n) {
        const ch = (name.characterAt(pos) if? ' ')
        if (((ch == '<') or (ch == '>')) or (ch == ',')) {
            break
        }
        pos = (pos + (1 as i64))
    }
    if (pos == tokStart) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const token = name.slice(tokStart, (pos - tokStart))
    // 标量叶子（VM BIL 别名形 / native canonical 形双拼写并收）。
    const scalar = scalarSpecOf(token)
    if (scalar != null) {
        return new core.Pair\<JsonTypeSpec, i64>((scalar as JsonTypeSpec), pos)
    }
    // 可空包装。
    if ((token == ".nullable") or (token == "core::Nullable")) {
        const r = parseWrappedSpec(name, pos, n, true)
        r.key.fullName = name.slice(tokStart, (r.value - tokStart)).trim()
        return r
    }
    // 数组。
    if ((token == ".array") or (token == "core::Array")) {
        const r = parseWrappedSpec(name, pos, n, false)
        r.key.fullName = name.slice(tokStart, (r.value - tokStart)).trim()
        return r
    }
    // List。
    if (token == "core.collections::List") {
        const r = parseWrappedSpec(name, pos, n, false)
        if (r.key.kind != (5 as i32)) {
            r.key.kind = (2 as i32)
        }
        r.key.fullName = name.slice(tokStart, (r.value - tokStart)).trim()
        return r
    }
    // Map<k, v>。
    if (token == "core.collections::Map") {
        const r = parseMapSpec(name, pos, n)
        r.key.fullName = name.slice(tokStart, (r.value - tokStart)).trim()
        return r
    }
    // 其余一律按自定义对象（含用户类型带闭合泛型实参的形态）。消费
    // 规则：token 本身即对象名（用户类型 NdPoint）；token 后紧随 '<'
    // 时并入闭合泛型实参段（'<'..'>' 平衡扫描，实参可嵌套，如
    // Box<core.collections::List<.i32>>）；实参段不闭合时回退整名消费
    // （由 parseTypeSpec 的「未完整消费」兜底再判为对象）。
    // b5-2c 修正：旧实现一律消费到串尾——顶层调用无碍，但作为容器
    // 内层实参（List<NdPoint> 的 NdPoint）会吞掉闭合 '>' 令包装解析
    // 回退整名对象（SB 宿主 List 构造的 fieldsOf 命中空字段闭包后在
    // buildObjectTop 报形状错误）。
    if ((pos < n) and ((name.characterAt(pos) if? ' ') == '<')) {
        const end = scanBalancedArgsEnd(name, pos, n)
        if (end > (0 as i64)) {
            return new core.Pair\<JsonTypeSpec, i64>(
                newObjectSpec(name.slice(tokStart, (end - tokStart))), end)
        }
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    return new core.Pair\<JsonTypeSpec, i64>(
        newObjectSpec(name.slice(tokStart, (pos - tokStart))), pos)
}

// '<'..'>' 平衡扫描：pos 指向 '<'，返回匹配 '>' 之后的位置；
// 不闭合返回 -1。
priv func scanBalancedArgsEnd(name: String, pos: i64, n: i64): i64 {
    var level: i64 = (0 as i64)
    var i: i64 = pos
    while (i < n) {
        const ch = (name.characterAt(i) if? ' ')
        if (ch == '<') {
            level = (level + (1 as i64))
        } else if (ch == '>') {
            level = (level - (1 as i64))
            if (level == (0 as i64)) {
                return (i + (1 as i64))
            }
        }
        i = (i + (1 as i64))
    }
    return (0 as i64) - (1 as i64)
}

// 单实参包装段 "<innerSpec>"：nullable=true 得可空包装（kind 6），
// false 得数组（kind 3，调用方可改判 List）。结构不合法回退 object。
priv func parseWrappedSpec(name: String, pos: i64, n: i64,
        nullable: bool): core.Pair\<JsonTypeSpec, i64> {
    if ((pos >= n) or ((name.characterAt(pos) if? ' ') != '<')) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const innerStart = (pos + (1 as i64))
    const innerSpec = parseSpecAt(name, innerStart)
    if (innerSpec.value >= n) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    if ((name.characterAt(innerSpec.value) if? ' ') != '>') {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const spec = new JsonTypeSpec()
    if (nullable) {
        spec.kind = (6 as i32)
        spec.nullable = true
        spec.elem = innerSpec.key
        spec.rawName = name.slice(innerStart, (innerSpec.value - innerStart)).trim()
        return new core.Pair\<JsonTypeSpec, i64>(spec,
            (innerSpec.value + (1 as i64)))
    }
    spec.kind = (3 as i32)
    spec.elem = innerSpec.key
    spec.elemRaw = name.slice(innerStart, (innerSpec.value - innerStart)).trim()
    return new core.Pair\<JsonTypeSpec, i64>(spec, (innerSpec.value + (1 as i64)))
}

// Map<k, v> 两段解析（顶层逗号切分，实参可带嵌套泛型）。
priv func parseMapSpec(name: String, pos: i64, n: i64):
        core.Pair\<JsonTypeSpec, i64> {
    if ((pos >= n) or ((name.characterAt(pos) if? ' ') != '<')) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const kStart = (pos + (1 as i64))
    // 顶层逗号（depth-aware）。
    var level: i64 = (0 as i64)
    var comma: i64 = (0 as i64) - (1 as i64)
    var q = kStart
    while (q < n) {
        const ch = (name.characterAt(q) if? ' ')
        if (ch == '<') {
            level = (level + (1 as i64))
        }
        if (ch == '>') {
            if (level == (0 as i64)) {
                break
            }
            level = (level - (1 as i64))
        }
        if ((ch == ',') and (level == (0 as i64))) {
            comma = q
            break
        }
        q = (q + (1 as i64))
    }
    if (comma < (0 as i64)) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const key = parseSpecAt(name, kStart)
    if (key.value != comma) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const vStart = (comma + (1 as i64))
    const value = parseSpecAt(name, vStart)
    if (value.value >= n) {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    if ((name.characterAt(value.value) if? ' ') != '>') {
        return new core.Pair\<JsonTypeSpec, i64>(newObjectSpec(name), n)
    }
    const spec = new JsonTypeSpec()
    spec.kind = (4 as i32)
    spec.key = key.key
    spec.keyRaw = name.slice(kStart, (comma - kStart)).trim()
    spec.value = value.key
    spec.valueRaw = name.slice(vStart, (value.value - vStart)).trim()
    return new core.Pair\<JsonTypeSpec, i64>(spec, (value.value + (1 as i64)))
}

// 标量叶子 token → 规格（sbKind 类别码；不识别返回 null）。
priv func scalarSpecOf(token: String): JsonTypeSpec? {
    var kind: i32 = (0 as i32)
    var matched = true
    if ((token == ".bool") or (token == "core::bool")) {
        kind = (1 as i32)
    } else if ((token == ".char") or (token == "core::char")) {
        kind = (2 as i32)
    } else if ((token == ".i8") or (token == "core::i8")) {
        kind = (3 as i32)
    } else if ((token == ".u8") or (token == "core::u8")) {
        kind = (4 as i32)
    } else if ((token == ".i16") or (token == "core::i16")) {
        kind = (5 as i32)
    } else if ((token == ".u16") or (token == "core::u16")) {
        kind = (6 as i32)
    } else if ((token == ".i32") or (token == "core::i32")) {
        kind = (7 as i32)
    } else if ((token == ".u32") or (token == "core::u32")) {
        kind = (8 as i32)
    } else if ((token == ".i64") or (token == "core::i64")) {
        kind = (9 as i32)
    } else if ((token == ".u64") or (token == "core::u64")) {
        kind = (10 as i32)
    } else if ((token == ".f32") or (token == "core::float")) {
        kind = (11 as i32)
    } else if ((token == ".f64") or (token == "core::double")) {
        kind = (12 as i32)
    } else if ((token == ".string") or (token == "core::String")) {
        kind = (13 as i32)
    } else if ((token == ".any") or (token == "core::any")) {
        const anySpec = new JsonTypeSpec()
        anySpec.kind = (0 as i32)
        return anySpec
    } else {
        matched = false
    }
    if (not matched) {
        return null
    }
    const spec = new JsonTypeSpec()
    spec.kind = (1 as i32)
    spec.scalarKind = kind
    return spec
}

// ── JsonNode → SB 值构造 ──

// 动态规则（§4.7.5 无目标读取）：整数词法先 i64；超 i64 非负 u64；
// 再超报错；小数/指数词法 double（不把整数先经过浮点）；对象无类型
// 标识读为 Map<String, Any?>（保插入序、空对象与空数组保持不同、
// null 实际 null、bool 只对应 JSON 布尔）；数组读为 Array\<Any?>；
// 带类型标识的对象按 §4.7.2/§4.7.3 解释（Parcel / Map content 活 Map）。
priv func buildDynamic(node: JsonNode): Any? {
    if (node.kind == (0 as i32)) {
        return null
    }
    if (node.kind == (1 as i32)) {
        const v: bool = node.boolValue
        return v
    }
    if (node.kind == (2 as i32)) {
        return dynamicNumber(node)
    }
    if (node.kind == (3 as i32)) {
        return node.text
    }
    if (node.kind == (4 as i32)) {
        const n = (node.elements.length as i32)
        const arr = core.collections.arrayOf\<Any?>(n)
        var i: i32 = 0
        while (i < n) {
            arr[i] = buildDynamic((node.elements.getAtIndex(i as i64) as JsonNode))
            i = (i + 1)
        }
        return arr
    }
    return buildObjectDynamic(node)
}

// 无目标数字（§4.7.5）：i64 → u64 → 报错；小数/指数 → double。
priv func dynamicNumber(node: JsonNode): Any {
    if (node.isInteger) {
        var asI64: i64 = (0 as i64)
        var ok = false
        try {
            asI64 = i64.parse(node.text)
            ok = true
        } catch (e: core.text.NumberParseException) {
            ok = false
        }
        if (ok) {
            return asI64
        }
        if (not node.text.startsWith("-")) {
            var asU64: u64 = (0 as u64)
            var okU = false
            try {
                asU64 = u64.parse(node.text)
                okU = true
            } catch (e2: core.text.NumberParseException) {
                okU = false
            }
            if (okU) {
                return asU64
            }
            throw new JsonException("整数数值超出 u64 可表示范围", node.offset)
        }
        throw new JsonException("整数数值超出 i64 可表示范围", node.offset)
    }
    try {
        return double.parse(node.text)
    } catch (e3: core.text.NumberParseException) {
        throw new JsonException("浮点数值超出 double 有限范围", node.offset)
    }
}

// 动态对象：有 .rigi.type-identifier → Map 类型标识走 §4.7.3 content
// 解释回活 Map；其余（含未知类型标识——结构合法即保留，到请求恢复
// 对象时才由 fromParcel 报未知类型，§4.7.1）读为 Parcel（
// .rigi.enum-case 进 meta 槽）。无标识 → Map<String, Any?> 业务键
// 原样（无标识对象的成员名都是业务键）。
priv func buildObjectDynamic(node: JsonNode): Any {
    const idNode = findMember(node, ".rigi.type-identifier")
    if (idNode == null) {
        const map = new core.collections.Map\<String, Any?>()
        var i: i64 = (0 as i64)
        while (i < node.members.length) {
            const m = (node.members.getAtIndex(i) as JsonMember)
            map.set(m.name, buildDynamic(m.value))
            i = (i + (1 as i64))
        }
        return map
    }
    if (idNode.kind != (3 as i32)) {
        throw new JsonException("类型标识成员必须是字符串", idNode.offset)
    }
    const id = idNode.text
    if (isMapIdentifier(id)) {
        const args = mapIdentifierArgs(id)
        const kSpec = parseTypeSpec(args.key)
        const vSpec = parseTypeSpec(args.value)
        if ((kSpec.kind == (5 as i32)) or (vSpec.kind == (5 as i32))) {
            throw new JsonException(
                "Map 类型标识的键/值类型无法按 JSON 读取：${id}", idNode.offset)
        }
        return buildMapContent(node, kSpec, vSpec, args.key, args.value)
    }
    return buildTypedParcel(node, id)
}

// 带类型标识对象的 Parcel 构建（动态字段读取）：.rigi.enum-case
// 进 meta 槽（..case，受控元数据通道）；其余成员为业务字段（动态
// 值），数组载荷走类型化 setElement 通道（动态写入面的 SB 值检查
// 不收 .array<.nullable<.any>>——ToySerializer 先例）。
priv func buildTypedParcel(node: JsonNode, typeName: String):
        core.serialization.Parcel {
    const parcel = new core.serialization.Parcel(typeName)
    const caseNode = findMember(node, ".rigi.enum-case")
    if (caseNode != null) {
        if (caseNode.kind != (3 as i32)) {
            throw new JsonException("枚举 case 成员必须是字符串", caseNode.offset)
        }
        parcel.setMetaElement\<String>("..case", caseNode.text)
    }
    var i: i64 = (0 as i64)
    while (i < node.members.length) {
        const m = (node.members.getAtIndex(i) as JsonMember)
        if ((m.name == ".rigi.type-identifier") or
                (m.name == ".rigi.enum-case")) {
            i = (i + (1 as i64))
            continue
        }
        putParcelField(parcel, m.name, buildDynamic(m.value))
        i = (i + (1 as i64))
    }
    return parcel
}

// Parcel 业务字段落槽（动态值）：null 真实落槽（NullSentinel 内部
// 表示，contains 区分「不存在」与「存在且 null」）；数组载荷走
// setElement\<Array<Any?>> 类型化通道（同 ToySerializer 先例，载荷
// 的 .array<.nullable<.any>> 形状不经动态写入面 SB 值检查）；其余
// 经 setDynamic（字段键合法性与 SB 值检查原样生效）。
priv func putParcelField(parcel: core.serialization.Parcel, key: String,
        value: Any?) {
    if (value == null) {
        parcel.setDynamic(key, null)
        return
    }
    putParcelFieldNN(parcel, key, value)
}

priv func putParcelFieldNN(parcel: core.serialization.Parcel, key: String,
        value: Any) {
    // 数组载荷走类型化 setElement 通道（动态写入面的 SB 值检查不收
    // .array<.any?>/.array<.any>——.any 非叶子表成员；ToySerializer
    // 先例）：类型引导路径产出 Array<Any>（§4.6.3 统一载荷形状，
    // 严格恢复的形状前提），动态路径产出 Array<Any?>。
    if (value is Array\<Any?>) {
        parcel.setElement\<Array\<Any?>>(key, value as Array\<Any?>)
        return
    }
    if (value is Array\<Any>) {
        parcel.setElement\<Array\<Any>>(key, value as Array\<Any>)
        return
    }
    parcel.setDynamic(key, value)
}

// 成员查找（线性）。
priv func findMember(node: JsonNode, name: String): JsonNode? {
    var i: i64 = (0 as i64)
    while (i < node.members.length) {
        const m = (node.members.getAtIndex(i) as JsonMember)
        if (m.name == name) {
            const v: JsonNode? = m.value
            return v
        }
        i = (i + (1 as i64))
    }
    const none: JsonNode? = null
    return none
}

// 无目标 read 对明确 Map 类型标识保留原有解释入口；有目标 Map 的
// 包装判别还须完整解析闭合规范名，不能仅凭前缀或成员名猜测。
priv func isMapIdentifier(id: String): bool {
    return id.startsWith("core.collections::Map<")
}

priv func isCompleteMapIdentifier(id: String): bool {
    if (not isMapIdentifier(id)) { return false }
    const spec = parseTypeSpec(id)
    return ((spec.kind == (4 as i32)) and (spec.fullName == id))
}

// Map 类型标识的键/值类型名（顶层逗号切分；缺失返回空串，由调用方
// 判错）。
priv func mapIdentifierArgs(id: String): core.Pair\<String, String> {
    const n = id.characterCount
    var lt: i64 = (0 as i64)
    while (lt < n) {
        if ((id.characterAt(lt) if? ' ') == '<') {
            break
        }
        lt = (lt + (1 as i64))
    }
    if (lt >= n) {
        return new core.Pair\<String, String>("", "")
    }
    var level: i64 = (0 as i64)
    var comma: i64 = (0 as i64) - (1 as i64)
    var i: i64 = (lt + (1 as i64))
    while (i < n) {
        const ch = (id.characterAt(i) if? ' ')
        if (ch == '<') {
            level = (level + (1 as i64))
        }
        if (ch == '>') {
            if (level == (0 as i64)) {
                break
            }
            level = (level - (1 as i64))
        }
        if ((ch == ',') and (level == (0 as i64))) {
            comma = i
            break
        }
        i = (i + (1 as i64))
    }
    if (comma < (0 as i64)) {
        return new core.Pair\<String, String>("", "")
    }
    const k = id.slice((lt + (1 as i64)), (comma - (lt + (1 as i64)))).trim()
    const v = id.slice((comma + (1 as i64)),
        ((n - (1 as i64)) - (comma + (1 as i64)))).trim()
    return new core.Pair\<String, String>(k, v)
}

// §4.7.3 Map content 解释：外层对象的 content 必须是数组；每项是恰
// 好 key/value 两成员的对象（成员顺序宽容，缺/多即报错）；键先按
// 目标键类型恢复，再按 Map 既有 == 语义判重（重复键报错，不经
// Map.set 静默覆盖）；键值均按目标类型规则读取；经 sbBuildMap 构
// 造活 Map（严格核验元素与登记实参一致）。
priv func buildMapContent(node: JsonNode, kSpec: JsonTypeSpec,
        vSpec: JsonTypeSpec, kRaw: String, vRaw: String): Any {
    const contentNode = findMember(node, "content")
    if (contentNode == null) {
        throw new JsonException("Map 表示缺少 content 成员", node.offset)
    }
    if (contentNode.kind != (4 as i32)) {
        throw new JsonException("Map 表示的 content 必须是数组",
            contentNode.offset)
    }
    const n = contentNode.elements.length
    const keys = core.collections.arrayOf\<Any?>((n as i32))
    const values = core.collections.arrayOf\<Any?>((n as i32))
    var i: i64 = (0 as i64)
    while (i < n) {
        const entry = (contentNode.elements.getAtIndex(i) as JsonNode)
        if (entry.kind != (5 as i32)) {
            throw new JsonException("Map content 条目必须是对象", entry.offset)
        }
        if (entry.members.length != (2 as i64)) {
            throw new JsonException(
                "Map content 条目必须恰好具有 key/value 两个成员", entry.offset)
        }
        const m0 = (entry.members.getAtIndex((0 as i64)) as JsonMember)
        const m1 = (entry.members.getAtIndex((1 as i64)) as JsonMember)
        var keyNode = m0
        var valNode = m1
        if ((m0.name == "key") and (m1.name == "value")) {
            keyNode = m0
            valNode = m1
        } else if ((m0.name == "value") and (m1.name == "key")) {
            keyNode = m1
            valNode = m0
        } else {
            throw new JsonException(
                "Map content 条目成员必须是 key 与 value", entry.offset)
        }
        const kv = buildValue(keyNode.value, kSpec)
        var j: i64 = (0 as i64)
        while (j < i) {
            if (jsonKeyEquals(keys[(j as i32)], kv)) {
                throw new JsonException("Map content 出现重复键", entry.offset)
            }
            j = (j + (1 as i64))
        }
        keys[(i as i32)] = kv
        values[(i as i32)] = buildValue(valNode.value, vSpec)
        i = (i + (1 as i64))
    }
    return core.serialization.sbBuildMap(kRaw, vRaw, keys, values)
}

// Map 键判等（Map 既有 == 语义的 JSON 读取面口径）：同 sbKind 按值
// 比较（数值按宽度归类，u64 独立比较防 i64 窄化失真）；null 只等
// null；其余引用类型不比（返回 false）。
priv func jsonKeyEquals(a: Any?, b: Any?): bool {
    if (a == null) {
        return (b == null)
    }
    if (b == null) {
        return false
    }
    return jsonKeyEqualsNN(a, b)
}

priv func jsonKeyEqualsNN(a: Any, b: Any): bool {
    const ka = core.serialization.sbKind(a)
    const kb = core.serialization.sbKind(b)
    if (ka != kb) {
        return false
    }
    // 逐宽度同型比较：禁止跨宽度 cast（native 的 Any→目标类型 cast
    // 按类型标签严格判定，i32 值 as i64 / float 值 as double 均抛
    // CastException；VM cast 宽松会掩盖该分裂——对拍实证）。
    if (ka == (1 as i32)) {
        return ((a as bool) == (b as bool))
    }
    if (ka == (2 as i32)) {
        return ((a as char) == (b as char))
    }
    if (ka == (3 as i32)) {
        return ((a as i8) == (b as i8))
    }
    if (ka == (4 as i32)) {
        return ((a as u8) == (b as u8))
    }
    if (ka == (5 as i32)) {
        return ((a as i16) == (b as i16))
    }
    if (ka == (6 as i32)) {
        return ((a as u16) == (b as u16))
    }
    if (ka == (7 as i32)) {
        return ((a as i32) == (b as i32))
    }
    if (ka == (8 as i32)) {
        return ((a as u32) == (b as u32))
    }
    if (ka == (9 as i32)) {
        return ((a as i64) == (b as i64))
    }
    if (ka == (10 as i32)) {
        return ((a as u64) == (b as u64))
    }
    if (ka == (11 as i32)) {
        return ((a as float) == (b as float))
    }
    if (ka == (12 as i32)) {
        return ((a as double) == (b as double))
    }
    if (ka == (13 as i32)) {
        return ((a as String) == (b as String))
    }
    return false
}

// SB 精确形态构造（readAs 顶层目标与集合元素）：严格按声明构造
// 对应 SB 值——i32 字段的 JSON 3 直接读成 i32（不经 i64 中转）；
// String 收 JSON 字符串；char 检查恰一个 Unicode 标量（支持补充
// 平面）；集合按元素声明经 sbBuild* 构造（空容器依声明恢复）；
// Map 目标按字典读普通对象（业务键原样，含 .rigi.type-identifier
// 形式）或解释 §4.7.3 包装；自定义对象按动态规则构 Parcel（类型
// 标识优先，否则用声明名——集合元素/字段槽的 wire 形态）。
priv func buildValue(node: JsonNode, spec: JsonTypeSpec): Any? {
    if (node.kind == (0 as i32)) {
        if (spec.kind == (6 as i32)) {
            return null
        }
        throw new JsonException("非可空目标遇到 null", node.offset)
    }
    if (spec.kind == (6 as i32)) {
        return buildValue(node, specOr(specOr(spec.elem)))
    }
    if (spec.kind == (0 as i32)) {
        return buildDynamic(node)
    }
    if (spec.kind == (1 as i32)) {
        return buildScalar(node, spec)
    }
    if ((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) {
        if (node.kind != (4 as i32)) {
            throw new JsonException("集合目标期望 JSON 数组", node.offset)
        }
        const n = (node.elements.length as i32)
        const arr = core.collections.arrayOf\<Any?>(n)
        var i: i32 = 0
        while (i < n) {
            arr[i] = buildValue((node.elements.getAtIndex(i as i64) as JsonNode),
                specOr(spec.elem))
            i = (i + 1)
        }
        if (spec.kind == (2 as i32)) {
            return core.serialization.sbBuildList(spec.elemRaw, arr)
        }
        return core.serialization.sbBuildArray(spec.elemRaw, arr)
    }
    if (spec.kind == (4 as i32)) {
        if (node.kind != (5 as i32)) {
            throw new JsonException("Map 目标期望 JSON 对象", node.offset)
        }
        return buildLiveMap(node, specOr(spec.key), specOr(spec.value), spec.keyRaw,
            spec.valueRaw)
    }
    // 自定义对象：wire 形态 = Parcel（§4.6.3 对象槽载荷）；枚举声明同槽
    // 位（case 名文本/对象形态由 buildNestedParcel 经按名反射判例引导）。
    // 嵌套成员递归同一判断（§4.7.1，块 5-2c）。
    return buildNestedParcel(node, spec.rawName)
}

// ── 顶层容器 envelope 通道（wb-5-2d）──
//
// readAs 顶层 Array<Custom>/List<Custom>/Map<String, Custom>（含可空元
// 素/值与嵌套容器）的恢复通道：构造与 EncodeOptionalElement 同口径的
// SB envelope wire（typeName = 目标名原文 + 受控元数据 ..value = 元素
// 记录载荷）后交给已知静态 T 的严格 fromParcel\<T> 恢复（§4.6.3）。
// 合成恢复体的开放泛型元素槽把每个载荷元素按「SB 值记录」解码
// （先经 Parcel 形状核验再按 wire 名分发），因此元素一律为记录形态：
//   - 自定义对象/枚举 = 业务字段记录（buildNestedParcel，同字段通道）；
//   - 标量 = SB 值 envelope（标量 wire 名 + ..value=活值）；
//   - 嵌套容器 = 子 envelope（容器 wire 名 + ..value=递归载荷）；
//   - JSON null（声明可空）= wire null 哨兵记录（与可空元素修复同口径）。
// 严格性不降级：对象成员缺失/多余/宽度不符/null 进非可空由严格
// fromParcel 判定，Map 重复键在读取面即拒绝，未知派生类型由 wire 名
// 分发拒绝——失败语义与既有面一致。

// 判定：容器规格内是否含「需要严格恢复通道」的自定义对象形态
// （含可空内层与嵌套容器内层；Any/纯标量容器继续走 sbBuild* 旧通道）。
priv func specNeedsRecovery(spec: JsonTypeSpec): bool {
    if (spec.kind == (5 as i32)) {
        return true
    }
    if (spec.kind == (6 as i32)) {
        return specNeedsRecovery(specOr(spec.elem))
    }
    if ((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) {
        return specNeedsRecovery(specOr(spec.elem))
    }
    if (spec.kind == (4 as i32)) {
        if (specNeedsRecovery(specOr(spec.key))) {
            return true
        }
        return specNeedsRecovery(specOr(spec.value))
    }
    return false
}

// 顶层容器 envelope：typeName = 目标名原文（与 typeNameOf\<T> 的字面
// 量同源同拼写，恢复端 typeName 分发按它命中候选）+ ..value = 载荷。
priv func buildContainerEnvelope(node: JsonNode, spec: JsonTypeSpec,
        targetName: String): core.serialization.Parcel {
    if (node.kind == (0 as i32)) {
        throw new JsonException("非可空目标遇到 null", node.offset)
    }
    if ((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) {
        if (node.kind != (4 as i32)) {
            throw new JsonException("集合目标期望 JSON 数组", node.offset)
        }
        return newEnvelopeParcel(targetName, buildArrayRecordPayload(node, spec))
    }
    // Map 目标（kind 4）：两种输入形态——保留类型信息的 §4.7.3 包装
    // 对象（content 键值对数组）或互操作普通对象字典（成员名即业务键）。
    if (node.kind != (5 as i32)) {
        throw new JsonException("Map 目标期望 JSON 对象", node.offset)
    }
    return newEnvelopeParcel(targetName, buildMapRecordPayload(node, spec))
}

// Array/List 载荷：元素逐个构造为记录形态。
priv func buildArrayRecordPayload(node: JsonNode, spec: JsonTypeSpec):
        Array\<Any> {
    const n = (node.elements.length as i32)
    const payload = core.collections.arrayOf\<Any>(n)
    var i: i32 = 0
    while (i < n) {
        payload[i] = buildElementRecord(
            (node.elements.getAtIndex(i as i64) as JsonNode), specOr(spec.elem))
        i = (i + 1)
    }
    return payload
}

// Map 载荷（摊平键值交替序，§4.6.3 统一形状）：键值均构造为记录
// 形态。重复键在读取面即拒绝（键按活值判等，与 buildMapContent 同
// 口径），不经 Map.set 静默覆盖。
priv func buildMapRecordPayload(node: JsonNode, spec: JsonTypeSpec):
        Array\<Any> {
    const kSpec = specOr(spec.key)
    if ((kSpec.kind == (1 as i32)) and (kSpec.scalarKind != (13 as i32))) {
        throw new JsonException(
            "非 String 键的目标 Map 只能从键值交替载荷恢复", node.offset)
    }
    const idNode = findMember(node, ".rigi.type-identifier")
    var fromContent = false
    var count: i64 = (0 as i64)
    // 只在闭合 Map 标识下进入包装；结构损坏的真包装继续明确报错。
    var wrapped = false
    if (idNode != null) {
        if (idNode.kind == (3 as i32)) {
            wrapped = isCompleteMapIdentifier(idNode.text)
        }
    }
    if (wrapped) {
        const contentNode = findMember(node, "content")
        if ((contentNode == null) or
                ((contentNode as JsonNode).kind != (4 as i32))) {
            throw new JsonException("Map 表示的 content 必须是数组",
                (contentNode if? node).offset)
        }
        fromContent = true
        count = (contentNode as JsonNode).elements.length
    }
    if (not fromContent) {
        if (kSpec.kind == (5 as i32)) {
            throw new JsonException(
                "Map 键不支持自定义对象类型（无法在读取面判重）", node.offset)
        }
        if ((kSpec.kind != (1 as i32)) or (kSpec.scalarKind != (13 as i32))) {
            throw new JsonException(
                "非 String 键的目标 Map 不能从普通 JSON 对象的字符串成员名恢复（§4.7.4）",
                node.offset)
        }
        count = node.members.length
    }
    const payload = core.collections.arrayOf\<Any>(((count * (2 as i64))) as i32)
    const keys = core.collections.arrayOf\<Any?>((count as i32))
    var i: i64 = (0 as i64)
    while (i < count) {
        var keyNode: JsonNode? = null
        var valNode: JsonNode? = null
        var keyValue: Any? = null
        if (fromContent) {
            const contentNode = (findMember(node, "content") as JsonNode)
            const entry = (contentNode.elements.getAtIndex(i) as JsonNode)
            if (entry.kind != (5 as i32)) {
                throw new JsonException("Map content 条目必须是对象",
                    entry.offset)
            }
            if (entry.members.length != (2 as i64)) {
                throw new JsonException(
                    "Map content 条目必须恰好具有 key/value 两个成员", entry.offset)
            }
            const m0 = (entry.members.getAtIndex((0 as i64)) as JsonMember)
            const m1 = (entry.members.getAtIndex((1 as i64)) as JsonMember)
            var kn = m0
            var vn = m1
            if ((m0.name == "value") and (m1.name == "key")) {
                kn = m1
                vn = m0
            } else if ((m0.name != "key") or (m1.name != "value")) {
                throw new JsonException(
                    "Map content 条目成员必须是 key 与 value", entry.offset)
            }
            keyNode = kn.value
            valNode = vn.value
            keyValue = buildScalarOrText(keyNode as JsonNode, kSpec)
        } else {
            const m = (node.members.getAtIndex(i) as JsonMember)
            keyNode = memberNameNode(m)
            valNode = m.value
            keyValue = m.name
        }
        // 重复键判等（活值口径，先于 envelope 化）。
        var j: i64 = (0 as i64)
        while (j < i) {
            if (jsonKeyEquals(keys[(j as i32)], keyValue)) {
                throw new JsonException("Map content 出现重复键",
                    (keyNode as JsonNode).offset)
            }
            j = (j + (1 as i64))
        }
        keys[(i as i32)] = keyValue
        payload[((i * (2 as i64)) as i32)] = buildElementRecord((keyNode as JsonNode), kSpec)
        payload[(((i * (2 as i64)) + (1 as i64)) as i32)] = buildElementRecord((valNode as JsonNode), specOr(spec.value))
        i = (i + (1 as i64))
    }
    return payload
}

// content 形态的键活值（判重用）：标量键直接构造；可空键 null 记为
// 真实 null 参与 null 只等 null 的判等；其余形态不经此路径。
priv func buildScalarOrText(node: JsonNode, spec: JsonTypeSpec): Any? {
    if (node.kind == (0 as i32)) {
        if (spec.kind == (6 as i32)) {
            return null
        }
        throw new JsonException("非可空目标遇到 null", node.offset)
    }
    if (spec.kind == (6 as i32)) {
        return buildScalarOrText(node, specOr(spec.elem))
    }
    if (spec.kind == (1 as i32)) {
        return buildScalar(node, spec)
    }
    throw new JsonException("Map content 的键必须是标量", node.offset)
}

// 元素记录构造（容器载荷元素一律为 Parcel 记录形态，见通道头注）。
priv func buildElementRecord(node: JsonNode, spec: JsonTypeSpec): Any {
    if (node.kind == (0 as i32)) {
        if (spec.kind == (6 as i32)) {
            return core.serialization.Parcel.newNullSentinelWire()
        }
        throw new JsonException("非可空目标遇到 null", node.offset)
    }
    if (spec.kind == (6 as i32)) {
        return buildElementRecord(node, specOr(spec.elem))
    }
    if (spec.kind == (1 as i32)) {
        return buildScalarEnvelope(node, spec)
    }
    if ((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) {
        if (node.kind != (4 as i32)) {
            throw new JsonException("集合目标期望 JSON 数组", node.offset)
        }
        // 嵌套容器 = 子 envelope（容器 wire 名 + ..value=递归载荷）。
        return newEnvelopeParcel(spec.fullName,
            buildArrayRecordPayload(node, spec))
    }
    if (spec.kind == (4 as i32)) {
        if (node.kind != (5 as i32)) {
            throw new JsonException("Map 目标期望 JSON 对象", node.offset)
        }
        return newEnvelopeParcel(spec.fullName, buildMapRecordPayload(node, spec))
    }
    if (spec.kind == (0 as i32)) {
        throw new JsonException(
            "容器 envelope 载荷不支持动态 Any 元素声明", node.offset)
    }
    // 自定义对象/枚举（kind 5）：业务字段记录（与字段通道同一构造器，
    // 类型标识优先的派生类型恢复语义不变）。
    return buildNestedParcel(node, spec.rawName)
}

// 标量元素 = SB 值 envelope（标量 wire 名 + ..value=按声明宽度的活值；
// 宽度保真由 buildScalar 保证，严格恢复的 StrictWireCheck 按声明核验）。
priv func buildScalarEnvelope(node: JsonNode, spec: JsonTypeSpec):
        core.serialization.Parcel {
    const value = buildScalar(node, spec)
    const parcel = new core.serialization.Parcel(scalarWireName(spec.scalarKind))
    const k = spec.scalarKind
    if (k == (1 as i32)) {
        parcel.setMetaElement\<bool>("..value", value as bool)
        return parcel
    }
    if (k == (2 as i32)) {
        parcel.setMetaElement\<char>("..value", value as char)
        return parcel
    }
    if (k == (3 as i32)) {
        parcel.setMetaElement\<i8>("..value", value as i8)
        return parcel
    }
    if (k == (4 as i32)) {
        parcel.setMetaElement\<u8>("..value", value as u8)
        return parcel
    }
    if (k == (5 as i32)) {
        parcel.setMetaElement\<i16>("..value", value as i16)
        return parcel
    }
    if (k == (6 as i32)) {
        parcel.setMetaElement\<u16>("..value", value as u16)
        return parcel
    }
    if (k == (7 as i32)) {
        parcel.setMetaElement\<i32>("..value", value as i32)
        return parcel
    }
    if (k == (8 as i32)) {
        parcel.setMetaElement\<u32>("..value", value as u32)
        return parcel
    }
    if (k == (9 as i32)) {
        parcel.setMetaElement\<i64>("..value", value as i64)
        return parcel
    }
    if (k == (10 as i32)) {
        parcel.setMetaElement\<u64>("..value", value as u64)
        return parcel
    }
    if (k == (11 as i32)) {
        parcel.setMetaElement\<float>("..value", value as float)
        return parcel
    }
    if (k == (12 as i32)) {
        parcel.setMetaElement\<double>("..value", value as double)
        return parcel
    }
    parcel.setMetaElement\<String>("..value", value as String)
    return parcel
}

// 标量 wire 名（envelope typeName；sbKind 类别码 → BIL 别名形 token，
// 与 scalarSpecOf 接受的别名拼写同表——恢复端双拼写并收，本面恒产
// 别名形，与 typeNameOf 字面量同源）。
priv func scalarWireName(k: i32): String {
    if (k == (1 as i32)) { return ".bool" }
    if (k == (2 as i32)) { return ".char" }
    if (k == (3 as i32)) { return ".i8" }
    if (k == (4 as i32)) { return ".u8" }
    if (k == (5 as i32)) { return ".i16" }
    if (k == (6 as i32)) { return ".u16" }
    if (k == (7 as i32)) { return ".i32" }
    if (k == (8 as i32)) { return ".u32" }
    if (k == (9 as i32)) { return ".i64" }
    if (k == (10 as i32)) { return ".u64" }
    if (k == (11 as i32)) { return ".f32" }
    if (k == (12 as i32)) { return ".f64" }
    return ".string"
}

// envelope 记录构造：业务字段恒空，载荷进受控元数据 ..value 槽
// （§4.6.3 元数据隔离；与 SB 宿主 ..toParcel 的记录形态同构）。
priv func newEnvelopeParcel(typeName: String, payload: Array\<Any>):
        core.serialization.Parcel {
    const parcel = new core.serialization.Parcel(typeName)
    parcel.setMetaElement\<Array\<Any>>("..value", payload)
    return parcel
}

// 标量目标构造（§4.7.5 读侧）：整数目标只接受整数词法并检查符号与
// 范围（3.0/3e0 不作整数输入；禁止截断/回绕）；float/double 目标
// 按该精度直接解析数值词法并舍入（core.text 正确舍入路径），超出
// 有限范围报错；bool 只对应 JSON 布尔；char 恰一个 Unicode 标量。
priv func buildScalar(node: JsonNode, spec: JsonTypeSpec): Any {
    const k = spec.scalarKind
    if (k == (1 as i32)) {
        if (node.kind != (1 as i32)) {
            throw new JsonException("bool 目标期望 JSON 布尔值", node.offset)
        }
        const v: bool = node.boolValue
        return v
    }
    if (k == (2 as i32)) {
        if (node.kind != (3 as i32)) {
            throw new JsonException("char 目标期望 JSON 字符串", node.offset)
        }
        if (node.text.characterCount != (1 as i64)) {
            throw new JsonException(
                "char 目标要求恰为一个 Unicode 标量的字符串", node.offset)
        }
        return (node.text.characterAt((0 as i64)) if? ' ')
    }
    if (k == (13 as i32)) {
        if (node.kind != (3 as i32)) {
            throw new JsonException("String 目标期望 JSON 字符串", node.offset)
        }
        return node.text
    }
    if ((k == (11 as i32)) or (k == (12 as i32))) {
        if (node.kind != (2 as i32)) {
            throw new JsonException("浮点目标期望 JSON 数字", node.offset)
        }
        if (k == (11 as i32)) {
            try {
                return float.parse(node.text)
            } catch (e: core.text.NumberParseException) {
                throw new JsonException("浮点数值超出 float 有限范围",
                    node.offset)
            }
        }
        try {
            return double.parse(node.text)
        } catch (e2: core.text.NumberParseException) {
            throw new JsonException("浮点数值超出 double 有限范围", node.offset)
        }
    }
    // 整数目标（sbKind 3..10）。
    if (node.kind != (2 as i32)) {
        throw new JsonException("整数目标期望 JSON 数字", node.offset)
    }
    if (not node.isInteger) {
        throw new JsonException(
            "整数目标不接受小数/指数词法（3.0、3e0 不作整数输入）",
            node.offset)
    }
    return parseIntToken(node, k)
}

// 整数 token 按目标宽度解析（值域/符号错误统一报 JsonException 数值
// 范围族，携带 token 偏移）。
priv func parseIntToken(node: JsonNode, k: i32): Any {
    if (k == (3 as i32)) {
        try {
            return i8.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (4 as i32)) {
        try {
            return u8.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (5 as i32)) {
        try {
            return i16.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (6 as i32)) {
        try {
            return u16.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (7 as i32)) {
        try {
            return i32.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (8 as i32)) {
        try {
            return u32.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (9 as i32)) {
        try {
            return i64.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    } else if (k == (10 as i32)) {
        try {
            return u64.parse(node.text)
        } catch (e: core.text.NumberParseException) {
        }
    }
    throw new JsonException("整数数值超出目标类型范围或符号不符", node.offset)
}

// Map 目标（readAs）：带 Map 类型标识 + content → §4.7.3 包装解释；
// 普通对象 → 按字典读取（成员名全是业务键，.rigi.type-identifier /
// content 形式也不剥离，§4.7.1/§4.7.4）。K 非 String 的普通对象
// 报错（非 String 键目标 Map 不能从普通 JSON 对象恢复）。
priv func buildLiveMap(node: JsonNode, kSpec: JsonTypeSpec,
        vSpec: JsonTypeSpec, kRaw: String, vRaw: String): Any {
    const idNode = findMember(node, ".rigi.type-identifier")
    // 只有完整 Map 类型名才使 content 成为包装载荷；目标 String 键下，
    // 其他字符串值（包括普通文本及形似未闭合类型名）仍是业务键。
    // 真包装即便 content 缺失/类型错误也须交 buildMapContent 明确报错。
    if (idNode != null) {
        if (idNode.kind == (3 as i32)) {
            if (isCompleteMapIdentifier(idNode.text)) {
                return buildMapContent(node, kSpec, vSpec, kRaw, vRaw)
            }
        }
    }
    if ((kSpec.kind != (1 as i32)) or (kSpec.scalarKind != (13 as i32))) {
        throw new JsonException(
            "非 String 键的目标 Map 不能从普通 JSON 对象的字符串成员名恢复（§4.7.4）",
            node.offset)
    }
    const n = node.members.length
    const keys = core.collections.arrayOf\<Any?>((n as i32))
    const values = core.collections.arrayOf\<Any?>((n as i32))
    var i: i64 = (0 as i64)
    while (i < n) {
        const m = (node.members.getAtIndex(i) as JsonMember)
        keys[(i as i32)] = m.name
        values[(i as i32)] = buildValue(m.value, vSpec)
        i = (i + (1 as i64))
    }
    return core.serialization.sbBuildMap(kRaw, vRaw, keys, values)
}

// 自定义对象的 Parcel 构造（声明名兜底）：带标识用标识（实际类型，
// §4.7.2 多态恢复依据），无标识用声明名；字段值按动态规则（无声明
// 通道的未知类型/多余成员——缺失/多余/宽度不匹配交严格 fromParcel
// 判定）。
priv func buildObjectParcelWithSpec(node: JsonNode, defaultName: String):
        core.serialization.Parcel {
    const idNode = findMember(node, ".rigi.type-identifier")
    if (idNode == null) {
        return buildTypedParcel(node, defaultName)
    }
    if (idNode.kind != (3 as i32)) {
        throw new JsonException("类型标识成员必须是字符串", idNode.offset)
    }
    return buildTypedParcel(node, idNode.text)
}

// casesOf 按名重载的宽容入口：非枚举/未登记目标的 casesOf 抛
// IllegalArgumentException（反射查询未登记的类型），捕获后视为非
// 枚举（cases 空数组）——枚举判定只能靠反射 API 的异常面（同
// JsonSerializer.tryCasesOf 先例）。
priv func tryCasesOfName(typeName: String):
        Array\<core.serialization.EnumCaseInfo> {
    try {
        return core.serialization.casesOf(typeName)
    } catch (e: core.IllegalArgumentException) {
        return core.collections.arrayOf\<core.serialization.EnumCaseInfo>(0)
    }
}

// 嵌套自定义对象/枚举的类型引导 Parcel 构造（§4.7.1「递归到每个
// 成员时重复这一判断」，块 5-2c）：经按名反射（成员的
// FieldInfo.typeName → casesOf/fieldsOf 按名重载，§4.6.3 反射边界
// 同一候选集）取得嵌套类型元信息后递归走类型引导——嵌套 i32 直接
// 读成 i32、嵌套枚举按 case 声明引导（含互操作裸 case 名形态）、
// 嵌套嵌套同理（buildObjectTop 的成员槽递归回本路径）；容器元素/
// Map 键值声明中的自定义对象经 buildWire 的 parseTypeSpec 递归
// 结构进入本路径。未登记类型回退动态规则（buildObjectParcelWithSpec），
// 失败语义与现有面一致（缺失/多余/类型不匹配交严格 fromParcel，
// 形状错误 JsonException）。
// 递归安全：类型级递归引导由输入 JSON 节点驱动——自引用类型的展开
// 深度受 JsonParser 容器深度上限约束（默认 256），天然终止，无
// 类型图无限递归。
priv func buildNestedParcel(node: JsonNode, declaredName: String):
        core.serialization.Parcel {
    const cases = tryCasesOfName(declaredName)
    if (cases.length > (0 as i32)) {
        return buildEnumParcel(node, declaredName, cases)
    }
    try {
        const fields = core.serialization.fieldsOf(declaredName)
        return buildObjectTop(node, declaredName, fields)
    } catch (e: core.IllegalArgumentException) {
        return buildObjectParcelWithSpec(node, declaredName)
    }
}

// Parcel 字段槽 wire 形态构造（readAs 对象字段/枚举载荷按声明读取）：
// 集合字段产出统一载荷 Array\<Any>（List 元素序、Map 摊平键值交替序，
// §4.6.3——严格恢复的形状前提）；标量按声明宽度直接构造；嵌套对象
// 为 Parcel；元素/槽位级 JSON null 经顶部通道按声明可空性处理——
// 可空声明造形为与 EncodeOptionalElement 同形态的 wire null 哨兵记录
// （严格 fromParcel 判回真实 null），非可空声明在读取面即拒绝。
// 整字段 null 仍由调用方落 NullSentinel 槽（buildObjectTop/
// buildEnumParcel 的 m.value.kind 判定）。
priv func buildWire(node: JsonNode, spec: JsonTypeSpec): Any {
    if (node.kind == (0 as i32)) {
        if (spec.kind == (6 as i32)) {
            return core.serialization.Parcel.newNullSentinelWire()
        }
        throw new JsonException("非可空声明遇到 null", node.offset)
    }
    if (spec.kind == (6 as i32)) {
        return buildWire(node, specOr(spec.elem))
    }
    if (spec.kind == (0 as i32)) {
        return buildDynamic(node)
    }
    if (spec.kind == (1 as i32)) {
        return buildScalar(node, spec)
    }
    if ((spec.kind == (2 as i32)) or (spec.kind == (3 as i32))) {
        if (node.kind != (4 as i32)) {
            throw new JsonException("集合字段期望 JSON 数组", node.offset)
        }
        const n = (node.elements.length as i32)
        const arr = core.collections.arrayOf\<Any>(n)
        var i: i32 = 0
        while (i < n) {
            // 元素 null 由顶部通道按元素声明可空性处理。
            arr[i] = buildWire((node.elements.getAtIndex(i as i64) as JsonNode),
                specOr(spec.elem))
            i = (i + 1)
        }
        return arr
    }
    if (spec.kind == (4 as i32)) {
        // Map 字段的内部 wire 为交替键值数组；写侧已不输出此形态。
        // 读取保留对既有数组文本的兼容入口，新输出使用 JSON 对象。
        if (node.kind == (4 as i32)) {
            return buildMapWirePayload(node, spec)
        }
        if (node.kind != (5 as i32)) {
            throw new JsonException("Map 字段期望 JSON 对象或键值交替数组",
                node.offset)
        }
        return buildMapWire(node, spec)
    }
    // 自定义对象/枚举声明（kind 5）：递归类型引导（§4.7.1「递归到每个
    // 成员时重复这一判断」，块 5-2c）——经按名反射取得嵌套字段/case
    // 清单后按声明构造；未登记回退动态规则（形状/缺失/多余交严格
    // fromParcel 判定，与现有失败语义一致）。形状错误（对象字段遇
    // 非对象/非 case 名节点）由 buildNestedParcel 内的通道报错。
    return buildNestedParcel(node, spec.rawName)
}

// Map 字段载荷（摊平键值交替序）：content 包装或普通对象（K 必须
// String）；键/值按声明构造为 wire 形态。
priv func buildMapWire(node: JsonNode, spec: JsonTypeSpec): Array\<Any> {
    const idNode = findMember(node, ".rigi.type-identifier")
    var count: i64 = (0 as i64)
    var fromContent = false
    if (idNode != null) {
        if (idNode.kind == (3 as i32)) {
            if (isCompleteMapIdentifier(idNode.text)) {
                const contentNode = findMember(node, "content")
                if ((contentNode == null) or (contentNode.kind != (4 as i32))) {
                    throw new JsonException("Map 表示的 content 必须是数组",
                        node.offset)
                }
                fromContent = true
                count = (contentNode as JsonNode).elements.length
            }
        }
    }
    if (not fromContent) {
        if ((specOr(spec.key).kind != (1 as i32)) or
                (specOr(spec.key).scalarKind != (13 as i32))) {
            throw new JsonException(
                "非 String 键的目标 Map 不能从普通 JSON 对象的字符串成员名恢复（§4.7.4）",
                node.offset)
        }
        count = node.members.length
    }
    const arr = core.collections.arrayOf\<Any>((((count * (2 as i64))) as i32))
    var i: i64 = (0 as i64)
    while (i < count) {
        var keyNode: JsonNode? = null
        var valNode: JsonNode? = null
        if (fromContent) {
            const contentNode2 = (findMember(node, "content") as JsonNode)
            const entry = (contentNode2.elements.getAtIndex(i) as JsonNode)
            if (entry.kind != (5 as i32)) {
                throw new JsonException("Map content 条目必须是对象",
                    entry.offset)
            }
            if (entry.members.length != (2 as i64)) {
                throw new JsonException(
                    "Map content 条目必须恰好具有 key/value 两个成员",
                    entry.offset)
            }
            const m0 = (entry.members.getAtIndex((0 as i64)) as JsonMember)
            const m1 = (entry.members.getAtIndex((1 as i64)) as JsonMember)
            if ((m0.name == "key") and (m1.name == "value")) {
                keyNode = m0.value
                valNode = m1.value
            } else if ((m0.name == "value") and (m1.name == "key")) {
                keyNode = m1.value
                valNode = m0.value
            } else {
                throw new JsonException(
                    "Map content 条目成员必须是 key 与 value", entry.offset)
            }
        } else {
            const m = (node.members.getAtIndex(i) as JsonMember)
            keyNode = memberNameNode(m)
            valNode = m.value
        }
        const kw = buildWire((keyNode as JsonNode), specOr(spec.key))
        const vw = buildWire((valNode as JsonNode), specOr(spec.value))
        arr[((i * (2 as i64)) as i32)] = kw
        arr[(((i * (2 as i64)) + (1 as i64)) as i32)] = vw
        i = (i + (1 as i64))
    }
    return arr
}

// Map 字段历史数组文本兼容入口：旧写侧曾泄漏交替键值载荷，
// 新写侧不再产生；读取时逐对按 K/V 声明构造 wire 形态；长度
// 非偶即报错。键/值槽的 JSON null 经 buildWire 顶部通道按各自声明
// 可空性处理（可空→wire null 哨兵记录，非可空→JsonException）。
priv func buildMapWirePayload(node: JsonNode, spec: JsonTypeSpec): Array\<Any> {
    const count = node.elements.length
    if ((count % (2 as i64)) != (0 as i64)) {
        throw new JsonException("Map 字段的键值交替载荷长度非偶", node.offset)
    }
    const pairs = (count / (2 as i64))
    const arr = core.collections.arrayOf\<Any>((count as i32))
    var i: i64 = (0 as i64)
    while (i < pairs) {
        const keyEl = (node.elements.getAtIndex((i * (2 as i64))) as JsonNode)
        const valEl = (node.elements.getAtIndex(((i * (2 as i64)) + (1 as i64))) as JsonNode)
        arr[((i * (2 as i64)) as i32)] = buildWire(keyEl, specOr(spec.key))
        arr[(((i * (2 as i64)) + (1 as i64)) as i32)] = buildWire(valEl, specOr(spec.value))
        i = (i + (1 as i64))
    }
    return arr
}

// 普通对象成员名的 String 节点（Map 字段摊平时的键槽；成员名已是
// 转义还原后的业务键）。
priv func memberNameNode(m: JsonMember): JsonNode {
    const node = new JsonNode()
    node.kind = (3 as i32)
    node.text = m.name
    return node
}

// readAs 顶层对象构建（静态字段闭包引导，§4.7.1）：类型标识优先
// （实际类型，多态恢复依据）；成员按反射字段声明逐槽构造（宽度/可空
// /集合形状依声明）；JSON 中缺少的成员不落槽、多余成员动态落槽——
// 缺失/多余/null 进非可空的最终判定由严格 fromParcel（§4.6.3）承担，
// 读取面不做宽松默认值。
priv func buildObjectTop(node: JsonNode, targetName: String,
        fields: Array\<core.serialization.FieldInfo>):
        core.serialization.Parcel {
    if (node.kind != (5 as i32)) {
        throw new JsonException("对象目标期望 JSON 对象", node.offset)
    }
    var typeName = targetName
    const idNode = findMember(node, ".rigi.type-identifier")
    if (idNode != null) {
        if (idNode.kind != (3 as i32)) {
            throw new JsonException("类型标识成员必须是字符串", idNode.offset)
        }
        typeName = idNode.text
    }
    const parcel = new core.serialization.Parcel(typeName)
    const caseNode = findMember(node, ".rigi.enum-case")
    if (caseNode != null) {
        if (caseNode.kind != (3 as i32)) {
            throw new JsonException("枚举 case 成员必须是字符串",
                caseNode.offset)
        }
        parcel.setMetaElement\<String>("..case", caseNode.text)
    }
    var i: i64 = (0 as i64)
    while (i < node.members.length) {
        const m = (node.members.getAtIndex(i) as JsonMember)
        if ((m.name == ".rigi.type-identifier") or
                (m.name == ".rigi.enum-case")) {
            i = (i + (1 as i64))
            continue
        }
        if (m.value.kind == (0 as i32)) {
            putParcelField(parcel, m.name, null)
            i = (i + (1 as i64))
            continue
        }
        const fi = findFieldInfo(fields, m.name)
        if (fi.name == "?") {
            putParcelField(parcel, m.name, buildDynamic(m.value))
        } else {
            const fspec = fieldSpecOf(fi)
            putParcelField(parcel, m.name, buildWire(m.value, fspec))
        }
        i = (i + (1 as i64))
    }
    return parcel
}

// 字段声明 → 规格（nullable 包装为 kind 6）。
priv func fieldSpecOf(fi: core.serialization.FieldInfo): JsonTypeSpec {
    const base = parseTypeSpec(fi.typeName)
    if (fi.nullable) {
        if (base.kind == (6 as i32)) {
            return base
        }
        return newNullableSpec(base)
    }
    return base
}

// 字段查找（线性；未命中返回 name="?" 的哨兵——FieldInfo 是 struct
// 无 null 形态，哨兵避开可空结构判定）。
priv func findFieldInfo(fields: Array\<core.serialization.FieldInfo>,
        name: String): core.serialization.FieldInfo {
    var i: i64 = (0 as i64)
    while (i < (fields.length as i64)) {
        const fi = (fields[(i as i32)] if?
            (new core.serialization.FieldInfo("?", ".any", true)))
        if (fi.name == name) {
            return fi
        }
        i = (i + (1 as i64))
    }
    return new core.serialization.FieldInfo("?", ".any", true)
}

// readAs 枚举目标构建（§4.7.4 读侧 + 保留类型信息形态）：校验 case
// 存在（casesOf 清单）；无载荷 case 的互操作形态是 case 名字符串；
// 带载荷是 { "case": 名, ...载荷 }；保留类型信息形态是
// { .rigi.type-identifier, .rigi.enum-case, ...载荷 }。格式成员 case
// 转为枚举元数据（meta ..case）后再严格业务字段匹配（载荷按 case
// 的反射字段声明读取）。枚举自带 case 载荷字段时互操作形态成员名
// 冲突，读写均报错，不覆盖或改名。
priv func buildEnumParcel(node: JsonNode, targetName: String,
        cases: Array\<core.serialization.EnumCaseInfo>):
        core.serialization.Parcel {
    if (node.kind == (3 as i32)) {
        const info = findCaseInfo(cases, node.text)
        if (info.name == "?") {
            throw new JsonException("未知枚举 case：${node.text}", node.offset)
        }
        if (info.fields.length > (0 as i32)) {
            throw new JsonException(
                "case ${node.text} 带载荷，互操作形态应为 case 对象而非裸字符串",
                node.offset)
        }
        const parcel0 = new core.serialization.Parcel(targetName)
        parcel0.setMetaElement\<String>("..case", node.text)
        return parcel0
    }
    if (node.kind != (5 as i32)) {
        throw new JsonException("枚举目标期望 case 名字符串或 case 对象",
            node.offset)
    }
    const idNode = findMember(node, ".rigi.type-identifier")
    var typeName = targetName
    if (idNode != null) {
        if (idNode.kind != (3 as i32)) {
            throw new JsonException("类型标识成员必须是字符串", idNode.offset)
        }
        typeName = idNode.text
    }
    var caseName: String = ""
    if (idNode != null) {
        const caseNode = findMember(node, ".rigi.enum-case")
        if ((caseNode == null) or (caseNode.kind != (3 as i32))) {
            throw new JsonException(
                "保留类型信息的枚举形态缺少 .rigi.enum-case 成员", node.offset)
        }
        caseName = caseNode.text
    } else {
        if (anyCaseHasCaseField(cases)) {
            throw new JsonException(
                "枚举载荷字段与互操作输出的 case 成员名冲突，不覆盖或改名（§4.7.4）",
                node.offset)
        }
        const caseMember = findMember(node, "case")
        if ((caseMember == null) or (caseMember.kind != (3 as i32))) {
            throw new JsonException("互操作枚举形态缺少 String 成员 case",
                node.offset)
        }
        caseName = caseMember.text
    }
    const info = findCaseInfo(cases, caseName)
    if (info.name == "?") {
        throw new JsonException("未知枚举 case：${caseName}", node.offset)
    }
    const parcel = new core.serialization.Parcel(typeName)
    parcel.setMetaElement\<String>("..case", caseName)
    var i: i64 = (0 as i64)
    while (i < node.members.length) {
        const m = (node.members.getAtIndex(i) as JsonMember)
        if (((m.name == ".rigi.type-identifier") or
                (m.name == ".rigi.enum-case")) or (m.name == "case")) {
            i = (i + (1 as i64))
            continue
        }
        if (m.value.kind == (0 as i32)) {
            putParcelField(parcel, m.name, null)
            i = (i + (1 as i64))
            continue
        }
        const fi = findFieldInfo(info.fields, m.name)
        if (fi.name == "?") {
            putParcelField(parcel, m.name, buildDynamic(m.value))
        } else {
            const fspec = fieldSpecOf(fi)
            putParcelField(parcel, m.name, buildWire(m.value, fspec))
        }
        i = (i + (1 as i64))
    }
    return parcel
}

// case 查找（线性；未命中返回 name="?" 哨兵）。
priv func findCaseInfo(cases: Array\<core.serialization.EnumCaseInfo>,
        name: String): core.serialization.EnumCaseInfo {
    var i: i64 = (0 as i64)
    while (i < (cases.length as i64)) {
        const c = (cases[(i as i32)] if?
            (new core.serialization.EnumCaseInfo("?",
                core.collections.arrayOf\<core.serialization.FieldInfo>(0))))
        if (c.name == name) {
            return c
        }
        i = (i + (1 as i64))
    }
    return new core.serialization.EnumCaseInfo("?",
        core.collections.arrayOf\<core.serialization.FieldInfo>(0))
}

// 枚举任 case 是否自带名为 case 的载荷字段（互操作形态冲突判定）。
priv func anyCaseHasCaseField(
        cases: Array\<core.serialization.EnumCaseInfo>): bool {
    var i: i64 = (0 as i64)
    while (i < (cases.length as i64)) {
        const c = (cases[(i as i32)] if?
            (new core.serialization.EnumCaseInfo("?",
                core.collections.arrayOf\<core.serialization.FieldInfo>(0))))
        var j: i64 = (0 as i64)
        while (j < (c.fields.length as i64)) {
            const fi = (c.fields[(j as i32)] if?
                (new core.serialization.FieldInfo("?", ".any", true)))
            if (fi.name == "case") {
                return true
            }
            j = (j + (1 as i64))
        }
        i = (i + (1 as i64))
    }
    return false
}
