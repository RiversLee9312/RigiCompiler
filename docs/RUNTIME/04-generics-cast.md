# 泛型运行时 / Type / 类型检查 / cast（§10–§13）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 10. 泛型的运行时实现

Rigi 泛型不擦除实际类型。实现采用**单份共享 Native 代码体 + 隐式 typeid 侧信道 + 统一胖值 ABI**；不为每组类型实参重复生成机器码，但泛型体在运行时始终能取得真实类型。

编译器向泛型函数/类型隐式传入类型信息：

| 泛型形态 | 传入的类型信息 |
|----------|----------------|
| 单个类型参数 | `typeid` |
| 具名可变参数 | `Map\<String, typeid>` |
| 位置可变参数 | `Array\<typeid>` |

编译器传参形态（与 `BIL_STANDARD.md` §7 一致）：泛型函数的 `.args` 以 `.generic.T = .typeid` 隐藏参数承载固定泛型参数、`.generic.TArgs` 承载可变泛型包（`.array<.typeid>` / `.map<.string, .typeid>`），按 §7.2 规范序排列；调用点静态类型实参以 `getid.type` 物化 typeid（BIL §12.5），嵌套泛型调用把接收到的 `.generic.T` 隐藏参数原样转发。以 `.` 开头的隐藏参数名由编译器保留，普通源码参数不得声明同名标识符。

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
- **`new a(...)`**：显式发起普通构造。`a` 可以是静态类型符号，也可以是 `Type\<T>` 值；普通类型的构造必须经 `new` 发起，`TypeName(...)` 不构成构造调用。泛型体内 `T()` 与动态 `new` 共用同一套 typeid 构造机制。
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
