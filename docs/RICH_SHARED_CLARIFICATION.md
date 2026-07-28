# rich 和 shared 的正确理解

**日期**: 2026-07-17  
**重要性**: ⚠️ 关键架构澄清

> **⚠️ 修订注记（2026-07-28，M31）**：本文 §3「shared 可以用于任何类型」
> 与现行 `SYNTAX.md`（§3.1.1、§9.2 修饰符表、§10 规则）矛盾——
> 现行规范为：**shared 仅可用于 class，或与 rich 一起用于 struct**
> （`shared struct` 而没有 `rich` 是编译错误；interface/wrapper 不在
> shared 作用域内）。Parser 已按现行规范实现校验（M31）。
> 本文其余部分（rich 是类型声明修饰符、仅用于 struct/enum struct、
> 使用类型时不写修饰符）仍然有效。**有歧义时以 `SYNTAX.md` 为准。**

## ❌ 错误理解（已修正）

**错误 1**: 认为 `rich` 和 `shared` 是类型引用修饰符，可以在使用类型时添加。

```latte
// ❌ 错误示例（不存在的语法）
var data: rich String = "Hello"
const obj: shared MyClass = getObject()
```

**错误 2**: 认为 `rich` 可以用于任何类型。

```latte
// ❌ 错误示例（rich 不能用于 class）
rich class MyClass { ... }
```

## ✅ 正确理解

### 1. rich 和 shared 是类型声明修饰符

**`rich` 和 `shared` 是类型声明修饰符**，只用于**定义类型**时，不用于使用类型时。

### 2. rich 只能用于值类型（ValueType）

**`rich` 仅适用于值类型**：
- ✅ `struct`
- ✅ `enum struct`

**不能用于引用类型**：
- ❌ `class` - 类本身已经是引用语义
- ❌ `interface`

```latte
// ✅ 正确：rich 用于 struct
rich struct MyStruct {
    x: i32
    y: i32
}

// ✅ 正确：rich 用于 enum struct  
rich enum struct Result {
    Ok(value: i32)
    Err(msg: String)
}

// ❌ 错误：rich 不能用于 class
rich class MyClass { ... }  // 编译错误！
```

### 3. shared 仅可用于 class 或 rich struct（已按 SYNTAX.md 修订）

**`shared` 的作用域（以 SYNTAX.md §3.1.1/§9.2 为准）**：
- ✅ `class`
- ✅ `rich struct`（含 shared rich enum struct）
- ❌ 非 rich 的 `struct` / `enum struct`（`shared struct` 而没有 `rich` 是编译错误）
- ❌ `interface` / `wrapper`

```latte
// ✅ 正确：shared 用于 class
shared class Logger {
    buffer: String
}

// ✅ 正确：shared 与 rich 一起用于 struct
shared rich struct SharedEntry {
    owner: SharedUser
}

// ❌ 错误：shared 不能单独修饰非 rich struct
shared struct Counter { ... }  // 编译错误！
```

（本节旧版「shared 可以用于任何类型」的表述已作废，见文首修订注记。）

### 4. 使用类型时不需要修饰符

```latte
// ✅ 使用时直接写类型名
var obj: MyStruct = new MyStruct()        // 不写 rich MyStruct
var logger: Logger = new Logger()         // 不写 shared Logger
var list: List\<MyStruct> = getList()      // 泛型中也不写 rich
```

## 语义含义

### rich - 引用计数的值类型
- 将**值类型**（struct）升级为引用计数管理
- 允许值类型像引用类型一样共享
- 自动管理生命周期
- **为什么只用于值类型**：引用类型（class）本身已经是引用语义

**示例场景**：
```latte
// 大型结构体，不希望每次传递都复制
rich struct LargeImage {
    width: i32
    height: i32
    pixels: Array\<u8>
}

// 现在可以像引用类型一样传递，避免复制
func processImage(img: LargeImage) { ... }
```

### shared - 共享所有权（线程安全）
- 允许多个所有者
- 线程安全的共享语义
- 可能涉及原子操作
- **可以用于任何类型**

**示例场景**：
```latte
// 多线程共享的日志器
shared class Logger {
    func write(msg: String) {
        // 自动线程安全
    }
}
```

## 架构影响

### TypeReferenceParserLayer
**职责**: 解析类型引用（使用类型时）

**不处理**:
- ❌ `rich` 关键字
- ❌ `shared` 关键字

**处理**:
- ✅ 类型名: `String`, `MyClass`, `MyStruct`
- ✅ 泛型: `List\<T>`
- ✅ 可空: `String?`

### ClassDeclarationParserLayer（待实现）
**职责**: 解析类声明

**应该处理**:
- ✅ `shared` 关键字（可选）
- ❌ `rich` 关键字（不适用于 class）

### StructDeclarationParserLayer（待实现）
**职责**: 解析结构体声明

**应该处理**:
- ✅ `rich` 关键字（可选）
- ✅ `shared` 关键字（可选）

## 类型系统设计表

| 类型 | rich | shared | 说明 |
|------|------|--------|------|
| `class` | ❌ | ✅ | 引用类型，不需要 rich |
| `struct` | ✅ | ✅ | 值类型，可以用 rich 升级 |
| `enum struct` | ✅ | ✅ | 值类型的枚举 |
| `interface` | ❌ | ✅ | 引用类型 |
| `enum` | ❌ | ❌ | 简单枚举不需要 |

## 实际示例

### 正确的定义

```latte
// ✅ rich struct - 引用计数的值类型
rich struct Point3D {
    x: f64
    y: f64
    z: f64
    
    func distance(): f64 { ... }
}

// ✅ shared class - 线程安全的引用类型
shared class ThreadSafeQueue\<T> {
    items: Array\<T>
    
    func enqueue(item: T) { ... }
    func dequeue(): T? { ... }
}

// ✅ rich + shared struct - 既是引用计数又是线程安全
rich shared struct SharedCounter {
    value: i32
    
    func increment() { ... }
}
```

### 错误的定义

```latte
// ❌ rich class - 语法错误！class 不能用 rich
rich class MyClass { ... }

// ❌ rich interface - 语法错误！interface 不能用 rich
rich interface MyInterface { ... }
```

### 使用阶段（始终不带修饰符）

```latte
// ✅ 使用时永远不写 rich/shared
var point: Point3D = Point3D(1.0, 2.0, 3.0)    // 不写 rich
var queue: ThreadSafeQueue\<i32> = ...           // 不写 shared

// ✅ 函数参数
func process(p: Point3D) { ... }                // 不写 rich

// ✅ 泛型类型参数
var points: List\<Point3D> = []                  // 不写 rich

// ✅ 可空类型
var optionalPoint: Point3D? = null              // 不写 rich
```

## 关键要点总结

1. ✅ **rich/shared 只出现在类型定义中**
2. ✅ **使用类型时永远不写 rich/shared**
3. ✅ **rich 只能用于值类型（struct, enum struct）**
4. ✅ **shared 可以用于任何类型**
5. ✅ **TypeReferenceParserLayer 不处理 rich/shared**
6. ✅ **DeclarationParserLayer 才处理 rich/shared**

## 语言设计理由

### 为什么 rich 只用于值类型？

```latte
// 值类型（struct）默认按值传递 - 会复制
struct Point { x: i32, y: i32 }
var p1 = Point(1, 2)
var p2 = p1  // 复制！p1 和 p2 是独立的

// rich struct 使其像引用类型一样 - 不复制
rich struct RichPoint { x: i32, y: i32 }
var rp1 = RichPoint(1, 2)
var rp2 = rp1  // 共享！rp1 和 rp2 指向同一个对象（引用计数）

// 引用类型（class）本身就是引用语义 - 不需要 rich
class MyClass { ... }
var c1 = new MyClass()
var c2 = c1  // 已经是共享！不需要 rich
```

### 为什么 shared 可以用于所有类型？

`shared` 关注的是**线程安全**，与类型是值类型还是引用类型无关：

```latte
// shared 使类型在多线程环境下安全
shared class ThreadSafeLogger { ... }    // 引用类型也需要线程安全
shared struct ThreadSafeCounter { ... }  // 值类型也需要线程安全
```

## 实现检查清单

### 当前状态（P0）
- ✅ TypeReferenceParserLayer - 不处理 rich/shared
- ✅ TypeReferenceASTNode - 不包含修饰符字段

### 待实现（P1/P2）
- ⏳ StructDeclarationParserLayer - 需要处理 rich 和 shared
- ⏳ ClassDeclarationParserLayer - 只处理 shared，拒绝 rich
- ⏳ 编译期验证：检查 rich 只用于值类型

---

**教训**: 
1. 仔细阅读语言规范的每一个细节
2. 理解语言设计背后的原理
3. 不同修饰符有不同的适用范围
