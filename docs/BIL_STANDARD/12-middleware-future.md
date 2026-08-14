# Middleware 边界 / 职责关系 / Legacy 迁移 / 未来扩展（§23–§27）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 23. Middleware 合法 lowering 的边界

Middleware 可以：

- 把 primitive `add` lower 为 LLVM `add`/`fadd`；
- 把用户 `add` lower 为精确 operator body 调用；
- 把 `get.field` lower 为 offset load 或 getter 虚调用；
- 把 `set.field` lower 为 store、setter 和 ARC/GC 屏障；
- 把 `get.array` lower 为 Array 胖槽、Span stride 或用户索引运算；
- 把 `invoke` 的 BIL 参数签名转换为任意合法 Native ABI；
- 把 await/yield lower 为 coroutine state machine；
- 把 try lower 为目标异常机制；
- 消除不改变可观察语义的 copy、Box 和临时变量；
- 进行 inlining、devirtualization、constant folding 和 dead-code elimination。

Middleware 不得：

- 接受类型非法的 BIL 并猜测隐式转换；
- 重新进行 source-level overload ranking；
- 改变 getter/setter/operator/wrapper 的可观察顺序；
- 把内建 bool 短路表达式错误变为两侧都求值；
- 把 async 调用变为惰性启动；
- 绕过 using 清理；
- 允许 enum struct 通过普通 new 构造；
- 根据 Native 表示改变 BIL 类型语义。

---

## 24. 与 SYNTAX.md / RUNTIME.md 的职责关系

### 24.1 以 SYNTAX.md 为准的内容

- 源码语法；
- 普通方法调用的 source-level overload resolution；
- 运算符名称和语言含义；
- getter/setter、wrapper、extension、async、using 的源码规则；
- 类型、字段、方法、运算符、getter/setter 的 canonical symbol 命名；
- wrapper 存储命名约定（`.wrapper.<wrapper 类型全称>`，Middleware ABI，§5.3）；
- 泛型参数命名；
- 可变参数声明形态；
- 入口函数允许的源码签名；
- 访问修饰符和声明合法性。

### 24.2 以 RUNTIME.md 为准的内容

- reified generic 的 runtime typeid 语义；
- fixed/positional/named generic type 信息容器；
- Box、Span、胖引用和 TypeSheet；
- is/supers/with/cast 的运行时含义；
- wrapper 路由与 canonical symbol；
- async、Task、Coroutine、Executor、await、yield；
- rich/shared 与 GC；
- IDisposable 和销毁检查。

### 24.3 本文档独立规定的内容

- BIL 文本结构；
- 严格类型规则；
- BIL hidden argument 的规范签名；
- BIL opcode 与操作数形式；
- block 的结构化执行模型；
- BIL verifier；
- BIL VM 必须实现的抽象语义；
- BIL 与 Middleware 的边界。

若三份文档出现表述差异：

1. 源语言合法性以 `SYNTAX.md` 为准；
2. 运行时可观察行为以 `RUNTIME.md` 为准；
3. frontend 与 Middleware 之间的 IR 编码与验证以本文档为准；
4. 本文档不得重新定义与前两份文档冲突的语言或运行时语义。

---

## 25. Legacy BIL 迁移说明

旧 BIL 文本可按下表迁移：

| Legacy | 标准形式 |
|---|---|
| `.load` | `load` |
| `.invoke` | `invoke` |
| `Type_X` / `F_X` / `M_X` 及 `Type_X::M_X` | 第 5.2 节 canonical symbol；标准 BIL 不保留独立符号别名 |
| `movl` | `shift.left` |
| `movr` | `shift.right` |
| `cmp.bg` | `cmp.gt` |
| `cmp.beq` | `cmp.ge` |
| `getid.type TYPE` | `getid.type type(TYPE) TARGET` |
| `getid.var VAR` | `getid.var VAR TARGET` |
| `getid.field FIELD` | `getid.field field(FIELD) TARGET` |
| `switch TABLE ...` | `switch SELECTOR TABLE ...` |
| 旧 `try` 四 block 形式 | 第 16.7 节 catch-table 形式 |
| `exported` | `pub` |
| `private` | `priv` |
| `atomic[$lock]` block | 删除；非标准 |

兼容解析器可以读取 legacy spelling，但应当在内部规范化，并在重新输出时使用本标准形式。

---

## 26. 未来绝对不允许加入的扩展

- SSA 形式； （过于底层，这是Middleware的职责）
- 任意 CFG 分支； （过于底层，完全和BIL的设计理念背道而驰）
- 原子内存序指令； （应使用现有的block + volatile block修饰符，更底层的语义不允许在这个级别介入，这是Middleware的事情）
- SIMD/vector 类型； （过于底层，完全和BIL的设计理念背道而驰，而且应该使用标准库类型+native函数）
- unsafe pointer；（应使用标准库类型+native函数）
- generator/yield-value； （现有的指令已经足够）
- backend-specific intrinsic； （过于底层，完全和BIL的设计理念背道而驰，即使有极少数情况，也应使用统一的 hint 指令（§18））
- 调试器专用 scope/lifetime 指令； （应使用统一的 hint 指令（§18））
- profile-guided 元数据。 （应使用统一的 hint 指令（§18））

---

## 27. 未来可能加入的扩展

以下能力如果未来加入，必须通过 BIL 版本或 feature flag 明确声明，不得静默改变现有指令含义：

- tail call 语义；

扩展必须同时定义：

- 文本语法；
- 类型规则；
- verifier 规则；
- BIL VM 参考语义；
- 不属于 BIL 的 backend lowering 边界。
