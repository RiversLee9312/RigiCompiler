# 设计总览与胖引用（§1–§3）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

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
- Object 的 ARC 按对象头实际类型选择 local/shared 路径；两路计数均原子化，覆盖失败 Task 的异常图由多个 waiter 同时持有的运行时边界，视图转换不改变生命周期协议。
- 例：用 `Object` 类型的字段装一个值，引用里的 typeid 指向 `Object`，对象头里的 typeid 指向实际类型。`cast` 借此低成本实现（见 §12）。

---

## 3. 128-bit 引用读写的并发语义（非原子）

引用的读写**非原子**：运行时不为胖引用槽提供撕裂保护。

- **数据竞争即未定义行为**：多线程并发写、或写与读并发同一引用槽属于用户数据竞争，行为未定义（允许撕裂，包括 typeid 与 payload 不一致的视图）。`shared` 只决定共享资格与 GC 路径（§3.1），不为用户字段提供线程安全（§21）；需要并发访问的共享数据由标准库原子、锁、Channel 等同步原语保护。
- **对齐**：所有引用类型的槽（字段、局部、数组元素）仍强制 16 字节对齐——这是布局与 cache 行为要求，不再是原子性前提。
- **实现自由**：实现可以用任何满足对齐的方式读写引用槽（含向量指令），但不得依赖其原子性。
- **Box 槽**：持有 Box 的那个外层槽（字段/变量）传递时整体复制而非共享（见 §4）；rich Box 数据块内部的托管引用字段是普通 128-bit 引用，与其他引用槽适用同一非原子规则。

### 3.1 `rich` / `shared` 的运行时域

`rich` 与 `shared` 是 TypeSheet 可观察的类型属性，而不是引用槽上的限定符。运行时按以下域解释对象和值：

- **非 rich ValueType**：不含托管引用，`refMap` 恒为空；复制、构造和栈上计算完全不进入 GC 引用图。`String` 属于本域（字符数据是特权裸缓冲区，不是托管引用，见 §4）。
- **rich ValueType**：可以含托管引用，仍遵守值语义和 Box 的 unique ownership；复制/销毁时按 `refMap` 对内部引用执行 acquire/release。只有显式 rich 的 wrapper 属于本域；wrapper 默认非 rich（见 §14）。
- **shared rich ValueType**：rich ValueType 的共享安全子集；内部只能指向 shared Object，并只能内嵌非 rich 或 shared rich ValueType。`shared wrapper` 属于本域。
- **local Object**：未标记 `shared` 的 class 实例，归创建它的 Coroutine 所有，引用计数由 microGC 管理。运行时计数使用原子操作以支持 Task 异常传播的多 waiter 持有；这不放宽语言的 shared 闭包限制。
- **shared Object**：标记 `shared` 的 class 实例，可由多个 Coroutine 持有，引用计数由 microSGC 以同步 ARC 管理。

静态闭包保证 shared Object 和 shared rich ValueType 只能指向 shared Object，并只能内嵌非 rich/shared rich ValueType；local Object 可以指向 local/shared Object，并持有任意 ValueType。由此 shared 图不可能反向到达 local 对象域。shared 只决定共享资格与 GC 路径，不自动为用户字段提供线程安全。

编译器保证进入全局/静态存储与 async 边界的值必然属于「shared Object ∪ shared rich ValueType ∪ 非 rich ValueType」这三个域（`SYNTAX.md` §3.1.1 的共享安全类型）。运行时可以依赖这一不变量，不为跨边界的 local 值提供任何动态检查或搬运机制。

---
