# 各 NS 的最小能力：core.time（§4.9）

> 本文件是 [STDLIB.md](../STDLIB.md)（Rigi 标准库 MVP 设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

### 4.9 `core.time`

保留 TimeStamp、DateTime、TimeSpan 的 struct 分工，补齐精确时间运算、固定文本格式与独立单调时钟。以下公共行为已确定；sleep、Timer、Alarm 仍归 `core.coroutine`。

#### 4.9.1 表示、范围与运算

- TimeStamp 表示相对于 Unix 纪元的时刻戳，保留 `milliseconds: i64` 与 `nanoseconds: i32`，纳秒字段为毫秒之外的非负余量 `0..999999`；原有字段校验契约保持。DateTime 包装确定时刻并提供日期时间操作，TimeSpan 表示有正负的固定持续时间。
- TimeSpan 扩展为 i64 毫秒加 `0..999999` 纳秒余量，与 TimeStamp 使用相同的规范化数值分解。数学总量为 `milliseconds × 1000000 + nanoseconds`；例如 -1ns 表示为 -1ms 加 999999ns，零只有一种表示。内部计算不得因先把完整总纳秒塞入 i64 而缩小原有毫秒范围。
- 提供 `TimeSpan.fromDays/fromHours/fromMinutes/fromSeconds/fromMilliseconds/fromMicroseconds/fromNanoseconds`，参数为 i64；一天固定 24 小时，各单位转换检查表示范围。相应整数 total 属性返回 i64，向零截断不完整单位；例如 -1.5ms 的 totalMilliseconds 为 -1，-1ns 的 totalMilliseconds 为 0。若目标总量超出 i64，明确报错。
- DateTime 支持加减 TimeSpan、与另一 DateTime 相减得到 TimeSpan；TimeSpan 支持加减、取负、乘除整数及比较。时刻相减保留完整纳秒精度，不再采用现有仅减毫秒字段的行为。TimeSpan 除以整数后不足一纳秒的部分向零截断。DateTime 运算须以规范化毫秒段及纳秒余量计算：合法公历跨度可达数千年，完整总纳秒可能超出 i64，因此不得通过 TimeSpan.totalNanoseconds 或「毫秒差 × 1000000」作中间值；时刻相减应直接构造规范化 TimeSpan。对任意 i64 毫秒段的 TimeSpan，先以足以包含所有合法结果的保守范围排除显然越界值，再归一化进位/借位并检查最终 UTC 界；早期检查不得误拒纳秒调整后合法的边界结果，任何暂态加减或取负（包括 -i64_MIN）均不得回绕。
- TimeSpan 加减必须先归一化纳秒进位或借位，再以最终毫秒段判定可表示性；毫秒字段的暂态和或差即使越过 i64 边界，也可能被至多一次进借位抵消。实现须安全地把该调整折入可表示的操作数，不得先按暂态判溢出，不得依赖整数回绕；最终值真正越界仍抛范围异常。
- 时间运算、构造及单位转换均检查溢出，不采用整数基础运算的隐式回绕；除零抛 `core.DividedByZeroException`，范围与溢出抛 `core.OutOfBoundException`。恰好最小 TimeSpan 取负超出范围时同样报错，最小毫秒携带正纳秒余量时取负则仍可表示。乘整数按完整纳秒幅值精确运算后依最终符号分别检查：正结果上限为最大毫秒加合法纳秒余量，负结果下限为最小毫秒且不得再减少纳秒；负幅值带纳秒时借位规范化，不能先构造超出 i64 的正幅值或计算 `-i64_MIN`。除法向零截断后同样不得产生范围外结果。
- DateTime 使用公历规则，UTC 范围为公元 0001 年起至 9999 年最后一纳秒；构造和运算均验证真实日期，不把非法日期自动调整到下一月。TimeStamp 保留更宽数值范围，但越界不能转换成 DateTime。首版不提供按月份或年份加减的日历运算，不表示独立闰秒。
- DateTime 提供只读 `year/month/day/hour/minute/second: i32`，均从已校验时刻的 UTC 公历分量计算；显式显示偏移与解析输入的原始偏移不得改变属性结果。Unix 纪元前的负毫秒先按向下取整的整日分解，不能将向零截断直接当日号（例如纪元前 1ns 仍属 1969-12-31）；闰年/闰日与 0001、9999 两端必须和 `toString(0)` 同一日历算法，跨 UTC 午夜准确换日，毫秒内纳秒余量不改变这六个整分量。比较和相等按完整时刻或持续时间进行，不丢弃纳秒；时区显示不参与时刻相等判断。

#### 4.9.2 DateTime 文本与数值偏移

- DateTime 表示确定时刻，不保存系统时区或原始输入偏移。首版支持 UTC 与显式整分钟偏移，范围为 `-23:59..+23:59`；解析时换算为 UTC。格式化可传入 offsetMinutes，默认 0；显示偏移导致日期超出 0001..9999 时同样报范围错误。
- 不读取系统时区，不内置时区数据库、夏令时规则或本地化格式模板。日历计算与文本行为不因操作系统、系统语言或进程 locale 改变。
- `DateTime.parse/tryParse` 接受 `YYYY-MM-DDTHH:mm:ss[.fraction]Z` 或末尾为 `±HH:MM` 的形式。使用 ASCII、大写 T/Z；fraction 为 1～9 位，直接形成纳秒，不先经过浮点。严格消费全文，不去除空白，不调用通用整数的前缀解析来放过非法尾部。
- 拒绝缺失偏移、非法日期、秒值 60、24:00、未知偏移 `-00:00`、超过九位小数及其他文本形态；输入当地日期与换算后的 UTC 日期都须处于支持范围。这是明确收窄的 [RFC 3339](https://www.rfc-editor.org/rfc/rfc3339) 子集，不宣称接受其全部表示。
- toString 默认输出 UTC，大写 T/Z，秒的小数部分去掉末尾零，全零时省略小数点及小数。显式零偏移仍输出 Z；非零偏移输出 `±HH:MM`。默认文本能够恢复原时刻及纳秒。

#### 4.9.3 TimeSpan 默认使用 ISO 8601 Duration

- `TimeSpan.parse/tryParse/toString` 默认使用 ISO 8601 Duration 的日时分秒子集，采用 [dayTimeDuration](https://www.w3.org/TR/xmlschema11-2/#dayTimeDuration) 的单位组织与整体负号：`[-]P[nD][T[nH][nM][n[.fraction]S]]`。本库进一步限制秒小数最多九位并严格拒绝空白，不隐含 XML 的空白折叠行为。
- 只接受 D、T 后的 H/M/S；年 Y、T 之前的月 M、周 W 均拒绝，不把年/月猜测成固定天数。仅秒允许小数，使用点号，整数部分至少一位；有小数点时后面必须有 1～9 位数字。单位大写且顺序固定，不重复，分量内部不带正负号；整体负号只能在 P 前，不接受前导加号。
- 至少出现一个数量与单位；T 出现后至少有一个时间分量，因此 P、PT、P1DT 均失败。非规范化的整数分量可按固定单位求和，例如 PT90M 表示 90 分钟；总值须落在 TimeSpan 范围内，不截断超精度文本或回绕溢出。
- 输出先按绝对时长分解为天、0～23 小时、0～59 分、0～不足 60 秒，省略零分量及无用的 T；秒小数去掉末尾零。非零负值加整体负号，零统一输出 `PT0S`，负零输入也归一为该值。
- 固定样例：1 天 2 时 3 分 4.5 秒输出 `P1DT2H3M4.5S`；-1ns 输出 `-PT0.000000001S`；PT90M 规范输出 `PT1H30M`；P1M、P1W、PT0.0000000001S 拒绝。旧的 `天数.hh:mm:ss` 形态不是默认解析或输出格式。

#### 4.9.4 时钟、Stopwatch 与定时器衔接

- `DateTime.now()` 返回系统 UTC 时刻，保留平台可提供的亚毫秒部分，不伪造纳秒精度。墙上时钟可能因校时跳变，不用于测量持续时间；其实际分辨率与准确度不承诺各平台相同。
- `DateTime.now()` 的毫秒与纳秒余量取自**同一次**系统采样：运行时原语一次性写入 12 字节缓冲（小端布局：i64 毫秒 + i32 毫秒外纳秒余量 `0..999999`），不在毫秒读数之外另行采样纳秒——两次采样会跨毫秒边界把时刻撕裂。实现语义：Windows 用 `GetSystemTimePreciseAsFileTime`，FILETIME 本身为 100ns 粒度，纳秒余量是其 100 倍放大，不声称 1ns 精度；Linux 用 `clock_gettime(CLOCK_REALTIME)` 的同一 timespec 直接分解。纪元前（负）时刻按向下取整的商与非负余数归一（floor 语义），与 TimeStamp 的纳秒余量契约一致。
- 旧的 `rigi_time_now` 毫秒 ABI（仅返回 i64 毫秒）继续保留，供 `core.coroutine` 的 Timer/sleep 毫秒级调度使用；`DateTime.now()` 走上述专用的多分量单次采样原语，两条路径互不影响。
- `MonotonicClock.now()` 返回专用值类型 MonotonicInstant，仅用于同一进程时钟域内的比较与求差；顺序采样不倒退但可以相等，跨 Worker 使用有效。不能转换成日期、持久化比较或把原始计数当 Unix 时间戳。
- 单调时钟计入协程等待、进程未调度的时间，排除整机睡眠/休眠时间。Windows 可使用 [QueryUnbiasedInterruptTimePrecise](https://learn.microsoft.com/en-us/windows/win32/sysinfo/interrupt-time) 对应能力，Linux 可使用 [CLOCK_MONOTONIC](https://man7.org/linux/man-pages/man3/clock_gettime.3.html)；VM/native 使用同一语义，不直接继承语义不同的宿主便利 API。实际分辨率可不同，不要求每次读取增加一纳秒。
- 提供 local Stopwatch，包含 `start/stop/reset/restart`、只读 elapsed 与 isRunning。初始停止且为零；start 对运行中实例、stop 对已停止实例均幂等；再次 start 继续累计，reset 清零并停止，restart 清零并开始。运行中读取 elapsed 包含当前区间，使用上述单调时钟；不因内存状态管理而要求 IDisposable，不支持同实例并发操作。
- 保留现有 sleep/Timer 的毫秒入口和协程等待语义。`Timer.schedule(DateTime)` 在**毫秒网格**上取整（#564 口径）：目标时刻向上取整到毫秒（非零纳秒余量进一位），与当前采样的整毫秒（floor）作差得到延迟；延迟 ≤ 0 立即具备触发条件，实际恢复仍受调度影响。换算只消费两次采样的毫秒读数，不依赖 `DateTime.now()` 的亚毫秒部分——亚毫秒采样粒度因宿主而异（native 墙钟 100ns 级插值、VM 采样较粗），若按「剩余持续时间的符号」分派取整/立即，同一份 schedule 代码在细粒度宿主上会把「now+ε(<1ms)」的亚毫秒未来量误判为已过去而立即触发，与粗粒度宿主的「向上取整为 ≥1ms 等待」分歧；毫秒网格化后两宿主行为一致：亚毫秒未来量不提前到期（取整为 ≥1ms 等待，在最近的可表示毫秒时刻触发），整毫秒相等或已过去恒立即触发。调度后不随墙上时钟调整反复修改既有单调 deadline。

#### 4.9.5 错误、序列化与实现依赖

- DateTime/TimeSpan 文本解析失败抛 `core.time.TimeParseException`，可区分非法文本与不可表示的范围；tryParse 只将预期解析失败转成 null，不吞掉其他异常。非文本构造、运算与单位转换的范围错误沿用 OutOfBoundException。
- TimeStamp、DateTime、TimeSpan 提供 Serializable，使用规范化对象字段表示；恢复同样验证数值范围和不变量，不能仅依赖正常构造时的检查。JSON 不自动把时间对象转换成日期或 Duration 字符串，需要字符串表示时由调用者显式 toString/parse。
- MonotonicInstant 和 Stopwatch 不提供持久化序列化，不暴露可跨进程恢复的时钟计数状态。
- 实现时同步 TimeSpan 的字段表示、时间运算、序列化恢复校验、墙上/单调时钟原语、Timer.schedule 换算及运行时文档；这些是已定契约的实施依赖，不把现有毫秒实现当作最终精度。

