# LatteCompiler 交接（M103 收口后）

> 更新时间：2026-08-11（M103 提交后）
> 目的：供下一次对话的 coding agent 快速恢复上下文。
> 真实仓库根：`C:\Users\SaRiv\source\repos\LatteCompiler\LatteCompiler`（`.git` 在此；
> 外层目录只有 `LatteCompiler.sln`，不是仓库）
> 进度权威来源是 `docs/PROGRESS_REPORT.md`；本文件只提供恢复上下文与待办。

## 当前状态

M97–M103 均已落地并提交（M103：S13 lambda 对象模型全链 + proxy 模板属性化，
65 文件 +3140/−719）。**S13 实质收官**（await/yield/using/lambda 全链贯通）。
工作树干净，基线见文末。

---

## 待办一：lambda/闭包直接遗留（M103 归口，按建议优先级序）

### 1. 局部访问器路线 C（建议优先）

局部变量/参数的 getter/setter（SYNTAX §9.4 局部访问器，M63 起归口）。
用户已裁决走**路线 C：与 closure cell 共用机制**——当时等的闭包设施
已由 M103 落地，现在可直接复用：

- `Lowering/ClosureStoragePlan.cs` 的存储判定表模式（符号 → 存储形态
  三分支：默认直存 / cell 局部 / 闭包字段）就是路线 C 的骨架——把
  「带访问器的局部」登记为另一种存储形态（读 = getter 调用、
  写 = setter 调用），与 cell 读写同构；
- `LocalSymbol.AppliedWrappers` 槽（M79）是既有先例：局部声明的
  P3 登记槽位已存在，访问器符号槽可仿照（注意 M63 访问器三槽
  Getter/Setter/HasBackingStorage 的字段语义）；
- 交互点：访问器局部被 lambda 捕获时的语义（cell 内值 vs 访问器
  代理）需要明确——建议捕获一律作用于 cell 存储，访问器只代理
  外层直接读写，规范上写进 SYNTAX §9.4 局部访问器段。

### 2. 循环/catch/finally(e)/using 变量被捕获（需规范裁决）

**现状**：这些变量不经普通声明语句产生（for 循环变量在 Body 头由
`.e.current()` 赋值、catch/finally(e)/using 由编织产生），cell 创建点
没有现成位置；P3 未拦截，P4 行为未定义——**使用前请先加显式归口
诊断或在裁决后落地**。

**待裁决的规范点**：每迭代新 cell（C#5 foreach 语义）还是函数级共享
cell（C#4 语义）？循环变量是 const（M61），共享 cell 会让全部捕获的
lambda 看到最终值；每迭代新 cell 需要在循环 Body 头合成 cell 构造
（`Lowering/Rewriters/LoopRewriters.cs` for 脱糖处）。catch/finally(e)/
using 变量无迭代问题，补创建点即可。裁决后同步 SYNTAX §5.2/§7.3/§8。

### 3. 括号形态 void 间接调用 `(act)()`（语句位置）

`(act)()` / `(getHandler())()` 这类**非直写名**的 void callable 调用在
语句位置仍报「no result (void)」。根因：`PathVisitors.FoldSuffixes` 的
Call 后缀分支只产 `BoundCallExpression`（值形态），void 结果在值位置
报错；语句位置需要落成 `BoundCallStatement`（invoke.indirect.noret）。
直写形态 `act()` 已正常（CallForm 路径）。落地：语句位置表达式若归
结为间接调用且 void，转 BoundCallStatement（参照
`DeclarationVisitors` 表达式语句分流先例）。

### 4. 间接调用的泛型限制（已有归口诊断，不急）

`invoke.indirect` 路径不支持：显式泛型实参（`f\<T>(x)` 形态）、
泛型可变包命中的 call 运算符。两处都在
`CallVisitors.BindIndirectCallOverload` 明确报错归口。解封需要
invoke.indirect 携带 typeid 实参的 ABI 决策（§15.3 未覆盖）。

---

## 待办二：M88 留下的 wrapper/ext 专项（当时裁决「下一对话专项」）

- **#27⑦**：可变参数成员的 wrapper 拦截建链（当前不拦截不建链）。
- **M84 三项**：wrapper place 深层写穿的剩余形态、索引写、
  局部与静态 wrapper place 存储（`WrapperPlaceLowering.cs:47-49`
  仍是显式归口报错）。
- **#28 剩余子项**：以 `docs/PROGRESS_REPORT.md` 技术债表为准
  （#28⑤ 已随 M88 VM hook 内建消解）。

---

## 待办三：大路线剩余（勿与 frontend 混推）

1. **Middleware**（LLVM IR Generator 前的全部职责，RUNTIME.md）：
   Task/Alarm 状态机、async eager spawn 实体、continuation、
   GC ownership fence、proxy 烘焙（M88 反转后全归此处）、
   `..create` 生命周期与 init 选择（§14.1/§15.5）、FFI ABI（§4.6 native）。
2. **S14 BIL VM**：执行层（§21.9 VM 语义、协程指令 §17 暂缓部分）。
3. S12（BIL 验证器）已在 M58 提前落地；S11 除上述专项外收官。

---

## 关键代码位置（M103 后）

- lambda 对象模型：`Semantic/Binding/CallableModel.cs`（Func/Action/Cell 族
  定位/构造）、`Semantic/Binding/Visitors/LambdaVisitors.cs`（隐藏类合成 +
  捕获集）、`Lowering/ClosureStoragePlan.cs`（存储判定表）、
  `Lowering/Rewriters/ExpressionRewriters.cs`（LambdaRewriter = new 隐藏类）
- 符号属性：`TypeSymbol.LambdaClosure`（隐藏类身份 + 闭包信息）、
  `LocalSymbol/ParameterSymbol.CaptureCell`、`MethodSymbol.ProxyTemplate`
  （proxy 模板属性化，P1 落定——名字判定已全删）
- BIL：`invoke.indirect` 对象 `$$call` 语义（`Bil/BilDataInstructions.cs`）、
  验证器 §15.3 + 宿主泛型签名代入（`Bil/BilVerifier.Types.cs`）、
  `.cell<T>`/`.readonly_cell<T>` 类型构造（§6.3）
- stdlib：`stdlib/.bootstrap.latte` —— Func/Action/AsyncFunc/AsyncAction
  （0–32 元数）+ Cell/ReadonlyCell
- 测试 UUID 归一化：`Tests/BilTestHarness.cs`（`..lambda..[0-9a-f]{32}` →
  `..lambda..UUID`）

## 验证基线

在仓库根执行：

```text
dotnet build
dotnet run --no-build -- test --all
```

当前基线（2026-08-11，M103）：

- build：0 errors, 0 warnings
- 44 test suites，0 failed
- deterministic：3536/3536
- Lexer fuzz：6000/6000；semantic fuzz：3000/3000
- 分项：Binder 893、Lowerer 211、BilEmitter 572、BilVerifier 151

任何后续里程碑完成后，独立运行完整 `test --all`，再更新
`docs/PROGRESS_REPORT.md`。
