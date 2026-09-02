# Rigi 语言语法参考

本文档是语法参考的**目录索引**。正文已按主题拆分到 `SYNTAX/` 子目录，
章节号（§）与原文件完全一致、永不重排——代码与文档中的 `§N.M` 引用经本索引定位分文件。

| § | 章节 | 文件 |
|---|---|---|
| §1–§2 | 基本规则 / 变量声明 | [SYNTAX/01-basics.md](SYNTAX/01-basics.md) |
| §3 | 类型系统（层级 / rich·shared / 基本类型 / 字面量 / 空安全 / 转换与检查 / 泛型 / 反射 / 字符串转换与插值） | [SYNTAX/02-type-system.md](SYNTAX/02-type-system.md) |
| §4 | 函数（声明 / 调用与重载 / 可变参数 / 扩展 / async 与 Task API / native） | [SYNTAX/03-functions.md](SYNTAX/03-functions.md) |
| §5 | Lambda 表达式 | [SYNTAX/04-lambda.md](SYNTAX/04-lambda.md) |
| §6 | `seq` 块（语句块） | [SYNTAX/05-seq-block.md](SYNTAX/05-seq-block.md) |
| §7–§8 | 控制流 / 异常处理 | [SYNTAX/06-control-flow.md](SYNTAX/06-control-flow.md) |
| §9 | 类（声明 / 修饰符 / init / 属性 / 内部类 / 委托 like） | [SYNTAX/07-classes.md](SYNTAX/07-classes.md) |
| §10–§12 | struct / interface / enum struct | [SYNTAX/08-struct-interface-enum.md](SYNTAX/08-struct-interface-enum.md) |
| §13 | 运算符 | [SYNTAX/09-operators.md](SYNTAX/09-operators.md) |
| §14 | Wrapper（修饰器） | [SYNTAX/10-wrapper.md](SYNTAX/10-wrapper.md) |
| §15–§18 | 模块系统 / 访问修饰符 / 程序入口 / 解构声明 | [SYNTAX/11-modules-access-entry.md](SYNTAX/11-modules-access-entry.md) |
| §19 | 关键字一览 | [SYNTAX/12-keywords.md](SYNTAX/12-keywords.md) |
| §20 | 序列化（@Serializable / @SerializationBase / @Temporary / @Terminal 与 Parcel） | [SYNTAX/13-serialization.md](SYNTAX/13-serialization.md) |

## 维护约定

- 新增语言规则进对应主题文件，沿用既有 `## N.` / `### N.M` 编号；新增整章时在末尾续号并回本索引登记。
- 不记录里程碑/进度信息（历史见 `legacy/`）；只描述语言的当前规范行为。
