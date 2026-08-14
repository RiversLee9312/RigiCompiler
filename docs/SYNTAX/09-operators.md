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
| `[]` 读取 | `getAtIndex` | `operator getAtIndex\<TElement, TIndex>(index: TIndex): TElement` |
| `[]` 赋值 | `setAtIndex` | `operator setAtIndex\<TElement, TIndex>(index: TIndex, element: TElement)` |

- 索引恰好接收一个实参——多参数索引 `a[i, j]` 是编译错误（签名固定单 `TIndex` 参数）。
- 具名索引实参与普通调用同规则（按形参名归位）。
- `a[i] = x` 映射 `setAtIndex`；复合赋值（`a[i] += x`）按 §13.2 通用规则自动推导。
- **复合赋值的容器与索引表达式只求值一次**：脱糖时先物化为临时变量——`a[i] op= x` 等价于 `{ var __c = a; var __i = i; __c[__i] = (__c[__i] op x) }`，求值序为容器 → 索引 → 右值。

#### 枚举运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `to`（仅 for 头，§7.3） | `EnumerateInRange` | `operator EnumerateInRange(end: T): core.collections.IEnumerable\<T\>` |

- `a to b` 为半开区间 `[a, b)`；`this` 即区间起点（start），`end` 为终点（不含）。
- 返回的 `IEnumerable\<T\>` 随即按 for-each 协议迭代（§7.3）。

#### 调用运算符（callable 协议）

| 运算符/访问形式 | 名称 | 签名 |
|-----------------|------|------|
| 值调用 `expr(args)` | `call` | `operator call(...): TRet` 或 void（省略返回类型） |

- 任何声明了 `operator call` 的类型的值都可以像函数一样被调用（§5.2）；这是通用 callable 协议，不是 lambda 特例。
- `operator call` 可 `abstract`/`override`/`async`（§9.2.1 例外）；async 时调用点结果为 `Task\<TRet\>` / `Task`（§4.5）。

#### 通用规则

- `+=`/`-=`/`*=`/`/=`/`<<=`/`>>=`/`>>>=`/`&=`/`|=`/`^=` 从对应运算符自动推导
- 不可自定义新运算符名称
- **复合赋值的目标表达式只求值一次**：无论目标是字段链还是索引，接收者/容器/索引等含副作用的子表达式均在读取前物化为临时变量——`recv.f op= x` 等价于 `{ var __r = recv; __r.f = (__r.f op x) }`，`a[i] op= x` 等价于 `{ var __c = a; var __i = i; __c[__i] = (__c[__i] op x) }`；求值序为接收者（含容器/索引）→ 右值。局部变量与参数目标天然单次求值，无需物化。
- **赋值的求值顺序不可依赖**：编译器当前按「接收者（含容器/索引）先求值、右值后求值」落地（简单赋值与复合赋值同规则），但使用者不应假设该求值顺序——依赖赋值两侧求值顺序的行为是未定义行为，编译器可在不另行通知的情况下改变求值顺序。

---
