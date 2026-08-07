# Latte 运行时模型

本文档描述 Latte 的运行时表示与语义，回答"怎么跑"。语言表层语法见 `SYNTAX.md`。

核心取舍：**运行时类型信息完全具化（reified）+ 单份共享 Native 代码体 + 统一胖值槽**。Latte 不擦除泛型实参的实际类型；所有独特能力（`is`/`supers`/`with`、`Type\<T>`/`new`、wrapper 派发）都建立在“typeid 始终伴随值与泛型调用”这一机制上。ValueType 进入统一泛型/动态槽位时使用系统特权 Box 表示，而不是退化成普通堆对象。

---

## 1. 设计总览与取舍

**这套机制换来的能力：**

- 泛型在语义和运行时类型信息上完全 reified；机器码采用共享代码体并隐式传递 typeid，`is`/`supers`/`with`/`new a(...)` 从“typeid 恒在传”里直接获得。
- 小值类型（≤ 8 字节）内联进 128-bit 胖引用，躲掉 Java 式的逐元素装箱，且仍带完整 typeid、仍可多态。
- Box 是统一泛型值槽的系统级效率后门：它在类型系统中进入 `Object`/`Any` 多态世界，在 Native 表示中仍维持 ValueType 的复制语义、固定 16 字节外槽和 unique 裸数据块；其自身存活不需要 GC 可达性判定。只有 `rich` ValueType 才能在 Box/内嵌布局中携带托管引用字段，GC 借同一套 `TypeSheet`/`refMap` 机制扫描这些字段（见 §4、§8、§22）。
- `shared` 是类型声明属性：shared class 构成可跨 Coroutine 引用的共享对象域，shared rich struct 与 shared wrapper 则以值语义进入共享图；静态字段闭包禁止 shared 图反向指向 local object。`String` 是非 rich ValueType，天然可跨越所有共享边界（见 §4）。
- 生命周期管理分为 microGC、microSGC 与 macroGC：前两者分别以非同步/同步 ARC 处理绝大多数即时释放，macroGC 仅对 ARC 遗留的候选闭包做低门槛、小步快跑的循环检测。
- 运行时不支持 finalizer。外部资源由 `core.IDisposable`/`using` 确定性释放；GC 只在对象销毁时检查遗漏并上报全局异常，绝不代替用户执行 `dispose()`（见 §25）。
- wrapper 逻辑经 Middleware 烘焙进方法体、骑静态 vtable、`call???` 固定 slot 兜底，实现 swizzling/forwarding 表达力而无动态派发框架（见 §14）。
- 加载期扁平化 + 二分接口查找，AOT 友好、无 JIT 依赖。
- 原生协程从 `main` 开始贯穿整个程序；Executor 是可观察的调度域，Worker 保持透明。
- `enum struct` 以隐藏判别字段统一固定 case 与参数化 case；所有 enum 值都必须通过具名 case 入口产生，init 从不作为普通 constructor 暴露。

**这套机制签下的代价（实现前须知）：**

- 引用全场 16 字节，需 16 字节对齐，cache 密度减半，指针密集结构受影响。
- 普通泛型容器使用统一胖值槽：`Array\<i32>` 固定为 16 字节/元素，而不是按 `i32` 的原生 4 字节连续布局。缓冲区场景由 `Span\<T extends ValueType>` 单独兜底（见 §5），不为此给 `Array` 开特例。不提供泛型热路径的单态化特化 pass——typeid 间接是共享泛型代码的固定成本，需要原生连续布局时改用 `Span\<T>` 或非泛型代码。
- 通过接口访问字段恒为虚调用（接口字段降级为 getter/setter）。
- 单个 `obj.foo()` 的派发链可能有多层（specific proxy、各类别唯一 wildcard、跨 wrapper 嵌套、`call???` 兜底）；编译器需实现派发链诊断工具（见 §15）。
- run-to-suspension 不提供一般性的隐藏抢占点；长期计算任务需要显式 `yield`。唯一的内部例外之一是 macroGC acquire/release 慢路径可生成不可见的 `yield core.GCAlarm(...)`，用于在 GC ownership fence 上挂起当前 Coroutine（见 §23）。

---

## 2. 胖引用（Fat Reference，128-bit）

每个引用为 128 bit（16 字节），布局：

| 偏移 | 宽度 | 内容 |
|------|------|------|
| 0 | 64 bit | `typeid`：指向 `TypeSheet` 的地址，最高字节复用为分类 tag |
| 8 | 64 bit | payload：内联值 **或** 堆地址 |

**typeid 首字节（分类 tag）：**

| 首字节 | 含义 | payload |
|--------|------|---------|
| `0` | ValueType 且尺寸 ≤ 8 字节 | 内联值（紧接 typeid 之后） |
| `1` | ValueType 且尺寸 > 8 字节 | 堆地址 |
| `2` | Object | 堆地址 |

取值时机器码先看首字节：`0` 直接取内联内容，`1`/`2` 解引用。几个位运算即可分流，无需先读 `typeSize`（尺寸类别已编码进 tag）。取真实 `TypeSheet` 地址时需掩掉 tag 字节。

**视图 typeid 与对象头 typeid（仅 Object）：**

- 胖引用中的 typeid 表示"当前代码认为它是什么类型"（静态视图）。
- 对象头中的 typeid 表示"这个对象实际上是什么类型"（决定方法与内存布局如何解释）。
- 例：用 `Object` 类型的字段装一个值，引用里的 typeid 指向 `Object`，对象头里的 typeid 指向实际类型。`cast` 借此低成本实现（见 §12）。

---

## 3. 128-bit 引用写入的原子性与并发

采用**方案 A**：引用的读写使用 16 字节原子指令，避免撕裂导致的类型混淆。

- **依赖**：对齐的 16 字节原子 load/store。现代 x86（AVX）与 ARMv8.4+（LSE2）架构性保证；老 ARM64 / 非 AVX 目标回退到 `cmpxchg16b` 或 `ldxp/stxp` 循环。
- **对齐**：所有引用类型的槽（字段、局部、数组元素）强制 16 字节对齐。
- **LLVM 注意**：`atomic i128` 需正确的 target-feature + 对齐才会降为向量指令，否则可能降为 `cmpxchg16b` 或 `__atomic_*` libcall。
- **Box 槽例外**：持有 Box 的那个外层槽（字段/变量）传递时整体复制而非共享（见 §4），这次批量复制不必当作一次原子操作；但 rich Box 数据块内部的托管引用字段仍是普通 128-bit 引用，原地写入时依旧遵守本节的 16 字节原子写规则。

### 3.1 `rich` / `shared` 的运行时域

`rich` 与 `shared` 是 TypeSheet 可观察的类型属性，而不是引用槽上的限定符。运行时按以下域解释对象和值：

- **非 rich ValueType**：不含托管引用，`refMap` 恒为空；复制、构造和栈上计算完全不进入 GC 引用图。`String` 属于本域（字符数据是特权裸缓冲区，不是托管引用，见 §4）。
- **rich ValueType**：可以含托管引用，仍遵守值语义和 Box 的 unique ownership；复制/销毁时按 `refMap` 对内部引用执行 acquire/release。全部 wrapper 属于本域（wrapper 恒为 rich struct，见 §14）。
- **shared rich ValueType**：rich ValueType 的共享安全子集；内部只能指向 shared Object，并只能内嵌非 rich 或 shared rich ValueType。`shared wrapper` 属于本域。
- **local Object**：未标记 `shared` 的 class 实例，归创建它的 Coroutine 所有，引用计数由 microGC 以非同步 ARC 管理。
- **shared Object**：标记 `shared` 的 class 实例，可由多个 Coroutine 持有，引用计数由 microSGC 以同步 ARC 管理。

静态闭包保证 shared Object 和 shared rich ValueType 只能指向 shared Object，并只能内嵌非 rich/shared rich ValueType；local Object 可以指向 local/shared Object，并持有任意 ValueType。由此 shared 图不可能反向到达 local 对象域。shared 只决定共享资格与 GC 路径，不自动为用户字段提供线程安全。

编译器保证进入全局/静态存储与 async 边界的值必然属于「shared Object ∪ shared rich ValueType ∪ 非 rich ValueType」这三个域（`SYNTAX.md` §3.1.1 的共享安全类型）。运行时可以依赖这一不变量，不为跨边界的 local 值提供任何动态检查或搬运机制。

---

## 4. Box：统一泛型值槽的系统级效率后门

当一个 ValueType 处于静态具体、非泛型的布局位置时，例如 `class Foo { var x: i32 }`，它直接内嵌进 `Foo` 的对象布局，与 C/C++ 的内嵌 struct 字段一样，不经过 Box。

当 ValueType 需要进入统一的泛型、`Object`、`Any`、动态参数或其他固定 ABI 槽位时，运行时使用 `Box\<T extends ValueType>` 的**系统特权表示**。这不是类型擦除后的补救：实际 ValueType 的 typeid 始终保留；Box 的目的，是让任意 ValueType 以规整的 128-bit 外槽进入统一多态体系，同时避免把它实现成带对象头、对象身份和独立 GC 节点的普通堆对象。

在 Latte 类型系统中，`Box\<T>` 位于 `Object` 分支，可以参加统一的泛型与动态派发；在 Native 物理表示中，它仍遵守 ValueType 的复制语义。这是运行时明确开放的效率后门，而不是普通用户类型可以复制的布局规则。

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

`String` 是非 rich ValueType，因此它的 `refMap` 恒为空，不参加 GC 引用图，也不需要任何 shared 标注即可跨越 Coroutine 边界。它的字符数据是一段由编译器与运行时管理的**特权裸缓冲区**——与 `Span\<T>` 同属 §1 所说的内建后门，不是托管引用，不是普通 Object 字段：

- 短字符串可以完全内联进胖值 payload；超出内联预算时 payload 指向 unique 裸缓冲区，与 tag `1` 的大 ValueType 走同一条物化路径。
- **可观察语义是按值深拷贝**：每次复制、传参、跨协程传递都产生一份独立的字符数据。
- 实现**可以**引入用户完全不可观察的 copy-on-write、驻留（interning）或不可变共享缓冲区来消除实际拷贝，包括为共享缓冲区维护 native 侧引用计数——这类计数不进入 GC 引用图，不影响「非 rich ValueType 的 `refMap` 恒为空」这一不变量。
- 但**源码语义、编译器分析与用户代码一律不得假设这些优化存在**，正如 BIL 不得假设任何特定 GC 模型或 GC 行为。任何能让用户观察到缓冲区共享的行为都是实现缺陷。

**固定成本：**

- 大于 8 字节的 ValueType 跨统一泛型/动态值槽时需要物化 unique 裸数据块并复制值；编译器可在不改变值语义时消除中间副本。
- 值槽读写需按 tag 分流：tag `0` 直接读取 payload，tag `1` 访问裸数据块，tag `2` 访问普通 Object。

---

## 5. `Span\<T>`：无装箱的连续缓冲区

`Span\<TElement extends ValueType>` 是内建的 ValueType（struct），为需要连续、非装箱原生存储的场景（缓冲区、数组等）提供 `Array` 的替代——它不走 §4 的泛型装箱路径，即便 `TElement` 尺寸 > 8 字节，元素也**不装箱**，一视同仁地连续内联存储。

- **表示**：`Span\<T>` 自身是一个小 struct（基址 + 长度，概念上类似胖指针）；作为具体字段类型出现时（非再次泛型化）直接落布局，不触发装箱（同 §4 的非泛型字段规则）。
- **元素访问**：`Span\<T>` 持有的单一 typeid（`T`）在运行期从 `TypeSheet.typeSize` 得到元素步长（stride），索引即 `基址 + i * stride` 的直接指针运算，不分配、不解引用装箱对象。
- **定位**：这是"一视同仁地对所有 ValueType 开特例的 Array"，而不是给 `Array\<T>` 本身开特例——`Array\<T>` 保持普通泛型语义（装箱，见 §4）。缓冲区/数值密集场景应使用 `Span\<T>`。
- **来源（"官方后门"）**：`Span\<T>` 的基址指向**独立分配的原生 buffer**（如 `Span.alloc(n)`），不经过 `Array\<T>`/`List\<T>` 装箱再借出。它是绕开普通泛型 16 字节胖值槽与 Box 间接表示、直接取得连续原生内存的官方手段，端到端按 `T` 的原生布局存储。
- **GC 可见性**：若 `T` 为非 rich ValueType，`Span\<T>` 对 GC 完全透明（纯字节 buffer，无需扫描）。若 `T` 为 rich ValueType，`Span\<T>` 就不再是 GC 透明的——运行时需按 `TypeSheet.typeSize` 给出的 stride 逐元素遍历，并用 `T` 的 `refMap` 对内部引用执行 acquire/release 或 macroGC 扫描。共享存储中的元素必须为非 rich 或 shared rich ValueType。

---

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

`refMap` 是 `TypeSheet` 里供 GC 定位引用字段的"跳棋式"稀疏编码，紧跟在 `iMap` 之后（见 §6）。它同时服务于普通 `Object` 与 rich tag `1` Box 裸数据块；非 rich ValueType 的 `refMapSize` 恒为 0。两类存储使用同一套字段定位例程，但 acquire/release 选择 microGC 还是 microSGC 由对象域和 `typeFlags` 决定。

**编码规则**：`refMapSize`（32 bit）给出 `refMap` 的条目数；`refMap` 是紧凑排列的 16-bit 无符号整数序列，每个条目表示"从当前扫描位置起，跳过多少个 128-bit（16 字节）槽位后到达下一个引用"。扫描位置的初始值为对象头结束处（Box 裸数据块则为数据块起始处）；每读完一个引用（128 bit）后，扫描位置前移 16 字节，作为下一条目的起点。

例如 `refMap = [0, 2, 1]`：

1. 从起始位置跳 0 个槽位 → 第一个引用就在起始位置，读取 128 bit。
2. 位置 +16 字节（跳过刚读的引用），再跳 2 个槽位（+32 字节）→ 第二个引用。
3. 位置 +16 字节，再跳 1 个槽位（+16 字节）→ 第三个引用。
4. `refMap` 条目耗尽，扫描结束。

**与 Box 的关系**：Box 本身的存活由持有者的生命周期确定性决定，GC 不需要为 Box 自身做可达性判定；但 rich Box 的裸数据块内部可能含托管引用字段（仅 tag `1` 可能），运行时仍要通过胖引用中的 typeid 找到 `TypeSheet.refMap`，对这些字段执行 ARC 与 macroGC 图扫描；非 rich Box 无需扫描。

**与 `Span\<T>` 的关系**：`Span\<T>` 若 `T` 为 rich ValueType，运行时按 `TypeSheet.typeSize` 给出的 stride 逐元素遍历，每个元素用 `T` 的 `refMap` 处理内部引用（见 §5）。

**加载期扁平化**：当一个类型的字段直接内嵌了 rich ValueType（非装箱，直接落布局，见 §4）时，其 `refMap` 条目在加载期按字段偏移量整体折算、拼入外层类型的 `refMap`（同 §9 vtable/iMap 拍平的思路：体积换速度，运行时一次线性扫描到底，不需要在扫描时递归下钻进内嵌类型的 TypeSheet）。

---

## 9. 加载期扁平化

加载类型时：

- 将继承的方法与接口默认方法**拷贝一份进本类 vtable**（保持相同 offset），使 vtable 查找一次命中，不在调用时上溯。
- iMap 排序以支持二分。
- 内嵌 rich ValueType 字段的 `refMap` 条目按偏移量折算后拼入外层 `refMap`（见 §8）；非 rich ValueType 无条目。

这是"体积换速度 + 编译器实现简单"的取舍。

---

## 10. 泛型的运行时实现

Latte 泛型不擦除实际类型。实现采用**单份共享 Native 代码体 + 隐式 typeid 侧信道 + 统一胖值 ABI**；不为每组类型实参重复生成机器码，但泛型体在运行时始终能取得真实类型。

编译器向泛型函数/类型隐式传入类型信息：

| 泛型形态 | 传入的类型信息 |
|----------|----------------|
| 单个类型参数 | `typeid` |
| 具名可变参数 | `Map\<String, typeid>` |
| 位置可变参数 | `Array\<typeid>` |

编译器传参形态（S9 定稿，2026-08-05，与 `BIL_STANDARD.md` §7 一致）：泛型函数的 `.args` 以 `.generic.T = .typeid` 隐藏参数承载固定泛型参数、`.generic.TArgs` 承载可变泛型包（`.array<.typeid>` / `.map<.string, .typeid>`），按 §7.2 规范序排列；调用点静态类型实参以 `getid.type` 物化 typeid（BIL §12.5），嵌套泛型调用把接收到的 `.generic.T` 隐藏参数原样转发。以 `.` 开头的隐藏参数名由编译器保留，普通源码参数不得声明同名标识符。

override 中的 `super(...)` 将当前固定泛型隐藏参数按声明序转发给 `fn(..super)`，并以 `$.this` 为首参。Middleware 将其解析为直接基类原始实现；frontend 不生成 `..create`，该符号只表示 Middleware/VM 的 create 生命周期阶段。

- ValueType 进入统一泛型值槽时使用 §4 的 Box 特权表示：小值内联，大值由 unique 裸数据块承载；无论哪种情况，实际 typeid 都保留。
- Object 使用普通胖引用表示；泛型代码通过 typeid 与对象头实际 typeid 完成视图和动态类型操作。
- 泛型字段或跨 Coroutine 边界必须对实际类型执行共享闭包检查：shared 容器只接受 shared Object、shared rich ValueType 或非 rich ValueType。
- `is`/`supers`/`with`/`new`/`T()` 均基于隐式 typeid 在运行时完成；其中 enum struct 不允许走普通构造入口，只能使用具名 case。
- **具名可变参数 = 方案 A**：所有值进入统一 `Any` 胖值槽，另配一份 `Map\<String, typeid>` 描述；实现内如遍历 map 一样遍历。

不提供泛型热路径的二次单态化特化 pass。共享代码固定承担 typeid 间接和统一槽位成本；需要零间接、连续同构存储时使用 `Span\<T>`（§5），需要静态直接布局时使用具体非泛型 ValueType 字段。

---

## 11. `Type\<T>` / typeOf / new

- **`Type\<T>`**：typeid 的封装，基本类型（`struct`）。
- **`typeOf(x)`**：返回 `Type\<实际类型>`。
- **`new a(...)`**：显式发起普通构造。`a` 可以是静态类型符号，也可以是 `Type\<T>` 值；静态 `TypeName(...)` 是 `new TypeName(...)` 的简写。泛型体内 `T()` 与动态 `new` 共用同一套 typeid 构造机制。
  - 静态具体目标的 init 重载解析在编译期完成，运行期仅定位具体入口。
  - `Type\<T>` 值或其他非静态具体目标的 init 重载解析在运行期用 `TypeSheet` 的 init 表完成。
  - `enum struct` 不进入普通构造路径：即使其 init 为 `pub`，`EnumType(...)`、`new EnumType(...)`、`new enumTypeValue(...)` 与解析到 enum 的泛型 `T()` 都必须失败；enum 只能调用编译器生成的具名 case 入口（§16）。
  - 目标为抽象类型、enum struct 或找不到匹配 init 时抛 `core.NoSuchMethodException`。
- `Type\<T>` 的值可作类型出现在 `is`/`supers` 右侧。

---

## 12. is / supers / with

| 运算符 | 语义 | 实现 |
|--------|------|------|
| `is` | 对象类型是否为目标或其子类（协变） | 沿实际类型的 `baseTypeId` 链比对目标 `TypeSheet`；接口经 iMap 查 |
| `supers` | 参数类型是否为目标类型的基类（逆变） | 反向比对；用于通配代理的逆变匹配转发（见 §14） |
| `with` | 类型是否被指定 wrapper 修饰 | 查该类型的 wrapper 元数据 |

三者均以运行时 typeid / `TypeSheet` 为基础。

---

## 13. cast

- 视图 typeid（胖引用）与对象头 typeid（实际类型）分离。
- `cast` 检查对象头的实际 typeid 与目标是否兼容：兼容则改写胖引用的视图 typeid（无数据移动）；失败抛 `core.CastException`。

---

## 14. Wrapper 派发管线

**静态组合**：实体修饰器在语言语义上把 wrapper 逻辑按声明序从内到外嵌套进方法派发（替换 `inner`），因此天然骑 vtable。运行时**不能**增删、重排或禁用 wrapper。烘焙动作（逐应用特化、inner 链接、原始体替换，以及 `call???` router 体合成）由 Middleware 在合法 lowering 时完成（边界见 `BIL_STANDARD.md` §23）；frontend（编译器）产物只携带三类标记，不合成派发链符号、不替换原始方法体：

- (a) 声明上的 wrapper 应用标记（BIL 修饰符）；
- (b) proxy 模板 fn——wrapper 类型的成员 fn，带 `wrapper-proxy(specific|wildcard)` 修饰符（`BIL_STANDARD.md` §8.4），体内的 `inner` / `self` 以占位指令表达（`invoke fn(..inner)` 见 `BIL_STANDARD.md` §15.4，`get.self` 见 `BIL_STANDARD.md` §12）。`fn(..inner)` 调用操作数显式携带待转发的可变泛型包（`.generic.<Pack>` 前置）与值包（`.kwargs.*` / `.vargs.*` 随后）；Middleware 烘焙下一环时消费这些包操作数（解包/shim/特化链接），frontend 不展开；
- (c) 未声明方法的降级调用点 = 对 `core::Any$call???` 的普通 `invoke`（见 §14.2）。

最终内联仍归 Middleware。

**wrapper 值的表示**：wrapper 恒为 rich struct（`SYNTAX.md` §14.9），因此它是一个带 typeid 的胖值，而不是独立的堆对象——没有对象头、没有对象身份、不作为独立 GC 节点被追踪；其内部托管引用字段照常经 `refMap` 参加 acquire/release。wrapper 实例存放在宿主的 Middleware 合成隐藏存储中（命名约定见 `BIL_STANDARD.md` §5.3；存储布局是 Middleware 的实现职责，BIL 文本不再声明隐藏字段），因此：

- 宿主类型必须允许内嵌 rich struct；非 rich struct 不能被修饰，这是编译期不变量，运行时无需检查。
- 非 shared wrapper 可能持有 local object，所以只能出现在非 shared 宿主与栈帧中；shared wrapper 走 microSGC 路径。
- 路径表达式 `value:WrapperType` 与 proxy 体内的 `this` 都是对该隐藏存储的**原地访问**，从不复制。源码层 `value:WrapperType` 是只读 place（`SYNTAX.md` §14.5）：既不能被整体赋值，也不能被整体取出，因此运行时不存在脱离宿主独立存活的 wrapper 值，也不为 wrapper 提供任何别名或共享机制。wrapper place 写侧链指令操作数是已有两态 `field(F)|wrapper(W)`（而非编译器合成的隐藏字段符号；字段-Value 应用 = 相邻 `field(HOST_FIELD)+wrapper(W)`，Entity 应用 = `wrapper(W)`）。frontend lowering 在成员**读取**/调用/索引读路径上经 `get.wrapper` / `get.wrapper.field` 取得**值拷贝**，再发普通 `get.field`/`invoke`/`get.array`（与原地写路径分离；BIL §12.4）；wrapper 字段原地写走 `set.wrapper.field`；深层写穿由 frontend 展开为多次现有 get/set（最外层必要写回复用 `set.wrapper.field`，普通值中间反向写回仍发 `set.field`），不新增专用深写指令、也不把整条深路径压进单条超长链。隐藏存储不可用普通字段寻址（M88）。

### 14.1 四类唯一 wildcard proxy

Entity Wrapper 可以分别实现以下四种 universal wildcard；每一类别在同一个 wrapper 中只能出现零个或一个：

```latte
operator .proxy.*<named TNamedArgs..., TUnnamedArgs..., TReturn>(
    symbol: String,
    namedArgs: named TNamedArgs...,
    unnamedArgs: TUnnamedArgs...
): TReturn

operator .proxy.get.*<TValue>(
    symbol: String,
    value: TValue
): TValue

operator .proxy.set.*<TValue>(
    symbol: String,
    value: TValue
)

operator .proxy.opr.*<named TNamedArgs..., TUnnamedArgs..., TReturn>(
    symbol: String,
    namedArgs: named TNamedArgs...,
    unnamedArgs: TUnnamedArgs...
): TReturn
```

这些 wildcard 不是可重复声明并按泛型 pattern 竞争的 overload，而是四个操作类别各自唯一的 fallback handler。其参数和泛型形状由编译器固定；同一 wrapper 内重复实现同类别 wildcard 是编译错误。

派发顺序为：

- 跨 wrapper：按声明序 outer → inner 嵌套；
- 同 wrapper 内：匹配 specific proxy 时使用 specific，否则使用对应类别的唯一 wildcard；
- specific 与同层 wildcard 是择一关系；调用 `inner(...)` 后，下一层独立重复该选择；
- 不存在 wildcard 重叠、pattern specificity 或 `@ProxyPriority`。

### 14.2 单一 `call???` slot

`call???` 定义在 `Any`（万物基类）上，因此是每个对象 vtable 中一个**固定 offset 的 slot**，不涉及动态向 vtable 增加条目；继承链经 vtable 正常解析：

```latte
call???<TResult, named TNamedArgs..., TUnnamedArgs...>(
    symbol: String,
    namedArgs: named TNamedArgs...,
    unnamedArgs: TUnnamedArgs...
): TResult
```

- `call???` 是 bootstrap 内建方法（与 `Any.toString` 同先例）：bootstrap 声明 + VM 内建 hook 实现（`BIL_STANDARD.md` §22.5）。默认实现（请求未被任何 wrapper 路由时）由该 hook 提供，直接抛 `core.NoSuchMethodException`，含可按配置启用的 log 代码——**不是**编译器生成的 body。
- 方法、getter、setter、operator 在 lowering 后本质上都是方法请求。运行时只保留这一个 slot；对已有声明成员的命中烘焙，以及 `call???` 按 canonical `symbol` 判定类别并转入 `.proxy.*`、`.proxy.get.*`、`.proxy.set.*` 或 `.proxy.opr.*` 的类别路由体，均由 Middleware 合成，不另外设置 `get???`、`set???`、`opr???`。类别路由作为 Middleware 插入点的架构预留（`SYNTAX.md` §14.7 末条语义不变，执行主体为 Middleware）。
- 跨模块编译调用方时，若被调用成员已有普通实现则走正常 vtable slot；需要 fallback 时交给 `call???`，而 `call???` 自身仍经 vtable 解决继承。
- 给已有方法增加 specific proxy 后，只需重编译被修饰模块并由 Middleware 重新烘焙，使原 vtable slot 指向新的 wrapped body；调用方无需因 wrapper 变化而重编译。

**未声明普通方法的降级规则**（对应 `SYNTAX.md` §14.7）：静态类型无匹配声明方法且 wrapper 链中存在 `.proxy.*` 时，frontend 发射对 `core::Any$call???` 的普通 `invoke`（携带 canonical symbol）；实参按统一胖值 ABI 传递，返回值在调用点按期望类型转换，不符抛 `core.CastException`。frontend **不**合成任何 router / 降级链符号。

落地形态注记（M86）：上文的 `call???` 泛型签名是**逻辑签名**——`call???` 的规范签名实质化为非泛型胖值签名 `(symbol: String, namedArgs: Array\<Pair\<String, Any\>\>, unnamedArgs: Array\<Any\>): Any`（`BIL_STANDARD.md` §15.4）。泛型 typeid 包不单独传递：每个 `Any` 胖值自描述 typeid（§2），wildcard proxy 体可在包元素上直接做 `is`/`as` 检查；`TResult` 的角色由调用点的 cast 物化承担（`BIL_STANDARD.md` §12.1，不符抛 `core.CastException`）。frontend 降级调用点发射 `invoke core::Any$call???`；被 wrapper 命中的宿主上的类别路由体由 Middleware 按 vtable 语义合成（链末落到 `Any.call???` 的 VM hook 默认实现）。

### 14.3 canonical symbol ABI

`symbol` 是编译器生成并传递的完整调用身份：

```text
类型：
命名空间::类名[.子类名...]

方法：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static.]方法名([参数名:参数类型,...])@返回值类型

字段 / 全局变量 / 全局常量：
命名空间::[可能有的类名[.可能有的子类名...]]#[.static.]名称@字段类型

运算符：
命名空间::类名[.可能有的子类名...]$$运算符名称([参数名:参数类型,...])@返回值类型

getter / setter：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static].get.名称@字段类型
命名空间::[可能有的类名[.可能有的子类名...]]$[.static].set.名称@字段类型
```

`.static.` 仅用于静态方法、静态字段及其访问器；Singleton 实例成员不因类型是 singleton 而自动成为 static symbol。

泛型与可变参数被规范化为保留名称的隐藏参数：

- 类型声明的 `out`/`in` 方向保留在 BIL `.type generic(...)` 元数据中；它只影响
  构造类型的赋值兼容，不改变 hidden typeid 参数的顺序或 ABI；
- 单个泛型 `T` → `.generic.T: Type`；
- 匿名可变泛型 `TArgs...` → `.generic.TArgs: Array\<Type>`；
- 具名可变泛型 `named TArgs...` → `.generic.TArgs: Array\<Pair\<String, Type>>`；
- 匿名值可变参数 `args...` → `.vargs.args: Array\<Any>`；
- 具名值可变参数 `named args...` → `.kwargs.args: Array\<Pair\<String, Any>>`。

这些 hidden arguments 与 canonical symbol 一起保留实际泛型 typeid、值参数包及其名称，不需要为动态 forwarding 再建立第二套类型擦除协议。以 `.` 开头的 hidden 参数名由编译器保留。

未声明方法的降级请求没有声明侧泛型参数名可展开为 hidden argument；调用点的显式泛型实参按书写序编码在方法名后的 `<...>` 段，使用 canonical 类型引用（例如 `Service$fetch<.i32,.string>(.i32)@.any`）。该段属于 `symbol` 字符串，不新增 `call???` 的 hidden 参数或 BIL invoke 操作数。

---

## 15. 派发链诊断工具

编译器需提供诊断能力：给定一个调用点，打印其解析出的完整 wrapper 派发链——即 **Middleware 将要烘焙的链**，包括跨 wrapper 的 outer→inner 顺序、每层命中的 specific 或对应类别唯一 wildcard、proxy 模板声明的 canonical symbol，以及是否具备降级到 `call???` 的资格。这是随实现一并提供的编译器功能，而非事后补充的调试手段——§1 提到的“派发链可能有多层”这一复杂度，靠这个工具而非靠用户记忆来管理。

工具形态：CLI 子命令 `compile --file <src> --explain-dispatch`。数据源为编译器报告的**应用登记 × proxy 声明的形状匹配结果**（每层将命中 specific|wildcard 与 proxy 声明 canonical symbol）与**降级资格**（wrapper 链是否含 `.proxy.*`）。编译器不再合成烘焙符号，报告中的特化身份改为 proxy 模板声明的 canonical symbol；按调用点（源位置）过滤为预留扩展。

---

## 16. enum struct 的运行时表示

每个 `enum struct` 都是普通 ValueType 的封闭特例，并额外含有一个编译器生成、用户不可直接访问和修改的隐藏判别字段（discriminant）。它不能标记为 `open`，不能继承用户声明的 struct，也不能被其他 class/struct/enum 继承；固定继承链为 `具体 enum → Enum → ValueType`。判别字段随 enum 值一起内嵌、复制或进入 Box，并计入 `TypeSheet.typeSize`；所有 case 的实际运行时类型仍然是该 enum struct 本身。

Enum 没有对源码开放的普通构造入口。init 只作为编译器生成 case 入口的实现部件存在；无论 init 是否为 `pub`，都不能通过 `EnumType(...)`、`new EnumType(...)`、`new enumTypeValue(...)` 或泛型 `T()` 直接创建 enum 值。

### 16.1 判别字段宽度

判别字段只使用两种宽度：

- `u16`：全部具名 case 的判别值都能表示时使用；
- `u32`：存在无法由 `u16` 表示的 case 判别值，或自动编号 case 数量超过 `u16` 容量时自动扩展。

没有“自由构造值”及其保留判别值；`u16` 的全部 `0...65535` 均可用于具名 case。因此自动编号的 `u16` enum 最多容纳 65,536 个具名 case；再增加 case 时扩展为 `u32`。

判别宽度是类型布局和 ABI 的一部分。由 `u16` 扩展为 `u32` 可能改变 enum 自身及内嵌它的外层 ValueType/Object 的尺寸、对齐、字段偏移和 `Span\<T>` stride，因此属于 ABI-breaking change。

### 16.2 case 构造入口

`[]` 中的每个具名 case 编译为一个设置固定判别值的构造入口：

1. 创建未完成初始化的 enum 值；
2. 在用户 init 可观察 `this` 前写入该 case 的隐藏判别值；
3. 按 case 模板中的固定参数与调用时参数洞组成实参，调用编译期已经解析的 init；
4. 返回完整 enum 值。

没有参数洞的固定 case 可以物化为模块级只读值并按 ValueType 规则复制；含参数洞的 case 物化为静态 case factory。两者都属于 case 入口，而不是 init 的直接调用。

参数化 case 只有在其目标 init 为 `pub` 时才能生成对调用方可见的参数洞；绑定到 `priv`/`protected` init 的 case 必须是固定模板。enum 不参与用户继承，因此 `protected` 不扩展出派生 enum 的 case 模板；所有 case 模板都只声明在本 enum 的 `[]` 中。`pub` 授予的是“通过具名 case 传入参数”的资格，不授予直接构造 enum 的资格。

省略类型名的 `.Case` 在编译期必须拥有已确定 enum 类型的 receiver/期望类型；这不改变运行时布局，只决定编译器选择哪一个 case 入口。

### 16.3 `is .Case` 的实现

对 enum case 的模式检查：

```latte
value is .Failed
```

编译为隐藏判别字段与 `Failed` 编译期判别常量的整数比较。它不是子类型检查，不访问 `TypeSheet.baseTypeId`，也不比较任何用户字段。`typeOf(value)` 始终返回该 enum struct 的类型。

即使编译器能够看见当前 enum 声明中的全部具名 case，enum `switch` 作为表达式时仍必须保留 `default`。运行时不把“安全源码只能通过 case 入口构造”提升为“任何来源的位模式都必然属于当前 case 集合”；FFI、unsafe/raw memory、反序列化与跨版本 ABI 等 corner case 可以把未知 discriminant 带入程序，`default` 为这些状态提供定义行为。

### 16.4 自动与显式判别值

未使用 `->` 时，编译器按声明顺序从 `0` 开始分配判别值；这些数值属于编译产物，不承诺跨源码重排或重新 codegen 稳定。

使用 `Case -> integer` 时，该整数成为 case 的稳定判别 ABI：

- 所有显式值必须唯一、非负、为编译期常量；
- 同一 enum 内必须全显式或全隐式；
- 编译器依据最大显式值选择 `u16` 或 `u32`；
- case 重排不会改变显式判别值；
- 后续加入超出原宽度的显式值会触发宽度扩展，仍然是 ABI-breaking change。

`->` 只稳定判别值；payload 字段布局由普通 struct 布局规则决定。

---

## 17. 原生协程、Executor 与 Worker

Latte 从 `main` 开始就在协程中执行。协程（Coroutine）是语言的逻辑执行单元；Executor 是协程永久绑定的调度域；Worker 是 Executor 内部实际运行用户代码的操作系统线程。

### 17.1 核心不变量

- 每个 Coroutine 在创建时确定一个 Executor，并在整个生命周期内保持不变。
- Coroutine 只能选择 Executor，不能选择具体 Worker。
- Worker 对程序透明；程序不得依赖 Worker 身份或挂起前后的线程一致性。
- 任意时刻，同一个 Coroutine 最多只能由一个 Worker 执行。
- Coroutine 挂起后不再占用 Worker；恢复时重新进入所属 Executor 的逻辑待执行协程池，并可由该 Executor 的任意 Worker 取走。
- 同一 Executor 的全部 Worker 使用相同调度策略，并从同一个逻辑 Runnable Set 获取工作。

“共享待执行协程池”只是一项语义约束。实现可以使用单队列、分片队列、per-worker 本地队列、局部缓存或 work stealing；这些差异不得被 Latte 程序观察，也不得改变上述不变量。

### 17.2 run-to-suspension

Coroutine 采用 run-to-suspension。一个 Worker 开始执行某个 Coroutine 后，持续执行到以下边界之一：

- `await` 一个尚未终止的 Task；
- 裸 `yield`；
- `yield PollingAlarm`；
- `yield EventAlarm`；
- 正常返回；
- 未处理异常；
- 进入取消终态。

编译器和运行时不在普通语句、循环回边或函数调用之间暗中插入协程轮换点。操作系统仍可抢占 Worker 线程，但 OS 抢占不会使该 Worker 在同一个 Latte 执行段中改为执行另一个 Coroutine。

普通函数和普通 lambda 在当前 Coroutine 内执行，并可以使当前 Coroutine `await` 或 `yield`；`async` 的意义是“调用时另建 Coroutine”，而不是“允许函数体挂起”。

### 17.3 状态机

Coroutine 的公开语义状态为：

```text
Created
  ↓
Runnable
  ↓
Running
  ├──→ Suspended ──→ Runnable
  ├──→ Completed
  ├──→ Failed
  └──→ Cancelled
```

- `Created`：运行时实体已建立但尚未发布。
- `Runnable`：已进入所属 Executor 的逻辑可运行集合。
- `Running`：当前由某个 Worker 执行。
- `Suspended`：正在等待 Task、Alarm 或其他可挂起同步源，不占用 Worker。
- `Completed`：正常结束并保存结果。
- `Failed`：以未处理异常结束并保存异常。
- `Cancelled`：进入取消终态。

三个终止状态均不可再次恢复。运行时必须以原子状态转换保证一个 Coroutine 不会被重复发布或同时运行。

---

## 18. `async` 调用与 Task

### 18.1 eager spawn

每次调用 async 函数或 async lambda 时，调用方执行：

1. 求值 receiver、泛型参数和全部实参；
2. 创建 Coroutine 及其 Task 句柄；
3. 确定并永久绑定目标 Executor；未显式指定时继承调用方 Coroutine 的 Executor；
4. 将 Coroutine 从 `Created` 转换为 `Runnable` 并发布到目标 Executor；
5. 向调用方返回 Task。

async 函数体不作为调用方当前执行段的一部分运行。实参求值期间的异常直接发生在调用方；进入 async 函数体之后的未处理异常记录进 Task。

在多 Worker Executor 上，新 Coroutine 可能在调用表达式返回 Task 前就被另一个 Worker 取走执行。Task 是热任务句柄，不是惰性计算。

### 18.2 Task 类型

- 无结果异步调用返回 `core.coroutine.Task`；
- 有结果异步调用返回 `core.coroutine.Task\<TResult>`。

Task 是运行时内建的 shared Object，因为句柄、终态和 waiter 列表都可能同时被多个 Coroutine 访问。async receiver、实参、capture 与 `TResult` 必须满足 shared 闭包：shared Object 可共享，非 rich/shared rich ValueType 按值复制，local Object 与非 shared rich ValueType 不得跨边界。

一个 Task 对应一次异步调用的终态，并可由多个等待者、多次 `await`；这不会重新执行 async 函数。Task 保存以下三类终态：

- 成功：保存 `TResult` 或无结果完成标记；
- 失败：保存未处理异常；
- 取消：保存取消状态。

丢弃 Task 句柄只表示调用方不再同步或观察它，不会取消对应 Coroutine。Executor/运行时活跃协程表必须持有该 Coroutine 直到终态，因此直接调用 async 函数并忽略返回值即可实现 fork/fire-and-forget。

### 18.3 `await`

求值 `await taskExpression` 后：

- Task 已成功：立即取得结果；
- Task 已失败：在 await 点重新抛出保存的异常；
- Task 已取消：在 await 点传播取消；
- Task 尚未终止：把当前 Coroutine 登记为 waiter，转入 `Suspended`，并把 Worker 归还给 Executor。

Task 进入终态后，每个 waiter 都重新发布到 **waiter 自己永久绑定的 Executor**，而不是 Task 所属 Coroutine 的 Executor。若 Task 在 await 时已经终止，await 不要求实际挂起。

Task 终态发布与 await 成功恢复之间建立同步关系：被等待 Coroutine 在终止前完成的写入，在 await 返回后对等待者可见。

---

## 19. `yield` 与 Alarm

### 19.1 裸 `yield`

裸 `yield` 执行：

```text
Running → Runnable
```

当前执行段结束，Coroutine 重新进入所属 Executor 的逻辑 Runnable Set，Worker 返回调度器。`yield` 只保证重新经过一次调度决策，不保证一定换到另一个 Coroutine，不保证 FIFO，也不保证另一个任务至少执行一次。

### 19.2 `PollingAlarm`

`core.coroutine.PollingAlarm` 定义同步探测方法：

```latte
pub func isReady(): bool
```

执行 `yield alarm` 且静态/运行时类型为 PollingAlarm 时：

1. 当前 Coroutine 保存 continuation，进入 `Suspended(PollingAlarm)`；
2. Executor 将它登记到逻辑 polling 等待集合；
3. 每当调度器再次给该等待任务一次调度机会时，由某个 Worker 调用一次 `alarm.isReady()`；
4. 返回 `false`：不恢复用户 continuation，继续处于 PollingAlarm 等待；
5. 返回 `true`：调度器原子地取得该 Coroutine 的执行权，使其转为 `Running`，并在本次调度机会中直接从 `yield` 后继续执行；
6. 抛出异常：该异常被视为发生在 yield 点，使 Coroutine 进入失败传播流程。

`isReady()` 必须同步、线程安全、可重复调用，不得执行 `await`/`yield`，也不得进行长期阻塞。连续探测可以由不同 Worker 执行。轮询频率不是语言保证；实现可使用退避、批量扫描或专用 polling 队列避免空闲时忙等。

### 19.3 `EventAlarm`

`core.coroutine.EventAlarm` 表示由外部事件 callback 唤醒的一次性、粘滞事件：事件发生后该实例保持已触发状态，避免事件在 waiter 注册前发生而丢失。

执行 `yield eventAlarm` 时：

1. 当前 Coroutine 与 EventAlarm 原子完成 waiter 注册；
2. 未触发时进入 `Suspended(EventAlarm)`；
3. 已触发时仍结束当前执行段，但立即具备重新发布条件；
4. 事件源触发时，callback 原子地标记 Alarm，并把 waiter 重新发布到各自所属 Executor；
5. callback 不直接恢复 continuation，也不执行用户 Latte 代码。

注册和触发之间必须进行原子握手，保证并发发生时不丢失唤醒；同一个 waiter 最多只能被发布一次。EventAlarm 的重复触发是幂等的。

### 19.4 `sleep`

标准库函数：

```latte
core.coroutine.sleep(milliseconds: i32): core.coroutine.EventAlarm
```

返回运行时内部的 EventAlarm 子类。它把 deadline 注册到系统时钟树；时钟到期时 signal 该 Alarm，并由 Alarm 将等待 Coroutine 发布回原 Executor。

`sleep` 的等待不占用 Worker，也不调用阻塞当前 Worker 的系统 sleep。计时基于单调时钟；到达 deadline 只表示 Coroutine 重新可运行，实际继续执行时间仍取决于 Executor 调度。

任何带 Alarm 的 `yield` 都会结束当前 run-to-suspension 执行段，即使 Alarm 在执行 yield 时已经就绪或已触发。

---

## 20. 内置 Executor 与 CoroutineLocal

### 20.1 Executor 层级

公共基类：

```text
core.coroutine.Executor
```

内置 Executor：

```text
core.coroutine.MainExecutor
core.coroutine.ComputeExecutor
core.coroutine.IOExecutor
```

所有 Executor 都遵守 §17 的统一不变量。每个 Executor 拥有一个或多个 Worker；同一 Executor 的所有 Worker 共享相同调度策略与同一逻辑 Runnable Set。具体 Worker 数量、队列结构、work stealing 和扩缩容策略属于实现细节，除标准库另有明示外不得成为程序语义。

`main` 根 Coroutine 默认绑定 MainExecutor。新 Coroutine 未显式选择 Executor 时继承创建方的 Executor。跨 Executor 执行不会迁移当前 Coroutine，而是在目标 Executor 上创建新的 Coroutine，并通过 Task/await 同步。

### 20.2 `CoroutineLocal\<TValue>`

```text
core.coroutine.CoroutineLocal\<TValue>
```

CoroutineLocal 的绑定存储在 Coroutine 的上下文中，跟随 Coroutine 跨 Worker 迁移。其读取结果不得依赖当前 Worker 或 OS Thread，因此不能使用普通 ThreadLocal 来实现公共语义。

Coroutine 挂起、重新发布以及换 Worker 恢复时，必须看到同一份 CoroutineLocal 上下文。Worker 私有缓存若存在，只能作为不可观察的实现优化。

CoroutineLocal 是「每协程一个实例」的**唯一**机制。`singleton` class 的实例存储属于全局存储，因此按 `SYNTAX.md` §3.1.1 必须标记 `shared`，恒为进程内唯一的 shared object；singleton 不承担 per-coroutine 语义。

---

## 21. 协程、GC 与同步边界

运行中的 Coroutine、挂起 continuation、Task 终态、Alarm waiter 注册以及 CoroutineLocal 上下文都可能持有托管引用，因此属于 ARC root slot，或由可达运行时对象间接保持。Coroutine 挂起不得使其局部变量、异常、返回槽或等待对象被错误释放。

本节协程语义不规定实现采用 stackful 栈、分段栈或 stackless continuation；无论采用何种表示，都必须提供精确的活跃引用映射，并与 `TypeSheet`/`refMap` 对 Object 和 rich Box 内容的扫描协同工作。

§3 的 128-bit 原子读写只保证胖引用不撕裂；对象生命周期由 §22 的 ARC 和 §23 的 macroGC fence 保证。以下运行时边界至少建立 happens-before：

- async Coroutine 发布前的调用参数/捕获初始化 → 新 Coroutine 开始执行；
- Task 对应 Coroutine 的终止前写入 → waiter 的 await 成功恢复；
- EventAlarm 的 signal 前写入 → 因该 signal 恢复的 Coroutine；
- Executor 将 Coroutine 从 Suspended 原子转换并发布为 Runnable → 该 Coroutine 后续 Running；
- macroGC 完成清理并把 `gcFlag` 发布为 `IDLE` → 因本轮 `GCAlarm` 恢复后重新进入 acquire/release 的 Coroutine。

shared 只允许对象跨 Coroutine 可达，并不使共享可变字段自动同步；用户数据竞争仍需标准库原子、锁、Channel 等同步原语处理。

---

## 22. 三级 GC：microGC、microSGC 与 macroGC

Latte 的“GC”由两层确定性 ARC 和一层候选式循环回收组成。正常路径优先由 ARC 即时解决；macroGC 只处理 ARC 无法独立释放的循环候选。

### 22.1 microGC

microGC 是 Coroutine 本地的非同步 ARC：

- 管理 local Object 的强引用计数；
- 管理普通 rich ValueType/Box 内部指向 local 或 shared Object 的引用 acquire/release，其中指向 shared Object 的计数操作转入 microSGC；
- 同一 Coroutine 任意时刻最多由一个 Worker 执行，因此 local Object 的引用计数不需要原子更新；Coroutine 换 Worker 不改变其对象域。

当 local Object 的引用计数降为 0 时，microGC 先执行 §25 的 `IDisposable` 合约检查，再释放对象并沿 `refMap` release 它持有的引用。绝大多数短命局部小对象在这一层直接消失，不进入 macroGC 候选集。

### 22.2 microSGC

microSGC（S = synchronized）是 shared 对象域的同步 ARC：

- 管理 shared Object 的原子强引用计数；
- 管理 shared rich ValueType/Box 内部的 shared Object 引用；
- 支持多个 Coroutine 并发 acquire/release 同一个 shared Object。

计数降为 0 时，运行时先执行 §25 的 `IDisposable` 合约检查，再确定性释放对象。microSGC 只同步生命周期元数据，不为对象的普通用户字段提供互斥或原子语义。

### 22.3 macroGC

macroGC 是基于图染色的循环引用检测器。它不扫描整个堆，只处理“被 ARC release 路径波及、但未被 ARC 清理”的候选对象及其相关闭包。典型候选是 release 后引用计数仍大于 0、因而可能被循环内部引用维持的对象。

运行时按对象体积累计候选债务；同一对象在一轮候选账本中只计一次。候选累计体积达到 macroGC 门槛后触发一次回收。门槛被**有意设置得偏低**，使运行模式倾向于频繁、短小的“小步快跑”而非长时间积攒后进行大规模回收。

这一策略与前两层 ARC 配合：

- 能由 microGC/microSGC 直接归零的对象立即释放，尤其是大量本地小对象；
- macroGC 的候选数量和闭包通常保持较小；
- 每次图染色只触及候选相关对象，暂停窗口短，回收延迟低；
- 回收完成后从候选债务中扣除已处理对象，并继续以低门槛积累下一小批。

macroGC 可分别处理 local/shared 候选，但只要进入一次 macroGC pass，就通过 §23 的 ownership fence 冻结所有托管引用 acquire/release，从而得到稳定图。它冻结的是引用所有权变化，不是全部程序执行。

---

## 23. macroGC 轻量级 ownership fence

### 23.1 flag 布局与状态

运行时维护一组按 cache-line 尺寸独占并显式对齐的原子 flag，避免 false sharing：

- 每个 Coroutine 一个 `cFlag`：`IDLE(0)`、`ENTERING(1)`、`PROCESSING(2)`；
- GC 一个 `gcFlag`：`IDLE(0)`、`STARTING(1)`、`PROCESSING(2)`。

`cFlag.PROCESSING` 表示该 Coroutine 正处于一个引用 acquire/release 区域；`ENTERING` 表示它试图进入但因 macroGC 已开始而在 `GCAlarm` 上等待。

`cFlag` 的协议身份必须绑定 Coroutine，而不是 Worker：Coroutine 是 Latte 最小且稳定的串行执行/所有权主体，同一 Coroutine 任意时刻最多由一个 Worker 执行；Worker 只是 Executor 内透明且可替换的执行载体。Coroutine 在 `GCAlarm` 上挂起后释放原 Worker，恢复时可以由同一 Executor 的任意 Worker 继续，因此 `ENTERING → Suspended → 恢复 → retry` 的状态必须随 Coroutine 保存。实现不得让 macroGC correctness 依赖挂起前后的 Worker 绑定、Worker 生命周期或具体调度队列结构。Worker-local 缓存可以作为不可观察优化，但不能取代 Coroutine-owned `cFlag`。

### 23.2 GC 侧协议

macroGC 开始时：

1. 原子地将 `gcFlag` 从 `IDLE` 更新为 `STARTING`；只有成功完成该状态转换的 collector 可以启动本轮 macroGC。
2. 在该原子操作后建立 fence，禁止后续对 `cFlag` 的巡视和清理指令被重排到发布 `STARTING` 之前。
3. 自旋巡视全部已注册 `cFlag`，直到每个 flag 都不等于 `PROCESSING`。引用修改区是编译器生成的短代码，因此这一排空阶段预期很短；`ENTERING` 和 `IDLE` 都不阻止 GC 继续。
4. 原子地把 `gcFlag` 置为 `PROCESSING`，开始候选闭包的图染色与清理。此时托管引用图和 ARC 计数对普通 Coroutine 保持稳定。
5. 对确认不可达的对象执行 §25 的 `IDisposable` 合约检查并完成清理；在发布结束前建立 fence，禁止清理和元数据写入被重排到结束状态之后；随后原子地把 `gcFlag` 置为 `IDLE`。
6. 触发本轮内部 `core.GCAlarm`，唤醒所有处于 `ENTERING` 的 Coroutine。

### 23.3 Coroutine 侧 acquire/release 协议

编译器在所有托管引用 acquire/release 操作外生成进入协议。概念流程如下：

```text
retry:
    fence
    if gcFlag != IDLE:
        cFlag = ENTERING
        yield core.GCAlarm(currentGeneration)
        goto retry

    cFlag = PROCESSING
    fence              // 保证 PROCESSING 在第二次 gcFlag 检查前对 GC 可见

    if gcFlag != IDLE:
        cFlag = ENTERING
        yield core.GCAlarm(currentGeneration)
        goto retry

    perform acquire/release region

    fence              // 保证区域内引用图和 RC 写入先完成
    cFlag = IDLE
```

双重检查关闭了 Coroutine 与 GC 同时进入的竞态：如果 Coroutine 先进入 `PROCESSING`，GC 必须等待它退出；如果 GC 先发布 `STARTING`，Coroutine 会在第二次检查时退到 `ENTERING` 并挂起。

一个复合引用操作可合并为同一 acquire/release region，例如 rich ValueType 整体复制可在一次进入中 acquire 新内部引用、替换存储并 release 旧内部引用。递归 release 继续复用当前 region；实现可用 Coroutine 本地嵌套计数，只有最外层进入/退出实际修改 `cFlag`。

### 23.4 `core.GCAlarm`

源码中看不到 GC 等待语句。`yield` 在此处特指编译器生成的隐藏语句：

```latte
yield core.GCAlarm(...)
```

`GCAlarm` 是运行时内部的 `core.coroutine.EventAlarm`。等待者进入 `Suspended`，不会以裸 `yield` 反复进入 Runnable 集合。它使用 EventAlarm 的原子 waiter 注册/触发握手，保证 GC 在注册前后完成都不会丢失唤醒；恢复后 Coroutine 必须重新执行完整双重检查。实现可以用 GC generation 区分连续的 macroGC pass。

### 23.5 fence 覆盖的操作

“修改引用图”被定义为所有托管引用的 acquire 和 release，包括：

- 在栈、Coroutine frame、对象字段、容器槽或 rich ValueType 字段中建立一个强引用；
- 覆盖、清空、移出或销毁上述引用槽；
- rich ValueType/Box 复制时 acquire 内部引用，覆盖或销毁时 release 内部引用；
- ARC 归零后的级联字段 release；
- 修改与循环候选账本、引用计数和图边一致性直接相关的运行时元数据。

引用槽写入、对应 RC 更新和候选元数据更新必须作为同一个 acquire/release region 对 macroGC 保持一致；16 字节原子写只能防止胖引用撕裂，不能替代本节 fence。

### 23.6 特别放行：分配与纯值执行

macroGC 不阻止单纯的内存分配或不涉及托管引用的执行：

- Object、Box、Span buffer 的原始内存分配可以在 `STARTING`/`PROCESSING` 期间继续；
- 对象头和纯数值字段初始化可以继续；
- 非 rich ValueType 的创建、复制、传参、返回、栈上运算完全不经过 GC fence；
- 尚未向任何托管引用槽发布的新对象可以继续完成纯值初始化。

只有当代码尝试给新对象添加托管引用、把新对象放入栈/Coroutine frame/字段等正式引用槽，或操作 rich ValueType 的内部引用时，才会撞上 acquire/release wall 并在必要时等待 `GCAlarm`。因此 macroGC 的“停顿”严格局限于 ownership mutation；大量数值代码、原生 buffer 工作和未装箱的非 rich ValueType 计算可以无感继续运行。

---

## 24. macroGC 的性能取向与可观察语义

macroGC 的低触发门槛、候选闭包扫描和 ownership-only fence 共同形成以下性能模式：

1. ARC 先清除绝大多数无环对象，特别是 Coroutine 本地的小对象；
2. 少量未清除对象很快达到按字节计的低门槛；
3. macroGC 频繁启动，但每次只处理很小的候选闭包；
4. `STARTING` 只等待已有的短 acquire/release region 排空；
5. `PROCESSING` 期间只有新的 acquire/release 被 EventAlarm 挂起，纯值代码和分配继续；
6. GC 完成后集中唤醒等待者，它们重新检查后继续原操作。

语言不承诺具体阈值数值、图染色颜色编码或候选容器结构；这些属于运行时调优参数。但实现必须保持“按候选体积触发、默认门槛偏低、只处理候选相关闭包、非 ownership 工作继续执行”这四项语义与性能取向。


---

## 25. 确定性资源管理：`IDisposable`、`using` 与全局泄漏异常

Latte 明确不支持 finalizer，也不允许运行时在对象回收阶段调用任意用户终结逻辑。对象内存由 microGC/microSGC/macroGC 管理；文件、句柄、流、锁封装等外部资源则由 `core.IDisposable` 确定性管理。

概念接口为：

```latte
pub interface IDisposable {
    func dispose()
}
```

### 25.1 `using` 的 lowering

`seq using(...) ... named ... {}` 中的每个资源绑定由编译器 lowering 为与该 `seq` 词法退出绑定的清理记录：

- 初始化按源码顺序进行；
- 只有成功完成初始化的绑定才进入清理栈；
- 退出 `seq` 时按逆序调用 `dispose()`；
- 正常返回、`return@`、裸 `return`、异常展开及其他离开作用域的控制流共享同一清理路径；
- `await`/`yield` 只保存并挂起当前 Coroutine 状态，不触发清理，资源记录随 Coroutine frame 保留；
- `dispose()` 本身是普通函数，可以 `await` 或 `yield`。清理中的挂起保存当前 disposal 调用、逆序清理进度和尚未处理的资源；恢复后继续同一清理路径；
- 外层函数的 return、异常继续传播或 Coroutine 的终态发布，必须等待已建立的 `using` 清理全部完成，不能通过丢弃 frame 绕过可挂起 disposal。

`using` 提供的是编译器保证的确定性调用，不依赖引用计数何时归零，也不依赖 macroGC 是否运行。可挂起只延长清理路径，不降低“每个 `IDisposable` 必须由用户代码负责 dispose”的强制要求。

### 25.2 销毁时的强制检查

`TypeSheet.typeFlags` 的 `DISPOSABLE` 位标识类型是否实现 `core.IDisposable`；对应 Object 的生命周期元数据中带有运行时可检查的 disposal 状态。无论对象最终由 microGC、microSGC 还是 macroGC 销毁，运行时都必须在释放其内存前检查该状态：

- 已由用户代码直接或通过 `using` 调用 `dispose()`：正常继续销毁；
- 从未调用 `dispose()`：立即产生全局的 undisposed-resource 异常事件。

若 `dispose()` 正在挂起，对象及清理记录仍由对应 Coroutine frame 保持，尚未进入对象销毁检查；运行时不能把“正在执行可挂起 disposal”误当成未负责的遗失资源。真正到达 microGC、microSGC 或 macroGC 销毁点而 disposal 状态仍表明从未调用 `dispose()` 的对象必须爆炸上报。

这一后门是错误检测机制，不是隐式清理机制：

- GC **绝不**替对象调用 `dispose()`；
- 不建立 finalization queue；
- 不延迟对象释放来等待用户终结逻辑；
- 不允许对象复活；
- 上报后仍按正常内存生命周期完成销毁。

该异常不绑定到“恰好触发最后一次 release 或 macroGC”的普通用户调用栈，因此普通 `try/catch` 不能接住。它只能通过 `core.GlobalExceptionHandler` 提供的全局处理方法接收。运行时应至少携带对象实际类型；实现还可以附加创建位置、最后释放位置等诊断信息。

这一分工保持三类生命周期彼此独立：

```text
托管内存：microGC / microSGC / macroGC
外部资源：dispose() / using
遗漏检测：destruction-time global exception
```

---

## 26. native 互操作与 `latte_rt`

`native` 函数（`SYNTAX.md` §4.6）把 Latte 调用路由到运行时原生方法面。原生方法面由一个 C 编写的 shim 库提供，库标识为 `latte_rt`：它把 libc 风格的 C 函数包装为 Latte 调用约定下的可调用入口，并负责 Latte 值（如 `String` 的 native 表示）与 C 类型之间的转换。

- **调用约定**：暂定 fastcall；精确的寄存器/栈分配、胖值槽传递与 `String` 布局规则在 Middleware 阶段定稿，本节不预先约束。
- **第一版原生方法面**只有三个定参函数，不提供可变参数：
  - `print(text: String)`：把字符串写入标准输出；
  - `printErr(text: String)`：把字符串写入标准错误；
  - `toString(value: Any): String`：`SYNTAX.md` §3.8 的 `toString` 内建承载——内建基本类型（数值/`bool`/`char`）返回标准文本（`String` 的 `toString` 即自身，不经此路由）；未覆写 `toString` 的对象返回其类型 canonical 名。`Any` 上声明 `toString(): String`（无体，接口承诺），`Object` 提供 open 默认实现并把 body 路由到本函数；用户类型 `override` 后经普通虚派发执行自身实现，不再命中原生面。
- **BIL VM 不链接原生库**：VM 对 `(lib, symbol)` 命中 `BIL_STANDARD.md` §22.5 内建 hook 表的 native 调用直接执行内建行为，因此在没有 Middleware 与 `latte_rt` 实现的环境下也能完整执行程序。
- 标准库在 Latte 层封装原生方法面（如 `core.io::Console.println` 调用 `print`），用户代码不直接依赖 `latte_rt`；格式化、插值等逻辑全部在 Latte 层演进，不进入原生方法面。
