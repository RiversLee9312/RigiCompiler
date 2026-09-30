# 各 NS 的最小能力：core.system / core.math（§4.10 + §4.11）

> 本文件是 [STDLIB.md](../STDLIB.md)（Rigi 标准库 MVP 设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

### 4.10 `core.system`

本模块整体留待后续设计与实现，不作为首版依赖。未来提供参数访问、环境变量查询、当前目录查询和基础平台信息，以下为后续建议基线。

- 衔接已有入口参数语法，验证宿主实际传参路径，不建设命令行解析框架。
- 建议返回参数快照，明确是否包含可执行文件名。
- 区分未设置的环境变量和空字符串值。
- 当前目录/环境变量是进程级状态；模块初版优先读取，修改能力另行定义并发影响。
- 环境层可以用 String 表达目录，Path 转换归 `core.fs`，避免相互依赖。
- 正常退出沿用入口返回值，新增退出 API 不得绕过协程收尾和资源清理。

子进程启动、shell 执行和进程管道不在本次最小集合。

### 4.11 `core.math`

提供基础数值函数、常量与通用伪随机数。以下公共行为已确定；任意精度数值、复数、矩阵和统计框架不属于标准库范围，不提供密码学 API。

#### 4.11.1 函数与常量

- 数学入口为本 NS 的普通函数与具体类型重载，如 `abs(value)`、`sqrt(value)`，不引入 Numeric 泛型协议。整数重载覆盖 i8/i16/i32/i64/u8/u16/u32/u64，浮点使用 Rigi 源码类型名 float/double，各重载按自身目标类型运算和返回，不先统一成 double。
- 整数及浮点提供 abs、min/max、clamp。浮点另提供 floor/ceil/trunc/round、sqrt、pow、exp、ln/log2/log10、sin/cos/tan、asin/acos/atan/atan2，以及返回 bool 的 isNaN/isInfinite/isFinite。pow 首版只提供浮点重载，三角函数统一使用弧度；atan2 参数顺序为 y、x。
- 提供 double 常量 pi/e/tau，固定为相应数学常数在 double 中的最近值、正中取偶表示；需要 float 时显式转换。常量在 VM/native 使用相同位表示，不受宿主地区或运行时浮点环境影响。
- 不加入小数点后 N 位的十进制舍入、修改全局浮点舍入环境或独立统计框架；常用聚合通过集合 fold 与数学函数组合。

#### 4.11.2 整数、取整及区间

- abs 保持输入宽度；无符号 abs 返回自身，有符号最小值因其绝对值不能表示而抛 `core.OutOfBoundException`。这是 abs 的库契约，不改变内建整数加减乘及取负的既有回绕语义。
- floor 向负无穷、ceil 向正无穷、trunc 向零。round 接收 RoundingMode，默认 `.ToEven`（最近值，中点取偶），另提供 `.AwayFromZero`（最近值，中点远离零）。结果保持原浮点类型，不隐式转换为整数；例如 round(2.5) 为 2，round(3.5) 为 4，round(-2.5, .AwayFromZero) 为 -3。
- 浮点 min/max 任一参数为 NaN 时返回 NaN；正负零并列时 min 取负零、max 取正零，其他相等值按相同数值处理。
- clamp 首先校验 lower/upper：下界大于上界或任一边界为 NaN 时抛 OutOfBoundException，不自动交换。边界合法后，value 为 NaN 时返回 NaN；其他值按 `min(max(value, lower), upper)` 的本库规则处理，允许合法的无穷边界与相等边界。

#### 4.11.3 浮点特殊值与精度

- 浮点数学遵循 NaN/无穷的值语义，不把宿主 errno 或硬件浮点状态直接变为 Rigi 异常。负有限数的 sqrt、负数的 ln/log2/log10、超出 [-1,1] 的 asin/acos、无穷输入的 sin/cos/tan 返回 NaN；ln/log2/log10 的零输入返回负无穷。
- 有限结果溢出得到相应符号的无穷，下溢允许次正规数及带符号零，不开启把所有次正规数直接归零的模式。sqrt(-0) 保留负零，abs(-0) 为正零；取整对已为零的输入保持零的符号，对 NaN/无穷返回相应特殊值。
- pow 固定 `pow(0,0)=1`、`pow(NaN,0)=1`、`pow(1,NaN)=1`。负有限底数与非整数有限指数得到 NaN；零、无穷、奇偶整数指数及正负零组合使用明确的 IEEE 风格结果，VM/native 不因宿主库差异改变分类和符号。具体组合应以独立预期样例覆盖，不能只比较两端实现是否碰巧相同。
- atan2 保留有符号零的象限区别：(+0,+0) 为 +0，(-0,+0) 为 -0，(+0,-0) 为 pi，(-0,-0) 为 -pi；不把原点统一改为异常。NaN 输入传播为 NaN。
- 基础取整、分类、min/max、clamp、abs 要求确定结果；sqrt 要求按目标精度最近值、正中取偶的正确舍入。超越函数允许跨平台末位不同，有限结果误差上限为 4 ULP；以目标格式正确舍入参考值处的间距度量，次正规区使用固定最小间距，NaN/无穷/正负零分类及符号单独验证。
- 不承诺 NaN 载荷或符号往返。不得直接以宿主默认结果定义规范；如 [.NET Pow](https://learn.microsoft.com/en-us/dotnet/api/system.math.pow?view=net-10.0) 所述，底层 C 运行库可能带来平台差异。实现须验证既定误差与特殊值规则，不满足时修正底层实现，不自动放宽契约。

#### 4.11.4 Random 实例与种子

- 提供 local `Random` 类，`new Random(seed: u64)` 使用显式种子，`new Random()` 从系统随机源取得一个 u64 种子。相同种子、相同 API 调用及参数序列，在 VM/native 和 Windows/Linux 产生相同结果；不依赖宿主 Random 类或当前时间决定显式种子的序列。
- 每个实例独立持有状态，不设全局共享随机状态，不支持同实例并发或重入操作。生成方法为普通函数；不提供 IDisposable、自动重播种或随机状态的 Serializable 表示。重放使用种子和调用序列。
- 无参构造由私有 native 原语取得完整的 8 字节系统随机材料，按小端解释为种子后进入同一初始化路径；获取失败抛 `core.IOException`，不静默回退为时间戳、零或固定种子。此内部能力不要求新增 core.system 或对外提供密码学随机 API。
- 标准生成序列固定采用 [xoshiro256** 1.0](https://prng.di.unimi.it/xoshiro256starstar.c)。以 seed 作为 [SplitMix64](https://prng.di.unimi.it/splitmix64.c) 的初始状态，连续取得四个 u64，依次初始化 xoshiro 的四个状态字；所有 u64 运算按模 2^64，移位/旋转及输出在状态更新前后的顺序严格依参考算法。全部 u64 种子包括零均合法。
- 核心算法、种子展开及下述各 API 的位消费方式共同构成可复现契约；发布后改变序列视为兼容性变更，不因更换宿主或优化实现而静默改变。该生成器用于通用伪随机用途，不承诺密码学安全。

#### 4.11.5 Random 输出与范围

- 提供八种整数的 `nextI8/nextI16/nextI32/nextI64/nextU8/nextU16/nextU32/nextU64`。无参版本覆盖对应类型完整值域；每次消耗一个新的 64 位核心输出，取最高的目标宽度位，无符号按位值解释，有符号按二进制补码解释。
- 每种整数方法另提供同类型 `minInclusive, maxExclusive` 参数，产生半开区间 `[minInclusive, maxExclusive)` 的均匀结果。要求 minInclusive < maxExclusive；非法区间抛 OutOfBoundException 且不消耗状态。区间宽度使用同宽无符号数学表示处理，跨过有符号零点也不能溢出为错误宽度。
- 有界取样使用固定拒绝采样：宽度 w、区间大小 r，阈值为 `2^w mod r`；每次从新的核心输出取最高 w 位得到 x，若 x 小于阈值则丢弃并重取，否则返回 minInclusive 加 `x mod r`。不以直接取余引入区间偏差，依据见 [有界随机数取样说明](https://www.pcg-random.org/posts/bounded-rands.html)。单元素区间也按此流程消耗一次核心输出。
- `nextFloat(): float` 取新核心输出的最高 24 位后除以 2^24；`nextDouble(): double` 取最高 53 位后除以 2^53。结果为对应二进制网格上的均匀值，范围 `[0,1)`，可为 0、不能为 1；不先把完整 u64 转为浮点再缩放。
- `nextBool(): bool` 消耗一个核心输出，最高位为 1 时返回 true，否则 false；不提供隐藏的跨调用位缓存。
- `fillBytes(buffer: Span\<u8>, offset: i32, count: i32)` 填充指定范围，另提供整个 Span 的重载。每个核心输出按小端顺序拆成 8 字节依次填入；最后不足 8 字节时丢弃剩余字节，不留到下次调用。合法零长度不消耗状态；负参数、越界或范围求和溢出先报 OutOfBoundException，不修改缓冲区或生成器状态。
- 不同 API 或不同 fillBytes 分块方式可以消耗不同的核心输出数量，因此不承诺相同总字节数但不同调用序列的结果一致。默认只提供均匀整数、上述浮点网格、bool 与字节填充，不附带正态分布或统计框架。

