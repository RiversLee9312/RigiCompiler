# 中端三树 Visitor 化重构：协议定稿与重写交接

> **定位**：Binder/Lowerer/BilEmitter 三个巨石类（2890 + 1331 + 1045 行）的模块化重构定稿，
> 2026-08-03 经用户逐条拍板。**停线重写**（用户决策），本文档即跨会话交接底稿——
> 任何后续会话读本文件 + `SMART_CAST_DESIGN.md` 即可继续，无需对话上下文。
> 重构完成后本文件的核心结论应并入 `SEMANTIC_ARCHITECTURE.md`（§9 代码组织），本文档保留为决策推理记录。

## 1. 动机与方案选择

- **问题**：`Semantic/Binder.cs`、`Lowering/Lowerer.cs`、`Lowering/BilEmitter.cs` 三个 session 巨石类，60+/40+/20+ 方法共享全部状态字段，任何方法可碰任何状态（值块栈、循环栈、switch 占位栈……），已构成维护性灾难风险。
- **用户方案（定稿）**：visitor 化——所有模块类继承 CRTP 抽象基类；基类静态 `Visit` 统一入口，外部调用者（上一层 visitor 或启动器）传入节点；CRTP 创建子类对象并**模板化管理生命周期**（Enter/Exit 配对，栈压/弹固化在基类，杜绝手工配对泄漏）。Lowerer/BilEmitter 以同一模式遍历各自的树。
- **被否定的方案**：Roslyn 式超大 partial class（文件拆分但单类状态共享）——用户判定状态污染面仍在，隔离收益不足。注：Roslyn 的 Binder 实为 partial 大类，本项目方案在隔离性上**超越** Roslyn，代价是样板代码量增加（用户明确接受：「用代码量换模块化以及 agent 可读性」）。

## 2. 已拍板决策

| 决策点 | 结论 |
|---|---|
| 迁移策略 | **停线重写**（放弃逐簇双轨增量）；重写期间允许测试红，完成前不提交 git |
| 顺序 | **Binder 先行**全链走通验证协议手感 → Lowerer → BilEmitter 模式复制 |
| 协议 | **双协议**：`Visit → TResult?`（上行合成，常态）+ `VisitInto(shell)`（施工壳填充，值块/循环/函数体等少数壳）。Binder 是上行合成（子表达式绑完才建父节点），不同于 Parser 的纯下行施工，故不硬拼单签名 |
| CRTP 落地 | `where TSelf : ..., new()` + 抽象 `VisitCore`；不用 C# 11 static abstract（调用方全泛型化，绕） |
| Context | `BindEnvironment`（只读，全编译期不变，固定参数）+ `TContext` 泛型方言；**方言 = 同一函数级状态对象的接口视图**（禁止多方言对象导致 DA/标签栈状态分裂）；初期从粗（一个 `BindContext` + 少量角色接口），按需 extract 再切细 |
| 分派 | 两层：**类别分派器**（Expression/Statement/Block，唯一 switch 所在，对应旧 BindExpression/BindStatement）+ **结构 visitor**（每语法结构一个类）；结构 visitor 之间不直接互调，一律经分派器 |
| 诊断恢复 | `TResult?` 可空约定：绑定失败落诊断返回 null 继续；ErrorType 毒化静默规则（已失败者不次生报错）在各 visitor 间统一遵守（沿用现有口径） |

## 3. 协议骨架（定稿代码形态）

> **协议 v2 修正（动工时发现）**：绑定遍历有两个固有下传参数是原 v1 签名
> 遗漏的——`scope`（当前词法环境，随块嵌套变化，等价 Parser 的施工目标）
> 与 `expectedType`（期望类型下传：null 字面量定型、return/赋值/实参的
> 目标类型传播，仅表达式消费）。定稿为三基类：通用 `BinderVisitor`
> （node/scope/ctx/env）、表达式专用 `ExpressionVisitor`（追加 expectedType
> 可选参数）、壳填充 `BinderShellVisitor`（node/scope/shell/ctx/env）。

```csharp
// Semantic/Binding/BinderVisitor.cs
namespace LatteCompiler;

// 通用基类（语句/块/其他结构）
internal abstract class BinderVisitor<TSelf, TResult, TContext>
    where TSelf : BinderVisitor<TSelf, TResult, TContext>, new()
{
    public static TResult? Visit(ASTNode node, Scope scope, TContext ctx, BindEnvironment env)
    {
        var visitor = new TSelf();
        try { visitor.Enter(node, scope, ctx, env); return visitor.VisitCore(node, scope, ctx, env); }
        finally { visitor.Exit(node, scope, ctx, env); }
    }
    protected abstract TResult? VisitCore(ASTNode node, Scope scope, TContext ctx, BindEnvironment env);
    protected virtual void Enter(...) { }   // 栈压
    protected virtual void Exit(...) { }    // 栈弹（finally 配对）
}

// 表达式专用基类：追加 expectedType 可选下传
internal abstract class ExpressionVisitor<TSelf, TContext>
    where TSelf : ExpressionVisitor<TSelf, TContext>, new()
{
    public static BoundExpression? Visit(ASTNode node, Scope scope, TContext ctx,
        BindEnvironment env, TypeSymbol? expectedType = null) { ...同模板... }
}

// 壳填充基类：值块/循环/函数体等「壳先建压栈、体绑完回填」场景
internal abstract class BinderShellVisitor<TSelf, TShell, TContext>
    where TSelf : BinderShellVisitor<TSelf, TShell, TContext>, new()
{
    public static void VisitInto(ASTNode node, Scope scope, TShell shell, TContext ctx,
        BindEnvironment env) { ...同模板... }
}
```

```csharp
// Semantic/Binding/BindEnvironment.cs —— 只读，全编译期不变
internal sealed class BindEnvironment
{
    public CompilationUnit Unit { get; }            // 符号图 + 诊断袋（诊断经此落袋）
    public DeclarationCollection Declarations { get; }
    public NameResolver Names { get; }              // Phase P3 实例
    public BootstrapSymbols B => Unit.Symbols.Bootstrap;
    public void Error(CharRange? span, string message);   // P3 诊断落袋统一出口
}

// Semantic/Binding/BindContext.cs —— 函数级可变状态（一个函数体绑定期间存活）
internal sealed class BindContext : IFlowContext
{
    public MethodSymbol Method; public FileContext FileCtx; public TypeSymbol? DeclaringType;
    public List<LocalSymbol> Locals;
    public FlowState Flow { get; }                  // DA + （S8b）收窄事实
    public Scope 作用域链; 标签栈（值块/循环/switch 占位）;
}

// Semantic/Binding/FlowState.cs —— 流分析状态（DA assigned + S8b 收窄表，同生命周期）
internal sealed class FlowState
{
    // assigned 集 + 收窄事实表的分叉/合并/快照恢复（迁移自旧 BindSession 的
    // assigned 字段 + RestoreAssigned + MergeBranches + if/循环分叉合并内联代码）
}
```

**方言接口**（初期从粗，按需 extract）：

```csharp
internal interface IFlowContext { FlowState Flow { get; } }
// 后续按真实隔离需求从 BindContext 拉接口（IScopeContext/ILabelContext……），
// 组合成类别方言（IExprContext/IStmtContext）。禁止切成多个独立状态对象——
// DA/标签栈是跨 visitor 共享可变状态，多方言对象 = 状态分裂。
```

**分派器**（类别分派，唯一 switch）：

```csharp
// Semantic/Binding/Dispatchers.cs
internal static class ExpressionDispatcher
{
    public static BoundExpression? Visit(ASTNode node, BindContext ctx, BindEnvironment env) =>
        node switch { LiteralExpressionASTNode => LiteralVisitor.Visit(node, ctx, env), ... };
}
// StatementDispatcher / BlockDispatcher 同形
```

## 4. 文件组织（Binder 重写目标态）

```
Semantic/
├── Binder.cs                  # 瘦入口：Bind(unit, declarations) → 建环境 + 逐函数启动
└── Binding/
    ├── BinderVisitor.cs       # CRTP 双协议基类
    ├── BindEnvironment.cs     # 只读环境
    ├── BindContext.cs         # 函数级状态对象
    ├── FlowState.cs           # DA + 收窄事实（S8b 的家）
    ├── Scope.cs               # 词法作用域链
    ├── Dispatchers.cs         # 类别分派（Expression/Statement/Block）
    └── Visitors/
        ├── FunctionBodyVisitor.cs   # 函数体启动（return 全路径检查/GuaranteesReturn）
        ├── LiteralVisitors.cs       # 字面量 + 字符串插值
        ├── BinaryVisitors.cs        # 二元/一元/复合赋值/if? 空值回退
        ├── ConditionalVisitors.cs   # if 语句/表达式 + 值块壳（VisitInto 首验）
        ├── LoopVisitors.cs          # while/do-while/for/break/continue（标签栈首验）
        ├── SwitchVisitors.cs        # switch 双形态（占位栈）
        ├── TrySeqVisitors.cs        # try/throw/seq
        ├── PathVisitors.cs          # 路径/this/实例链/字段引用（最大簇）
        ├── CallVisitors.cs          # 调用/实参/new
        ├── TypeCheckVisitors.cs     # is/supers/with/typeOf/cast
        └── DeclarationVisitors.cs   # 局部声明/解构/赋值/表达式语句/return
```

粒度说明：**簇级分文件**（每文件 1–4 个 visitor 类），非一个 visitor 一个文件——仿 Parser「一层一文件」的既有惯例。

## 5. 迁移顺序与进度跟踪

停线期间仍按簇推进（可调试性）；每簇完成跑 build，全链完成后跑全量测试。

| # | 簇 | 状态 | 备注 |
|---|---|---|---|
| 1 | 协议骨架（§3 全部 + 分派器空壳） | ☑ | new() 约束 + 双协议编译通过；协议 v2 修正（scope/expectedType） |
| 2 | 字面量簇（含插值） | ☑ | 协议管线首验 |
| 3 | 语句基础（块/表达式语句/return/声明/赋值/解构） | ☑ | 块分派 + DA 写入 |
| 4 | if 语句/表达式 + 值块壳 | ☑ | 壳协议首验（ValueBlockShell 包装承载 construct 名）+ DA 分叉合并 |
| 5 | 循环（while/do-while/for/break/continue） | ☑ | LoopBodyVisitor 壳（Enter 压栈/Exit 弹栈替代手工 try/finally） |
| 6 | switch 双形态 | ☑ | SwitchMatchContext 载荷 + 实例字段记录压栈（任务局部状态范例） |
| 7 | try/seq/throw | ☑ | DA 合并复用 |
| 8 | 二元/一元/复合赋值/if? | ☑ | IntrinsicMapping 设施 |
| 9 | 路径/this/实例链/字段引用 | ☑ | PathFacility 双入口（值/赋值目标）+ MemberLookup 查找序设施 |
| 10 | 调用/实参/new | ☑ | CallFacility + NewVisitor |
| 11 | typeCheck/typeOf/cast | ☑ | 不落袋试探保持 |
| 12 | 函数体启动器收尾 + GuaranteesReturn 等静态分析 | ☑ | BindingDriver + BoundAnalysis |
| 13 | 删旧 Binder.cs + `test --all` 全绿 + build 0 警告 | ☑ | **2026-08-03 完成：40 套件 0 失败一次通过，重写引入 0 新警告**（StdlibSourcesTests CS8602 为 HEAD 预先存在） |
| 14 | Lowerer visitor 化（模式复制） | ☑ | 见 §6 |
| 15 | BilEmitter visitor 化 | ☑ | **2026-08-03 完成：40 套件 0 失败一次通过（BilEmitterTests 120 用例黄金文本逐字节一致），重写引入 0 新警告**（StdlibSourcesTests CS8602 为 HEAD 预先存在）；EmittingDriver/EmitDispatchers + Emitting/ 三簇（LocalSymbolEmitters 符号段静态设施、StatementEmitters 12 visitor、ValueEmitters 14 visitor）+ EmittingFacility（Unit 占位 + 资源/转义静态辅助） |
| 16 | SEMANTIC_ARCHITECTURE §9 + AGENTS.md + PROGRESS_REPORT 同步 | ☑ | **2026-08-03 完成**：ARCH §9 代码组织重写为 visitor 化目标态；AGENTS.md 进度段 + 结构树 + 关键文件表 + 中端标准流程；PROGRESS_REPORT M55 段 + 总览 + 技术债 19 |

## 6. Lowerer / BilEmitter 复制要点（阶段 14/15）

- **Lowerer**：`LoweredVisitor<TSelf, TResult, TContext>` 同构基类；`LowerEnvironment`（unit/诊断）；`LowerContext` 函数级持有：synthLocals/计数器、**outputStack 前置语句机制**（表达式降级途中往当前块塞前置语句——跨 visitor 共享可变状态，归 context 是物理限制）、valueBlocks/loops/switchTemps/safeReceivers 各栈、transformFailed。脱糖规则每规则一 visitor；值块降级 + if 转换 + continuation 编织一簇**整体迁移不拆散**（M49 miscompile 修复区）。
- **BilEmitter**：`EmitVisitor` 同构；本已天然「节点 + BilBlock target」下行填充形态（EmitStatement/EmitValue 签名即 VisitInto）——三棵树中与壳协议贴合度最高。`EmitContext`：资源表（resourceKeys/tempVars/block 计数/EmitBuiltinExtMembers 状态）。
- 两树的簇划分沿用各自文件内既有 `// =====` 区域注释。

## 7. 风险与注意事项

1. **壳协议与值块编织**（簇 4）是全重写最微妙处——M49 修过 else-if 链编织 miscompile；迁移时逐行对照旧 continuation 编织逻辑，编织语义（终止分支织空、非终止分支织 rest）不变。
2. **outputStack / 标签栈的手工压弹**是现有代码中唯一不成对的隐患源，visitor 化后一律收进 Enter/Exit——迁移时如发现旧代码有不成对路径，记录后按新协议修正并在 PROGRESS_REPORT 技术债备注。
3. **BinderTests/LowererTests/BilEmitterTests 三套件**是重写的直接验证（其余 Parser 等套件不受影响应全程绿）；测试**零改动原则**——树形态/描述串/诊断消息均不变，任何测试断言的修改都必须先论证是旧行为 bug。
4. Bound/Lowered 节点集**不动**（BoundSmartCastExpression 是 S8b 的事，不在重写期夹带）。
5. 重写完成前**不提交 git**；完成后由用户确认再提交。

## 8. S8b 落点预告（重写完成后）

smart cast（定稿见 `SMART_CAST_DESIGN.md`）在新架构下的落点：`FlowState` 增收窄表 + 新 visitor `ConditionFactsVisitor`（条件事实提取，`TContext = IFlowContext`，返回真/假边事实对——§3 协议表达力的首个新需求验证）+ 引用绑定点查表包 `BoundSmartCastExpression` + Lowerer 物化 visitor。
