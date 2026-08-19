# seq 块（§6）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 6. `seq` 块（语句块）

`seq` 块等同于 C 的 `{}`，用于创建作用域、限制变量生命周期，不是 lambda。裸 `return`（不带 `@标签`）在 `seq` 块内仍然透传给外层函数，直接参与外层函数的控制流，语义与普通 `{}` 一致：

```rigi
pub func process(): i32 {
    seq {
        var temp = computeSomething()
        if (temp > 100) {
            return temp   // 跳出 process，而不是跳出 seq
        }
    }
    // temp 在此处不可见
    return 0
}

// 带修饰符
volatile seq {
    // 此块中的操作标记为 volatile
}
```

### 6.1 作为表达式：返回值与 `named`

`seq` 的一个重要用途是缓解"没有运算符优先级"（见 §1.3）带来的书写负担——用 `seq` 可以近乎零成本地拆出大量命名中间变量，避免把一条表达式硬塞进一堆括号里。为此 `seq` 支持：

- **返回值**：`seq { ... }` 可以整体作为表达式使用。体恰好一条非赋值表达式语句时，该表达式即隐式值；多条语句时通过 `return@` 从块内部产生结果值。
- **命名**：同 §7.4 的循环标签，用 `named` 给 `seq` 块起名，配合 `return@名字` 精确指定从哪一层 `seq` 返回。
- **默认标签**：未显式 `named` 的值块（`seq` 块、if/switch 表达式分支体），其隐式默认标签就是 `_`——`return@_ value` 表示"从最内层这个匿名值块返回 `value`"。一旦值块被 `named` 命名，`_` 默认标签在该块内不再可用，块内须以 `return@名字` 产出值。

```rigi
const result = seq {
    const ac = a * c
    const discriminant = (b * b) - (4.0 * ac)
    const delta = sqrt(discriminant)
    const numerator = (-b) - delta
    const denominator = 2.0 * a

    return@_ numerator / denominator  // 未命名时，默认标签就是 _，指最内层这个匿名 seq
}

var x: i32 = seq { 7 }                 // 单表达式：隐式值，无需 return@
var e: E = seq { .A(3) }               // 同上；期望类型回填 enum case / null 等语境
```

规则：

- `return@名字` / `return@_` 与裸 `return` 是两回事：前者结束对应的值块并把值作为该块表达式的结果；后者始终结束外层函数。
- **裸 `return` 不得穿透值块边界**：值块（值语境 `seq` 块、if/switch 表达式分支体，含其内部任意嵌套语句块）内的一切裸 `return` 均为编译错误——裸 `return` 语义恒为结束外层函数，其穿透路径必然经过值块边界。要离开函数请把值块改为语句用法（语句位置的 `seq`/if/switch 不受此限），或在值块内用 `return@标签` 产出值后由外层决定（lambda 体内的裸 `return` 另有独立禁令，见 §5.1）。
- 当 `seq` 被当作表达式使用时，取值规则与通用值块（if/switch 表达式分支体）一致：body 恰好一条非赋值表达式语句时，该表达式即隐式值（无需 `return@`）；body 含多条语句时，产值路径必须显式 `return@_ value` / `return@名字 value`，落到块尾而没有 `return@` 是编译错误。多语句例须写 `return@_ a + b`，最后一条表达式不会自动成为块值。这与 §4.1 函数体不支持隐式返回是两回事。
- 仅作语句使用（不取值）的 `seq` 不受此限制，可以像原来一样不写任何 `return@`。
- `return@值块标签` 允许穿透中间循环命中外层值块（语义为结束循环并产出该值块的值）；但"所有执行路径都必须产值"的判定对循环保守：`while`/`for` 可零次执行，其循环体内的 `return@` **不构成**产值保证——即使条件字面量为 `true` 也不做恒真识别，故 `seq { while (true) { return@_ 1 } }` 仍是编译错误，须在循环之后另给产值路径；`do-while` 至少执行一次，其循环体每条路径都产值时构成保证。
- `return@` 的目标也可以是语句位置（不取值）的 `seq` 块：此时 `return@标签` 必须**不携带值**，语义为提前结束该 `seq` 块、继续执行块后的语句（区别于裸 `return`——后者穿透并结束外层函数）。仅**显式 `named`** 的语句 `seq` 可作目标（`_` 默认标签是值块专属）；穿透中间循环或值块命中外层语句 `seq` 为编译错误。

### 6.2 `using` 资源绑定

Rigi 不提供 finalizer。需要确定性释放外部资源的类型实现 `core.IDisposable`：

```rigi
pub interface IDisposable {
    func dispose()
}
```

`using` 是 `seq` 的资源绑定子句。可以在 `seq` 与可选的 `named` 之间放置一个或多个 `using(...)`；每个 `using` 内必须是一条单行的 `const` 或 `var` 声明并完成初始化，其结果类型必须实现 `core.IDisposable`。

```rigi
seq using(const file = new File("./mydoc"))
using(const stream = new FileInputStream(file))
using(var reader = new StreamReader(stream))
named readFile {
    // Some Logic
}
```

也可以省略 `named`：

```rigi
seq using(const resource = openResource()) {
    use(resource)
}
```

语义规则：

- 各个 `using` 绑定按源码顺序从左到右初始化；后一个初始化器可以引用前面已经建立的绑定。
- 离开该 `seq` 时，编译器按声明的逆序调用 `dispose()`；上例的顺序为 `reader` → `stream` → `file`。
- 正常落到块尾、`return@_`/`return@名字`、穿透外层函数的裸 `return`、异常传播以及其他离开该块的控制流都必须执行清理。
- 若某个初始化器抛出异常，只清理此前已经成功初始化的资源。
- `await` 或 `yield` 只挂起 Coroutine，并不离开 `seq`；资源继续保存在该 Coroutine 的执行状态中，直到最终退出作用域。
- `dispose()` 是普通函数，因此可以执行 `await` 或 `yield`。若逆序清理中的某次 `dispose()` 挂起，清理栈、当前资源和尚未清理的资源继续保存在 Coroutine frame 中；恢复后从同一清理进度继续。
- 外层函数的正常 `return`、异常传播或其他终止流程，只有在所有已建立的 `using` 清理完成后才真正继续完成；可挂起的清理不会被跳过。

如果一个实现 `core.IDisposable` 的对象在销毁前从未调用 `dispose()`，无论销毁来自编译器生成的 ARC 路径还是 macroGC，运行时都会立即触发只能由 `core.GlobalExceptionHandler` 提供的机制接收的全局异常。GC 绝不代替用户隐式调用 `dispose()`；普通 `try/catch` 不能拦截该异常。详见 `RUNTIME.md`。

---
