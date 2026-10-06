# 中端代码组织与验证

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](SEMANTIC_ARCHITECTURE.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 9. 代码组织

中端与 BIL 的三个顶层目录（与既有 `AST/` `Parser/` `Lexer/` 并列）：

```text
Semantic/                  # P1–P3 + 符号图 + 诊断
├── Diagnostics.cs            # Diagnostic / DiagnosticBag / Severity
├── Symbols/                  # SemanticSymbol 家族、驻留 cache、
│                             #   BootstrapSymbols、CanonicalSymbolPrinter
├── DeclarationCollector.cs   # P1
├── DeclarationResolver.cs    # P2
├── Binder.cs                 # P3 瘦入口
├── Binding/                  # P3 visitor 化基建：
│   ├── BinderVisitor.cs         # CRTP 三基类（通用/ExpressionVisitor
│   │                            #   追加 expectedType/BinderShellVisitor 壳填充）
│   ├── BindEnvironment.cs       # 共享只读输入 + job 局部默认值/合成 delta（unit/declarations/NameResolver/诊断）
│   ├── BindContext.cs           # 函数级状态组合根（组件化：Frame/Accessor/
│   │                            #   Labels/Flow/Locals 与 Proxy/捕获状态；组件即方言）
│   ├── BindFunctionFrame.cs     # 只读函数帧（Method/FileCtx/DeclaringType/
│   │                            #   IsDefaultValueContext + HasThis/CanAccess）
│   ├── AccessorBodyState.cs     # 访问器体状态（value 别名：Field/IsSetter）
│   ├── BindLabelState.cs        # 控制流标签栈集（值块/循环/switch 占位/seq 标签
│   │                            #   四栈封装 + 命中查找领域方法）
│   ├── FlowState.cs             # DA 流分析（收窄表的家——同生命周期分叉合并）
│   ├── Scope.cs                 # 词法作用域链
│   ├── Dispatchers.cs           # 类别分派（Expression/Statement/Block 唯一 switch）
│   ├── BoundAnalysis.cs         # BoundTree 静态分析（GuaranteesReturn 等）
│   ├── BindingDriver.cs         # 声明骨架遍历 + 逐函数体启动
│   ├── SymbolLookup.cs          # 实例成员/泛型字段替换/IsAssignable 查询
│   ├── MemberLookup.cs          # 名字解析查找序（宿主→命名空间链→通配 import）
│   ├── TypeReferences.cs        # 函数体内类型引用解析
│   └── Visitors/                # 结构 visitor 簇（Literal/Declaration/Conditional/
│                                #   Loop/Switch/TrySeq/Binary/Path/Call/TypeCheck）
└── Bound/                    # BoundNode 家族（按类别分文件，仿 AST/）
Lowering/                  # P4
├── Lowered/                  # LoweredNode 家族
├── Lowerer.cs                # P4a 瘦入口
├── LoweredVisitor.cs         # P4a CRTP 基类
├── LowerEnvironment.cs       # 只读环境
├── LowerContext.cs           # 函数级组合根（Method/TransformFailed +
│                             #   Synth/Output/Targets 三组件）
├── SynthLocalFactory.cs      # 合成局部工厂（.sN/.bN 独立计数统一登记 + ReferenceTo）
├── LowerOutputState.cs       # 前置语句机制（输出列表栈封装）
├── LowerTargetState.cs       # 循环/switch/安全访问三栈；值块/seq 已转独立目标表
├── StructuredExitTargetTable.cs # return@ 目标身份 → 结果局部 + region breakId
├── StructuredExitRouting.cs  # 独立 route local、break 与 region 后 dispatcher
├── LowerDispatchers.cs       # 类别分派 + LowerBlockVisitor（输出列表压弹）
├── LoweringDriver.cs         # 逐函数体启动
├── LoweringFacility.cs       # LowerArguments/EnsureDeclaredType（cast 物化）
├── Rewriters/                # 结构 visitor 簇（Statement/Loop/Switch/TrySeq/
│                             #   ValueBlock/Expression/NullSafety/Destructuring）
├── BilEmitter.cs             # P4b 瘦入口
├── EmitVisitor.cs            # P4b CRTP 基类（签名带 BilBlock target 施工目标）
├── EmitEnvironment.cs        # 每函数 job 独占；join 后重放资源驻留键并组装模块/切片
├── EmitContext.cs            # 函数级组合根（Function + Temps/BlockIds）
├── TempVarTable.cs           # 临时变量 .tN 工厂（自 EmittingFacility 收编）
├── BlockIdAllocator.cs       # 分支 block 编号分配器（if/loop/switch/seq/try）
├── EmitDispatchers.cs        # 类别分派（语句 Unit/值 BilVariableOperand）
├── EmittingDriver.cs         # 模块组装 + fn 定义发射
├── EmittingFacility.cs       # 资源登记/intrinsic 枚举映射/转义 共享辅助
└── Emitting/                 # 结构 visitor 簇（LocalSymbols/Statement/Value）
Bil/                       # BIL 生态（对中端零依赖）
├── BilModule.cs              # Module/Metadata/Resources（含 switch-table/
│                             #   catch-table 专用资源类）+ BilScalarType
├── BilSymbols.cs             # 类型与成员声明 + 种类枚举 + BilModifier 子类族
├── BilFunction.cs            # Function/.args/.vars/Block + BilBlockModifier
├── BilInstructions.cs        # 指令基类 + 操作数模型（blk/res 持对象引用）
├── BilComputeInstructions.cs # §11–§12 指令 + 运算/类型检查枚举
├── BilDataInstructions.cs    # §13–§15 指令
├── BilControlFlowInstructions.cs # §16 指令
├── BilSpellings.cs           # 枚举 → 标准拼写唯一定义点
├── BilWriter.cs              # 模型 → 标准 BIL 文本（指令自渲染，无 opcode switch）
├── BilVerifier.cs
└── BilVm.cs
```

文件粒度按实现时实际情况拆分；上表只钉死**目录边界与依赖方向**：
`Semantic → AST`；`Lowering → Semantic`；`Lowering → Bil`；
`Bil` 不依赖任何编译器内部目录。

**visitor 化**：
三树的遍历统一为 CRTP visitor 协议——静态 `Visit` 唯一入口（创建子类
实例 + Enter/Exit 生命周期模板，栈压/弹 finally 固化）、双协议
（`Visit → TResult?` 上行合成 / `VisitInto(shell)` 施工壳填充）、
context 方言（同一函数级状态对象的接口视图，Environment 只读共享；
组件化——组合根 + 职责组件类，组件即方言）、
类别分派器唯一 switch + 结构 visitor 簇级分文件。新增语法结构的
落点：对应簇文件新增 visitor 类 + 分派器注册一行。

CLI 接入已位于 `Core/Commands.cs`：`CompileCommand` 在 parse-only 之后
启动 P1–P3；`--emit-bil PATH` 发射并验证 BIL，按命名空间写切片；
`--sema-only` 只跑 P1–P3，用于诊断验收。选项继承命令注册协议，
与 `--parse-only`、`--explain-dispatch` 按声明互斥。

---

## 10. 测试策略

全部 provider 由独立 TUnit/MTP 宿主发现执行，编译器只转发兼容测试命令；入口与资源协议见
[DEVELOPMENT.md](../../../DEVELOPMENT.md)。

本机提交使用单进程完整 AOT 全量；CI 在独立 runner 按稳定 ID 分片后汇总精确全集，
只改变发现行选择，不拆共享状态动作或 seed 批次，也不重复执行框架契约。中端逐层验收：

- **符号图测试**：断言驻留（同一引用）、继承图、canonical 打印串
  （`CanonicalSymbolPrinter` 输出直接对照 BIL §5.2 的例子）。
- **P3 测试**：使用既有 `BoundDescribe`（仿 `AstDescribe` 的唯一描述器），
  断言 bound 树形态 + 类型定型结果 + 结构性事实；诊断测试断言
  `DiagnosticBag` 内容（消息子串 + Span），复用 `CheckSemanticError`
  类断言由 `CaseAssertions` 在每 worker 的请求 scope 记录。
- **P4 测试**：`LoweredDescribe` 断言脱糖形态（BIL §3.4 每条规则
  至少一个用例）；发射测试直接断言 `BilWriter` 文本（BIL 文本本身
  就是规范化的快照格式，无需再造描述器）。
- **端到端**：`compile --emit-bil` 对照 BIL §20 例子级别的黄金文件。
- **BilVm**：维护执行断言（跑出结果/异常与预期比对），
  测试从「形态断言」升级为「语义断言」；这也是 BIL_STANDARD §21.9
  「VM 可执行性」的持续验证。
- provider 的 Spec 与 `StaticTestProviders` 提供实际动作，`TestSuiteCatalog`
  只保留套件编号兼容；`CaseCatalog` 为全部动作/种子批次提供稳定 ID，
  由 TUnit adapter 消费并在隔离 worker 内执行。共享状态或完整序列对拍的
  单方法保留其生命周期，一条框架发现行可能含多条关联断言，不冒称逐输入拆分。

---

## 11. 原则重申

1. **文档驱动**：动一个语义规则前先读 SYNTAX/RUNTIME/BIL_STANDARD
   对应章节；三份文档冲突时按 BIL §24.3 的优先序，并同步相应规范与实现专题，记录尚未解决的具体契约，不静默绕过。
2. **简洁三问**同样适用于中端：每个新 pass、新节点、新符号种类
   都要过「有必要吗 / 有更简单的吗 / 能复用吗」。
3. **P3/P4 边界是纪律**：P4 发现自己需要"再想一下类型/重载/名字"，
   说明 P3 缺信息——回去补 BoundTree，禁止在 P4 里就地分析。
4. **AST 只读、符号图 P2 后声明侧冻结（透明驻留/P3 合成遵守发布屏障）、BIL 模型自足**——三条数据所有权
   规则违反任何一条都视为架构破坏。
