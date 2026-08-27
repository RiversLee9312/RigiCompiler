# 运算符（§13）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 13. 运算符

### 13.1 声明

使用 `operator` 关键字替代 `func`，固定的 camelCase 命名：

```rigi
pub operator plus(another: MyType): MyType { ... }
pub operator plus\<TAnother extends Addable>(another: TAnother): MyType { ... }
```

### 13.2 固定运算符映射

#### 算术运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `+` | `plus` | `operator plus\<TAnother, TResult>(another: TAnother): TResult` |
| `-` | `minus` | `operator minus\<TAnother, TResult>(another: TAnother): TResult` |
| `*` | `times` | `operator times\<TAnother, TResult>(another: TAnother): TResult` |
| `/` | `div` | `operator div\<TAnother, TResult>(another: TAnother): TResult` |
| `-a`（一元） | `opposite` | `operator opposite\<TResult>(): TResult` |

#### 逻辑运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `a and b` | `and` | `operator and\<TAnother, TResult>(another: TAnother): TResult` |
| `a or b` | `or` | `operator or\<TAnother, TResult>(another: TAnother): TResult` |
| `not a`（一元） | `not` | `operator not\<TResult>(): TResult` |

逻辑运算使用 `and`/`or`/`not` 关键字，没有 `&&`/`||`。

**短路求值**：仅当 `and`/`or` **未被重载**（即作用于内建 `bool`）时才短路求值；一旦作用于重载了 `and`/`or` 的类型，两侧都会被求值（运算符方法的参数先求值再传入）。因此 `a and b` 是否短路取决于操作数的**静态类型**，编译器会在对可能重载的类型使用 `and`/`or` 时给出警告。

#### 位运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `<<` | `leftShift` | `operator leftShift\<TBits, TResult>(bits: TBits): TResult`（低位补 0） |
| `>>` | `rightShift` | `operator rightShift\<TBits, TResult>(bits: TBits): TResult`（高位补符号位） |
| `>>>` | `unsignedRightShift` | `operator unsignedRightShift\<TBits, TResult>(bits: TBits): TResult`（高位补 0） |
| `&` | `bitwiseAnd` | `operator bitwiseAnd\<TAnother, TResult>(another: TAnother): TResult` |
| `\|` | `bitwiseOr` | `operator bitwiseOr\<TAnother, TResult>(another: TAnother): TResult` |
| `!a`（一元） | `bitwiseNot` | `operator bitwiseNot\<TResult>(): TResult` |
| `^` | `bitwiseXor` | `operator bitwiseXor\<TAnother, TResult>(another: TAnother): TResult` |

内建标量类型中，位运算符（`<<` `>>` `>>>` `&` `|` `^` 与一元 `!`）仅对**整数族** `i8`/`i16`/`i32`/`i64`/`u8`/`u16`/`u32`/`u64` 定义；`bool`、`char`、`f32`、`f64` 未定义位运算（`bool` 的逻辑组合使用 `and`/`or`/`not` 关键字）。用户类型可经 `operator` 声明自定义位运算，按普通运算符派发，不受此限。

#### 比较运算符

相等与排序分离为两个运算符：

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `==`/`!=` | `equals` | `operator equals\<TAnother>(another: TAnother): bool` |
| `<`/`>`/`<=`/`>=` | `compareTo` | `operator compareTo\<TAnother>(another: TAnother): core.ComparisonResult` |

- `!=` 由 `equals` 取反自动推导。
- `<`/`>`/`<=`/`>=` 由 `compareTo` 的结果推导。
- `core.ComparisonResult` 枚举值：`.Equal`、`.GreaterThanAnother`、`.LesserThanAnother`。
- 两者返回类型固定，因此不需要 `TResult`。仅需相等语义的类型只实现 `equals` 即可，无需具备全序。

#### 索引运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `[]` 读取 | `getAtIndex` | `operator getAtIndex\<TElement, TIndex>(index: TIndex): TElement?` |
| `[]` 赋值 | `setAtIndex` | `operator setAtIndex\<TElement, TIndex>(index: TIndex, element: TElement)` |

- **索引读取一律返回 `TElement?`（`Nullable\<TElement\>`）**。读取元素是可失败的
  操作（越界、稀疏容器缺键等），调用方必须用空安全机制解包——`if?` 空值回退、
  `?.` 安全访问、null 判等 smart cast 或显式 `as`（失败抛 `core.CastException`）：

  ```rigi
  var first = arr[0] if? -1        // 越界/空 → 回退 -1
  const e = arr[0]
  if (e != null) { use(e) }        // smart cast 收窄为 TElement
  var name = users[0]?.name        // 安全访问成员
  ```

- `getAtIndex` 的声明形状由编译器在声明处校验：恰好 1 个形参（类型由实现自定），
  返回类型必须是 `T?` 构造——返回非可空类型的 `getAtIndex` 是编译错误（死声明，
  任何读取点都无法按空安全语义消费）。
- **内建 `Array\<T\>` 的 `a[i]` 读取在语义上同样走 `getAtIndex` 运算符**（返回
  `T?`），用户自定义索引容器与内建数组遵守同一套规则；差别只在实现——编译器
  清楚内建数组的底细，直接 emit 对应的 BIL 特权指令（`BIL_STANDARD.md` §13.6
  `get.array`），不经过普通方法调用。
- **越界读取语义**：内建数组越界读取不 trap，按「读取失败」得 `null`（这正是
  返回 `T?` 的意义——`arr[99] if? -1` 得 `-1`）。用户容器的「失败」语义由各自
  `getAtIndex` 实现自定（返回 `null` 或抛异常均可）。
- **索引写（`a[i] = v`）不在可空化范围**：`setAtIndex` 的 `element` 形参按声明
  类型接收（内建数组仍收非空 `T`）；内建数组/Span 越界**写入**抛可捕获的
  `core.OutOfBoundException`（§8.1；写入没有「返回 null」的退路，静默丢弃写入
  会掩盖 bug）。
- 读出的 `T?` 不提供隐式成员访问与写入：`a[i].f`、`a[i].f = x`、`a[i][j]` 都是
  编译错误（nullable 上无成员/非可写 place），须先解包（`a[i]?.f`、`const e =
  a[i]; if (e != null) { e.f = x }`）。
- 复合赋值（`a[i] += x`）按 §13.2 通用规则从读+写推导；由于读侧类型是 `T?`，
  `T?` 上没有算术/位运算符，`a[i] op= x` 不再可用——写显式读改写形态
  `a[i] = ((a[i] if? 0) + x)`。
- **for-in 循环不经 `getAtIndex`**：`for (x in c)` 走 `core.collections.IEnumerable\<T\>`
  协议（`iterate`/`moveNext`/`current`，§7.3），循环变量类型保持 `T`，不受本
  规则影响。
- 索引恰好接收一个实参——多参数索引 `a[i, j]` 是编译错误（签名固定单 `TIndex` 参数）。
- 具名索引实参与普通调用同规则（按形参名归位）。
- **复合赋值的目标表达式只求值一次**（适用于仍合法的字段链等形态）：脱糖时先物化
  为临时变量——求值序为容器/接收者 → 索引 → 右值。

#### 枚举运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `to`（仅 for 头，§7.3） | `EnumerateInRange` | `operator EnumerateInRange(end: TEnd): core.collections.IEnumerable\<T>`（恰好 1 个形参，类型由实现自定） |

- `for (i in a to b)` **始终按左操作数 `a` 的类型派发**其实例
  `operator EnumerateInRange`，等价于 `for (i in a.EnumerateInRange(b))`。
  **区间开闭、步长、`a >= b` 的行为由该 operator 的实现自定义，不是
  语言的通用语义。**
- 右操作数 `b` 按 §4.2 重载解析绑定到该 operator 的唯一形参（含隐式
  推断与泛型约束），**不必与 `a` 同型**；形参类型由实现自定。返回类型
  必须是 `core.collections.IEnumerable\<T>` 构造；循环变量类型为 `T`
  （`T` 由返回的可枚举元素类型决定，不必等于 `Self` 或形参类型）。
- 内建整数系列类型的 SDK 实现是半开区间 `[a, b)`、步长 +1、`a >= b`
  零次迭代（§15.3）。
- 返回的 `IEnumerable\<T>` 随即按 for-each 协议迭代（§7.3）。

#### 调用运算符（callable 协议）

| 运算符/访问形式 | 名称 | 签名 |
|-----------------|------|------|
| 值调用 `expr(args)` | `call` | `operator call(...): TRet` 或 void（省略返回类型） |

- 任何声明了 `operator call` 的类型的值都可以像函数一样被调用（§5.2）；这是通用 callable 协议，不是 lambda 特例。
- `operator call` 可 `abstract`/`override`/`async`（§9.2.1 例外）；async 时调用点结果为 `Task\<TRet>` / `Task`（§4.5）。

#### 通用规则

- `+=`/`-=`/`*=`/`/=`/`<<=`/`>>=`/`>>>=`/`&=`/`|=`/`^=` 从对应运算符自动推导（位运算复合赋值与位运算符同限：内建标量仅整数族）
- 不可自定义新运算符名称
- **复合赋值的目标表达式只求值一次**：无论目标是字段链还是索引，接收者/容器/索引等含副作用的子表达式均在读取前物化为临时变量——`recv.f op= x` 等价于 `{ var __r = recv; __r.f = (__r.f op x) }`，`a[i] op= x` 等价于 `{ var __c = a; var __i = i; __c[__i] = (__c[__i] op x) }`；求值序为接收者（含容器/索引）→ 右值。局部变量与参数目标天然单次求值，无需物化。字段链中间层含值类型时，叶写后对值类型中间层反向写回（§10 嵌套字段链写穿）。
- **赋值的求值顺序不可依赖**：编译器当前按「接收者（含容器/索引）先求值、右值后求值」落地（简单赋值与复合赋值同规则），但使用者不应假设该求值顺序——依赖赋值两侧求值顺序的行为是未定义行为，编译器可在不另行通知的情况下改变求值顺序。

### 13.3 泛型参数操作数

泛型参数 `T` 上的成员解析（普通方法、operator 名字调用、运算符位置、字段、索引）走**有效成员类型**，不因 `T` 不是具体 `TypeSymbol` 而一律拒绝：

- `T extends B`：按 `B` 解析成员。`B` 的 `BaseType` 链与接口闭包一并可见（与「`B` 类型变量调成员」同一口径）。构造界（如 `T extends IEnumerable\<i32>`）按已代入的构造类型查找；界含外层宿主泛型参数时保留参数身份（`U extends SomeBound\<T>` 内 `U` 的成员按 `SomeBound\<T>` 解析）。
- `T` 无约束，或只有 `supers` / `with`：按 `Any` 解析（stdlib 承诺成员，如 `toString`）。`supers` 是下界，不提供成员保证；`with` 只提供 wrapper place（`param:W`），不提供普通成员。在运算符位置对仅有 `supers`/`with` 的 `T` 使用未承诺运算符时，诊断会标明该约束不提供成员。

**结果定型**按约束签名（宿主代入后）给出，不是 `T` 本身：`T extends Addable` 且 `Addable.plus` 返回 `Addable` 时，`a + b` 与 `a.plus(b)` 的类型都是 `Addable`。把它赋回 `: T` 是编译错误；赋给 `: Addable` 合法。`F-bounded`（`T extends Addable\<T>`）仍非法，见 §3.6。

**运行时**是「静态签名 + 动态实现」：编译期按约束解析签名；运算符位置仍发 intrinsic 指令，VM 按操作数实际 typeid 沿派生链派发到最具体实现；方法调用复用接口/虚调用既有发射路径（`T` receiver 的 BIL invoke 目标与 `B` 类型变量调用同一成员符号）。左操作数决定派发（`T` 作右操作数时查左操作数类型上的 operator）。

```rigi
func add\<T extends Addable>(a: T, b: T): Addable {
    return a + b          // 类型 Addable，不是 T
}

func show\<T>(x: T): String {
    return x.toString()   // 无约束 T 经 Any 承诺
}
```

---
