# 模块系统 / 访问修饰符 / 程序入口 / 解构声明（§15–§18）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 15. 模块系统

### 15.1 命名空间声明

```rigi
namespace com.example.myapp
```

### 15.2 导入

```rigi
import core.collections.List             // 单个导入
import core.collections.{List, Map}      // 多个导入
import core.collections.*                // 全部导入
```

规则：
- `{}` 列表项只能是单标识符，不允许带路径——`import core.collections.{a.List}` 是编译错误。需要导入不同子路径的符号时写多条 `import` 语句。

### 15.3 SDK 自举源

编译器自带的标准库 Rigi 源（`stdlib/`）随每次编译**默认参与编译**，无需
import 即进入编译单元（与用户源同走语义全流程）：

- `core.collections`：`IEnumerable\<T\>` / `IEnumerator\<T\>` 迭代协议
  （§7.3）与容器接口、实现；
- `core.io`：`Console` 等 I/O 表层；
- `core.coroutine`：`Task` / `Task\<TResult\>` / `Executor` 家族 /
  `PollingAlarm` / `EventAlarm` / `CoroutineLocal\<TValue\>` 类型面与最小
  native API 面（§4.5/§7.5、`RUNTIME.md` §17–§20）——协程运行时机制是
  语言内建（async/await/yield lowering 见 §4.5/§7.5，BIL VM 提供
  执行），stdlib 只声明类型与 `sleep`/`isReady` 等运行时函数的形状；
  `Task`（无结果）与 `Task\<TResult\>`（泛型）是**同名不同元数**的合法
  共存类型（类型名唯一性按「名 + 泛型参数个数」判定；裸名引用解析到
  非泛型声明，带实参引用解析到泛型声明）；
- `core` 命名空间内的异常具体子类（`stdlib/core/exceptions.rg`）：
  `RuntimeException` / `IOException` / `CastException` /
  `NoSuchMethodException` / `DividedByZeroException`（§8.1）；
- `core` 命名空间内的 `IDisposable`（`stdlib/core/disposable.rg`，
  §6.2 确定性资源管理协议）；
- `.bootstrap.rg`：**基元类型自举辅助成员**——内建数值类型
  （`i32` 等）无法在自己的声明处携带这些实现，经 `ext` 以 Rigi 自举
  （如 `EnumerateInRange`，§13.2），以及解构协议根 `core.Pair`（§18）；
  另含 callable / 闭包运行时面（§5.2）：
  - `core.Func\<TRet, T0…\>` / `core.Action\<T0…\>` /
    `core.AsyncFunc\<TRet, T0…\>` / `core.AsyncAction\<T0…\>`（各 0–32 元数变种，
    abstract class + abstract `operator call`；Async 族为 `shared class`）；
  - `core.Cell\<T\>` / `core.ReadonlyCell\<T\>`——**抽象基类**（抽象
    `getValue`/`setValue`——ReadonlyCell 无 `setValue`；无 `value` 字段、
    无显式 init）；实际实例恒为编译器合成的隐藏子类 `..cell..UUID`
    （统一 cell 存储，§5.2 / §14.3）；BIL 特权拼写 `.cell<T>`/
    `.readonly_cell<T>` 保留供 Middleware 激进优化识别。

**bootstrap 与 stdlib 的边界**：语言级类型层级根与基元类型
（`Any`/`Object`/`ValueType`/`Enum`/`Wrapper`/`Exception` 与 §3.2 基本类型、
§3.1.2 特权泛型类型）由编译器硬编码构造进符号图（`BootstrapSymbols`），
从不写入 `stdlib/` 源——它们的层级关系、内建运算符键与 shared 推导是
编译器语义的一部分，无法用 Rigi 声明表达；异常根 `core.Exception` 的
`message` 字段与 `getMessage()` 同样由 bootstrap 程序化携带（§8.1）。
其余全部标准库表面走 `stdlib/` Rigi 源，与用户源同一条 P1–P4 路径
（含上列 Func/Action/Cell 族——它们虽由 `.bootstrap.rg` 提供，仍走源路径）。

---

## 16. 访问修饰符

| 关键字 | 可见性 |
|--------|--------|
| `pub` | 公开（所有地方可见） |
| `protected` | 子类和同包可见 |
| `internal` | 模块内可见（项目级别） |
| `priv` | 私有（当前类/文件内可见，显式） |
| （无） | private（默认，当前类/文件内可见） |

默认访问级别为 private。构造函数、对外可见的字段和方法都需要显式标注 `pub`。

### 16.1 可见性判定规则

- **private（`priv` 或默认）**：顶层声明（类型/全局函数/全局变量）仅在**当前文件**内可见；类型成员仅在**声明类型及其嵌套类型**（递归）内可见。
- **`protected`**：两种位置可见——使用点所在宿主类型沿基类链可达成员的宿主类型（子类体内）；或与成员宿主同属一个**包**。「包」即同一命名空间（限定名全等，不含子命名空间）。
- **`internal`**：模块（项目）内可见。当前编译模型以一次编译的编译单元为模块，internal 在单元内恒可见。
- **`pub`**：无限制。

ext 成员（§4.4）的可见性按**声明位置**判定而非目标类型：顶层 ext 声明适用顶层规则（private = 仅当前文件可见）。ext 方法/访问器体不因此获得目标类型私有成员的访问特权。

接口成员默认 `pub`（接口即契约）；其余声明默认 private 不变。访问控制在使用点检查：类型引用（声明侧与函数体内）、继承、成员访问（字段/方法/索引运算符）与构造调用（含 `init` 可见性，§12.2）均为使用点。

---

## 17. 程序入口

```rigi
// 无参数
pub func main(): i32 {
    return 0
}

// 带命令行参数
pub func main(args: Array\<String>): i32 {
    return 0
}

// 无返回值
pub func main() {
    ...
}
```

运行时从 `main` 开始就创建根协程，并默认将其绑定到 `core.coroutine.MainExecutor`。因此 `main` 以及由它同步调用的普通函数可以直接使用 `await` 和 `yield`，不需要给 `main` 添加 `async` 修饰符。

---

## 18. 解构声明

```rigi
var (key, value) = pair   // pair 必须为 core.Pair\<TKey, TValue> 的子类
```

规则：解构名字必须恰好两个，按声明序绑定到 `key`/`value` 分量（类型取 `core.Pair` 构造的实参）；解构必须带初始化器，不支持类型标注；`const (k, v) = pair` 同样适用（分量局部只读）。`core.Pair` 是 `stdlib/.bootstrap.rg` 的自举 open class（§15.3），可继承——用户类型经继承它获得解构能力。

---
