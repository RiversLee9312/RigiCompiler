# S9 交接文档（给下一位接手的 Agent）

> **用途**：本轮（M67–M72）已完成 S9 泛型里程碑的主体，剩余工作在文档末尾。
> 本文件是**临时交接文档**——接手的 Agent 完成 S9 剩余施工后**必须删除本文件**
> （连同本文档的提及一并清理），并把完成情况记入 `docs/PROGRESS_REPORT.md`
> （项目进度唯一权威来源）。
>
> **交接日期**：2026-08-05

---

## 1. 当前进度（全绿基线：2380/2380 + fuzz 6000 + 语义 fuzz 3000，43 套件）

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M67 | S9 细化（S9a–S9f 进 `SEMANTIC_ROADMAP.md`）+ 规范定稿（SYNTAX §4.2/§3.6/§4.3、RUNTIME §10、BIL §8.2） | ✅ |
| M68 | S9a 函数体内泛型参数放行（纯 P3）：三树类型契约 `TypeSymbol → SemanticSymbol` 放宽、16 处 gate 解开、语句位置 `foo\<i32>(1)` 静默丢实参漏洞修复、`SubstituteFieldType` 泛型参数实参替换、must-return 恢复 | ✅ |
| M69 | S9b 泛型调用绑定（纯 P3）：`OverloadResolution` 候选视图（双层代入）、§4.2 候选池规则、BoundCall 携带 TypeArguments、新 `Semantic/Binding/GenericConstraints.cs` 使用侧约束检查 | ✅ |
| M70 | S9c 泛型 new（纯 P3）：NewVisitor ConstructedFrom 回退 + init 宿主代入 + 泛型定义不可构造保留 | ✅ |
| M71 | S9e hidden args 物化（P4a/P4b）：fn `.args` 的 `.generic.T = .typeid`、调用点 `getid.type` 物化/嵌套转发 `$.generic.T`、`.type generic(...)` 子句、BilVerifier 三处适配——**泛型函数/调用端到端出合法 BIL** | ✅ |
| M72 | S9d-1 值可变参数 vargs/kwargs：bootstrap `ArrayDefinition`、stdlib `core::Pair` 补 init、`BoundVarArgsArgument` 打包、`.vargs.<名>/.kwargs.<名>` 隐藏条目、特权构造打包（元素装箱 .any、具名逐项 Pair） | ✅ |

**CLI 端到端已验证**：`identity\<i32>(box.get\<String>("s"))`、`new Box\<i32>(1)`、`sum(1, 2, 3)`、`config(name = "latte")` 全部出合法 BIL 且验证器零错误。

---

## 2. 剩余路线（按序）

### S9d-2：泛型可变参数（TArgs... / named TValues...）

SYNTAX §4.3 唯一形态是与值可变参数**成对**（M72 已落地 vargs/kwargs，前置就绪）：

```latte
pub func update\<named TValues... with Serializable>(configs: named TValues...): bool { ... }
update(isDarkMode = true, userName = "Andy")
```

- 泛型可变参数的类型实参**不显式书写**，由对应值实参的静态类型推导（§4.3 定稿⑤；位置包 ← 位置实参类型、具名包 ← 具名值实参类型）
- 逐推导类型做约束检查（复用 `GenericConstraints.CheckArguments`）
- BIL §7.1：位置 `.generic.TArgs = .array<.typeid>`、具名 `.generic.TValues = .map<.string, .typeid>`——**注意 `EmittingDriver.EmitFunction` 的泛型参数循环目前一律输出 `.typeid`，需按 `IsVariadic/IsNamedVariadic` 区分**（命名空间 `Semantic/Symbols/SemanticSymbol.cs` 的 `GenericParameterSymbol` 已带这两个标记，M67 前 P1 就收集）
- 调用点 `.generic.TArgs` 物化：位置包 = `new type(.array<.typeid>)`（元素逐项 getid.type，参考 M71 `EmittingFacility.MaterializeTypeId` 与 M72 `VarArgsEmitter` 的打包模式）；具名包 = `.map<.string, .typeid>` 构造（.map 是标准构造，尚无发射先例——可仿 .pair 的 `core::Pair` 模式，.map 对应 bootstrap 无定义，可加 `MapDefinition` 或直接特权构造）
- 候选池规则（SYNTAX §4.2 定稿）：无显式实参时「泛型参数全为可变」的泛型方法应参与（包推导是其固有形态，与「固定泛型参数必须显式实参」不冲突）——`OverloadResolution` 目前无显式实参时只放行 `GenericParameters.Count == 0`，需放宽

### M73：S9f stdlib 泛型化 + 技术债勾销

- `stdlib/core/collections.latte` 泛型化：`RangeEnumerator\<T\>`/`Range\<T\>`（勾销技术债 **#15④**；现 `RangeEnumeratorI32`/`RangeI32` 具体形态退役或保留为具体别名，for 范围循环走泛型路径端到端）
- 技术债：#18② is/supers/with 动态形态值路径带泛型实参（M69 后使用侧泛型已落地，可解开）；#22⑥ 构造宿主覆写签名比对泛型精确性（`OverrideChecker`）；#23③ 转换运算符多泛型参数/宿主泛型参数（`FindConversionOperator` 按不适用回退复核）；#23④ async 闸门 5 调用点实际实参检查（泛型实参共享安全，接 `AsyncGates` 调用点 1/2 同落点）
- 泛型 operator 名字调用复核（M69 注记：`FindInstanceMethods` 仅 `Kind == Regular`，operator 按名字调用未放开——SYNTAX §4.2 定稿说「名字形式与普通方法同规则」）
- SemanticsFuzzTests 泛型归口形态更新（M68/M69 后 `P3: generic ... (S9)` 归口已大量解开——fuzz 用例按新行为核对）

### M74：收尾

- `AGENTS.md` 顶部摘要段追加 S9 里程碑（M67–M72 已在 PROGRESS_REPORT，AGENTS.md 的「当前进度」段需同步）
- 全量 `dotnet build`（0 错误 0 警告）+ `dotnet run -- test --all` + CLI `--emit-bil` 端到端抽查
- **删除本交接文档**

---

## 3. 关键技术信息（改代码前必读）

### 3.1 三树类型契约放宽（M68 核心架构决策）

`BoundExpression.Type` / `LocalSymbol.Type` / `LoweredExpression.Type` 及设施签名（`NewTemp`/`NewSynthLocal`/`EnsureDeclaredType`/`SafeReceiverEntry`/`SubstituteFieldType` 等）从 `TypeSymbol` 放宽为 **`SemanticSymbol`**——泛型参数（`GenericParameterSymbol`）按**引用相等身份**出现在定型类型中。P4 侧 `CanonicalSymbolPrinter.PrintType` 已具 §7.5 投影（`GenericParameterSymbol → .generic<$.generic.T>`）。

⚠️ **常见坑（本轮踩过）**：C# pattern `TypeSymbol { ConstructedFrom: not null } x` 中设计符在花括号外绑定**整个对象**、在花括号内嵌子模式上绑定**属性值**——把「对象判 null 后取属性」误写成「解构属性后再取属性」会得到恒 null。判型统一写法：`x is TypeSymbol t && t.ConstructedFrom == ...` 或 `switch { TypeSymbol t when t.ConstructedFrom == ... }`。**非构造类型（如 String）不匹配 `{ ConstructedFrom: not null }` pattern，会掉进原样分支**——包装类逻辑（如 `?.` 结果 Nullable 包装）必须按「已是 Nullable → 原样 / 普通类型 → 包装 / 泛型参数 → 原样」三态 switch 写。

### 3.2 OverloadResolution 候选视图（M69）

`CandidateView`（定义级符号身份 + 代入后参数/返回类型），`Resolve` 返回三元组 `(Method, Arguments, ReturnType)`——**调用方必须用三元组的 ReturnType 定型 Bound 节点**（定义级 `Method.ReturnType` 是未代入的 T）。`ViewOf` 做双层代入：方法泛型参数 ← 显式实参、宿主泛型参数 ← receiver 构造实参（`receiverType` 参数）。新增调用方要传 `receiverType`（实例调用 = receiver 静态类型、new = 构造类型、静态调用 = null）。

### 3.3 使用侧约束检查（M69）

`Semantic/Binding/GenericConstraints.cs`：`CheckArguments`（调用点显式实参）+ `CheckConstructedType`（类型引用实例化点，嵌套递归）。判定：extends = IsAssignable、supers = 反向、with = 查 `AppliedWrappers`（构造类型回退定义）。边界或实参含未替换泛型参数跳过、ErrorType 静默。**P2 声明侧类型引用的使用侧约束检查尚未接入**（归 M73 技术债，勿在 P2 阶段检查——P2 约束解析在类型引用之后，顺序问题）。

### 3.4 vargs/kwargs（M72）

- `BoundVarArgsArgument`/`LoweredVarArgsArgument`：调用点打包节点，规范参数序最后元素，Type = Array\<Any\>
- `LoweringFacility.LowerArguments` 对包节点**直通不 cast**（否则会被元素类型误 cast——已修，勿回退）
- **named 参数 `IsVariadic` 与 `IsNamedVariadic` 同时为 true**（Parser 标记）——所有分支判断必须「具名先判」（`ValueReferenceEmitter` 的 switch 顺序是关键）
- `CanonicalSymbolPrinter.PrintParameters` 跳过可变参数（canonical 不含它们）
- 体内可变参数引用定型 Array\<元素类型\>（PathVisitors 参数分支），发射映射 `$.vargs.<名>`/`$.kwargs.<名>`
- 打包特权构造：位置包 `new type(.array<.any>)`（元素 cast 到 .any 装箱）；具名包逐项 `new type(core::Pair<.string, .any>)`（名字字符串资源 + 值装箱）再装 array。`core::Pair` 的 init 是 M72 在 `stdlib/.bootstrap.latte` 补的——**勿删**
- `OverloadResolution` 单候选可变参数放行、**多候选含可变仍归口**（包不参与 ranking——后续里程碑可按需放开）

### 3.5 验证器（BilVerifier）

- §5.1 `IsLocalIdentifier` 放行 `.generic.`/`.vargs.`/`.kwargs.` 保留名家族（含内部点）
- §21.3 `VerifyInvoke` 的隐藏参数计数 = 被调 fn 定义 `.args` 的 `.generic./.vargs./.kwargs.` 条目数（`context.Module.Module.Functions` 查）——调用点实参按 §7.2 序（.this → .generic.* → 普通）跳过该数后与 canonical 签名比对。新增隐藏参数形态（如 S9d-2 的 `.generic.TArgs`）会被自动计入，只需保证发射顺序正确
- `.generic<` 类型引用保持降级（M58 防误报优先，勿收紧）

### 3.6 本轮新符号与入口

- `Semantic/Symbols/BootstrapSymbols.cs`：`ArrayDefinition`（Object 分支，`.array` 标准构造，shared 按 T 推导）
- `Semantic/Binding/GenericConstraints.cs`：使用侧约束设施
- `Lowering/TempVarTable.cs`：`NewTypeIdTemp()`（`.typeid` 临时变量，勿用 NewTemp——打印会是 canonical 名）
- `Lowering/EmittingFacility.cs`：`MaterializeTypeId(typeArg, origin, target, ctx, env)`（静态实参 getid.type / 泛型参数转发 `$.generic.T` / ErrorType 占位）
- `Bil/BilSymbols.cs`：`BilTypeDeclaration.GenericParameters`（`.type` 的 `generic(T1, T2)` 子句，S9e 定稿于 BIL §8.2）

---

## 4. 验证命令（每步完成后执行）

```bash
dotnet build                    # 0 错误 0 警告
dotnet run -- test --all        # 43 套件 + fuzz 6000 + 语义 fuzz 3000 全绿
dotnet run -- compile --file x.latte --emit-bil x.bil   # 端到端抽查（验证器零错误）
```

## 5. 提醒（再次强调）

**S9 剩余施工完成后删除本文件**（`docs/HANDOVER.md`），把完成情况按项目惯例更新到 `docs/PROGRESS_REPORT.md`（里程碑历史顶部追加，倒序）与 `AGENTS.md` 摘要段。
