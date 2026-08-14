# 类（§9）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 9. 类

### 9.1 类声明

```rigi
pub open class Animal {
    pub var name: String
    priv var age: i32

    pub init(_ -> name, _ -> age)

    pub func speak(): String {
        return "..."
    }
}

class Dog : Animal implements Comparable {
    pub init(_ -> name, _ -> age) {
        // ...
    }

    override func speak(): String {
        return "Woof!"
    }
}

pub shared class SharedSession {
    pub var owner: SharedUser
    pub var cursor: Point
}
```

未标记 `shared` 的 class 实例是 local object；标记 `shared` 的 class 实例是 shared object。shared class 的全部继承字段和直接字段只能形成 §3.1.1 所定义的共享闭包；`shared` 按 §3.1.1 单向传染——shared 基类的子类必须 shared，而 shared 子类可以继承非 shared 基类，前提是继承来的字段同样满足共享闭包。`shared` 不等同于锁、原子或 actor isolation；并发读写共享可变字段仍需要显式同步。

### 9.2 修饰符

| 修饰符 | 作用 |
|--------|------|
| `open` | 允许 class 或 rich struct 被继承；`enum struct` 与非 rich struct 明确禁止使用 |
| `abstract` | 抽象（天然 open，与 open 互斥）；非 rich struct 禁止使用 |
| `override` | 声明对继承链上 `open` 成员的覆写 |
| `singleton` | 单例（类似 Kotlin 的 `object`）；实例存储为全局存储，因此必须同时标记 `shared`（§3.1.1） |
| `pub` | 公开访问 |
| `protected` | 子类与同包可见 |
| `internal` | 模块内访问 |
| `priv` | 私有访问（显式，与默认一致） |
| （无） | private（默认） |
| `static` | 静态方法/字段 |
| `rich` | 允许 struct 直接或间接持有 Object；仅适用于 struct/enum struct（wrapper 恒为 rich，不显式书写） |
| `shared` | 将 class 声明为可跨协程共享的对象类型，或将 rich struct / wrapper 声明为可进入共享图的值类型 |
| `async` | 调用时创建新协程并返回 Task；函数写在声明前（`async func`）；lambda 写在 `{` 之后、参数列表之前（`func{async (...)...}`，见 §5.4） |
| `native` | 声明无函数体的原生函数，由运行时原生方法面提供实现；仅适用于函数，须配 `@NativeLibrary`（§4.6） |

#### 9.2.1 `override` 配套规则

- `open`/`override` 也适用于非 static 成员 getter/setter，且 getter 与 setter 分别是独立的多态单元；字段本身、全局访问器和 static 访问器不接受这两个修饰符。`abstract` 仍仅适用于普通成员方法；`init` 与 `static` 方法不参与多态。**callable 协议例外**（SYNTAX §5.2）：`operator call` 可 `abstract`/`override`/`async`（`core.Func`/`Action`/`AsyncFunc`/`AsyncAction` 基类族与 lambda 隐藏类覆写依赖此例外）；其余 operator 仍不参与多态。
- `override` 必须在基类链或接口表中找到签名匹配（名称 + 参数类型序列 + 返回类型均严格相等）的 `open`/`abstract` 方法或接口成员；找不到、或目标非 `open`/`abstract`，均为编译错误。
- 与继承成员同名同签名的成员必须显式 `override`（禁止静默隐藏）。
- `abstract` 方法必须位于 `abstract` 类内；接口之外的无体方法必须标 `abstract` 或 `native`。
- 非 `abstract` 类必须实现继承链上全部 `abstract` 成员与无体接口成员（有默认实现的接口成员隐式继承；§11 的显式委托语法 `override func m() -> InterfaceName` 不支持）；`new` 一个 `abstract` 类是编译错误。

#### 9.2.2 `super(...)`

`super(...)` 是保留调用名，只能写成调用，不能作为值、不能链式访问（没有 `super.run(...)`）。在 regular 方法中，它仅允许出现在当前 `override` 方法体内，并在**直接基类**的同名实例 regular 方法重载中按普通重载规则选择；不再次按可见性过滤候选。init 体也可选调用 `super(...)`，候选仅为直接基类 init 重载，不要求调用或限制调用次数。static/global/proxy 体及非 override regular 方法中均非法。

override 方法的固定泛型参数按当前声明序隐式转发，源码调用点不写显式泛型；含泛型可变参数包的 super 转发为编译错误。`super` 绕过 wrapper 派发链，BIL 只生成 `fn(..super)`（§15.5）。

### 9.3 构造函数（`init`）

```rigi
pub class Point {
    pub var x: i32
    pub var y: i32

    // 参数直接映射到字段（_ 表示参数名与字段名相同）
    pub init(_ -> x, _ -> y)

    // 带默认值
    pub init(_ -> x = 0, _ -> y = 0)

    // 混合：映射参数 + 普通参数
    pub init(_ -> x, _ -> y, label: String) {
        // label 不映射到字段，在函数体中使用
    }

    // 显式参数名
    pub init(horizontal: i32 -> x, vertical: i32 -> y)
}
```

参数映射语法：`[modifier...] param_name[:type] -> field_name [=default_value]`
- `param_name` 为 `_` 时，参数名自动与字段名相同
- `type` 省略时，沿用字段的类型

**默认构造**：未声明任何显式 `init` 的类型隐含一个零参公有构造函数（默认构造），`new T()` 经它完成构造——全部字段初始化为其默认值：声明处带初始化器的取初始化器，否则取该类型的零值。一旦声明任意显式 `init`，默认构造不再隐含，零参构造必须显式书写。

### 9.4 属性（getter/setter）

getter/setter 可以在以下所有位置定义：类/struct 的字段、全局变量、栈上的 `var` 和 `const`。

```rigi
var width: i32 {
    pub get(value: _) {
        return value
    }
    priv set(value: _) {
        // ...
    }
} = 100

// 只定义访问控制，让编译器生成实现
var height: i32 {
    pub get
    priv set
} = 200

// 栈上变量也可以定义
pub func example() {
    var localCounter: i32 {
        get(value: _) { return value }
        set(value: _) { log("set to ${value}") }
    } = 0
}
```

- `value` 参数：表示需要编译器生成 backing field
- `_` 参数：表示不需要 backing field（计算属性）
- get 和 set 在是否需要 backing field 上必须保持一致

#### 9.4.1 绑定语义

- 访问器上的修饰符允许访问级别以及 `open`/`override`；访问器的可见性 = 访问器显式修饰 ?? 字段声明的访问级别 ?? private。`open` 与 `override` 互斥，getter/setter 分别检查继承目标。接口不能声明字段或属性访问器。
- **backing 形态**（`value: _`）：编译器生成隐藏 backing 存储（永为私有，用户不可直接访问）；访问器体内 `value` 是 backing 的别名——getter 体内只读、setter 体内可读写。setter 语义 = 进入时隐含 `backing = value`（`value` 即新值），随后执行体；体可改写 `value`（即改写 backing），用于钳制、通知等场景。
- **自动访问器**（无体，如 `pub get` / `priv set`）：编译器合成实现——getter 为 `return value`，setter 为空体（隐式 `backing = value` 已足）。无体 + 计算形态（无 backing）是编译错误（编译器无法生成计算实现）。
- `const` 字段不得声明 setter。仅声明 get 的字段不可写、仅声明 set 的字段不可读；访问器自身的可见性在读写使用点分别检查。
- 带访问器的字段，外部读写一律经访问器；其读取结果不参与 smart cast 收窄（§3.5）。
- **栈上局部变量/常量的访问器**（路线 C，与闭包 cell 共用机制）：
  - 带访问器的局部声明在绑定期立即 cell 化（无论是否被捕获）：合成 `Cell`/`ReadonlyCell` 隐藏子类，`override getValue` 体 = 用户 getter 体、`override setValue` 体 = 用户 setter 体。
  - **backing 形态**（`value` 参数）：cell 的 `value` 字段即 backing 存储；访问器体内 `value` 是该字段的别名；自动访问器（无体）= 默认透传（`return value` / 隐含 `value =`）。
  - **计算形态**（`_` 参数）：cell `value` 字段形态保留但闲置（实现统一）；无体 + 计算形态仍是编译错误（§9.4.1）。
  - 读 = `getValue` 调用、写 = `setValue` 调用（与捕获 cell 读写同构）；仅 get 不可写、仅 set 不可读。
  - **捕获**：带访问器局部被 lambda 捕获 = 已 cell 化变量按引用直捕（§5.2）；lambda 内读写同样经 getValue/setValue，访问器代理一切读写。访问器体引用的外层局部/参数/this 按 lambda 同规则捕获进该 cell（init 追加捕获实参）。
  - **修饰符**：局部访问器无可见性/多态概念——禁止 `pub`/`priv`/`protected`/`internal`/`open`/`override`。
  - `const` 局部不得声明 setter；带访问器局部不参与 smart cast 收窄（cell 化根一律不收窄）。
  - 参数访问器：形参列表不接受访问器块；若出现则为编译错误。

### 9.5 内部类

```rigi
pub class Outer {
    pub class Inner { ... }
    pub singleton class Companion { ... }   // 类似 Java 静态内部类
}
```

### 9.6 委托（`like`）

```rigi
pub class Apple : Fruit like pear {
    pub var pear: Pear = Pear()
    // 将 Fruit 接口的实现委托给 pear 字段
}
```

---
