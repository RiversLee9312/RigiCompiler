# struct / interface / enum struct（§10–§12）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 10. struct

普通 struct 是不含托管对象引用的 ValueType：

```rigi
pub struct Vector2 {
    pub var x: float
    pub var y: float

    pub init(_ -> x, _ -> y)

    pub operator plus(another: Vector2): Vector2 {
        return new Vector2(x=(this.x + another.x), y=(this.y + another.y))
    }
}
```

需要让 struct 持有 Object 或内嵌其他 rich struct 时，必须使用 `rich`：

```rigi
pub rich struct Entry {
    pub var owner: User
    pub var metadata: Metadata
}
```

需要让 rich struct 安全进入 shared object graph 时，同时使用 `shared rich`：

```rigi
pub shared rich struct SharedEntry {
    pub var owner: SharedUser
    pub var location: Vector2
}
```

规则：

- struct 是值类型（`ValueType` 子类），复制、参数传递和装箱继续遵守值语义。
- struct 实例方法的 receiver（`this`）按**调用点 place 的引用**处理：方法体内对 `this` 字段的写入原地生效于该 place。这不改变值语义——赋值、参数传递与返回仍是深拷贝；只有以可写 place 为 receiver 的调用原地生效，对临时副本调用时修改随副本丢弃。
- 嵌套字段链写穿：对可写 place 的深层字段写入（如 `r.origin.x = 7`、复合赋值 `r.origin.x += 1`）在语义上等价于正向逐字段读取并物化中间值、写叶、再对值类型中间层反向写回（遇引用类型中间层即停止；需要写回但中间层 const/无 setter 时是编译错误）。值类型 receiver 的方法调用（如 `r.origin.bumpX()`）同理：调用后对可写 place 逐层写回 `this`。
- 非 rich struct 不得直接或间接持有 Object，也不得内嵌 rich struct；其 `refMap` 恒为空。
- rich struct 可以持有 local/shared object 和任意 ValueType。
- shared rich struct 只能持有 shared object、shared rich ValueType 和非 rich ValueType。
- `shared` 不能单独修饰非 rich struct。
- rich/shared 属性属于类型及其布局闭包，泛型实例化和继承后仍必须满足 §3.1.1 的规则。
- **非 rich struct 不得标记 `open` 或 `abstract`**，因此不可能拥有子类型；可被继承的 struct 必须是标记 `open` 的 rich struct。struct 只能继承 struct，不能继承 class，不能实现接口。
- 继承遵守 §3.1.1 的单向传染：rich 基类的子类必须 rich，shared 基类的子类必须 shared；反向可以收紧（非 shared 基类可以有 shared 子类），前提是包括继承字段在内的完整闭包合法。
- `enum struct` 是 struct 的封闭特例：不能标记为 `open`，不能继承用户声明的 struct，也不能被其他类型继承；其固定继承链为 `具体 enum → Enum → ValueType`。
- class 不能继承 struct，struct 不能继承 class。
- 非 rich struct 不能被任何 wrapper 修饰，其字段与实例方法也不能挂载 wrapper（见 §14.9）。
- **值类型布局环拒绝（P18/S2 配套）**：struct/enum struct 的实例字段按值内嵌，布局必须有限。自包含（`struct Box { var next: Box }`）、互包含（A↔B 三方及以上同论），含**经泛型实参代入**形成的环（`struct A { var b: B\<A> }` 且 `struct B\<T> { var x: T }`），一律在声明点报编译错误（诊断给出环路径，如 `A -> B -> A`）。DA 单独堵不死布局无限大（`init(other: Box) { next = other }` 每条路径都赋值但布局仍无限），故布局环是独立于 DA 的结构检查。经引用类型或 `Nullable\<T>`（Object 分支）字段打断的环合法——`rich struct Node { var next: Node? }` 是合法的链表节点（非 rich struct 持 `Node?` 仍被 §3.1.1 闭包表拒绝，两规则正交）。泛型参数类型的字段不展开（`var x: T` 不贡献边）。**泛型发散链深度上限 64**：每次代入都生成新构造、理论上无限加深但不构成环的链，展开深度超过 64 即截断——停止继续下钻，**不报环**（纯防御；合法有限布局不会触达该上限）。

---

## 11. interface

```rigi
pub interface Drawable {
    func draw(canvas: Canvas)

    // 默认实现
    func debugDraw(canvas: Canvas) {
        draw(canvas)
    }
}

pub class Circle : Shape implements Drawable {
    // 必须显式实现或显式委托默认实现
    pub override func draw(canvas: Canvas) { ... }

    // 显式使用接口的默认实现
    override func debugDraw(canvas: Canvas) -> Drawable
}
```

子类必须：
- 显式实现自己的版本，或
- 显式指定使用哪个接口的默认实现：`override func method() -> InterfaceName`

（显式委托语法 `-> InterfaceName` 不支持；带默认实现的接口成员由实现类隐式继承，无体接口成员必须显式实现。）

**默认方法冲突**：实现类的接口闭包（含传递继承）中存在两个或更多**不同接口**各自提供的同签名默认方法时，隐式继承有歧义——类必须显式 `override` 该成员，否则在类声明点报编译错误（bug S3）。真菱形不构成冲突：两条继承路径最终指向**同一个**默认实现（同一符号）时照常隐式继承。接口各自声明同签名默认方法本身合法，冲突只在被同一具体类实现时才判定；`-> InterfaceName` 指定语法不支持。

interface 可标记 `shared`；声明了 `async` 成员的接口必须标记 `shared`，`shared` 接口沿接口继承与 implements 单向传染（见 §3.1.1、§4.5）。

---

## 12. enum struct

`enum struct` 是带有编译器隐藏判别字段的 ValueType。它既可以表达传统固定枚举值，也可以表达通过具名 case 接收运行时参数的枚举值。

```rigi
pub enum struct Direction {
    pub const degrees: i32

    priv init(_ -> degrees)

    pub func opposite(): Direction {
        return switch(this) {
            (_ is .North) -> { .South }
            (_ is .South) -> { .North }
            default -> { this }
        }
    }
}[
    North(0),
    South(180),
    East(90),
    West(270)
]
```

基本规则：

- 固定继承链：`MyEnum` → `Enum` → `ValueType`。`enum struct` 不能标记为 `open`，不能继承用户声明的 struct，也不能被 class/struct/enum 继承。
- 可以有字段、方法和 init，但 init 只供编译器生成的 case 构造入口使用；enum 值不能由用户直接调用 init 创建。
- 枚举 case 在类型声明后的 `[]` 中定义；每个 case 都绑定到一个编译期已解析的 init 调用模板。
- case 名称在同一个 enum 中必须唯一。
- 省略 enum 类型名的 `.CaseName` 必须拥有一个已经确定 enum 静态类型的 receiver/期望类型上下文；编译器不会单凭 case 名反向猜测 enum 类型。
- switch 的 selector 静态类型为 enum struct 时，其分支体内（表达式与语句形态，含嵌套）的 `.CaseName` 以 selector 类型为解析上下文（§7.2）；这只是解析上下文的贡献，分支产值类型仍按既有统一规则推导。
- case 亦可以 `EnumType.Case` 全形引用（参数化 case 为 `EnumType.Case(args)` 调用形态，位置/具名实参规则与省略形式相同）；全形自带类型上下文，不依赖 receiver/期望类型，与 `.Case` 省略形式走同一条 case 构造通道。

```rigi
// 正确：赋值 receiver 已显式指定为 RequestResult
const result: RequestResult = .Success

// 正确：函数参数给出了期望类型
consumeResult(.Success)

// 编译错误：没有任何带类型的 receiver/期望类型
const inferred = .Success
```

### 12.1 固定 case 与参数化 case

case 模板中的普通实参在声明处固定；独占一个实参位置的 `_` 表示调用 case 时必须填入的参数洞。

```rigi
pub enum struct RequestResult {
    pub const errorCode: i32

    pub init(_ -> errorCode) {
        // some code
    }
}[
    Success(-1),
    Failed(errorCode = _)
]
```

由此生成的使用形式为：

```rigi
const success: RequestResult = .Success
const failed: RequestResult = .Failed(404)
const failedNamed: RequestResult = .Failed(errorCode = 404)
const failedFull = RequestResult.Failed(404)   // 全形引用，与上一行同语义
```

- `Success(-1)` 没有参数洞，因此 `.Success` 是固定 case。
- `Failed(errorCode = _)` 有一个 `i32` 参数洞，因此 `.Failed` 是参数化 case。
- `_` 的名称、类型和位置由它对应的 init 参数确定。
- `_` 必须独占一个实参位置；不允许写成 `someExpression(_)`。
- 无论参数取何值，同一个参数化 case 始终只有一个 case 身份。

### 12.2 init 可见性与 enum 构造限制

`enum struct` 的 init 可以使用 `priv`、`protected` 或 `pub`，但其可见性**不产生普通构造能力**：

- 无论 init 是否为 `pub`，源码都不能写 `RequestResult(...)` 直接调用它。
- 写 `new RequestResult(...)`、对保存 `Type\<RequestResult>` 的值使用 `new enumType(...)`，或让泛型 `T()` 在运行时解析到 `RequestResult`，同样是非法构造。
- enum 值始终只能通过 `[]` 中声明的具名 case 入口产生。

init 的 `pub` 含义是允许 case 把 init 的参数暴露为参数洞，从而形成可由调用方使用的参数化 case。绑定到非 `pub` init 的 case 必须是固定 case，不得包含 `_`：

```rigi
pub enum struct TokenKind {
    pub const code: i32

    priv init(_ -> code)
}[
    Identifier(1),       // 正确：固定 case
    Number(2),           // 正确：固定 case
    // Custom(code = _)  // 编译错误：参数化 case 要求目标 init 为 pub
]
```

`enum struct` 不参与用户继承，因此 `protected` 不会扩展出任何“派生 enum case 模板”。所有 case 模板仍只声明在本 enum 紧随类型体的 `[]` 中；绑定到 `priv`/`protected` init 的 case 必须是固定模板，只有绑定到 `pub` init 的 case 才能向调用方暴露参数洞。无论访问级别如何，普通表达式都不能直接调用 enum init。

### 12.3 使用 `is` 匹配 case

`is` 的右侧可以是 enum case：

```rigi
if (result is .Failed) {
    log(result.errorCode)
}
```

这只检查隐藏判别字段，不比较 payload，也不会改变值的静态类型。参数化 case 在 `is` 右侧不带参数；需要比较完整值时使用 `==`，需要附加 payload 条件时显式组合条件。

```rigi
if ((result is .Failed) and (result.errorCode == 404)) {
    ...
}

const message = switch(result) {
    (_ is .Success) -> { "ok" }
    (_ is .Failed) -> { "failed: ${result.errorCode}" }
    default -> { "unknown case" }
}
```

即使源码中已经逐一列出当前声明的全部具名 case，enum 的 `switch` 作为表达式时仍然必须包含 `default`。编译器不以“当前 case 集合看似穷尽”为由删除这一要求；FFI、unsafe/raw memory、反序列化、跨版本 ABI 或其他 corner case 仍可能产生当前源码未声明的判别位模式。

### 12.4 显式判别值与稳定判别 ABI

默认情况下，case 的隐藏判别值由编译器分配，不保证在 case 增删、重排或重新 codegen 后保持不变。需要稳定判别值时，使用 `->`：

```rigi
pub enum struct SteadyABIEnum {}[
    First -> 0,
    Second -> 2,
    Third -> 1
]
```

参数化 case 同样可以指定：

```rigi
pub enum struct StableRequestResult {
    pub const errorCode: i32
    pub init(_ -> errorCode)
}[
    Success(-1) -> 0,
    Failed(errorCode = _) -> 1
]
```

规则：

- `->` 右侧必须是非负、唯一的编译期整数常量。
- 同一个 enum 的 case 要么全部显式指定判别值，要么全部由编译器分配，不能混用。
- 显式值固定 case 的判别身份；声明顺序仍只影响源码、反射和文档顺序。
- `->` 稳定的是判别值，不自动冻结用户字段的布局。增加字段、修改字段类型或使判别字段由 `u16` 扩展到 `u32` 仍属于 ABI 变化。
- 隐藏判别字段及其整数值不作为普通公开字段暴露。

---
