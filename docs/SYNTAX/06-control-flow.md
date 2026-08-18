# 控制流与异常处理（§7–§8）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 7. 控制流

### 7.1 条件

```rigi
// if 语句
if (condition) {
    ...
}

// if-else 表达式（作为表达式时必须有 else）
var result = if (x > 0) { x } else { opposite(x) }

// if 表达式：多语句分支体，用 return@ 显式产出分支值
var result = if (x > 0) {
    logPositive(x)
    return@_ x                  // 匿名分支体的默认标签是 _
} else {
    return@_ opposite(x)
}

// 用 named 给 if 表达式起名后，return@标签 可穿透内层匿名块精确指定返回目标
var result = if (x > 0) named check {
    seq {
        return@check x          // 从最内层 seq 直接跳出到 check
    }
} else {
    return@check opposite(x)
}
```

if 表达式规则：
- 必须有 `else` 分支
- 分支体为单表达式时，该表达式即分支值（隐式取值，无需 `return@`）
- 分支体含多条语句时，所有执行路径都必须显式 `return@_`（匿名）或 `return@标签`（`if (cond) named 标签` 命名后）产出值——规则同 §6.1；落到块尾而没有 `return@` 是编译错误
- 分支体是值块：其内（含嵌套语句块）一切裸 `return` 均为编译错误（裸 `return` 不得穿透值块边界，见 §6.1）

### 7.2 switch

switch 有两种形态：表达式形态与语句形态。两者共用同一套匹配规则，区别只在出现位置与分支体：

```rigi
// 表达式形态：switch 出现在表达式位置，产出值
var result = switch(expr) {
    (1) -> { "one" }
    (2) -> { "two" }
    (_ > 10) -> { "big" }           // pattern match：使用 _ 引用 expr 的值
    (_ == (3 + 4)) -> { "seven" }   // pattern match
    default -> { "other" }
}

// 语句形态：switch 出现在语句位置，结果值被丢弃
switch(expr) {
    (1) -> { handleOne() }
    (_ > 10) -> {
        logBig(expr)
        handleBig()
    }
    default -> { handleOther() }
}

// 表达式形态：多语句分支体用 return@ 显式产出分支值；named 命名后可用 return@标签
var result = switch(expr) named match {
    (1) -> { return@match "one" }
    (_ > 10) -> {
        logBig(expr)
        return@match "big"
    }
    default -> { return@match "other" }
}
```

规则：
- 不含 `_` 的分支为值匹配（value match），要求为编译期常量
- 含 `_` 的分支为模式匹配（pattern match），`_` 代表被检查的表达式的值，最终结果必须为 `bool`
- 两种形态都必须有 `default` 分支
- selector 的静态类型为 enum struct 时，分支体内（表达式与语句形态，含嵌套）的 `.Case` 省略形式以 selector 类型为解析上下文（§12）；这只是解析上下文的贡献，分支结果类型仍按既有统一规则推导，分支产出与 selector 异质的用法不受影响
- 语句形态的分支体是完整代码块，可写多条语句
- 表达式形态的分支体取值规则同 if 表达式（§7.1）：单表达式分支隐式取值；多语句分支体必须显式 `return@_`（匿名）或 `return@标签`（`switch (expr) named 标签` 命名后）产出值，落到块尾而没有 `return@` 是编译错误；分支体是值块，其内一切裸 `return` 均为编译错误（见 §6.1）

### 7.3 循环

```rigi
// for-each
for (item in collection) {
    ...
}

// 范围循环
for (i in 0 to 10) {
    ...
}

// while
while (condition) {
    ...
}

// do-while
do {
    ...
} while (condition)
```

大括号不可省略。

循环语义：

- **for-each**：`for (item in collection)` 要求 `collection` 的类型实现
  `core.collections.IEnumerable\<T\>`；循环变量 `item` 的类型为 `T`。
  循环等价于：先调用 `iterate()` 取得 `IEnumerator\<T\>`；每轮迭代先调用
  `moveNext()`，返回 `false` 时结束循环，否则以 `current()` 的值作为本轮
  的 `item` 执行循环体。`IEnumerable\<T\>` / `IEnumerator\<T\>` 是双接口
  （可重入，每次 `iterate()` 产生独立枚举器），属标准库 `core.collections`。
- **范围循环**：`for (i in a to b)` 为**半开区间 `[a, b)`**，步长恒 +1；
  `a >= b` 时零次迭代。它就是对枚举运算符结果的 for-each：
  等价于 `for (i in a.EnumerateInRange(b))`（见 §13.2 枚举运算符）。
  `to` 是 for 头专用语法，不是通用表达式。
- 基元数值类型的 `EnumerateInRange` 实现由 SDK 自举源提供（见 §15.3）。
- 循环变量是只读的（`const`）：循环体内不可对其赋值或复合赋值；
  每轮迭代是一个新的绑定。被 lambda 捕获时每迭代构造新 cell，各 lambda
  见当迭代的值（与 §5.2 捕获语义一致）。

### 7.4 带标签的循环

使用 `named` 关键字声明标签：

```rigi
for (i in 0 to 10) named outer {
    for (j in 0 to 10) named inner {
        if (someCondition) {
            break@outer
        }
        continue@inner
    }
}

while (true) named loop {
    ...
    break@loop
}

do named loop {
    ...
} while (condition)
```

### 7.5 `await` 与 `yield`

`await` 是一元运算符，用于等待 `core.coroutine.Task` 或 `core.coroutine.Task\<TResult>`：

```rigi
await flushLogs()
const user = await loadUser(42)
const result = await aTaskExpression
```

- `await Task\<TResult>` 在任务成功完成后产生 `TResult`。
- `await Task` 只等待任务完成，不产生值。
- 任务失败时，`await` 在当前位置重新抛出任务保存的异常。
- 任务尚未完成时，当前协程挂起并释放 Worker；任务结束后，当前协程重新进入其原本所属 Executor 的待执行协程池。
- 任务已经结束时，`await` 可以直接取得终态，不要求发生实际挂起。

`yield` 是只能单独出现的语句，不能作为表达式、参数或返回值使用。它有三种形式：

```rigi
yield                         // 主动结束当前执行段，重新参与调度
yield pollingAlarm            // 等待 PollingAlarm 就绪
yield eventAlarm              // 等待 EventAlarm 通知
yield sleep(1000)             // 基于 EventAlarm 的非阻塞睡眠
```

裸 `yield` 使当前协程从 Running 回到 Runnable，并重新经过一次所属 Executor 的调度决策；它不保证一定切换到另一个协程，也不保证 FIFO 顺序。

`core.coroutine.PollingAlarm` 提供：

```rigi
pub func isReady(): bool
```

执行 `yield pollingAlarm` 后，当前协程挂起。每当 Executor 再次给该等待任务一次调度机会时，运行时调用一次 `isReady()`：返回 `false` 时继续等待，返回 `true` 时才恢复 `yield` 后的代码。`isReady()` 必须同步、线程安全且不得阻塞或挂起。

`core.coroutine.EventAlarm` 由事件源通过 callback 通知 Executor。执行 `yield eventAlarm` 后，协程保持挂起，直到 EventAlarm 发出通知；callback 只负责把协程重新发布到原 Executor，不直接执行用户代码。

标准睡眠函数为：

```rigi
core.coroutine.sleep(milliseconds: i32): core.coroutine.EventAlarm
```

它返回一个内部 EventAlarm 子类，事件源为系统时钟树。因此 `yield sleep(1000)` 不会阻塞 Worker 线程。

任何形式的 `yield` 都会结束当前 run-to-suspension 执行段；即使给出的 Alarm 已经就绪，恢复也要重新经过 Executor 调度。

---

## 8. 异常处理

```rigi
try {
    riskyOperation()
} catch (e: IOException) {
    handleIO(e)
} catch (_: RuntimeException) {
    // 丢弃异常变量
} finally(e) {
    // e 为仍在向外传播的异常（无匹配 catch 捕获、异常穿出本 try）；
    // 被本 try 的 catch 捕获的异常不会出现在 e 中（此时 e 为 null），无异常时亦为 null
    cleanup()
}
```

- `catch` 子句的异常变量与 `finally(e)` 的 `e` 均为只读（`const`）——子句/块体内不可对其赋值或复合赋值。
- 被 lambda 捕获时，进入 catch / finally 块时构造一个 cell（见 §5.2）；`using` 资源变量同理（进入 using 作用域时构造）。

### 8.1 异常类型层级

异常根 `core.Exception` 是语言级内建类型（进编译器 bootstrap，与 `Object`/`ValueType` 同列），open 可继承：

```rigi
pub open class Exception { ... }   // 概念形态；实际声明在编译器 bootstrap，不在 stdlib 源
```

异常根携带：

- `protected var message: String` 字段——异常的人类可读描述；
- `pub func getMessage(): String` 方法——message 的唯一公共读取通道（abstract，由各具体异常子类 override 实现；`toString` 不覆写，插值/打印仍走 `Object` 的默认实现）。

`throw` 操作数类型与 `catch` 子句类型必须是 `core.Exception` 或其子类（§3.1 层级兼容判定）。标准库在 `stdlib/core/exceptions.rg` 提供五个具体子类（均可继承，用户自定义异常以同样的 `: core.Exception` 声明）：

| 类型 | 含义 |
|------|------|
| `core.RuntimeException` | 通用运行时异常基类 |
| `core.IOException` | I/O 相关异常 |
| `core.CastException` | `as`/`as?`/nullable 展开等类型转换失败（BIL §12.1） |
| `core.NoSuchMethodException` | 运行期 init 重载解析失败与 wrapper 派发失败（§10/§14.6） |
| `core.DividedByZeroException` | 整数除法除零（BIL §11.2；float/double 除零按 IEEE 754 产 inf/NaN，不抛） |

每个子类**自持**显式 init（异常根不写 init；需要时 init 体可选调用 `super(...)`，字段也可直接赋值继承字段）：

```rigi
pub open class IOException : core.Exception {
    pub init(text: String) { message = text }
}
```

`getMessage()` 返回 message 的当前值；未显式赋值时为 String 零值（空字符串）。`core.GlobalExceptionHandler` 与运行时内部类型（`GCAlarm` 等）不在 stdlib 声明，随 BIL VM 定稿。

---
