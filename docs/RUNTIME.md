# Rigi 运行时模型

本文档描述 Rigi 的运行时表示与语义，回答"怎么跑"。语言表层语法见 `SYNTAX.md`。

核心取舍：**运行时类型信息完全具化（reified）+ 单份共享 Native 代码体 + 统一胖值槽**。Rigi 不擦除泛型实参的实际类型；所有独特能力（`is`/`supers`/`with`、`Type\<T>`/`new`、wrapper 派发）都建立在“typeid 始终伴随值与泛型调用”这一机制上。ValueType 进入统一泛型/动态槽位时使用系统特权 Box 表示，而不是退化成普通堆对象。

---

本文件是**目录索引**。正文已按主题拆分到 `RUNTIME/` 子目录，
章节号（§）与原文件完全一致、永不重排——代码与文档中的 `§N.M` 引用经本索引定位分文件。

| § | 章节 | 文件 |
|---|---|---|
| §1–§3 | 设计总览与取舍 / 胖引用（128-bit）/ 引用写入原子性与并发 | [RUNTIME/01-overview-fat-reference.md](RUNTIME/01-overview-fat-reference.md) |
| §4–§5 | Box（统一泛型值槽）/ `Span\<T>` | [RUNTIME/02-box-span.md](RUNTIME/02-box-span.md) |
| §6–§9 | TypeSheet / vtable 模型 / iMap、refMap 与 GC 追踪 / 加载期扁平化 | [RUNTIME/03-type-metadata.md](RUNTIME/03-type-metadata.md) |
| §10–§13 | 泛型的运行时实现 / `Type\<T>`·typeOf·new / is·supers·with / cast | [RUNTIME/04-generics-cast.md](RUNTIME/04-generics-cast.md) |
| §14–§16 | Wrapper 派发管线 / 派发链诊断工具 / enum struct 的运行时表示 | [RUNTIME/05-wrapper-dispatch-enum.md](RUNTIME/05-wrapper-dispatch-enum.md) |
| §17–§21 | 原生协程、Executor 与 Worker / async 调用与 Task / yield 与 Alarm / 内置 Executor 与 CoroutineLocal / 协程、GC 与同步边界 | [RUNTIME/06-coroutines.md](RUNTIME/06-coroutines.md) |
| §22–§24 | 三级 GC / macroGC ownership fence / macroGC 的性能取向与可观察语义 | [RUNTIME/07-gc.md](RUNTIME/07-gc.md) |
| §25–§26 | 确定性资源管理（IDisposable、using 与全局泄漏异常）/ native 互操作与 `rigi_rt` | [RUNTIME/08-resources-native.md](RUNTIME/08-resources-native.md) |

## 维护约定

- 新增运行时机制进对应主题文件，沿用既有 `## N.` / `### N.M` 编号；新增整章时在末尾续号并回本索引登记。
- 不记录里程碑/进度信息（历史见 `legacy/`）；只描述运行时的当前规范行为。
