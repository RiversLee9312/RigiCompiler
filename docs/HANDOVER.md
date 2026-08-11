# LatteCompiler 临时交接

> 更新时间：2026-08-11
> 目的：供下一次对话的 coding agent 快速恢复上下文。
> 真实仓库根：`C:\Users\SaRiv\source\repos\LatteCompiler\LatteCompiler`

## 当前状态

M97–M103 均已落地。不要回退工作树中已有的 S13 改动。
进度权威来源是 `docs/PROGRESS_REPORT.md`；本文件只提供恢复上下文。

### 已完成里程碑（M97–M103）

- **M97 await**：`Task`/`Task<T>` P3 绑定，`BoundAwaitExpression`、P4a/P4b
  `await TASK [RESULT]`，await 后清除 smart-cast 但保留 DA，BilVerifier 校验结果类型。
- **M98 yield**：裸 `yield` 与 `yield Alarm`，接受 `PollingAlarm`/`EventAlarm`
  及可赋值用户子类；新增 `BoundYieldStatement`、`LoweredYieldStatement`、
  `YieldInstruction`，yield 后 smart-cast 失效；泛型 Alarm 约束和 verifier 防御已补齐。
- **M99 using 语句**：`BoundUsingBinding`，IDisposable/dispose 解析，初始化失败只清理
  已建立前缀，finally 按逆序 dispose；P4 使用 nested try/finally，不新增 opcode。
- **M100 using 表达式**：结果局部先写入，再以完整值块作为 protected body 包入资源
  nested try/finally；结果局部在清理后返回。复杂 outer value-block continuation 仍保守拦截。
- **M101 lambda P3 Slice A**：匿名 callable 绑定与符号级捕获集（后续 M103 废除
  `LambdaTypeSymbol`，改为隐藏类对象模型）。
- **M102 lambda P4 B0**：曾以 `.methodid`/`getid.method` 发射无捕获 lambda——
  **已由 M103 整体替换**为隐藏类 + `$$call` 对象模型（见下）。
- **M103 lambda 对象模型全链**（SYNTAX §5.2，2026-08-11）：
  - 隐藏类 `..lambda..UUID` 继承 Func/Action/AsyncFunc/AsyncAction（0–32 元数）；
  - 捕获全 Cell 化（`core::Cell<T>` / `ReadonlyCell<T>`，this 普通字段例外）；
  - BIL `.cell<T>`/`.readonly_cell<T>`；`invoke.indirect` = 对象虚调用 `$$call`；
  - 删 `getid.method`/`.methodid`；`LambdaTypeSymbol` 废除；
  - `ClosureStoragePlan` + `CallableModel`；值块体降级；验证器 §15.3 重写。

## 明确未实现边界（M103 后）

- ~~捕获 lambda 的 closure environment / cell~~ → **M103 已落地**（全 Cell 化）。
- ~~async lambda 的 eager spawn / Task 化~~ → **M103 已落地**（AsyncFunc/AsyncAction +
  async `$$call` + invoke.indirect 结果 Task）。
- ~~函数值字段/返回值传递~~ → **M103 已落地**（普通对象 upcast + cast 物化）。
- 循环变量 / catch / finally(e) / using 资源变量**被捕获**暂不支持（cell 创建点语义待定）。
- 语句位置括号形态 void 间接调用 `(act)()` 归口（直接 `act()` 已发
  `invoke.indirect.noret`）。
- 局部访问器路线 C（与 closure cell 共用机制，仍待施工）。
- 动态/可挂起 dispose、完整清理游标、复杂 outer value-block continuation。
- Middleware 的 Task/Alarm 状态机、continuation、GC ownership fence；S14 VM 执行。

## 关键代码位置

- `Semantic/Binding/CallableModel.cs`：Func/Action/Cell 族查找与构造（M103 新）。
- `Semantic/Binding/Visitors/LambdaVisitors.cs`：lambda P3 绑定、隐藏类合成、捕获集。
- `Semantic/Binding/BindingDriver.cs`：synthetic lambda bodies 汇入。
- `Lowering/ClosureStoragePlan.cs`：闭包存储判定表（M103 新）。
- `Lowering/Rewriters/ExpressionRewriters.cs`：lambda P4a / 值块体降级。
- `Lowering/Emitting/ValueEmitters.cs`：new 隐藏类、invoke.indirect、await/yield。
- `Lowering/Emitting/LocalSymbolEmitters.cs`、`Lowering/EmittingDriver.cs`：
  synthetic 类型声明 + cell .vars / 参数 prologue。
- `Bil/BilDataInstructions.cs`：`InvokeIndirectInstruction`（对象 `$$call`）。
- `Bil/BilVerifier.Types.cs`：§15.3 $$call 查找 + 宿主泛型签名代入。
- `stdlib/.bootstrap.latte`：Func/Action/AsyncFunc/AsyncAction（0–32）+ Cell/ReadonlyCell。
- `Tests/BinderTests.Lambda.cs`、`Tests/LowererTests.*.cs`、
  `Tests/BilEmitterTests.Lambda.cs`、`Tests/BilVerifierTests.cs`。
- `docs/compiler/semantic/ASYNC_LOWERING_DESIGN.md`：§6/§8 以 SYNTAX §5.2 为准（M103 注记）。
- `docs/compiler/semantic/SEMANTIC_ROADMAP.md`：S13 状态。
- `docs/PROGRESS_REPORT.md`：M103 里程碑与精确测试数。

## 验证基线

在仓库根执行：

```text
dotnet build
dotnet run --no-build -- test --all
```

当前基线（2026-08-11）：

- build：0 errors, 0 warnings
- 44 test suites，0 failed
- deterministic：3536/3536
- Lexer fuzz：6000/6000
- semantic fuzz：3000/3000
- 分项：Binder 893、Lowerer 211、BilEmitter 572、BilVerifier 151

## 下一步建议

1. 循环/catch/finally(e)/using 变量捕获的 cell 创建点语义定稿与落地；或
2. 括号形态 void 间接调用 `(act)()` 归口解封；或
3. 局部访问器路线 C（复用 ClosureStoragePlan）；或
4. Middleware Task/Alarm 状态机边界（勿与 frontend 混推）。

任何后续里程碑完成后，独立运行完整 `test --all`，再更新 `docs/PROGRESS_REPORT.md`。
