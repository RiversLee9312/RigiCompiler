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
- 具名导入的目标可以是泛型类型定义（导入的是定义本身，实参在使用处书写，如 `import core.Pair` 后写 `Pair\<i32, String>`）。同名不同元数共存时，具名导入的裸名按使用处同一规则命中非泛型声明；泛型兄弟经通配导入或全限定名可达。
- 具名导入的目标也可以是命名空间的**顶层函数**或**全局字段/常量**（S4）：`import scene.geom.pickAxis` 后即可裸名调用 `pickAxis(...)`。导入的是名字而非某个签名——同名重载全部随导入进入候选池，按常规重载解析消歧。具名导入与通配导入同名冲突时具名优先；两条具名导入同名且分属不同容器时报 `Ambiguous import`；文件命名空间内的本地同名声明优先于任何 import。

### 15.3 SDK 自举源

编译器自带的标准库 Rigi 源（`stdlib/`）随每次编译**默认参与编译**，无需
import 即进入编译单元（与用户源同走语义全流程）：

- `core.collections`：`IEnumerable\<T>` / `IEnumerator\<T>` 迭代协议
  （§7.3）与容器接口、实现；
- `core.io`：`Console` 等 I/O 表层；
- `core.coroutine`：`Task` / `Task\<TResult>`（具体 shared class，pub
  init 冷 Task 构造与 run/executor/state API，§4.5）/ `TaskState` /
  `Executor` 家族（基类 abstract；`MainExecutor`/`ComputeExecutor`/
  `IOExecutor` 为 `pub shared singleton class`，`RUNTIME.md` §20.1）/
  `Mutex`（异步互斥锁 + 嵌套 `Lock` 令牌，`RUNTIME.md` §19.6）/
  `Timer`（`EventAlarm` 子类 + 嵌套 `RepeatOption`，`RUNTIME.md`
  §19.5）/ `PollingAlarm` / `EventAlarm` / `CoroutineLocal\<TValue>`
  类型面与最小 native API 面（§4.5/§7.5、`RUNTIME.md` §17–§20）——
  调度逻辑（Dispatcher）与 Task 生命周期由 Rigi 世界实现，native 只留
  Worker/协程句柄/定时器/同步锁/TLS/时钟原语（`RUNTIME.md` §17.4），
  stdlib 声明类型面与 `sleep`/`isReady` 等运行时函数的形状；
  `Task`（无结果）与 `Task\<TResult>`（泛型）是**同名不同元数**的合法
  共存类型（类型名唯一性按「名 + 泛型参数个数」判定；裸名引用解析到
  非泛型声明，带实参引用解析到泛型声明）；
- `core.time`：`TimeStamp` / `DateTime` / `TimeSpan`（时刻戳（毫秒+纳秒双字段）、
  UTC 时刻与毫秒跨度，Timer 的时间底座，`RUNTIME.md` §19.7）；
- `core` 命名空间内的异常具体子类（`stdlib/core/exceptions.rg`）：
  `RuntimeException` / `IOException` / `CastException` /
  `NoSuchMethodException` / `DividedByZeroException` /
  `OutOfBoundException` / `IllegalStateException`（§8.1）；
- `core` 命名空间内的 `IDisposable`（`stdlib/core/disposable.rg`，
  §6.2 确定性资源管理协议）；
- `.bootstrap.rg`：**基元类型自举辅助成员**——内建数值类型
  （`i32` 等）无法在自己的声明处携带这些实现，经 `ext` 以 Rigi 自举
  （如 `EnumerateInRange`，§13.2），以及解构协议根 `core.Pair`（§18）；
  另含 callable / 闭包运行时面（§5.2）：
  - `core.Func\<TRet, T0…>` / `core.Action\<T0…>` /
    `core.AsyncFunc\<TRet, T0…>` / `core.AsyncAction\<T0…>`（各 0–32 元数变种，
    abstract class + abstract `operator call`；Async 族为 `shared class`）；
  - `core.Cell\<T>` / `core.ReadonlyCell\<T>`——**抽象基类**（抽象
    `getValue`/`setValue`——ReadonlyCell 无 `setValue`；无 `value` 字段、
    无显式 init）；实际实例恒为编译器合成的隐藏子类 `..cell..UUID`
    （统一 cell 存储，§5.2 / §14.3）；BIL 特权拼写 `.cell<T>`/
    `.readonly_cell<T>` 保留供 Middleware 激进优化识别。

**bootstrap 与 stdlib 的边界**：语言级类型层级根与基元类型
（`Any`/`Object`/`ValueType`/`Enum`/`Wrapper` 与 §3.2 基本类型、
§3.1.2 特权泛型类型）由编译器硬编码构造进符号图（`BootstrapSymbols`），
从不写入 `stdlib/` 源——它们的层级关系、内建运算符键与 shared 推导是
编译器语义的一部分，无法用 Rigi 声明表达。异常根 `core.Exception`
不在硬编码之列：它由 `stdlib/core/exceptions.rg` 源码声明（`pub
abstract class Exception`，protected `message` 字段 + abstract
`getMessage()`，§8.1 同形），bootstrap 侧按名懒解析进符号图。
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

补充两条规则（bug S5 修复）：

- **签名可见性单调性**：`pub`/`protected`/`internal` 顶层函数与成员的签名（返回类型与参数类型）不得提及有效可见性更低的类型，声明点即编译错误（`Inconsistent accessibility`）。有效可见性取符号自身与嵌套宿主链的逐级最小值（`priv` 类内的 `pub` 成员按 `priv` 论）；构造类型递归检查实参（`Box\<Hidden\>` 的泄漏点是 `Hidden`）。可见性按 §16 层级比较：`pub` 签名出现 `internal` 类型、`internal` 签名出现 `private` 类型均报错。字段/全局变量与属性访问器同闸（F2）：`pub var x: Hidden` 与 `pub get` 暴露低可见性类型同样在声明点报错（访问器签名即字段类型，可见性可独立于字段，§9.4.1）。
- **推断类型也是使用点**：无类型标注的 `const`/`var` 推断、解构分量与 `for-in` 元素等推断定型位置同样是使用点，推断得到不可见类型即在声明推断点报错（一次），下游成员访问不再重复报同一类型的诊断。

再补两条规则（F2）：

- **继承可见性单调性**（C# CS0060/CS0061 式）：基类/基接口（含 `implements` 与接口继承）的有效可见性不得低于派生类型——`pub` 类继承 `private` 基类、实现 `private` 接口均为声明点编译错误（`Inconsistent accessibility: base ... is less accessible than ...`）。合法形态不受影响：同文件 `private` 基类 + `private` 派生、`pub` 基类 + `pub` 派生、`internal` 同级组合。
- **like 合成成员随来源接口可见性**（§9.6）：like 委托合成的 `pub` 转发器把被委托接口成员提升为本类公开契约，其泄漏面由继承单调性在声明点收口——`pub` 类实现 `private` 接口即报错，合成转发器不再可能把不可见接口成员暴露出去。

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

### 17.1 @EntryPoint 注解

`@EntryPoint` 是编译器内建注解（与 §4.6 `@NativeLibrary`/`@NativeSymbol`
同族——不属于 wrapper 体系，不产生组合链），修饰一个**静态方法**即把
它登记为程序入口；任意命名空间的静态方法都可以，不再受「裸 `main`
必须在全局命名空间」的命名约定限制：

```rigi
namespace myapp

@EntryPoint
pub func main(): i32 {          // myapp::main 成为入口
    return 0
}

// 静态成员方法同样合法
pub class App {
    @EntryPoint
    pub static func run(): i32 { return 0 }
}
```

规则：

- 只允许修饰普通 `func`（不能是 `init`/`operator`/`native`）；类型成员必须同时是 `static`；不接受实参。
- 全局命名空间的裸 `main` 命名约定仍然有效（不写注解也是入口）。
- 一次编译/合并后的 BIL 中允许存在**多个**入口（多个 `@EntryPoint` 或与裸 `main` 并存）；此时运行前必须显式选择：

```bash
rigic vm --file app.bil ... --entry-point "myapp::$main()@.i32"
```

缺省（恰一个入口）自动选中；零个或多个入口都是运行前错误。
入口签名仍按本节上面的三种形态；VM 以零实参启动入口 fn。

### 17.2 BIL 按命名空间切分

`compile --emit-bil <路径>` 默认把一次编译的 BIL 按**命名空间**切分为多个文件：全局命名空间写入指定路径，其余每个命名空间写入同目录的 `<基名>.<命名空间><扩展名>`（如 `app.core.bil`、`app.core.collections.bil`）。切片是 merge 兼容的：资源全局统一编号、每个符号/fn 恰好归属一个切片，`vm --file` 传入全部切片即还原完整模块。

---

## 18. 解构声明

```rigi
var (key, value) = pair   // pair 必须为 core.Pair\<TKey, TValue> 的子类
```

规则：解构名字必须恰好两个，按声明序绑定到 `key`/`value` 分量（类型取 `core.Pair` 构造的实参）；解构必须带初始化器，不支持类型标注；`const (k, v) = pair` 同样适用（分量局部只读）。`core.Pair` 是 `stdlib/.bootstrap.rg` 的自举 open class（§15.3），可继承——用户类型经继承它获得解构能力。

---
