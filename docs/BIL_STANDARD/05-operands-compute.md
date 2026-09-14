# 操作数与求值规则 / 运算指令（§10–§11）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 10. 操作数与求值规则

### 10.1 指令只接受变量与符号表达式

普通指令操作数只能是：

- `$variable`；
- `fn(...)`；
- `field(...)`；
- `type(...)`；
- `case(...)`；
- `blk(...)`；
- `res(...)`；
- 结构位置中的 `none`。

用户字面量不得直接出现。

### 10.2 从左到右求值

当一条指令包含多个可能产生可观察行为的语义输入时，其语义顺序按操作数从左到右确定。

大多数 BIL 操作数已经是局部变量，因此 frontend 应在前置指令中显式完成复杂表达式求值。

### 10.3 结果变量

一条产生结果的指令必须把结果写入显式目标变量。目标变量的声明类型必须严格满足该指令的结果约束。

写入已初始化变量等价于覆盖旧值。ValueType 复制、引用 acquire/release、Box unique 数据复制及销毁由 Middleware 按 `RUNTIME.md` 实现。

---

## 11. 运算指令

### 11.1 统一规则

除比较指令另有规定外，二元运算指令必须满足：

```text
type(OPR1) == type(OPR2)
```

结果类型由以下精确键唯一确定：

```text
opcode + operand type + declared result type
```

验证器必须确认该类型存在唯一精确运算实现，或该组合是内建 intrinsic。

这不是 source-level overload ranking；不存在隐式转换、候选优先级或“最佳匹配”。

### 11.2 算术运算

```bil
add OPR1 OPR2 RESULT
sub OPR1 OPR2 RESULT
mul OPR1 OPR2 RESULT
div OPR1 OPR2 RESULT
mod OPR1 OPR2 RESULT
opposite OPR RESULT
```

对应 Rigi 运算符：

| BIL | Rigi operator |
|---|---|
| `add` | `plus` |
| `sub` | `minus` |
| `mul` | `times` |
| `div` | `div` |
| `mod` | `mod` |
| `opposite` | `opposite` |

对于内建整数/浮点类型，Middleware 可以直接生成 LLVM 算术指令。对于用户类型，Middleware 按精确类型选择唯一运算实现。

内建整数算术（`add`/`sub`/`mul`/`opposite`）溢出行为为**二进制补码回绕**
（two's-complement wraparound）：不产生异常、不饱和、不截断诊断；有符号与
无符号类型同例。VM 参考实现（§22）必须逐字复现该行为。浮点类型遵循
IEEE 754（产生 inf/NaN，不抛异常）。

内建整数 `div` 右操作数为零时抛语言级异常 `core::DividedByZeroException`
（SYNTAX §8.1；可被 `try`/`catch` 捕获，未捕获按未捕获异常终止），有符号与
无符号各宽度同例；浮点 `div` 除零遵循 IEEE 754（产生 inf/NaN），不抛异常。

内建整数 `mod` 为**截断取余**（truncated remainder，与 `div` 同号约定：
结果符号随被除数）；右操作数为零时与整数 `div` 抛同一语言级异常
`core::DividedByZeroException`（同消息，可捕获同例）。浮点 `mod` 为
IEEE 754 截断余数（fmod 语义），模零产生 NaN，不抛异常。

`add` 作用于两个 `.string` 操作数时是**内建字符串拼接**（Rigi `String` 的 `+`）：按值语义产出一个新字符串，VM 内建执行，不属于 `rigi_rt` 原生方法面（RUNTIME §26）。

### 11.3 逻辑运算

```bil
and OPR1 OPR2 RESULT
or OPR1 OPR2 RESULT
not OPR RESULT
```

这些指令表示**两个输入均已求值**的类型驱动逻辑运算。

内建 `.bool` 的 source-level `and` / `or` 具有短路语义，因此 frontend 不得把短路表达式简单生成为一条 `and` / `or`。它必须使用 `if` 与临时变量表达条件求值。

只有以下情况可以直接发出 `and` / `or`：

- 对非短路的用户重载运算；
- frontend 已经证明两侧均应求值的规范化形式。

### 11.4 位运算

```bil
bin.and OPR1 OPR2 RESULT
bin.or OPR1 OPR2 RESULT
bin.xor OPR1 OPR2 RESULT
bin.not OPR RESULT
shift.left VALUE BITS RESULT
shift.right VALUE BITS RESULT
shift.right.unsigned VALUE BITS RESULT
```

标准 BIL 仍要求 `VALUE` 与 `BITS` 的类型严格相同。若源码运算符声明接受不同的 `TBits`，frontend 必须先规范化为满足 BIL 规则的类型，或在无法等价规范化时使用已解析的普通方法调用表示该显式实现。

这些 opcode 的操作数类型在内建标量中仅允许 `.i8`/`.i16`/`.i32`/`.i64`/`.u8`/`.u16`/`.u32`/`.u64`；作用于其他内建标量（`.bool`/`.char`/`.f32`/`.f64`/`.string`）属类型非法，验证器必须按 §23 拒绝（§21.3）。用户类型的位运算经 operator 声明以普通 invoke 表达，不适用本规则。

legacy opcode：

```text
movl → shift.left
movr → shift.right
```

标准生成器不得输出 `movl` / `movr`。

### 11.5 比较运算

```bil
cmp.eq OPR1 OPR2 RESULT_BOOL
cmp.ne OPR1 OPR2 RESULT_BOOL
cmp.lt OPR1 OPR2 RESULT_BOOL
cmp.le OPR1 OPR2 RESULT_BOOL
cmp.gt OPR1 OPR2 RESULT_BOOL
cmp.ge OPR1 OPR2 RESULT_BOOL
```

规则：

- 两个操作数类型必须严格相同；
- 结果变量必须为 `.bool`；
- `cmp.eq` / `cmp.ne` 使用 `equals` 语义；
- 排序比较使用 `compareTo` 与 `core.ComparisonResult` 语义；
- `cmp.ne` 可以由 `cmp.eq` + `not` 实现；
- `<`、`<=`、`>`、`>=` 可以由精确 `compareTo` 结果实现。

legacy opcode：

```text
cmp.bg  → cmp.gt
cmp.beq → cmp.ge
```

---
