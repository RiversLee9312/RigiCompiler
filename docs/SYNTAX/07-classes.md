# 类（§9）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 9. 类

### 9.1 类声明

```rigi
pub open class Animal {
    pub var name: String
    priv var age: i32

    pub init(_ -> name, _ -> age)

    pub open func speak(): String {
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

- `open`/`override` 也适用于非 static 成员 getter/setter，且 getter 与 setter 分别是独立的多态单元；字段本身（见下方字段覆写条款）、全局访问器和 static 访问器另有规则。`abstract` 仍仅适用于普通成员方法；`init` 与 `static` 方法不参与多态。**callable 协议例外**（SYNTAX §5.2）：`operator call` 可 `abstract`/`override`/`async`（`core.Func`/`Action`/`AsyncFunc`/`AsyncAction` 基类族与 lambda 隐藏类覆写依赖此例外）；其余 operator 仍不参与多态。
- **字段覆写（`open`/`override` 字段）**：class/struct 的非 static 字段可标 `open`——允许子类以同名字段标 `override` 给出**不同的初始值**。语义：override 字段的类型必须与基类字段一致（构造基类按 `extends` 实参代入后比较）；**存储仍是基类槽**（不产生新字段槽，名称解析与 BIL 都落到基类字段）；override 只替换初始值——编译器为每个带声明初始值的实例字段生成可覆写的 `..init.field.<名>` 方法（§9.3/§9.7），子类 override 字段生成同族 override 版，虚派发自动选中最高派生实现。规则：override 字段必须给出新初始值（无新初始值=编译错误）；被 override 的基类字段必须标 `open`（非 open=编译错误）；override 字段不得携带 wrapper 应用与访问器（访问器覆写归基类字段上的访问器机制）；`open` 字段自身可以没有初始值（子类 override 补初始值合法）。带访问器的同名字段重声明归访问器 override 机制（getter/setter 各自带 `override`）；无访问器的同名字段当且仅当**双方都带声明初始值**时必须显式 `override`（否则静默 hiding 会让基类槽初值在子类构造中被同名 `..init.field` 族的虚派发吞掉，为编译错误）；单方带初始值的 hiding 沿用既有行为。
- `override` 必须在基类链或接口表中找到签名匹配（名称 + 参数类型序列 + 返回类型均严格相等，且 `async` 修饰符一致——sync 成员与 async 成员互不构成合法覆写目标，违反为专项编译错误）的 `open`/`abstract` 方法或接口成员；找不到、或目标非 `open`/`abstract`，均为编译错误。
- 与继承成员同名同签名的成员必须显式 `override`（禁止静默隐藏）；仅 `async` 修饰符不同的同名同签名成员同样禁止（按 async 不一致专项诊断拦截）。
- `abstract` 方法必须位于 `abstract` 类内；接口之外的无体方法必须标 `abstract` 或 `native`。
- 非 `abstract` 类必须实现继承链上全部 `abstract` 成员与无体接口成员（有默认实现的接口成员隐式继承；§11 的显式委托语法 `override func m() -> InterfaceName` 不支持）；`new` 一个 `abstract` 类是编译错误。

#### 9.2.2 `super(...)`

`super(...)` 是保留调用名，只能写成调用，不能作为值、不能链式访问（没有 `super.run(...)`）。在 regular 方法中，它仅允许出现在当前 `override` 方法体内，并在**直接基类**的同名实例 regular 方法重载中按普通重载规则选择；不再次按可见性过滤候选。init 体也可选调用 `super(...)`，候选仅为直接基类 init 重载，不要求调用或限制调用次数。static/global/proxy 体及非 override regular 方法中均非法。

override 方法的固定泛型参数按当前声明序隐式转发，源码调用点不写显式泛型；含泛型可变参数包的 super 转发为编译错误。`super` 绕过 wrapper 派发链，BIL 只生成 `fn(..super)`（§15.5）。

**super init 实参的编译期 cast**：init 体内的 `super(...)` 经重载解析选定基类 init 后，前端对每个实参生成到该 init **形参声明类型**的 cast 指令（如 `Leaf` 实参 cast 到 `Node` 形参，落编译器生成的临时变量）再调用——构造重载的 ranking 只发生在语义期，运行期不再承担选择；VM 侧对 `fn(..super)` 的 init 重匹配按**可赋值性**进行（与 `new` 路径同口径：子类实参命中基类形参、`null` 命中可空形参），引用类型 upcast 不改写运行期 typeid，值类型窄化/装箱由该 cast 在调用点落定。

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

`init` 不能声明为 `async`（任何 init 都不允许是 async 的）。

**默认构造**：未声明任何显式 `init` 的类型隐含一个零参公有构造函数（默认构造），`new T()` 经它完成构造——字段取声明处初始化器。一旦声明任意显式 `init`，默认构造不再隐含，零参构造必须显式书写。默认构造（以及任何构造路径）同时受下面的字段定值赋值规则约束。

**字段定值赋值（DA，P18/S2）**：所有实体（值类型与对象同规则）的实例字段在分配后语义上视为**未赋值**（不再有「无初始化器取零值」的兜底）。非 `Nullable` 的实例字段必须满足三选一：

1. 带声明初始化器（编译器合成 `..init.field.<名>`，在任何 init 体之前由实际类型的 `..init.wrapper` 调用——构造进入 init 体时已赋值）；或
2. 在 `init` 里被显式赋值（`init(_ -> x)` 参数映射算赋值；经 setter 的属性赋值同样算）；或
3. 字段类型是 `Nullable\<T\>`（含 `T?`）。

检查全部是**前端静态检查**（编译器不设 VM 哨兵），检查点覆盖每条构造路径：

- **每个显式 init 重载的每条路径出口**——块尾与中途裸 `return` 都是出口；if/switch 取全分支交集，while/for 循环体可能零次执行（体内赋值不计入出口），do-while 取体尾，try/catch 取交集再叠 finally 并集。某条出口仍有未赋值的非空字段即编译错误。
- **super 与继承字段**：init 体内显式调用了 `super(...)` 时，基类闭包字段由基类 init 担保（基类 init 自身已过检），本 init 只对本类声明的字段负责；**不调 `super` 时**，基类无初始值的非空字段计入本 init 的义务——子类可以直接给可见（`pub`/`protected`）的基类字段赋值来满足（RangeEnumerator/异常子类先例），无法满足时报编译错误并引导调 `super(...)`。新 init 原则下基类字段的**声明初始值**已由 `..init.wrapper` 缝合，与此正交。
- **抽象类**自身不可构造，其未赋值非空字段不在声明点报错，义务转移给具体子类的 init（按上一条处理）。
- **无 init 类型**（从未声明 init）：声明点不报错（仅声明/抽象使用合法），零参 `new T()` 使用点要求不存在无初始值非空字段，否则编译错误。
- **无 init 的 enum struct**：固定 case 走默认零参构造，存在无初始值非空字段时在 case 声明点报错。
- 边界（不做 DA，保持既定零值/空值语义）：数组元素（native 魔法，`getAtIndex` 返回 `T?`、越界读得 null）；`ext` 实例字段（模块化附加槽，宿主 init 不应被迫感知）；仅 `get` 无 `set` 的访问器字段（无写入通道，规范本就不允许其携带初始值）；内建类型声明的字段（如 `core.Exception.message`——子类 init 可直接赋值，不强制）；全局/静态字段（初始值由 `..globals.init` 承载，局部变量另有局部 DA）；标量零值（`ZeroOf`/`i32()` 等内建零值语义不变）。泛型参数类型的字段（`var v: T`）按**非空悲观**计入义务——要豁免须显式写 `T?`。

**构造顺序（新 init 原则，§9.7 配套）**：`new T(...)` 的执行序固定为

1. **分配**：实例存储分配并零填充（含继承闭包全部字段槽；override 字段不新增槽）；
2. **`..init.wrapper`**：VM 只调用**实际类型**（分配类型）的 `..init.wrapper`，它由前端完成全部缝合——先安装继承闭包（基→本）的全部 wrapper（本类与基类的 Entity/Field/Method 应用，含基类未被 override 方法的 Method wrapper；§14.9 重申的同定义 Entity 应用按派生覆盖去重），再按基→本、声明序调用继承闭包全部 `..init.field.<名>`（每个带声明初始值的实例字段一个；字段 override 时虚派发选中最高派生实现，同一槽只写一次）。字段初始值因此**早于任何基类 init 体**落地；
3. **init 链**：用户显式 `super(...)` + `init(_ -> x)` 参数映射（映射覆盖初始值）+ 用户 init 体。

用户不调 `super()` 时基类**用户 init 体**不跑，但基类字段初始值与基类 wrapper 已被 `..init.wrapper` 缝合。此时基类**无初始值的非空字段**按 DA 规则计入子类 init 的义务（见上）：子类 init 必须自己给它们赋值，或调 `super(...)` 把义务交还基类 init。属性（带 setter 的字段）的初始值经 setter 应用（`..init.field.*` 内 `set.field` 自动走 setter；访问器被 override 时随虚派发）；仅 get 无 set 的字段无法携带初始值（编译错误）。基类 init 体读取字段时读到的是初始值而非零值；若该字段（或实体）带 wrapper，读取命中已安装的 wrapper 链。

**默认构造的链式**：隐式/合成零参构造的构造体仅含 `super()`（直接基类有零参 `init`——含基类被合成的情形——时）——基类用户 init 体的链式调用仍由逐环 `super()` 保证（`C : B : A` 链上按 A→B→C 顺序）；字段初始值与 wrapper 安装不再依赖该链（由第 2 步一次性缝合）。显式 `init` 里的 `super(...)` 维持可选显式调用（§9.2.2）。

**全局与静态字段初始值**：顶层全局字段（`var`/`const`）与类型的 `static` 字段的声明初始值由编译器合成的 `..globals.init` 全局 fn 承载（体内按文件序+声明序 `set.field.static`），VM 在 singleton 初始化（§8.7 companion/全局 cell）之后、`main` 之前同步执行。带 wrapper 的全局/静态字段不在此列——其初值随 cell/companion 的 `init` 求值（§14.3/§8.7）。

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
- **backing 形态**（`value: _`）：编译器生成隐藏 backing 存储（永为私有，用户不可直接访问）；访问器体内 `value` 是 backing 的别名——getter 体内只读、setter 体内可读写。setter 语义 = 进入时隐含 `backing = value`（`value` 即新值），随后执行体；体可改写 `value`（即改写 backing），用于钳制、通知等场景。BIL 表现：setter 体内对 `value` 的多次读写统一引用保留字段符号 `..value`（VM/Middleware 据此直写 backing，不再绕 wrapper）；getter 体内 `value` 只读，引用逻辑字段。
- **自动访问器**（无体，如 `pub get` / `priv set`）：编译器合成实现——getter 为 `return value`，setter 为空体（隐式 `backing = value` 已足）。无体 + 计算形态（无 backing）是编译错误（编译器无法生成计算实现）。
- `const` 字段不得声明 setter。仅声明 get 的字段不可写、仅声明 set 的字段不可读；访问器自身的可见性在读写使用点分别检查。
- 带访问器的实例字段，声明处初始化器**经 setter 应用**（默认构造合成普通的 `this.field = 初始化器` 赋值，读写一律经访问器的规则不变）——setter 语义（钳制、通知等）自初始化起生效；setter 进入时隐含 `backing = value`、体内可改写 `value`（即改写 backing），初始化同样走这条路径（backing 形态）。仅 get 无 set 的字段不得携带初始化器（无法经 setter 应用，编译错误；`const` 字段本就不得声明 setter，故 `const` + 仅 get + 初始化器同样被拒）。
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
    pub var pear: Pear = new Pear()
    // 将 Fruit 接口的实现委托给 pear 字段
}
```

委托目标字段的类型可以是类，也可以是接口；接口类型字段签名匹配即成立（允许抽象成员），转发调用在运行时对字段值虚派发。

---
