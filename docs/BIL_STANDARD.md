# Rigi BIL 标准

> 状态：规范草案 1.1  
> BIL：Basic Intermediate Language（基础中介语言）

本文档定义 Rigi 编译器 frontend 与 Middleware 之间的标准中间表示 BIL。

本文档只规定 BIL 的程序结构、类型规则、符号模型、指令语义、验证规则和文本表示。Rigi 表层语言规则以 `SYNTAX.md` 为准；Native 运行时表示与行为以 `RUNTIME.md` 为准。

---

本文件是**目录索引**。正文已按主题拆分到 `BIL_STANDARD/` 子目录，
章节号（§）与原文件完全一致、永不重排——代码与文档中的 `§N.M` 引用经本索引定位分文件。

| § | 章节 | 文件 |
|---|---|---|
| §1–§3 | 定位与编译边界 / 规范用语 / BIL 的核心不变量 | [BIL_STANDARD/01-overview.md](BIL_STANDARD/01-overview.md) |
| §4–§5 | 文件与程序集结构 / 词法规则与符号名称 | [BIL_STANDARD/02-file-structure-lexical.md](BIL_STANDARD/02-file-structure-lexical.md) |
| §6–§7 | 类型系统 / 泛型与可变参数的规范签名 | [BIL_STANDARD/03-types-generics.md](BIL_STANDARD/03-types-generics.md) |
| §8–§9 | 符号模型 / 函数、参数、局部变量与 block | [BIL_STANDARD/04-symbols-functions.md](BIL_STANDARD/04-symbols-functions.md) |
| §10–§11 | 操作数与求值规则 / 运算指令 | [BIL_STANDARD/05-operands-compute.md](BIL_STANDARD/05-operands-compute.md) |
| §12–§13 | 转换、wrapper 与运行时类型指令 / 值、变量、字段与索引指令 | [BIL_STANDARD/06-conversion-data.md](BIL_STANDARD/06-conversion-data.md) |
| §14 | 构造指令 | [BIL_STANDARD/07-construction.md](BIL_STANDARD/07-construction.md) |
| §15 | 方法调用 | [BIL_STANDARD/08-invocation.md](BIL_STANDARD/08-invocation.md) |
| §16–§18 | 结构化控制流 / 协程指令 / 提示指令 | [BIL_STANDARD/09-control-coroutine-hint.md](BIL_STANDARD/09-control-coroutine-hint.md) |
| §19–§20 | Resources 文本格式 / 完整文本示例 | [BIL_STANDARD/10-resources-example.md](BIL_STANDARD/10-resources-example.md) |
| §21–§22 | BIL 验证器 / BIL VM 语义要求 | [BIL_STANDARD/11-verifier-vm.md](BIL_STANDARD/11-verifier-vm.md) |
| §23–§27 | Middleware 合法 lowering 的边界 / 与 SYNTAX·RUNTIME 的职责关系 / Legacy 迁移 / 未来扩展 | [BIL_STANDARD/12-middleware-future.md](BIL_STANDARD/12-middleware-future.md) |

## 维护约定

- 新增规则进对应主题文件，沿用既有 `## N.` / `### N.M` 编号；新增整章时在末尾续号并回本索引登记。
- 不记录里程碑/进度信息（历史见 `legacy/`）；只描述 BIL 的当前规范。
