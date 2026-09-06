# TypeSheet / vtable / iMap / 加载期扁平化（§6–§9）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 6. TypeSheet

`typeid` 指向的 Native 结构。字段从前到后：

| 字段 | 宽度 | 说明 |
|------|------|------|
| `typeInfoId` | 64 bit | 指向 `TypeInfo` 对象（更完整的类型信息） |
| `baseTypeId` | 64 bit | 指向基类的 `TypeSheet`；仅 `Any` 的此字段为 `NULL` |
| `typeSize` | 32 bit | 算上对象头后的对象尺寸（字节）。单对象上限 4 GB |
| `typeFlags` | 32 bit | 类型属性位，至少含 `RICH`、`SHARED`、`DISPOSABLE`；用于泛型、Box、共享闭包、GC 路径与资源合约判定 |
| `vTableSize` | 32 bit | vtable 元素数量 |
| `vTable` | 变长 | 紧凑排列的 64-bit 方法入口指针 |
| `iMapSize` | 32 bit | iMap 键值对数量 |
| `iMap` | 变长 | 紧凑排列的键值对（见 §8） |
| `refMapSize` | 32 bit | refMap 条目数量 |
| `refMap` | 变长 | 紧凑排列的 16-bit 间隔值，供 GC 定位引用字段（见 §8） |

`TypeSheet` 是 Native 元数据结构，自身按 64 bit 对齐即可（不同于 §2 中胖引用需要的 16 字节对齐）。它描述可由运行时 typeid 指向的实际类型实体，但不等于全部源码类型节点；例如 `Box\<T>` 只有编译器语义类型投影，没有独立 Box TypeSheet，其槽中的 typeid 直接指向底层 `T`。

---

具化 `Nullable<T>` 具有独立 TypeSheet 身份。TypeInfo 的 Native 字段序为 `name`、`sheet`、`wrappers`、`wrapperCount`、`ifaceClosure`、`ifaceClosureCount`、`nullableElement`；最后一项为 Nullable 的元素 TypeSheet 指针，其余类型为 NULL。Nullable 仍使用原胖值表示，此元数据不引入新的值对象。泛型 typeid 不得将闭合 Nullable 擦为裸 Nullable：动态 cast 接受 null，非 null 按元素类型检查；`is` 对 null 仍为 false，对非 null 按元素类型判断。

## 7. vtable 模型

**布局顺序**（从前到后）：

1. 从基类继承的方法入口指针；
2. 本类实现的方法入口指针；
3. 各 interface 实现的方法入口指针。

**不变量**：继承中相同方法保持相同 vtable offset。因此对继承来的方法，调用方直接用相同 offset 即可访问到正确（被覆盖的）版本。

**接口派发**：到 iMap 查得该接口的 vtable base offset，加上接口约定的 offset，得实际 vtable offset。

**虚派发的类型来源**：虚派发使用**实际类型**的 vtable。vtable offset 由静态视图在编译期给出；实际 vtable 指针经对象（头）的实际 typeid 取得。

---

## 8. iMap、refMap 与 GC 追踪

### iMap 与接口派发

每个类的 iMap 键值对，每对：

| 偏移 | 宽度 | 内容 |
|------|------|------|
| 0 | 64 bit | 该 interface 的 `TypeSheet` 地址 |
| 8 | 32 bit | 该 interface 方法段在本类 vtable 中的 base offset |

- iMap 在**加载期按接口 `TypeSheet` 地址排序**，查找用二分（O(log n)）。
- 查找策略：先在本类 iMap 二分；未命中则沿 `baseTypeId` 链向上到各基类 iMap 继续查。
- **接口字段**：降级为编译器生成的 getter/setter 方法，进 vtable/方法段，走方法派发。因此 iMap **不含字段 offset**，菱形字段冲突天然不存在。代价：通过接口访问字段恒为一次虚调用。

### refMap 与 GC 追踪

`refMap` 是 `TypeSheet` 里供 GC / ARC 定位托管槽的"跳棋式"稀疏编码，紧跟在 `iMap` 之后（见 §6）。它同时服务于普通 `Object`、值类型（含非 rich）与 tag `1` Box 裸数据块。两类存储使用同一套字段定位例程，但 acquire/release 选择 microGC 还是 microSGC 由对象域和 `typeFlags` 决定。

**编码规则**：每个 `u16` 条目 = **高 2 位 kind（0 = 胖引用槽，1 = String 槽）| 低 14 位 16B 槽跳数**。`refMapSize`（32 bit）给出条目数。扫描位置的初始值为对象头结束处（值类型 / Box 裸数据块则为数据块起始处）；每读完一个槽（128 bit）后，扫描位置前移 16 字节，作为下一条目的起点。kind 决定该槽解释：kind0 读 `{u64 typeid, u64 payload}` 胖引用；kind1 读槽首 8 字节为 `char *data` 的 String。

例如 `refMap = [0, 2, 1]`（三条目皆 kind0）：

1. 从起始位置跳 0 个槽位 → 第一个胖引用就在起始位置，读取 128 bit。
2. 位置 +16 字节（跳过刚读的槽），再跳 2 个槽位（+32 字节）→ 第二个引用。
3. 位置 +16 字节，再跳 1 个槽位（+16 字节）→ 第三个引用。
4. `refMap` 条目耗尽，扫描结束。

**String 字段入 refMap（kind1）**：持有 String 槽的类型（class / struct / enum）把该槽编为 kind1 条目。值类型**恒计算** refMap（不再被 rich 门挡掉）——非 rich struct 只要含 String 或内嵌带托管槽的值类型，同样产出条目。`core::String` 内建 sheet 自描述一条 `[kind1 | hop0]`，供按值扫描 String 槽自身。

**数组无静态 refMap**：`core::Array` 前缀 32B = 对象头 `[0..16)` + `elemSheet*` `@16` + `i32 length` `@24` + pad `@28`，元素从 32 起按元素 ABI 步长排列。析构按头内 `elemSheet × length × stride` 走查，不把变长元素编进静态 refMap。

**与 Box 的关系**：Box 本身的存活由持有者的生命周期确定性决定，GC 不需要为 Box 自身做可达性判定；但 Box 的裸数据块内部可能含托管引用 / String 槽（仅 tag `1` 可能），运行时仍要通过胖引用中的 typeid 找到 `TypeSheet.refMap`，对这些字段执行 ARC 与 macroGC 图扫描。

**与 `Span\<T>` / `SharedSpan\<T>` 的关系**：布局与数组同构（32B 前缀 + 元素内联），无静态 refMap；析构与 GC 走查复用数组机制——按头内 `elemSheet × length × stride` 逐元素处理内部引用（见 §5）。

**加载期扁平化**：当一个类型的字段直接内嵌了带 refMap 的值类型（非装箱，直接落布局，见 §4）时，其 `refMap` 条目在加载期按字段偏移量整体折算、拼入外层类型的 `refMap`（同 §9 vtable/iMap 拍平的思路：体积换速度，运行时一次线性扫描到底，不需要在扫描时递归下钻进内嵌类型的 TypeSheet）。

---

## 9. 加载期扁平化

加载类型时：

- 将继承的方法与接口默认方法**拷贝一份进本类 vtable**（保持相同 offset），使 vtable 查找一次命中，不在调用时上溯。
- iMap 排序以支持二分。
- 内嵌值类型字段的 `refMap` 条目按偏移量折算后拼入外层 `refMap`（见 §8；含非 rich 的 String 槽）。

这是"体积换速度 + 编译器实现简单"的取舍。

---
