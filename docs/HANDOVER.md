# LatteCompiler 交接（M104 收口后）

> 更新时间：2026-08-12（M104 提交后）
> 目的：供下一次对话的 coding agent 快速恢复上下文。
> 真实仓库根：`C:\Users\SaRiv\source\repos\LatteCompiler\LatteCompiler`（`.git` 在此；
> 外层目录只有 `LatteCompiler.sln`，不是仓库）
> 进度权威来源是 `docs/PROGRESS_REPORT.md`；本文件只提供恢复上下文与待办。

## 当前状态

M97–M104 均已落地并提交（M104：统一 cell 存储——wrapper 值 Cell 化 +
Cell 抽象基类 + 逐变量隐藏子类，含 lambda 捕获迁移与 BilVerifier
HostMatches 修复）。**S13 实质收官**（await/yield/using/lambda 全链贯通），
M84「局部/静态 wrapper place 存储」归口已解。工作树干净，基线见文末。

---

## 待办一：lambda/闭包直接遗留（M103 归口，按建议优先级序）

### 1. 局部访问器路线 C（建议优先）

局部变量/参数的 getter/setter（SYNTAX §9.4 局部访问器，M63 起归口）。
用户已裁决走**路线 C：与 closure cell 共用机制**——闭包设施已由
M103 落地，M104 进一步把 `ClosureStoragePlan` **通用化为统一 cell
存储计划**（§5.2 捕获 + §14.3 wrapper 值；条目按 `CellStorage` 标记），
局部访问器局部的存储形态接入点不变，现在可直接复用：

- `Lowering/ClosureStoragePlan.cs` 的统一 cell 存储判定表（符号 →
  存储形态）就是路线 C 的骨架——把「带访问器的局部」登记为另一种
  存储形态（读 = getter 调用、写 = setter 调用），与 cell 读写同构；
- `LocalSymbol.AppliedWrappers` 槽（M79）+ `CellStorage` 槽（M104）
  是既有先例：局部声明的 P3 登记槽位已存在，访问器符号槽可仿照
  （注意 M63 访问器三槽 Getter/Setter/HasBackingStorage 的字段语义）；
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

## 待办二：wrapper/ext 专项

- **#27⑦**：可变参数成员的 wrapper 拦截建链（当前不拦截不建链）。
- **M84 剩余两项**：wrapper place 深层写穿的剩余形态、索引写
  （仍是显式归口报错；局部/静态 place 存储已于 M104 由统一 cell
  存储解决）。
- **M104 归口**：
  - 泛型参数值类型的 cell 化在册守卫——lambda 体内包装外层方法泛型
    参数等合成点泛型缺席场景显式归口（"P3: cell storage for
    generic-parameter-typed values in this context is not supported yet"）；
    解封需补隐藏子类泛型上下文转发链（InScopeGenericParameters 对
    合成类方法覆盖外层方法泛型参数）。
  - 静态/全局字段 cell 的构造时机归 Middleware（frontend 只生成与
    标注，BIL 无构造点语法；若需源级语义先在 RUNTIME/BIL 定稿）。
  - wrapper 应用 init 实参的 BIL 承载留白（§8.3.1 既有留白，全语言
    一致——含 cell 子类 value 字段上的 wrapped(W) 标记）。
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

## 关键代码位置（M104 后）

- 统一 cell 存储：`Semantic/Binding/CellClassFactory.cs`（逐变量隐藏
  子类 `..cell..UUID` 合成 + 泛型上下文共享守卫）、
  `Semantic/Binding/CallableModel.cs`（Func/Action 族与
  Cell/ReadonlyCell 抽象基类定位/构造）、符号槽
  `LocalSymbol/ParameterSymbol/FieldSymbol.CellStorage`（TypeSymbol 同名
  槽回挂兼作识别标记）、`BindEnvironment.SyntheticCellBodies` →
  BindingDriver 汇入（静态/全局字段 cell 化 = 阶段 1.6）
- lambda 对象模型：`Semantic/Binding/Visitors/LambdaVisitors.cs`
  （隐藏类合成 + 捕获集——`.capture.*` 字段类型 = 捕获符号的 cell
  子类，已 cell 化变量按引用直捕）、`Lowering/ClosureStoragePlan.cs`
  （统一 cell 存储判定表：§5.2 捕获 + §14.3 wrapper 值）、
  `Lowering/Rewriters/ExpressionRewriters.cs`（LambdaRewriter = new 隐藏类）
- wrapper place 降级：`Lowering/Rewriters/WrapperPlaceLowering.cs`
  （局部/静态 = cell 根分派：链 field(value)+wrapper(W)）+
  `Lowering/Rewriters/CellStorageLowering.cs`（静态/全局 cell 值读写改写）
- 符号属性：`TypeSymbol.LambdaClosure`、`MethodSymbol.ProxyTemplate`
  （proxy 模板属性化，P1 落定——名字判定已全删）
- BIL：`invoke.indirect` 对象 `$$call` 语义（`Bil/BilDataInstructions.cs`）、
  验证器 §15.3 + 宿主泛型签名代入（`Bil/BilVerifier.Types.cs`）、
  `.cell<T>`/`.readonly_cell<T>` 抽象基类类型构造（§6.3）、
  `BilVerificationContext.HostMatches` 归一化先于剥实参（M104 修复）
- stdlib：`stdlib/.bootstrap.latte` —— Func/Action/AsyncFunc/AsyncAction
  （0–32 元数）+ Cell/ReadonlyCell 抽象基类（无 value 字段/无显式 init）
- 测试 UUID 归一化：`Tests/BilTestHarness.cs`（`..lambda../..cell..` 两族
  分别归一为 `..lambda..UUID`/`..cell..UUID`）

## 验证基线

在仓库根执行：

```text
dotnet build
dotnet run --no-build -- test --all
```

当前基线（2026-08-12，M104）：

- build：0 errors, 0 warnings
- 44 test suites，0 failed
- deterministic：3648/3648
- Lexer fuzz：6000/6000；semantic fuzz：3000/3000
- 分项：Binder 915、Lowerer 211、BilEmitter 628、BilVerifier 151

任何后续里程碑完成后，独立运行完整 `test --all`，再更新
`docs/PROGRESS_REPORT.md`。
