// Rigi 标准库：core.time 时间类型面（MW11c，RUNTIME §19.7；施工块 6-1
// 时间值与运算 + 施工块 6-2 文本格式 + 施工块 6-3 单调时钟与
// Stopwatch，均按 STDLIB §4.9.1～§4.9.5 与维护契约 D6 扩展）。
// DateTime.now() 的 native 时钟原语（§17.4 rigi_time_now，
// @NativeSymbol("time_now")）经 rigi_rt 落地。
//   - TimeStamp：时刻戳——milliseconds（1970/1/1 00:00 UTC 起毫秒，
//     负数为该时刻前）+ nanoseconds（毫秒外非负余量，setter 限
//     0..999_999，越界抛 core.OutOfBoundException）；总纳秒 =
//     milliseconds * 1_000_000 + nanoseconds。TimeStamp 保留更宽数值
//     范围，越界转换成 DateTime 时由 DateTime 侧拒绝（§4.9.1）。
//   - DateTime：包一个 TimeStamp；UTC 公历范围 公元 0001 年至 9999 年
//     最后一纳秒（构造、运算、序列化恢复全路径校验）；now() 经 native
//     时钟原语；加减 TimeSpan、相减得保完整纳秒精度的 TimeSpan。
//   - TimeSpan：i64 毫秒 + 0..999_999 纳秒余量（与 TimeStamp 同款规范
//     化分解；数学总量 = milliseconds × 1_000_000 + nanoseconds；如
//     -1ns = -1ms + 999999ns；零只有 0ms+0ns 一种表示）。加减、取负、
//     乘除整数全部显式检查溢出（整数基础运算隐式回绕，不用）；乘除经
//     base-100 limb 宽中间形态计算，不把完整总纳秒塞进 i64（§4.9.1
//     「不得缩小原有毫秒范围」）。
// 序列化（§4.9.5）：三者均 @Serializable，Parcel 用规范化对象字段表示；
// 恢复经属性 setter，数值范围与不变量（纳秒 0..999999、DateTime 公历
// 范围）在恢复路径同样校验。JSON 不自动把时间对象转换成日期/Duration
// 字符串——需要字符串表示时由调用者显式 toString/parse（toString 属
// 块 6-2）；JSON 中时间对象即普通 Parcel 对象（数值字段原样）。
namespace core.time

@core.serialization.Serializable()
pub struct TimeStamp {
    // const 在严格恢复通道不参与解码（只核验字段存在），故序列化字段
    // 必须是带支撑的 var：init 参数洞与序列化恢复都经 setter 通道，
    // 校验契约（含恢复路径，§4.9.5）由此保持
    pub var milliseconds: i64 {
        pub get
        pub set(value: _) { }
    }

    // setter 限范围 0..999_999（越界抛 core.OutOfBoundException）；
    // backing 形态：进入时隐含 backing = value，体只做校验
    pub var nanoseconds: i32 {
        pub get
        pub set(value: _) {
            if ((value < 0) or (value > 999999)) {
                throw new core.OutOfBoundException(
                    "TimeStamp.nanoseconds 越界：${value}（范围 0..999999）")
            }
        }
    }

    pub init(_ -> milliseconds, _ -> nanoseconds) { }
}

@core.serialization.Serializable()
pub struct TimeSpan {
    // 规范化分解：milliseconds 任意 i64，nanoseconds 恒 ∈ 0..999999
    // （setter 校验；恢复路径经 setter 同样校验，§4.9.5）。零的唯一
    // 表示为 0ms+0ns：总量为 0 时由 ns ∈ [0,1e6) 的分解唯一性保证。
    priv var milliseconds: i64 {
        get
        set(value: _) { }
    }

    priv var nanoseconds: i32 {
        get
        set(value: _) {
            if ((value < 0) or (value > 999999)) {
                throw new core.OutOfBoundException(
                    "TimeSpan.nanoseconds 越界：${value}（范围 0..999999）")
            }
        }
    }

    priv init(_ -> milliseconds, _ -> nanoseconds) { }

    // 内部构造通道：调用方保证已规范化；同编译单元 DateTime 相减复用。
    internal static func fromParts(milliseconds: i64, nanoseconds: i32): TimeSpan {
        return new TimeSpan(milliseconds, nanoseconds)
    }

    // 同编译单元 DateTime 只读分解，不暴露 TimeSpan 私有字段给公共 API。
    internal func asStamp(): TimeStamp {
        return new TimeStamp(milliseconds, nanoseconds)
    }

    priv static func isZeroValue(ms: i64, ns: i32): bool {
        return (ms == 0L) and (ns == 0)
    }

    // ---- 显式溢出检查的基础运算（整数 + - 隐式回绕，§4.9.1 禁用） ----

    priv static func addChecked(a: i64, b: i64): i64 {
        if (b > 0L) {
            if (a > (9223372036854775807L - b)) {
                throw new core.OutOfBoundException("TimeSpan 加法溢出")
            }
        } else if (b < 0L) {
            if (a < ((-9223372036854775807L - 1L) - b)) {
                throw new core.OutOfBoundException("TimeSpan 加法溢出")
            }
        }
        return a + b
    }

    priv static func subChecked(a: i64, b: i64): i64 {
        if (b > 0L) {
            if (a < ((-9223372036854775807L - 1L) + b)) {
                throw new core.OutOfBoundException("TimeSpan 减法溢出")
            }
        } else if (b < 0L) {
            if (a > (9223372036854775807L + b)) {
                throw new core.OutOfBoundException("TimeSpan 减法溢出")
            }
        }
        return a - b
    }

    // 正倍率缩放（单位换算工厂用）：越界抛 OutOfBoundException
    priv static func scaleChecked(a: i64, scale: i64): i64 {
        if (a > 0L) {
            if (a > (9223372036854775807L / scale)) {
                throw new core.OutOfBoundException("TimeSpan 单位换算溢出")
            }
        } else if (a < 0L) {
            if (a < ((-9223372036854775807L - 1L) / scale)) {
                throw new core.OutOfBoundException("TimeSpan 单位换算溢出")
            }
        }
        return a * scale
    }

    // ---- 构造入口（§4.9.1；一天固定 24 小时，各单位转换检查表示范围） ----

    pub static func fromDays(days: i64): TimeSpan {
        return TimeSpan.fromParts(TimeSpan.scaleChecked(days, 86400000L), 0)
    }

    pub static func fromHours(hours: i64): TimeSpan {
        return TimeSpan.fromParts(TimeSpan.scaleChecked(hours, 3600000L), 0)
    }

    pub static func fromMinutes(minutes: i64): TimeSpan {
        return TimeSpan.fromParts(TimeSpan.scaleChecked(minutes, 60000L), 0)
    }

    pub static func fromSeconds(seconds: i64): TimeSpan {
        return TimeSpan.fromParts(TimeSpan.scaleChecked(seconds, 1000L), 0)
    }

    pub static func fromMilliseconds(milliseconds: i64): TimeSpan {
        return TimeSpan.fromParts(milliseconds, 0)
    }

    // 微秒/纳秒总量恒可表示（余量规范化到 0..999999 不缩小毫秒范围）
    pub static func fromMicroseconds(microseconds: i64): TimeSpan {
        var q = microseconds / 1000L
        var r = microseconds - (q * 1000L)
        if (r < 0L) {
            q = q - 1L
            r = r + 1000L
        }
        return TimeSpan.fromParts(q, (r * 1000L) as i32)
    }

    pub static func fromNanoseconds(nanoseconds: i64): TimeSpan {
        var q = nanoseconds / 1000000L
        var r = nanoseconds - (q * 1000000L)
        if (r < 0L) {
            q = q - 1L
            r = r + 1000000L
        }
        return TimeSpan.fromParts(q, r as i32)
    }

    // ---- total 属性族（i64，向零截断不完整单位；总量超 i64 明确报错） ----
    //
    // 推导：总量 T = ms*1_000_000 + ns（ns），单位 U 纳秒时
    // trunc(T/U) = trunc(ms/(U/1_000_000))（U ≥ 1_000_000 且整除），
    // 因为余量 ns < 1_000_000 不会跨过截断边界（逐例验证于
    // Tests/e2e/rigi/time_values.rg）。毫秒/微秒单位下用
    // B + ((B < 0) and (余量 > 0)) ? 1 : 0 形态（B 为整除部分），
    // 与整数除法向零截断一致。

    pub var totalDays: i64 {
        pub get(_: _) { return milliseconds / 86400000L }
    }

    pub var totalHours: i64 {
        pub get(_: _) { return milliseconds / 3600000L }
    }

    pub var totalMinutes: i64 {
        pub get(_: _) { return milliseconds / 60000L }
    }

    pub var totalSeconds: i64 {
        pub get(_: _) { return milliseconds / 1000L }
    }

    pub var totalMilliseconds: i64 {
        pub get(_: _) {
            if ((milliseconds < 0L) and (nanoseconds > 0)) {
                return milliseconds + 1L
            }
            return milliseconds
        }
    }

    pub var totalMicroseconds: i64 {
        pub get(_: _) {
            const t = (nanoseconds as i64) / 1000L
            // 表示范围检查：总量（微秒）超出 i64 明确报错（§4.9.1）
            if (milliseconds > ((9223372036854775807L - t) / 1000L)) {
                throw new core.OutOfBoundException("TimeSpan 总微秒超出 i64 范围")
            }
            const minBoundUs = (-9223372036854775807L - 1L) + 999L
            if (milliseconds < ((minBoundUs - t) / 1000L)) {
                throw new core.OutOfBoundException("TimeSpan 总微秒超出 i64 范围")
            }
            const b = (milliseconds * 1000L) + t
            const rem = (nanoseconds as i64) - (t * 1000L)
            if ((b < 0L) and (rem > 0L)) {
                return b + 1L
            }
            return b
        }
    }

    pub var totalNanoseconds: i64 {
        pub get(_: _) {
            const ns = nanoseconds as i64
            // 表示范围检查：总纳秒超出 i64 明确报错（§4.9.1）
            if (milliseconds > ((9223372036854775807L - ns) / 1000000L)) {
                throw new core.OutOfBoundException("TimeSpan 总纳秒超出 i64 范围")
            }
            const minBoundNs = (-9223372036854775807L - 1L) + 999999L
            if (milliseconds < ((minBoundNs - ns) / 1000000L)) {
                throw new core.OutOfBoundException("TimeSpan 总纳秒超出 i64 范围")
            }
            return (milliseconds * 1000000L) + ns
        }
    }

    // ---- 运算（§4.9.1：检查溢出；比较按完整总量，不丢纳秒） ----

    pub operator plus(other: TimeSpan): TimeSpan {
        // 纳秒和 ∈ [0, 1999998]：进位至多 1，i32 不溢出
        var nsSum = nanoseconds + other.nanoseconds
        var carry = 0
        if (nsSum >= 1000000) {
            nsSum = nsSum - 1000000
            carry = 1
        }
        // (a+b)+1 = a+(b+1)：先折入负操作数，避免 a+b 暂态
        // 跌破 MIN；两侧均非负则先检查毫秒和，再检查正向进位。
        if (carry == 1) {
            if (milliseconds < 0L) {
                return TimeSpan.fromParts(
                    TimeSpan.addChecked(milliseconds + 1L, other.milliseconds), nsSum)
            }
            if (other.milliseconds < 0L) {
                return TimeSpan.fromParts(
                    TimeSpan.addChecked(milliseconds, other.milliseconds + 1L), nsSum)
            }
        }
        const msSum = TimeSpan.addChecked(milliseconds, other.milliseconds)
        return TimeSpan.fromParts(TimeSpan.addChecked(msSum, carry as i64), nsSum)
    }

    pub operator minus(other: TimeSpan): TimeSpan {
        // 纳秒差 ∈ (-1000000, 1000000)：借位至多 1
        var nsDiff = nanoseconds - other.nanoseconds
        var borrow = 0
        if (nsDiff < 0) {
            nsDiff = nsDiff + 1000000
            borrow = 1
        }
        // (a-b)-1 = a-(b+1)：先折入减数，避免 a-b 暂态越过
        // 正上界；b 为 MAX 时改为 (a-1)-b，且两步仍各自检查范围。
        if (borrow == 1) {
            if (other.milliseconds < 9223372036854775807L) {
                return TimeSpan.fromParts(
                    TimeSpan.subChecked(milliseconds, other.milliseconds + 1L), nsDiff)
            }
            return TimeSpan.fromParts(
                TimeSpan.subChecked(TimeSpan.subChecked(milliseconds, 1L), other.milliseconds), nsDiff)
        }
        return TimeSpan.fromParts(TimeSpan.subChecked(milliseconds, other.milliseconds), nsDiff)
    }

    pub operator opposite(): TimeSpan {
        if (milliseconds == (-9223372036854775807L - 1L)) {
            if (nanoseconds == 0) {
                // 恰好最小毫秒取负越界；带正纳秒余量则仍落在正界内。
                throw new core.OutOfBoundException("最小 TimeSpan 取负超出范围")
            }
            // 借位后为 MAX 毫秒，绝不计算 -i64_MIN。
            return TimeSpan.fromParts(9223372036854775807L, 1000000 - nanoseconds)
        }
        if (nanoseconds == 0) {
            return TimeSpan.fromParts(-milliseconds, 0)
        }
        // -(ms + ns) = (-ms - 1) + (1_000_000 - ns)
        return TimeSpan.fromParts((-milliseconds) - 1L, 1000000 - nanoseconds)
    }

    pub operator times(factor: i64): TimeSpan {
        if (TimeSpan.isZeroValue(milliseconds, nanoseconds) or (factor == 0L)) {
            return TimeSpan.fromParts(0L, 0)
        }
        if (factor == 1L) { return this }
        if (factor == (-1L)) { return -this }
        // 宽中间形态：总量（≤ ~9.2e24 ns）超 i64，用 base-100 limb
        // 精确相乘；结果超 TimeSpan 表示范围抛 OutOfBoundException
        const tDig = core.collections.arrayOf\<i64>(24)
        const tc = TimeSpan.totalAbsDigits(milliseconds, nanoseconds, tDig)
        const kDig = core.collections.arrayOf\<i64>(16)
        const kc = TimeSpan.absDigits(factor, kDig)
        const prod = core.collections.arrayOf\<i64>(48)
        TimeSpan.mulLimbs(tDig, tc, kDig, kc, prod)
        // 超出毫秒段容量的高位立即拒绝；低位仍须按最终符号分别检查。
        var i = 13
        while (i < 48) {
            if ((prod[i] if? 0L) != 0L) {
                throw new core.OutOfBoundException("TimeSpan 乘法溢出")
            }
            i += 1
        }
        const nsPart = (((prod[0] if? 0L) + ((prod[1] if? 0L) * 100L))
            + ((prod[2] if? 0L) * 10000L)) as i32
        // T 的符号由被乘数和因子共同决定；负值允许幅值恰为 MAX+1 毫秒，
        // 但若还有纳秒余量，则规范化借位会使毫秒段低于 MIN。
        const neg = (((milliseconds >= 0L) and (factor < 0L))
            or ((milliseconds < 0L) and (factor > 0L)))
        if (neg) {
            const magnitudeMs = TimeSpan.limbsToMs(prod, nsPart == 0)
            if (magnitudeMs == (-9223372036854775807L - 1L)) {
                return TimeSpan.fromParts(magnitudeMs, 0)
            }
            if (nsPart == 0) { return TimeSpan.fromParts(-magnitudeMs, 0) }
            return TimeSpan.fromParts((-magnitudeMs) - 1L, 1000000 - nsPart)
        }
        const msPart = TimeSpan.limbsToMs(prod, false)
        return TimeSpan.fromParts(msPart, nsPart)
    }

    pub operator div(divisor: i64): TimeSpan {
        if (divisor == 0L) {
            throw new core.DividedByZeroException("TimeSpan 除以零")
        }
        if (TimeSpan.isZeroValue(milliseconds, nanoseconds)) {
            return TimeSpan.fromParts(0L, 0)
        }
        if (divisor == 1L) { return this }
        if (divisor == (-1L)) { return -this }
        // 结果恒可表示（|T/k| ≤ |T|），宽中间形态只做精确除法
        const tDig = core.collections.arrayOf\<i64>(24)
        const tc = TimeSpan.totalAbsDigits(milliseconds, nanoseconds, tDig)
        const kDig = core.collections.arrayOf\<i64>(16)
        const kc = TimeSpan.absDigits(divisor, kDig)
        const quo = core.collections.arrayOf\<i64>(24)
        const qc = TimeSpan.divLimbs(tDig, tc, kDig, kc, quo)
        const nsPart = (((quo[0] if? 0L) + ((quo[1] if? 0L) * 100L))
            + ((quo[2] if? 0L) * 10000L)) as i32
        const msPart = TimeSpan.limbsToMs(quo, false)
        if (TimeSpan.isZeroValue(msPart, nsPart)) {
            return TimeSpan.fromParts(0L, 0)
        }
        // 不足一纳秒部分向零截断由整数长除法天然保证（§4.9.1）；
        // T 的符号：ms < 0 为负；ms ≥ 0（ns ≥ 0）为非负（零已早退）
        const neg = (((milliseconds >= 0L) and (divisor < 0L))
            or ((milliseconds < 0L) and (divisor > 0L)))
        var result = TimeSpan.fromParts(msPart, nsPart)
        if (neg) {
            // 取负经 opposite 的规范化：亚毫秒负值借位到毫秒段
            result = -result
        }
        return result
    }

    pub operator compareTo(other: TimeSpan): core.ComparisonResult {
        if (milliseconds < other.milliseconds) { return .LesserThanAnother }
        if (milliseconds > other.milliseconds) { return .GreaterThanAnother }
        if (nanoseconds < other.nanoseconds) { return .LesserThanAnother }
        if (nanoseconds > other.nanoseconds) { return .GreaterThanAnother }
        return .Equal
    }

    pub operator equals(other: TimeSpan): bool {
        return ((milliseconds == other.milliseconds)
            and (nanoseconds == other.nanoseconds))
    }

    // ---- 文本（§4.9.3：默认 ISO 8601 Duration 日时分秒子集） ----
    //
    // 仅秒允许小数（点号、1～9 位）；整体负号只在 P 前；零统一输出
    // PT0S（负零输入归一）。旧的 `天数.hh:mm:ss` 形态不是默认解析或
    // 输出格式（D6 钉死）。parse/tryParse 为同文件 ext 静态扩展
    // （core.text parse 族先例），toString 为实例成员（需触达 priv
    // 字段做 MIN 安全的绝对值分解，ext 体无私有访问特权）。

    pub override func toString(): String {
        // 符号与绝对值分解：负 iff ms < 0（规范化保证 ms==0 ⇒ 总量零）。
        // |T| 分解防 MIN 取负溢出（-MIN 回绕）：借位表示 |T| = aMs·1e6 +
        // aNs，MIN 且 ns==0 时 |T| = 2^63 ms 经 MAX 分解 +1 进位。
        var neg = false
        var days = 0L
        var remMs = 0L
        var aNs = 0
        if (milliseconds == (-9223372036854775807L - 1L)) {
            neg = true
            if (nanoseconds == 0) {
                // |T| = 2^63 ms：2^63-1 = q·86400000 + r，|T| = q·86400000
                // + (r+1)（r+1 达 86400000 时向天进位）
                const q = 9223372036854775807L / 86400000L
                const r = 9223372036854775807L - (q * 86400000L)
                if (r == 86399999L) {
                    days = q + 1L
                    remMs = 0L
                } else {
                    days = q
                    remMs = r + 1L
                }
            } else {
                // |T| = 2^63·1e6 - ns = (2^63-1)·1e6 + (1e6-ns)
                days = 9223372036854775807L / 86400000L
                remMs = 9223372036854775807L
                    - (days * 86400000L)
                aNs = 1000000 - nanoseconds
            }
        } else if (milliseconds < 0L) {
            neg = true
            if (nanoseconds == 0) {
                days = (-milliseconds) / 86400000L
                remMs = (-milliseconds) - (days * 86400000L)
            } else {
                // |T| = (-ms-1)·1e6 + (1e6-ns)
                const ams = (-milliseconds) - 1L
                days = ams / 86400000L
                remMs = ams - (days * 86400000L)
                aNs = 1000000 - nanoseconds
            }
        } else {
            days = milliseconds / 86400000L
            remMs = milliseconds - (days * 86400000L)
            aNs = nanoseconds
        }
        const hours = remMs / 3600000L
        const remH = remMs - (hours * 3600000L)
        const minutes = remH / 60000L
        const remM = remH - (minutes * 60000L)
        const seconds = remM / 1000L
        // 秒以下纳秒（0..999999999）：毫秒部分 ×1e6 + 纳秒余量
        const fracNs = ((remM - (seconds * 1000L)) * 1000000L) + (aNs as i64)
        // 零分量省略与无用的 T 省略；全零 → PT0S；非零负值整体负号
        var out = new core.text.StringBuilder()
        if (neg) { out.append("-") }
        out.append("P")
        if (days != 0L) {
            out.append("${days}")
            out.append("D")
        }
        const hasSeconds = (seconds != 0L) or (fracNs != 0L)
        if (((hours != 0L) or (minutes != 0L)) or hasSeconds) {
            out.append("T")
            if (hours != 0L) {
                out.append("${hours}")
                out.append("H")
            }
            if (minutes != 0L) {
                out.append("${minutes}")
                out.append("M")
            }
            if (hasSeconds) {
                out.append("${seconds}")
                if (fracNs != 0L) {
                    out.append(".")
                    out.append(tmFrac9(fracNs))
                }
                out.append("S")
            }
        }
        if ((((days == 0L) and (hours == 0L)) and (minutes == 0L))
                and (not hasSeconds)) {
            // 零值（负零经规范化已归一）：统一 PT0S
            return "PT0S"
        }
        return out.toString()
    }

    // ---- base-100 limb 宽算术（乘法/除法的中间形态，§4.9.1） ----
    //
    // 总量 T = ms*1_000_000 + ns 可达 ~9.2e24 ns，超 i64；以下全部用
    // 小端 base-100 limb（每 limb ∈ [0,100)）表示非负整数，单 limb
    // 运算不溢出（乘积 ≤ 99*99 + 进位 < 10000）。1e6 = 100^3，纳秒
    // 余量恰占 3 根 limb，对齐天然成立。

    // |x| 的 base-100 limbs（小端），返回 limb 数；x == 0 → 0。
    // MIN 经 (|x|-1) 取正再 +1 进位，避免 -MIN 回绕。
    priv static func absDigits(x: i64, limbs: Array\<i64>): i32 {
        var v = x
        var neg = false
        if (v < 0L) {
            neg = true
            v = -(v + 1L)
        }
        var cnt = 0
        while (v > 0L) {
            limbs[cnt] = v % 100L
            v = v / 100L
            cnt += 1
        }
        if (neg) {
            var i = 0
            var done = false
            while (not done) {
                const d = (limbs[i] if? 0L) + 1L
                if (d >= 100L) {
                    limbs[i] = 0L
                    i += 1
                } else {
                    limbs[i] = d
                    done = true
                }
            }
            if (i == cnt) {
                limbs[cnt] = 1L
                cnt += 1
            }
        }
        return cnt
    }

    // |T| = |ms*1_000_000 + ns| 的 limbs：|ms| 左移 3 根后按符号与
    // ns 加/减（ms < 0 时 |T| = |ms|*1e6 - ns，由 |ms| ≥ 1 保证不减出负）
    priv static func totalAbsDigits(ms: i64, ns: i32, out: Array\<i64>): i32 {
        const tmp = core.collections.arrayOf\<i64>(16)
        const mc = TimeSpan.absDigits(ms, tmp)
        var i = 0
        while (i < mc) {
            out[i + 3] = tmp[i] if? 0L
            i += 1
        }
        var cnt = mc + 3
        const nd = core.collections.arrayOf\<i64>(8)
        const nc = TimeSpan.absDigits(ns as i64, nd)
        if (ms >= 0L) {
            cnt = TimeSpan.digitsAdd(out, cnt, nd, nc)
        } else {
            cnt = TimeSpan.digitsSub(out, cnt, nd, nc)
        }
        return cnt
    }

    // a += b（小端 limbs；a 容量足够），返回新长度
    priv static func digitsAdd(a: Array\<i64>, ac: i32,
            b: Array\<i64>, bc: i32): i32 {
        var mx = ac
        if (bc > ac) { mx = bc }
        var carry = 0L
        var i = 0
        while (i < mx) {
            var av = 0L
            if (i < ac) { av = a[i] if? 0L }
            var bv = 0L
            if (i < bc) { bv = b[i] if? 0L }
            const s = (av + bv) + carry
            if (s >= 100L) {
                a[i] = s - 100L
                carry = 1L
            } else {
                a[i] = s
                carry = 0L
            }
            i += 1
        }
        if (carry > 0L) {
            a[mx] = 1L
            return mx + 1
        }
        return mx
    }

    // a -= b（要求 a ≥ b ≥ 0），返回收紧后的长度
    priv static func digitsSub(a: Array\<i64>, ac: i32,
            b: Array\<i64>, bc: i32): i32 {
        var borrow = 0L
        var i = 0
        while (i < ac) {
            const av = a[i] if? 0L
            var bv = 0L
            if (i < bc) { bv = b[i] if? 0L }
            var d = (av - bv) - borrow
            if (d < 0L) {
                d = d + 100L
                borrow = 1L
            } else {
                borrow = 0L
            }
            a[i] = d
            i += 1
        }
        var cnt = ac
        while ((cnt > 0) and ((a[cnt - 1] if? 0L) == 0L)) {
            cnt -= 1
        }
        return cnt
    }

    // 乘法门：res = a × b（res 预清零、容量 ≥ ac + bc）
    priv static func mulLimbs(a: Array\<i64>, ac: i32,
            b: Array\<i64>, bc: i32, res: Array\<i64>) {
        var i = 0
        while (i < ac) {
            const ai = a[i] if? 0L
            var carry = 0L
            var j = 0
            while (j < bc) {
                const p = ((ai * (b[j] if? 0L)) + (res[i + j] if? 0L)) + carry
                res[i + j] = p % 100L
                carry = p / 100L
                j += 1
            }
            var k = i + bc
            while (carry > 0L) {
                const s = (res[k] if? 0L) + carry
                res[k] = s % 100L
                carry = s / 100L
                k += 1
            }
            i += 1
        }
    }

    // 除法门：q = u / v（Knuth 4.3.1 D，base = 100；v 非零），
    // 返回商长度（uc < vc 时为 0）；余数丢弃。
    priv static func divLimbs(u: Array\<i64>, uc: i32,
            v: Array\<i64>, vc: i32, q: Array\<i64>): i32 {
        if (uc < vc) { return 0 }
        if (vc == 1) {
            // 单 limb 除法：逐位带进位除
            const d0 = v[0] if? 0L
            var rem = 0L
            var i = uc - 1
            while (i >= 0) {
                const cur = (rem * 100L) + (u[i] if? 0L)
                q[i] = cur / d0
                rem = cur % d0
                i -= 1
            }
            var ql = uc
            while ((ql > 0) and ((q[ql - 1] if? 0L) == 0L)) {
                ql -= 1
            }
            return ql
        }
        // 归一化：vn 最高 limb ≥ 50，商位估计至多偏大 1
        const d = 100L / ((v[vc - 1] if? 0L) + 1L)
        const un = core.collections.arrayOf\<i64>(28)
        var carry = 0L
        var i2 = 0
        while (i2 < uc) {
            const t = ((u[i2] if? 0L) * d) + carry
            un[i2] = t % 100L
            carry = t / 100L
            i2 += 1
        }
        un[uc] = carry
        const vn = core.collections.arrayOf\<i64>(20)
        carry = 0L
        i2 = 0
        while (i2 < vc) {
            const t = ((v[i2] if? 0L) * d) + carry
            vn[i2] = t % 100L
            carry = t / 100L
            i2 += 1
        }
        const top = vn[vc - 1] if? 0L
        const ql = (uc - vc) + 1
        var j = ql - 1
        while (j >= 0) {
            const numer = ((un[j + vc] if? 0L) * 100L) + (un[(j + vc) - 1] if? 0L)
            var qhat = numer / top
            var rhat = numer % top
            // 商位校正（归一化后至多两轮）
            var corrected = false
            while (not corrected) {
                var cond2 = false
                if (vc >= 2) {
                    cond2 = ((qhat * (vn[vc - 2] if? 0L))
                        > ((rhat * 100L) + (un[(j + vc) - 2] if? 0L)))
                }
                if ((qhat < 100L) and (not cond2)) {
                    corrected = true
                } else {
                    qhat -= 1L
                    rhat += top
                    if (rhat >= 100L) { corrected = true }
                }
            }
            // 乘减（+10000 技巧保证中间值非负，借位 ∈ [0,99]）
            var borrow = 0L
            var k = 0
            while (k < vc) {
                const t = (((un[j + k] if? 0L) + 10000L)
                    - (qhat * (vn[k] if? 0L))) - borrow
                un[j + k] = t % 100L
                borrow = 100L - (t / 100L)
                k += 1
            }
            const remainder = (un[j + vc] if? 0L) - borrow
            if (remainder < 0L) {
                // qhat 偏大 1：加回 vn（余数 ∈ (-v, 0)，加回一次即复原）
                qhat -= 1L
                var c2 = 0L
                k = 0
                while (k < vc) {
                    const s = ((un[j + k] if? 0L) + (vn[k] if? 0L)) + c2
                    if (s >= 100L) {
                        un[j + k] = s - 100L
                        c2 = 1L
                    } else {
                        un[j + k] = s
                        c2 = 0L
                    }
                    k += 1
                }
                un[j + vc] = remainder + c2
            } else {
                un[j + vc] = remainder
            }
            q[j] = qhat
            j -= 1
        }
        var qc = ql
        while ((qc > 0) and ((q[qc - 1] if? 0L) == 0L)) {
            qc -= 1
        }
        return qc
    }

    // 商/积 limbs 的低 3 根（1e6 = 100^3 对齐）为纳秒余量，3..12 根
    // 为毫秒幅值。负积且纳秒余量为零才允许幅值达到 MAX+1；
    // 将末位单独延后相加，避免正 i64 暂态中构造 2^63。
    priv static func limbsToMs(limbs: Array\<i64>, allowNegativeMin: bool): i64 {
        var acc = 0L
        var i = 12
        while (i > 3) {
            const d = limbs[i] if? 0L
            if (acc > ((9223372036854775807L - d) / 100L)) {
                throw new core.OutOfBoundException("TimeSpan 超出表示范围")
            }
            acc = (acc * 100L) + d
            i -= 1
        }
        const last = limbs[3] if? 0L
        if (allowNegativeMin) {
            // 唯一额外可表示的幅值：MAX 的末位加一，直接返回 MIN。
            if ((acc == (9223372036854775807L / 100L))
                    and (last == ((9223372036854775807L % 100L) + 1L))) {
                return (-9223372036854775807L - 1L)
            }
        }
        if (acc > ((9223372036854775807L - last) / 100L)) {
            throw new core.OutOfBoundException("TimeSpan 超出表示范围")
        }
        return (acc * 100L) + last
    }
}

// 单一 civil-from-days 结果载体：不对外承诺布局；文本显示与 UTC
// 六分量属性共用算法，避免纪元前 floor 与闰年判定出现双实现漂移。
internal struct DateTimeCivil {
    internal var year: i32
    internal var month: i32
    internal var day: i32
    internal var hour: i32
    internal var minute: i32
    internal var second: i32
    internal var millisecond: i32

    internal init(_ -> year, _ -> month, _ -> day, _ -> hour,
            _ -> minute, _ -> second, _ -> millisecond) { }
}

@core.serialization.Serializable()
pub struct DateTime {
    // setter 校验 UTC 公历范围（§4.9.1：公元 0001 年起至 9999 年最后
    // 一纳秒；非法日期不自动调整）。Unix 毫秒界：
    //   0001-01-01T00:00:00Z = -62135596800000
    //   9999-12-31T23:59:59.9999999Z 的最后一毫秒 = 253402300799999
    // init 参数洞与序列化恢复都经 setter 通道，全路径校验（§4.9.5）。
    pub var stamp: TimeStamp {
        pub get
        pub set(value: _) {
            if ((value.milliseconds < (-62135596800000L))
                    or (value.milliseconds > 253402300799999L)) {
                throw new core.OutOfBoundException(
                    "DateTime 超出 UTC 公历范围（公元 0001..9999）：ms=${value.milliseconds}")
            }
        }
    }

    pub init(_ -> stamp) { }

    // 毫秒先按真正的 floor 日数分解；Rigi/i64 负数除法向零截断，
    // remMs<0 时必须借一天。亚毫秒纳秒不参与日/秒分量取值。
    // 此入口只接收已校验的 0001..9999 UTC 或同范围显示毫秒。
    priv static func civilFromMilliseconds(ms: i64): DateTimeCivil {
        var days = ms / 86400000L
        var remMs = ms - (days * 86400000L)
        if (remMs < 0L) {
            days = days - 1L
            remMs = remMs + 86400000L
        }
        const z = days + 719468L
        const era = z / 146097L
        const doe = z - (era * 146097L)
        const yoe = (((doe - (doe / 1460L)) + (doe / 36524L)) - (doe / 146096L)) / 365L
        var y = yoe + (era * 400L)
        const doy = doe - (((yoe * 365L) + (yoe / 4L)) - (yoe / 100L))
        const mp = ((5L * doy) + 2L) / 153L
        const d = (doy - (((153L * mp) + 2L) / 5L)) + 1L
        var m = mp + 3L
        if (mp >= 10L) { m = mp - 9L }
        if (m <= 2L) { y = y + 1L }
        const hh = remMs / 3600000L
        const remH = remMs - (hh * 3600000L)
        const mm = remH / 60000L
        const remM = remH - (mm * 60000L)
        const ss = remM / 1000L
        return new DateTimeCivil(y as i32, m as i32, d as i32,
            hh as i32, mm as i32, ss as i32,
            (remM - (ss * 1000L)) as i32)
    }

    pub var year: i32 {
        pub get(_: _) { return DateTime.civilFromMilliseconds(stamp.milliseconds).year }
    }
    pub var month: i32 {
        pub get(_: _) { return DateTime.civilFromMilliseconds(stamp.milliseconds).month }
    }
    pub var day: i32 {
        pub get(_: _) { return DateTime.civilFromMilliseconds(stamp.milliseconds).day }
    }
    pub var hour: i32 {
        pub get(_: _) { return DateTime.civilFromMilliseconds(stamp.milliseconds).hour }
    }
    pub var minute: i32 {
        pub get(_: _) { return DateTime.civilFromMilliseconds(stamp.milliseconds).minute }
    }
    pub var second: i32 {
        pub get(_: _) { return DateTime.civilFromMilliseconds(stamp.milliseconds).second }
    }

    // 专用 time_now_parts 单次采样填 12 字节，不从毫秒 time_now 另读
    // 纳秒（否则跨毫秒边界会撕裂）。旧 time_now 的协程/Timer ABI 不变。
    pub static func now(): DateTime {
        const out = core.collections.spanOf\<u8>(12)
        rigi_time_now_parts(out)
        return new DateTime(new TimeStamp(tmReadI64Le(out, 0),
            tmReadI32Le(out, 8)))
    }

    // 时长的毫秒段可占满 i64；先用「公历全跨度向上取整至毫秒」
    // 排除显然越界的值，再取反/相加，避免 -i64_MIN 与任何回绕。
    // 此门仅保证后续中间运算安全，不能代替最终归一化结果的界检查：
    // 边界处纳秒进位/借位可能使暂态毫秒越界而最终值合法。
    priv static func shifted(base: TimeStamp, span: TimeSpan, subtract: bool): DateTime {
        const parts = span.asStamp()
        if ((parts.milliseconds < (-315537897600000L))
                or (parts.milliseconds > 315537897600000L)) {
            throw new core.OutOfBoundException("DateTime 运算超出 UTC 公历范围")
        }
        var deltaMs = parts.milliseconds
        var deltaNs = parts.nanoseconds
        if (subtract) {
            deltaMs = -deltaMs
            deltaNs = -deltaNs
        }
        var nsSum = base.nanoseconds + deltaNs
        if (nsSum < 0) {
            nsSum = nsSum + 1000000
            deltaMs = deltaMs - 1L
        } else if (nsSum >= 1000000) {
            nsSum = nsSum - 1000000
            deltaMs = deltaMs + 1L
        }
        // base 与已限幅 delta 的毫秒和远小于 i64；只检查最终公历时刻。
        const finalMs = base.milliseconds + deltaMs
        if ((finalMs < (-62135596800000L)) or (finalMs > 253402300799999L)) {
            throw new core.OutOfBoundException("DateTime 运算超出 UTC 公历范围")
        }
        return new DateTime(new TimeStamp(finalMs, nsSum))
    }

    pub operator plus(span: TimeSpan): DateTime {
        return DateTime.shifted(stamp, span, false)
    }

    pub operator minus(span: TimeSpan): DateTime {
        return DateTime.shifted(stamp, span, true)
    }

    // 公历两端的毫秒差远小于 i64；只在纳秒借位后构造规范化分解，
    // 不把完整数千年的总纳秒乘回 i64（totalNanoseconds 仅是窄投影）。
    pub operator minus(other: DateTime): TimeSpan {
        var msDiff = stamp.milliseconds - other.stamp.milliseconds
        var nsDiff = stamp.nanoseconds - other.stamp.nanoseconds
        if (nsDiff < 0) {
            nsDiff = nsDiff + 1000000
            msDiff = msDiff - 1L
        }
        return TimeSpan.fromParts(msDiff, nsDiff)
    }

    pub operator compareTo(other: DateTime): core.ComparisonResult {
        if (stamp.milliseconds < other.stamp.milliseconds) { return .LesserThanAnother }
        if (stamp.milliseconds > other.stamp.milliseconds) { return .GreaterThanAnother }
        if (stamp.nanoseconds < other.stamp.nanoseconds) { return .LesserThanAnother }
        if (stamp.nanoseconds > other.stamp.nanoseconds) { return .GreaterThanAnother }
        return .Equal
    }

    pub operator equals(other: DateTime): bool {
        return ((stamp.milliseconds == other.stamp.milliseconds)
            and (stamp.nanoseconds == other.stamp.nanoseconds))
    }

    // ---- 文本（§4.9.2：RFC 3339 收窄子集） ----
    //
    // 默认输出 UTC（大写 T/Z）；秒小数去尾零、全零省略；显式零偏移仍
    // 输出 Z；非零偏移输出 ±HH:MM。默认文本可恢复原时刻及纳秒（往返
    // 断言见 Tests/e2e/rigi/time_text.rg）。日历计算为纯 Rigi 整数运
    // 算 + ASCII 扫描，不读系统时区，不因 OS/语言/locale 改变（D6）。

    pub override func toString(): String { return toString(0) }

    pub func toString(offsetMinutes: i32): String {
        if ((offsetMinutes < (-1439)) or (offsetMinutes > 1439)) {
            throw new core.OutOfBoundException(
                "显示偏移超出范围 -23:59..+23:59：${offsetMinutes}")
        }
        // 显示毫秒 = UTC 毫秒 + 偏移平移（界内有界，i64 不溢出）；
        // 显示结果超出 0001..9999 报范围错误（非文本范围沿用
        // OutOfBoundException，§4.9.5）
        const displayMs = stamp.milliseconds + ((offsetMinutes as i64) * 60000L)
        if ((displayMs < (-62135596800000L))
                or (displayMs > 253402300799999L)) {
            throw new core.OutOfBoundException(
                "显示偏移使日期超出 UTC 公历范围（0001..9999）")
        }
        // 文本与 UTC 属性同用 civil-from-days；显示偏移只改变传入毫秒，
        // 绝不修改 stamp 或把原始输入偏移误作 UTC 属性。
        const civil = DateTime.civilFromMilliseconds(displayMs)
        // 秒以下纳秒（0..999999999）
        const fracNs = (((civil.millisecond as i64) * 1000000L)
            + (stamp.nanoseconds as i64))
        const out = new core.text.StringBuilder()
        out.append(tmPad4(civil.year as i64))
        out.append("-")
        out.append(tmPad2(civil.month as i64))
        out.append("-")
        out.append(tmPad2(civil.day as i64))
        out.append("T")
        out.append(tmPad2(civil.hour as i64))
        out.append(":")
        out.append(tmPad2(civil.minute as i64))
        out.append(":")
        out.append(tmPad2(civil.second as i64))
        if (fracNs != 0L) {
            out.append(".")
            out.append(tmFrac9(fracNs))
        }
        if (offsetMinutes == 0) {
            // 显式零偏移仍输出 Z
            out.append("Z")
        } else {
            var om = offsetMinutes
            if (om < 0) {
                out.append("-")
                om = -om
            } else {
                out.append("+")
            }
            out.append(tmPad2((om / 60) as i64))
            out.append(":")
            out.append(tmPad2((om % 60) as i64))
        }
        return out.toString()
    }
}

// 旧 time_now 毫秒 ABI 仍由 core.coroutine/Timer 使用；DateTime 只经
// 新专用单次采样写入 12 字节 Span（i64 ms + i32 ns余量，小端）。
@NativeLibrary("rigi_rt")
@NativeSymbol("time_now_parts")
priv native func rigi_time_now_parts(out: Span\<u8>)

priv func tmReadI64Le(bytes: Span\<u8>, offset: i32): i64 {
    var value: i64 = 0L
    var i = 0
    while (i < 8) {
        const b: u8 = (bytes[offset + i] if? (0 as u8))
        value = value | ((b as i64) << ((i * 8) as i64))
        i = i + 1
    }
    return value
}

priv func tmReadI32Le(bytes: Span\<u8>, offset: i32): i32 {
    var value: i32 = 0
    var i = 0
    while (i < 4) {
        const b: u8 = (bytes[offset + i] if? (0 as u8))
        value = value | ((b as i32) << (i * 8))
        i = i + 1
    }
    return value
}

// ============================================================================
// 施工块 6-2：core.time 文本格式（STDLIB §4.9.2 / §4.9.3 / §4.9.5，D6）
//
//   - TimeParseException：DateTime/TimeSpan 文本解析失败载体（§4.9.5），
//     区分非法文本（isOutOfRange=false）与不可表示的范围（true），并
//     携带失败处零基 UTF-8 字节位置（形态同 core.text
//     NumberParseException）。tryParse 只把该类预期解析失败转成 null，
//     不吞其他异常。
//   - DateTime.parse/tryParse：RFC 3339 收窄子集——
//     YYYY-MM-DDTHH:mm:ss[.fraction](Z|±HH:MM)，ASCII、大写 T/Z、
//     fraction 1～9 位直接形成纳秒（不经浮点）、严格消费全文（不去
//     空白、不用通用整数前缀解析放过非法尾部）。拒绝缺失偏移、非法
//     日期、秒 60、24:00、未知偏移 -00:00、超九位小数与其他形态；
//     当地与换算后 UTC 日期都须落在 0001..9999。数值偏移范围
//     -23:59..+23:59，解析换算为 UTC（DateTime 不保存系统时区或
//     原始输入偏移）。
//   - TimeSpan.parse/tryParse：ISO 8601 Duration 日时分秒子集
//     （dayTimeDuration 单位组织与整体负号）[-]P[nD][T[nH][nM][nS]]，
//     仅秒允许 1～9 位小数；拒绝 Y/W/T 前 M、分量符号、前导加号；
//     P、PT、P1DT 失败；非规范化整数分量按固定单位求和（PT90M=90
//     分钟）；总值不截断、不回绕，超 TimeSpan 范围报超范围类。
//   - 日历与文本行为纯 Rigi 计算 + ASCII 扫描：不读系统时区，不依赖
//     OS/系统语言/进程 locale（D6）。
//
// ext 静态扩展声明（DateTime.parse 等）按 §4.4 不获得目标类型私有
// 成员的访问特权：DateTime 侧所需（stamp/init）本为 pub 直接可用；
// TimeSpan 侧经 pub 工厂与运算组合（全程溢出检查，OutOfBoundException
// 捕获后归为超范围类 TimeParseException）；日历助手为文件级 priv。
// ============================================================================

// ===== 解析失败异常（§4.9.5）=====

pub open class TimeParseException : core.RuntimeException {
    // 失败种类投影：false=非法文本（格式错误），true=不可表示的范围
    pub var isOutOfRange: bool
    // 失败处零基 UTF-8 字节位置（无法给出时 null）
    pub var position: i64?

    // 兼容入口：仅消息（种类=非法文本、无位置）
    pub init(text: String) {
        message = text
        isOutOfRange = false
        const none: i64? = null
        position = none
    }

    // 消息 + 种类
    pub init(text: String, outOfRange: bool) {
        message = text
        isOutOfRange = outOfRange
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

// ===== 内部扫描与日历助手（priv，仅本文件可用；全部 ASCII 判定）=====

// Span 指定偏移的单字节读取（界内前提由调用方保证；core.text parse.rg
// intByteAt 同款）
priv func tmByteAt(bytes: Span\<u8>, offset: i64): u8 {
    return (bytes[(offset as i32)] if? (0 as u8))
}

// ASCII 十进制数字判定（只接受 ASCII，与系统语言/locale 无关）
priv func tmIsDigit(b: u8): bool {
    return ((b >= (48 as u8)) and (b <= (57 as u8)))
}

// 固定长度纯数字段：恰好 count 个 ASCII 数字，任一位置非数字即非法
// 文本（位置精确到字节偏移）；值按十进制累积（count ≤ 4，无溢出）
priv func tmReadDigits(bytes: Span\<u8>, start: i64, count: i64): i64 {
    var v = 0L
    var k = 0L
    while (k < count) {
        const b = tmByteAt(bytes, (start + k))
        if (not tmIsDigit(b)) {
            throw new TimeParseException(
                "日期时间文本含有非数字字符（位置 ${start + k}）", false, (start + k))
        }
        v = (v * 10L) + ((b - (48 as u8)) as i64)
        k = k + 1L
    }
    return v
}

// 变长无符号分量扫描（Duration 的 D/H/M/S 整数部分）：至少一位数字
// （调用方已预检首字符为数字），连续读取到非数字止；i64 累积显式
// 防溢出（整数基础运算隐式回绕，不用），超 i64 即不可表示的范围
priv func tmScanUint(bytes: Span\<u8>, start: i64, n: i64): core.Pair\<i64, i64> {
    var v = 0L
    var i = start
    while ((i < n) and tmIsDigit(tmByteAt(bytes, i))) {
        const d = (tmByteAt(bytes, i) - (48 as u8)) as i64
        if (v > ((9223372036854775807L - d) / 10L)) {
            throw new TimeParseException(
                "Duration 分量超出 i64 可表示范围（位置 ${i}）", true, i)
        }
        v = (v * 10L) + d
        i = i + 1L
    }
    return new core.Pair\<i64, i64>(v, i)
}

// 10^k（0 ≤ k ≤ 8；fraction 位数补齐到纳秒位用）
priv func tmPow10(k: i64): i64 {
    var v = 1L
    var t = 0L
    while (t < k) {
        v = v * 10L
        t = t + 1L
    }
    return v
}

// 两位/四位零填充十进制（v 非负前提；ASCII 输出）
priv func tmPad2(v: i64): String {
    if (v < 10L) { return "0${v}" }
    return "${v}"
}

priv func tmPad4(v: i64): String {
    if (v < 10L) { return "000${v}" }
    if (v < 100L) { return "00${v}" }
    if (v < 1000L) { return "0${v}" }
    return "${v}"
}

// 秒小数输出：fracNs ∈ [1, 999999999] 的 9 位十进制去尾零（不经浮点；
// 全零由调用方先行省略小数点）
priv func tmFrac9(fracNs: i64): String {
    var s = ""
    var div = 100000000L
    while (div > 0L) {
        s = "${s}${(fracNs / div) % 10L}"
        div = div / 10L
    }
    while (s.endsWith("0")) {
        s = s.slice(0L, s.length - 1L)
    }
    return s
}

// 公历闰年：4 的倍数且（非 100 的倍数或 400 的倍数）
priv func tmIsLeap(y: i64): bool {
    return ((y % 4L) == 0L) and (((y % 100L) != 0L) or ((y % 400L) == 0L))
}

priv func tmDaysInMonth(y: i64, m: i64): i64 {
    if (m == 2L) {
        if (tmIsLeap(y)) { return 29L }
        return 28L
    }
    if (((m == 4L) or (m == 6L)) or ((m == 9L) or (m == 11L))) { return 30L }
    return 31L
}

// days-from-civil（Howard Hinnant 算法；前提 y ≥ 1 ⇒ 调整后的 yy ≥ 0，
// i64 向零截断除法语义与 floor 一致）：公历 y-m-d 距 Unix 纪元的天数
priv func tmDaysFromCivil(y: i64, m: i64, d: i64): i64 {
    var yy = y
    if (m <= 2L) { yy = y - 1L }
    const era = yy / 400L
    const yoe = yy - (era * 400L)
    var mp = m + 9L
    if (m > 2L) { mp = m - 3L }
    const doy = ((((153L * mp) + 2L) / 5L) + d) - 1L
    const doe = ((((yoe * 365L) + (yoe / 4L)) - (yoe / 100L)) + doy)
    return ((era * 146097L) + doe) - 719468L
}

// ===== DateTime.parse（§4.9.2）=====

pub ext static func DateTime.parse(text: String): DateTime {
    const bytes = text.toUtf8Span()
    const n: i64 = (bytes.length as i64)
    // 固定前缀 YYYY-MM-DDTHH:mm:ss = 19 字节 + 偏移（Z=1 或 ±HH:MM=6），
    // 更短必残缺；更长由严格全文消费把关
    if (n < 20L) {
        throw new TimeParseException(
            "DateTime 文本过短：须为 YYYY-MM-DDTHH:mm:ss[.fraction]Z 或末尾 ±HH:MM", false, n)
    }
    const y = tmReadDigits(bytes, 0L, 4L)
    if (tmByteAt(bytes, 4L) != (45 as u8)) {
        throw new TimeParseException("位置 4 应为 '-'", false, 4L)
    }
    const mo = tmReadDigits(bytes, 5L, 2L)
    if (tmByteAt(bytes, 7L) != (45 as u8)) {
        throw new TimeParseException("位置 7 应为 '-'", false, 7L)
    }
    const dy = tmReadDigits(bytes, 8L, 2L)
    if (tmByteAt(bytes, 10L) != (84 as u8)) {
        throw new TimeParseException("位置 10 应为大写 'T'（不接受小写 t 或其他分隔）", false, 10L)
    }
    const hh = tmReadDigits(bytes, 11L, 2L)
    if (tmByteAt(bytes, 13L) != (58 as u8)) {
        throw new TimeParseException("位置 13 应为 ':'", false, 13L)
    }
    const mi = tmReadDigits(bytes, 14L, 2L)
    if (tmByteAt(bytes, 16L) != (58 as u8)) {
        throw new TimeParseException("位置 16 应为 ':'", false, 16L)
    }
    const ss = tmReadDigits(bytes, 17L, 2L)
    var i = 19L
    // 可选小数：1～9 位直接形成纳秒（不经浮点）；超九位为不可表示的
    // 范围（形态合法但精度超出本库承诺，不截断）
    var fracNs = 0L
    if (tmByteAt(bytes, i) == (46 as u8)) {
        i = i + 1L
        const fs = i
        while ((i < n) and tmIsDigit(tmByteAt(bytes, i))) {
            fracNs = (fracNs * 10L) + ((tmByteAt(bytes, i) - (48 as u8)) as i64)
            i = i + 1L
        }
        const k = i - fs
        if (k == 0L) {
            throw new TimeParseException("小数点后没有数字（位置 ${fs}）", false, fs)
        }
        if (k > 9L) {
            throw new TimeParseException(
                "小数超过九位：本库不截断超精度文本（位置 ${fs}）", true, fs)
        }
        fracNs = fracNs * tmPow10(9L - k)
    }
    // 偏移：Z 或 ±HH:MM（显式整分钟，-23:59..+23:59；拒绝未知偏移
    // -00:00）；缺失偏移在此拒绝
    var offsetMin = 0
    if (i < n) {
        const ob = tmByteAt(bytes, i)
        if (ob == (90 as u8)) {
            i = i + 1L
            offsetMin = 0
        } else if ((ob == (43 as u8)) or (ob == (45 as u8))) {
            if ((i + 6L) != n) {
                throw new TimeParseException(
                    "偏移形态非法：须恰为 ±HH:MM（位置 ${i}）", false, i)
            }
            const oh = tmReadDigits(bytes, (i + 1L), 2L)
            if (tmByteAt(bytes, (i + 3L)) != (58 as u8)) {
                throw new TimeParseException("偏移中应为 ':'", false, (i + 3L))
            }
            const om = tmReadDigits(bytes, (i + 4L), 2L)
            if (oh > 23L) {
                throw new TimeParseException("偏移小时越界（范围 -23:59..+23:59）", false, (i + 1L))
            }
            if (om > 59L) {
                throw new TimeParseException("偏移分钟越界", false, (i + 4L))
            }
            if (((ob == (45 as u8)) and (oh == 0L)) and (om == 0L)) {
                throw new TimeParseException("未知偏移 -00:00（RFC 3339 未知本地偏移语义，本库拒绝）", false, i)
            }
            var signed = ((oh * 60L) + om) as i32
            if (ob == (45 as u8)) { signed = -signed }
            offsetMin = signed
            i = i + 6L
        } else {
            throw new TimeParseException(
                "缺失偏移：须为 Z 或 ±HH:MM（位置 ${i}）", false, i)
        }
    } else {
        throw new TimeParseException("缺失偏移：须为 Z 或 ±HH:MM", false, i)
    }
    if (i != n) {
        throw new TimeParseException(
            "DateTime 文本含有无法消费的尾随字符（位置 ${i}）", false, i)
    }
    // 日历校验：真实日期不自动调整；年份 0000 为不可表示的范围
    if (y == 0L) {
        throw new TimeParseException(
            "年份 0000 超出 UTC 公历支持范围（0001..9999）", true, 0L)
    }
    if ((mo < 1L) or (mo > 12L)) {
        throw new TimeParseException("月份越界（1..12）", false, 5L)
    }
    const dim = tmDaysInMonth(y, mo)
    if ((dy < 1L) or (dy > dim)) {
        throw new TimeParseException(
            "非法日期：${y}-${mo}-${dy}（该月无此日，不自动调整）", false, 8L)
    }
    if (hh > 23L) {
        throw new TimeParseException("小时越界：拒绝 24:00", false, 11L)
    }
    if (mi > 59L) {
        throw new TimeParseException("分钟越界", false, 14L)
    }
    if (ss > 59L) {
        throw new TimeParseException("秒值 60 拒绝（本库不表示闰秒）", false, 17L)
    }
    // 当地毫秒（整日 + 日内秒）+ 小数进位毫秒；纳秒余量 0..999999
    const localMs = (((tmDaysFromCivil(y, mo, dy) * 86400000L) + (((((hh * 60L) + mi) * 60L) + ss) * 1000L)) + (fracNs / 1000000L))
    const nsRem = (fracNs % 1000000L) as i32
    // 换算 UTC（DateTime 不保存原始输入偏移）；当地与 UTC 都须界内
    // （当地经 y/mo/dy 校验已界内），UTC 越界为不可表示的范围
    const utcMs = localMs - ((offsetMin as i64) * 60000L)
    if ((utcMs < (-62135596800000L)) or (utcMs > 253402300799999L)) {
        throw new TimeParseException(
            "换算后的 UTC 日期超出支持范围（公元 0001..9999）", true, 0L)
    }
    return new DateTime(new TimeStamp(utcMs, nsRem))
}

pub ext static func DateTime.tryParse(text: String): DateTime? {
    try {
        const v = DateTime.parse(text)
        const boxed: DateTime? = v
        return boxed
    } catch (e: TimeParseException) {
        // 只将预期解析失败转成 null，不吞其他异常（§4.9.5）
        return null
    }
}

// ===== TimeSpan.parse（§4.9.3）=====

pub ext static func TimeSpan.parse(text: String): TimeSpan {
    const bytes = text.toUtf8Span()
    const n: i64 = (bytes.length as i64)
    var i = 0L
    var neg = false
    if (i < n) {
        const b0 = tmByteAt(bytes, 0L)
        if (b0 == (45 as u8)) {
            neg = true
            i = 1L
        } else if (b0 == (43 as u8)) {
            throw new TimeParseException("Duration 不接受前导加号（位置 0）", false, 0L)
        }
    }
    if ((i >= n) or (tmByteAt(bytes, i) != (80 as u8))) {
        throw new TimeParseException("Duration 缺少 P 标志（位置 ${i}）", false, i)
    }
    i = i + 1L
    // 日分量（可选）：整数 + D；T 之前的 M 为月份，本库拒绝（P1M 在
    // 此处因单位不是 D 失败）
    var days = 0L
    var hasAny = false
    if ((i < n) and tmIsDigit(tmByteAt(bytes, i))) {
        const dc = tmScanUint(bytes, i, n)
        days = dc.key
        if ((dc.value >= n) or (tmByteAt(bytes, dc.value) != (68 as u8))) {
            throw new TimeParseException(
                "日分量后必须是大写 'D'（位置 ${dc.value}）", false, dc.value)
        }
        i = dc.value + 1L
        hasAny = true
    }
    // T 后时间分量：H/M/S 顺序固定不重复（各自可选但至少一个：PT、P1DT
    // 失败）；仅秒允许 1～9 位小数。分量单位在数字后才出现，故按单位
    // 字母分派并用 unitFloor 守卫顺序（H=1、M=2、S=3 递增）
    var hours = 0L
    var minutes = 0L
    var seconds = 0L
    var fracNs = 0L
    var hasTime = false
    if ((i < n) and (tmByteAt(bytes, i) == (84 as u8))) {
        i = i + 1L
        var unitFloor = 0
        var groups = 0
        while ((i < n) and tmIsDigit(tmByteAt(bytes, i))) {
            const gc = tmScanUint(bytes, i, n)
            if (gc.value >= n) {
                throw new TimeParseException(
                    "时间分量缺少单位（位置 ${gc.value}）", false, gc.value)
            }
            const ub = tmByteAt(bytes, gc.value)
            if (ub == (72 as u8)) {
                // H：只允许作为首个时间单位出现
                if (unitFloor > 0) {
                    throw new TimeParseException(
                        "单位顺序固定不重复：H 须在 M/S 之前（位置 ${gc.value}）", false, gc.value)
                }
                unitFloor = 1
                hours = gc.key
                i = gc.value + 1L
            } else if (ub == (77 as u8)) {
                // M：前驱只能是 H 或首组
                if (unitFloor > 1) {
                    throw new TimeParseException(
                        "单位顺序固定不重复：M 须在 S 之前（位置 ${gc.value}）", false, gc.value)
                }
                unitFloor = 2
                minutes = gc.key
                i = gc.value + 1L
            } else if ((ub == (83 as u8)) or (ub == (46 as u8))) {
                // S（或带小数的秒：数字后直接跟点号）：至多一次
                if (unitFloor > 2) {
                    throw new TimeParseException(
                        "单位顺序固定不重复：S 至多一次（位置 ${gc.value}）", false, gc.value)
                }
                unitFloor = 3
                seconds = gc.key
                i = gc.value
                if (ub == (46 as u8)) {
                    i = i + 1L
                    const fs = i
                    var fv = 0L
                    while ((i < n) and tmIsDigit(tmByteAt(bytes, i))) {
                        fv = (fv * 10L) + ((tmByteAt(bytes, i) - (48 as u8)) as i64)
                        i = i + 1L
                    }
                    const k = i - fs
                    if (k == 0L) {
                        throw new TimeParseException("秒小数点后没有数字（位置 ${fs}）", false, fs)
                    }
                    if (k > 9L) {
                        throw new TimeParseException(
                            "秒小数超过九位：本库不截断超精度文本（位置 ${fs}）", true, fs)
                    }
                    fracNs = fv * tmPow10(9L - k)
                }
                if ((i >= n) or (tmByteAt(bytes, i) != (83 as u8))) {
                    throw new TimeParseException(
                        "秒分量后必须是大写 'S'（位置 ${i}）", false, i)
                }
                i = i + 1L
            } else {
                throw new TimeParseException(
                    "时间分量单位必须是 H/M/S 之一（位置 ${gc.value}）", false, gc.value)
            }
            groups = groups + 1
        }
        if (groups == 0) {
            throw new TimeParseException(
                "T 后至少一个时间分量（P、PT、P1DT 均失败）", false, i)
        }
        hasTime = true
    }
    if (i != n) {
        throw new TimeParseException(
            "Duration 含有无法消费的尾随字符（位置 ${i}）", false, i)
    }
    if ((not hasAny) and (not hasTime)) {
        throw new TimeParseException(
            "Duration 至少出现一个数量与单位（P、PT 失败）", false, i)
    }
    // 组合：pub 工厂与运算全程显式检查溢出（整数基础运算不用于这里），
    // OutOfBoundException 统一归为不可表示的范围类；不截断超精度文本、
    // 不回绕溢出。负号整体作用于 P 前。
    try {
        var span = TimeSpan.fromDays(days)
        span = span + TimeSpan.fromHours(hours)
        span = span + TimeSpan.fromMinutes(minutes)
        span = span + TimeSpan.fromSeconds(seconds)
        span = span + TimeSpan.fromNanoseconds(fracNs)
        if (neg) {
            span = -span
        }
        return span
    } catch (e: core.OutOfBoundException) {
        throw new TimeParseException("Duration 总值超出 TimeSpan 表示范围", true, n)
    }
}

pub ext static func TimeSpan.tryParse(text: String): TimeSpan? {
    try {
        const v = TimeSpan.parse(text)
        const boxed: TimeSpan? = v
        return boxed
    } catch (e: TimeParseException) {
        // 只将预期解析失败转成 null，不吞其他异常（§4.9.5）
        return null
    }
}

// ============================================================================
// 施工块 6-3：单调时钟 / MonotonicInstant / Stopwatch（STDLIB §4.9.4 /
// §4.9.5，D6）
//
//   - rigi_monotonic_now_ns：单调时钟原语（纳秒读数）。VM/native 双端
//     同语义（§4.9.4）：计入协程等待与进程未调度的时间，排除整机
//     睡眠/休眠——Windows QueryUnbiasedInterruptTimePrecise（100ns 单
//     位 ×100 转纳秒）、Linux clock_gettime(CLOCK_MONOTONIC)（man7：
//     「does not count time that the system is suspended」）。实际
//     分辨率平台相关，不要求每次读取增加一纳秒。i64 纳秒可表 ~292
//     年，进程存活不可能触达，不设额外溢出分支。
//   - MonotonicInstant：专用值类型，包 i64 纳秒读数。仅用于同一进程
//     时钟域内的比较与求差；顺序采样不倒退但可以相等，跨 Worker
//     有效。不能转换成日期、持久化比较或把原始计数当 Unix 时间戳。
//     不提供 Serializable（§4.9.5：不持久化序列化、不暴露可跨进程
//     恢复的时钟计数状态）。
//   - Stopwatch：local 秒表；不要求 IDisposable，不支持同实例并发。
// ============================================================================

// 单调时钟原语（§4.9.4；@NativeSymbol 必带——缺省符号经 rigi_rt 前缀
// 拼接会落空成 rigi_rigi_monotonic_now_ns）
@NativeLibrary("rigi_rt")
@NativeSymbol("monotonic_now_ns")
priv native func rigi_monotonic_now_ns(): i64

// 单调时刻（§4.9.4/§4.9.5）：进程时钟域内的专用值类型
pub struct MonotonicInstant {
    // 单调时钟读数（纳秒）。非 Unix 时间戳、不可转日期、不持久化
    // （无 @Serializable，§4.9.5）；比较与求差仅限同一进程时钟域
    priv var nanos: i64 {
        get
        set(value: _) { }
    }

    // 内部构造通道：仅编译单元内（MonotonicClock.now() 与运算）使用，
    // 对外不可构造（internal = 编译单元内可见，§16）
    internal init(_ -> nanos) { }

    pub operator compareTo(other: MonotonicInstant): core.ComparisonResult {
        if (nanos < other.nanos) { return .LesserThanAnother }
        if (nanos > other.nanos) { return .GreaterThanAnother }
        return .Equal
    }

    pub operator equals(other: MonotonicInstant): bool {
        return nanos == other.nanos
    }

    // 求差（§4.9.4）：TimeSpan 有符号表示，this 早于 other 得负
    // TimeSpan（与 DateTime 相减同口径）。两读数均非负且同域，差
    // 不可能触达 i64 边界（~292 年量级），基础减法不回绕到异常值
    pub operator minus(other: MonotonicInstant): TimeSpan {
        return TimeSpan.fromNanoseconds(nanos - other.nanos)
    }
}

// 单调时钟门面（§4.9.4）：static now() 取 MonotonicInstant
pub class MonotonicClock {
    // 无实例状态；priv init 防误构造
    priv init() { }

    // 顺序采样不倒退但可以相等（实际分辨率平台相关）；跨 Worker
    // 使用有效（同一进程时钟域）
    pub static func now(): MonotonicInstant {
        return new MonotonicInstant(rigi_monotonic_now_ns())
    }
}

// 本地秒表（§4.9.4）：start/stop/reset/restart、只读 elapsed 与
// isRunning。初始停止且为零；start 对运行中实例、stop 对已停止实例
// 均幂等；再次 start 继续累计（不丢前段）；reset 清零并停止；
// restart 清零并开始。运行中读取 elapsed 包含当前区间（用
// MonotonicClock.now()）。不因内存状态管理而要求 IDisposable
// （§4.9.4）；不支持同实例并发操作（无内部同步，协程间共享需自加锁）
pub class Stopwatch {
    // 已停止区间的累计
    priv var accumulated: TimeSpan
    // 当前运行区间起点（停止状态无意义）
    priv var startMark: MonotonicInstant
    priv var running: bool

    pub init() {
        accumulated = TimeSpan.fromNanoseconds(0L)
        startMark = MonotonicClock.now()
        running = false
    }

    pub var isRunning: bool {
        pub get(_: _) { return running }
    }

    pub var elapsed: TimeSpan {
        pub get(_: _) {
            if (not running) { return accumulated }
            // 运行中：累计 + 当前区间（起点至今之差非负）
            return accumulated + (MonotonicClock.now() - startMark)
        }
    }

    // 幂等：运行中再次 start 不改变状态（§4.9.4）
    pub func start() {
        if (not running) {
            startMark = MonotonicClock.now()
            running = true
        }
    }

    // 幂等：已停止再次 stop 不改变状态；停止时把当前区间并入累计
    pub func stop() {
        if (running) {
            accumulated = elapsed
            running = false
        }
    }

    pub func reset() {
        accumulated = TimeSpan.fromNanoseconds(0L)
        running = false
    }

    pub func restart() {
        accumulated = TimeSpan.fromNanoseconds(0L)
        startMark = MonotonicClock.now()
        running = true
    }
}
