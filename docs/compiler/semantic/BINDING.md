# 函数体绑定与诊断

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](SEMANTIC_ARCHITECTURE.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 5. BoundTree（P3 产物）

### 5.1 节点设计

- 基类 `BoundNode`，回指字段 `Syntax: ASTNode`（必填；合成节点指向
  最近的语法来源）。表达式基类 `BoundExpression` 额外携带
  `Type: TypeSymbol`（分析定型后的严格类型）。
- 节点按语义命名（`BoundCall`、`BoundFieldAccess`、`BoundConversion`、
  `BoundBlock`……），子类集合以 P3 的分析需要为准，不与 AST 节点
  一一对应——例如 AST 的 `MemberAccess` 在 P3 分化为
  字段访问 / getter 访问 / 方法组等不同 bound 形态。
- **AST 只读**：P3 不修改 AST 任何字段、不重挂 Parent。
  `ExpressionRootASTNode` 是透明容器，BoundTree 不为它建节点。
- 分析结果以**函数体**为单位：`BoundFunctionBody { MethodSymbol,
  Locals, BoundBlock }`。声明侧初始化器同样产出 BoundFunctionBody
  （归属编译器合成的初始化方法符号）。

### 5.2 P3 必须落实的语义规则（易漏清单）

- **值块隐式取值**：if/switch 表达式分支体、表达式形态 `seq`、以及
  部分求值位置的代码块，统一规则——「块内恰好一条非赋值
  ExpressionStatement」即隐式取值（如 `var x: i32 = seq { 7 }`、
  `var e: E = seq { .A(3) }`）；多语句块须有 `return@_` /
  `return@标签`。判定在 P3 完成并显式记录在 bound 节点上
  （P4 不再看语法形态）。
- **无运算符优先级**是前端已保证的事实：`BinaryExpression` 树无需
  也不得再平衡。
- smart cast：P3 只做**分析与标记**（某表达式在某区域内可视为窄化类型），
  显式 `cast` 指令由 P4a 物化。
- definite assignment 与「所有路径显式返回」在 P3 报错（BIL §21.4
  的对应义务在这里兑现，而不是等 BIL verifier 兜底）。
- wrapper proxy 声明体**模板态绑定**与 pass 归属（烘焙归 Middleware，
  BIL §23 边界）：
  - **P1**：符号壳（proxy 成员与普通成员同路径收集）。
  - **P2**：只保留形状校验（`ProxyShapeChecker`：元数/类别矩阵/
    canonical shape）与 wrapper 应用登记（`AppliedWrappers` /
    `WrapperApplication`，TTarget 代入显形、宿主构造安装用）。
    **不再**合成隐藏字段、派发链、特化符号或降级链——Freeze 前
    零合成符号。
  - **P3**：
    - **proxy 声明体模板态绑定**——绑定语境 = proxy 声明符号自身：
      wrapper 级泛型参数（`TTarget`）与 proxy 方法级泛型参数
      （`TReturn`/`TField` 等）按既有泛型参数路径直接解析
      （#27⑧ 随之闭环）；`self` 类型 = `TTarget` 泛型参数
      （Entity 恰一时；零个则引用 `self` 报错——SYNTAX §14.2）；
      `this` = wrapper 实例自身（模板 fn 的 `.this`，绑定宿主即
      wrapper 类型，不再重写为 `BoundWrapperAccessExpression`）；
      `inner(...)` 绑定为占位调用节点（实参正常绑定；期望形状 =
      specific 按 proxy 声明自身签名 / wildcard 按 canonical 形状
      去 symbol；返回类型 = proxy 声明返回类型）。**#27⑦ 包透传**：
      Bound 节点显式携带当前 proxy 方法声明序中的可变泛型包列表
      （`IsVariadic`/`IsNamedVariadic`；固定泛型不入列），不可仅在
      emitter 临时扫声明隐式推断。非 proxy 语境出现 `self`/`inner`
      是编译错误；诊断按 (proxy, span, message) 去重。
    - **使用点 wrapper place 绑定**原样保留：双源同池查找
      + 只读禁令全拦截面。
    - **局部/静态 wrapper 应用触发 cell 子类合成**（统一
      cell 存储，P3 合成例外）：`CellClassFactory` 逐变量合成
      隐藏子类 `..cell..稳定摘要`（P2 仍 Freeze 前零合成；合成方法体
      经 `BindEnvironment.SyntheticCellBodies` 汇入 BindingDriver
      函数体列表走统一 P4 管线；静态/全局字段 cell 化在
      BindingDriver 阶段 1.6）。wrapper 应用标记挂在子类
      `value` 字段上（同 `WrapperApplication` 实例）。
    - **未声明方法降级判定**保留：candidates 空 + wrapper 链含
      `.proxy.*` → 产物改 `invoke core::Any$call???`（bootstrap
      声明 + VM hook）；`IsDowngradeCallResult`
      五位置豁免保留，判定改为引用相等 bootstrap `Any.call???`。
  - **P4**：发射 wrapper 类型的 proxy 成员为带
    `wrapper-proxy(specific|wildcard)` 修饰符的**模板 fn**
    （`inner` → `invoke fn(..inner)`、`self` → `get.self` 占位指令；
    `invoke fn(..inner)` 操作数 = 可变泛型包 `.generic.<Pack>` 前置 +
    显式值实参，BIL §15.4 / §7.2）；使用点 place 成员访问降级（读 =
    值拷贝 + 普通指令；写 = set.wrapper.field；局部/静态走 cell
    根分派）与声明段平铺仍归 P4（操作数见 §6.1）；cell 子类声明
    按方法体宿主归属收集发射（同 lambda 口径）。
  - **一切烘焙**（特化 / inner 链接 / 原始体替换 / 隐藏存储 /
    `call???` 类别路由体 / 包解包 shim / **静态 cell 构造时机**）
    **归 Middleware**（BIL §23 边界）。frontend 产物只携带标记：
    应用登记、proxy 模板 fn（含 `invoke fn(..inner)` 包透传操作数）、
    降级调用点对 `core::Any$call???` 的 `invoke`、cell 子类声明
    （含 `value` 字段 `wrapped(W)`）。

---

## 8. 诊断模型（P1–P4 通用）

前端的 `LexerException` / `ParserException`（单发即死）**保持不变**；
中端新开可恢复诊断：

- `Diagnostic { Severity, Span?, Message, Phase }`；`Severity` 至少
  `Error` / `Warning`。暂不建错误码编号体系（简洁三问），测试按
  消息子串断言（与现有 `CheckParseError` 惯例一致）。
- `DiagnosticBag`：全编译单元一个实例贯穿 P1–P4，各 pass 只追加。
  函数体之间、声明之间的错误互不阻断——一次编译报出尽可能多的错误。
- 阶段推进门槛：任一 pass 结束时存在 `Error` 即停止推进到 P4
  （P1→P2→P3 之间尽量继续，以最大化单次报错量；无法继续的连锁错误
  用「毒化」符号/类型抑制次生噪音——例如解析失败的类型引用绑定为
  `ErrorTypeSymbol`，后续用到它的检查静默通过）。
- `CompilerInternalException` 语义不变：编译器自身 bug，永不用于
  用户源码错误。
- CLI 出口：诊断经 `Core/Logger` 走 stderr，格式含
  `文件:行:列` 与 Phase 标记；stdout 数据流（`--emit-bil` 等）不受污染。

---
