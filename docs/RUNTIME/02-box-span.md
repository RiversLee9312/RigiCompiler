# Box 与 Span（§4–§5）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 4. Box：统一泛型值槽的系统级效率后门

当一个 ValueType 处于静态具体、非泛型的布局位置时，例如 `class Foo { var x: i32 }`，它直接内嵌进 `Foo` 的对象布局，与 C/C++ 的内嵌 struct 字段一样，不经过 Box。

当 ValueType 需要进入统一的泛型、`Object`、`Any`、动态参数或其他固定 ABI 槽位时，运行时使用 `Box\<T extends ValueType>` 的**系统特权表示**。这不是类型擦除后的补救：实际 ValueType 的 typeid 始终保留；Box 的目的，是让任意 ValueType 以规整的 128-bit 外槽进入统一多态体系，同时避免把它实现成带对象头、对象身份和独立 GC 节点的普通堆对象。

在 Rigi 类型系统中，`Box\<T>` 位于 `Object` 分支，可以参加统一的泛型与动态派发；在 Native 物理表示中，它仍遵守 ValueType 的复制语义。这是运行时明确开放的效率后门，而不是普通用户类型可以复制的布局规则。

`Box\<T>` 是编译器 intrinsic／语义投影，不是普通 nominal class：

- 不创建 Box 对象头、Box identity、Box vtable 或独立的闭合 `Box\<T>` TypeSheet；
- `Box\<T> <: Object` 是编译器内建的类型关系，不要求运行时通过 `baseTypeId` 链证明；
- Box 槽中的 typeid 永远指向底层实际 ValueType `T` 的 TypeSheet，而不是一个不存在的 `Box\<T>` TypeSheet；
- 对 Box 进行 Object/Any 约束检查、`is`/`cast`、方法或 wrapper 派发时，由编译器和运行时 helper 使用专用 lowering，将语法层的 Object 行为投影到底层 `T` 与统一胖值槽；
- 源语言类型节点与 Native `TypeSheet` 通常一一对应，但这不是语言不变量。Box、Span 及其他规范明确列出的内建后门可以打破这一对应；用户声明类型不能复制这种特权。

**Box 的特殊行为：**

- 外层槽恒为 16 字节：`typeid + payload`。因此普通泛型容器的 ValueType 元素布局与 `T` 的实际尺寸无关，访问代码可以共享。
- typeid 始终表示被承载 ValueType 的实际类型，不发生泛型类型信息擦除。
- Box 没有普通 Object identity；它不像普通引用那样由多个字段/变量共享同一值实例，传递时遵守值语义并强制复制（实现可做不改变可观察语义的 copy elision）。
- 对大值使用的裸数据块由外层 Box 槽 unique 持有，生命周期紧跟该槽；GC 不需要把 Box 外壳作为独立可达节点追踪。
- 裸数据块的布局信息完全由外层胖值中的 typeid 还原，因此块内无需对象头，也不重复保存 typeid。

**按尺寸分两种物化：**

- **尺寸 ≤ 8 字节（tag `0`）**：值直接内联进胖值 payload，不产生堆分配，仍带完整 typeid、仍可参加泛型和多态。一个托管引用本身占 128 bit，超过内联预算，因此这一档 ValueType 不可能含托管引用字段。
- **尺寸 > 8 字节（tag `1`）**：payload 指向一段只包含值字节的 unique 裸数据块；没有对象头、没有块内 typeid。脱离持有它的胖值，这段内存不可自解释。

这使普通泛型值槽拥有固定而规整的 Native 布局：

```text
small ValueType: [actual typeid | inline value]
large ValueType: [actual typeid | unique raw-block pointer]
Object:          [view typeid   | object pointer]
```

`Array\<T>`、普通泛型参数槽和动态参数槽可以统一按 16 字节步长处理；需要按 `T` 原生尺寸连续排列的同构数据则使用 §5 的 `Span\<T>`。

**rich ValueType 内的托管引用（仅 tag `1` 可能出现）：**

- 只有声明为 `rich` 的 ValueType 才允许直接或间接持有托管引用；非 rich ValueType 的 `refMap` 恒为空。
- 普通 rich ValueType 可以指向 local/shared Object；shared rich ValueType 只能指向 shared Object，并且不能内嵌非 shared rich ValueType。
- “Box 本身 GC 不 trace”指不需要为 Box 外壳做可达性判定；裸数据块内部的引用字段仍参加 acquire/release 与 macroGC 图遍历。运行时通过胖值 typeid 找到 `TypeSheet.refMap`，逐字段定位内部引用。
- 复制 rich Box 时创建独立值副本并对内部引用执行相应 acquire；覆盖或销毁时对旧内部引用执行 release。shared rich Box 的这些操作走 microSGC。

**其他规则：**

- `Nullable\<T>` 已是 `Object` 子类，不会被再次 Box（语法上也禁止 `T??`）。
- `Nullable\<T>` 的共享域由 `T` 推导而非由声明给出（`SYNTAX.md` §3.1.2）：`T` 共享安全时 `Nullable\<T>` 按 shared Object 处理，否则按 local Object 处理。

**`String` 的表示：**

`String` 是非 rich ValueType，因此它的 `refMap` 恒为空，不参加 GC 引用图，也不需要任何 shared 标注即可跨越 Coroutine 边界。它的字符数据是一段由编译器与运行时管理的**特权裸缓冲区**——同属 §1 所说的内建后门，不是托管引用，不是普通 Object 字段：

- 短字符串可以完全内联进胖值 payload；超出内联预算时 payload 指向 unique 裸缓冲区，与 tag `1` 的大 ValueType 走同一条物化路径。
- **可观察语义是按值深拷贝**：每次复制、传参、跨协程传递都产生一份独立的字符数据。
- 实现**可以**引入用户完全不可观察的 copy-on-write、驻留（interning）或不可变共享缓冲区来消除实际拷贝，包括为共享缓冲区维护 native 侧引用计数——这类计数不进入 GC 引用图，不影响「非 rich ValueType 的 `refMap` 恒为空」这一不变量。
- 实现注记（MW7a）：当前 native 采用不可变 + ARC 计数缓冲（块头 `{atomic u32 rc, u32 reserved}`，data = 块+8；字面量 `rc=0xFFFFFFFF` 永生）。此即本条允许的 native 侧计数优化，可观察语义仍为按值深拷贝。
- 但**源码语义、编译器分析与用户代码一律不得假设这些优化存在**，正如 BIL 不得假设任何特定 GC 模型或 GC 行为。任何能让用户观察到缓冲区共享的行为都是实现缺陷。

**固定成本：**

- 大于 8 字节的 ValueType 跨统一泛型/动态值槽时需要物化 unique 裸数据块并复制值；编译器可在不改变值语义时消除中间副本。
- 值槽读写需按 tag 分流：tag `0` 直接读取 payload，tag `1` 访问裸数据块，tag `2` 访问普通 Object。

---

## 5. `Span\<T>` 与 `SharedSpan\<T>`：无装箱的连续缓冲区对象

`Span\<TElement extends ValueType>` 是**内建 class（Object，引用语义）**，为需要连续、非装箱原生存储的场景（缓冲区、数值密集计算等）提供 `Array` 的替代——它不走 §4 的泛型装箱路径，即便 `TElement` 尺寸 > 8 字节，元素也**不装箱**，一视同仁地连续内联存储。复制与传参共享同一 buffer 对象，经别名写入互相可见（这是特性，与 `String` / struct 的值语义刻意区分）；生命周期由普通 ARC 管理（tag `2` 胖引用）。

- **表示**：对象布局与数组完全同构——对象头（16B）+ elemSheet 指针（@16）+ length i32（@24）+ 元素内联连续存储（@32 起）。元素**不装箱**、按 `T` 原生布局排列；步长 = `T` 的 `TypeSheet.typeSize`（编译期已知，生成代码使用常量 stride）。
- **元素访问**：索引即 `基址 + i × stride` 的直接指针运算，不分配、不解引用装箱对象。读越界返回 null（`.nullable<T>` 形态，与数组同约定）；写越界抛可捕获 `core.OutOfBoundException`（MW9b 起，与数组同；此前为 abort）。
- **定位**：这是"一视同仁地对所有 ValueType 开特例的连续缓冲区"，而不是给 `Array\<T>` 本身开特例——`Array\<T>` 保持普通泛型语义（装箱，见 §4）。缓冲区/数值密集场景应使用 `Span\<T>`。
- **来源（官方后门）**：`spanOf\<T>(n)`（stdlib 公共面，native `span_alloc` 实现）直接取得连续原生内存，不经过 `Array\<T>` / `List\<T>`。`T extends ValueType` 由泛型约束在编译期强制（`Span\<class>` 为编译错误）。
- **GC 可见性**：`T` 非 rich 时，对象无引用图边（元素不含托管引用）。`T` 为 rich 时，析构按 `elemSheet × length × stride` 逐元素走查内部引用——与数组析构同一机制（`RIGI_TYPE_ARRAY` 标志；Span 的 TypeSheet 由 C# 侧发射时带上该位）。
- **`SharedSpan\<T>`**：`Span\<T>` 的 shared class 变体，对象布局相同，`typeFlags` 含 `SHARED`（原子 rc，走 microSGC）。元素约束收紧为「非 rich 或 shared rich ValueType」（编译期检查）。Mutex 等同步原语与更完整的并发支持由未来版本接入；本期仅提供类型与原子生命周期。

---
