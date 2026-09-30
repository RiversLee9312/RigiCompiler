// Rigi 标准库：序列化修饰器（MW11d Phase A）。
// SerializationBase 声明在 core，应用限于标准库命名空间树。
// Serializable 的 toParcel/fromParcel 暴露面属 Phase B。
// Temporary 是字段 Value wrapper，懒恢复全源码实现，零编译器魔法。
namespace core.serialization

// 内部动态值解码仍由 Serializable 的真实类型实现承担；未知类型必须拒绝。
priv func decodeAnyValue(parcel: Parcel, context: SerializationGraphContext, id: i64): Any {
    throw new IllegalStateException("未登记的序列化类型")
}

// 编译器递归入口专用；顶层 private 限定为本标准库文件可见，用户不能
// 构造或传递此上下文。Place<Object> 统一引用类型身份，不暴露地址或哈希。
priv class SerializationGraphContext {
    pub const enabled: bool
    priv var places: core.collections.List\<core.Place\<Object>>
    priv var decoded: core.collections.List\<Object>
    priv var depth: i32

    pub init(_ -> enabled) {
        places = new core.collections.List\<core.Place\<Object>>()
        decoded = new core.collections.List\<Object>()
        depth = 0
    }

    // 每个合成对象编解码方法占一帧。把限制放在共享上下文而非宿主
    // 调用栈探测上，可令 VM/native 在栈耗尽前以同一语言异常失败。
    pub func enterFrame() {
        if (depth >= 256) {
            throw new core.IllegalStateException("序列化对象嵌套深度超过 256")
        }
        depth = depth + 1
    }

    pub func leaveFrame() {
        if (depth > 0) { depth = depth - 1 }
    }

    // 正数为已存在节点，负数为刚登记节点。树模式只保存活动路径。
    pub func enter(value: Any): i64 {
        // 开放 Serializable 型参也可具化为值类型，值本身不参与身份表。
        if (value is ValueType) { return 0L }
        const reference = value as Object
        const candidate = placeOf reference
        var i: i64 = 0L
        while (i < places.length) {
            if (candidate == (places.getAtIndex(i) as core.Place\<Object>)) {
                candidate.dispose()
                if (not enabled) {
                    throw new core.IllegalStateException("检测到环引用，需显式启用 loopedRefEnabled")
                }
                return i + 1L
            }
            i = i + 1L
        }
        places.add(candidate)
        return 0L - places.length
    }

    pub func leave(id: i64) {
        if ((not enabled) and (id < 0L)) {
            const last = places.length - 1L
            (places.getAtIndex(last) as core.Place\<Object>).dispose()
            places.removeAt(last)
        }
    }

    pub func isNewNode(id: i64): bool { return enabled and (id < 0L) }

    // 模式标记（..ref 等）只存于 Parcel 受控元数据槽（§4.6.3 元数据
    // 隔离），判别只扫元数据表，业务字段表不参与。
    pub func validateParcelMode(parcel: Parcel, marker: String) {
        var marked = false
        var i: i64 = 0L
        while (i < parcel.metaElementCount()) {
            if ((parcel.metaKeyAtIndex(i) as String) == marker) { marked = true }
            i = i + 1L
        }
        if (marked != enabled) {
            throw new core.IllegalStateException(
                "序列化 wire 模式与 loopedRefEnabled 不匹配")
        }
    }

    // 开放 T 运行期既可能是引用类型（图 envelope），也可能是值类型
    // （即使 enabled 也直接编码其 Parcel）。仅把 i64 的保留标记认作
    // envelope；元数据隔离后业务键不可能与保留键同名，i64 判别保留为
    // wire 形状兜底。
    pub func isGraphParcel(parcel: Parcel, marker: String): bool {
        var i: i64 = 0L
        while (i < parcel.metaElementCount()) {
            if ((parcel.metaKeyAtIndex(i) as String) == marker) {
                const graphMarker = parcel.metaValueAtIndex(i) is i64
                if (graphMarker and (not enabled)) {
                    throw new core.IllegalStateException(
                        "序列化 wire 模式与 loopedRefEnabled 不匹配")
                }
                return graphMarker
            }
            i = i + 1L
        }
        return false
    }

    pub func remember(id: i64, value: Object) {
        if (id != (decoded.length + 1L)) {
            throw new core.IllegalStateException("无效或重复的序列化节点编号")
        }
        decoded.add(value)
    }

    pub func resolve(id: i64): Object {
        if ((id < 1L) or (id > decoded.length)) {
            throw new core.IllegalStateException("序列化引用指向不存在的节点")
        }
        return decoded.getAtIndex(id - 1L) as Object
    }

    pub func referenceId(id: i64): i64 {
        if (id < 0L) { throw new core.IllegalStateException("无效的序列化引用编号") }
        return id
    }

    pub func dispose() {
        var i: i64 = 0L
        while (i < places.length) {
            (places.getAtIndex(i) as core.Place\<Object>).dispose()
            i = i + 1L
        }
    }
}

/**
 * 可序列化实体修饰器（MW11d-B2）。
 * 暴露面（归属本 wrapper，源码层 toParcel 经编译器改写转发宿主 ..toParcel）：
 *   obj:Serializable.toParcel(loopedRefEnabled: bool = false) → Parcel
 *   fromParcel\<T with Serializable\>(parcel, loopedRefEnabled: bool = false) → T
 *   deepCopy\<T with Serializable\>(value, loopedRefEnabled: bool = false) → T
 *   obj:Serializable.deepCopy(loopedRefEnabled: bool = false) → 宿主类型
 * 默认树模式将重复引用分别复制，活动路径遇环抛 IllegalStateException。
 * true 图模式通过节点/引用记录保留复制图内部的别名和环，值类型不登记身份。
 * toParcel 快照不别名源图任何可变部分（数组/列表/Map/对象全部新建）。
 */
@WrapperTarget(.Entity)
pub shared wrapper Serializable {
    pub init()
}

@WrapperTarget(.Value)
@Terminal
pub rich wrapper Temporary\<TField> {
    priv var materialized: bool = false
    priv var cached: TField?
    priv var resumeStub: core.Func\<TField>

    pub init(resume: core.Func\<TField>) {
        resumeStub = resume
        materialized = false
    }

    operator .proxy.get\<TValue>(value: TValue): TValue {
        if (not materialized) {
            cached = resumeStub()
            materialized = true
        }
        return ((cached if? resumeStub()) as TValue)
    }

    operator .proxy.set\<TValue>(value: TValue) {
        cached = (value as TField)
        materialized = true
        inner(value)
    }
}

/**
 * 序列化对象的通用中间表示（MW11d-B1）。
 * getElement：absent key 抛 NoSuchElementException；存入的 null 返回 null。
 * 取回值从 Any 槽 cast 到 T——类型不符抛 CastException（预期）。
 * 嵌套 Parcel 合法：本类标 @SerializationBase，Parcel/Parcel? 可作 element。
 *
 * 元数据隔离（§4.6.3 / D3）：类型判别（typeName）、枚举 case、基元/集合
 * 载荷（..value）与图引用 envelope（..id/..ref/..data）只进受控元数据槽
 * meta，不进入公开业务字段表；elementCount/keyAtIndex/valueAtIndex/iterate
 * 只反映业务字段 data。meta 的读写面为 internal 级（§16.1 单编译单元
 * 恒可见），供序列化合成器与格式层使用，不承诺为用户动态面。
 *
 * 动态访问面（§4.6.1 / D3）：getDynamic/contains/setDynamic 以 Any? 表达
 * 动态根值——读取与迭代返回真实 null（NullSentinel 不外泄）；存在性查询
 * 区分「键不存在」与「键存在且值为 null」；动态写入执行与 setElement
 * 相同的字段名合法性校验与 SB 值检查（非 null 值必须是格式支持的 SB
 * 表示：标量/String/char/bool/整数/浮点/Array/List/Map/Parcel，内容经
 * 类型名递归校验，不接受任意业务对象）。
 */
@SerializationBase()
@Serializable()
pub class Parcel implements core.collections.IEnumerable\<core.Pair\<String, Any?>> {
    @Serializable()
    priv class NullSentinel {
        pub init()
    }

    pub const typeName: String
    priv var data: core.collections.Map\<String, Any>
    // meta 与 data 同生命周期：Parcel 自身经合成编解码往返（record 的
    // meta 槽走 EncodeValue/DecodeValue 通用通道），可空化会让
    // .nullable<.any> → .any 的槽写入在 native 管线抛 CastException
    // （b4-1 实测：内嵌容器图快照用例 native=1 vm=0）。树模式白付的
    // 一次空表分配远小于此风险，保持非空。
    priv var meta: core.collections.Map\<String, Any>

    pub init(_ -> typeName) {
        data = new core.collections.Map\<String, Any>()
        meta = new core.collections.Map\<String, Any>()
    }

    pub func getElement\<T with SerializationBase>(key: String): T? {
        if (data.containsKey(key)) {
            const boxed = data.tryGet(key)
            if (boxed is NullSentinel) {
                return null
            }
            if (boxed == null) {
                return null
            }
            return (boxed as T)
        } else {
            throw new core.NoSuchElementException("Parcel 中不存在键：${key}")
        }
    }

    // 字段键契约（§4.6.3 / D3）：只接受合法 Rigi 字段名，校验先于写入，
    // 非法键不落业务表。内部元数据键（..value/..case/..id/..ref/..data）
    // 经 setMetaElement 通道，不受本规则约束。
    pub func setElement\<T with SerializationBase>(key: String, element: T?) {
        if (not isLegalParcelFieldKey(key)) {
            throw new core.IllegalArgumentException(
                "Parcel 字段键不是合法 Rigi 字段名：${key}")
        }
        if (element == null) {
            data.set(key, (new NullSentinel() as Any))
        } else {
            data.set(key, (element as Any))
        }
    }

    // 存在性查询（§4.6.1 / D3）：区分「键不存在」与「键存在且值为
    // null」——contains 只看业务字段表键成员，不触值。
    pub func contains(key: String): bool {
        return data.containsKey(key)
    }

    // 动态读取（§4.6.1 / D3）：返回存储值，存入的 null 返回真实 null
    // （NullSentinel 不外泄）；键不存在抛 core.NoSuchElementException，
    // 与类型化 getElement 的 absent 行为一致。
    pub func getDynamic(key: String): Any? {
        if (data.containsKey(key)) {
            const boxed = data.tryGet(key)
            if (boxed is NullSentinel) {
                return null
            }
            if (boxed == null) {
                return null
            }
            return boxed
        }
        throw new core.NoSuchElementException("Parcel 中不存在键：${key}")
    }

    // 动态写入（§4.6.1 / D3）：与类型化 setElement 执行相同的字段名
    // 合法性校验（复用 isLegalParcelFieldKey）与 SB 值检查（非 null 值
    // 必须是格式支持的 SB 表示，经 isSbRepresentable 按类型名递归验证；
    // 不接受任意业务对象）。写入后类型化 getElement 可读回。
    // null 判定留在本方法完成：SB 值检查经 Any（非可空）形参转发——
    // native 后端对可空 Any 槽直接取 typeof 存在收窄缺陷（b4-2 实测
    // 抛 CastException，VM 正常；对非可空 Any 形参取 typeof 双宿主
    // 一致），收窄后的值以 Any 形参传递则两宿主都正确。
    pub func setDynamic(key: String, value: Any?) {
        if (not isLegalParcelFieldKey(key)) {
            throw new core.IllegalArgumentException(
                "Parcel 字段键不是合法 Rigi 字段名：${key}")
        }
        if (value == null) {
            data.set(key, (new NullSentinel() as Any))
            return
        }
        putDynamicChecked(key, value)
    }

    // setDynamic 的 null 排除后路径：SB 值检查 + 落业务表。
    priv func putDynamicChecked(key: String, value: Any) {
        if (not isSbRepresentable(value)) {
            throw new core.IllegalArgumentException(
                "Parcel 动态值不是格式支持的 SB 表示：${typeOf(value).toString()}")
        }
        data.set(key, value)
    }

    pub func elementCount(): i64 {
        return data.count
    }

    pub func keyAtIndex(index: i64): String? {
        return data.keyAtIndex(index)
    }

    // valueAtIndex 与迭代均返回真实 null（§4.6.1 / D3）：NullSentinel
    // 在公开面解包为 null，不暴露内部哨兵。
    pub func valueAtIndex(index: i64): Any? {
        const boxed = data.valueAtIndex(index)
        if (boxed is NullSentinel) {
            return null
        }
        if (boxed == null) {
            return null
        }
        return boxed
    }

    pub override func iterate(): core.collections.IEnumerator\<core.Pair\<String, Any?>> {
        return new ParcelEntryEnumerator(data.iterate())
    }

    // 受控元数据读写面：与业务字段同语义（absent 抛 NoSuchElement、
    // null 经 NullSentinel 哨兵往返），但只作用于 meta 槽。元数据键是
    // 合成器保留拼写（..value/..case/..id/..ref/..data），不受业务
    // 字段键合法性规则约束。
    internal func getMetaElement\<T with SerializationBase>(key: String): T? {
        if (meta.containsKey(key)) {
            const boxed = meta.tryGet(key)
            if (boxed is NullSentinel) {
                return null
            }
            if (boxed == null) {
                return null
            }
            return (boxed as T)
        } else {
            throw new core.NoSuchElementException("Parcel 元数据中不存在键：${key}")
        }
    }

    internal func setMetaElement\<T with SerializationBase>(key: String, element: T?) {
        if (element == null) {
            meta.set(key, (new NullSentinel() as Any))
        } else {
            meta.set(key, (element as Any))
        }
    }

    internal func metaElementCount(): i64 {
        return meta.count
    }

    internal func metaKeyAtIndex(index: i64): String? {
        return meta.keyAtIndex(index)
    }

    internal func metaValueAtIndex(index: i64): Any? {
        return meta.valueAtIndex(index)
    }

    // ── wire null 哨兵受控面（internal 级，§16.1 单编译单元恒可见，
    // 供同编译单元的格式层 core.serialization.json 使用）──
    //
    // 可空集合元素/槽位的 null 在 wire 载荷（.array<.any>）中的表示：
    // 合成器 EncodeOptionalElement 把它编码为「typeName = NullSentinel
    // wire 名的空 Parcel 记录」，严格恢复 DecodeOptionalElement 按
    // 同名判回真实 null（非可空声明经 StripSlotToAny/StrictWireCheck
    // 拒绝）。本受控面把该表示的构造与判别开放给格式层：读侧把按
    // 声明可空的 JSON null 造形为同一 wire 记录，写侧把该记录还原为
    // 格式 null（格式边界不泄漏哨兵文本）。NullSentinel 类保持 priv
    // 不进用户公开面。wire 名经运行时 typeOf 视图的 toString 取得，
    // 与合成器 WireTypeName(NullSentinel) 的 typeof 视图同源同拼写，
    // 两宿主拼写差异（VM BIL 别名形 / native canonical 形）天然一致。

    // wire null 哨兵记录的 wire 名。
    internal static func nullSentinelWireName(): String {
        return typeOf(new NullSentinel()).toString()
    }

    // 构造与 EncodeOptionalElement 同形态的 wire null 哨兵记录
    // （仅 typeName，无业务字段与元数据）。
    internal static func newNullSentinelWire(): Parcel {
        return new Parcel(nullSentinelWireName())
    }

    // 判别：本记录是否为 wire null 哨兵记录（按 typeName 同名判定，
    // 与 DecodeOptionalElement 同口径）。
    internal func isNullSentinelWire(): bool {
        return typeName == Parcel.nullSentinelWireName()
    }

    // 业务字段迭代适配器（§4.6.1 / D3）：项类型 Pair<String, Any?>，
    // 值为内部 NullSentinel 的槽在公开面还原为真实 null。
    priv class ParcelEntryEnumerator implements
            core.collections.IEnumerator\<core.Pair\<String, Any?>> {
        priv var source: core.collections.IEnumerator\<core.Pair\<String, Any>>

        pub init(_ -> source) {}

        pub override func moveNext(): bool {
            return source.moveNext()
        }

        pub override func current(): core.Pair\<String, Any?> {
            const entry = source.current()
            if (entry.value is NullSentinel) {
                return new core.Pair\<String, Any?>(entry.key, null)
            }
            return new core.Pair\<String, Any?>(entry.key, entry.value)
        }
    }
}

/**
 * Serializable 暴露面：由 parcel 重建 T。
 * 体由编译器填充（构造通道建 T 实例 + 调 ..fromParcel 覆盖）。
 */
pub func fromParcel\<T with Serializable>(parcel: Parcel, loopedRefEnabled: bool = false): T {
    throw new core.IllegalStateException("fromParcel 应由编译器填充")
}

/**
   * 深复制：toParcel 快照再 fromParcel 重建；Serializable 对 SB 宿主使用基元/容器编码。
   */
pub func deepCopy\<T with Serializable>(value: T, loopedRefEnabled: bool = false): T {
    return fromParcel\<T>(value:Serializable.toParcel(loopedRefEnabled), loopedRefEnabled)
}

// 字段名分类 BMP 真值：Lexer/IdentifierCharacters.cs 是单一数据源，
// tools/Generate-IdentifierCharacters.py 机械生成下面四表，并全 BMP 哈希
// 锁定 Windows .NET 10.0.11 / Linux .NET 10.0.12 的 char 分类。
// BEGIN GENERATED BMP RANGES
priv var identifierLetterStarts: Array\<i64> = core.collections.arrayOfElements\<i64>(65, 97, 170, 181, 186, 192, 216, 248, 710, 736, 748, 750, 880, 886, 890, 895, 902, 904, 908, 910, 931, 1015, 1162, 1329, 1369, 1376, 1488, 1519, 1568, 1646, 1649, 1749, 1765, 1774, 1786, 1791, 1808, 1810, 1869, 1969, 1994, 2036, 2042, 2048, 2074, 2084, 2088, 2112, 2144, 2160, 2185, 2208, 2308, 2365, 2384, 2392, 2417, 2437, 2447, 2451, 2474, 2482, 2486, 2493, 2510, 2524, 2527, 2544, 2556, 2565, 2575, 2579, 2602, 2610, 2613, 2616, 2649, 2654, 2674, 2693, 2703, 2707, 2730, 2738, 2741, 2749, 2768, 2784, 2809, 2821, 2831, 2835, 2858, 2866, 2869, 2877, 2908, 2911, 2929, 2947, 2949, 2958, 2962, 2969, 2972, 2974, 2979, 2984, 2990, 3024, 3077, 3086, 3090, 3114, 3133, 3160, 3165, 3168, 3200, 3205, 3214, 3218, 3242, 3253, 3261, 3293, 3296, 3313, 3332, 3342, 3346, 3389, 3406, 3412, 3423, 3450, 3461, 3482, 3507, 3517, 3520, 3585, 3634, 3648, 3713, 3716, 3718, 3724, 3749, 3751, 3762, 3773, 3776, 3782, 3804, 3840, 3904, 3913, 3976, 4096, 4159, 4176, 4186, 4193, 4197, 4206, 4213, 4238, 4256, 4295, 4301, 4304, 4348, 4682, 4688, 4696, 4698, 4704, 4746, 4752, 4786, 4792, 4800, 4802, 4808, 4824, 4882, 4888, 4992, 5024, 5112, 5121, 5743, 5761, 5792, 5873, 5888, 5919, 5952, 5984, 5998, 6016, 6103, 6108, 6176, 6272, 6279, 6314, 6320, 6400, 6480, 6512, 6528, 6576, 6656, 6688, 6823, 6917, 6981, 7043, 7086, 7098, 7168, 7245, 7258, 7296, 7312, 7357, 7401, 7406, 7413, 7418, 7424, 7680, 7960, 7968, 8008, 8016, 8025, 8027, 8029, 8031, 8064, 8118, 8126, 8130, 8134, 8144, 8150, 8160, 8178, 8182, 8305, 8319, 8336, 8450, 8455, 8458, 8469, 8473, 8484, 8486, 8488, 8490, 8495, 8508, 8517, 8526, 8579, 11264, 11499, 11506, 11520, 11559, 11565, 11568, 11631, 11648, 11680, 11688, 11696, 11704, 11712, 11720, 11728, 11736, 11823, 12293, 12337, 12347, 12353, 12445, 12449, 12540, 12549, 12593, 12704, 12784, 13312, 19968, 42192, 42240, 42512, 42538, 42560, 42623, 42656, 42775, 42786, 42891, 42960, 42963, 42965, 42994, 43011, 43015, 43020, 43072, 43138, 43250, 43259, 43261, 43274, 43312, 43360, 43396, 43471, 43488, 43494, 43514, 43520, 43584, 43588, 43616, 43642, 43646, 43697, 43701, 43705, 43712, 43714, 43739, 43744, 43762, 43777, 43785, 43793, 43808, 43816, 43824, 43868, 43888, 44032, 55216, 55243, 63744, 64112, 64256, 64275, 64285, 64287, 64298, 64312, 64318, 64320, 64323, 64326, 64467, 64848, 64914, 65008, 65136, 65142, 65313, 65345, 65382, 65474, 65482, 65490, 65498)
priv var identifierLetterEnds: Array\<i64> = core.collections.arrayOfElements\<i64>(90, 122, 170, 181, 186, 214, 246, 705, 721, 740, 748, 750, 884, 887, 893, 895, 902, 906, 908, 929, 1013, 1153, 1327, 1366, 1369, 1416, 1514, 1522, 1610, 1647, 1747, 1749, 1766, 1775, 1788, 1791, 1808, 1839, 1957, 1969, 2026, 2037, 2042, 2069, 2074, 2084, 2088, 2136, 2154, 2183, 2190, 2249, 2361, 2365, 2384, 2401, 2432, 2444, 2448, 2472, 2480, 2482, 2489, 2493, 2510, 2525, 2529, 2545, 2556, 2570, 2576, 2600, 2608, 2611, 2614, 2617, 2652, 2654, 2676, 2701, 2705, 2728, 2736, 2739, 2745, 2749, 2768, 2785, 2809, 2828, 2832, 2856, 2864, 2867, 2873, 2877, 2909, 2913, 2929, 2947, 2954, 2960, 2965, 2970, 2972, 2975, 2980, 2986, 3001, 3024, 3084, 3088, 3112, 3129, 3133, 3162, 3165, 3169, 3200, 3212, 3216, 3240, 3251, 3257, 3261, 3294, 3297, 3314, 3340, 3344, 3386, 3389, 3406, 3414, 3425, 3455, 3478, 3505, 3515, 3517, 3526, 3632, 3635, 3654, 3714, 3716, 3722, 3747, 3749, 3760, 3763, 3773, 3780, 3782, 3807, 3840, 3911, 3948, 3980, 4138, 4159, 4181, 4189, 4193, 4198, 4208, 4225, 4238, 4293, 4295, 4301, 4346, 4680, 4685, 4694, 4696, 4701, 4744, 4749, 4784, 4789, 4798, 4800, 4805, 4822, 4880, 4885, 4954, 5007, 5109, 5117, 5740, 5759, 5786, 5866, 5880, 5905, 5937, 5969, 5996, 6000, 6067, 6103, 6108, 6264, 6276, 6312, 6314, 6389, 6430, 6509, 6516, 6571, 6601, 6678, 6740, 6823, 6963, 6988, 7072, 7087, 7141, 7203, 7247, 7293, 7306, 7354, 7359, 7404, 7411, 7414, 7418, 7615, 7957, 7965, 8005, 8013, 8023, 8025, 8027, 8029, 8061, 8116, 8124, 8126, 8132, 8140, 8147, 8155, 8172, 8180, 8188, 8305, 8319, 8348, 8450, 8455, 8467, 8469, 8477, 8484, 8486, 8488, 8493, 8505, 8511, 8521, 8526, 8580, 11492, 11502, 11507, 11557, 11559, 11565, 11623, 11631, 11670, 11686, 11694, 11702, 11710, 11718, 11726, 11734, 11742, 11823, 12294, 12341, 12348, 12438, 12447, 12538, 12543, 12591, 12686, 12735, 12799, 19903, 42124, 42237, 42508, 42527, 42539, 42606, 42653, 42725, 42783, 42888, 42957, 42961, 42963, 42972, 43009, 43013, 43018, 43042, 43123, 43187, 43255, 43259, 43262, 43301, 43334, 43388, 43442, 43471, 43492, 43503, 43518, 43560, 43586, 43595, 43638, 43642, 43695, 43697, 43702, 43709, 43712, 43714, 43741, 43754, 43764, 43782, 43790, 43798, 43814, 43822, 43866, 43881, 44002, 55203, 55238, 55291, 64109, 64217, 64262, 64279, 64285, 64296, 64310, 64316, 64318, 64321, 64324, 64433, 64829, 64911, 64967, 65019, 65140, 65276, 65338, 65370, 65470, 65479, 65487, 65495, 65500)
priv var identifierDigitStarts: Array\<i64> = core.collections.arrayOfElements\<i64>(48, 1632, 1776, 1984, 2406, 2534, 2662, 2790, 2918, 3046, 3174, 3302, 3430, 3558, 3664, 3792, 3872, 4160, 4240, 6112, 6160, 6470, 6608, 6784, 6800, 6992, 7088, 7232, 7248, 42528, 43216, 43264, 43472, 43504, 43600, 44016, 65296)
priv var identifierDigitEnds: Array\<i64> = core.collections.arrayOfElements\<i64>(57, 1641, 1785, 1993, 2415, 2543, 2671, 2799, 2927, 3055, 3183, 3311, 3439, 3567, 3673, 3801, 3881, 4169, 4249, 6121, 6169, 6479, 6617, 6793, 6809, 7001, 7097, 7241, 7257, 42537, 43225, 43273, 43481, 43513, 43609, 44025, 65305)
// END GENERATED BMP RANGES

// UTF-16 字符单元口径：当前前端不把补充平面标量合并为 WordToken，
// Parcel 对应拒绝 > U+FFFF；ASCII 键仍只经快速字节扫描。
priv func isLegalParcelFieldKey(key: String): bool {
    const bytes = key.toUtf8Span()
    const blen: i64 = (bytes.length as i64)
    if (blen == (0 as i64)) { return false }
    var i: i64 = (0 as i64)
    while (i < blen) {
        const c = ((bytes[(i as i32)] if? (0 as u8)) as i64)
        if (c < (128 as i64)) {
            const letter = (((c >= (65 as i64)) and (c <= (90 as i64))) or
                ((c >= (97 as i64)) and (c <= (122 as i64))))
            const digit = ((c >= (48 as i64)) and (c <= (57 as i64)))
            if ((not letter) and (c != (95 as i64))) {
                if ((i == (0 as i64)) or (not digit)) { return false }
            }
            i = i + (1 as i64)
        } else {
            const head = (i == (0 as i64))
            var cp: i64 = (0 as i64)
            if ((c >= (194 as i64)) and (c <= (223 as i64))) {
                if ((i + (1 as i64)) >= blen) { return false }
                const b1 = ((bytes[((i + (1 as i64)) as i32)] if? (0 as u8)) as i64)
                if ((b1 < (128 as i64)) or (b1 > (191 as i64))) { return false }
                cp = (((c - (192 as i64)) * (64 as i64)) + (b1 - (128 as i64)))
                i = i + (2 as i64)
            } else if ((c >= (224 as i64)) and (c <= (239 as i64))) {
                if ((i + (2 as i64)) >= blen) { return false }
                const b1 = ((bytes[((i + (1 as i64)) as i32)] if? (0 as u8)) as i64)
                const b2 = ((bytes[((i + (2 as i64)) as i32)] if? (0 as u8)) as i64)
                if ((b1 < (128 as i64)) or (b1 > (191 as i64))) { return false }
                if ((b2 < (128 as i64)) or (b2 > (191 as i64))) { return false }
                if ((c == (224 as i64)) and (b1 < (160 as i64))) { return false }
                if ((c == (237 as i64)) and (b1 > (159 as i64))) { return false }
                cp = ((((c - (224 as i64)) * (4096 as i64)) +
                    ((b1 - (128 as i64)) * (64 as i64))) + (b2 - (128 as i64)))
                i = i + (3 as i64)
            } else {
                // 合法四字节标量也无法成为现有 UTF-16 逐 char 词法的字段名。
                return false
            }
            if (not identifierInRanges(cp, identifierLetterStarts, identifierLetterEnds)) {
                if (head) { return false }
                if (not identifierInRanges(cp, identifierDigitStarts, identifierDigitEnds)) {
                    return false
                }
            }
        }
    }
    return true
}

// 固定 BMP 区间二分：starts/ends 严格同序且互不重叠。
priv func identifierInRanges(cp: i64, starts: Array\<i64>, ends: Array\<i64>): bool {
    var low: i64 = (0 as i64)
    var high: i64 = ((starts.length as i64) - (1 as i64))
    while (low <= high) {
        const mid: i64 = ((low + high) / (2 as i64))
        if (cp < (starts[(mid as i32)] as i64)) {
            high = mid - (1 as i64)
        } else if (cp > (ends[(mid as i32)] as i64)) {
            low = mid + (1 as i64)
        } else {
            return true
        }
    }
    return false
}

// ---- SB 表示校验（§4.6.1 / D3）----
// setDynamic 的值检查：非 null 动态值必须是格式支持的 SB 表示——
// 标量/String/char/bool/整数/浮点/Array/List/Map/Parcel，其内容递归
// 满足表示要求；不接受任意业务对象。判定依据是 typeOf 的类型名（携带
// 闭合泛型实参与可空信息）。双宿主拼写并收（b4-2 native 对拍实测，
// VM TypeId.ToStandardText 与 rigi_rt TypeInfo.name 两种口径）：
//   容器头  ：".array<" / "core::Array<"；".nullable<" / "core::Nullable<"
//             | "core.collections::List<" | "core.collections::Map<"
//   标量叶子：".bool" 或 "core::bool"、".char" 或 "core::char"、
//             ".i8"…".u64" 或 "core::i8"…"core::u64"、
//             ".f32" 或 "core::float"、".f64" 或 "core::double"、
//             ".string" 或 "core::String"
//   对象叶子："core.serialization::Parcel"
// 顶层解析必须完整消费名称（结束位置 == 名称字节长度），余字一律拒绝。
// 可空包装递归放行：容器元素的可空声明（如 List<i32?>）其 wire 表示
// 本来就携带元素级 null，格式层负责解包；保留闭合可空信息即本校验的
// 判定内容之一。Map 实参分隔逗号后允许空格（两种宿主 canonical 形均
// 为 ", "，解析对空格宽容）。

// 动态值的 SB 表示校验入口。
priv func isSbRepresentable(value: Any): bool {
    const name = typeOf(value).toString()
    const bytes = name.toUtf8Span()
    const blen: i64 = (bytes.length as i64)
    const end = parseSbType(bytes, blen, (0 as i64))
    if (end < (0 as i64)) { return false }
    return end == blen
}

// 名称字节读取（界内前提由调用方保证）。
priv func sbNameByte(bytes: Span\<u8>, pos: i64): i64 {
    return ((bytes[(pos as i32)] if? (0 as u8)) as i64)
}

// bytes[lo..hi) 与 ASCII 字面量等值。
priv func sbTokenEquals(bytes: Span\<u8>, lo: i64, hi: i64, literal: String): bool {
    const lit = literal.toUtf8Span()
    if ((hi - lo) != (lit.length as i64)) { return false }
    var i: i64 = lo
    var j: i64 = (0 as i64)
    while (i < hi) {
        if (sbNameByte(bytes, i) != sbNameByte(lit, j)) { return false }
        i = (i + (1 as i64))
        j = (j + (1 as i64))
    }
    return true
}

// 标量叶子表（契约固定的 SB 基元集；BIL 别名与 canonical 两种拼写并收）。
priv func isSbScalarToken(bytes: Span\<u8>, lo: i64, hi: i64): bool {
    if (sbTokenEquals(bytes, lo, hi, ".bool")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::bool")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".char")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::char")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".i8")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::i8")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".u8")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::u8")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".i16")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::i16")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".u16")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::u16")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".i32")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::i32")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".u32")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::u32")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".i64")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::i64")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".u64")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::u64")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".f32")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::float")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".f64")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::double")) { return true }
    if (sbTokenEquals(bytes, lo, hi, ".string")) { return true }
    if (sbTokenEquals(bytes, lo, hi, "core::String")) { return true }
    return false
}

// 解析单个 SB 类型名：成功返回消费后的字节位置（>= 0），失败返回 -1。
// pos 必须指向类型名起点；token 扫描在 '<'（60）','（44）'>'（62）或
// 串尾处停止。
priv func parseSbType(bytes: Span\<u8>, blen: i64, pos: i64): i64 {
    if (pos >= blen) { return ((0 as i64) - (1 as i64)) }
    const start = pos
    var end = pos
    while (end < blen) {
        const c = sbNameByte(bytes, end)
        if (((c == (60 as i64)) or (c == (62 as i64))) or (c == (44 as i64))) { break }
        end = (end + (1 as i64))
    }
    if (end == start) { return ((0 as i64) - (1 as i64)) }
    const c0 = sbNameByte(bytes, start)
    if (c0 == (46 as i64)) {
        // 点号族（VM BIL 别名形态）：.nullable / .array 为单实参包装；
        // 其余须命中标量叶子。
        if (sbTokenEquals(bytes, start, end, ".nullable")) {
            return parseSbWrapped(bytes, blen, end)
        }
        if (sbTokenEquals(bytes, start, end, ".array")) {
            return parseSbWrapped(bytes, blen, end)
        }
        if (isSbScalarToken(bytes, start, end)) { return end }
        return ((0 as i64) - (1 as i64))
    }
    // canonical 形态包装头（native TypeInfo.name 口径）。
    if (sbTokenEquals(bytes, start, end, "core::Nullable")) {
        return parseSbWrapped(bytes, blen, end)
    }
    if (sbTokenEquals(bytes, start, end, "core::Array")) {
        return parseSbWrapped(bytes, blen, end)
    }
    if (sbTokenEquals(bytes, start, end, "core.collections::List")) {
        return parseSbWrapped(bytes, blen, end)
    }
    if (sbTokenEquals(bytes, start, end, "core.collections::Map")) {
        // Map<k, v>：两实参，逗号后允许空格（两种宿主 canonical 形均
        // 为 ", "，解析对空格宽容）。
        var p = end
        if ((p >= blen) or (sbNameByte(bytes, p) != (60 as i64))) {
            return ((0 as i64) - (1 as i64))
        }
        p = parseSbType(bytes, blen, (p + (1 as i64)))
        if (p < (0 as i64)) { return ((0 as i64) - (1 as i64)) }
        if ((p >= blen) or (sbNameByte(bytes, p) != (44 as i64))) {
            return ((0 as i64) - (1 as i64))
        }
        p = (p + (1 as i64))
        while ((p < blen) and (sbNameByte(bytes, p) == (32 as i64))) {
            p = (p + (1 as i64))
        }
        p = parseSbType(bytes, blen, p)
        if (p < (0 as i64)) { return ((0 as i64) - (1 as i64)) }
        if ((p >= blen) or (sbNameByte(bytes, p) != (62 as i64))) {
            return ((0 as i64) - (1 as i64))
        }
        return (p + (1 as i64))
    }
    if (sbTokenEquals(bytes, start, end, "core.serialization::Parcel")) {
        // Parcel 非泛型叶子：实参不存在，token 后即终止符/串尾，完整
        // 消费校验由顶层兜底（如 "ParcelX" 在 token 等值处已拒绝）。
        return end
    }
    // canonical 形态标量叶子（只出现在泛型实参位置；顶层标量两种宿主
    // 均走点号别名，此处为 native 容器实参内的 core::… 形式）。
    if (isSbScalarToken(bytes, start, end)) { return end }
    return ((0 as i64) - (1 as i64))
}

// 解析 "<" sbType ">" 包装段：pos 指向 '<'，返回 '>' 之后位置或 -1。
priv func parseSbWrapped(bytes: Span\<u8>, blen: i64, pos: i64): i64 {
    if ((pos >= blen) or (sbNameByte(bytes, pos) != (60 as i64))) {
        return ((0 as i64) - (1 as i64))
    }
    var p = parseSbType(bytes, blen, (pos + (1 as i64)))
    if (p < (0 as i64)) { return ((0 as i64) - (1 as i64)) }
    if ((p >= blen) or (sbNameByte(bytes, p) != (62 as i64))) {
        return ((0 as i64) - (1 as i64))
    }
    return (p + (1 as i64))
}

// ---- 格式层最小动态面（§4.6.1 / D3，块 4-2 B 面）----
// Array/List/Map 补充格式实现所需的最小动态遍历、类型查询和构造能力。
// 全部为编译器合成填充（仿 decodeAnyValue 先例：占位体在序列化合成器
// 安装时替换；internal 级，同编译单元的格式层可见，不承诺为用户面）。
// 分支由 ConstructedTypeSnapshot 的容器实参生成：分支内静态 typed
// 访问后把元素以 Any? 上抛（元素级上抛是合法泛型上抛，不违反容器
// 名义身份——List\<i32> 仍不可强转为 List\<Any?>）。元素/键值原样
// 读取活值，不做 wire 编解码；动态构造侧对元素做严格核验（可空目标
// 保留 null，非空目标遇 null 以 CastException 失败）。
//
// sbKind 类别码（契约固定）：0=不支持（非 SB 值）；1=bool；2=char；
// 3=i8；4=u8；5=i16；6=u16；7=i32；8=u32；9=i64；10=u64；11=f32；
// 12=f64；13=String；14=Parcel；15=Array；16=List；17=Map。
// sbBuild* 的 elementTypeName/keyTypeName/valueTypeName 同时接受两种
// 宿主拼写（VM BIL 别名形 ".i32"/".string"/".array"/".nullable" 与
// native canonical 形 "core::i32"/"core::String"/"core::Array"/
// "core::Nullable"，见上方 SB 名解析注释）。

// 动态根值的 SB 类别查询（标量按宽度区分，供格式层选择数字/字符编码）。
internal func sbKind(value: Any): i32 {
    throw new core.IllegalStateException("sbKind 应由编译器填充")
}

// SB 容器长度：Array/List 元素数、Map 条目数、Parcel 业务字段数。
internal func sbLength(value: Any): i64 {
    throw new core.IllegalStateException("sbLength 应由编译器填充")
}

// Array/List 按下标读取元素，Any? 上抛（元素 null 原样）。
internal func sbElementAt(value: Any, index: i64): Any? {
    throw new core.IllegalStateException("sbElementAt 应由编译器填充")
}

// Map 按插入序读取第 index 条目的键/值，Any? 上抛。
internal func sbKeyAt(value: Any, index: i64): Any? {
    throw new core.IllegalStateException("sbKeyAt 应由编译器填充")
}

internal func sbValueAt(value: Any, index: i64): Any? {
    throw new core.IllegalStateException("sbValueAt 应由编译器填充")
}

// 动态值的类型名查询（携带闭合泛型实参与可空信息；两种宿主拼写见
// 文件头注释，格式层消费时按双拼写解析）。
internal func sbTypeName(value: Any): String {
    throw new core.IllegalStateException("sbTypeName 应由编译器填充")
}

// 按元素类型名动态构造 Array/List/Map 并逐个填入（严格核验：
// 元素实际类型必须与登记实参一致，不做任何值类型转换）。
internal func sbBuildArray(elementTypeName: String, elements: Array\<Any?>): Any {
    throw new core.IllegalStateException("sbBuildArray 应由编译器填充")
}

internal func sbBuildList(elementTypeName: String, elements: Array\<Any?>): Any {
    throw new core.IllegalStateException("sbBuildList 应由编译器填充")
}

internal func sbBuildMap(keyTypeName: String, valueTypeName: String,
        keys: Array\<Any?>, values: Array\<Any?>): Any {
    throw new core.IllegalStateException("sbBuildMap 应由编译器填充")
}

// ---- 严格恢复契约（§4.6.3 / D3，块 4-3）----
// 类型/字段集合不匹配的统一异常（新增错误类别归所属 NS，D3）：wire 值
// 类型互换（含整数宽度转换、浮点截断、String/bool 与数值互转）、null→
// 非可空声明、集合形状/元素不符、业务字段缺失/多余/可空字段缺项一律
// 抛本异常；未登记 wire 类型、图/树模式不匹配、节点编号错误等 wire
// 结构性状态错误仍归 core.IllegalStateException。
pub open class SerializationException : core.RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

// ---- 通用字段反射（§4.6.3「反射与实现边界」，块 5-1a）----
// 类型元信息查询面：规范标识、Serializable 能力、参与序列化的字段及各
// 字段声明类型，并保留泛型实参、可空性、容器键值/元素类型与枚举 case
// 信息。字段筛选口径与 Serializable 合成规则完全一致（SerializationFacts
// + 字段闭包）：继承字段并入、@Temporary / static / 无支撑存储的计算
// 属性排除；宿主类型不限于 @Serializable——「参与序列化的字段」闭包对
// 任何 class/struct/enum struct 可计算，能力查询经 isSerializable 暴露。
// 全部函数体由编译器填充（仿 fromParcel/decodeAnyValue/sbKind 先例）。
//
// typeName 文本约定（不依赖两宿主 toString 差异）：VM BIL 规范拼写
// （CanonicalSymbolPrinter 规则：标量 .i32、容器 core.collections::
// List<.i32>、用户类型 ns::Name），闭合泛型实参与容器键值/元素类型
// 完整保留；可空包装不进文本——结构化载体是 nullable 标志，内层类型
// 名进 typeName。标量与容器的字段闭包为空，fieldsOf 返回空数组。

// 参与序列化字段的元信息条目。
pub struct FieldInfo {
    pub const name: String
    pub const typeName: String
    pub const nullable: bool

    pub init(_ -> name, _ -> typeName, _ -> nullable) { }
}

// 枚举 case 元信息条目：case 名 + 参数洞载荷（固定 case 的 fields 为空数组）。
pub class EnumCaseInfo {
    pub const name: String
    pub const fields: Array\<FieldInfo>

    pub init(_ -> name, _ -> fields) { }
}

// 实际类型的规范标识（含闭合泛型实参；不调用业务对象的 toString）。
// 无参形态取调用点泛型实参的类型；带 Type\<T> 形态取所指实际类型
//（§4.7.1：调用者已有的 Type\<T> 值作目标类型信息）。
pub func typeNameOf\<T>(typeValue: Type\<T>): String {
    throw new core.IllegalStateException("typeNameOf 应由编译器填充")
}

pub func typeNameOf\<T>(): String {
    throw new core.IllegalStateException("typeNameOf 应由编译器填充")
}

// Serializable 能力查询：未知/未登记类型返回 false，不抛异常。
pub func isSerializable\<T>(typeValue: Type\<T>): bool {
    throw new core.IllegalStateException("isSerializable 应由编译器填充")
}

// 字段清单：应序列化字段闭包（含继承字段；@Temporary/static 排除）。
// 无参形态取调用点泛型实参的类型；带 Type\<T> 形态取所指实际类型
// （§4.7.1：调用者已有的 Type\<T> 值作目标类型信息）。
pub func fieldsOf\<T>(): Array\<FieldInfo> {
    throw new core.IllegalStateException("fieldsOf 应由编译器填充")
}

pub func fieldsOf\<T>(typeValue: Type\<T>): Array\<FieldInfo> {
    throw new core.IllegalStateException("fieldsOf 应由编译器填充")
}

// 枚举 case 清单（含各 case 参数洞的名称与声明类型）。
pub func casesOf\<T>(): Array\<EnumCaseInfo> {
    throw new core.IllegalStateException("casesOf 应由编译器填充")
}

pub func casesOf\<T>(typeValue: Type\<T>): Array\<EnumCaseInfo> {
    throw new core.IllegalStateException("casesOf 应由编译器填充")
}

// ---- 按类型名的反射重载（块 5-2c，§4.7.1 递归契约）----
// 与泛型形态 / Type\<T> 值形态同一候选集与筛选口径；输入为规范类型
// 名文本（VM BIL 规范拼写；分发双拼写并收——调用方持有的名称可能
// 来自任一宿主的类型名通道），未登记抛 IllegalArgumentException。
// 供格式层在嵌套成员引导读取时按 FieldInfo.typeName 取得嵌套类型
// 元信息（调用点没有该嵌套类型的静态 Type\<T> 值可达，§4.7.1「递归
// 到每个成员时重复这一判断」）。

pub func fieldsOf(typeName: String): Array\<FieldInfo> {
    throw new core.IllegalStateException("fieldsOf 应由编译器填充")
}

pub func casesOf(typeName: String): Array\<EnumCaseInfo> {
    throw new core.IllegalStateException("casesOf 应由编译器填充")
}

pub func isSerializable(typeName: String): bool {
    throw new core.IllegalStateException("isSerializable 应由编译器填充")
}
