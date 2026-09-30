// Rigi 标准库：core.text 整数与浮点解析（STDLIB §4.3.5；整数段为
// 施工块 3-5a，浮点 parse/tryParse 为施工块 3-5b，均在本文件）。
//
// 覆盖（公共行为以 docs/STDLIB/04-text.md §4.3.5 为契约）：
//   - IntegerRadix：整数解析进制枚举（.Decimal 默认/.Hexadecimal/
//     .Binary/.Octal）。无 Auto 模式——进制完全由 radix 参数决定，不
//     做前缀自动识别；前导零不改变选定进制。
//   - NumberParseException（新异常）：parse 失败报告载体，区分两类
//     失败（isOutOfRange：false=非法文本、true=数值超出目标类型的
//     宽度/符号范围）并可携带 String 内零基 UTF-8 字节位置（position，
//     无法给出时为 null）。
//   - 八种整数（i8/i16/i32/i64/u8/u16/u32/u64）的 parse/tryParse 静态
//     扩展：parse 失败抛 NumberParseException；tryParse 对同一规则的
//     两类失败返回 null（0 是正常值），其他异常照常传播——parse 与
//     tryParse 共用同一套语法及范围规则（同一实现核心，天然一致）。
//
// 语法裁决（§4.3.5 明文，逐条落实）：
//   - .Hexadecimal 要求 0x/0X 前缀 + 至少一个十六进制数字（ASCII
//     0-9/a-f/A-F）；前缀只校验该模式的文本形态，不负责选择进制；
//     无前缀的 FF 在该模式下失败。
//   - .Decimal/.Binary/.Octal 均为无前缀数字（0-9/0-1/0-7）；不识别
//     或剥离 0b/0B、0o/0O——二进制读 0b1010 先取得 0 在 b 停止得 0，
//     八进制读 0o17 同理；默认十进制读 0xFF 取得 0 在 x 停止。
//   - 可有一个前导 + 或 -；十六进制时符号必须在 0x/0X 之前，其他
//     模式在数字之前。无符号目标拒绝任何负号（含 -0、-0x0）。
//   - 读取有效数字前缀，不要求消费完整文本：处理符号/前缀后连续
//     读取该进制有效数字，遇第一个非有效数字字符或文本结束即停，
//     返回已读数字的值（"123abc"→123、"12 34"→12、"1_000"→1）。
//     数字后面的空白/逗号/下划线/字母等只是停止位置，不因后缀使
//     已读数字失效。
//   - 不跳过前导空白；空串、仅符号、十六进制缺前缀、处理符号/前缀
//     后没有任何有效数字时失败。
//   - 范围检查按完整有效数字前缀的数学数值（不将十六进制文本解释为
//     目标类型的补码位模式，最小负数正确解析），越界均失败，不回绕、
//     不经浮点、不能因溢出提前成功返回更短前缀（i8.parse("128abc")
//     仍超范围失败）。
//   - 数字与进制前缀一律 ASCII 判定，与系统语言/locale 无关；bool
//     不提供解析 API。
//
// 实现底座（纯 Rigi 层，无新 native）：
//   - 文本经 String.toUtf8Span() 取独立字节副本顺序扫描（每次解析
//     复制一份，契约无性能要求；字节位置与 String.length 同一 UTF-8
//     字节口径，§4.3.1）。
//   - 有效数字前缀先按所选进制在 u64 域累积为幅值（u64 可容纳全部
//     八型目标的最坏幅值：u64 最大值与 i64 最小负数的幅值）。累积
//     每步用显式比较防溢出——mag > (u64max - digit) / base 即越界
//     （Rigi 整数运算回绕语义，不依赖回绕判定溢出）。
//   - 收尾按目标宽度/符号范围检查后转换：全部 as 窄化都发生在值已
//     验证处于目标值域之后（值域内的窄化对「值检查」与「位回绕」
//     两种转换语义给出同一正确结果，实现不依赖具体语义）。i64 最小
//     负数经 -(mag-1) - 1 构造，规避「幅值恰为 2^63」的 u64→i64
//     越界转换。
//   - ext 静态扩展语法先例：SYNTAX §4.4（pub ext static func），经
//     目标类型调用（i32.parse(...)）；enum case 默认参数先例：
//     core.io buffered（ownership: ... = .Borrowed）。
namespace core.text

// ===== 进制（§4.3.5：统一接收的 IntegerRadix 枚举参数，默认 .Decimal）=====

pub enum struct IntegerRadix {}[
    Decimal,
    Hexadecimal,
    Binary,
    Octal
]

// ===== 解析失败异常（§4.3.5 失败段）=====

// parse 失败抛出的异常：区分非法文本（isOutOfRange=false）与超出
// 目标类型范围（isOutOfRange=true）两类，并可给出 String 内零基
// UTF-8 字节位置 position（可给出时非 null：非法文本为检测失败处
// 的字节偏移，超范围为有效数字前缀结束处的字节偏移）。open 供派生
// 细化（形态同 TextFormatException/UndisposedResourceException 先例）。
pub open class NumberParseException : core.RuntimeException {
    // 失败种类投影：false=非法文本（格式错误），true=超出范围
    pub var isOutOfRange: bool
    // 失败相关位置（String 内零基 UTF-8 字节偏移；无法给出时 null）
    pub var position: i64?

    // 兼容入口：仅消息（种类=非法文本、无位置）
    pub init(text: String) {
        message = text
        isOutOfRange = false
        const none: i64? = null
        position = none
    }

    // 全量入口：消息 + 种类 + 字节位置
    pub init(text: String, outOfRange: bool, at: i64) {
        message = text
        isOutOfRange = outOfRange
        const atBoxed: i64? = at
        position = atBoxed
    }

    pub override func getMessage(): String { return message }
}

// ===== 内部扫描助手（priv，仅本文件可用）=====

// u64 域无符号最大值（累积防溢出检查的基准）
priv func intU64Max(): u64 {
    return 18446744073709551615UL
}

// 进制 → 累积基数（.Decimal/.Binary/.Octal/.Hexadecimal → 10/2/8/16；
// enum struct 等值比较经类型注解中间量，buffered.rg 先例）
priv func intRadixBase(radix: IntegerRadix): u64 {
    const hexCase: IntegerRadix = .Hexadecimal
    const binCase: IntegerRadix = .Binary
    const octCase: IntegerRadix = .Octal
    if (radix == hexCase) {
        return (16 as u64)
    }
    if (radix == binCase) {
        return (2 as u64)
    }
    if (radix == octCase) {
        return (8 as u64)
    }
    return (10 as u64)
}

// 单字符数值（ASCII 判定，与系统语言无关）：'0'-'9' → 0..9，
// 'a'-'f'/'A'-'F' → 10..15，其余 → -1；调用方按基数校验数值上限
//（数值 ≥ 基数的字符与「非数字字符」同责：停止读取）
priv func intDigitValue(b: u8): i64 {
    const c: i64 = (b as i64)
    if ((c >= (48 as i64)) and (c <= (57 as i64))) {
        return c - (48 as i64)
    }
    if ((c >= (97 as i64)) and (c <= (102 as i64))) {
        return (c - (97 as i64)) + (10 as i64)
    }
    if ((c >= (65 as i64)) and (c <= (70 as i64))) {
        return (c - (65 as i64)) + (10 as i64)
    }
    return (-1 as i64)
}

// 文本首字节是否为负号（'-'，UTF-8 下 ASCII 字符恒单字节）。符号
// 只允许出现在最前（十六进制时在 0x/0X 之前），故经首字节即可恢复
// 扫描核心不回传的符号信息
priv func intStartsWithMinus(text: String): bool {
    const bytes = text.toUtf8Span()
    if ((bytes.length as i64) < (1 as i64)) {
        return false
    }
    return (intByteAt(bytes, (0 as i64)) == (45 as u8))
}

// Span 指定偏移的单字节读取（界内前提由调用方保证）
priv func intByteAt(bytes: Span\<u8>, offset: i64): u8 {
    return (bytes[(offset as i32)] if? (0 as u8))
}

// 扫描核心：按 §4.3.5 语法处理可选符号与十六进制前缀，随后连续读取
// 所选进制的有效数字前缀，在 u64 域累积为幅值。
//   返回 (幅值, 有效数字前缀结束处的字节偏移)。
//   rejectNegative=true（无符号目标）时遇到任何负号（含 -0、-0x0）
//   即抛 NumberParseException（非法文本）。
//   失败（空串/仅符号/十六进制缺前缀/无有效数字/累积超 u64）抛
//   NumberParseException：前四类为非法文本，累积超 u64 为超范围。
priv func intParseScan(text: String, radix: IntegerRadix, rejectNegative: bool): core.Pair\<u64, i64> {
    const bytes = text.toUtf8Span()
    const n: i64 = (bytes.length as i64)
    var i: i64 = (0 as i64)

    // 可有一个前导 + 或 -（不跳过前导空白——空白在此即停，数字
    // 阶段无有效数字而失败）。负号信息不在此记录：符号只允许在
    // 最前，有符号调用方经 intStartsWithMinus（首字节判定）恢复
    if (i < n) {
        const lead: u8 = intByteAt(bytes, i)
        if (lead == (43 as u8)) {
            i = (i + (1 as i64))
        } else if (lead == (45 as u8)) {
            if (rejectNegative) {
                throw new NumberParseException(
                    "无符号整数不接受负号（位置 ${i}）", false, i)
            }
            i = (i + (1 as i64))
        }
    }

    // 进制与十六进制前缀（0x/0X 必须存在，前缀后至少一个数字由
    // 有效数字检查保证；符号在前缀之前——上面已消费）
    const base: u64 = intRadixBase(radix)
    const hexCase: IntegerRadix = .Hexadecimal
    const isHex: bool = (radix == hexCase)
    if (isHex) {
        if ((i + (1 as i64)) >= n) {
            throw new NumberParseException(
                "十六进制文本缺少 0x/0X 前缀（位置 ${i}）", false, i)
        }
        const h0: u8 = intByteAt(bytes, i)
        const h1: u8 = intByteAt(bytes, (i + (1 as i64)))
        const ok0: bool = (h0 == (48 as u8))
        const okX: bool = (h1 == (120 as u8)) or (h1 == (88 as u8))
        if ((not ok0) or (not okX)) {
            throw new NumberParseException(
                "十六进制文本缺少 0x/0X 前缀（位置 ${i}）", false, i)
        }
        i = (i + (2 as i64))
    }

    // 连续读取有效数字前缀：遇第一个非有效数字字符或文本结束即停
    //（数值 ≥ 基数的字符同责停止；不要求消费完整文本）
    const u64max: u64 = intU64Max()
    var mag: u64 = (0 as u64)
    var any: bool = false
    while (i < n) {
        const d: i64 = intDigitValue(intByteAt(bytes, i))
        if (d < (0 as i64)) {
            // 非该进制数字字符：停止位置（后缀不使已读数字失效）
            break
        }
        const dv: u64 = (d as u64)
        if (dv >= base) {
            break
        }
        // 累积防溢出（显式比较，不依赖回绕）：mag > (u64max - d)/base
        // 时 mag*base + d 必超 u64；等号不超（u64max = q*base + r）
        if (mag > ((u64max - dv) / base)) {
            throw new NumberParseException(
                "整数文本的数值超出 u64 可表示范围（数字于位置 ${i}）",
                true, i)
        }
        mag = (mag * base) + dv
        any = true
        i = (i + (1 as i64))
    }

    // 处理符号/前缀后必须至少一个有效数字（空串/仅符号/缺前缀/
    // 首字符即非数字均在此失败）
    if (not any) {
        throw new NumberParseException(
            "整数文本没有有效数字（位置 ${i}）", false, i)
    }

    return new core.Pair\<u64, i64>(mag, i)
}

// 2^(bits-1)（u64 域；bits ∈ {8, 16, 32, 64}，有符号目标的负数上界）。
// 查表代替移位（u64 移位量类型对齐与 2^63 字面量定型均无歧义负担）；
// UL 后缀字面量按 NumericLiteral 无符号路径定型
priv func intHalfBound(bits: i64): u64 {
    if (bits == (8 as i64)) {
        return 128UL
    }
    if (bits == (16 as i64)) {
        return 32768UL
    }
    if (bits == (32 as i64)) {
        return 2147483648UL
    }
    return 9223372036854775808UL
}

// 2^bits（u64 域；bits ∈ {8, 16, 32}，无符号目标的独占上界）
priv func intUnsignedBound(bits: i64): u64 {
    if (bits == (8 as i64)) {
        return 256UL
    }
    if (bits == (16 as i64)) {
        return 65536UL
    }
    return 4294967296UL
}

// 有符号目标收尾：幅值按「位宽 bits（8/16/32/64）+ 符号」检查数学
// 数值范围（完整前缀检查——不因溢出提前成功返回更短前缀），构造
// i64 值返回。最小负数（幅值 = 2^(bits-1)）正确解析：经 -(mag-1)-1
// 构造，规避幅值 2^63 的 u64→i64 直接转换。十六进制文本不解释为
// 补码位模式（i8 的 0xFF 是幅值 255 超范围失败，而非 -1）。
priv func intParseSignedI64(text: String, radix: IntegerRadix, bits: i64): i64 {
    const scan = intParseScan(text, radix, false)
    const mag: u64 = scan.key
    const negative: bool = intStartsWithMinus(text)

    // 2^(bits-1)（u64 域）
    const half: u64 = intHalfBound(bits)
    if (negative) {
        // 允许恰为最小负数幅值 2^(bits-1)：i8 的 128 / i64 的 2^63
        if (mag > half) {
            throw new NumberParseException(
                "整数的数值超出 ${bits} 位有符号范围（前缀止于位置 ${scan.value}）",
                true, scan.value)
        }
    } else {
        const maxPos: u64 = half - (1 as u64)
        if (mag > maxPos) {
            throw new NumberParseException(
                "整数的数值超出 ${bits} 位有符号范围（前缀止于位置 ${scan.value}）",
                true, scan.value)
        }
    }

    // 值构造（全部转换都发生在已验证值域内）
    if (not negative) {
        return (mag as i64)
    }
    if (mag == (0 as u64)) {
        return (0 as i64)
    }
    const shrunk: u64 = mag - (1 as u64)
    return (-((shrunk as i64))) - (1 as i64)
}

// 无符号目标（u8/u16/u32）收尾：拒绝负号（扫描核心处理），幅值按
// 位宽检查 [0, 2^bits) 后转 i64 返回（值域包含于 i64，转换安全）。
priv func intParseUnsignedI64(text: String, radix: IntegerRadix, bits: i64): i64 {
    const scan = intParseScan(text, radix, true)
    const mag: u64 = scan.key
    const limit: u64 = intUnsignedBound(bits)
    if (mag >= limit) {
        throw new NumberParseException(
            "整数的数值超出 ${bits} 位无符号范围（前缀止于位置 ${scan.value}）",
            true, scan.value)
    }
    return (mag as i64)
}

// u64 目标（八型中唯一值域超出 i64 的目标）：拒绝负号，幅值即值
//（累积防溢出已保证不超 u64；无需收尾检查）
priv func intParseU64(text: String, radix: IntegerRadix): u64 {
    const scan = intParseScan(text, radix, true)
    return scan.key
}

// ===== i8 =====

pub ext static func i8.parse(text: String, radix: IntegerRadix = .Decimal): i8 {
    const v: i64 = intParseSignedI64(text, radix, (8 as i64))
    return (v as i8)
}

pub ext static func i8.tryParse(text: String, radix: IntegerRadix = .Decimal): i8? {
    try {
        const v: i8 = i8.parse(text, radix)
        const boxed: i8? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== i16 =====

pub ext static func i16.parse(text: String, radix: IntegerRadix = .Decimal): i16 {
    const v: i64 = intParseSignedI64(text, radix, (16 as i64))
    return (v as i16)
}

pub ext static func i16.tryParse(text: String, radix: IntegerRadix = .Decimal): i16? {
    try {
        const v: i16 = i16.parse(text, radix)
        const boxed: i16? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== i32（§4.3.5 代表性声明形态）=====

pub ext static func i32.parse(text: String, radix: IntegerRadix = .Decimal): i32 {
    const v: i64 = intParseSignedI64(text, radix, (32 as i64))
    return (v as i32)
}

pub ext static func i32.tryParse(text: String, radix: IntegerRadix = .Decimal): i32? {
    try {
        const v: i32 = i32.parse(text, radix)
        const boxed: i32? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== i64 =====

pub ext static func i64.parse(text: String, radix: IntegerRadix = .Decimal): i64 {
    return intParseSignedI64(text, radix, (64 as i64))
}

pub ext static func i64.tryParse(text: String, radix: IntegerRadix = .Decimal): i64? {
    try {
        const v: i64 = i64.parse(text, radix)
        const boxed: i64? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== u8 =====

pub ext static func u8.parse(text: String, radix: IntegerRadix = .Decimal): u8 {
    const v: i64 = intParseUnsignedI64(text, radix, (8 as i64))
    return (v as u8)
}

pub ext static func u8.tryParse(text: String, radix: IntegerRadix = .Decimal): u8? {
    try {
        const v: u8 = u8.parse(text, radix)
        const boxed: u8? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== u16 =====

pub ext static func u16.parse(text: String, radix: IntegerRadix = .Decimal): u16 {
    const v: i64 = intParseUnsignedI64(text, radix, (16 as i64))
    return (v as u16)
}

pub ext static func u16.tryParse(text: String, radix: IntegerRadix = .Decimal): u16? {
    try {
        const v: u16 = u16.parse(text, radix)
        const boxed: u16? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== u32 =====

pub ext static func u32.parse(text: String, radix: IntegerRadix = .Decimal): u32 {
    const v: i64 = intParseUnsignedI64(text, radix, (32 as i64))
    return (v as u32)
}

pub ext static func u32.tryParse(text: String, radix: IntegerRadix = .Decimal): u32? {
    try {
        const v: u32 = u32.parse(text, radix)
        const boxed: u32? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== u64（值域超出 i64 的唯一目标：扫描核心直出 u64 幅值）=====

pub ext static func u64.parse(text: String, radix: IntegerRadix = .Decimal): u64 {
    return intParseU64(text, radix)
}

pub ext static func u64.tryParse(text: String, radix: IntegerRadix = .Decimal): u64? {
    try {
        const v: u64 = u64.parse(text, radix)
        const boxed: u64? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== 浮点解析（§4.3.5 浮点段，施工块 3-5b）=====
//
// 语法（契约逐条落实）：十进制整数/小数/eE 指数形态；可有前导正负号
// 及前导零；整数部分至少一位数字；出现小数点后至少一位数字；指数可
// 有正负号且至少一位数字。只接受 ASCII 数字与点号；必须消费全部文
// 本（与整数前缀解析不同）；不接受空白/地区格式/进制前缀/十六进制
// 浮点/f/F 后缀。
// 特殊值：区分大小写 NaN/Infinity/+Infinity/-Infinity（与既有 toString
// 输出衔接）；其余大小写/缩写一律非法文本。
//
// 舍入（关键契约）：直接按目标精度（float 24 位/double 53 位有效数）
// 做最近值、正中取偶的正确舍入——float 有独立的 24 位路径，不经
// double 中转（契约明文）。支持次正规数；过小的有限数按同一规则舍入
// 至带符号零并视为成功（保留负零）；有限文本超出目标有限范围抛
// NumberParseException（超范围类），不自动产生 Infinity；普通十进制
// 不能精确二进制表示不单独构成失败。
//
// 实现（纯 Rigi 层，无新 native，不调宿主 strtod/formatter）：
//   - 快速路径（仅 double，Clinger 思想）：有效数字 ≤ 15 位（幅值
//     < 10^15 < 2^53 可精确表示）且十进制指数 |s| ≤ 22（10^22 = 5^22·
//     2^22，5^22 < 2^53，10^0..10^22 全体可精确表示）时，(I as double)
//     与 10^|s| 均精确，单次乘/除即真值的唯一一次舍入 → 正确舍入。
//   - 通用路径（float 全部与 double 其余）：十进制有效数字收入任意
//     精度无符号整数 FltBig（u32 limb 小端数组；mulSmall(10)+addSmall
//     累积、mulPow5 造 5 幂），值 = num/den·2^s2（s≥0：num=I·5^s、
//     den=1；s<0：num=I、den=5^-s）。取 D = p+2-diff（diff = bitlen(num)
//     -bitlen(den)）缩放被除数，逐位长除得商 q（p+2 或 p+3 位，收进
//     i64）与余数 r（sticky）；v = (q + r/den)·2^-S（S = D-s2），
//     e = qb-1-S。正规段：q 末两位 guard/sticky + r 凑齐取偶信息，
//     得 p 位有效数 m；尾数进位 e+1；e 超 emax 报超范围。结果 =
//     m·2^(e-p+1)——m（≤2^53/2^24）与 2 幂均可精确表示，乘积有效位
//     数不变且落在值域内 → 与舍入模式无关恒精确（VM/native 双宿主
//     稳健）。次正规段（e < emin）：不经 p 位预舍入，直接对 q/r 按
//     2^-esub 网格 guard/sticky 取偶（避免 p 位预舍入再收缩的二次
//     舍入错误），m2·2^-esub 同理恒精确；m2=0 即带符号零。
//   - 十进制粗筛（避免极端指数下的大数运算）：d-1+s ≥ 310/39（d 为
//     有效位数）值必 ≥ 10^309/10^39，直接超范围；d+s ≤ -324/-46 值
//     必 < 10^-324/10^-46 < 2^-1075/2^-150（次正规格点半距），直接
//     带符号零。两界之间的活动带内大整数规模有界（位数 ≤ 数百）。
//   - 慢例优化（块 4-3 收尾评审，纯解释常数因子，数学逐位不变）：
//     mulPow5 按 5^13 一.chunk 块乘（k 次单步乘 5 → ⌈k/13⌉ 次）；
//     digits 累积按 9 位一.chunk（mulSmall(10^9)+addSmall(chunk)）；
//     长除前跳到商位长 qb（j ≥ qb 的迭代商位恒 0，初值 r0 =
//     floor(D/2^qb) 经块移位提取），消去次正规大除数下数百步的前
//     导零迭代；dShift < 0 的除数左移改 shlBits 整块移位。

// ---- 任意精度无符号整数（浮点通用路径专用，priv）----

// u32 limb 小端数组，count 为有效 limb 数；规范不变式：count==0（值
// 零）或 limbs[count-1] != 0。单 limb 乘/加的小整数上界 < 2^31（慢例
// 优化配套，见 mulSmall/addSmall 注释）：单步积 < 2^32·2^31 + 2^31
// < 2^63，u64 中间量无溢出。
priv class FltBig {
    priv var limbs: Array\<u32>
    priv var count: i32

    pub init() {
        limbs = core.collections.arrayOf\<u32>(32)
        count = 0
    }

    // 小值初始化（v=0 得零值）
    pub func setU32(v: u32) {
        count = 0
        if (v != (0 as u32)) {
            limbs[0] = v
            count = 1
        }
    }

    // 容量倍增（List.grow 先例）
    priv func grow() {
        const bigger = core.collections.arrayOf\<u32>((limbs.length * 2))
        var i: i32 = 0
        while (i < count) {
            bigger[i] = (limbs[i] as u32)
            i = (i + 1)
        }
        limbs = bigger
    }

    pub func isZero(): bool {
        return (count == 0)
    }

    // 乘小整数（m < 2^31；性能：块 4-3 收尾慢例评审——mulPow5 逐次
    // 乘 5 的 O(k·limbs) 是解释执行下头号热点，故把单次可乘上界从
    // 16 放宽到 5^13 = 1220703125 < 2^31，mulPow5 按 13 个十进幂
    // 一.chunk。溢出界：prod = limb·m + carry ≤ (2^32-1)(2^31-1) +
    // (2^31-1) < 2^63，u64 中间量无溢出）
    pub func mulSmall(m: u32) {
        if (count == 0) {
            return
        }
        var carry: u64 = (0 as u64)
        var i: i32 = 0
        while (i < count) {
            const prod: u64 = ((limbs[i] as u64) * (m as u64)) + carry
            limbs[i] = ((prod & (4294967295UL)) as u32)
            carry = (prod >> (32 as u64))
            i = (i + 1)
        }
        if (carry != (0 as u64)) {
            if (count == limbs.length) { grow() }
            limbs[count] = (carry as u32)
            count = (count + 1)
        }
    }

    // 加小整数（a < 2^32；同 mulSmall 的慢例优化配套——digit 累积
    // 按 9 位一.chunk 直接加 10^9 量级的部分值。sum = limb + carry
    // ≤ (2^32-1) + (2^32-1) < 2^33，u64 无溢出）
    pub func addSmall(a: u32) {
        var carry: u64 = (a as u64)
        var i: i32 = 0
        while ((carry != (0 as u64)) and (i < count)) {
            const sum: u64 = (limbs[i] as u64) + carry
            limbs[i] = ((sum & (4294967295UL)) as u32)
            carry = (sum >> (32 as u64))
            i = (i + 1)
        }
        if (carry != (0 as u64)) {
            if (count == limbs.length) { grow() }
            limbs[count] = (carry as u32)
            count = (count + 1)
        }
    }

    // 自乘 5^k（k ≥ 0；造 den=5^t 与 num=I·5^s）。慢例优化（块 4-3
    // 收尾）：按 5^13 = 1220703125（< 2^31，mulSmall 新上界）一
    // .chunk 乘入，k 次单步乘 5 降为 ⌈k/13⌉ 次块乘（k 达 355 时
    // 27 次）；余数 r = k mod 13 < 13 次单步乘 5。数值与逐次乘 5
    // 完全同一条乘法链，无任何精度/舍入差异
    pub func mulPow5(k: i64) {
        var t: i64 = (0 as i64)
        while ((t + (13 as i64)) <= k) {
            mulSmall((1220703125 as u32))
            t = (t + (13 as i64))
        }
        while (t < k) {
            mulSmall((5 as u32))
            t = (t + (1 as i64))
        }
    }

    // 左移 k 位（k ≥ 0；慢例优化配套：dShift < 0 时除数左移逐位
    // shl1Or 为 O(k·limbs)，整块搬移 + 单次部分移位降为
    // O(limbs + k/32)。零值不变；结果保持规范形）
    pub func shlBits(k: i64) {
        if (count == 0) {
            return
        }
        const limbShift: i32 = ((k / (32 as i64)) as i32)
        const bitShift: u64 = ((k % (32 as i64)) as u64)
        const needed: i32 = ((count + limbShift) + (1 as i32))
        while (limbs.length < needed) { grow() }
        // 高位 limb 整体搬移（自顶向下，避免覆盖）
        var i: i32 = (count - (1 as i32))
        while (i >= (0 as i32)) {
            limbs[(i + limbShift)] = (limbs[i] as u32)
            i = (i - (1 as i32))
        }
        // 低位补零
        var j: i32 = (0 as i32)
        while (j < limbShift) {
            limbs[j] = (0 as u32)
            j = (j + (1 as i32))
        }
        if (bitShift != (0 as u64)) {
            // 部分位左移：u64 窗口逐 limb 进位（自低向高）
            var carry: u64 = (0 as u64)
            var m: i32 = limbShift
            const top: i32 = ((count - (1 as i32)) + limbShift)
            while (m <= top) {
                const v: u64 = (((limbs[m] as u64) << bitShift) | carry)
                limbs[m] = ((v & (4294967295UL)) as u32)
                carry = (v >> (32 as u64))
                m = (m + (1 as i32))
            }
            if (carry != (0 as u64)) {
                limbs[(top + (1 as i32))] = (carry as u32)
                count = (needed)
            } else {
                count = (count + limbShift)
            }
        } else {
            count = (count + limbShift)
        }
    }

    // 右移 k 位取顶部位（k ≥ 0，floor 语义；慢例优化配套：商位长
    // 跳过与 ge 判定的初值提取用。结果为新 FltBig，保持规范形）
    pub func shrBits(k: i64): FltBig {
        const r = new FltBig()
        const limbShift: i64 = (k / (32 as i64))
        const bitShift: u64 = ((k % (32 as i64)) as u64)
        if (limbShift >= (count as i64)) {
            return r
        }
        const newCount: i32 = (count - (limbShift as i32))
        while (r.limbs.length < newCount) { r.grow() }
        if (bitShift == (0 as u64)) {
            var i: i32 = (0 as i32)
            while (i < newCount) {
                r.limbs[i] = (limbs[((i as i64) + limbShift) as i32] as u32)
                i = (i + (1 as i32))
            }
        } else {
            // u64 窗口 [hi<<32|lo] >> bitShift 取低 32 位
            var i: i32 = (0 as i32)
            while (i < newCount) {
                const loIdx: i32 = (((i as i64) + limbShift) as i32)
                const lo: u64 = (limbs[loIdx] as u64)
                var hi: u64 = (0 as u64)
                if ((i + (1 as i32)) < newCount) {
                    hi = (limbs[(loIdx + (1 as i32))] as u64)
                }
                const window: u64 = ((hi << (32 as u64)) | lo)
                r.limbs[i] = (((window >> bitShift) & (4294967295UL)) as u32)
                i = (i + (1 as i32))
            }
        }
        r.count = newCount
        while ((r.count > 0) and ((r.limbs[(r.count - (1 as i32))] as u32) == (0 as u32))) {
            r.count = (r.count - (1 as i32))
        }
        return r
    }

    // 位长（零值 0；u64 移位口径，回避 u32 移位量定型负担）
    pub func bitLength(): i64 {
        if (count == 0) {
            return (0 as i64)
        }
        var bits: i64 = ((count - 1) as i64) * (32 as i64)
        var t: u64 = (limbs[(count - 1)] as u64)
        while (t != (0 as u64)) {
            t = (t >> (1 as u64))
            bits = (bits + (1 as i64))
        }
        return bits
    }

    // 第 i 位（零基；越界为 0——调用方依赖此补零语义）
    pub func getBit(i: i64): u64 {
        const li: i64 = (i / (32 as i64))
        if (li >= (count as i64)) {
            return (0 as u64)
        }
        const off: u64 = ((i % (32 as i64)) as u64)
        return (((limbs[(li as i32)] as u64) >> off) & (1 as u64))
    }

    // 左移一位再 or 低位（乘 2 加 bit∈{0,1}；长除余数更新步）
    pub func shl1Or(bit: u64) {
        var carry: u64 = bit
        var i: i32 = 0
        while (i < count) {
            const v: u64 = (limbs[i] as u64)
            const outBit: u64 = (v >> (31 as u64))
            const nv: u64 = ((v << (1 as u64)) | carry)
            limbs[i] = ((nv & (4294967295UL)) as u32)
            carry = outBit
            i = (i + 1)
        }
        if (carry != (0 as u64)) {
            if (count == limbs.length) { grow() }
            limbs[count] = (carry as u32)
            count = (count + 1)
        }
    }

    // 长除单步（性能关键：VM 层调用开销敏感，移位/比较/减法合并为
    // 一次调用）：r = r·2 + bit；r ≥ divisor 则减并返回商位 1，否则 0
    pub func divStep(divisor: FltBig, bit: u64): i64 {
        shl1Or(bit)
        if (cmp(divisor) >= (0 as i64)) {
            subInPlace(divisor)
            return (1 as i64)
        }
        return (0 as i64)
    }

    // 比较：1=this 大，0=相等，-1=this 小（均规范形，先比长再比 limb）
    pub func cmp(other: FltBig): i64 {
        if (count != other.count) {
            if (count > other.count) {
                return (1 as i64)
            }
            return (-1 as i64)
        }
        var i: i32 = (count - 1)
        while (i >= 0) {
            const a: u32 = (limbs[i] as u32)
            const b: u32 = (other.limbs[i] as u32)
            if (a != b) {
                if (a > b) {
                    return (1 as i64)
                }
                return (-1 as i64)
            }
            i = (i - 1)
        }
        return (0 as i64)
    }

    // 原位减（前提 this ≥ other；i64 域逐 limb 借位）
    pub func subInPlace(other: FltBig) {
        var borrow: i64 = (0 as i64)
        var i: i32 = 0
        while (i < count) {
            var ob: i64 = (0 as i64)
            if (i < other.count) {
                ob = (other.limbs[i] as i64)
            }
            var diff: i64 = ((limbs[i] as i64) - ob) - borrow
            if (diff < (0 as i64)) {
                diff = (diff + (4294967296L))
                borrow = (1 as i64)
            } else {
                borrow = (0 as i64)
            }
            limbs[i] = (diff as u32)
            i = (i + 1)
        }
        while ((count > 0) and ((limbs[(count - 1)] as u32) == (0 as u32))) {
            count = (count - 1)
        }
    }

    // 拷贝（den 负向缩放前先复制，避免破坏调用方对象）；只拷有效
    // limb（容量按需倍增）
    pub func copyOf(): FltBig {
        const r = new FltBig()
        while (r.limbs.length < count) { r.grow() }
        var i: i32 = 0
        while (i < count) {
            r.limbs[i] = (limbs[i] as u32)
            i = (i + 1)
        }
        r.count = count
        return r
    }

    // 折 u64（前提：值 < 2^64；快速路径幅值 < 10^15 保证 ≤ 2 limb）
    pub func toU64(): u64 {
        var v: u64 = (0 as u64)
        var i: i64 = ((count - 1) as i64)
        while (i >= (0 as i64)) {
            v = ((v << (32 as u64)) | (limbs[(i as i32)] as u64))
            i = (i - (1 as i64))
        }
        return v
    }
}

// ---- 扫描结果载体 ----

// fltScan 的投影：special（0=数值/1=NaN/2=Infinity）、negative、
// mag（有效数字幅值，去前导/尾随零；零值为空）、exp（折入尾零后的十
// 进制指数：值 = mag × 10^exp）、sig（有效数字位数）。
priv class FltScan {
    pub var special: i32
    pub var negative: bool
    pub var mag: FltBig
    pub var exp: i64
    pub var sig: i64

    pub init() {
        special = 0
        negative = false
        mag = new FltBig()
        exp = (0 as i64)
        sig = (0 as i64)
    }
}

// ---- 浮点扫描核心（语法裁决见文件头注释）----

// Span 指定偏移的单字节读取（界内前提由调用方保证；intByteAt 同款）
priv func fltByteAt(bytes: Span\<u8>, offset: i64): u8 {
    return (bytes[(offset as i32)] if? (0 as u8))
}

// ASCII 十进制数字判定（只接受 ASCII，与系统语言无关）
priv func fltIsDigit(b: u8): bool {
    return ((b >= (48 as u8)) and (b <= (57 as u8)))
}

// 区分大小写的字面量匹配（bytes[at, at+len) 与 lit 全等；调用方保证
// 长度恰好，配合 rest 检查即「必须消费全部文本」）
priv func fltMatchLit(bytes: Span\<u8>, at: i64, lit: String): bool {
    const lb = lit.toUtf8Span()
    const ln: i64 = (lb.length as i64)
    var k: i64 = (0 as i64)
    while (k < ln) {
        if (fltByteAt(bytes, (at + k)) != (lb[(k as i32)] if? (0 as u8))) {
            return false
        }
        k = (k + (1 as i64))
    }
    return true
}

// 浮点语法扫描 + 有效数字收入 FltBig。
//   返回 FltScan（特殊值时 mag 为空、exp/sig 无意义）。
//   语法失败抛 NumberParseException（非法文本类，position = 检测点
//   的零基 UTF-8 字节偏移）：
//     空串/仅符号/整数部分无数字（含 .5 形态）→ 数字应有位置；
//     小数点后无数字（1. 形态）→ 点号后位置；指数无数字（1e/1e+）
//     → 数字应有位置；存在无法消费的尾随字符 → 该字符位置。
// 与整数解析不同：必须消费全部文本（契约明文）。
priv func fltScan(text: String): FltScan {
    const bytes = text.toUtf8Span()
    const n: i64 = (bytes.length as i64)
    var i: i64 = (0 as i64)
    const sc = new FltScan()

    // 可有一个前导 + 或 -（不跳过前导空白）
    if (i < n) {
        const lead: u8 = fltByteAt(bytes, i)
        if (lead == (43 as u8)) {
            i = (i + (1 as i64))
        } else if (lead == (45 as u8)) {
            sc.negative = true
            i = (i + (1 as i64))
        }
    }

    // 特殊值（区分大小写、完整消费；NaN 载荷/符号不承诺保留）
    const rest: i64 = (n - i)
    if ((rest == (3 as i64)) and fltMatchLit(bytes, i, "NaN")) {
        sc.special = 1
        return sc
    }
    if ((rest == (8 as i64)) and fltMatchLit(bytes, i, "Infinity")) {
        sc.special = 2
        return sc
    }

    // 数字收集：整数部分与小数部分连续收入 digitBuf（int 段后接
    // frac 段，下标即十进制位序），同时完成全部语法校验
    const buf = core.collections.arrayOf\<u8>((n as i32))
    var bufLen: i64 = (0 as i64)

    // 整数部分：至少一位数字
    const intStart: i64 = i
    while ((i < n) and fltIsDigit(fltByteAt(bytes, i))) {
        buf[(bufLen as i32)] = (fltByteAt(bytes, i) - (48 as u8))
        bufLen = (bufLen + (1 as i64))
        i = (i + (1 as i64))
    }
    const intCount: i64 = (i - intStart)
    if (intCount == (0 as i64)) {
        throw new NumberParseException(
            "浮点文本的整数部分没有数字（位置 ${i}）", false, i)
    }

    // 小数部分（点号出现则至少一位数字）
    if (i < n) {
        if (fltByteAt(bytes, i) == (46 as u8)) {
            i = (i + (1 as i64))
            const fracStart: i64 = i
            while ((i < n) and fltIsDigit(fltByteAt(bytes, i))) {
                buf[(bufLen as i32)] = (fltByteAt(bytes, i) - (48 as u8))
                bufLen = (bufLen + (1 as i64))
                i = (i + (1 as i64))
            }
            if (i == fracStart) {
                throw new NumberParseException(
                    "浮点文本的小数点后没有数字（位置 ${i}）", false, i)
            }
        }
    }
    const fracCount: i64 = (bufLen - intCount)

    // 指数部分（e/E 出现则至少一位数字，可有符号）
    var expNeg: bool = false
    var expMag: i64 = (0 as i64)
    if (i < n) {
        const eb: u8 = fltByteAt(bytes, i)
        if ((eb == (101 as u8)) or (eb == (69 as u8))) {
            i = (i + (1 as i64))
            if (i < n) {
                const sb: u8 = fltByteAt(bytes, i)
                if (sb == (43 as u8)) {
                    i = (i + (1 as i64))
                } else if (sb == (45 as u8)) {
                    expNeg = true
                    i = (i + (1 as i64))
                }
            }
            const expStart: i64 = i
            // 饱和累积（cap = n+400：超出后十进制数量级已远超活动带，
            // 与 d 合取的粗筛判定不受影响——见 fltToDouble/fltToFloat）
            const cap: i64 = (n + (400 as i64))
            while ((i < n) and fltIsDigit(fltByteAt(bytes, i))) {
                if (expMag <= cap) {
                    expMag = ((expMag * (10 as i64)) + ((fltByteAt(bytes, i) - (48 as u8)) as i64))
                }
                i = (i + (1 as i64))
            }
            if (i == expStart) {
                throw new NumberParseException(
                    "浮点文本的指数部分没有数字（位置 ${i}）", false, i)
            }
        }
    }

    // 必须消费全部文本（与整数前缀解析不同）：残余即非法
    if (i != n) {
        throw new NumberParseException(
            "浮点文本含有无法消费的尾随字符（位置 ${i}）", false, i)
    }

    // 去前导零：p0 = 首个非零位（全零 → 带符号零值，成功）
    var p0: i64 = (0 as i64)
    while ((p0 < bufLen) and ((buf[(p0 as i32)] as u8) == (0 as u8))) {
        p0 = (p0 + (1 as i64))
    }
    if (p0 == bufLen) {
        return sc
    }

    // 去尾随零（精确折入指数：值 = D × 10^(expVal - fracCount + tz)）
    var p1: i64 = bufLen
    while (((buf[((p1 - (1 as i64)) as i32)] as u8) == (0 as u8)) and (p1 > p0)) {
        p1 = (p1 - (1 as i64))
    }
    sc.sig = (p1 - p0)
    var expVal: i64 = expMag
    if (expNeg) {
        expVal = (0 as i64) - expMag
    }
    sc.exp = ((expVal - fracCount) + (bufLen - p1))

    // 有效数字收入任意精度幅值：9 位一.chunk（mag·10^9 + chunk，
    // 10^9 与 chunk ≤ 10^9-1 均 < 2^30，mulSmall/addSmall 新上界
    // 内；与逐位 mulSmall(10)+addSmall 完全同一条累积链，结果
    // 逐 limb 相同），尾段 < 9 位逐位收
    var k: i64 = p0
    while ((k + (9 as i64)) <= p1) {
        var chunk: u32 = (0 as u32)
        var j: i64 = (0 as i64)
        while (j < (9 as i64)) {
            chunk = ((chunk * (10 as u32)) + ((buf[((k + j) as i32)] as u8) as u32))
            j = (j + (1 as i64))
        }
        sc.mag.mulSmall((1000000000 as u32))
        sc.mag.addSmall(chunk)
        k = (k + (9 as i64))
    }
    while (k < p1) {
        sc.mag.mulSmall((10 as u32))
        sc.mag.addSmall(((buf[(k as i32)] as u8) as u32))
        k = (k + (1 as i64))
    }
    return sc
}

// ---- 精确幂构造（物化用；2 的幂全体可精确表示，乘积与舍入模式无关）----

// 10^k（0 ≤ k ≤ 22：10^k = 5^k·2^k 且 5^k < 2^53，全体精确；快速路径用）
priv func fltPow10d(k: i64): double {
    var r: double = 1.0
    var t: i64 = (0 as i64)
    while (t < k) {
        r = (r * 10.0)
        t = (t + (1 as i64))
    }
    return r
}

// 2^k（-1074 ≤ k ≤ 1023：反复平方法，O(log|k|) 次乘法）。所需乘积
// 均为精确 2 幂且部分和介于 0 与 k 之间，无中间上溢/下溢（未选用的
// 末次平方可得未使用的 Inf/0，直接丢弃）
priv func fltPow2d(k: i64): double {
    var r: double = 1.0
    var base: double = 2.0
    var kk: i64 = k
    if (kk < (0 as i64)) {
        base = 0.5
        kk = ((0 as i64) - kk)
    }
    while (kk > (0 as i64)) {
        if ((kk & (1 as i64)) == (1 as i64)) {
            r = (r * base)
        }
        base = (base * base)
        kk = (kk >> (1 as i64))
    }
    return r
}

// 2^k（float 版，-149 ≤ k ≤ 127；精确性论证同 fltPow2d）
priv func fltPow2f(k: i64): float {
    var r: float = (1.0 as float)
    var base: float = (2.0 as float)
    var kk: i64 = k
    if (kk < (0 as i64)) {
        base = (0.5 as float)
        kk = ((0 as i64) - kk)
    }
    while (kk > (0 as i64)) {
        if ((kk & (1 as i64)) == (1 as i64)) {
            r = (r * base)
        }
        base = (base * base)
        kk = (kk >> (1 as i64))
    }
    return r
}

// ---- 任意精度正确舍入核心 ----

// fltRoundCore 的投影：值 = mant × 2^scale（恒可精确表示的组合）。
// 正规结果 mant 为 p 位有效数（scale = e-(p-1)）；次正规结果 mant
// 按 2^-esub 网格取偶（scale = -esub，mant 可为 0 = 带符号零）。
priv class FltRounded {
    pub var mant: i64
    pub var scale: i64

    pub init() {
        mant = (0 as i64)
        scale = (0 as i64)
    }
}

// 十进制 → 二进制正确舍入（正中取偶）核心。
//   入参：num/den（FltBig，值 = num/den·2^s2，den ≥ 1）、s2、p（有效
//   位数 53/24）、esub（次正规最小指数幅值 1074/149）、emax（正规最大
//   指数 1023/127）、pos（超范围异常的报告位置）。
//   算法（文件头注释的完整论证）：
//     diff = bitlen(num)-bitlen(den)；D = p+2-diff（被除数 2^D 缩放，
//     负则转除数左移）；逐位长除得商 q（p+2/p+3 位，收 i64）与余数
//     r（sticky）；v = (q + r/den)·2^-S（S = D-s2），e = qb-1-S。
//     e ≥ emin（= p-1-esub）→ 正规：q 末两位 + r 凑齐 guard/sticky
//     取偶得 p 位 m；进位 m=2^p 时 m=2^(p-1)、e+1；e+1 > emax 抛
//     超范围。e < emin → 次正规：k2 = S-esub，直接对 q/r 按 2^-esub
//     网格取偶（不经 p 位预舍入，规避二次舍入错误）。
//   正确性要点：q 的位数与舍入位相对 v 的刻度由 D 的选择保证（q ∈
//   [2^(p+1), 2^(p+3))，证明见文件头）；舍入所需的全部低位信息只有
//   guard（q 的次低位）与 sticky（q 末位 + 余数非零），正中（余数恰
//   半）由 sticky=false 且 guard=1 且 m 偶识别 → 取偶。
priv func fltRoundCore(num: FltBig, den: FltBig, s2: i64, p: i64,
        esub: i64, emax: i64, pos: i64): FltRounded {
    const res = new FltRounded()
    const bn: i64 = num.bitLength()
    const bd: i64 = den.bitLength()
    const diff: i64 = (bn - bd)
    const dShift: i64 = ((p + (2 as i64)) - diff)
    const sScale: i64 = (dShift - s2)

    // 除数（dShift < 0 时把缩放转嫁除数左移；shlBits 整块移位，
    // O(limbs + k/32)，替代逐位 shl1Or 的 O(k·limbs)）
    var divisor: FltBig = den
    var denShifted: FltBig = den
    if (dShift < (0 as i64)) {
        denShifted = den.copyOf()
        denShifted.shlBits((0 as i64) - dShift)
        divisor = denShifted
    }

    // 逐位长除：被除数 = num·2^dShift（dShift < 0 时为 num，除数已
    // 代以 denShifted）。商位长跳过（慢例优化，块 4-3 收尾，数学
    // 与逐位全量迭代逐位等价）：
    //   商 q = floor(num·2^dShift/den)（dShift < 0 时即
    //   floor(num/divisor)），qb = bitlen(q)。j ≥ qb 的迭代商位
    //   恒 0：D < divisor·2^qb 推出不变式 r = floor(D/2^(j+1)) <
    //   divisor，无减法、无商位。故以 r0 = floor(D/2^qb) 为初值
    //   直接从 j = qb-1 起迭代，每步不变式 D ≡ r·2^j + qTop·
    //   divisor·2^j (mod divisor·2^j) 与全量版完全一致，q/sticky
    //   结果逐位相同。活动带内被除数高位前导零迭代（次正规大除数
    //   下达数百步、每步 O(limbs) 的 shl1Or）由此消去——这是解释
    //   执行下的头号热点。
    //   qb 判定：q ≥ 2^t（t = bn+dShift-bd）⇔ num ≥ den·2^(bn-bd)
    //   （两侧同除 2^dShift，2 的幂精确）⇔ num 块移位后与 den 比较；
    //   不成立则 bitlen(q) = t，成立则 = t+1。粗筛保证 q ∈
    //   [2^(p+1), 2^(p+3))，qb ≥ p+2 ≥ 26；qb < 1 为防御回退（按旧
    //   式从顶位全量迭代，结束后由 qTop 位长回填 qb）。
    const t0: i64 = ((bn + dShift) - bd)
    var ge: bool = false
    if (bn >= bd) {
        ge = (num.shrBits((bn - bd)).cmp(den) >= (0 as i64))
    } else {
        const numUp = num.copyOf()
        numUp.shlBits((bd - bn))
        ge = (numUp.cmp(den) >= (0 as i64))
    }
    var qb: i64 = t0
    if (ge) {
        qb = (t0 + (1 as i64))
    }
    var fullScan: bool = false
    var r = new FltBig()
    var jStart: i64 = (qb - (1 as i64))
    if (qb >= (1 as i64)) {
        // r0 = floor(num·2^dShift / 2^qb)：右移量 qb-dShift（负则左移）
        if (dShift >= (0 as i64)) {
            const rs: i64 = (qb - dShift)
            if (rs >= (0 as i64)) {
                r = num.shrBits(rs)
            } else {
                r = num.copyOf()
                r.shlBits((0 as i64) - rs)
            }
        } else {
            r = num.shrBits(qb)
        }
    } else {
        fullScan = true
        jStart = (bn - (1 as i64))
        if (dShift > (0 as i64)) {
            jStart = (jStart + dShift)
        }
    }
    var qTop: i64 = (0 as i64)
    var started: bool = false
    var j: i64 = jStart
    while (j >= (0 as i64)) {
        var bit: u64 = (0 as u64)
        if (dShift >= (0 as i64)) {
            if (j >= dShift) {
                bit = num.getBit((j - dShift))
            }
        } else {
            bit = num.getBit(j)
        }
        const ob: i64 = r.divStep(divisor, bit)
        if (started or (ob == (1 as i64))) {
            started = true
            qTop = ((qTop << (1 as i64)) | ob)
        }
        j = (j - (1 as i64))
    }
    if (fullScan) {
        // 防御回退：qb 由实际商位长（qTop 位长）回填
        var c: i64 = (0 as i64)
        var v: i64 = qTop
        while (v != (0 as i64)) {
            c = (c + (1 as i64))
            v = (v >> (1 as i64))
        }
        qb = c
    }
    const sticky: bool = (not r.isZero())
    const e: i64 = ((qb - (1 as i64)) - sScale)
    const emin: i64 = ((p - (1 as i64)) - esub)

    if (e >= emin) {
        // 正规段：q 规约到恰 p+2 位（多余低位折入 sticky）
        var q: i64 = qTop
        var stk: bool = sticky
        if (qb == (p + (3 as i64))) {
            if ((q & (1 as i64)) == (1 as i64)) {
                stk = true
            }
            q = (q >> (1 as i64))
        }
        var m: i64 = (q >> (2 as i64))
        const g: i64 = ((q >> (1 as i64)) & (1 as i64))
        if ((q & (1 as i64)) == (1 as i64)) {
            stk = true
        }
        var extra: i64 = (0 as i64)
        if ((g == (1 as i64)) and (stk or ((m & (1 as i64)) == (1 as i64)))) {
            m = (m + (1 as i64))
            // 进位：m 达 2^p → 规格化回 2^(p-1)、指数 +1
            const bound: i64 = ((1 as i64) << p)
            if (m >= bound) {
                m = (bound >> (1 as i64))
                extra = (1 as i64)
            }
        }
        const ee: i64 = (e + extra)
        if (ee > emax) {
            throw new NumberParseException(
                "浮点文本的数值超出有限范围（位置 ${pos}）", true, pos)
        }
        res.mant = m
        res.scale = (ee - (p - (1 as i64)))
        return res
    }

    // 次正规段：k2 ≥ 1（e ≤ emin-1 推出 S ≥ qb+esub-p+1）；k2-1 ≤ 62
    // 由十进制粗筛的活动带保证（界外早已按带符号零返回）
    const k2: i64 = (sScale - esub)
    var m2: i64 = (0 as i64)
    var g2: i64 = (0 as i64)
    var stk2: bool = sticky
    if (k2 <= (62 as i64)) {
        m2 = (qTop >> k2)
        g2 = ((qTop >> (k2 - (1 as i64))) & (1 as i64))
        const lowmask: i64 = (((1 as i64) << (k2 - (1 as i64))) - (1 as i64))
        if ((qTop & lowmask) != (0 as i64)) {
            stk2 = true
        }
    } else {
        // k2 > 62：v·2^esub < 2^(qb-k2+1) ≤ 2^-6，必舍入到零
        if ((qTop != (0 as i64)) or sticky) {
            stk2 = true
        }
    }
    if ((g2 == (1 as i64)) and (stk2 or ((m2 & (1 as i64)) == (1 as i64)))) {
        m2 = (m2 + (1 as i64))
    }
    res.mant = m2
    res.scale = ((0 as i64) - esub)
    return res
}

// ---- 目标精度收尾（double/float 各一；物化乘积恒精确）----

// double 通用路径：组 num/den·2^s2 → fltRoundCore(p=53, esub=1074,
// emax=1023) → mant·2^scale（pow2d 精确幂；mant ≤ 2^53-1 精确转换）
priv func fltGeneralD(mag: FltBig, s: i64, pos: i64): double {
    var num: FltBig = mag.copyOf()
    var den = new FltBig()
    var s2: i64 = s
    if (s > (0 as i64)) {
        num.mulPow5(s)
        den.setU32((1 as u32))
    } else {
        den.setU32((1 as u32))
        den.mulPow5((0 as i64) - s)
    }
    const rr: FltRounded = fltRoundCore(num, den, s2,
        (53 as i64), (1074 as i64), (1023 as i64), pos)
    return ((rr.mant as double) * fltPow2d(rr.scale))
}

// float 通用路径：与 fltGeneralD 同构，p=24/esub=149/emax=127；
// float 全部经此 24 位独立路径（不经过 double，契约明文）
priv func fltGeneralF(mag: FltBig, s: i64, pos: i64): float {
    var num: FltBig = mag.copyOf()
    var den = new FltBig()
    var s2: i64 = s
    if (s > (0 as i64)) {
        num.mulPow5(s)
        den.setU32((1 as u32))
    } else {
        den.setU32((1 as u32))
        den.mulPow5((0 as i64) - s)
    }
    const rr: FltRounded = fltRoundCore(num, den, s2,
        (24 as i64), (149 as i64), (127 as i64), pos)
    return ((rr.mant as float) * fltPow2f(rr.scale))
}

// ---- 类型收尾：特殊值/零/粗筛/快速路径/通用路径 + 符号 ----

priv func fltToDouble(text: String, sc: FltScan): double {
    if (sc.special == 1) {
        // NaN（载荷/符号不承诺）
        return (0.0 / 0.0)
    }
    if (sc.special == 2) {
        var v: double = (1.0 / 0.0)
        if (sc.negative) {
            v = -v
        }
        return v
    }
    if (sc.mag.isZero()) {
        // 带符号零（"-0"/"-0.0" → 负零，符号位保留）
        if (sc.negative) {
            return -(0.0)
        }
        return 0.0
    }
    const digits: i64 = sc.sig
    const s: i64 = sc.exp
    const pos: i64 = (text.length as i64)

    // 十进制粗筛：d-1+s ≥ 310 → 值 ≥ 10^310 > 2^1024，必超范围
    if ((((digits - (1 as i64)) + s)) >= (310 as i64)) {
        throw new NumberParseException(
            "浮点文本的数值超出 double 有限范围（位置 ${pos}）", true, pos)
    }
    // d+s ≤ -324 → 值 < 10^-324 < 2^-1075（次正规格点半距之下），
    // 按同一舍入规则归于带符号零（成功，保留负零）
    if ((digits + s) <= (-324 as i64)) {
        if (sc.negative) {
            return -(0.0)
        }
        return 0.0
    }

    // 快速路径（Clinger）：幅值 < 10^15 < 2^53、|s| ≤ 22（10^22 =
    // 5^22·2^22 精确），两端精确下单次乘/除 = 真值唯一一次舍入
    if ((digits <= (15 as i64)) and ((s >= (-22 as i64)) and (s <= (22 as i64)))) {
        const id: u64 = sc.mag.toU64()
        var v: double = (id as double)
        if (s >= (0 as i64)) {
            v = (v * fltPow10d(s))
        } else {
            v = (v / fltPow10d((0 as i64) - s))
        }
        if (sc.negative) {
            v = -v
        }
        return v
    }

    var r: double = fltGeneralD(sc.mag, s, pos)
    if (sc.negative) {
        r = -r
    }
    return r
}

priv func fltToFloat(text: String, sc: FltScan): float {
    if (sc.special == 1) {
        const z: float = (0.0 as float)
        return (z / z)
    }
    if (sc.special == 2) {
        const one: float = (1.0 as float)
        const z: float = (0.0 as float)
        var v: float = (one / z)
        if (sc.negative) {
            v = -v
        }
        return v
    }
    if (sc.mag.isZero()) {
        if (sc.negative) {
            return -(0.0 as float)
        }
        return (0.0 as float)
    }
    const digits: i64 = sc.sig
    const s: i64 = sc.exp
    const pos: i64 = (text.length as i64)

    // 粗筛界按 float 值域：10^39 > 2^128（必超范围）；10^-46 < 2^-150
    // （次正规格点半距之下，必舍入到带符号零）
    if ((((digits - (1 as i64)) + s)) >= (39 as i64)) {
        throw new NumberParseException(
            "浮点文本的数值超出 float 有限范围（位置 ${pos}）", true, pos)
    }
    if ((digits + s) <= (-46 as i64)) {
        if (sc.negative) {
            return -(0.0 as float)
        }
        return (0.0 as float)
    }

    // float 一律走 24 位独立通用路径（快速路径仅 double 持有；
    // float 经 double 中转会引入二次舍入风险，契约明文禁止）
    var r: float = fltGeneralF(sc.mag, s, pos)
    if (sc.negative) {
        r = -r
    }
    return r
}

// ===== float =====

pub ext static func float.parse(text: String): float {
    const sc: FltScan = fltScan(text)
    return fltToFloat(text, sc)
}

pub ext static func float.tryParse(text: String): float? {
    try {
        const v: float = float.parse(text)
        const boxed: float? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}

// ===== double（§4.3.5 代表性声明形态）=====

pub ext static func double.parse(text: String): double {
    const sc: FltScan = fltScan(text)
    return fltToDouble(text, sc)
}

pub ext static func double.tryParse(text: String): double? {
    try {
        const v: double = double.parse(text)
        const boxed: double? = v
        return boxed
    } catch (e: NumberParseException) {
        return null
    }
}
