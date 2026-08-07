# LatteCompiler 临时交接

> 更新时间：2026-08-08
> 目的：供下一次对话的 coding agent 快速恢复上下文。
> 真实仓库根：`C:\Users\SaRiv\source\repos\LatteCompiler\LatteCompiler`

## 当前状态

本次工作已完成并准备提交 M97–M102。不要回退工作树中已有的 S13 改动。
进度权威来源是 `docs/PROGRESS_REPORT.md`；本文件只提供恢复上下文。

### 已完成里程碑

- **M97 await**：`Task`/`Task<T>` P3 绑定，`BoundAwaitExpression`、P4a/P4b
  `await TASK [RESULT]`，await 后清除 smart-cast 但保留 DA，BilVerifier 校验结果类型。
- **M98 yield**：裸 `yield` 与 `yield Alarm`，接受 `PollingAlarm`/`EventAlarm`
  及可赋值用户子类；新增 `BoundYieldStatement`、`LoweredYieldStatement`、
  `YieldInstruction`，yield 后 smart-cast 失效；泛型 Alarm 约束和 verifier 防御已补齐。
- **M99 using 语句**：`BoundUsingBinding`，IDisposable/dispose 解析，初始化失败只清理
  已建立前缀，finally 按逆序 dispose；P4 使用 nested try/finally，不新增 opcode。
- **M100 using 表达式**：结果局部先写入，再以完整值块作为 protected body 包入资源
  nested try/finally；结果局部在清理后返回。复杂 outer value-block continuation 仍保守拦截。
- **M101 lambda P3 Slice A**：匿名 `LambdaTypeSymbol`、`BoundLambdaExpression`，隔离
  `Scope/BindContext`，符号级捕获集，DA 只继承已赋值事实，不继承 smart-cast；嵌套捕获
  向外传递；async lambda 补参数/返回/捕获共享安全闸门。
- **M102 lambda P4 B0**：仅无捕获普通 lambda 可进入 P4。生成 synthetic body 和
  LocalSymbols method declaration；局部 lambda 值使用 `.methodid`，发射 `getid.method`
  和 `invoke.indirect`/`invoke.indirect.noret`；verifier 校验 methodid 参数/返回签名。

## 明确未实现边界

- 捕获 lambda 的 closure environment、按值捕获、可变 `var`/参数的 closure cell。
- 函数值比较、字段/静态字段存储、普通函数参数/返回值传递等未定形态。
- async lambda 的 eager spawn、Task 化和 closure 复用。
- 局部访问器路线 C，需和 closure cell 共用环境机制。
- 动态/可挂起 dispose、完整清理游标、复杂 continuation。
- Middleware 的 Task/Alarm 状态机、continuation、GC ownership fence；S14 VM 执行。

捕获 lambda 的预期行为：P3 能绑定并记录捕获；P4 在
`Lowering/Rewriters/ExpressionRewriters.cs` 的 `LambdaRewriter` 报明确
`captured lambda closure lowering is not available` pending，并跳过该函数体。

## 关键代码位置

- `Semantic/Binding/Visitors/LambdaVisitors.cs`：lambda P3 绑定、synthetic body 注册、
  async 闸门和 pending consumer。
- `Semantic/Binding/BindingDriver.cs`：`env.SyntheticLambdas` 汇入 bodies；捕获 lambda
  不在此阶段报错，保持 P3 无诊断。
- `Semantic/Bound/BoundExpressions.cs`：`BoundLambdaExpression`、lambda callable 类型。
- `Lowering/Rewriters/ExpressionRewriters.cs`：无捕获 lambda P4a；捕获 lambda pending。
- `Lowering/Emitting/ValueEmitters.cs`：lambda handle、indirect call、await/yield 发射。
- `Bil/BilFunction.cs`：`BilMethodIdType`。
- `Bil/BilComputeInstructions.cs`：`GetIdMethodInstruction`。
- `Bil/BilDataInstructions.cs`：`InvokeIndirectInstruction` 与 noret 形态。
- `Bil/BilVerifier.Types.cs`：methodid canonical 签名、indirect invoke 参数/返回校验。
- `Lowering/Emitting/LocalSymbolEmitters.cs`、`Lowering/EmittingDriver.cs`：synthetic
  lambda method declaration 和 function body 发射。
- `Tests/BinderTests.Lambda.cs`、`Tests/LowererTests.Basics.cs`、
  `Tests/BilEmitterTests.Lambda.cs`、`Tests/BilVerifierTests.cs`：lambda 回归覆盖。
- `docs/compiler/semantic/ASYNC_LOWERING_DESIGN.md`：S13 设计边界。
- `docs/compiler/semantic/SEMANTIC_ROADMAP.md`：S13 顺序和当前 B0 状态。
- `docs/PROGRESS_REPORT.md`：M97–M102 里程碑与精确测试数。

## 验证基线

在仓库根执行：

```text
dotnet build
dotnet run --no-build -- test --all
git diff --check
```

当前基线：

- build：0 errors, 0 warnings
- 44 test suites，0 failed
- deterministic：3441/3441
- Lexer fuzz：6000/6000
- semantic fuzz：3000/3000
- 分项：Binder 884、Lowerer 206、BilEmitter 481、BilVerifier 150

## 下一步建议

下一次只推进 closure environment/cell，不要同时推进 Middleware。推荐顺序：

1. 先为无捕获 B0 的 synthetic method 机制确定是否复用同一保留符号/声明路径。
2. 做只读 `const`/从未被闭包写入局部的按值捕获，明确创建点求值一次和环境字段布局。
3. 再做被闭包写入的 `var`/参数唯一 cell，并同步 DA、smart-cast 失效和复合赋值单次求值。
4. 最后接局部访问器路线 C、async lambda 和 Middleware 生命周期。

任何后续里程碑完成后，独立运行完整 `test --all`，再更新 `docs/PROGRESS_REPORT.md`。
