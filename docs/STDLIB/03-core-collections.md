# 各 NS 的最小能力：core 与 core.collections（§4–§4.2）

> 本文件是 [STDLIB.md](../STDLIB.md)（Rigi 标准库 MVP 设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 4. 各 NS 的最小能力

本节给出各模块的已确定契约与后续建议范围，公共行为的裁决集中在 §10。§4.5 文件系统属于首版；§4.10 进程环境属于后续规划，不计入首版施工范围。

### 4.1 `core`

沿用基础类型与协议，例如 `IDisposable`、`Func`/`Action` 家族、`Pair`、异常和现有原子设施。
语言内建类型按现有 bootstrap 机制提供，不声明另一套 `String`、`Array` 或泛型根。

新能力优先放入所属 NS，只有多个模块需要的基础协议才考虑放入 `core`。
现有 `core.IOException` 可以作为 I/O 异常基类，无需先移动它。

### 4.2 `core.collections`

保留 `IEnumerable`/`IEnumerator`、Array/Span 构造入口、List 和 Map，新增 Set、FIFO Queue 及通用集合算法。以下公共行为已确定。

#### 4.2.1 API 组织与容量

- 集合自身操作直接声明在类型内；通用算法使用本 NS 的泛型函数，例如 `map(source, selector)`。不假定当前语言支持向泛型定义直接注入扩展成员，不为集合补库扩展语言语法。
- Array/Span 经适配器接入 IEnumerable，适配器借用原存储；Span 元素仍须满足既有 ValueType 约束。每次 iterate 产生独立枚举器，不把适配器隐式变为快照。
- 保留已有宽度：Array/Span 的长度及索引为 i32，List 的 length/索引及 Map 的 count 为 i64；Set/Queue 的 count 为 i64。公开 i64 不承诺底层容量超过 i32 存储限制，所有容量运算须检查溢出，不能窄化或回绕。
- 普通集合为 local，不支持同实例并发。删除/出队/清空及时释放不再持有的引用，扩容保留已有元素；失败后容器结构仍须有效，不留下键值数量不一致等损坏状态。

#### 4.2.2 List、Map、Set 与 Queue

- List 增加 `clear`、`contains`、`indexOf`、`insert`、`remove`。indexOf 返回首个相等元素的 `i64?` 位置；remove 删除首个相等元素并返回 bool，无匹配不修改。insert 接受 `0..length`，末尾表示追加；非法位置及容量不足报范围错误。既有越界读 null、越界写/删除抛错的规则保持。
- Map 保留 `set/tryGet/containsKey/remove`，增加 clear 及 keys/values 的独立 List 结果。覆盖已有键只更新值，保留原键对象和位置；删除后重新添加排在末尾。tryGet 保持 `V?`，存储 null 与不存在仍由 containsKey 区分。
- Set 提供 `add/remove/contains/clear/count`，保持插入顺序。add/remove 返回是否实际增删；重复添加或删除不存在的元素不改变集合。删除后重新添加排在末尾。
- Map 键与 Set 元素均严格采用既有 `==`（equals-or-hash 判等链），不改用 toString，不隐式增加引用身份或其他相等判断。浮点 NaN 不等于自身，因此可出现多个 NaN，按 NaN 值查找仍不匹配；正负零按既有数值相等规则处理。
- 键及 Set 元素在存入期间应保持判等依据稳定。首版允许线性查找，不宣称哈希表复杂度；未来采用哈希桶也必须先证明不会改变既有命中语义，不能假定自定义 equals 必然与 hash 一致。
- Queue 提供 `enqueue/dequeue/peek/clear/count`，按 FIFO 出队和遍历；内部采用可扩容环形缓冲区。dequeue/peek 在空队列抛 `core.NoSuchElementException`。`tryDequeue/tryPeek` 返回 `Pair\<bool, T?>`：第一项表示成功，成功值可以是 null；失败为 false/null。

#### 4.2.3 通用算法与排序

- 首版提供 `map/filter/fold/any/all/contains/find/findIndex/toArray/toList/sorted`。fold 显式接收初始值；空序列 any 为 false、all 为 true、fold 返回初始值。contains 沿用元素 `==`，any/all/contains/find/findIndex 在结果确定后短路。
- find 返回 `Pair\<bool, T?>`，区分命中 null 与未命中；findIndex 返回零基 `i64?`，未命中为 null。
- 集合变换立即求值。map/filter/sorted 返回新的 List，toArray/toList 创建独立容器；复制元素值而不隐式深复制引用对象。输入按顺序遍历一次，map/filter 对访问到的元素各调用一次回调；不预先枚举计数后再遍历。
- predicate/selector/comparer 是普通函数，在当前协程运行，可以挂起，不自动创建 Task。不得在回调中重入修改正在操作的输入或目标集合；其他用户副作用不自动回滚，回调异常按既有单异常模型传播。
- 排序显式接收 `Func\<ComparisonResult, T, T>`；该 Func 的第一个泛型参数是返回类型。不假定无约束 T 能使用 `<`。提供常用标量和 String 的比较辅助函数，String 遵守 §4.3 的标量字典序。
- 浮点排序辅助函数把 NaN 排在所有非 NaN 值之后，NaN 之间排序相等，正负零排序相等；此规则不改变 `==` 或 Map/Set 判等。调用者提供的 comparer 须保持自洽、传递且在本次排序期间稳定。
- 排序稳定。sorted 返回新集合，sortInPlace 修改目标集合；原地排序先在临时存储完成全部比较与排序，再写回，比较器抛错时排序自身尚未修改原集合。该保证不回滚回调对外部对象造成的副作用；原地排序不是跨协程事务。
- 首版不建设独立查询语言或优化器，不提供惰性查询链；常用组合继续放在本 NS。

#### 4.2.4 遍历与修改

- List/Map/Set/Queue 对增删、赋值、clear 和原地排序进行修改检测，使已有枚举器失效；之后的 moveNext/current 抛 `core.IllegalStateException`。成功赋值即使写入相同值也失效，成功 clear/原地排序同样失效，**空 List 成功原地排序也算修改**；比较器抛错前排序自身尚未修改目标，也不使既有枚举器无端失效（回调对外部对象的副作用不在此保证内）。重复添加已有 Set 元素、删除不存在元素不计修改。元素对象内部字段变化不属于容器修改，不承诺检测。
- 首次 moveNext 前或正常返回 false 后，current 抛 `core.NoSuchElementException`；正常结束后的 moveNext 持续返回 false。枚举器因集合修改而失效时优先报告失效，不伪装成正常结束。
- Array/Span 的借用适配器不新增修改计数；通过任意别名在遍历期间写入该存储属于不支持的用法，首版不承诺检测。

#### 4.2.5 Set/Queue 的标准序列化行为

- **Set/Queue 不属于 SB，也不直接提供 Serializable。** 它们允许任意元素类型，不因序列化为类型参数强加 Serializable 约束。
- 序列化必须由调用者显式转换为 Array/List，保持 Set 的插入顺序及 Queue 的出队顺序；恢复后再按同一顺序构造 Set/Queue。转换为序列化容器后，元素仍须满足该容器的序列化要求；转换本身不使不可序列化元素获得能力。
- 恢复 Set 时按 add 的既定判等去重，恢复 Queue 时保留全部元素和顺序；不提供保留原 Set/Queue 容器身份或其图引用的隐式桥接。
- Serializable 对象不能直接把 Set/Queue 作为应序列化字段；应显式使用 Array/List 表示，或按既有 Temporary 规则排除相关字段。JsonSerializer 的普通/保留类型信息模式都不自动展开 Set/Queue。
- 此项是确定的标准行为，不是等待将 Set/Queue 自动加入 SB 的占位方案；D3/D4 的 SB 集合种类和格式映射不因此扩展。

