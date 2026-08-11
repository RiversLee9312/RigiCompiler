# LatteCompiler 项目指南（AGENTS.md）

> **用途**: 为 AI 编码代理提供 Latte 编译器项目的完整上下文。读者默认对本项目一无所知。
> 本文件是项目指南的唯一载体（原 `CLAUDE.md` 为陈旧副本，已删除），内容以实际代码为准（已验证日期：2026-07-31）。

**项目名**: LatteCompiler
**语言**: C#（.NET 8.0，控制台程序，`Nullable` 与 `ImplicitUsings` 已启用）
**开发阶段**: 中端（语义分析 + BIL 生成）阶段 —— 编译器前端（Lexer + Parser）已完成（roadmap P0–P5 全部落地，M23–M34 大扫除与多轮修复）；中端 M35 完成架构定稿（`docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md`）与路线图 S0–S14（`SEMANTIC_ROADMAP.md`，S0–S8 已细化（S7 分为 S7a–S7f，S8 分为 S8a–S8f））+ 语言规范修订（shared/rich/wrapper/String）；M36 落地 S0 诊断基建（`Semantic/Diagnostics.cs`）、M37 落地 S1 符号图内核（`Semantic/Symbols/` + bootstrap + `CanonicalSymbolPrinter`）、M38 落地 S4 BIL 对象模型 + BilWriter（`Bil/`，§20 黄金示例逐行一致）、M39 落地 S2 P1 声明收集（`Semantic/CompilationUnit.cs` + `Semantic/DeclarationCollector.cs`）、M40 落地 S3 P2 声明解析（`Semantic/DeclarationResolver.cs`，七子任务 + Parser 即死拦截移交 P2）、M41 落地 S5 P3 最小闭环（`Semantic/Binder.cs` + `Semantic/Bound/` + `Semantic/NameResolver.cs` 提取 + `Tests/BoundDescribe.cs`，AST → BoundTree）、M42 完成路径表达式统一（表达式位置五节点删除，统一为 `PathExpressionASTNode`，语义上色归 P3）、M43 落地 native 函数机制（SYNTAX §4.6：`native` 修饰符 + `@NativeLibrary`/`@NativeSymbol` 内建注解，P1/P2 全规则校验；BIL §8.4/§8.4.1 声明形态与 §22.5 VM 内建 hook 表；RUNTIME §26 `latte_rt` shim 约定）与 stdlib 内嵌源载入（`Semantic/StdlibSources.cs` + `stdlib/core/Console.latte`），同批落地 Binder 宿主成员查找、符号 Accessibility 与 Bil 段裸条目模型、M44 落地 S6 P4 最小闭环（`Lowering/`：Lowered 节点集 + Lowerer P4a 恒等重写 + BilEmitter P4b 发射，中端四 pass 全通——hello world 端到端出合法 BIL 文本）并以 CLI `--emit-bil`/`--sema-only` 接线收官 S6、M45 落地 S7a P4 基础发射补齐（Lowered 节点补齐八类 + Lowerer 覆盖 S5 全部 Bound 节点 + BilEmitter 新发射 set.var/get/set.field.static/§11 运算/invoke/new + §19.1 标量资源全形态 + `Tests/LoweredDescribe.cs` 与 LowererTests）、M46 落地 S7b（if 语句/表达式 + 值块 + 短路 and/or + 复合赋值，P3/P4 同步——值块标签栈 return@ 绑定 + definite assignment 分支合并；Lowerer session 化短路展开/值块降级与 if 转换/复合赋值脱糖；BilEmitter 多 block §16.2 if 指令）、M47 落地 S7c-1（while/do-while/break/continue 三 pass + 循环协议定稿——循环标签栈 + DA 循环两规则 + 值块穿透 + return@ 隔循环拦截；Lowerer 循环降级 Judge 块 + .breakid 合成局部 .bN（LocalSymbol.Type 可空）；BilEmitter 发射 loop/loop.rev/break/continue + .vars .breakid 条目）、M48 落地 S7c-2（实例成员最小闭环 + core.collections 迭代协议 + for 双形态——this/实例链上色/裸名实例成员补 this（宿主统一 method.Owner）；for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext + Body 头=current）；BilEmitter 开闸 .this/实例 invoke/get.field/set.field/init/operator §8.4 声明 + EmitBuiltinExtMembers；stdlib 三源全量过 P1–P4）、M49 落地 S7d（switch 语句/表达式 + throw 三 pass——异常根 core.Exception 定稿进 bootstrap（IsOpen，具体子类归 S10）；P3 switch 占位 `_` 栈 + 值匹配/pattern 显式分类（常量限定 + 类型严格相等 / pattern 必须 bool）+ throw IsAssignable 到 Exception + GuaranteesReturn 终止口径扩展；P4a 常量 switch 恒等 + pattern 链降级嵌套 if（selector 物化 .sN + 合成 cmp.eq），同批修复 M46 else-if 链值块编织 miscompile（TransformStatements → continuation 编织）；P4b §16.6 switch 指令（switch0-itemN/switch0-default 块 id + .breakid 条目）+ §19.4 switch-table 单行资源跨 fn 去重 + §16.9 throw）、M50 落地 S7e（cast 最小闭环提前自 S8 + try/catch/finally + seq 三 pass——P3 BoundCast/BoundTry/BoundSeq 双形态节点 + catch 类型 IsAssignable 到 Exception + seq 表达式必须产值；P4a ExceptionSlot 合成 + catch 头 cast 编织 + seq 表达式脱糖 + try-finally 部分终止编织拦截；P4b §12.1/§12.2 cast/cast.safe + §16.1 call blk(seqN)（volatile → §9.6 block 修饰符）+ §16.7 try 四操作数 + §19.5 catch-table 多行资源——SYNTAX §7 控制流全部贯通）；M51 落地 S7f-1 字符串插值（spec 定稿：SYNTAX §3.8 toString 机制/插值语义 + RUNTIME §26 原生方法面 toString + BIL §22.5 hook/§11.2 string add 内建拼接；Lexer StringToken.RawContent 定位底稿 + Parser StringInterpolationSplitter 配平截取/子词法/子解析/span rebase 精确回源 + AST 插值段节点；bootstrap String.Add 开放 + Any.toString 承诺/Object open native 默认实现；P3 绑定即规范化（非 String 段包 toString 调用 + 左结合 + 链，P4 零新增节点）；P4a 子类型 cast 物化五位置——ARCH §6.1 首个落地）；M52 收官 S7f（`?.` 安全调用 + `if?` 空值回退 + 解构声明——nullable BIL 语义定稿（§19.1 null 资源类型即 .nullable\<T\>、§12.1 装箱/展开、§13.3 泛型宿主字段替换判定）；BoundSafeAccess/占位叶子 + P4a 物化/unwrap/wrap 脱糖；`if?` Parser 中缀重组 + P3 严格定型；core.Pair 进 .bootstrap.latte + 构造类型成员查找 ConstructedFrom 回退与泛型字段最小替换（S9 前置）——**S7f 四项全部端到端出合法 BIL**）、M53 插值词法帧机制（前端回补，用户决策的架构重构：插值解析从「Parser 侧拆分」改为「Lexer 层栈嵌套」——字符串层遇 `${` 压基础层正常词法，驱动按 token 层大括号计数配平弹回；InterpolationStart/End 标记 token + 驱动插值帧栈 + LiteralParserLayer 段序列状态机；StringInterpolationSplitter 与 RawContent 全部删除；SYNTAX §3.8 单行宿主引号限制解除）、M54 细化 S8 为 S8a–S8f 并落地 S8a（is/supers/with + typeOf 三 pass——SYNTAX §3.5/§3.7 右侧双形态定稿 + castFrom 笔误修正 + §9.2 补 override 行；BoundTypeCheck/BoundTypeOf 两节点 + Binder 不落袋试探双形态解析；P4a 恒等；BilEmitter 首次发射 §12.3 type.is/type.supers/type.with（含三 .indirect）与 §12.5 getid.var/getid.type——六种形态全部端到端出合法 BIL；is .Case 归 S11）；M55 完成中端三树 visitor 化重构（S8b 前置架构重构，用户决策：Binder/Lowerer/BilEmitter 三个 session 巨石（2890/1331/1045 行）按 CRTP visitor 协议全部重写——静态 Visit 统一入口 + Enter/Exit 生命周期模板（栈压弹 finally 固化）+ 双协议（Visit→TResult? 上行合成 / VisitInto 壳填充）+ context 方言（同一函数级状态对象的接口视图，Environment 只读共享）+ 类别分派器唯一 switch + 结构 visitor 簇级分文件（`Semantic/Binding/` + `Lowering/Rewriters/` + `Lowering/Emitting/` 新组织）+ FlowState 提取（DA 的家，S8b 收窄表预留）；行为零变化（40 套件 1924 全绿、BIL 黄金文本逐字节一致、测试零改动、0 新警告）；同批完成 S8b smart cast 语言规则专项定稿（Q1=B 含 const 字段收窄/Q2=A guard/Q3=A and-or-not/Q4=A switch 占位））、M56 落地 S8b smart cast 三 pass 全通（SYNTAX §3.5 完整规则 + §3.4 null 判等段；FieldSymbol.IsConst + const 赋值检查（init 豁免）；FlowState 收窄事实表（NarrowKey 根+const 字段链，纯交集合并）+ ConditionFactsExtractor 真/假边提取 + BoundSmartCastExpression 标记；guard 反向传播/and-or 右侧上下文/while 体真边+体赋值根剔除/switch `(_ is T)` selector 收窄/赋值失效/字段链稳定判定；P4a 物化 LoweredCastExpression，P4b 零新增；SmartCastTests 新套件 55 用例）、M57 完成 BIL 生成全模型对象化重构（用户决策：去魔法 string——BilInstruction 从 opcode 字符串 + 位置操作数列表改为强类型子类族（opcode 拼写/操作数序/多行排版由类固定，`BilSpellings` 拼写唯一定义点）+ 六枚举与 BilModifier 子类族 + switch-table/catch-table 专用资源类 + blk/res 操作数持对象引用；BilWriter 删除 opcode switch；P4b 值发射契约 string → BilVariableOperand；行为零变化——41 套件全绿、BIL 黄金文本逐字节一致）、M58 落地 **BIL 验证器 BilVerifier**（用户决策：不推进语言特性，回补质量基建——提前自路线图 S12；`Bil/BilVerifier.cs` 主文件 + `BilVerificationContext.cs` 模块/函数索引与 canonical 符号解析 + `BilVerifier.Symbols.cs`/`BilVerifier.Types.cs`/`BilVerifier.Flow.cs` 按 §21 类别 partial 分文件，覆盖 §21.1–21.8 静态可判子集（21.9 VM 语义除外；含 .generic< 降级、保守 DA 零误报优先、语言预定义符号表对齐 BootstrapSymbols）；配套 `Tests/BilTestHarness.cs`（EmitBilUnit 共享驱动 + CheckBilValid/CheckBilInvalid + res 重编号形状黄金 CheckFnShape/CheckResShape）与新套件 `Tests/BilVerifierTests.cs`（正例全管线零错误 + §21 逐类负例）；BilEmitterTests 全量迁移（私有 RenderFn/RenderFnAllBlocks/RenderResources 删除，全模块黄金改验证器 + 结构断言），BilWriterTests 黄金保留并对自足模块补验证；CLI `--emit-bil` 接入验证（产出非法即报错不落盘））、M59 落地 S8c 索引访问 + 实例成员完整化三 pass 全通（P3：BoundIndexExpression 读绑 getAtIndex/写绑 setAtIndex + SymbolLookup.FindInstanceOperators + PathVisitors 重构解开全部 S8 归口诊断（表达式底座/后缀折叠/this[i]/容器末段后缀）+ 赋值与复合赋值 place 扩展，多参数索引定稿为编译错误（SYNTAX §13.2 同步）；P4a LoweredIndexExpression 恒等；P4b §13.6 get.array/set.array 发射 + BilVerifier 严格三元组查询（§6.4 精确匹配）；42 套件全绿 + CLI 端到端实测；下一步 S8d（重载解析 + 默认参数 + 具名参数，纯 P3））、M60 落地 S8d 重载解析 + 默认参数 + 具名参数（纯 P3——SYNTAX §4.2 规则定稿：结构过滤（个数/具名/重复）→ 类型适用性（null 仅 Nullable 形参）→ 最具体胜出（逐实参形参类型两两比较）+ 平局打破（填充默认值更少者优先），实例/ext 同池，泛型/可变参数归口，init 同规则；新设施 `Semantic/Binding/OverloadResolution.cs`（source-level ranking 唯一落点，BIL §3.3——静默结构映射 + 无目标类型实参预绑（null 字面量占位、落定以胜者形参定型）+ Materialize 规范序落定）；默认参数三件套（ParameterSymbol.DefaultValue/IsVariadic/IsNamedVariadic + P1 填充 + P2 顺序检查）与声明点绑定（BindingDriver 阶段 1 + BindContext.IsDefaultValueContext 隔离形参与 this（HasThis 统一三处实例上色判定）+ BindEnvironment.ParameterDefaults 记忆化按需绑定——前向依赖声明顺序无关，in-flight 拦依赖环）；调用/init/索引读三处接 Resolve（MatchSingleCandidate 删除，写模式索引重载保持归口——RHS 类型赋值侧才可知）；同批修复位置实参静默覆盖具名占位；BinderTests 新 TestDefaultParameters/TestOverloadResolution 两组 30 用例 + 语义 fuzz 新套件 SemanticsFuzzTests（3000 用例零发现）；43 套件全绿 + CLI --emit-bil 端到端实测（值块默认值调用点物化 .sN 与 if 块）；下一步 S8e（访问控制 + getter/setter + override，纯 P3））、M61 兑现规范定稿批次（用户决策：默认构造 §9.3、循环/catch/finally(e) 变量一律 const §7.3/§8、复合赋值单次求值 §13.2 通用规则（含字段与索引）、语句 seq 作 return@ 目标 §6.1——复合赋值脱糖重写（副作用目标物化 .sN、纯读取 IsSideEffectFree 直通零物化、三处复用同一物化节点）+ BoundSeqExitStatement/LoweredSeqExitStatement 纯控制流标记与 SeqLabels/SeqTargets 双栈（named 专属、隔循环/隔值块拦截、全部 seq 降级压栈防误消费）+ TransformStatements 命中本层消费/外层传播编织，勾销技术债 #11/#15①/#17③/#20①/#17②；43 套件全绿 + CLI 冒烟（副作用调用计数验证单次求值、嵌套 seq exit 传播、try+finally 拦截一致））、M62 完成巨石拆解批次（用户决策的纯重构：`Semantic/DeclarationResolver.cs`（1327 行）visitor 化迁移 `Semantic/Resolution/` 12 文件（阶段级 CRTP visitor）+ `Lexer/LexerLayers.cs` 每类一文件 11 个 + BinderTests（2832 行）/BilEmitterTests/LowererTests 三测试套件 partial 分文件——零行为变化 43 套件全绿）、M63 落地 **S8e 访问控制 + getter/setter + override 检查**（SYNTAX §16.1 可见性判定/§9.4.1 访问器绑定语义/§9.2.1 override 配套定稿；符号六槽（MethodSymbol.IsOpen/IsAbstract/IsOverride/HasBody + FieldSymbol.Getter/Setter/HasBackingStorage）+ SourceFile 文件身份；P1 访问器壳（不进容器 Methods 表）；P2 `Semantic/AccessChecker.cs` 共享设施 + 声明侧接入 + Resolution/ 新 AccessorChecker/OverrideChecker 两阶段（构造宿主签名 Substitute 代入）；P3 使用点访问控制（候选过滤先于 ranking）+ 访问器读写检查与体绑定（value 别名/隐含赋值/自动体合成，Bound 节点形态不变——BIL get.field/set.field 承载）+ 局部访问器归口 S11；P4 声明段开闸（BilAccessorModifier + getter(FIELD)/setter(FIELD) 字段槽驱动 + backing/computed/override/abstract 投影）+ BilVerifier §21.8 增补——访问器样例端到端出合法 BIL；同批修复 SymbolLookup override 遮蔽去重；新 Tests/BinderTests.Access.cs 三组 + 三套件增补共 110 用例，43 套件 2286 全绿）、M64 落地 hint 提示指令（BIL_STANDARD 新 §18 提示指令章 + 全文 §18–§26 重编号为 §19–§27 与全仓库引用/验证器错误码同步；`Bil/BilHintInstruction.cs` 模型 + BilVerifier §21.2/§21.3 检查 + 两套件用例；VISITOR_REWRITE/SMART_CAST_DESIGN 两文档移除——内容已由 SYNTAX §3.5、ARCHITECTURE §6 内联协议摘要与各文件头注释吸收）、M65 完成函数级 Context 组件化拆分（用户决策纯重构：BindContext/LowerContext/EmitContext 三平板巨石 narrow 化为「组合根 + 职责组件类」——Bind 侧新 BindFunctionFrame（只读函数帧）/AccessorBodyState/BindLabelState（四标签栈封装 + return@/break-continue/占位命中查找收编），Lower 侧新 SynthLocalFactory/LowerOutputState/LowerTargetState（五映射栈封装），Emit 侧新 TempVarTable/BlockIdAllocator；设施层签名窄化为组件类型（MemberLookup/TypeReferences/ConstFieldRules/ConditionFacts/NarrowKey 等）；IFlowContext 删除（组件即方言，兑现 M55「按真实隔离需求拉组件」预留）；行为零变化——43 套件全绿、测试零改动）、M66 落地 S8f 收官步（castTo/castFrom 名字分析 + async 边界五项闸门，纯 P3——P1 MethodSymbol.IsAsync 标记位；P2 新 ConversionOperatorChecker（castTo/castFrom 声明形状：零参数/恰一参数/必声明返回类型）与 AsyncGateChecker（声明侧闸门 2 参数/3 返回值/5 泛型约束边界共享安全 + async 仅函数收口）两阶段；P3 BoundCastExpression.Conversion 槽 + SymbolLookup.FindConversionOperator（单泛型参数代入签名匹配）+ CastVisitor 三级转换优先级（源 castTo → 目标 castFrom → 内建）；新 Binding/AsyncGates.cs 调用点闸门 1/2（BoundTree 后置遍历）+ LambdaVisitor 闸门 4（async lambda 捕获 AST 扫描）；43 套件 2342 全绿 + CLI 端到端实测）、M67 完成 S9 细化（S9a–S9f 进 `SEMANTIC_ROADMAP.md`）+ 规范定稿（SYNTAX §4.2/§3.6/§4.3 + RUNTIME §10 传参形态）纯文档步、M68 落地 S9a 函数体内泛型参数放行（纯 P3：三树值层类型契约放宽 `TypeSymbol → SemanticSymbol`、16 处「使用侧泛型归口 S9」gate 解开、语句位置泛型调用静默丢实参漏洞修复、must-return 级联撤销）、M69 落地 S9b 泛型调用绑定（纯 P3：OverloadResolution 候选视图双层代入 + 候选池规则 + BoundCall 携带 TypeArguments + 新 `GenericConstraints.cs` 使用侧约束检查）、M70 落地 S9c 泛型 new（纯 P3：NewVisitor ConstructedFrom 回退 + init 宿主代入）、M71 落地 S9e hidden args 物化（P4a/P4b：fn `.args` 的 `.generic.T = .typeid`、调用点 getid.type 物化/嵌套转发、`.type generic(...)` 子句、BilVerifier 三处适配——泛型函数/调用端到端出合法 BIL）、M72 落地 S9d-1 值可变参数（bootstrap ArrayDefinition + stdlib core::Pair 补 init + BoundVarArgsArgument 打包 + `.vargs.<名>/.kwargs.<名>` 隐藏条目 + 特权构造打包）、M73 收官 S9（S9d-2 泛型可变参数 + S9f stdlib 泛型化 + 技术债勾销：bootstrap MapDefinition + BoundGenericVarArgsArgument/GenericPack 槽 + OverloadResolution 候选池放宽（泛型参数全为可变的泛型方法参与，包实参由值实参推导）+ EmittingDriver `.generic.TArgs/.generic.TValues` 隐藏包形态 + GenericVarArgsEmitter 调用点打包（.array<.typeid<.any>>/.map<.string, .typeid<.any>>）——泛型可变参数端到端出合法 BIL；stdlib collections 泛型化：RangeEnumerator\<T\> 泛型抽象基类（协议级状态机骨架 + abstract moveNext，start_ 归具体实现）+ RangeEnumeratorI32 继承，for 范围循环走泛型路径；同批修复 InheritanceResolver 基类/接口子句宿主泛型参数解析、OverrideChecker 泛型方法覆写同构比较与接口闭包构造实参代入、IsAssignable 沿基类链接口判定（#22⑥）；技术债勾销 #18②/#23③/#23④ + 泛型 operator 名字调用放开（FindInstanceMethods 收 Operator）；43 套件 2413 全绿 + fuzz 6000 + 语义 fuzz 3000）、M74 落地 S10 core.latte 载入机制 + stdlib 扩充（用户决策四件套：① 类型名唯一性按「名 + 泛型元数」判定——`Task` 与 `Task\<TResult\>` 同名共存（P1 重复检测 + NameResolver 查找分流）；② `stdlib/core/coroutine.latte` 自举声明协程运行时面（Task/Task\<TResult\>/Executor 家族/PollingAlarm/EventAlarm/CoroutineLocal\<TValue\> 全 shared abstract 空壳 + `sleep` native + PollingAlarm.isReady abstract）+ §4.6 native 返回类型放宽至用户引用类型（FFI ABI 归 Middleware）；③ `stdlib/core/exceptions.latte` 四异常子类（RuntimeException/IOException/CastException/NoSuchMethodException，`: core.Exception` + 自持 init）+ bootstrap Exception 根程序化携带 protected message 字段 + pub native getMessage()；④ `stdlib/core/disposable.latte`（core.IDisposable）+ P3 async 调用返回类型改写（SYNTAX §4.5 表兑现——调用点 = Task\<T\>/Task，await 仍归 S13）；P4b 同步：§8.4 async 修饰符 + 语句位置 async 调用发 invoke 非 invoke.noret（§15.2）+ BilVerifier 预定义符号表补 getMessage/message + 类型判重键加元数 + async invoke 结果形态校验；43 套件 2467 全绿 + fuzz 6000 + 语义 fuzz 3000；下一步 S11 wrapper/extension/enum struct）、M75 落地 S11 首段（纯 Bil 层规范定稿 + 模型增补——BIL §12.3 type.is.case 判别比较 + §13.3 get.wrapper(.field)+get.field / set.wrapper.field 嵌套字段 place 形态（ARCH §7.1 缺口兑现）+ §8.5/§19.1 判别值注记；IsCaseInstruction/（旧嵌套读指令已移除）/SetWrapperFieldInstruction 模型 + BilVerifier §21.3 校验；同批修复 M74 遗留两处构建/测试问题；43 套件 2497 全绿 + fuzz 6000 + 语义 fuzz 3000；下一步 S11 施工（enum case 先行））、M76 完成**全仓库 review + 39 项 bug 修复批次**（用户决策：不推进语言特性，先回补质量——六模块并行 review 出 30+ 确认发现，两波九组并行修复 + 主代理收口，全部带回归测试、零既有失败：Bil 验证器 5 误报（namespaced 全局函数 receiver/§7.2 值包序/loop.rev DA/try-finally 空 catch 终止/TypeDeclarations 键加元数）；P1/P2 架构级修复——构造类型 BaseType「创建即代入 + InheritanceResolver 后统一回填」（SymbolGraph.BackfillConstructedBaseTypes + CreatesCycle 定义级比较）修复泛型基类两跳断链全症状，TypeReferenceResolver 与 InheritanceResolver 阶段换序（init 映射可达继承内建字段）+ ext 判重/override 元数/命名空间非类型/裸名泛型元数/ext native 闸门；Lowering 7 修复（?. Access 前置语句收 thenBlock miscompile、Nullable\<泛型参数\> null §7.5 投影、IsSideEffectFree 收紧（索引恒非纯/字段按 Getter）、variadic Type 透传与写映射统一、具名包 .kwargs 契约、Enumerator 死代码删除）；前端 4 修复（多行字符串引号串+转义内容错乱（FlushQuoteRun）、插值首段 span、访问器游离修饰符、JSONL 落位类型校验）；Core/Tests 3 修复（LoggerTests 状态保存还原、sema-only↔emit-bil 互斥、输出路径异常友好报错）；P3 流分析 6 修复（smart cast 收窄复活三处不 sound（无 else 交集/循环出口 ClearRoot/TryVisitor 快照合并）、null 非 Nullable 上下文落诊断、值块裸 return 终止、return@ 下钻表达式子树）；P3 调用/闸门 13 修复（复合赋值剥 SmartCast 壳、写模式索引宿主代入 SubstituteForReceiver、Type.instanceMethod 链检查、裸名调用宿主代入、async 闸门包逐元素 + GenericPack 闸门 5、显式泛型约束逐候选、泛型 backing 访问器四处）；§21.8 init 豁免 + const/var 字段修饰符开闸（§8.3 表序：访问 → const/var → ext，BIL_STANDARD 同步）；主代理收口——OverloadResolution 非显式路径统一 ViewOf 宿主代入 + P4b 赋值 place 剥 LoweredCastExpression 壳 + P4a 复合赋值写回值按 place 声明类型物化 cast（§6.5）；43 套件 2699 全绿 + fuzz 6000 + 语义 fuzz 3000；下一步 S11 施工（enum case 先行））、M77 落地 **S11 enum case 全链 + init 映射赋值合成**（SYNTAX §12/§9.3 + RUNTIME §16 + BIL §8.5/§12.3/§14.3 全兑现，规范零修订——三阶段串行：P1 `EnumCaseSymbol` 家族（Owner/Discriminant + ResolvedInit/HoleParameters 模板槽，首例 P3 写符号）+ `TypeSymbol.Cases` 表 + PrintCase + P2 新阶段 EnumCaseResolver（洞独占性/case 名复核/判别值落定）；P3 BindingDriver 阶段 1.5 声明点模板绑定（结构过滤 → 固定实参绑定决胜 → 洞 pub 规则 → 符号落定，泛型 enum 归口）+ `BoundEnumCaseExpression` 与 `BoundTypeCheckExpression` 增 IsCase Kind/Case 槽（天然不触发 smart cast）+ 使用侧三形态（裸 `.Case` expectedType 推断/`.Case(args)` 底座特判/`is .Case` 解归口，switch pattern 自动贯通，`new EnumType(...)` 永久规则 §12.2）；P4 Lowered 恒等 + 洞实参 cast 物化（§14.3 严格匹配）+ 声明段发 BilCaseDeclaration（洞签名 + 判别值 res/auto，与 M75 S11Module 逐点一致）+ new.case/type.is.case 值发射——**enum case 端到端出合法 BIL**；同批裁决落地 §9.3 映射赋值合成（既有 bug 两症状：无体 init 无 fn 定义被 §21.2 拒、有体 init 映射赋值从未合成（stdlib Pair 字段从未写入）——ParameterSymbol.MappedField 槽 P2 回写 + BindingDriver 合成（无体产 body/有体前插）+ enum 无体 init 跳过分支删除统一发射）；43 套件 2817 全绿 + fuzz 6000 + 语义 fuzz 3000；下一步 S11 剩余（wrapper place 绑定 → ext 收尾 + 局部访问器 → proxy 烘焙 → 派发链诊断））、M78 落地**存疑项裁决批次**（M76 review 存疑清单六项用户决策全部落地：① BilVerifier TypesCompatible 收紧 canonical 全等（NormalizeTypeRef + 内建别名表，构造类型递归逐实参——协变注释预留归后续）；② kwargs 体内视角闭环（`Array\<Pair\<String, T\>\>` 统一设施 + bootstrap Array\<T\> 补 getAtIndex/setAtIndex operator + P4a variadic 索引装箱/拆箱物化（读 Type 覆盖 + 拆箱 cast/写按 ABI 元素类型装箱/复合赋值贯通）——`nums[0]`/`options[0].key` 端到端出合法 BIL）；③ 简单赋值求值序对齐复合赋值（接收者先右值后）+ SYNTAX §13.2 UB 句；④ 前端三件套（`>` 系列重组相邻性校验/科学计数法修复（§3.3 增补）/一元 `+` 删除）；⑤ P1/P2 六项（方法判重键加泛型元数/interface 字段禁止/重复 implements 诊断/static operator 禁止/具名 import 同名修正/显式实参拦截全可变包候选 §4.3）；43 套件 2911 全绿 + fuzz 6000 + 语义 fuzz 3000）、M79 落地 **S11 wrapper place 绑定与只读禁令**（纯 P3 步，SYNTAX §14.1/§14.5 兑现——新 `BoundWrapperAccessExpression`（Receiver + Wrapper，Type = Wrapper 定义；只作成员访问接收者，永不作路径绑定结果产出）+ PathVisitors 两处 Colon 归口解开（BindWrapperSegment：双源同池查找（字段/局部符号 AppliedWrappers + 宿主类型 AppliedWrappers 构造回退定义）+ nullable 宿主拒绝 + 只读禁令全拦截面（链末无后缀 Colon 段按赋值/取值一处收口全部逃逸路径；带后缀与非链末即合法接收者））+ 容器路径 Colon 切分（`Type.staticField:W` 静态字段宿主、`Type:W` 无值宿主诊断）+ 局部变量 wrapper 应用 P3 登记（`LocalSymbol.AppliedWrappers` 槽 + 注解解析/类别检查，矩阵 C 恒合法）+ 解构注解归口；AsyncGates 收编 + P4 显式归口（`get.wrapper(.field)+get.field / set.wrapper.field` 与 `.wrapper.` 隐藏字段声明归 proxy 烘焙）+ BoundDescribe 支持；43 套件 2946 全绿 + fuzz 6000 + 语义 fuzz 3000；下一步 S11 剩余：ext 收尾 + 局部访问器解归口 → proxy 烘焙 lowering → 派发链诊断工具）、M80 落地 **S11 ext 收尾**（按序推进无用户决策项——P4b 修复 `EmitBuiltinExtMembers` 随迁访问器声明（内建 ext 字段 + 访问器此前被 §21.2 拒绝落盘，SYNTAX §4.4 示例形态实测复现后修复）+ P2 两闸门收口（ExtensionRegistrar：ext 字段禁注 interface（§11 成员禁令 ext 路径）+ ext 实例字段同受 §3.1.1 闭包表（`FieldClosureChecker.CheckExtensionField` 完整复用 CheckClosureField）——违规不注册与判重同口径）+ 端到端样例勾销技术债 #22④（`Tests/BilEmitterTests.Ext.cs` 新 partial 五组：实例字段读写/方法调用/backing 访问器/内建 computed 访问器/static 三形态/复合赋值）+ SYNTAX §4.4 补成员语义注记（注册后与目标成员同规则——默认 private/闭包表/共享闸门/interface 禁字段）+ 技术债 #26 登记（priv/protected ext 可见性、ext static 明文、ext 泛型目标三事待裁决）；43 套件 2978 全绿 + fuzz 6000 + 语义 fuzz 3000、M81 落地 S11 proxy 烘焙细化（S11 剩余拆为 S11a–S11g 七子步 + 烘焙形态三项定稿：声明侧烘焙骑 vtable/特化体独立合成 fn 带 `wrapper-proxy(PROXY_KIND)` 修饰符且最终内联归 Middleware/特化符号 P2 合成·P3 逐组合绑定·P4 零 wrapper 语义 + 技术债 #26 三事裁决：ext static 明文、priv·protected ext 可见性按声明位置、ext 泛型目标元数与歧义诊断——纯文档，规范修订落 SYNTAX §4.4/§14.2/§16.1 + BIL §8.4/§5.1 + RUNTIME §14/§15 + ARCH §5.2/§6.1）、M82 落地 S11a（P2 形状校验与符号合成：AppliedWrappers 升级 WrapperApplication 记录（TTarget 代入显形）+ ProxyShapeChecker（元数/类别矩阵/canonical shape）+ ProxyDispatchResolver（`.wrapper.` 隐藏字段合成 + Entity 派发链计算 + 特化/原始体符号合成）+ 发射闸门（「.」前缀名与 IsCompilerGenerated 字段跳过归 S11c/S11d）；3026 全绿）、M83 落地 S11b（P3 proxy 体逐组合绑定——BindingDriver 阶段 2 分流（`.proxy.` 声明体收集、被拦截成员含访问器用户体改挂 WrappedBodySymbol）+ 阶段 2.5 三件套：转发壳（原名 fn body = invoke 链首）/特化体（新组件 Binding/ProxyBodyState.cs 组合语境 + wildcard 前奏物化——symbol canonical 常量/namedArgs·unnamedArgs 双包打包（BoundVarArgsArgument 复用）/get 的 value = invoke 下一环）/wildcard 解包 shim（`.proxy.unwrap.<序>.<键>`，P2 同批合成双包参签名，body = 逐元素 cast 解包 invoke 下一环）；self = 宿主角色 this（TTarget 代入结果，零泛型报错）、inner = 下一环普通调用（OverloadResolution 单候选复用）、proxy 体 this 重写 BoundWrapperAccessExpression（只读 place，裸 this 禁令同 M79 族）、非 proxy 语境 self/inner 专门诊断、诊断按 (proxy, span, message) 去重（BindEnvironment.CurrentProxy）；P4 闸门 LoweringDriver/EmittingDriver 跳过合成 fn 与转发壳——`--sema-only` 零诊断、`--emit-bil` 归 S11d 拦截不落盘；可变参数成员不拦截建链（技术债 #27⑦）；3057 全绿）、M84 落地 S11c（P4a/P4b wrapper place 成员访问，解 M79 归口——BoundWrapperAccessExpression 携带命中应用记录（Application 槽）+ 新设施 `Lowering/Rewriters/WrapperPlaceLowering.cs` 按应用类别分派（Entity：成员读/调用/索引读 = get.wrapper 值拷贝链、字段写 = set.wrapper.field 链；字段-Value：字段读写 = wrapper 写链，宿主取字段属主对象）+ 复合赋值读写分离（宿主单次求值共享）+ `.wrapper.` 隐藏字段 §8.3.1 声明开闸（priv var backing compiler-generated）+ 深层写穿/索引写/局部与静态存储显式归口（归 S11g 复核）；3094 全绿）、M85 落地 **S11d P4b 合成 fn 发射，烘焙端到端**（Bil 模型 `BilProxyKind` 四态 + `BilWrapperProxyModifier`（§8.4 `wrapper-proxy(PROXY_KIND)`）+ LocalSymbolEmitters 闸门改分流（烘焙产物发射、proxy 声明模板不进 BIL）与修饰符投影 + 双驱动闸门删除（特化/原始体/转发壳/解包 shim 平铺）+ BilVerifier §21.8 适配（保留名 ↔ 修饰符双向校验 + kind ↔ 名段一致）+ wildcard 具名包 ABI 类型修复（§14.7 `Array\<Pair\<String, Any\>\>`）——specific/wildcard/get 访问器链三样例端到端出合法 BIL（invoke 原名 → 特化链 → 原始体）；3127 全绿）、M86 落地 **S11e `call???` 降级全链**（SYNTAX §14.7 + BIL §15.4 兑现——规范落地修订：RUNTIME §14.2 泛型逻辑签名实质化为**非泛型胖值签名**（Any.call??? 默认实现/router/降级特化三合成符号统一 `(symbol: String, namedArgs: Array\<Pair\<String, Any\>\>, unnamedArgs: Array\<Any\>): Any`，独立 typeid 包取消——Any 胖值自描述 typeid）；P2 ComputeDowngradeChains（router = 宿主成员 `call???` + 逐应用 `.proxy.<序>.???` 特化 + Any.call??? 幂等合成）+ PrintDowngradeRequest 请求 symbol 定稿；P3 降级判定（BindInstanceMethodCall candidates 空分支沿 BaseType 链找 router + symbol 字面量 + 双包打包 + ResultType=Any）+ inner 自动补 symbol（合成具名实参）+ BindingDriver 阶段 2.6（router 直通体/特化零前奏绑定/Any.call??? 体 throw NoSuchMethodException）+ 类型兼容五检查豁免（BoundAnalysis.IsDowngradeCallResult 单点——转换骑 P4a §6.5 cast 物化，失败抛 CastException）；P4 双闸门删除 + wrapper-proxy(router) 投影；BilVerifier §21.8 放行 call???↔router + §21.2 builtin 宿主 fn 定义豁免——未声明方法降级端到端出合法 BIL；3196 全绿）、M87 落地 S11f 派发链诊断工具（CLI `compile --file a.latte --explain-dispatch`——`Semantic/DispatchExplainer.cs` 报告编译单元全部烘焙链（被修饰成员 outer→inner 每层命中 specific|wildcard + canonical symbol）与降级路由（router + `.proxy.<序>.???` 链 + Any.call???），与 --sema-only 互斥，RUNTIME §15 形态兑现）；M88 落地 **wrapper 烘焙架构反转**（用户推翻 M81①③：烘焙/派发链/隐藏存储/call??? 路由体归 Middleware，编译器只携带标记——删 ProxyDispatchResolver 与合成符号槽，新 ProxyMatching，proxy 模板态 + get.self/invoke fn(..inner)，BilProxyKind 两态 + wrapped(W)，Any.call??? 为 VM hook 内建；同批 #26 代码落地 + #27⑧/M79 param:W；约 3119 + fuzz 6000 + 语义 fuzz 3000，44 套件全绿）；S11g 已收口，#27⑦/M84 三项/#28 保持归口下一对话专项；局部访问器用户决策（2026-08-06）走路线 C 随 S13 lambda 闭包机制落地，移出 S11 序列——决策由 PROGRESS_REPORT 技术债 #22① 承载）
**最新进度**: M103 完成 S13 lambda 对象模型全链（SYNTAX §5.2）：隐藏类 `..lambda..UUID` 继承 Func/Action/AsyncFunc/AsyncAction；捕获全 Cell 化；`invoke.indirect`→`$$call`；删 methodid；`ClosureStoragePlan`/`CallableModel`；值块体降级端到端出合法 BIL。归口：循环/catch/using 变量捕获、`(act)()`。
**版本控制**: Git（`main` 分支，2026-07-17 首次提交，工作树干净；CI 见 `.github/workflows/ci.yml`）——⚠️ **仓库根在内层 `LatteCompiler/LatteCompiler/`（`.git` 在此），外层目录只放 `LatteCompiler.sln`，不是仓库**；`dotnet build`/`dotnet run` 等工作目录同样是内层

---

## 1. 项目概述

Latte 是一门现代的、类型安全的编程语言，本仓库是它的编译器。语言设计目标：

- **完全具化的泛型**：运行时类型信息完全保留（reified），不擦除
- **协程为核心**：从 `main` 开始的原生协程支持
- **值类型/引用类型分离**，`rich`/`shared` 类型声明修饰符
- **Wrapper 系统**：类似 Python 装饰器 + Java 注解的修饰器机制
- **无运算符优先级**：所有运算必须用括号明确指定（见 §4.1）

编译器目标架构：

```
Latte 源码 (.latte) → Frontend (Lexer + Parser) ✅ 完成（含大扫除重构） → 语义分析 ← 当前阶段
                    → BIL (Basic Intermediate Language)
                    → Middleware (LLVM IR Generator)
                    → LLVM 工具链 → 原生可执行文件
```

**当前进度**：编译器前端（Lexer + Parser）已完成，且经过一次彻底的架构大扫除（见 §4.7）：控制流系统与 AST 施工系统严格分离，Layer 之间只传递控制权不传递 AST 数据。已可解析字面量、类型引用、变量声明（含 getter/setter 属性访问器）、完整表达式（含 Lambda（单表达式/多语句块体 + named）、if/switch 表达式（分支体为代码块，`return@_`/named 取值）、typeOf/as/is、seq 表达式形态、await、前导点 enum case 引用、wrapper 路径访问 `:`）、完整语句系统（代码块、if、switch 语句、循环、try-catch-finally、seq、throw、yield、return/break/continue、赋值；lambda 体内裸 return 为编译错误）、泛型参数列表、函数形参列表（含 init `_ -> field` 参数映射）、统一声明层（全局字段/函数、class/interface/struct/wrapper 声明、成员方法与 init/operator、继承与 implements、like 委托、ext 限定名、嵌套类型、声明上的泛型参数、enum struct 的 `[]` case 列表）、wrapper 主体（`@` 注解/wrapper 应用、`@WrapperTarget(.X)` 类型标识、`.proxy.*` 代理成员）、模块系统（import §15.2 三种形态、namespace 声明 §15.1）。**中端基建与 pass 进度**：三条基建线已就位（M36–M38）——可恢复诊断模型（Diagnostic/DiagnosticBag）、符号图内核（驻留 + bootstrap + canonical 打印）、BIL 对象模型 + 文本生成；P1 声明收集（M39）、P2 声明解析（M40）、P3 函数体分析最小闭环（M41：Binder → BoundTree，含 LocalSymbol、NameResolver 共享设施、BoundDescribe）与 P4 最小闭环（M44：Lowerer P4a 恒等重写 + BilEmitter P4b 发射，`Lowering/`，含 M43 stdlib 内嵌源同走 P1–P4）已落地——**四 pass 全通，hello world 端到端出合法 BIL 文本**（BilEmitterTests 套件黄金对照 + CLI `--emit-bil` 实测逐行一致）；M45（S7a）补齐 P4 基础发射——**P3 能绑定的全部 Bound 节点均已端到端过 P4**（赋值/运算/带返回值调用/new + §19.1 标量资源全形态 + LoweredDescribe/LowererTests 基建）；M46（S7b）落地 if 语句/表达式 + 值块 + 短路 and/or + 复合赋值（P3/P4 同步：Bound 五节点 + 值块标签栈 return@ 绑定 + DA 分支合并；Lowerer session 化脱糖；BilEmitter 出多 block BIL）；M47（S7c-1）落地 while/do-while/break/continue（循环标签栈 + DA 循环两规则 + 值块穿透；P4a Judge 块机制 + .breakid 合成局部；P4b loop/loop.rev/break/continue 发射）；M48（S7c-2）落地实例成员最小闭环 + core.collections 迭代协议 + for 双形态统一脱糖（this/实例链/裸名实例成员/for 绑定；P4b 实例发射开闸；stdlib 三源全量过 P1–P4）；M49（S7d）落地 switch 语句/表达式 + throw（P3/P4 同步：异常根 core.Exception 定稿进 bootstrap；P3 占位 `_` 栈 + 值匹配/pattern 分类 + throw 兼容性；P4a pattern 链降级 + M46 值块编织 miscompile 修复；P4b §16.6 switch + §19.4 switch-table + §16.9 throw）；M50（S7e）落地 cast 最小闭环（提前自 S8）+ try/catch/finally + seq 双形态（P3/P4 同步：BoundCast/BoundTry/BoundSeq 节点 + catch 兼容性 + seq 必须产值；P4a ExceptionSlot + catch 头 cast + seq 脱糖 + try-finally 编织拦截；P4b cast/cast.safe + call blk(seqN) + try 四操作数 + catch-table——SYNTAX §7 控制流全部贯通）；M51（S7f-1）落地字符串插值（toString 机制 spec 定稿（SYNTAX §3.8 + RUNTIME §26 原生方法面 + BIL §22.5 hook） + String 内建拼接开放 + Lexer RawContent/Parser 插值拆分（span 精确回源）/AST 插值段 + P3 绑定即规范化（非 String 段包 toString 调用 + 左结合 + 链，P4 零新增节点）+ P4a 子类型 cast 物化五位置——插值端到端出合法 BIL）；M52（S7f 收官）落地 `?.` 安全调用（BoundSafeAccess/占位叶子 + P4a 物化/unwrap/wrap 脱糖）、`if?` 空值回退（Parser 中缀 if 重组 + P3 严格定型 + if 双分支延迟求值脱糖）与解构声明（core.Pair 进 .bootstrap.latte 自举 + Parser 解构分支 + 构造类型成员查找 ConstructedFrom 回退与泛型字段最小替换（S9 前置）+ P4a 物化/逐字段读取），同批定稿 nullable BIL 语义（§19.1/§12.1/§13.3）——**S7f 四项全部端到端出合法 BIL**；M53 插值词法帧机制（前端回补：Lexer 层栈嵌套解析插值——字符串层遇 `${` 压基础层正常词法，驱动按 token 层大括号计数配平弹回；M51 的 Parser 侧拆分器与 RawContent 全部删除，SYNTAX §3.8 单行宿主引号限制解除，AST 与 P3/P4 零改动）；M54（S8a）落地 is/supers/with + typeOf 三 pass（S8 已细化为 S8a–S8f：smart cast/索引与实例成员完整化/重载与默认参数/访问控制与 getter-setter/castTo-castFrom 与 async 闸门——规范定稿随步落地：SYNTAX §3.5/§3.7 右侧双形态解析规则；P3 BoundTypeCheck/BoundTypeOf 两节点 + 不落袋试探双形态绑定；P4a 恒等；P4b 首次发射 §12.3 六形态与 §12.5 getid——端到端出合法 BIL）；M55 完成中端三树 visitor 化重构（S8b 前置：三个 session 巨石按 CRTP visitor 协议全部重写——静态 Visit 统一入口 + Enter/Exit 生命周期模板 + 双协议 + context 方言接口视图 + 类别分派器 + 结构 visitor 簇级分文件，`Semantic/Binding/`、`Lowering/Rewriters/`、`Lowering/Emitting/` 新组织；FlowState 提取为独立组件（S8b 收窄表的家）；行为零变化、测试零改动）；M56 落地 S8b smart cast 三 pass 全通（定稿落地：SYNTAX §3.5 完整规则 + §3.4 null 判等；FieldSymbol.IsConst 前置；FlowState 收窄表 + ConditionFactsExtractor + BoundSmartCastExpression 标记 + P4a 物化；guard/and-or 上下文/循环/switch 占位/失效/字段链全规则落地；SmartCastTests 55 用例 + CLI 端到端三样例 BIL 核对）；M57 完成 **BIL 生成全模型对象化重构**（用户决策：去魔法 string——`BilInstruction` 强类型子类族（`Bil/BilInstructions.cs` 基类 + Compute/Data/ControlFlow 三指令文件，opcode 拼写/操作数序/§16.6/§16.7 多行排版由类固定）+ `BilSpellings` 全部拼写唯一定义点 + 六枚举（BilTypeKind/BilMemberKind/BilBlockModifier/BilAccessibility/BilKeyword/BilScalarType）与 BilModifier 子类族 + `BilSwitchTableResource`/`BilCatchTableResource` 专用资源类（header/元素自渲染）+ blk/res 操作数持对象引用（悬空引用不可构造）；BilWriter 删除 opcode switch（指令自渲染 WriteTo）；P4b 值发射契约 string → `BilVariableOperand`；行为零变化——黄金文本逐字节一致、测试用例数不变、CLI 样例 diff 字节一致）；M58 落地 **BIL 验证器 BilVerifier**（用户决策：不推进语言特性、回补质量基建，提前自路线图 S12——`Bil/` 五新文件按 §21 类别 partial 分文件，覆盖 §21.1–21.8 静态可判子集（含 .generic< 降级、保守 DA 零误报优先、预定义符号表对齐 BootstrapSymbols、entrypoint 结构化终止判定）；测试基建 `Tests/BilTestHarness.cs`（共享全管线驱动 + 验证器断言 + res 重编号形状黄金）与新套件 BilVerifierTests（正例全管线零错误 + §21 逐类负例）；BilEmitterTests 全量迁移（私有渲染器与全模块黄金删除，改验证器 + 形状黄金 + 结构断言），BilWriterTests 黄金保留并对自足模块补验证；CLI `--emit-bil` 接入验证——产出非法即报错不落盘）；M59 落地 S8c 索引访问 + 实例成员完整化三 pass 全通（BoundIndexExpression 读/写分型 + FindInstanceOperators + PathVisitors 重构解开全部 S8 归口诊断 + 赋值/复合赋值 place 扩展；多参数索引定稿编译错误（SYNTAX §13.2）；P4b §13.6 get.array/set.array + BilVerifier 严格三元组查询）；M60 落地 S8d 重载解析 + 默认参数 + 具名参数（纯 P3——SYNTAX §4.2 规则定稿（结构过滤 → 类型适用性 → 最具体胜出 + 默认值填充数平局打破；实例/ext 同池；泛型/可变参数归口；init 同规则）；新设施 `Semantic/Binding/OverloadResolution.cs`（source-level ranking 唯一落点，BIL §3.3）；默认参数声明点绑定（BindingDriver 阶段 1 + BindContext.IsDefaultValueContext 隔离形参与 this）+ 调用点规范序填充（BindEnvironment.ParameterDefaults 记忆化按需绑定，前向依赖声明顺序无关）；同批修复位置实参静默覆盖具名占位；语义 fuzz 新套件 SemanticsFuzzTests）；M61 兑现规范定稿批次（默认构造/循环与异常变量一律 const/复合赋值单次求值（含字段）/语句 seq 作 return@ 目标——P4a 物化临时变量与 LoweredSeqExit 标记编织，勾销技术债五项）。M62 完成巨石拆解批次（用户决策纯重构：DeclarationResolver（1327 行）visitor 化迁移 `Semantic/Resolution/` 12 文件（阶段级 CRTP visitor + ResolveEnvironment 只读环境）+ LexerLayers 每类一文件 11 个 + 三测试套件 partial 分文件，零行为变化 43 套件全绿）。M63 落地 S8e 访问控制 + getter/setter + override 检查（方案 A 三项一次落地：SYNTAX §16.1/§9.4.1/§9.2.1 定稿；符号六槽 + SourceFile 文件身份 + bootstrap 统一 Public；P2 AccessChecker 共享设施 + AccessorChecker/OverrideChecker 两新阶段；P3 使用点访问控制（候选过滤先于 ranking）+ 访问器读写检查与体绑定（Bound 节点形态不变，BIL get.field/set.field 承载）+ 局部归口 S11；P4 声明段开闸（getter(FIELD)/setter(FIELD)/backing/computed/override/abstract 投影）+ BilVerifier §21.8 增补——访问器样例端到端出合法 BIL；43 套件 2286/2286 + fuzz 6000 + 语义 fuzz 3000 全绿；下一步 S8f（castTo/castFrom 名字分析 + async 边界五项闸门，纯 P3））。M64 落地 **hint 提示指令**（规范定稿 + 模型与验证器落地：BIL_STANDARD 新 §18——仅 block 内、string 资源 JSON 负载 schema 留白、纯位置标记不参与 DA/控制流、删除全部 hint 可观察行为不变（§22.2）、VM no-op、Middleware 可用可忽略且内容不得影响语义；§18–§26 重编号为 §19–§27，全仓库 § 引用与 BilVerifier 错误码 “20.x”→“21.x” 同步；§26/§27 扩展清单收口）+ `Bil/BilHintInstruction.cs`（HintInstruction 模型）+ BilVerifier §21.2 资源归属/§21.3 string 标量限定与 ClassifyVariables 零读写分类 + BilWriterTests §18 黄金与 BilVerifierTests 三用例；VISITOR_REWRITE 与 SMART_CAST_DESIGN 两文档移除（内容已由 SYNTAX §3.5、ARCHITECTURE §6 内联协议摘要与各文件头注释吸收；PROGRESS_REPORT 历史段落中的提及保留为编年史）。M65 完成**函数级 Context 组件化拆分**（用户决策纯重构：BindContext 拆为 BindFunctionFrame（只读函数帧）/AccessorBodyState（访问器体状态）/BindLabelState（值块/循环/switch 占位/seq 标签四栈封装 + 命中查找领域方法）+ Flow/Locals 留根部，LowerContext 拆为 SynthLocalFactory（.sN/.bN 工厂）/LowerOutputState（前置语句机制封装）/LowerTargetState（五映射栈封装 + 命中查找收编）+ Method/TransformFailed 留根部，EmitContext 拆为 TempVarTable（.tN 工厂）/BlockIdAllocator（block id 分配）+ Function 留根部；裸 Stack/元组全部封装为语义方法；设施层签名窄化为组件类型（MemberLookup/TypeReferences/ConstFieldRules/ConditionFacts/NarrowKey 等——真编译期边界，跨组件消费者保持组合根）；IFlowContext 删除（组件即方言，兑现 M55「按真实隔离需求拉组件、禁止切多个独立状态对象」预留）——零行为变化 43 套件全绿、测试零改动）；M66 落地 S8f 收官步（castTo/castFrom 名字分析 + async 边界五项闸门，纯 P3——P1 MethodSymbol.IsAsync；P2 ConversionOperatorChecker（转换声明形状）/AsyncGateChecker（声明侧闸门 2/3/5 + async 仅函数）两新阶段；P3 BoundCastExpression.Conversion + FindConversionOperator（源 castTo → 目标 castFrom → 内建三级优先级）+ AsyncGates 调用点 1/2 + LambdaVisitor 捕获 4；43 套件 2342 全绿 + CLI 端到端实测；下一步 S9 泛型）。M67 完成 S9 细化（S9a–S9f 进 `SEMANTIC_ROADMAP.md`）+ 规范定稿（SYNTAX §4.2 泛型方法调用/§3.6 使用侧约束/§4.3 泛型可变参数 + RUNTIME §10 传参形态）纯文档步；M68 落地 S9a 函数体内泛型参数放行（纯 P3：三树值层类型契约放宽 `TypeSymbol → SemanticSymbol`、16 处「使用侧泛型归口 S9」gate 解开、语句位置泛型调用静默丢实参漏洞修复、must-return 级联撤销）；M69 落地 S9b 泛型调用绑定（纯 P3：OverloadResolution 候选视图双层代入 + 候选池规则 + BoundCall 携带 TypeArguments + 新 `GenericConstraints.cs` 使用侧约束检查）；M70 落地 S9c 泛型 new（纯 P3：NewVisitor ConstructedFrom 回退 + init 宿主代入）；M71 落地 S9e hidden args 物化（P4a/P4b：fn `.args` 的 `.generic.T = .typeid`、调用点 getid.type 物化/嵌套转发、`.type generic(...)` 子句、BilVerifier 三处适配——泛型函数/调用端到端出合法 BIL）；M72 落地 S9d-1 值可变参数（bootstrap ArrayDefinition + stdlib core::Pair 补 init + BoundVarArgsArgument 打包 + `.vargs.<名>/.kwargs.<名>` 隐藏条目 + 特权构造打包）；M73 收官 S9（S9d-2 泛型可变参数 + S9f stdlib 泛型化 + 技术债勾销——bootstrap MapDefinition + BoundGenericVarArgsArgument/GenericPack 槽 + OverloadResolution 候选池放宽（泛型参数全为可变的泛型方法参与，包实参由值实参推导）+ EmittingDriver `.generic.TArgs/.generic.TValues` 隐藏包形态 + GenericVarArgsEmitter 调用点打包（.array<.typeid<.any>>/.map<.string, .typeid<.any>>）——泛型可变参数端到端出合法 BIL；stdlib collections 泛型化：RangeEnumerator\<T\> 泛型抽象基类（协议级状态机骨架 + abstract moveNext，start_ 归具体实现）+ RangeEnumeratorI32 继承，for 范围循环走泛型路径；同批修复 InheritanceResolver 基类/接口子句宿主泛型参数解析、OverrideChecker 泛型方法覆写同构比较与接口闭包构造实参代入、IsAssignable 沿基类链接口判定（#22⑥）；技术债勾销 #18②/#23③/#23④ + 泛型 operator 名字调用放开（FindInstanceMethods 收 Operator）；43 套件 2413 全绿 + fuzz 6000 + 语义 fuzz 3000）、M74 落地 S10 core.latte 载入机制 + stdlib 扩充（用户决策四件套：① 类型名唯一性按「名 + 泛型元数」判定——`Task` 与 `Task\<TResult\>` 同名共存（P1 重复检测 + NameResolver 查找分流）；② `stdlib/core/coroutine.latte` 自举声明协程运行时面（Task/Task\<TResult\>/Executor 家族/PollingAlarm/EventAlarm/CoroutineLocal\<TValue\> 全 shared abstract 空壳 + `sleep` native + PollingAlarm.isReady abstract）+ §4.6 native 返回类型放宽至用户引用类型（FFI ABI 归 Middleware）；③ `stdlib/core/exceptions.latte` 四异常子类（RuntimeException/IOException/CastException/NoSuchMethodException，`: core.Exception` + 自持 init）+ bootstrap Exception 根程序化携带 protected message 字段 + pub native getMessage()；④ `stdlib/core/disposable.latte`（core.IDisposable）+ P3 async 调用返回类型改写（SYNTAX §4.5 表兑现——调用点 = Task\<T\>/Task，await 仍归 S13）；P4b 同步：§8.4 async 修饰符 + 语句位置 async 调用发 invoke 非 invoke.noret（§15.2）+ BilVerifier 预定义符号表补 getMessage/message + 类型判重键加元数 + async invoke 结果形态校验；43 套件 2467 全绿 + fuzz 6000 + 语义 fuzz 3000；下一步 S11 施工（enum case 先行，M75 已定稿 BIL 侧 type.is.case/set.wrapper.field place 形态）。M75–M88 各段见 `docs/PROGRESS_REPORT.md` 里程碑总览（S11 BIL 定稿/全仓 review 修复/enum case 全链/存疑裁决/wrapper place 绑定/ext 收尾/S11a–S11g 细化与 ext 三事裁决/proxy 烘焙四步（S11a–S11d）/call??? 降级全链（S11e）/派发链诊断（S11f）/**M88 烘焙架构反转**（标记归 frontend、烘焙归 Middleware）+ #26 代码落地）。

---

## 2. 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、0 警告
dotnet clean
```

唯一配置文件是 `LatteCompiler.csproj`（无 NuGet 第三方依赖，纯 BCL）。另有 `LatteCompiler.sln`。

### 2.2 运行

CLI 结构为 `<COMMAND> [--sub-cmd [args...]...]`，顶层 COMMAND 三个：`compile` / `test` / `help`（M27 起，交互菜单已删除）。裸 `dotnet run` 等价于 `help`。

```bash
dotnet run -- test --all                 # 全量测试（CI 入口；任意失败非零退出码并列出失败套件名）
dotnet run -- test                       # 打印测试套件菜单（编号 + 名称）
dotnet run -- test --run 1 7             # 按编号运行指定套件（字面量 + 形参列表）
dotnet run -- compile --file a.latte                    # 编译（语义分析 P1–P3 + 诊断输出；无后端子命令时只到语义）
dotnet run -- compile --file a.latte --parse-only       # 只解析，AST 以 JSONL 输出到 stdout
dotnet run -- compile --file a.latte --parse-only --dump-ast ast.jsonl   # AST JSONL 写文件
dotnet run -- compile --file a.latte --sema-only        # 只跑语义分析（P1–P3），输出诊断后结束
dotnet run -- compile --file a.latte --emit-bil a.bil   # 语义通过后发射 BIL 文本写文件（M44；M58 起先经 BilVerifier 验证，非法即报错不落盘）
dotnet run -- compile --file a.latte --explain-dispatch # 派发链诊断报告（M87 S11f：烘焙链 + 降级路由，RUNTIME §15）
dotnet run -- help                       # 全部 COMMAND 与子命令概览（文本由注册表程序生成）
dotnet run -- help compile               # 单个 COMMAND 详情
dotnet run -- help compile.file          # 单个子命令详情（子命令名不带 -- 前缀）
```

诊断子命令（`compile` 与 `test` 共有，可组合）：

```bash
dotnet run -- test --all --verbose      # 控制台输出 verbose 级日志（默认只显示 Warning+）
dotnet run -- test --all --log-to run.jsonl   # 全量日志（含 verbose）以 JSONL 落盘
```

---

## 3. 代码库结构

```
LatteCompiler/
├── Program.cs                # 薄入口：命令行解析 → 分发 → 退出码（M27 起无交互菜单）
├── LatteCompiler.csproj      # net8.0，Exe，Nullable enable
├── AST/                      # AST 节点定义（按类别分文件）
│   ├── ASTNode.cs               # AST 节点基类 + RootASTNode（M30 迁出 Utilities.cs）
│   ├── SymbolNodes.cs           # 符号结构（Symbol/SymbolElement/SymbolASTNode）
│   ├── ImportNodes.cs           # import 声明节点（ImportASTNode + [AstCarrier] ImportItem）
│   ├── LiteralNodes.cs          # 字面量节点（LiteralASTNode 基类 + Int/Float/String/Bool/Null 等）
│   ├── TypeNodes.cs             # 类型引用节点
│   ├── DeclarationNodes.cs      # 声明节点（变量声明等）
│   ├── ExpressionNodes.cs       # 表达式节点（含 ExpressionRootASTNode 挂载点、
│   │                            #   PathExpression 统一路径节点四件套，M42）
│   ├── StatementNodes.cs        # 语句节点（代码块/if/循环/return/赋值等）
│   ├── ASTIntegrityValidator.cs # AST 完整性验证器（Parse 成功后自动运行，[ChildAstNode]/[AstCarrier] 标注驱动；
│   │                            #   含 Span 校验与「未标注 AST 成员」类型审计，M28）
│   ├── ASTVisitor.cs            # 统一 AST 遍历基建（[ChildAstNode] 子节点枚举唯一实现，M28）
│   ├── AstJsonlSerializer.cs   # AST 树 JSONL 序列化 v2（carrier 记录化、字段名键控，--dump-ast 输出）
│   └── AstJsonlDeserializer.cs # JSONL → AST 完整反序列化（M31，产物强制过 Validator）
├── Parser/                   # Parser 层实现（每层一个文件）
│   ├── Parser.cs                # 核心协议：IParserLayer、ParserLayerResult、
│   │                            #   TokenDisposition、ParserLayerContext、Parser 主循环
│   ├── Keywords.cs              # 关键字常量（Lexer 不区分关键字，由 Parser 比对识别，M30）
│   ├── RootParserLayer.cs       # 解析入口层，负责识别顶层结构并委托
│   ├── LiteralParserLayer.cs    # 字面量 + 插值段序列状态机（M53：InterpolationStart
│   │                            #   委托 ExpressionParserLayer 就地填充段 Root，§3.8）
│   ├── TypeReferenceParserLayer.cs  # 类型引用（不含 rich/shared，见 §4.2）
│   ├── VariableDeclarationParserLayer.cs
│   ├── ExpressionParserLayer.cs # 表达式框架（识别 + 运算符 + 委托）
│   ├── PathParserLayer.cs       # 符号路径（M42 起收窄为类型引用与 import 路径专用）
│   ├── DeclarationParserLayer.cs / ImportParserLayer.cs / CodeBlockParserLayer.cs
│   ├── GenericParametersParserLayer.cs  # 泛型形参列表
│   ├── ParameterListParserLayer.cs      # 函数形参列表
│   ├── ArgumentListParserLayer.cs       # 调用实参列表
│   ├── LambdaExpressionParserLayer.cs   # Lambda 表达式（含 async、trailing；体双形态：单表达式/块，named）
│   ├── IfStatementParserLayer.cs        # if 表达式（分支体为代码块 + named）+ if 语句（else if 链）
│   ├── SwitchStatementParserLayer.cs    # switch 表达式 + switch 语句（分支体为代码块，强制 default）
│   ├── TypeOfExpressionParserLayer.cs   # typeOf 表达式
│   ├── LoopParserLayer.cs               # 循环（for/while/do-while/named 标签）
│   ├── SeqBlockParserLayer.cs           # seq 块（volatile/using/named，语句+表达式双形态）
│   ├── TryCatchFinallyParserLayer.cs    # try/多 catch/finally(e)
│   ├── PropertyAccessorParserLayer.cs   # 属性访问器块 { get... set... }（§9.4）
│   ├── NamespaceParserLayer.cs          # namespace 声明（§15.1）
│   └── DeclarationParserLayer.cs        # 统一声明层：全局/成员/嵌套任何声明（P3）
├── Lexer/                    # 词法分析
│   ├── Lexer.cs                 # Tokenize(TextReader/string) 入口
│   ├── Tokens.cs                # Token 定义（TokenType + Word/String/Notation/Comment/LineBreak/EndOfFile，M30）
│   ├── Notations.cs             # 符号常量（单字符/多字符记号，M30）
│   ├── StringEscape.cs          # 共享转义表（M62 自 LexerLayers.cs 拆出）
│   └── 各 LexerLayer 每类一文件（M62：CommentBlock/CommentLine/Word/String/Char/
│                                #   MultilineString/Notation/Slash/Quote/Base 十层）
├── Core/                     # 基础设施
│   ├── Exceptions.cs            # LexerException / ParserException（用户源码错误，M30）
│   ├── CommandLine.cs           # CLI 内核：CommandLineMask（选项自描述元数据）、数据驱动解析器、
│   │                            #   注册表、帮助文本程序生成（M27）
│   ├── Commands.cs              # CLI 插件：compile/test/help 三个 COMMAND 及其 --sub-cmd（M27）
│   └── Logger.cs                # 唯一日志出口：Verbose/Warning/Error 分级；verbose 默认关闭，
│                                #   --verbose 开控制台 verbose，--log-to 全量 JSONL 落盘
├── Semantic/                 # 中端 P1–P3 + 符号图 + 诊断（M36 起，ARCHITECTURE §9）
│   ├── Diagnostics.cs           # 可恢复诊断模型（M36）：Diagnostic{Severity/Phase/Span?/Message}
│   │                            #   + DiagnosticBag（全编译单元单实例、只追加、HasErrors 门槛）
│   ├── CompilationUnit.cs       # 编译单元模型（M39）：多源文件 RootASTNode + DiagnosticBag + SymbolGraph
│   ├── DeclarationCollector.cs  # P1 声明收集（M39）：符号壳 + DeclarationCollection/FileContext
│   │                            #   + namespace 驻留合并 + import 登记 + ext 待注册 + 重复诊断
│   ├── DeclarationResolver.cs   # P2 瘦入口（M40 声明解析；M62 起 visitor 化）：13 步顺序
│   │                            #   启动各阶段 visitor + Freeze；语义规则编年史见 PROGRESS_REPORT
│   ├── Resolution/              # P2 visitor 化基建（M62，协议同 Binding/ 三基类，阶段级
│   │                            #   visitor——P2 遍历为「阶段 × 条目平铺」非 Binder 深递归）：
│   │   ├── ResolverVisitor.cs      # CRTP 基类（静态 Visit 唯一入口 + Enter/Exit finally 配对）
│   │   ├── ResolveEnvironment.cs   # 只读环境（unit/declarations/NameResolver + entries 三表
│   │   │                           #   IReadOnly 暴露 + Error 落袋 + ModifiersOf/FindField/
│   │   │                           #   Substitute 等共享设施）
│   │   ├── DeclEntry.cs            # 声明条目模型（节点+符号+解析上下文+InGraph）
│   │   ├── EntryCollector.cs       # 骨架遍历收集静态设施（标记位/Accessibility 写符号）
│   │   └── 阶段簇级分文件          # Import/TypeReference/Inheritance/Modifier/Accessor/
│   │                               #   Override（M63 S8e 两新阶段）/Native/
│   │                               #   AsyncGate（M66 S8f 声明侧闸门 2/3/5 +
│   │                               #   async 仅函数收口）/ConversionOperator
│   │                               #   （M66 S8f castTo/castFrom 声明形状）/
│   │                               #   Contagion（传染+字段闭包+shared 闸门三 checker）/
│   │                               #   GenericConstraint/Wrapper（7a/7b/7c 三 resolver）/
│   │                               #   EnumCase（M77 S11：洞独占性/case 名复核/
│   │                               #   判别值落定——模板绑定归 P3 声明点）/
│   │                               #   ProxyShape/ProxyMatching（M82 ProxyShapeChecker
│   │                               #   形状校验；M88：ProxyDispatchResolver 删除→
│   │                               #   ProxyMatching/ProxyMatchChecker 形状匹配 +
│   │                               #   名中形状不符诊断，零符号合成；烘焙归 Middleware）
│   ├── NameResolver.cs          # 名字解析共享设施（M41 提取自 P2）：符号路径/类型引用/泛型实参
│   │                            #   解析，诊断按构造传入的 Phase 落袋（P2/P3 各自实例化）
│   ├── AccessChecker.cs         # 使用点访问控制共享设施（M63 S8e，SYNTAX §16.1）：
│   │                            #   P2 声明侧与 P3 函数体内同一份判定（internal 单编译
│   │                            #   单元恒可见）+ 统一诊断措辞
│   ├── Binder.cs                # P3 瘦入口（M55 起 visitor 化；语义规则编年史见
│   │                            #   PROGRESS_REPORT M41–M54 各段与各 visitor 文件头）
│   ├── Binding/                 # P3 visitor 化基建（M55）：
│   │   ├── BinderVisitor.cs        # CRTP 三基类（通用/ExpressionVisitor 追加
│   │   │                           #   expectedType 下传/BinderShellVisitor 壳填充）——
│   │   │                           #   静态 Visit 唯一入口 + Enter/Exit 生命周期模板
│   │   ├── BindEnvironment.cs      # 只读环境（unit/declarations/NameResolver/诊断落袋；
│   │   │                           #   S8d 参数默认值记忆化表——懒绑定回调由 Driver 注入）
│   │   ├── BindContext.cs          # 函数级状态组合根（M65 组件化：Frame/Accessor/
│   │   │                           #   Labels/Flow/Locals 五成员；组件即方言）
│   │   ├── BindFunctionFrame.cs    # 只读函数帧（Method/FileCtx/DeclaringType/
│   │   │                           #   IsDefaultValueContext + HasThis/CanAccess）
│   │   ├── AccessorBodyState.cs    # 访问器体状态（S8e value 别名：Field/IsSetter）
│   │   ├── ProxyBodyState.cs       # proxy 模板态绑定语境（M88：IsActive + SelfType=
│   │   │                           #   TTarget 泛型参数；self/inner 占位，this=wrapper
│   │   │                           #   实例自身——不再重写 BoundWrapperAccess）
│   │   ├── BindLabelState.cs       # 控制流标签栈集（值块/循环/switch 占位/seq
│   │   │                           #   标签四栈封装 + 命中查找领域方法）
│   │   ├── FlowState.cs            # DA 流分析（分叉/合并/快照恢复原语；S8b
│   │   │                           #   smart cast 收窄表的家——同生命周期）
│   │   ├── Scope.cs                # 词法作用域链
│   │   ├── Dispatchers.cs          # 类别分派唯一 switch（Expression/Statement/Block）
│   │   ├── BoundAnalysis.cs        # GuaranteesReturn/值块终止/语句平铺枚举/TypeDisplay
│   │   ├── BindingDriver.cs        # 声明骨架遍历 + 三阶段启动（S8d 起：①参数
│   │   │                           #   默认值声明点绑定（记忆化按需，前向依赖
│   │   │                           #   声明顺序无关）①.5 enum case 模板绑定（M77
│   │   │                           #   S11：init 选择 + 洞签名落定符号）②逐函数体
│   │   │                           #   return 全路径检查 + init 映射赋值合成
│   │   │                           #   （M77 §9.3：无体产 body/有体前插）；
│   │   │                           #   M88：阶段 2.5/2.6 删除（合成体绑定取消）；
│   │   │                           #   proxy 声明体在阶段 2 按模板态绑定（ProxyBodyState）；
│   │   │                           #   降级调用点资格判定 + CallWildcard，无体合成）
│   │   ├── OverloadResolution.cs   # S8d 重载解析设施（SYNTAX §4.2：结构过滤/
│   │   │                           #   类型适用性/最具体胜出 + 平局打破；source-level
│   │   │                           #   ranking 唯一落点 BIL §3.3；调用/init/索引读共用）
│   │   ├── SymbolLookup.cs         # 实例成员查找/泛型字段最小替换/IsAssignable
│   │   ├── MemberLookup.cs         # 名字解析查找序（宿主 BaseType 链 → 命名空间链
│   │   │                           #   → 通配 import；容器解析）
│   │   ├── TypeReferences.cs       # 函数体内类型引用解析（NameResolver 委托）
│   │   ├── NarrowKey.cs            # S8b 收窄键（根（局部/参数/this/静态字段符号）
│   │   │                           #   + const 字段链，稳定链判定含 init 排除）
│   │   ├── ConditionFacts.cs       # S8b 条件事实提取器（is/null 判等/and-or-not
│   │   │                           #   → 真/假边收窄事实对，纯函数式）
│   │   ├── ConstFieldRules.cs      # S8b const 字段规则（赋值检查 init 豁免 +
│   │   │                           #   收窄资格 IsNarrowable）
│   │   ├── AsyncGates.cs           # async 边界闸门 P3 侧（M66 S8f，§4.5）：
│   │   │                           #   调用点 1/2（BoundTree 后置遍历单落点）+
│   │   │                           #   async lambda 捕获 4（AST 级粗粒度扫描）
│   │   ├── CallableModel.cs        # M103 lambda 对象模型：Func/Action/Cell 族查找与构造
│   │   └── Visitors/               # 结构 visitor 簇（Literal/Declaration/Conditional
│   │                               #   含值块壳/Loop/Switch/TrySeq/Binary/Path/Call/
│   │                               #   TypeCheck/Lambda（M66 S8f：async 捕获闸门 4 +
│   │                               #   S13 归口诊断）——语义落地范围同
│   │                               #   M41–M60 编年史：
│   │                               #   作用域链查找序/var 推断/intrinsic/调用规范
│   │                               #   参数序/DA/if 值块/循环/switch 占位/
│   │                               #   try/seq/实例链上色/?. if?/插值规范化/
│   │                               #   is-supers-with 双形态/typeOf/索引绑定
│   │                               #   与表达式底座链（S8c）/重载解析集成（S8d）/
│   │                               #   wrapper place 绑定与只读禁令（M79 S11：
│   │                               #   双源同池查找 + 链末 Colon 一处收口））
│   ├── Bound/                   # BoundTree 节点集（M41，按类别分文件仿 AST/）：
│   │                            #   BoundNode（Syntax 必填）/BoundExpression（Type）/语句节点 +
│   │                            #   BoundFunctionBody{Method, Locals, BoundBlock}；S7b 增补
│   │                            #   BoundIfStatement/BoundValueBlock 值块/BoundIfExpression/
│   │                            #   BoundReturnValueStatement/BoundCompoundAssignmentExpression；
│   │                            #   S7c-1 增补 BoundLoop（施工壳）/BoundLoopControl；
│   │                            #   S7c-2 增补 BoundThis/BoundInstanceCall/BoundFieldAccess
│   │                            #   三实例表达式节点 + BoundLoop For 路径（LoopVariable/
│   │                            #   Iterable/协议三方法）；S7d 增补 BoundSwitchStatement/
│   │                            #   BoundSwitchExpression/BoundThrowStatement/
│   │                            #   BoundSwitchPlaceholderExpression（`_` 占位）；
│   │                            #   S7e 增补 BoundCastExpression/BoundTryStatement/
│   │                            #   BoundCatchClause/BoundSeqStatement/BoundSeqExpression
│   │                            #   （含 BoundValueBlock.IsVolatile）；S8a 增补
│   │                            #   BoundTypeCheckExpression（Kind 三态 + TargetType/
│   │                            #   TargetValue 互斥双槽）/BoundTypeOfExpression
│   │                            #   （Operand/TargetType 互斥，Type = Type\<T\> 构造）；
│   │                            #   S8b 增补 BoundSmartCastExpression（smart cast
│   │                            #   标记：Operand + NarrowedType，Type = 收窄类型，
│   │                            #   P4a 物化为显式 cast）；S8c 增补
│   │                            #   BoundIndexExpression（Receiver/Index/Operator——
│   │                            #   读绑 getAtIndex、写绑 setAtIndex）；M61 增补
│   │                            #   BoundSeqExitStatement（return@语句seq，不带值）与
│   │                            #   BoundSeqStatement.Label（仅显式 named）；M66 增补
│   │                            #   BoundCastExpression.Conversion（castTo/castFrom
│   │                            #   名字分析产物：适用转换运算符，null = 内建）；
│   │                            #   M77 增补 BoundEnumCaseExpression（Case/规范序洞
│   │                            #   实参，Type=Owner）与 BoundTypeCheckExpression
│   │                            #   增 IsCase Kind + Case 第三槽（不触发 smart cast）；
│   │                            #   M79 增补 BoundWrapperAccessExpression（S11
│   │                            #   §14.5 只读 place：Receiver + Application
│   │                            #   应用记录（M84 S11c，Wrapper 为派生属性）——
│   │                            #   只作成员访问接收者，永不作
│   │                            #   路径绑定结果产出）；M88 增补 BoundSelfExpression
│   │                            #   /BoundInnerCallExpression（proxy 模板占位→
│   │                            #   get.self/invoke fn(..inner)）
│   ├── StdlibSources.cs         # stdlib 内嵌源载入（M43）：stdlib/**/*.latte 以 EmbeddedResource
│   │                            #   内嵌、编译时取出解析注入编译单元，与用户源同走 P1–P4
│   ├── DispatchExplainer.cs     # 派发链诊断报告器（M87 S11f，RUNTIME §15；M88 瘦身）：
│   │                            #   数据源 = 应用登记 × ProxyMatching 预览 + 降级资格
│   │                            #   （报告 Middleware 将烘焙的链；无合成 fn/router 槽）
│   └── Symbols/                 # 符号图内核（M37，M39 增补容器成员表/全局命名空间驻留）：
│                                #   SemanticSymbol 家族（引用相等即身份；M63 S8e 增补
│                                #   Accessibility/SourceFile 与 MethodSymbol 三标记/HasBody、
│                                #   FieldSymbol 访问器三槽 Getter/Setter/HasBackingStorage；
│                                #   M66 S8f 增补 MethodSymbol.IsAsync——async 边界
│                                #   闸门检查点分派依据；M77 增补 EnumCaseSymbol
│                                #   （Owner/Discriminant + ResolvedInit/HoleParameters
│                                #   模板槽）+ TypeSymbol.Cases 表 + ParameterSymbol.
│                                #   MappedField（§9.3 init 映射目标，P2 落定））+
│                                #   LocalSymbol（M41，P3 产生；
│                                #   Type 可空——null 仅限 P4a 合成 .breakid 局部，S7c-1；
│                                #   M79 增补 AppliedWrappers——局部变量 wrapper
│                                #   应用 P3 登记（栈上声明不进 P1/P2，§14.9
│                                #   矩阵 C 恒合法））、
│                                #   SymbolGraph 构造泛型驻留 + Freeze + Substitute
│                                #   单源与构造类型 BaseType 创建即代入/统一回填（M76）、BootstrapSymbols
│                                #   （M51 增补：String.Add intrinsic、Any.toString 接口承诺
│                                #   + Object open native 默认实现；M63 统一 Public；M78 增补
│                                #   Array\<T\> getAtIndex/setAtIndex operator——S8c 索引绑定
│                                #   内建目标，P4b 直发 §13.6 不走 invoke；M88：删
│                                #   DowngradeRouter/DowngradeChain/ProxySpecialization
│                                #   等合成槽；BootstrapSymbols.CallWildcard =
│                                #   Any.call???（pub native latte_rt/call???，§22.5
│                                #   VM hook，EnsureCallWildcard 幂等，胖值签名））、
│                                #   CanonicalSymbolPrinter
├── Lowering/                 # 中端 P4（M44 S6 + M45 S7a + M46 S7b + M47 S7c-1 + M48 S7c-2 + M49 S7d + M50 S7e + M51 S7f-1 + M54 S8a + M103 lambda，ARCHITECTURE §6）：依赖方向 Lowering → Semantic/Bil 单向
│   ├── ClosureStoragePlan.cs    # M103 闭包存储判定表（读 getValue/写 setValue/对象引用 + init 豁免）
│   ├── Lowered/                 # LoweredTree 节点集（S7a 起覆盖 S5 全部 Bound 节点，仿 Bound/ 分文件）：
│   │                            #   LoweredNode（Origin 必填回指 BoundNode）/LoweredExpression
│   │                            #   （Type 透传）/块/局部声明/表达式语句/void 调用/赋值/return/
│   │                            #   字面量/值引用/字段引用/二元/一元/带返回值调用/new + LoweredFunctionBody；
│   │                            #   S7b 增补 LoweredIfStatement/LoweredConstantExpression（脱糖合成节点，
│   │                            #   Origin 指最近语法来源）；S7c-1 增补 LoweredLoop/LoweredLoopControl
│   │                            #   （Judge 块 + 合成 .breakid 局部 .bN——Type null 特例）；
│   │                            #   S7c-2 增补 LoweredThis/LoweredInstanceCall（Type 自带）/
│   │                            #   LoweredFieldAccess；S7d 增补 LoweredSwitch/
│   │                            #   LoweredSwitchCase/LoweredThrowStatement；
│   │                            #   S7e 增补 LoweredCastExpression（Type 自带）/
│   │                            #   LoweredTryStatement（ExceptionSlot）/LoweredTryCatch/
│   │                            #   LoweredSeqBlock（volatile → §9.6 block 修饰符）；
│   │                            #   S8a 增补 LoweredTypeCheckExpression/
│   │                            #   LoweredTypeOfExpression（Type 自带，恒等重写）；
│   │                            #   S8c 增补 LoweredIndexExpression（Receiver/Index，
│   │                            #   Type 透传，不携带 Operator）；M61 增补
│   │                            #   LoweredSeqExitStatement（return@语句seq 纯控制流
│   │                            #   标记，编织消费不产指令）；M77 增补
│   │                            #   LoweredEnumCaseExpression（Case/规范序洞实参，
│   │                            #   cast 物化按洞签名）与 LoweredTypeCheckExpression
│   │                            #   Case 槽（IsCase，S11）；M84 增补
│   │                            #   LoweredGetWrapperExpression（§12.4 值拷贝）
│   │                            #   与 LoweredWrapperFieldExpression（§13.3
│   │                            #   PlaceChain：FieldSymbol|TypeSymbol→field|wrapper，仅作写 place；
│   │                            #   读侧值拷贝后使用普通字段访问）；M88 增补 LoweredGetSelfExpression
│   │                            #   /LoweredCallInnerExpression（§12.5/§15.4）
│   ├── Lowerer.cs               # P4a 瘦入口（M55 起 visitor 化）
│   ├── LoweredVisitor.cs        # P4a CRTP 基类（同 Binder 协议，无 scope/expectedType）
│   ├── LowerEnvironment.cs      # 只读环境（unit/诊断）
│   ├── LowerContext.cs          # 函数级状态组合根（M65：Method/TransformFailed
│   │                            #   + Synth/Output/Targets 三组件）
│   ├── SynthLocalFactory.cs     # 合成局部工厂（.sN/.breakid .bN 独立计数统一
│   │                            #   登记——顺序即 .vars 发射顺序 + ReferenceTo）
│   ├── LowerOutputState.cs      # 前置语句机制（输出列表栈封装：Push 双形态/
│   │                            #   Pop/Current/Add）
│   ├── LowerTargetState.cs      # 降级目标映射栈集（值块/循环/switch 占位/
│   │                            #   seq/安全访问五栈封装 + 命中查找）
│   ├── LowerDispatchers.cs      # 类别分派唯一 switch + LowerBlockVisitor（输出列表压弹）
│   ├── LoweringDriver.cs        # 逐函数体启动（合成局部收尾进 Locals）
│   ├── LoweringFacility.cs      # LowerArguments/EnsureDeclaredType（BIL §6.5 cast 物化）
│   │                            #   + variadic 索引 ABI 元素类型设施组（M78：vargs → Any/
│   │                            #   kwargs → Pair\<String, Any\>，读拆箱写装箱）
│   ├── Rewriters/               # 结构 visitor 簇（Statement/Loop（Judge 块）/Switch
│   │                            #   （常量表恒等 + pattern 链降级）/TrySeq/ValueBlock
│   │                            #   （continuation 编织——ValueBlockFacility 静态设施）/
│   │                            #   Expression（短路/if 表达式/复合赋值脱糖）/NullSafety
│   │                            #   （?. if? unwrap/wrap）/Destructuring）+
│   │                            #   WrapperPlaceLowering（M84 S11c：wrapper place
│   │                            #   降级唯一设施——get.wrapper 值拷贝链与
│   │                            #   get.wrapper(.field)+get.field/set.wrapper.field 链按应用类别分派，
│   │                            #   使用点与 proxy 体内共用）——脱糖落地
│   │                            #   范围同 M44–M54 编年史；未覆盖节点 P4 Error + 跳过函数体
│   ├── BilEmitter.cs            # P4b 瘦入口（M55 起 visitor 化）
│   ├── EmitVisitor.cs           # P4b CRTP 基类（签名带 BilBlock target 施工目标——
│   │                            #   下行填充，三树中与协议贴合度最高）
│   ├── EmitEnvironment.cs       # 模块级（Module/四类资源去重表跨 fn 共享，值为资源对象）
│   ├── EmitContext.cs           # 函数级组合根（M65：Function + Temps/BlockIds）
│   ├── TempVarTable.cs          # 临时变量表（.tN 工厂，自 EmittingFacility 收编）
│   ├── BlockIdAllocator.cs      # 分支 block 编号分配器（if/loop/switch/seq/try）
│   ├── EmitDispatchers.cs       # 类别分派（语句 Unit/值 BilVariableOperand——M57
│   │                            #   物化契约类型化）+ EmitBlockVisitor
│   ├── EmittingDriver.cs        # 模块组装 + fn 定义（.args/.vars/entry block/void 补 ret）
│   ├── EmittingFacility.cs      # 资源登记/intrinsic 枚举映射/转义（§19.1 标量全形态/
│   │                            #   §19.4 switch-table/§19.5 catch-table）
│   └── Emitting/                # 结构 visitor 簇（LocalSymbols（§8 平铺 + ext 裸条目 +
│                                #   §8.5 .case 声明（M77：洞签名 + 判别值 res/auto）+
│                                #   §8.3.1 wrapped(W) 应用标记投影（M88，无隐藏字段
│                                #   声明）+ §8.4 wrapper-proxy(specific|wildcard)
│                                #   模板 fn 投影（M88：proxy 声明体即模板进 BIL，
│                                #   无合成特化/原始体/shim））/
│                                #   Statement（§16 结构化指令发射：if/loop/switch/seq/try/
│                                #   break-continue/throw + set.var/get/set.field §13 +
│                                #   set.wrapper.field 写链（M84））/
│                                #   Value（§11 运算/invoke/new/cast §12.1–12.2/type.is
│                                #   §12.3/getid §12.5/new.case §14.3 与 type.is.case
│                                #   §12.3（M77）+ get.wrapper §12.4 与
│                                #   get.wrapper(.field)+get.field 读链（M84）+ get.self §12.5
│                                #   与 invoke fn(..inner) §15.4（M88）））——发射范围同
│                                #   M44–M54 编年史；
│                                #   M57 起全部强类型指令构造（无 opcode 字面量）
├── Bil/                      # BIL 生态（M38；M57 全模型对象化。对中端零依赖：
│                            #   字符串身份，不引用 Semantic/AST）
│   ├── BilModule.cs             # Module/Metadata/Resources（§4/§19：标量/null/通用
│   │                            #   collection + switch-table/catch-table 专用资源类）
│   │                            #   + BilScalarType 枚举
│   ├── BilSymbols.cs            # 类型与成员声明（§8.2–§8.5）+ BilTypeKind/BilMemberKind/
│   │                            #   BilAccessibility/BilKeyword 枚举与 BilModifier 子类族
│   │                            #   （M63 增 BilAccessorModifier：getter(FIELD)/setter(FIELD)；
│   │                            #   M88：BilProxyKind 两态 specific|wildcard +
│   │                            #   BilWrapperProxyModifier（proxy 模板 fn 标记）+
│   │                            #   BilWrappedModifier（wrapped(W) 应用标记）；
│   │                            #   GetSelfInstruction 与 invoke 指令见
│   │                            #   Compute/Data 指令文件）
│   ├── BilFunction.cs           # Function/.args/.vars/Block（§9）+ BilBlockModifier 枚举
│   ├── BilInstructions.cs       # 指令基类（Origin(object?) 占位 + WriteTo 排版协议）+
│   │                            #   操作数模型（§10；blk/res 持对象引用）
│   ├── BilComputeInstructions.cs # §11–§12 指令（BilBinaryOp/BilUnaryOp/BilTypeCheckKind
│   │                            #   + get.self M88）
│   ├── BilDataInstructions.cs   # §13–§15 指令（load/get/set/new/invoke 全形态
│   │                            #   + §13.6 set.array（M59）+ invoke fn(..inner)（M88））
│   ├── BilControlFlowInstructions.cs # §16 指令（§17 协程暂缓）
│   ├── BilHintInstruction.cs     # §18 hint 提示指令（M64：block 内可忽略 backend 提示，
│   │                            #   string 资源 JSON 负载，纯位置标记）
│   ├── BilSpellings.cs          # 全部枚举 → 标准拼写唯一定义点（M57）
│   ├── BilWriter.cs             # 模型 → 标准 BIL 文本（§20 黄金示例逐行一致；
│   │                            #   指令自渲染，无 opcode switch）
│   ├── BilVerifier.cs           # BIL 验证器（M58，§21；提前自 S12）：入口
│   │                            #   Verify(BilModule) + BilVerificationError + §21.1
│   ├── BilVerificationContext.cs # 验证器索引（资源/类型/成员符号 + 预定义符号表
│   │                            #   + 变量类型环境）与 canonical 符号/类型引用解析工具
│   ├── BilVerifier.Symbols.cs   # §21.2 符号 + §21.7 参数包 + §21.8 声明侧矩阵
│   ├── BilVerifier.Types.cs     # §21.3 类型（指令读写分类唯一表 + 逐指令 switch；
│   │                            #   M59 增补 §13.6 索引严格三元组查询）
│   └── BilVerifier.Flow.cs      # §21.4 保守 DA + §21.5 控制流 + §21.6 breakid
├── stdlib/                   # 编译器自携标准库源（M43 起，EmbeddedResource 内嵌，见 StdlibSources；
│                             #   M74 起六源，与用户源同走 P1–P4）
│   ├── .bootstrap.latte         # 基元自举源（M48 EnumerateInRange + M52 core.Pair\<TKey, TValue\>
│   │                            #   解构协议根，SYNTAX §18/§15.3）
│   └── core/                    # Console.latte（core.io::Console，M43）+ collections.latte
│                                #   （M48：IEnumerable/IEnumerator 双接口 + RangeI32/
│                                #   RangeEnumeratorI32；M73 泛型化 RangeEnumerator\<T\> 抽象基类）
│                                #   + coroutine.latte（M74：core.coroutine 类型面——Task/
│                                #   Task\<TResult\>/Executor 家族/PollingAlarm/EventAlarm/
│                                #   CoroutineLocal\<TValue\> 全 shared abstract + sleep native，
│                                #   SYNTAX §15.3）+ disposable.latte（M74：core.IDisposable，
│                                #   §6.2）+ exceptions.latte（M74：RuntimeException/IOException/
│                                #   CastException/NoSuchMethodException 四异常子类，§8.1）
├── Tests/                    # 自研控制台测试（非 xUnit/NUnit，见 §5）
│   ├── AstDescribe.cs           # 统一 AST 描述器（M31，全部套件共用）
│   ├── BoundDescribe.cs         # 统一 BoundTree 描述器（M41，P3 套件共用，仿 AstDescribe）
│   ├── LoweredDescribe.cs       # 统一 LoweredTree 描述器（M45，P4a 套件共用，仿 BoundDescribe）
│   ├── BilTestHarness.cs        # BIL 测试基建（M58）：EmitBilUnit 全管线驱动 +
│   │                            #   CheckBilValid/CheckBilInvalid 验证器断言 +
│   │                            #   res 重编号形状黄金 CheckFnShape/CheckResShape
│   ├── TestHarness.cs           # 统一驱动与断言基建（M31；M36 增 CheckSemanticError）
│   ├── TestRunner.cs            # test 命令驱动（套件注册表、菜单打印、按编号运行、退出码）
│   ├── TestRootParserLayer.cs   # 独立 Layer 测试垫底层（只接受 EOF）
│   ├── TokenDispositionTests.cs # Token 流转协议测试（四种组合）
│   ├── ASTIntegrityValidatorTests.cs # Validator 直调测试（合法树 + 结构破坏拒绝）
│   └── LexerFuzzTests.cs        # Lexer fuzz 测试（Slash/EOF/注释 + 6000 随机用例）
└── docs/                     # 设计与规范文档（全部为权威参考）
```

### 3.1 关键文件

| 文件 | 用途 | 重要性 |
|------|------|--------|
| `docs/SYNTAX.md` | **语言语法规范（最权威）** | ⭐⭐⭐ 有歧义时以此为准，不要猜语法 |
| `docs/RUNTIME.md` | 运行时模型与类型系统 | ⭐⭐⭐ |
| `docs/BIL_STANDARD.md` | BIL 中间语言规范 | ⭐⭐ |
| `docs/compiler/syntax/PARSER_ROADMAP.md` / `docs/PROGRESS_REPORT.md` | Parser 路线图与进度 | ⭐⭐ |
| `docs/compiler/syntax/EXPRESSION_ARCHITECTURE.md` | 表达式架构专项设计 | ⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md` | 语义分析与 BIL 生成架构（中端） | ⭐⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ROADMAP.md` | 语义分析路线图 | ⭐⭐ |
| `Parser/Parser.cs` | 层栈式 Parser 的核心协议 | ⭐⭐⭐ |
| `Lexer/Tokens.cs` / `Parser/Keywords.cs` / `AST/ASTNode.cs` | Token/关键字/AST 基类等核心数据结构（M30 拆分自原 `Core/Utilities.cs`） | ⭐⭐⭐ |

---

## 4. 核心设计决策（改动代码前必须理解）

### 4.1 ⚠️ Latte 没有运算符优先级

```latte
var result = 1 + 2 * 3       // ❌ 编译错误：歧义
var result = 1 + (2 * 3)     // ✅ 必须加括号
```

对 Parser 的影响：不需要优先级表；遇到未括号化的连续运算符必须报错。实现表达式相关功能时不要引入优先级概念。

### 4.2 ⚠️ `rich` / `shared` 是类型**声明**修饰符，不是类型引用修饰符

- `rich`：**仅用于 struct / enum struct / wrapper**（class 不能用）。允许值类型持有引用，但仍是值语义、unique ownership（类似 `unique_ptr`，**不是** `shared_ptr`）。wrapper 恒为 rich struct，`rich` 由声明形式隐含，显式书写是编译错误。
- `shared`：class、rich struct、wrapper 可用，表示允许跨协程共享；`singleton` class 必须 shared。
- 二者**单向传染**：基类 rich/shared ⇒ 子类必须同标，反向可收紧（详见 SYNTAX §3.1.1）。
- 使用类型时（变量声明、函数参数）**永远不写** `rich`/`shared`。因此 `TypeReferenceParserLayer` 不处理它们；它们属于 class/struct 声明解析的职责。
- 这些规则的**检查**全部属于语义期 P2（见 `docs/compiler/semantic/SEMANTIC_ROADMAP.md` S3），Parser 只负责收下修饰符。

### 4.3 ⚠️ 泛型列表必须以 `\<` 开启（2026-07-17 语法修订）

泛型的声明与使用统一写作 `Name\<...>`（反斜杠 + 小于号开启，`>` 闭合）：

```latte
class Container\<TElement> { ... }      // 声明
var list: List\<i32>                     // 使用
var sorted = myList.sort\<i32>()         // 泛型调用
var map: List\<Map\<String, i32>>        // 嵌套闭合写 >>
```

- `<` 只属于比较运算符：`a < b` 与 `a\<b>` 词法层面零歧义。
- Lexer 不合并 `>` 系列；`>=`/`>>`/`>>>` 由 `ExpressionParserLayer` 在运算符状态下重组相邻 token。
- BIL 自身的 `.array<T>` 等语法不受影响（BIL 用 `cmp.lt` 等指令，无 `<` 歧义）。
- 详见 `docs/SYNTAX.md` §3.6。

### 4.4 Parser 架构：层栈 + 状态机 + 施工目标协议

Parser 主循环维护一个 Layer 栈，每个 token 交给栈顶 Layer 处理。核心协议在 `Parser/Parser.cs`：

- `IParserLayer.ParseToken(token, context)` 返回 `ParserLayerResult`：
  - `Continue`（单例）：本层继续消费，token 已被本层吃掉
  - `PushLayer(layer, TokenDisposition)`：压入子 Layer（委托）
  - `PopLayer(TokenDisposition)`：本层完成，弹栈
- `TokenDisposition.Consume`：当前 token 已被本层消费，前进到下一个 token；
  `TokenDisposition.Replay`：当前 token 原样交给新的栈顶 Layer 重新处理。
- **每个 Layer 内部用状态机驱动**（`private enum State` + switch），状态转换处要写注释。
- 模块化原则："Delegate, don't implement" —— 框架层（如 `ExpressionParserLayer`）负责识别、路由、运算符处理；具体语法结构委托给专门 Layer。每个 Layer 职责单一、可独立测试。

### 4.7 ⚠️ 大扫除后的 Parser 架构规则（2026-07-26 重构，必须遵守）

重构后的 Parser 分为**控制流系统**与 **AST 施工系统**，两者严格分离：

1. **Layer 不返回 AST**。Layer 之间只传递控制权，不传递任何 AST 数据；
   `PopLayer` 只表示控制权归还。禁止任何形式的回传替代机制
   （回调、Context 字段、父层引用、全局临时字段、事件/委托等）。
2. **Layer 创建时必须获得施工目标**。构造函数接收明确、强类型的目标
   （具体施工节点，或 ExpressionRootASTNode/CodeBlockASTNode/RootASTNode 等附加目标），
   子层原地填充目标或向目标附加子节点；数据流严格单向（父→子）。
3. **Push/Pop 使用 TokenDisposition**（Consume/Replay），禁止布尔值；
   `Continue` 只表示"本层消费当前 token 并继续"，不支持 Replay。
4. **表达式位置统一使用 ExpressionRootASTNode** 作为稳定挂载点：
   一次性 `Attach`、禁止替换、禁止附加已有父节点的表达式；
   可选表达式用 null Root 表示，禁止"非 null 但为空的 Root"；
   `ASTNode.Parent` 只能设置一次。
5. **子 Layer 禁止修改施工目标之外的 AST**（父节点、兄弟节点、
   经 Context 获得的全局位置、其他 Layer 正在施工的节点）。
6. **EOF 是正式 Token**（`EndOfFileToken`）：由 Lexer 在输出 token 列表末尾
   追加（M25；Parser 对绕过 Lexer 的调用方保持追加兼容），只由
   RootParserLayer 消费；非 Root 层遇 EOF：结构完整 → Pop(Replay) 上交，
   不完整 → 抛 "Unexpected end of file"。禁止用换行伪装 EOF。
7. **新 Layer 必须有独立测试**（`TestRootParserLayer` 驱动，见 §5）。
8. **注释由 Parser 主循环统一跳过**（M25）：CommentToken 不参与语法，
   分发时直接跳过；各 Layer 不再自行处理注释。

解析成功后 `ASTIntegrityValidator` 自动验证 AST 不变量：遍历只走
`[ChildAstNode]` 标注的成员（`[AstCarrier]` 对象深入其公共字段），校验每个
子节点的 Parent 指向持有者，另含 Root 均已填充、节点无共享、Parent 链无环、
switch default 规则、**每节点 Span 合法（M28：非空、sourceName 非空、
End 不早于 Start）**、**类型审计（M28：装 ASTNode 的字段/自动属性必须带
[ChildAstNode]/[ParentAstNode] 标注）**；失败抛 `CompilerInternalException`（内部编译器错误，
与用户语法错误区分）。节点类型一律用 CLR 类型判断（无 ASTNodeType 枚举）。
「归属后知」的场景必须用创建时归属即定的结构承载
（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）
或延迟一次性 AttachTo（注解），**禁止任何形式的 Parent 重挂**。

**Span 施工（M28）**：每个 AST 节点都有源码范围 `ASTNode.Span`（`CharRange?`）。
约定：层目标节点由 Parser 主循环按 token 流计算 span，层弹出时经
`ISpanReceiver.ReceiveSpan` 回填（一律 `target.Span ??= span` 只填空）——
新 Layer 若有施工目标，应实现 `ISpanReceiver`；层内自建节点由所在层显式设置
（创建记 Start，完成经 `ParserLayerContext.GetPreviousLocation()` 封 End）；
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
**Span 统一为左闭右开 `[Start, End)`（M31 起）**：Start 指向首个字符，
End 指向最后一个字符的下一位置（token 与 AST 节点一致；EOF 为零宽范围）；
语句/声明的 span 不拖尾换行符到下一行（终态层不消费换行）。
Validator 与 AstJsonlSerializer 的 [ChildAstNode] 反射统一走
`AST/ASTVisitor.cs` 的 `AstStructureReflection`（M28），禁止再写第三份反射下钻。

**JSONL 往返（M31）**：`AstJsonlSerializer`（v2：carrier 记录化、字段名键控）
与 `AstJsonlDeserializer`（完整反序列化，产物强制过 Validator）构成往返；
消费方按字段名取值，不依赖字段顺序。

**allowBareReturn 传染（M33）**：lambda 是裸 return 边界（SYNTAX §5.1）——
`CodeBlockParserLayer` 构造标记 `allowBareReturn`（默认 true）为 false 时，
遇无 @标签 return 抛 ParserException。lambda 体一律下传 false；标记沿施工链
向所有嵌套代码块与表达式深处传染（If/Switch/Loop/TryCatch/Seq/
VariableDeclaration/ArgumentList/TypeOf/Expression 各层逐一传递）——
**新 Layer 若创建 CodeBlockParserLayer 或 ExpressionParserLayer，必须同样
接收并传递该标记**；if/switch 表达式分支体不是 lambda 边界，继承父上下文标记。

### 4.5 ⚠️ 简洁优先：新增代码前必须自问的三个问题

新增任何 AST 节点、Layer、状态或辅助方法之前，逐条回答：

1. **这个真的有必要存在吗？** 不服务当前需求的字段、状态、抽象一律不写。
2. **有没有更简洁更优雅的方法？** 能用现有状态机多一个分支解决的，不要新建一层。
3. **可不可以复用已有的轮子？** 先翻一遍 `Parser/` 下已有的 Layer，不要自己造轮子。

项目内已验证的复用范例：

| 特性 | 复用方式 | 没有做的事 |
|------|----------|-----------|
| `throw` / `yield` / `return` / `break` / `continue` | `CodeBlockParserLayer` 的内联子状态 | 各建一个 Layer |
| `await` | `ExpressionParserLayer.IsPrefixUnaryOperator` 加一个关键字 | 新建 AwaitParserLayer |
| `seq` 语句形态 + 表达式形态 | 共用同一套 `CodeBlockParserLayer` 基建 | 两套独立实现 |
| class/interface/struct/wrapper/enum 声明 | 扩展既有 `DeclarationParserLayer` 骨架 | 新建 ClassDeclarationParserLayer |

只有当职责确实独立、且需要被多个父层复用时，才新建 Layer。

### 4.6 Lexer 的特点

Lexer 只做简单字符识别，不理解语义。例如 `3.14` 会输出三个 token：`Word "3"`、`Notation "."`、`Word "14"` —— 由 `LiteralParserLayer` 的状态机组合成浮点字面量。不要在 Lexer 里加语义判断。

- 斜杠家族（`/`、`//`、`/*`）由专门的 `SlashLexerLayer` 分流（M25）；
- `EndOfFileToken` 由 `Lexer.Tokenize` 在输出末尾追加（M25）；输入结束时以
  虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException；
- 位置计量（M28 修复后）：`CharRange.sourceName` 是源名唯一来源
  （`CharPosition` 不携带）；`CharPosition.offset` 是 0 起始字符索引；
  行/列 1 起始，换行算当前行最后一列；token 头跳过空白字符；
  EOF 冲刷帧占一个末尾虚拟位置，保证冲刷 token 的 End 正确；
- **token 范围为左闭右开 [Start, End)**（M31 起）：End 是最后一个字符的下一位置；
- 块注释不吞字符、不吞换行（M31：按行分段，换行以 LineBreakToken 入流）；
  行尾归一只把 `\r\n`/`\r` 归一为 `\n`；
- 复合赋值（`+=`/`*=` 等）不合并 token（与 `>=` 同策略，Parser 遇 op+`=` 重组为
  CompoundAssignmentExpressionASTNode，M34）；字符字面量 `'` 已实现
  （M34：CharLexerLayer + CharToken + CharLiteralASTNode，转义复用 StringEscape）；
  多行字符串 `"""` 已实现（M32，SYNTAX §3.3：
  Swift 风格严格多行，QuoteLexerLayer 分流 `"`/`""`/`"""`，转义表 StringEscape 单源）；
- **字符串插值词法帧机制（M53，SYNTAX §3.8）**：字符串层遇未转义的 `${`
  （挂起 `$` 延迟判定，`\$` 不算引导）产出文本段 + InterpolationStartToken
  并**压基础层**正常词法；驱动按 **token 层**大括号计数配平（字符串/字符/
  注释内容不产生记号 token，天然豁免），归零把 `}` 改发 InterpolationEndToken
  并弹回字符串层；嵌套插值经帧栈递归，EOF 帧未闭合先于冲刷报错。
  多行层段 token 以原文暂存保序，闭界确定缩进基准后统一回填解码内容。
  段 token 的 span 不含引号与引导 `$`（首段/段尾修正）。

---

## 5. 测试策略

- **不使用任何测试框架**。测试是 `Tests/` 下的静态类，每个类提供 `public static int RunAll()`（返回失败用例数），由 `Tests/TestRunner.cs` 统一驱动（`test` 命令入口）。
- **全量入口**：`dotnet run -- test --all` 自动运行全部套件，任意失败返回非零退出码并列出失败套件名——这是 CI 与提交前验证的标准方式（CI 配置见 `.github/workflows/ci.yml`）。
- **统一基建（M31）**：`Tests/AstDescribe.cs` 是唯一的 AST 描述器（Expr/Stmt/Block/Decl/Root/Type/Symbol 等），`Tests/TestHarness.cs` 是唯一的驱动与断言（ParseRoot/ParseBlock/ParseWithLayer/ParseFirstDecl + Check/CheckTrue/CheckParseError/Summary）。禁止在套件里再写私有 Describe*/Format* 副本与计数样板。
- **断言对象约定（M31）**：除查的就是命令行/日志/token 流/层协议行为的套件（Logger、CommandLineParser、LexerFuzz、TokenDisposition）外，一律断言 AST 树产物（AstDescribe 描述串 + 结构断言），不断言控制台输出文本。
- **AST 结构断言**：表达式类测试除描述串快照外，还应断言结构性事实（Root 是否存在/已填充、Expression 的具体类型、Parent 链、子 Root 填充、无节点共享）——快照不能作为唯一验证方式。
- **独立 Layer 测试**：经 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)` 驱动（`TestHarness.ParseWithLayer` 封装）。`TestRootParserLayer` 只接受 EOF——被测 Layer 提前结束或漏消费普通 token 会立即失败，能发现 Layer 边界问题。
- **约定：每新增一个 ParserLayer，必须在 `Tests/` 添加对应测试类，并在 `TestRunner` 注册表注册（`test` 菜单与 `test --run N` 的编号即注册表顺序）。**
- 测试数量与通过状态等易变数字只记录在 `docs/PROGRESS_REPORT.md`，本文件不保存。

验证改动（已验证可用）：

```bash
dotnet build
dotnet run -- test --all    # 全量；或：dotnet run -- test --run 5（单个套件）
```

---

## 6. 代码规范与开发约定

- **命名**：标准 C# 约定（类/方法 PascalCase，局部变量与私有字段 camelCase）。
- **缩进**：4 空格。
- **注释语言**：中文。关键逻辑必须注释；状态机的状态含义与转换必须说明。
- **文档语言**：中文。`docs/` 下的规范文档是权威来源——**先读 SYNTAX.md 再写代码，不要凭其他语言的经验猜语法**（项目已因此返工过）。
- **思考语言**：为节省 token，思考一律使用中文；向子代理（subagent）下达任务时必须明确要求它也用中文思考。
- **禁止使用 AskUserQuestion**（harness 为 Kimi Code 时）：该工具有显示 bug，用户看不到第一个问题之后的后续问题。需要用户决策时，把问题整理好在回复正文中一次问完，然后停下来等待回答。
- 新代码应模仿相邻文件的风格；项目无 linter/格式化工具配置。
- 命名空间：主代码 `LatteCompiler`，测试 `LatteCompiler.Tests`。
- **日志**：Lexer/Parser 等编译器内部的日志一律走 `Core/Logger`（Verbose/Warning/Error），禁止直接 `Console.WriteLine`；verbose 默认关闭（`--verbose` 子命令打开控制台输出），`--log-to PATH` 把全量日志以 JSONL 落盘。控制台日志输出走 **stderr**（M31 起）——诊断不污染 stdout 的数据流（如 `compile --parse-only` 的 AST JSONL）。测试的报告输出（`[PASS]`/`[FAIL]` 等）不受此限。

### 添加新 Parser 功能的标准流程

1. 阅读 `docs/SYNTAX.md` 相关章节，理解规范与示例
2. 设计状态机（画出状态转换）
3. 在 `AST/` 对应文件中添加 AST 节点
4. 在 `Parser/` 新建 ParserLayer（实现 `IParserLayer`，构造函数接收明确施工目标，见 §4.7）
5. 在 `Tests/` 添加测试类，在 `TestRunner` 注册表注册
6. 在 `RootParserLayer`（或相应父层）接入委托入口
7. `dotnet build` + `dotnet run -- test --run N`（对应套件）验证

### 中端（P3/P4）新增语法结构的标准流程（M55 起）

三树遍历统一为 CRTP visitor 协议（M55 定稿）：

1. 阅读规范章节与 `SEMANTIC_ARCHITECTURE.md` 对应 pass 职责（P3/P4 边界是纪律）
2. P3：`Semantic/Bound/` 加 Bound 节点 → `Semantic/Binding/Visitors/` 对应簇文件加结构 visitor（继承 `BinderVisitor`/`ExpressionVisitor`/`BinderShellVisitor` 三基类之一；栈类上下文压弹只写 Enter/Exit）→ `Dispatchers.cs` 注册一行
3. P4a：`Lowering/Lowered/` 加 Lowered 节点 → `Lowering/Rewriters/` 簇加 rewriter → `LowerDispatchers.cs` 注册一行
4. P4b：`Lowering/Emitting/` 簇加 emitter → `EmitDispatchers.cs` 注册一行
5. 测试：BoundDescribe/LoweredDescribe 加节点支持 + Binder/Lowerer/BilEmitter 三套件加用例
6. 迁移铁律：先完整读旧实现再写（诊断消息文本/毒化静默位置逐字保真）；合成局部顺序敏感（`.sN` 先于 `.bN`——SynthLocals 顺序即 `.vars` 发射顺序）

### 进度对齐标准（必须遵守）

- **`docs/PROGRESS_REPORT.md` 是项目进度的唯一权威来源**。不要新建单点完成报告/实现总结类文档。
- **更新时机**：每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须立即更新。
- **更新方式**：保持文档既有结构不变，并在「里程碑历史」**顶部**追加新段落（倒序）。
- **分工**：`PARSER_ROADMAP.md` 管「计划」，`PROGRESS_REPORT.md` 管「现状」。计划调整改 ROADMAP，进度推进改 PROGRESS_REPORT。

---

## 7. 注意事项与已知限制

- 项目已在 **Git 版本控制**下（`main` 分支）：执行 `git commit` 等变更操作前先获得用户确认；提交前确保 `dotnet build` 通过且 `dotnet run -- test --all` 无失败。
- Verbose 调试日志默认关闭，不再刷屏；需要时加 `--verbose` 子命令（控制台）或 `--log-to PATH`（全量 JSONL 落盘）。
- 无安全敏感面：本项目是本地控制台工具，不处理网络、凭据或用户隐私数据。唯一文件操作是 `Program.cs` 读取用户指定路径的 `.latte` 文件。

---

## 8. 项目原则

1. **文档驱动** —— 先理解 SYNTAX.md，再写代码
2. **测试驱动** —— 每个 ParserLayer 都有对应测试
3. **模块化** —— 每个 Layer 职责单一，委托而非大包大揽
4. **渐进式** —— 按各阶段 ROADMAP（`docs/compiler/syntax/PARSER_ROADMAP.md`、`docs/compiler/semantic/SEMANTIC_ROADMAP.md`）逐步推进，不跳步
5. **不要猜测** —— 不确定时查文档
6. **简洁优先** —— 写代码时始终自问：这个真的有必要存在吗？有没有更简洁更优雅的方法？可不可以复用已有的轮子（比如已有的 Layer）？不要自己造轮子（详见 §4.5）
