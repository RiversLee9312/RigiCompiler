# Latte Compiler 进度报告

> **进度对齐标准**：本文档是项目进度的**唯一权威来源**。
> 每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须更新本文档；
> 更新时保持文档结构不变，并在「里程碑历史」追加一段。
> 计划与分工见 `compiler/syntax/PARSER_ROADMAP.md` 与 `compiler/semantic/SEMANTIC_ROADMAP.md`；本文档只记录「现状」。

**报告日期**: 2026-08-08
**最新里程碑**: M102 完成 S13 lambda P4 B0：无捕获 lambda 生成 synthetic fn declaration 与 `.methodid` 函数句柄，局部值直接调用发 `getid.method`/`invoke.indirect`（含 `noret`）；捕获 closure environment/cell、函数值比较/字段存储与 async spawn 仍待后续。`..create` 明确仅为 Middleware/VM 生命周期步骤，frontend 不生成。
**当前阶段**: **中端（语义分析 + BIL 生成）阶段** —— M35 为中端的开篇里程碑：架构定稿（`compiler/semantic/SEMANTIC_ARCHITECTURE.md`）+ 路线图 S0–S14（`compiler/semantic/SEMANTIC_ROADMAP.md`）+ 语言规范修订（shared/rich/wrapper/String）；M36 落地 S0 诊断基建（`Semantic/Diagnostics.cs` + `CheckSemanticError`），同批完成 ROADMAP 文件级细化（S0–S6）；M37 落地 S1 符号图内核（`Semantic/Symbols/` 四文件 + bootstrap 硬编码 + `CanonicalSymbolPrinter`）；M38 落地 S4 BIL 对象模型 + BilWriter（`Bil/` 五文件，§20 黄金示例逐行一致）；M39 落地 S2 P1 声明收集（`Semantic/CompilationUnit.cs` + `Semantic/DeclarationCollector.cs`，符号图首个真实消费者）；M40 落地 S3 P2 声明解析（`Semantic/DeclarationResolver.cs`，七个子任务全部落地）；M41 落地 S5 P3 最小闭环（`Semantic/Binder.cs` + `Semantic/Bound/` 节点集 + `Semantic/NameResolver.cs` 名字解析共享设施提取 + `Tests/BoundDescribe.cs`，AST → BoundTree）；M42 完成**路径表达式统一**重构（SYNTAX §1.4 忠实落地：表达式位置的符号/调用/索引/成员/wrapper 后缀链统一为单一 `PathExpressionASTNode`，原五节点删除，语义上色全部归 P3）；M43 落地 **native 函数机制**（SYNTAX §4.6：`native` 修饰符 + `@NativeLibrary`/`@NativeSymbol` 内建注解；P1 建壳 + P2 `CheckNativeDeclarations` 全规则校验；BIL §8.4 `native symbol(...) lib(...)` 声明形态 + §8.4.1 全局裸条目 + §22.5 VM 内建 hook 表；RUNTIME §26 `latte_rt` shim 约定）与 **stdlib 内嵌源机制**（`Semantic/StdlibSources.cs` + `stdlib/core/Console.latte`：core.io::Console 的 native print/printErr + Latte 层 println，与用户源同走 P1–P4），同批落地 Binder 宿主类型成员查找、符号 Accessibility（§16）与 Bil 符号段裸条目模型；M44 落地 S6 P4 最小闭环（`Lowering/`：Lowered 节点集 + Lowerer P4a 恒等重写 + BilEmitter P4b 发射，**中端四 pass 全通——hello world 端到端出合法 BIL 文本**），并以 CLI `--emit-bil`/`--sema-only` 接线收官 S6；M45 落地 S7a P4 基础发射补齐（Lowered 节点补齐八类 + Lowerer 覆盖 S5 全部 Bound 节点 + BilEmitter 新发射 set.var/get/set.field.static/§11 运算/带返回值 invoke/new + §19.1 标量资源全形态 + `Tests/LoweredDescribe.cs` 与 LowererTests 套件，**P3 能绑定的全部 Bound 节点均已端到端过 P4**）；M46 落地 S7b（**if 语句/表达式 + 值块 + 短路 and/or + 复合赋值，P3/P4 同步**：P3 新增 BoundIfStatement/BoundValueBlock/BoundIfExpression/BoundReturnValueStatement/BoundCompoundAssignmentExpression 五节点与值块标签栈 return@ 绑定、definite assignment 分支合并、GuaranteesReturn 双分支升级；P4a Lowerer session 化（前置语句机制 + 合成局部 `.sN`）落地短路展开/值块降级与 if 转换/复合赋值脱糖；P4b BilEmitter 多 block 与 §16.2 if 指令发射）；M47 落地 S7c-1（**while/do-while/break/continue 三 pass 落地** + 循环协议定稿（SYNTAX §7.3：范围循环半开 [a,b)、to 即 EnumerateInRange、IEnumerable 双接口）：P3 新增 BoundLoop（施工壳）/BoundLoopControl 与循环标签栈、definite assignment 循环两规则（while 后 = before、do-while 后 = 体尾）、值块内 break/continue 穿透（GuaranteesValueReturn 扩展）、return@ 隔循环边界拦截；P4a Lowerer 循环降级（条件求值移入 Judge 块 + 合成 bool 条件局部 .sN + 合成 .breakid 局部 .bN——LocalSymbol.Type 可空方案 + BoundLoop → BreakId 映射栈）；P4b BilEmitter 发射 loop/loop.rev（§16.3/§16.4 三 block）与 break/continue（§16.5）+ .vars 的 .breakid 条目（§9.3））；M48 落地 S7c-2（**实例成员最小闭环 + core.collections 迭代协议 + for 双形态统一脱糖**：P3 落地 this（宿主统一 method.Owner，含 ext 目标类型）/实例成员链上色（沿 BaseType 链 + 接口 receiver + ext 注册成员）/裸名实例成员补 this/for 双形态（范围循环 = EnumerateInRange ext operator 实例调用 + for-each 协议判定，协议三方法符号挂 BoundLoop，循环变量 const）；P4a for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext + Body 头=current）；P4b 开闸实例方法 fn（.args 的 .this，§9.2/§7.3）/实例 invoke（receiver 首实参）/get.field/set.field（§13.3）/init/operator §8.4 声明形态 + EmitBuiltinExtMembers（内建类型 ext 成员 §8.4.1 裸条目）；stdlib 三源（.bootstrap.latte 基元自举 + core/collections.latte 双接口与 RangeI32/RangeEnumeratorI32）全量同走 P1–P4）；M49 落地 S7d（**switch 语句/表达式 + throw，P3/P4 同步**：异常根 `core.Exception` 定稿进 bootstrap（`IsOpen`，具体子类归 S10 stdlib）；P3 新增 BoundSwitchStatement/BoundSwitchExpression/BoundThrowStatement 六节点 + switch 占位 `_` 栈（BindPath 单段 `_` 命中栈顶）+ 值匹配/pattern 显式分类（值匹配限编译期常量且类型严格相等、pattern 必须 bool）+ throw 异常根 IsAssignable 检查 + GuaranteesReturn/GuaranteesValueReturn 终止口径扩展与 DA 分支合并复用；P4a 常量 switch 恒等降级 + pattern 链降级（selector 物化 `.sN` + 嵌套 if 链 + 合成 cmp.eq 条件）+ switch 表达式结果局部，同批修复 M46 else-if 链值块编织 miscompile（TransformStatements 重写为 continuation 编织）；P4b 发射 switch 指令（§16.6 五操作数 + `switch0-itemN`/`switch0-default` 块 id + `.vars` .breakid 条目）+ §19.4 `switch-table<T>` 单行资源（同 header+元素序列跨 fn 去重）+ throw（§16.9 单操作数）——三形态 CLI 端到端逐行核对一致）；M50 落地 S7e（cast 最小闭环提前自 S8 + try/catch/finally + seq，P3/P4 同步：BoundCast/BoundTry/BoundCatchClause/BoundSeq 双形态六节点 + catch 类型 IsAssignable 到 Exception；P4a LoweredCast/LoweredTry（ExceptionSlot 合成 + catch 头 cast 编织）/LoweredSeqBlock + seq 表达式脱糖 + try-finally 部分终止编织拦截；P4b 发射 cast/cast.safe（§12.1/§12.2）+ call blk(seqN)（§16.1，volatile → §9.6 block 修饰符）+ try 四操作数指令（§16.7）+ §19.5 catch-table 多行资源——**SYNTAX §7 控制流全部贯通**）；M51 落地 S7f-1 字符串插值（spec 定稿 SYNTAX §3.8 toString 机制/插值语义 + RUNTIME §26 原生方法面 toString + BIL §22.5 hook/§11.2 string add 内建拼接；Lexer StringToken RawContent 定位底稿 + Parser StringInterpolationSplitter（配平截取 + 子词法/子解析 + span rebase 精确回源）+ AST 插值段节点；bootstrap String.Add 开放 + Any.toString 承诺/Object open native 默认实现；P3 绑定即规范化（非 String 段包 toString 调用 + 左结合 + 链，P4 零新增节点）；P4a 子类型 cast 物化五位置（receiver/实参/初始化/赋值/return，ARCH §6.1 首个落地）——插值端到端出合法 BIL）；M52 收官 S7f（`?.` 安全调用 + `if?` 空值回退 + 解构声明：nullable BIL 语义定稿（§19.1 null 资源类型即 .nullable\<T\>、§12.1 装箱/展开、§13.3 泛型宿主字段替换判定）；BoundSafeAccess/占位叶子 + P4a 物化/unwrap/wrap 脱糖；`if?` Parser 重组 + P3 严格定型；core.Pair 进 .bootstrap.latte + 构造类型成员查找 ConstructedFrom 回退与泛型字段最小替换（S9 前置）——**S7f 四项全部端到端出合法 BIL**）；M53 前端回补（用户决策的插值架构重构：「Parser 侧拆分」改为「Lexer 层栈嵌套」——字符串层遇 `${` 压基础层正常词法，驱动按 token 层大括号计数配平弹回；StringInterpolationSplitter 与 RawContent 全部删除，SYNTAX §3.8 单行宿主引号限制解除，AST 与 P3/P4 零改动）；M54 细化 S8 为 S8a–S8f（ROADMAP）并落地 S8a（is/supers/with + typeOf 三 pass——SYNTAX §3.5/§3.7 右侧双形态定稿（is/supers/with 先类型后值、with 静态目标必须 wrapper、不做静态不可能性拒绝；typeOf 值/类型双形态先值后类型）+ §3.5 castFrom 笔误修正 + §9.2 补 override 行；BoundTypeCheckExpression（Kind 三态 + TargetType/TargetValue 互斥双槽）/BoundTypeOfExpression 两节点 + Binder 不落袋试探双形态解析（reportErrors: false）；P4a 恒等重写；BilEmitter 首次发射 §12.3 type.is/type.supers/type.with（含三 .indirect 动态形态）与 §12.5 getid.var/getid.type——**六种形态全部端到端出合法 BIL**，is .Case 归 S11 落归口诊断）；M55 完成**中端三树 visitor 化重构**（S8b 前置架构重构，用户决策：Binder/Lowerer/BilEmitter 三个 session 巨石按 CRTP visitor 协议全部重写——静态 Visit 统一入口 + Enter/Exit 生命周期模板 + 双协议 + context 方言接口视图 + 类别分派器 + 结构 visitor 簇级分文件，行为零变化、测试零改动，`Semantic/Binding/` + `Lowering/Rewriters/` + `Lowering/Emitting/` 新组织；同批完成 S8b smart cast 语言规则专项定稿 `compiler/semantic/SMART_CAST_DESIGN.md`）；M56 落地 S8b smart cast 三 pass 全通（SYNTAX §3.5 完整规则 + §3.4 null 判等 + FlowState 收窄事实表 + P4a 物化，SmartCastTests 55 用例新套件）；M57 完成 **BIL 生成全模型对象化重构**（用户决策：Bil/ 从「opcode 字符串 + 位置操作数列表」迁移为强类型模型——指令子类族（拼写/操作数序/多行排版由类固定）+ `BilSpellings` 拼写唯一定义点 + 枚举化种类/修饰符/标量类型 + switch-table/catch-table 专用资源类 + blk/res 操作数持对象引用，BilWriter 删除 opcode switch，行为零变化——黄金文本逐字节一致）；M58 落地 **BIL 验证器 BilVerifier**（用户决策：不推进语言特性、回补质量基建，提前自路线图 S12——`Bil/` 五新文件按 §21 类别覆盖 §21.1–21.8 静态可判子集，配套 `Tests/BilTestHarness.cs` 基建与 BilVerifierTests 新套件，BilEmitterTests/BilWriterTests 全量迁移至验证器框架，CLI `--emit-bil` 接入验证——产出非法即报错不落盘）；M59 落地 S8c（**索引访问 + 实例成员完整化三 pass 全通**：getAtIndex/setAtIndex 运算符绑定（BoundIndexExpression 读/写形态分型）+ 赋值/复合赋值 place 扩展 + PathVisitors 重构表达式底座链泛化（解开全部 S8 归口诊断），多参数索引定稿为编译错误（SYNTAX §13.2 同步）；P4a LoweredIndexExpression 恒等 + P4b §13.6 get.array/set.array 发射 + BilVerifier 严格三元组查询（§6.4 精确匹配）——索引读写与底座链端到端出合法 BIL）；M60 落地 S8d（重载解析 + 默认参数 + 具名参数，纯 P3——SYNTAX §4.2 规则定稿（结构过滤/类型适用性/最具体胜出 + 平局打破）；新设施 OverloadResolution（source-level ranking 唯一落点，BIL §3.3）统一承载调用/init/索引读候选解析；默认参数声明点绑定（BindContext.IsDefaultValueContext 隔离）+ 调用点规范序填充（记忆化按需绑定，前向依赖声明顺序无关）；同批修复位置实参静默覆盖具名占位；语义 fuzz 新套件 SemanticsFuzzTests）；M61 兑现用户决策的规范定稿批次（默认构造 §9.3、循环/catch/finally(e) 变量一律 const §7.3/§8、复合赋值单次求值 §13.2 通用规则、语句 seq 作 return@ 目标 §6.1——复合赋值脱糖重写（副作用目标物化 .sN、纯读取直通零物化）+ BoundSeqExitStatement/LoweredSeqExitStatement 与 TransformStatements 消费/传播编织，勾销技术债 #11/#15①/#17③/#20①/#17②）；M62 完成**巨石拆解批次**（用户决策的纯重构：`Semantic/DeclarationResolver.cs`（1327 行）visitor 化迁移 `Semantic/Resolution/` 12 文件 + `Lexer/LexerLayers.cs` 每类一文件 11 个 + BinderTests/BilEmitterTests/LowererTests 三测试套件 partial 分文件，零行为变化）；M63 落地 **S8e 访问控制 + getter/setter + override 检查**（SYNTAX §16.1/§9.4.1/§9.2.1 规范定稿；符号六槽 + SourceFile 文件身份；P1 访问器壳；P2 AccessChecker 共享设施 + 声明侧接入 + AccessorChecker/OverrideChecker 两新阶段；P3 使用点访问控制（候选过滤先于 ranking）+ 访问器读写检查与体绑定（value 别名/隐含赋值/自动体合成，Bound 节点形态不变）+ 局部访问器归口 S11；P4 声明段开闸（getter(FIELD)/setter(FIELD)/backing/computed/override/abstract 投影，BIL §8.3/§8.4 已定稿形态）+ BilVerifier §21.8 增补——访问器样例端到端出合法 BIL）。M66 落地 **S8f 收官步**（castTo/castFrom 名字分析 + async 边界五项闸门，纯 P3——P1 MethodSymbol.IsAsync 标记位；P2 ConversionOperatorChecker（castTo/castFrom 声明形状：零参数/恰一参数/必声明返回类型，死声明拒绝）与 AsyncGateChecker（声明侧闸门 2 参数/3 返回值/5 泛型约束边界共享安全 + async 仅函数收口）两新阶段；P3 BoundCastExpression.Conversion 槽 + SymbolLookup.FindConversionOperator（单泛型参数代入签名匹配）+ CastVisitor 三级转换优先级（源 castTo → 目标 castFrom → 内建，as? 同分析）；新 Binding/AsyncGates.cs 调用点闸门 1/2（BoundTree 后置遍历单落点）+ LambdaVisitor 闸门 4（async lambda 捕获 AST 扫描）；43 套件 2342 全绿 + CLI 端到端实测）。M64 落地 hint 提示指令 + BIL 全文重编号（BIL_STANDARD 新 §18；§18–§26 重编号为 §19–§27 与全仓库引用/错误码同步；`Bil/BilHintInstruction.cs` 模型 + BilVerifier §21.2/§21.3 检查 + 两套件五用例；VISITOR_REWRITE/SMART_CAST_DESIGN 两文档移除）。M65 完成**函数级 Context 组件化拆分**（用户决策纯重构：BindContext/LowerContext/EmitContext 三平板巨石 narrow 化为「组合根 + 职责组件类」——Bind 侧新 BindFunctionFrame/AccessorBodyState/BindLabelState（四标签栈封装 + 命中查找收编），Lower 侧新 SynthLocalFactory/LowerOutputState/LowerTargetState（五映射栈封装），Emit 侧新 TempVarTable/BlockIdAllocator；设施层签名窄化为组件类型；IFlowContext 删除（组件即方言，兑现 M55 预留）；零行为变化、测试零改动）。M67 完成 **S9 细化 + 规范定稿**（纯文档里程碑）：SEMANTIC_ROADMAP S9 细化为 S9a–S9f 文件级施工清单（S9a 函数体内泛型参数放行（纯 P3）/S9b 泛型调用绑定（纯 P3）/S9c 泛型 new（纯 P3）/S9d 泛型可变参数（纯 P3）/S9e hidden args 物化（P4a/P4b）/S9f stdlib 泛型化 + 技术债勾销）+ 规范定稿落三文档——SYNTAX §4.2（泛型方法必须显式实参零推导；带显式实参候选池仅泛型方法（个数匹配过滤）、不带则泛型方法不参与且仅剩泛型候选时诊断；泛型 operator 名字调用同规则、运算符位置不参与；泛型 init 不存在）/§3.6（使用侧约束满足判定：extends = 实参 IsAssignable 边界、supers = 反向、with = 查 AppliedWrappers（含 interface 传染，构造类型随定义传播）、约束边界含未替换泛型参数跳过检查、ErrorType 静默；覆盖类型引用/泛型调用/泛型 new 三处实例化点）/§4.3（泛型可变参数 TArgs.../named TValues... 类型实参由对应值实参静态类型推导——包固有形态，与固定泛型参数显式实参决策不冲突）+ RUNTIME §10 编译器传参形态注记（.generic.T = .typeid、.generic.TArgs 包形态、调用点 getid.type 物化 + 嵌套泛型调用转发、`.` 开头保留名约定，与 BIL §7 对齐）。M68 落地 **S9a 函数体内泛型参数放行**（纯 P3 步，S9 首段施工——核心架构变更：三树值层类型契约放宽 `TypeSymbol → SemanticSymbol`——BoundExpression.Type/LocalSymbol.Type/LoweredExpression.Type 与 NewTemp/NewSynthLocal/SafeReceiverEntry 等设施签名，泛型参数按引用相等身份使用，P4 侧 CanonicalSymbolPrinter.PrintType 已具 §7.5 `.generic<$.generic.T>` 投影；16 处「使用侧泛型归口 S9」gate 逐一解开（形参/局部 T 声明/字段链读取/索引运算符签名/调用返回类型/is·supers·with 静态目标/typeOf 类型形态/解构分量/范围与 for-each/参数默认值）；SubstituteFieldType 修泛型参数实参替换——构造实参为外层泛型参数时返回实参本身（引用相等身份，`Box<T>` 内 item 类型即外层 T）；TypeReferences.Resolve 放宽返回 SemanticSymbol（`var x: T`/`x as T` 放行，as? 泛型参数目标保守取 T 自身）；语句位置泛型调用静默丢实参漏洞修复（CallForm.TryGet 补 GenericArguments 检查，落回路径绑定归口诊断）；must-return 级联跳过撤销（泛型返回类型恢复全路径检查）；泛型参数 new 归 S9c 诊断；测试：BinderTests 新 TestGenericFunctionBody 组 7 用例（含字段替换身份 ReferenceEquals、typeOf(T) 形态）+ 泛型函数 S9 归口用例改正向 + castTo 泛型 operator 空体补 return（as TTarget 目标放行））。M69 落地 **S9b 泛型调用绑定**（纯 P3 步，SYNTAX §4.2 定稿落地——显式实参唯一零推导）：OverloadResolution 重构为候选视图（CandidateView：定义级符号身份 + 代入后参数/返回类型，双层 SubstituteAll——方法泛型参数按显式实参、宿主泛型参数沿 receiver 构造实参替换），Resolve 返回三元组（胜者/实参/代入后返回类型，调用方据此定型 Bound 节点）；候选池规则（带显式实参仅泛型方法个数匹配过滤、不带时泛型方法不参与且仅剩泛型候选时诊断「需要显式泛型实参」、非泛型带实参诊断）；CallForm.TryGet 提取显式泛型实参（首段/末段被调名位置）+ CallFacility.ResolveGenericArguments 复用 NameResolver 解析 + CallBinding.TypeArguments/ResultType 槽；BoundCallExpression/BoundInstanceCallExpression 增 TypeArguments 槽（P4 发射 .generic.T 依据）；值位置/语句位置/实例链段（a.foo\<T>(x)）/调用底座（foo\<T>(x).c）四形态全接线；新设施 `Semantic/Binding/GenericConstraints.cs` 使用侧约束检查（extends = IsAssignable/supers = 反向/with = 查 AppliedWrappers 回退定义，边界或实参含未替换泛型参数跳过、ErrorType 静默）——接入调用点（Resolve 泛型候选）与函数体内类型引用（TypeReferences.Resolve，嵌套递归）；泛型 operator 名字调用归 S9f 复核（FindInstanceMethods 仅 Regular 的既有边界）；测试：BinderTests 新 TestGenericCalls 组 15 用例（绑定形态/实例泛型方法/个数不匹配/需要显式实参/共存命中/Box\<T extends ValueType> 与自定义约束违反）。M70 落地 **S9c 泛型 new**（纯 P3 步）：NewVisitor 补 ConstructedFrom 回退——构造类型在定义级查 init（修复 `new Box\<i32>(1)` 误报 has no constructor；M69 测试 2 借「构造类型查不到 init」旧行为通过，本步顺势改正为带参正向用例）+ Resolve receiverType 传构造类型（init 宿主泛型参数代入——`new Box\<i32>(1)` 的 init(_ -> item: T) 实参按 i32 绑定）；泛型定义不可构造诊断保留（`new Box(1)` 无实参仍拒绝）；泛型参数 new 归口诊断保持；使用侧约束检查经 TypeReferences.Resolve 已覆盖 new 目标（M69 提前接入）；测试：TestGenericCalls 增补 5 用例（定义级 init 命中/构造类型实参与实参绑定/泛型定义不可构造）。M71 落地 **S9e hidden args 物化**（P4a/P4b，泛型端到端出合法 BIL）：LoweredCallExpression/LoweredInstanceCallExpression/LoweredCallStatement 增 TypeArguments 透传（Bound 侧 M69 已携带）；fn 定义 `.args` 按 §7.2/§9.2 序插入固定泛型隐藏参数 `.generic.T = .typeid`（.return → .this → .generic.* → 普通参数）；调用点前置物化（`EmittingFacility.MaterializeTypeId`——静态实参 `getid.type type(...)` 产 `.typeid` 临时变量（`TempVarTable.NewTypeIdTemp`）、嵌套泛型调用转发 `$.generic.T` 零指令、ErrorType 占位），值位置/实例调用/void 语句三发射点接线；`.type` 声明补 `generic(...)` 子句（§8.2 定稿：泛型参数名逗号列表——BilTypeDeclaration.GenericParameters + Writer 渲染 + LocalSymbolEmitters 填，BIL_STANDARD §8.2 同步定稿）；BilVerifier 三处适配（§5.1 IsLocalIdentifier 放行 `.generic.`/`.vargs.`/`.kwargs.` 保留名家族、§21.3 invoke 实参个数按被调 fn `.args` 隐藏条目数跳过——调用序 `.this → .generic.* → 普通`）；new 指令形态核对（§14.1 第一操作数已含类型实参，零新增）；测试：BilEmitterTests 新 TestGenericEmission 组 6 用例（fn .args 顺序/静态实参 getid.type 物化/嵌套转发零指令/.type generic 子句）。M72 落地 **S9d-1 值可变参数 vargs/kwargs**（P3/P4 同步，泛型可变参数的前置——SYNTAX §4.3 唯一形态是二者成对）：bootstrap 新 `ArrayDefinition`（Object 分支，bilStandardConstructor `.array`，shared 按 T 推导）+ stdlib `core::Pair` 补显式 init（§9.3，kwargs 打包的 `.pair<.string, .any>` 特权构造 §14.1 匹配）；P3 新 `BoundVarArgsArgument` 节点（vargs 位置包/kwargs 具名包，Type = Array\<Any\>，规范参数序最后元素）+ OverloadResolution 单候选可变参数放行（多候选含可变仍归口）+ BindArguments 打包（剩余位置实参归 vargs、具名不在固定表且具名包存在归 kwargs）+ PathVisitors 体内可变参数引用定型 Array\<元素\> + AsyncGates 遍历补包节点；P4 `LoweredVarArgsArgument` 透传（LowerArguments 包直通不 cast——修包被元素类型误 cast）+ EmittingDriver `.args` 末位 `.vargs.<名> = .array<.any>`/`.kwargs.<名> = .array<.pair<.string, .any>>`（普通参数区与 canonical PrintMethod 均跳过可变参数）+ ValueReferenceEmitter 体内引用映射（named 参数 IsVariadic 与 IsNamedVariadic 双 true——具名先判）+ VarArgsEmitter 打包（`new type(.array<.any>)` 元素装箱 cast 到 .any；具名包逐项 `new type(core::Pair<.string, .any>)` 名字资源 + 值装箱后装 array）；测试：BinderTests 可变参数归口用例改正向（打包断言）+ BilEmitterTests 新 TestVarArgsEmission 组 4 用例（.vargs./.kwargs. 条目/体内映射/打包构造）。；M74 落地 S10 core.latte 载入机制 + stdlib 扩充（用户决策四件套：① P1/P2 类型名唯一性按「名 + 泛型元数」判定——Task 与 Task\<TResult\> 同名共存；② `stdlib/core/coroutine.latte` 自举声明协程运行时面（Task/Task\<TResult\>/Executor 家族/PollingAlarm/EventAlarm/CoroutineLocal\<TValue\> 全 shared abstract + sleep native）+ §4.6 native 返回类型放宽至用户引用类型（FFI ABI 归 Middleware）；③ `stdlib/core/exceptions.latte` 四异常子类 + bootstrap Exception 根程序化携带 protected message + pub native getMessage；④ `stdlib/core/disposable.latte`（core.IDisposable）+ P3 async 调用返回类型改写（SYNTAX §4.5 表兑现，await 仍归 S13）；P4b 同步 async 修饰符/语句位置 invoke/验证器 async 结果形态与预定义符号表，SYNTAX §8.1/§15.3 边界定稿；43 套件全绿）。M75 落地 S11 首段（BIL 规范定稿 + 模型增补，纯 Bil 层零行为变化——type.is.case 判别比较 + get/set.wrapper.field 嵌套字段 place 形态 + 判别值注记，ARCH §7.1 缺口兑现；同批修复 M74 遗留两处构建/测试问题）；M76–M80 见里程碑总览表（全仓 review 修复批次/enum case 全链/存疑裁决批次/wrapper place 绑定/ext 收尾）；M81 落地 **S11 proxy 烘焙细化**（S11 剩余拆为 S11a–S11g 七子步 + 烘焙形态定稿：声明侧烘焙骑 vtable、特化体为带 `wrapper-proxy(PROXY_KIND)` 的独立合成 fn 且最终内联归 Middleware、特化符号 P2 合成 P3 逐组合绑定 P4 零 wrapper 语义）+ **技术债 #26 三事裁决**（ext static 明文/priv·protected ext 可见性按声明位置/ext 泛型目标元数与歧义诊断）——纯文档里程碑，规范修订落 SYNTAX §4.4/§14.2/§16.1 + BIL §8.4/§5.1 + RUNTIME §14/§15 + ARCH §5.2/§6.1；M82 落地 S11a（P2 proxy 形状校验与符号合成：WrapperApplication 记录（TTarget 代入显形）+ ProxyShapeChecker 形状校验 + ProxyDispatchResolver（`.wrapper.` 隐藏字段 + Entity 派发链 + 特化/原始体符号合成）+ 发射闸门）；M83 落地 **S11b P3 proxy 体逐组合绑定**（BindingDriver 阶段 2 分流（`.proxy.` 声明体收集、被拦截成员含访问器用户体改挂 WrappedBodySymbol）+ 阶段 2.5 三件套：转发壳（invoke 链首）/特化体（新组件 ProxyBodyState 组合语境 + wildcard 前奏物化——symbol 常量/双包打包/get 的 value = invoke 下一环）/wildcard 解包 shim（逐元素 cast 解包 invoke 下一环）；self = 宿主角色 this、inner = 下一环普通调用、proxy 体 this 重写 BoundWrapperAccessExpression、非 proxy 语境专门诊断、诊断按 (proxy, span, message) 去重；P4 闸门跳过合成 fn 与转发壳——`--sema-only` 零诊断、`--emit-bil` 归 S11d 拦截不落盘；43 套件 3057 全绿）；M84 落地 **S11c P4a/P4b wrapper place 成员访问**（解 M79 归口：BoundWrapperAccessExpression 携带命中应用记录 + 新设施 `WrapperPlaceLowering` 按应用类别分派——Entity 读/调用/索引 = get.wrapper 值拷贝链、写 = set.wrapper.field 链；字段-Value 读写 = wrapper 写链（宿主取字段属主对象）；复合赋值读写分离 + 宿主单次求值共享；`.wrapper.` 隐藏字段 §8.3.1 声明开闸（priv var backing compiler-generated）；深层写穿/索引写/局部与静态存储显式归口；43 套件 3094 全绿；下一步 S11d 合成 fn 发射）；M85 落地 **S11d P4b 合成 fn 发射，烘焙端到端**（Bil 模型 `BilProxyKind` 四态 + `BilWrapperProxyModifier`（§8.4 `wrapper-proxy(PROXY_KIND)`）；LocalSymbolEmitters「.」前缀闸门改分流（烘焙产物发射、proxy 声明模板不进 BIL）+ 修饰符投影（specific/wildcard/original）；LoweringDriver/EmittingDriver 双闸门删除——特化 fn/原始体 fn/转发壳/解包 shim 平铺；BilVerifier §21.8 适配（保留名 ↔ 修饰符双向校验 + kind ↔ 名段一致）；同批修复 wildcard 具名包 ABI 类型不符（§14.7 `Array\<Pair\<String, Any\>\>` 两处同改）；specific/wildcard/get 访问器链三样例端到端出合法 BIL（invoke 原名 → 特化链 → 原始体）；43 套件 3127 全绿）；M86 落地 **S11e `call???` 降级全链**（SYNTAX §14.7 + BIL §15.4 兑现，验收「未声明方法降级端到端样例」达成；规范落地修订——RUNTIME §14.2 泛型逻辑签名实质化为**非泛型胖值签名**：三合成符号（Any.call??? 默认实现/router/降级特化）统一 `(symbol: String, namedArgs: Array\<Pair\<String, Any\>\>, unnamedArgs: Array\<Any\>): Any`，独立泛型 typeid 包取消（Any 胖值自描述 typeid，RUNTIME §2）——结构性必然：双泛型包/双值包/包整体转发无 Bound 层表达（M73 单 GenericPack 槽 + BindArguments 单末位包 + §7.2 序 + 包转发归 S11g）；P2 ComputeDowngradeChains（router = 宿主成员 `call???` + 逐应用降级特化 `.proxy.<序>.???` + Any.call??? 幂等合成）+ PrintDowngradeRequest 请求 symbol 定稿（定义级宿主前缀 + 位置实参只写类型/具名写 名:类型 + 返回段恒 .any）；P3 降级判定（BindInstanceMethodCall candidates 空分支——同名字段 is not a method 优先，沿 BaseType 链找 router，实参预绑 + symbol 字面量 + 双包打包 + ResultType=Any）+ inner 自动补 symbol（合成具名实参）+ BindingDriver 阶段 2.6（router 直通体/特化零前奏绑定/Any.call??? 体 throw NoSuchMethodException）+ 类型兼容五检查豁免（BoundAnalysis.IsDowngradeCallResult 单点——声明初始化/赋值/return/实参三处；转换骑 P4a §6.5 cast 物化，失败抛 CastException）；P4 双闸门删除 + wrapper-proxy(router) 投影；BilVerifier §21.8 放行 call???↔router + §21.2 builtin 宿主 fn 定义豁免 + 预定义符号表补 core::Any$call???；43 套件 3196 全绿）；M87 落地 S11f 派发链诊断工具（CLI `compile --explain-dispatch`，44 套件 3220 全绿）；**M88 落地 wrapper 烘焙架构反转**（用户决策推翻 M81 定稿①③：烘焙/派发链合成/inner 链接/原始体替换/隐藏存储/call??? 类别路由体全部归 Middleware，编译器只携带标记——M88a 规范修订 RUNTIME §14/§15、BIL §5.1/§5.3/§8.3.1/§8.4/§12.5/§13.3/§15.4/§15.5/§21/§22.5、ARCH §5.2 等；M88b 代码：删 ProxyDispatchResolver 与全部合成符号槽，新 ProxyMatching/ProxyMatchChecker，bootstrap Any.call??? 为 VM hook 内建，proxy 体改模板态绑定（BoundSelf/BoundInnerCall + get.self/invoke fn(..inner)），降级资格判定 + invoke core::Any$call???，BilProxyKind 两态 + wrapped(W) 应用标记 + wrapper 写链 field|wrapper 两态，DispatchExplainer 改应用登记×匹配预览；同批 #26 代码落地（ext 泛型目标元数/歧义 + ext 可见性按声明位置）+#27⑧/M79 param:W 落地；约 3119 确定性用例 + fuzz 6000 + 语义 fuzz 3000，44 套件全绿，CLI 冒烟三样例端到端合法 BIL）
前端里程碑回顾：Parser/PDA 大扫除（M23）、AST 结构标注与 Validator 重写（M24）、Lexer 修复与 fuzz 基建（M25）、日志与 AST JSONL（M26）、CLI 插件化（M27）、Lexer 位置与 AST Span（M28）、AST 容器重构（M29）、Utilities 拆分（M30）、前端大修（M31）、多行字符串（M32）、值块统一（M33）、技术债清扫（M34）。
**测试总计**: 3441/3441 确定性用例全绿 + Lexer fuzz 6000/6000 + 语义 fuzz 3000/3000（44 个套件，`dotnet run -- test --all` 单命令全量）
**版本控制**: Git `main` 分支（2026-07-17 首次提交）

---

## 1. 里程碑总览

| # | 里程碑 | 状态 | 完成日期 | 测试 |
|---|--------|------|----------|------|
| M102 | **S13 lambda P4 B0**：无捕获 lambda 作为局部值降级为 `.methodid` 句柄；新增强类型 `getid.method`、`invoke.indirect`/`invoke.indirect.noret`、canonical methodid 签名解析与 verifier 参数/返回形态校验；lambda synthetic body 进入既有 bodies 管线，synthetic fn declaration 进入 LocalSymbols，捕获 lambda 在 `LambdaRewriter` 保持 P4 pending | ✅ | 2026-08-08 | 3441/3441 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M101 | **S13 普通 lambda P3 Slice A**：新增不进入用户符号图的 `LambdaTypeSymbol` 与 `BoundLambdaExpression`；lambda 使用隔离 `Scope/BindContext`，参数、返回类型、单表达式/显式 `return@` 块体均绑定；捕获按 `LocalSymbol`/`ParameterSymbol`/`this` 身份记录，lambda 入口仅继承外层 DA，不传播 smart-cast，嵌套捕获向外传递；async lambda 补参数/返回/捕获共享安全闸门；P4 closure environment/cell、lambda invoke 和 async spawn 仍 pending | ✅ | 2026-08-08 | 3429/3429 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M100 | **S13 `using` 表达式形态切片**：`BoundSeqExpression` 携带 using 资源绑定；P3 复用语句形态的 IDisposable/dispose/只读资源校验与前序资源作用域；P4a 先完成值块结果局部写入，再以完整值块为 protected body 生成逆序 nested try/finally，finally 后返回结果局部；AsyncGates 遍历 initializer/dispose；BIL/verifier 复用既有 try/invoke，复杂 outer value-block continuation 保留 S7e P4 拦截 | ✅ | 2026-08-08 | 3415/3415 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M99 | **S13 `using` 语句形态切片**：P3 新增 `BoundUsingBinding`，按源码序绑定资源初始化器、`core.IDisposable` 兼容性与无参 `dispose` 符号；P4a 将资源声明与 body 包成嵌套 `LoweredTryStatement`，初始化失败只清理成功前缀，finally 按逆序发 dispose；P4b/BIL/verifier 复用既有 try/invoke，无新增 opcode；using 资源即使源写 `var` 也禁止重赋值，async/open/abstract dispose 保守拒绝；表达式 using 保留明确 S13 pending 诊断 | ✅ | 2026-08-08 | 3394/3394 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M98 | **S13 `yield` 垂直切片**：新增 `BoundYieldStatement`、`LoweredYieldStatement`、`YieldInstruction` 与 P3/P4/verifier 全链；裸 `yield` 与 `yield Alarm` 均允许出现在普通函数、async 函数和 `main`，Alarm 按赋值兼容接受 `PollingAlarm`/`EventAlarm` 及其用户子类；yield 后清除 smart-cast 但保留 DA，循环和 try/finally 保持非终止透传；补齐 guard 内嵌 await 递归屏障、泛型 Alarm 约束和运行时未声明 Alarm 的 verifier 防误报 | ✅ | 2026-08-08 | 3371/3371 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M97 | **S13 async lowering 首个 `await` 垂直切片**：新增 `docs/compiler/semantic/ASYNC_LOWERING_DESIGN.md`，定稿 BIL §17 保留强类型协程指令、async `invoke` eager spawn、Middleware 状态机/continuation/GC fence 边界；P3 新增 `BoundAwaitExpression`，精确接受 `core.coroutine.Task` 与 `Task<T>`，无结果 await 仅允许表达式语句，await 后清除 smart-cast 但保留 DA；P4a 新增 `LoweredAwaitExpression`，P4b/BIL 新增 `AwaitInstruction` 发射 `await TASK [RESULT]`；BilVerifier 增补 §21.3 Task/结果类型与读写/DA 校验；值块、guard、括号透明分组边界均有回归测试 | ✅ | 2026-08-08 | 3349/3349 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M96 | **wrapper 继承闭包、getter/setter 多态与泛型型变**：间接基类/interface wrapper 应用必须在子声明处显式重复，方法及 accessor override 继承 wrapper 检查保持定义/实参/顺序；getter/setter 分别支持 `open`/`override`，禁止全局/static accessor 多态；新增 `VarianceChecker`、`GenericParameterSymbol.Variance`、P3 构造类型协变/逆变赋值、BIL `generic(...)` 型变元数据与调用签名 verifier 兼容 | ✅ | 2026-08-07 | 见全量测试 |
| M95 | **源码 `super(...)` 与 BIL `..super` 全链**：override/init 直接基类候选、独立 Bound/Lowered 标记、固定泛型隐藏参数转发、P4 `$.this` 首参与 verifier 专用规则；wrapper 继承检查与 variance 不在本项范围，后由 M96 补齐；`..create` 保持 Middleware/VM 生命周期专属 | ✅ | 2026-08-07 | 见全量测试 |
| M94 | **BIL `..inner` invoke 协议迁移**：删除专用 inner 指令模型，P4 统一发射 `invoke` / `invoke.noret` 的 `fn(..inner)` 保留目标；BilVerifier 在 canonical 方法解析与 MethodSymbols 检查前分流，限制 proxy 模板、拒绝 receiver、保留泛型包顺序校验并严格校验返回形态与结果类型；全仓规范、历史记录与测试同步 | ✅ | 2026-08-07 | 见全量测试 |
| M93 | **彻底移除旧嵌套字段读指令**：读侧统一 `get.wrapper`/`get.wrapper.field` 值拷贝 + 普通 `get.field`；写侧保留 `set.wrapper.field`；`LoweredWrapperFieldExpression` 仅作写 place；Bil 模型/验证器/发射/测试/规范全仓清扫 | ✅ | 2026-08-07 | 见全量测试 |
| M92 | **`call???` 技术债 #28① 收口**：降级请求支持显式泛型实参，采用 `Host$method<.T,...>(参数段)@.any` canonical 形态；P3 复用使用点泛型解析/访问检查，P4 继续固定 `Any.call???` 三参胖值 ABI | ✅ | 2026-08-07 | 3271/3271 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M91 | **M84 wrapper place 遗留裁决与两项解归口**：字段-Value 方法调用/索引读新增 `get.wrapper.field OBJECT field(HOST_FIELD) type(W) RESULT` 值拷贝；字段应用 wrapper 寻址复用既有 `field(HOST_FIELD), wrapper(W)` 对消除同 owner 同 W 歧义；深层纯字段写穿由 P4a 展开为正向 get + 叶写 + 按值类型边界反向 set，最外层必要写回复用现有 `set.wrapper.field`，不新增深写 opcode；索引写、局部/静态存储继续显式归口 | ✅ | 2026-08-07 | 3267/3267 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M90 | **#27⑦ 可变参数成员链显式包透传**：`BoundInnerCallExpression` / `LoweredCallInnerExpression` 显式携带可变泛型包声明序；P4b 将 `.generic.<Pack>` 前置于 invoke fn(..inner) 值包；可变成员参与 specific/wildcard 匹配，包解包与烘焙仍归 Middleware；BilVerifier 校验包前缀同序同型 | ✅ | 2026-08-07 | 3208/3208 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M89 | **`call???` 技术债 #28③④ 收口**：interface wrapper 传染宿主的降级资格扩展到 BaseType + Interfaces 传递闭包（定义级只读查询，不回写应用槽）；if?/throw/复合赋值/索引写位置补降级 Any 的 P3 豁免与 P4a §6.5 cast 物化 | ✅ | 2026-08-07 | 3183/3183 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M1 | P0 核心基础（字面量/类型引用/变量声明） | ✅ | 2026-07-17 | 28/28 |
| M2 | 结果传递机制（IResultProducer/IResultConsumer） | ✅ | 2026-07-17 | 含于各套件 |
| M3 | 泛型语法迁移 `\<...>`（文档 + Lexer + SymbolLayer） | ✅ | 2026-07-17 | 18/18 |
| M4 | GenericParametersParserLayer（roadmap #22） | ✅ | 2026-07-17 | 21/21 |
| M5 | 表达式后缀链 + ArgumentListParserLayer（roadmap #4 大部分） | ✅ | 2026-07-17 | 54/54 |
| M6 | ParameterListParserLayer（roadmap #5） | ✅ | 2026-07-17 | 14/14 |
| M7 | P2 语句系统核心（CodeBlock/if 语句/循环/return/赋值） | ✅ | 2026-07-18 | 42/42 |
| M8 | P1 收尾（Lambda/if/switch 表达式 + typeOf/as/is） | ✅ | 2026-07-18 | 39/39 |
| M9 | TryCatchFinallyParserLayer（roadmap #10） | ✅ | 2026-07-26 | 9/9 |
| M10 | SeqBlockParserLayer（roadmap #11，含表达式形态） | ✅ | 2026-07-26 | 17/17 |
| M11 | throw 语句 | ✅ | 2026-07-26 | 10/10 |
| M12 | CoroutineOps：await/yield（roadmap #12） | ✅ | 2026-07-26 | 13/13 |
| M13 | P3 类型声明解析基础（DeclarationParserLayer 重构） | ✅ | 2026-07-26 | 16/16 |
| M14 | 统一声明层：全局/成员/嵌套共用一套 infra | ✅ | 2026-07-26 | 36/36 |
| M15 | 声明泛型参数接入统一声明层（类型/函数/operator） | ✅ | 2026-07-26 | 15/15 |
| M16 | 属性访问器 getter/setter（§9.4 三类位置，roadmap #23） | ✅ | 2026-07-26 | 17/17 |
| M17 | enum struct 的 `[]` case 列表（固定/参数化 case、显式判别值） | ✅ | 2026-07-26 | 10/10 |
| M18 | init 参数映射（`_ -> field`，SYNTAX §9.3，roadmap #18 收尾） | ✅ | 2026-07-26 | 7/7 |
| M19 | `like` 委托（§9.6）+ `ext` 扩展成员（§4.4）—— **P3/P4 收官** | ✅ | 2026-07-26 | 9/9 |
| M20 | P5 起步：wrapper 主体（@ 注解 + `.proxy.*` 代理成员 + 前导点 enum case） | ✅ | 2026-07-26 | 23/23 |
| M21 | 模块系统 import（§15.2）+ wrapper 路径访问（`:`，§14.1/§3） | ✅ | 2026-07-26 | 18/18 |
| M22 | namespace 声明（§15.1）—— **P5 收官** | ✅ | 2026-07-26 | 7/7 |
| M23 | Parser/PDA 大扫除：TokenDisposition、施工目标协议、ExpressionRootASTNode、EOF 正式化、AST 完整性验证、测试基础设施 | ✅ | 2026-07-26 | 425/425（22 套件） |
| M24 | AST 结构标注（ChildAstNode/ParentAstNode/AstCarrier）+ Validator 重写 + 删除 ASTNodeType + 5 个父子指针 bug 修复 | ✅ | 2026-07-26 | 430/430（23 套件） |
| M25 | Lexer 修复：SlashLexerLayer（除法/注释分流）+ Lexer 输出 EOF + 注释集中跳过 + fuzz 基建 | ✅ | 2026-07-26 | 453/453 + fuzz 6000（24 套件） |
| M26 | 日志系统（Logger 分级 + verbose 默认关闭 + JSONL 落盘）+ AST JSONL 序列化诊断 | ✅ | 2026-07-26 | 498/498 + fuzz 6000（26 套件） |
| M27 | CLI 插件化重构：`<COMMAND> [--sub-cmd...]`（help/compile/test）+ CommandLineMask + 帮助程序生成 + 交互菜单删除 | ✅ | 2026-07-27 | 544/544 + fuzz 6000（27 套件） |
| M28 | Lexer 位置修复（offset/列号/EOF 冲刷/token 头/sourceName 单源化）+ AST Source Span（ISpanReceiver 层 span 回填）+ ASTVisitor 统一遍历 + Validator span 检查与类型审计 | ✅ | 2026-07-27 | 556/556 + fuzz 6000（27 套件） |
| M29 | AST 容器重构：基类共有 `Children`/`Annotations` 删除；语义字段（`Declarations`/`Statements`/`Members`）+ wrapper 挂载接口（`IWrapperAttachable` + Entity/Method/Value 三分类） | ✅ | 2026-07-27 | 27 套件全绿（用例无增删）+ fuzz 6000 |
| M30 | `Core/Utilities.cs` 拆分（Token/Keywords/异常/AST 基类归位 7 文件）+ ASTVisitor 遍历可重载（VisitNode/EnumerateChildren virtual）+ 文档幽灵清理（FRONTEND_TYPES 修订、FRONTEND_ARCHITECTURE 删除、ROADMAP 头注） | ✅ | 2026-07-27 | 27 套件全绿（用例无增删）+ fuzz 6000 |
| M31 | 前端大修：全量 review 驱动的 40+ 项修复（Span 左闭右开、Lexer 块注释重写、续行规则、位运算符、0b/0o/下划线字面量、Keywords 大扫除、修饰符/标识符校验、JSONL v2 + 反序列化器、测试基建统一） | ✅ | 2026-07-28 | 746/746 + fuzz 6000（29 套件） |
| M32 | 多行字符串 `"""`：SYNTAX §3.3 规范定稿（Swift 风格严格多行）+ QuoteLexerLayer 引号分流 + MultilineStringLexerLayer 两阶段施工 + 转义表单源化 + 插值标记词法期判定（`\${` 误报修复；Parser/AST 经 StringToken 复用近零改动） | ✅ | 2026-07-28 | 790/790 + fuzz 6000（30 套件） |
| M33 | 值块统一：if/switch 表达式分支体与 lambda 体统一为代码块（多语句 + `return@_`/named 取值）、switch 语句形态（新 SwitchStatementASTNode）、lambda 体内裸 return 编译错误（allowBareReturn 全链传染）、seq 匿名默认标签 `seq`→`_` | ✅ | 2026-07-28 | 838/838 + fuzz 6000（30 套件） |
| M34 | 技术债清扫：字符字面量（CharLexerLayer + CharToken + CharLiteralASTNode）、复合赋值 10 运算符（CompoundAssignmentExpressionASTNode）、`is` 右侧 enum case（TypeCheck TargetType/TargetCase 双字段互斥）、wrapper `.name` 保留参数名、import `{}` 单标识符禁令规则化报错 —— **前端阶段收官** | ✅ | 2026-07-28 | 895/895 + fuzz 6000（30 套件） |
| M35 | **中端阶段开篇**：语义分析与 BIL 生成架构定稿（四 pass + 双 Bound Tree + 驻留符号图）+ 路线图 S0–S14 + 语言规范修订（String 归非 rich 值类型、wrapper 恒 rich struct 且 `obj:Wrapper` 为只读 place、共享安全类型与两条逃逸闸门、rich/shared 单向传染、非 rich struct 不得 open/abstract） | ✅ 文档 | 2026-07-29 | 895/895 + fuzz 6000（30 套件，纯文档无增删） |
| M36 | S0 诊断基建（中端第一段代码）：`Semantic/Diagnostics.cs`（Diagnostic + DiagnosticBag 可恢复诊断模型）+ `CheckSemanticError` 入 TestHarness + ROADMAP S0–S6 文件级细化 | ✅ | 2026-07-31 | 910/910 + fuzz 6000（31 套件） |
| M37 | S1 符号图内核：`Semantic/Symbols/`（SemanticSymbol 家族 + SymbolGraph 驻留 + BootstrapSymbols 硬编码层级/基元/intrinsic 键空间 + CanonicalSymbolPrinter 五形态 + BIL 类型引用投影） | ✅ | 2026-07-31 | 977/977 + fuzz 6000（33 套件） |
| M38 | S4 BIL 对象模型 + BilWriter：`Bil/` 五文件（Module/Resources/Symbols/Function/Instructions + Writer，对中端零依赖、字符串身份、§17 协程暂缓），§20 黄金示例逐行一致 | ✅ | 2026-07-31 | 983/983 + fuzz 6000（34 套件） |
| M39 | S2 P1 声明收集：`Semantic/CompilationUnit.cs` + `Semantic/DeclarationCollector.cs`（符号壳 + namespace 驻留合并 + import 登记 + ext 待注册 + 重复声明诊断） | ✅ | 2026-07-31 | 1066/1066 + fuzz 6000（35 套件） |
| M40 | S3 P2 声明解析：`Semantic/DeclarationResolver.cs`（类型引用解析 + ErrorType 毒化、继承/implements 图与双环检测、修饰符合法性、rich/shared 字段闭包与单向传染、共享安全闸门、泛型约束声明侧、ext 注册 + wrapper 适用性矩阵）+ Parser 两处越权拦截移交 P2 + 约束裸名参数 Parser 修复 + bootstrap 注册 Core.Types | ✅ | 2026-07-31 | 1204/1204 + fuzz 6000（36 套件） |
| M41 | S5 P3 最小闭环：`Semantic/Binder.cs` + `Semantic/Bound/` 节点集（AST → BoundTree）+ `Semantic/NameResolver.cs`（P2/P3 名字解析共享设施提取）+ LocalSymbol + `Tests/BoundDescribe.cs` | ✅ | 2026-07-31 | 1294/1294 + fuzz 6000（37 套件） |
| M42 | 路径表达式统一（SYNTAX §1.4）：表达式位置五节点（SymbolReference/Call/Index/MemberAccess/WrapperAccess）删除，统一为 `PathExpressionASTNode`（首段 + 段 + 后缀）；ExpressionParserLayer 后缀链重写、Binder BindPath 适配、171 用例快照迁移 | ✅ | 2026-07-31 | 1295/1295 + fuzz 6000（37 套件） |
| M43 | native 函数机制（SYNTAX §4.6 + BIL §8.4/§8.4.1/§22.5 + RUNTIME §26）+ stdlib 内嵌源载入（`Semantic/StdlibSources.cs` + `stdlib/core/Console.latte`）+ Binder 宿主成员查找 + 符号 Accessibility + Bil 段裸条目模型 | ✅ | 2026-07-31 | 含于全量（38 套件） |
| M44 | S6 P4 最小闭环：`Lowering/Lowered/` 节点集 + `Lowering/Lowerer.cs`（P4a 恒等重写）+ `Lowering/BilEmitter.cs`（P4b 发射）——hello world 端到端出合法 BIL 文本，中端四 pass 全通；CLI `--emit-bil`/`--sema-only` 接线收官 S6 | ✅ | 2026-07-31 | 1393/1393 + fuzz 6000（39 套件） |
| M45 | S7a P4 基础发射补齐：Lowered 节点补齐八类（局部声明/表达式语句/赋值 + 字段引用/二元/一元/带返回值调用/new）、Lowerer 覆盖 S5 全部 Bound 节点、BilEmitter 新发射（set.var、get/set.field.static、§11 运算单点映射、invoke、new、§19.1 标量资源全形态）+ `Tests/LoweredDescribe.cs` + LowererTests 套件 | ✅ | 2026-08-01 | 1439/1439 + fuzz 6000（40 套件） |
| M46 | S7b if 语句/表达式 + 值块 + 短路 and/or + 复合赋值（P3/P4 同步）：Bound 五节点（BoundIfStatement/BoundValueBlock/BoundIfExpression/BoundReturnValueStatement/BoundCompoundAssignmentExpression）+ 值块标签栈 return@ 绑定 + definite assignment 分支合并（before∪(setT∩setF)）+ GuaranteesReturn 双分支升级；Lowerer session 化（前置语句机制 + 合成局部 `.sN` + 短路展开 + 值块降级与 if 转换 + 复合赋值脱糖）；BilEmitter 多 block（§16.2 `if $c blk blk`、无 else 用 none、block id `if0-then` 形态、分支块落尾不补 ret） | ✅ | 2026-08-01 | 1514/1514 + fuzz 6000（40 套件） |
| M47 | S7c-1 while/do-while/break/continue（P3/P4 同步）+ 循环协议定稿（SYNTAX §7.3/§13.2/§15.3）：BoundLoop（施工壳，循环标签栈）/BoundLoopControl + DA 循环两规则（while 后 = before、do-while 后 = 体尾）+ 值块穿透（GuaranteesValueReturn 扩展，BIL §16.5）+ return@ 隔循环边界拦截；Lowerer 循环降级（条件求值移入 Judge 块 + 合成 bool 条件局部 `.sN` + 合成 .breakid 局部 `.bN`——LocalSymbol.Type 可空方案 + BoundLoop → BreakId 映射栈；break/continue 真跳转对 if 转换零改动）；BilEmitter 发射 loop/loop.rev（§16.3/§16.4 三 block，`loop0-body`/`loop0-judge` 块 id）与 break/continue（§16.5）+ `.vars` 的 `.breakid` 条目（§9.3，Type null 投影） | ✅ | 2026-08-01 | 1567/1567 + fuzz 6000（40 套件） |
| M48 | S7c-2 实例成员最小闭环 + core.collections 迭代协议 + for 双形态（P3/P4 同步）：BoundThis/BoundInstanceCall/BoundFieldAccess 三节点 + 实例链上色（沿 BaseType 链 + 接口 receiver + ext 注册成员，宿主统一 method.Owner）+ 裸名实例成员补 this + for 绑定（范围 = EnumerateInRange ext operator 调用、协议判定含「自身即构造」分支、协议三方法挂 BoundLoop、循环变量 const）；for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext + Body 头=current）；BilEmitter 开闸 .this（§9.2/§7.3）/实例 invoke receiver 首参/get.field/set.field（§13.3）/init/operator §8.4 声明形态 + EmitBuiltinExtMembers（内建类型 ext 成员 §8.4.1 裸条目）；stdlib 三源（.bootstrap 基元自举 + collections 双接口/RangeI32/RangeEnumeratorI32）全量过 P1–P4 | ✅ | 2026-08-01 | 1634/1634 + fuzz 6000（40 套件） |
| M49 | S7d switch 语句/表达式 + throw（P3/P4 同步）+ 异常根定稿（`core.Exception` 进 bootstrap，IsOpen，具体子类归 S10）：BoundSwitchStatement/BoundSwitchExpression/BoundThrowStatement 六节点 + switch 占位 `_` 栈 + 值匹配/pattern 显式分类（常量限定 + 类型严格相等 / pattern 必须 bool）+ throw IsAssignable 到 Exception + GuaranteesReturn 终止口径扩展；P4a 常量 switch 恒等 + pattern 降级嵌套 if 链（selector 物化 `.sN` + 合成 cmp.eq 条件）+ switch 表达式结果局部，同批修复 M46 else-if 链值块编织 miscompile（TransformStatements → continuation 编织）；BilEmitter 发射 switch 指令（§16.6 五操作数 + `switch0-itemN`/`switch0-default` 块 id + .breakid 条目）+ §19.4 switch-table 单行资源（同表跨 fn 去重）+ throw（§16.9） | ✅ | 2026-08-01 | 1688/1688 + fuzz 6000（40 套件） |
| M50 | S7e cast 最小闭环 + try/catch/finally + seq（P3/P4 同步，cast 提前自 S8）：BoundCastExpression（as/as?）+ BoundTryStatement/BoundCatchClause + BoundSeqStatement/BoundSeqExpression 六节点（含 BoundValueBlock.IsVolatile）+ catch 类型 IsAssignable 到 Exception + 语句 seq 不压值块栈；P4a LoweredCastExpression/LoweredTryStatement（ExceptionSlot 合成 + catch 头 cast 编织）/LoweredSeqBlock 四节点 + seq 表达式脱糖（合成结果局部 + 前置 seq 块）+ try-finally 部分终止编织拦截；BilEmitter 发射 cast/cast.safe（§12.1/§12.2）+ call blk(seqN)（§16.1，volatile → §9.6 block 修饰符）+ try 四操作数指令（§16.7）+ §19.5 catch-table 多行资源——**SYNTAX §7 控制流全部贯通** | ✅ | 2026-08-01 | 1780/1780 + fuzz 6000（40 套件） |
| M51 | S7f-1 字符串插值 + toString 机制 + String 拼接开放 + 子类型 cast 物化（P1–P4 全链路）：spec 定稿（SYNTAX §3.8 toString/插值语义 + RUNTIME §26 原生方法面 toString + BIL §22.5 hook/§11.2 string add）；Lexer StringToken.RawContent/MultilineIndent/IsMultiline 定位底稿；AST StringInterpolationPart（[AstCarrier] 段级字面量子结构/表达式 Root 互斥双字段）；Parser StringInterpolationSplitter（RawContent 一体化扫描 + 配平截取 + 子词法/子解析 + span rebase 精确回源）；bootstrap String.Add + Any.toString 承诺/Object open native 默认实现；P3 绑定即规范化（非 String 段包 toString 调用 + 左结合 + 链，P4 零新增节点）；P4a 子类型 cast 物化五位置（receiver/实参/初始化/赋值/return——ARCH §6.1 首个落地） | ✅ | 2026-08-01 | 1821/1821 + fuzz 6000（40 套件） |
| M52 | S7f 收官：`?.` 安全调用 + `if?` 空值回退 + 解构声明（P3/P4 同步 + Parser 回补）+ nullable BIL 语义定稿（§19.1 null 资源类型即 .nullable\<T\>——null 检查 = cmp.ne + null 资源；§12.1 nullable 装箱/展开；§13.3 泛型宿主字段替换判定）：BoundSafeAccess/占位叶子 + P4a 物化/unwrap/wrap 脱糖；`if?` Parser 重组（中缀 if + ?）+ P3 严格定型 + 延迟求值脱糖；core.Pair 进 .bootstrap.latte + Parser 解构分支 + P3 构造类型成员查找 ConstructedFrom 回退与泛型字段最小替换（S9 前置）+ P4a 物化/逐字段读取——**S7f 四项全部端到端出合法 BIL** | ✅ | 2026-08-01 | 1872/1872 + fuzz 6000（40 套件） |
| M53 | 插值词法帧机制（前端回补，用户决策的架构重构）：插值解析从「Parser 侧拆分」改为「Lexer 层栈嵌套」——字符串层遇 `${` 压基础层正常词法，驱动按 token 层大括号计数配平弹回（字符串/字符/注释内容天然豁免）；InterpolationStart/End 标记 token + 驱动插值帧栈 + StringLexerLayer 挂起 `$` 判定 + MultilineStringLexerLayer 段结算与闭界统一回填 + LiteralParserLayer 段序列状态机；StringInterpolationSplitter（~280 行）与 RawContent/MultilineIndent/IsMultiline 全部删除；SYNTAX §3.8 单行宿主引号限制解除（`"a${"b"}c"` 合法）；AST 与 P3/P4 零改动 | ✅ | 2026-08-01 | 1878/1878 + fuzz 6000（40 套件） |
| M54 | S8 细化（S8a–S8f 进 ROADMAP）+ S8a is/supers/with/typeOf 三 pass（P3/P4 同步）+ SYNTAX §3.5/§3.7 右侧双形态定稿（is/supers/with 先按类型引用解析、失败按值绑定且必须 Type\<T\>、同名类型优先；with 静态目标必须 wrapper；不做静态不可能性拒绝；typeOf 双形态——值形态定型 Type\<操作数静态类型\>、单段裸名先值后类型）+ §3.5 castFrom 笔误修正 + §9.2 补 override 行：BoundTypeCheckExpression（Kind 三态 + TargetType/TargetValue 互斥双槽，恒 bool）/BoundTypeOfExpression（Operand/TargetType 互斥）两节点 + Binder 不落袋试探双形态解析（ResolveSymbolPath reportErrors: false + ErrorTypeSymbol 显式排除）+ 动态形态 Type\<T\> 校验 + DA 未赋值检查；Lowered 同构两节点恒等重写；BilEmitter 首次发射 §12.3 type.is/type.supers/type.with（含三 .indirect 动态形态）与 §12.5 getid.var/getid.type（Bil/ 零改动——通用 opcode 模型）；is .Case 归 S11 落归口诊断——**六种形态全部端到端出合法 BIL** | ✅ | 2026-08-01 | 1924/1924 + fuzz 6000（40 套件） |
| M55 | **中端三树 visitor 化重构**（S8b 前置，用户决策的架构重构）：Binder（2890 行）/Lowerer（1331 行）/BilEmitter（1045 行）三个 session 巨石全部 visitor 化——CRTP 协议基类（静态 Visit 唯一入口 + Enter/Exit 生命周期模板，栈压弹 finally 固化）+ 双协议（Visit→TResult? 上行合成 / VisitInto 壳填充）+ context 方言（同一函数级状态对象的接口视图，Environment 只读共享）+ 类别分派器唯一 switch + 结构 visitor 簇级分文件；否定超大 partial（状态污染）；停线重写 + Binder 先行全链验证后复制（Lowerer/BilEmitter）；协议 v2 修正（scope/expectedType 下传参）；FlowState 提取（DA 的家，S8b 收窄表预留）；行为零变化（三树各自完成时 40 套件 1924 全绿，BilEmitter 黄金文本逐字节一致，测试零改动，0 新警告）；同批完成 S8b smart cast 专项定稿（SMART_CAST_DESIGN.md：Q1=B 含 const 字段收窄/Q2=A guard/Q3=A and-or-not/Q4=A switch 占位；发现 null 判等前置缺口） | ✅ | 2026-08-03 | 1924/1924 + fuzz 6000（40 套件） |
| M56 | S8b smart cast 三 pass 全通（M55 定稿落地）：SYNTAX §3.5 完整规则 + §3.4 null 判等段；FieldSymbol.IsConst + const 字段赋值检查（init 豁免，兑现 M41 技术债）；null 判等绑定（装箱 cast §12.1 + §11.5 合规）；FlowState 收窄事实表（NarrowKey 根+const 字段链，纯交集合并——收窄非单调与 DA 区分）+ ConditionFactsExtractor（真/假边事实对）+ BoundSmartCastExpression 标记；收窄应用点（if guard 反向传播（DA 不变）/and-or 右侧上下文/while 体真边+体赋值根剔除/switch `(_ is T)` selector 收窄/赋值失效/字段链稳定判定）；P4a 物化 LoweredCastExpression（P4b 零新增）；SmartCastTests 新套件（注册表 #41） | ✅ | 2026-08-03 | 1979/1979 + fuzz 6000（41 套件） |
| M57 | **BIL 生成全模型对象化重构**（用户决策：去魔法 string）：`BilInstruction` 从 opcode 字符串 + 位置操作数列表改为强类型子类族（`Bil/BilInstructions.cs` 基类 + Compute/Data/ControlFlow 三指令文件按规范章节划分——opcode 拼写、操作数个数/类型/顺序、switch/try 多行排版由类固定；§11 `BilBinaryOp`/`BilUnaryOp`、§12.3 `BilTypeCheckKind` 枚举）；`BilSpellings` 全部拼写唯一定义点 + 六枚举（`BilTypeKind`/`BilMemberKind`/`BilBlockModifier`/`BilAccessibility`/`BilKeyword`/`BilScalarType`）+ `BilModifier` 子类族（访问/关键字/operator(名)/symbol(...)/lib(...)）；`BilSwitchTableResource`/`BilCatchTableResource` 专用资源类（header/元素自渲染，EmittingFacility 字符串拼接删除）；`BilBlockOperand`/`BilResourceOperand` 持对象引用（悬空 blk/res 引用不可构造）；BilWriter 删除 opcode switch（指令自渲染 WriteTo）；P4b 值发射契约 string → `BilVariableOperand`；行为零变化（黄金文本逐字节一致、CLI 样例 diff 字节一致、测试用例数不变） | ✅ | 2026-08-03 | 1979/1979 + fuzz 6000（41 套件） |
| M58 | **BIL 验证器 BilVerifier + BIL 测试迁移**（用户决策：回补质量基建，提前自 S12）：`Bil/` 五新文件按 §21 类别 partial 分文件（§21.1–21.8 静态可判子集；防误报降级——.generic< 跳过/基名兼容/IsAssignableTo 链判定/保守 DA；预定义符号表收技术债 #16；entrypoint 结构化终止；双 switch default 防腐化）+ `Tests/BilTestHarness.cs`（EmitBilUnit 共享 + 验证器断言 + res 重编号形状黄金）+ BilVerifierTests 新套件（全管线正例零错误 + §21 逐类负例）+ BilEmitterTests 全量迁移（私有渲染器与全模块黄金删除）+ BilWriterTests 自足模块补验证 + CLI `--emit-bil` 验证接线（非法不落盘） | ✅ | 2026-08-03 | 2074/2074 + fuzz 6000（42 套件） |
| M59 | S8c 索引访问 + 实例成员完整化（P3/P4 同步）：BoundIndexExpression（读绑 getAtIndex/写绑 setAtIndex）+ SymbolLookup.FindInstanceOperators（BaseType 链 + ConstructedFrom 回退 + Kind/参数个数过滤）+ PathVisitors 重构（表达式底座绑定/首段后缀折叠/段后缀折叠/`this[i]`/容器末段后缀，解开全部 S8 归口诊断）+ 赋值/复合赋值 place 扩展；多参数索引 `a[i, j]` 定稿为编译错误（SYNTAX §13.2 同步）；P4a LoweredIndexExpression 恒等降级 + P4b §13.6 get.array/set.array 发射（新 SetArrayInstruction）+ BilVerifier 严格三元组查询（无候选/无精确匹配/多命中逐级诊断）；全部扩既有套件（Binder/Lowerer/BilEmitter/BilVerifier 四套件加用例，注册表未动） | ✅ | 2026-08-03 | 2119/2119 + fuzz 6000（42 套件） |
| M60 | S8d 重载解析 + 默认参数 + 具名参数（纯 P3）：SYNTAX §4.2 规则定稿（结构过滤 → 类型适用性 → 最具体胜出 + 默认值填充数平局打破；实例/ext 同池；泛型/可变参数归口；init 同规则）；新设施 `Semantic/Binding/OverloadResolution.cs`（source-level ranking 唯一落点，BIL §3.3——静默结构映射 TryMapArguments + 无目标类型实参预绑（null 占位胜者定型）+ IsApplicable/IsBetter + Materialize 规范序落定）；默认参数三件套（ParameterSymbol.DefaultValue/IsVariadic/IsNamedVariadic + P1 填充 + P2 顺序检查）与声明点绑定（BindingDriver 阶段 1 + BindContext.IsDefaultValueContext 隔离形参与 this（HasThis 统一三处上色判定）+ BindEnvironment.ParameterDefaults 记忆化按需绑定——前向依赖声明顺序无关，in-flight 拦环）；调用/init/索引读三处接 Resolve（MatchSingleCandidate 删除）；同批修复位置实参静默覆盖具名占位；BinderTests 新两组 30 用例 + 语义 fuzz 新套件 | ✅ | 2026-08-04 | 2149/2149 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M61 | 规范定稿兑现（用户决策批次）：默认构造（§9.3）+ 循环/catch/finally(e) 变量一律 const（§7.3/§8）勾销技术债 #11/#15①/#17③；复合赋值单次求值（§13.2 通用规则——CompoundAssignmentRewriter 重写：副作用目标物化 .sN、纯读取直通零物化，勾销 #20①）+ 语句 seq 作 return@ 目标（§6.1 明确化——P3 BoundSeqExitStatement + SeqLabels 栈（named 专属、隔循环/隔值块拦截）+ P4a LoweredSeqExitStatement 标记 + 全部 seq 降级压栈 + TransformStatements 三处扩展（命中本层消费/外层传播），勾销 #17②） | ✅ | 2026-08-04 | 2162/2162 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M62 | **巨石拆解批次**（用户决策纯重构，零行为变化零用例增删）：`Semantic/DeclarationResolver.cs`（1327 行）visitor 化迁移——瘦入口 + `Semantic/Resolution/` 12 文件（阶段级 CRTP visitor，协议同 VISITOR_REWRITE §3；ResolveEnvironment 只读环境 + EntryCollector 静态设施 + 九簇阶段 visitor，13 步顺序与 71 处诊断逐字保持）+ `Lexer/LexerLayers.cs`（763 行）每类一文件 11 个（含 StringEscape 共享转义表拆出）+ 测试三套件 partial 分文件（BinderTests 2832 行→主文件+8 partial、BilEmitterTests→主文件+5、LowererTests→主文件+5） | ✅ | 2026-08-04 | 2162/2162 + fuzz 6000 + 语义 fuzz 3000（43 套件，用例零增删） |
| M63 | **S8e 访问控制 + getter/setter + override 检查**（方案 A 一次落地三项）：SYNTAX §16.1/§9.4.1/§9.2.1 定稿 + §11 注记；符号六槽（MethodSymbol.IsOpen/IsAbstract/IsOverride/HasBody + FieldSymbol.Getter/Setter/HasBackingStorage）+ SourceFile 文件身份（构造类型随定义传播）+ bootstrap 统一 Public；P1 访问器壳（不进容器 Methods 表）；P2 AccessChecker 共享设施 + 声明侧接入（类型引用/继承/约束）+ AccessorChecker/OverrideChecker 两新阶段（构造宿主签名 Substitute 代入——stdlib 双接口依赖）+ ModifierChecker 三标记位置；P3 使用点检查（实例/静态/裸名调用、字段、init、索引 operator、函数体内类型引用——候选过滤先于 ranking）+ 访问器读写检查与体绑定（value 别名 BindPath 首段拦截、backing setter 隐含赋值、自动体合成、带访问器字段不收窄、Bound 节点形态不变——BIL get.field/set.field 承载）+ 局部归口 S11；P4 声明段开闸（BilAccessorModifier + getter(FIELD)/setter(FIELD) 字段槽驱动 + backing/computed/readable/writable/compiler-generated 字段修饰 + override/abstract 投影）+ BilVerifier §21.8 增补与命名空间宿主前缀修复；同批修复 SymbolLookup override 遮蔽去重（测试暴露真 bug）；新 Tests/BinderTests.Access.cs 三组 + 三套件增补共 110 用例 | ✅ | 2026-08-04 | 2286/2286 + fuzz 6000 + 语义 fuzz 3000（43 套件） |

| M64 | **hint 提示指令 + BIL 全文重编号**（用户决策的规范定稿 + 落地）：BIL_STANDARD 新 §18 提示指令（`hint res(RESOURCE_ID)`——仅 block 内、string 资源 JSON 负载 schema 留白、纯位置标记不参与 DA/控制流、删除全部 hint 可观察行为不变（§22.2）、VM no-op、Middleware 可用可忽略且内容不得影响语义）+ §18–§26 重编号为 §19–§27（全仓库 § 引用与 BilVerifier 错误码 "20.x"→"21.x" 同步）+ §26/§27 扩展清单收口 + `Bil/BilHintInstruction.cs` 模型与 BilVerifier §21.2/§21.3 检查 + 两套件五用例；VISITOR_REWRITE/SMART_CAST_DESIGN 两文档移除 | ✅ | 2026-08-05 | 2291/2291 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M65 | **函数级 Context 组件化拆分**（用户决策纯重构，零行为变化零用例增删）：三树函数级状态平板巨石（BindContext 14 成员/LowerContext 11/EmitContext 8）narrow 化为「组合根 + 职责组件类」——Bind 侧新 `BindFunctionFrame`（只读函数帧：Method/FileCtx/DeclaringType/IsDefaultValueContext + HasThis/CanAccess）+ `AccessorBodyState`（访问器体 value 别名状态）+ `BindLabelState`（四标签栈封装：值块/循环/switch 占位/seq 标签，return@ 隔层拦截与 break/continue/占位命中查找收编为领域方法，裸 Stack/元组不外泄）；Lower 侧新 `SynthLocalFactory`（.sN/.bN 独立计数统一登记 + ReferenceTo）+ `LowerOutputState`（前置语句机制封装，Push 双形态）+ `LowerTargetState`（五映射栈封装 + 命中查找收编）；Emit 侧新 `TempVarTable`（.tN 工厂自 EmittingFacility 收编）+ `BlockIdAllocator`（五 block id 计数器）；设施层签名窄化为组件类型（MemberLookup/TypeReferences/ConstFieldRules/ConditionFacts/NarrowKey 等——真编译期边界，跨组件消费者如 OverloadResolution/BindPath 保持组合根）；`IFlowContext` 删除（组件即方言，兑现 M55「按真实隔离需求拉组件、禁止切多个独立状态对象」预留） | ✅ | 2026-08-05 | 2291/2291 + fuzz 6000 + 语义 fuzz 3000（43 套件，用例零增删） |
| M66 | **S8f castTo/castFrom 名字分析 + async 边界五项闸门**（纯 P3 步，S8 收官）：P1 `MethodSymbol.IsAsync`；P2 新 `ConversionOperatorChecker`（castTo 零参数/castFrom 恰一参数/必声明返回类型——形状违反即死声明）+ 新 `AsyncGateChecker`（声明侧闸门 2 参数/3 返回值/5 泛型约束边界共享安全 + async 仅函数收口：init/operator/类型声明拒绝；置于 GenericConstraintChecker 后——闸门 5 依赖约束已解析）；P3 `BoundCastExpression.Conversion` 新槽 + `SymbolLookup.FindConversionOperator`（单泛型参数代入签名匹配，宿主泛型按不适用回退）+ CastVisitor 转换优先级（源 castTo → 目标 castFrom → 内建，as? 同分析）；新 `Binding/AsyncGates.cs` 调用点闸门 1/2（BoundTree 后置遍历单落点覆盖全部调用形态 + 访问器体）+ 新 `LambdaVisitor` 闸门 4（async lambda 捕获 AST 级扫描：形参/体内声明名排除、外层局部/参数解析命中即查；lambda 归 S13 归口消息落定）；测试 +51（DR 两组 25 + Binder 新 partial 两组 26） | ✅ | 2026-08-05 | 2342/2342 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M67 | **S9 细化 + 规范定稿**（纯文档里程碑）：SEMANTIC_ROADMAP S9 细化为 S9a–S9f（S9a 函数体内泛型参数放行/S9b 泛型调用绑定/S9c 泛型 new/S9d 泛型可变参数/S9e hidden args 物化/S9f stdlib 泛型化 + 技术债勾销）+ 规范定稿：SYNTAX §4.2 泛型方法调用解析（必须显式实参零推导、带显式实参候选池仅泛型方法、泛型 operator 名字调用同规则且运算符位置不参与、泛型 init 不存在）+ §3.6 使用侧约束满足判定（extends = IsAssignable/supers = 反向/with = 查 AppliedWrappers 含 interface 传染、边界含未替换泛型参数跳过、ErrorType 静默）+ §4.3 泛型可变参数调用规则（类型实参由对应值实参静态类型推导，包固有形态）+ RUNTIME §10 编译器传参形态注记（.generic.T/.generic.TArgs、getid.type 物化、嵌套转发、保留名约定） | ✅ 文档 | 2026-08-05 | 2342/2342 + fuzz 6000 + 语义 fuzz 3000（43 套件，纯文档无增删） |
| M68 | **S9a 函数体内泛型参数放行**（纯 P3 步，S9 首段施工）：三树值层类型契约放宽 `TypeSymbol → SemanticSymbol`（BoundExpression.Type/LocalSymbol.Type/LoweredExpression.Type + NewTemp/NewSynthLocal/SafeReceiverEntry/EnsureDeclaredType 等设施——泛型参数按引用相等身份使用，PrintType 已具 §7.5 `.generic<$.generic.T>` 投影，BilVerifier `.generic<` 降级保持）；16 处「使用侧泛型归口 S9」gate 逐一解开（形参/局部 T 声明/字段链读取/索引运算符签名/调用返回类型/is·supers·with 静态目标/typeOf 类型形态/解构分量/范围与 for-each/参数默认值）；SubstituteFieldType 修泛型参数实参替换（构造实参为外层泛型参数时返回实参本身——引用相等身份）；TypeReferences.Resolve 放宽（`var x: T`/`x as T`/`as? T` 放行，as? 泛型参数目标保守取 T 自身）+ 泛型参数 new 归 S9c 诊断；语句位置 `foo\<i32>(1)` 静默丢实参漏洞修复（CallForm.TryGet 补 GenericArguments 检查）；must-return 级联跳过撤销；测试：BinderTests 新 TestGenericFunctionBody 组 7 用例（字段替换身份/typeOf(T)/语句位置归口/泛型 new 归口）+ 泛型函数 S9 归口用例改正向 + castTo 泛型 operator 空体补 return（as TTarget 放行） | ✅ | 2026-08-05 | 2350/2350 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M69 | **S9b 泛型调用绑定**（纯 P3 步，SYNTAX §4.2 定稿落地——显式实参唯一零推导）：OverloadResolution 重构为候选视图 CandidateView（定义级符号身份 + 双层 SubstituteAll 代入——方法泛型参数按显式实参、宿主泛型参数沿 receiver 构造实参替换）+ Resolve 返回三元组（胜者/实参/代入后返回类型）；候选池规则（带显式实参仅泛型方法个数匹配过滤、不带时诊断「需要显式泛型实参」、非泛型带实参诊断）；CallForm.TryGet 提取显式泛型实参（首段/末段被调名位置）+ ResolveGenericArguments + CallBinding.TypeArguments/ResultType + BoundCallExpression/BoundInstanceCallExpression 增 TypeArguments 槽——值位置/语句位置/实例链段/调用底座四形态全接线；新 `GenericConstraints.cs` 使用侧约束检查（extends/supers/with 三判定，边界或实参含未替换泛型参数跳过、ErrorType 静默）接入调用点与函数体内类型引用（嵌套递归）；测试：TestGenericCalls 组 15 用例（绑定形态/实例泛型方法/个数不匹配/需要显式实参/共存命中/Box\<T extends ValueType> 与自定义约束违反） | ✅ | 2026-08-05 | 2365/2365 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M70 | **S9c 泛型 new**（纯 P3 步）：NewVisitor 补 ConstructedFrom 回退（构造类型定义级查 init，修复 `new Box\<i32>(1)` 误报 has no constructor）+ Resolve receiverType 传构造类型（init 宿主泛型参数代入）；泛型定义不可构造诊断保留；测试 +5（定义级 init 命中/构造类型实参与实参绑定/泛型定义不可构造） | ✅ | 2026-08-05 | 2369/2369 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M71 | **S9e hidden args 物化**（P4a/P4b，泛型端到端出合法 BIL）：Lowered 三调用节点增 TypeArguments 透传；fn `.args` 按 §7.2 序插 `.generic.T = .typeid`（.return → .this → .generic.* → 普通参数）；调用点 MaterializeTypeId（静态实参 getid.type type(...) 产 .typeid 临时 / 嵌套转发 $.generic.T 零指令），值/实例/void 语句三发射点接线；`.type` 声明 generic(...) 子句定稿（§8.2：泛型参数名列表，BIL_STANDARD 同步）；BilVerifier 适配（§5.1 放行 .generic./.vargs./.kwargs. 保留名、§21.3 invoke 实参按被调 fn 隐藏条目数跳过）；测试：TestGenericEmission 组 6 用例（fn .args 顺序/静态物化/嵌套转发/.type 子句） | ✅ | 2026-08-05 | 2375/2375 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M72 | **S9d-1 值可变参数 vargs/kwargs**（SYNTAX §4.3 前置）：bootstrap ArrayDefinition（.array<T> 标准构造）+ stdlib core::Pair 补 init；P3 BoundVarArgsArgument（vargs 位置包/kwargs 具名包，Type = Array\<Any\>）+ OverloadResolution 单候选可变放行（多候选含可变归口）+ BindArguments 打包 + 体内引用定型 Array\<元素\>；P4 .args 末位 .vargs.<名> = .array<.any>/.kwargs.<名> = .array<.pair<.string, .any>>（canonical 跳过可变）+ ValueReferenceEmitter 映射（具名先判）+ VarArgsEmitter 特权构造打包（元素装箱 cast .any，具名逐项 Pair）；测试 +5 | ✅ | 2026-08-05 | 2380/2380 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M88 | **wrapper 烘焙架构反转**（用户决策推翻 M81 定稿①③：烘焙/派发链合成/inner 链接/原始体替换/隐藏存储/call??? 类别路由体全部归 Middleware，编译器只携带标记）：**M88a 规范**——RUNTIME §14/§15 重写（frontend 三类标记：wrapped 应用修饰符 / proxy 模板 fn（wrapper-proxy(specific\|wildcard)，体内 inner→invoke fn(..inner)、self→get.self）/ 降级调用点 invoke core::Any$call???；call??? 改为 Any 的 bootstrap 内建 + §22.5 VM hook）；BIL §5.1/§5.3（隐藏字段名退出 BIL 文本，命名约定归 Middleware ABI）/§8.3.1（隐藏字段声明→wrapped(W) 应用标记）/§8.4（PROXY_KIND 两态 specific\|wildcard，删 router/original）/新 §12.5 get.self/新 §15.4 invoke fn(..inner)/§13.3 wrapper 写链 field(F)\|wrapper(W) 两态/§15.5 降级 invoke 恒 core::Any$call???/§21.2·§21.3·§21.8 适配/§22.5 hook 表加 call???；ARCH §5.2 重写（P2 零合成、P3 模板态：self=TTarget/this=wrapper 实例/inner=占位调用，P4 模板 fn 发射）/§6.1/§7.1；SYNTAX 仅「编译器生成隐藏字段/router」措辞改 Middleware。**M88b 代码**——删 ProxyDispatchResolver（~500 行）与全部合成符号槽（WrapperChain/WrappedBodySymbol/ProxySpecialization/DowngradeRouter/DowngradeChain/HiddenField/ProxySpecializationInfo/ProxyLinkKind/IsCompilerGenerated）；新 `Resolution/ProxyMatching.cs`（形状匹配 + ProxyMatchChecker，名中形状不符诊断逐字保留）；bootstrap Any.call???（CallWildcard，pub native latte_rt/call???，EnsureCallWildcard 幂等）；BindingDriver 删阶段 2 分流/2.5/2.6，proxy 声明体改模板态绑定；新 BoundSelfExpression/BoundInnerCallExpression；降级 = 资格判定（AppliedWrappers 含 .proxy.*）+ CallBinding{Method=CallWildcard}，IsDowngradeCallResult 改引用相等 CallWildcard；BilProxyKind 两态 + BilWrappedModifier + GetSelf/CallInner/CallInnerNoret + wrapper 写链两态；P4a LoweredGetSelf/LoweredCallInner + HiddenFields→WrapperChain + BuildWrapperFieldPlace 产 wrapper(W)；P4b GetSelfEmitter/CallInnerEmitter + wrapped(W) 投影 + wrapper-proxy 投影；BilVerifier 新规则；DispatchExplainer 数据源改应用登记×ProxyMatching。同批 **#26 代码落地**（ResolveDottedPath 泛型元数/歧义 + AccessChecker ext 顶层规则 + ModifierChecker 禁 ext protected，9 新用例）+ **#27⑧/M79 param:W**（模板态 TReturn/TField 天然可解析；BindWrapperSegment 第三源 with 约束）。测试：BilEmitterTests.Wrappers 整文件按 M88 重写（place 8 + proxy 模板 3 + 降级 5）、删隐藏字段/合成 fn/router 断言；约 3119 + fuzz 6000 + 语义 fuzz 3000，44 套件全绿；CLI 冒烟 specific/wildcard/降级三样例端到端合法 BIL | ✅ | 2026-08-07 | 约 3119 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M87 | **S11f 派发链诊断工具**（RUNTIME §15 兑现——CLI `compile --file <src> --explain-dispatch`：P1–P3 后打印编译单元全部烘焙链与降级路由到 stdout；调用点级过滤为预留扩展）：**报告器** `Semantic/DispatchExplainer.cs`（数据源 = S11a/S11e 符号产物 + CanonicalSymbolPrinter——按类型 canonical 名 Ordinal 分组：applied outer→inner 应用列表 / 被拦截成员链（每层 specific\|wildcard + proxy 声明名 + 特化 fn canonical）/ 链尾 `.wrapped.*` / 降级 router + `.proxy.<序>.???` 链 + 链末 Any.call???；无产物明示 `(no dispatch chains)`）+ **CLI** `--explain-dispatch` 无参标志（与 `--parse-only`/`--emit-bil`/`--sema-only` 互斥，诊断有错退出、无错写 stdout）+ 测试新套件 `DispatchExplainerTests`（第 44）20 用例 + CommandLineParser 互斥 4 | ✅ | 2026-08-06 | 3220/3220 + fuzz 6000 + 语义 fuzz 3000（44 套件） |
| M86 | **S11e `call???` 降级全链**（SYNTAX §14.7 + BIL §15.4 兑现，验收「未声明方法降级端到端样例」达成；规范落地修订——RUNTIME §14.2 泛型逻辑签名实质化为**非泛型胖值签名**：三合成符号（Any.call??? 默认实现/router/降级特化）统一 `(symbol: String, namedArgs: Array\<Pair\<String, Any\>\>, unnamedArgs: Array\<Any\>): Any`，独立泛型 typeid 包取消（Any 胖值自描述 typeid，RUNTIME §2）——结构性必然：双泛型包/双值包/包整体转发无 Bound 层表达（M73 单 GenericPack 槽 + BindArguments 单末位包 + §7.2 序 + 包转发归 S11g））：**P2** TypeSymbol.DowngradeRouter/DowngradeChain 两槽 + ProxySpecializationInfo.TargetMember 可空（降级链无目标成员）+ ProxyDispatchResolver 第三步 ComputeDowngradeChains（router = 宿主成员 `call???`（`?` 非标识符字符零冲突）+ 逐应用 `.proxy.<序>.???` 特化（Kind=Wildcard/TargetMember=null/OriginalBody=Any.call???）+ EnsureCallWildcard 幂等合成 Any.call???（Public））+ CanonicalSymbolPrinter.PrintDowngradeRequest（请求 symbol 定稿：定义级宿主前缀 + 位置实参只写静态类型/具名写 名:类型 + 返回段恒 .any——转换在物化点插入）；**P3** BindInstanceMethodCall candidates 空分支降级判定（同名字段 is not a method 优先；沿 BaseType 链定义级回退找 router；实参无目标类型预绑 + symbol 字面量 + 双包 BoundVarArgsArgument + ResultType=Any）+ inner 自动补 symbol（innerTarget 首形参 symbol 且用户未提供时合成具名实参 ArgumentASTNode——per-member shim 双参链天然不触发）+ BindingDriver 阶段 2.6（router 体直通 invoke 链首/降级特化零前奏绑定（形参同名直通，inner=下一环或 Any.call???）/Any.call??? 体合成 throw new NoSuchMethodException(symbol)，无 stdlib 跳过体合成）+ 类型兼容检查豁免五位置（BoundAnalysis.IsDowngradeCallResult 单一定义点：声明初始化/赋值/return/实参 IsApplicable·Materialize·BindArguments——返回值转换骑 P4a §6.5 cast 物化（Any→T 引用不等即物化），失败抛 CastException，SYNTAX §14.7 字面兑现；if?/throw/复合赋值位置不豁免——无 cast 物化点会产非法 BIL）；**P4** 双闸门删除（LocalSymbolEmitters 声明 + EmittingDriver 定义平铺）+ EmitMethodDeclaration router 投影（DowngradeRouter 引用相等 → wrapper-proxy(router)）；**BilVerifier** §21.8 放行 call???↔router（kind↔名段一致增补）+ §21.2 builtin 宿主 fn 定义豁免（Any IsBuiltin 不进符号段是结构性事实）+ 预定义符号表补 core::Any$call???；测试 +69（3127 → 3196：DeclarationResolver TestDowngradeChains 18（单环/双环/无链/PrintDowngradeRequest 黄金）+ Binder TestDowngradeBinding 11 用例（降级形态/具名 symbol 串/负例无链/字段同名/三体形态/语句位置/值位置 Any/return 与实参豁免/普通 Any 不误伤）+ BilEmitter TestDowngradeEmission 5 组端到端（单环黄金/cast 物化/语句位置/双环/负例）+ BilVerifier router 3 例 + CLI 冒烟落盘验证器零错误）；遗留归 S11g 复核：显式泛型实参降级 symbol 不含泛型信息、getter/setter/operator 未声明请求降级（router 类别路由预留）、interface 传染宿主降级链（应用记录槽不回写牵连） | ✅ | 2026-08-06 | 3196/3196 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M85 | **S11d P4b 合成 fn 发射，烘焙端到端**（ROADMAP S11d 落地，验收「specific 与 wildcard 声明侧烘焙端到端出合法 BIL（invoke 原名 → 特化链 → 原始体）」达成，规范零修订）：**Bil 模型**新 `BilProxyKind` 四态枚举（specific/wildcard/router/original）与 `BilWrapperProxyModifier`（§8.4，拼写入 BilSpellings 唯一定义点）；**P4b 开闸**——LocalSymbolEmitters「.」前缀闸门改分流（烘焙产物照常发射：特化 = `ProxySpecialization` 槽非空、原始体 = `.wrapped.` 名段、解包 shim = `.proxy.unwrap.` 名段；wrapper 类型内的 proxy 声明模板是编译期模板、自身无 fn 定义，不进 BIL）+ EmitMethodDeclaration 投影 `wrapper-proxy(KIND)`（特化按 Kind 取 specific/wildcard、原始体取 original、shim 取 wildcard——wildcard 环的解包辅助，转发壳即被修饰成员原名 fn 是普通成员声明不标本修饰符，§8.4 注记）；**双驱动闸门删除**（LoweringDriver/EmittingDriver——特化 fn/原始体 fn/转发壳/解包 shim 平铺发射，proxy 体经 M84 WrapperPlaceLowering 同路径出 get.wrapper/wrapper 写链）；**BilVerifier 适配**（§21.8，BilVerifier.Symbols.cs）：wrapper-proxy 只允许在 `.proxy.`/`.wrapped.` 保留名方法上（§5.1）、保留名方法必须带本修饰符、kind 与名段一致（`.wrapped.` ↔ original、`.proxy.` 不得 original）+ 修饰符重复检查；**同批修复 M82/M83 潜伏类型不符**（发射暴露，§21.3 拦下）：wildcard 具名包按 §14.7 胖值 ABI 定型 `Array\<Pair\<String, Any\>\>`（前奏物化局部（BindingDriver.PackMemberArguments）与 shim namedArgs 形参（ProxyDispatchResolver.SynthesizeUnwrapShim）两处同规则同改，core::Pair 缺席降级 Array\<Any\>——PathVisitors.VariadicParameterViewType 先例；此前前奏局部 Array\<Any\> 与 P4b 具名打包产物 `.array<core::Pair<.string, .any>>` 型不一致）；测试 +33（3094 → 3127：BilEmitterTests.Wrappers 三组端到端（specific 单环四 fn 黄金 + 声明形态与模板不进段断言/wildcard 单环（前奏物化 + shim cast 解包）/get 访问器链（字段槽壳 + 成员表两合成 fn））+ BilVerifierTests 修饰符规则 6 例（正例三 kind + 非保留名/缺修饰符/kind↔名段不符两态/重复）+ BinderTests P4 闸门用例转正为开闸贯通断言 4；CLI 冒烟三样例 `--emit-bil` 落盘验证器零错误） | ✅ | 2026-08-06 | 3127/3127 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M84 | **S11c P4a/P4b wrapper place 成员访问**（ROADMAP S11c 落地，解 M79 P4 归口；BIL §12.4/§13.3/§8.3.1）：P3 的 `BoundWrapperAccessExpression` 携带命中应用记录；P4a 的 `WrapperPlaceLowering` 按应用类别处理，Entity 读取为 `get.wrapper` 值拷贝、字段-Value 读取为 `get.wrapper.field` 值拷贝，随后均使用普通 `get.field`/`invoke`/`get.array`；直接字段写入走 `set.wrapper.field`，深层字段写穿正向读取、叶写后仅对值类型中间层反向 `set.field`，最外层需要时以 `set.wrapper.field` 写回。局部/静态存储和索引写仍归口。P4b 发射上述读取与写入形态，`LoweredWrapperFieldExpression` 仅作写 place；测试覆盖 Entity、字段-Value、嵌套值拷贝、深写与求值序。 | ✅ | 2026-08-06 | 3094/3094 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M83 | **S11b P3 proxy 体逐组合绑定**（ROADMAP S11b 落地，M82 符号产物消费，纯 P3 步）：BindingDriver 阶段 2 分流（`.proxy.` 声明体收集免常规绑定；被拦截成员含访问器的用户体改挂 `WrappedBodySymbol`）+ 阶段 2.5 三件套——**转发壳**（原名 fn body = invoke 链首，直接构造 bound 节点）+ **特化体**（组合语境挂新组件 `Binding/ProxyBodyState.cs`；前奏物化——wildcard `symbol` = canonical 常量（§14.8 PrintMethod）/双包 = `BoundVarArgsArgument` 复用打包、get 的 `value` = invoke 下一环；proxy 声明体经块分派绑定，同名形参直通特化符号）+ **wildcard 解包 shim**（P2 同批合成符号 `.proxy.unwrap.<序>.<键>` 双包参；body = 逐元素 cast 解包 invoke 下一环，§14.7 同款 CastException 语义）；**self/inner/this 上色**（self = 宿主角色 this（TTarget 代入结果，零泛型报错）；inner = 下一环普通调用（OverloadResolution 单候选复用 + 泛型逐位转发）；proxy 体 this 重写 `BoundWrapperAccessExpression`（只读 place，裸 this 取值/赋值禁令同 M79 族）；非 proxy 语境 self/inner 专门诊断，ARCH §5.2 兑现）+ **诊断去重**（(proxy, span, message) 集，BindEnvironment.CurrentProxy）；**P4 闸门**（LoweringDriver/EmittingDriver 跳过合成 fn 与转发壳——`--sema-only` 零诊断、`--emit-bil` 由 §21.2 正确拦截不落盘，归 S11d）；测试 +31（DR shim 断言 4 + Binder 新组 TestProxyBodyBinding 27：三件套形态/self/inner/this/wildcard 前奏与 shim/双环链/get 访问器链/负例六项/诊断去重/P4 闸门） | ✅ | 2026-08-06 | 3057/3057 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M82 | **S11a P2 proxy 形状校验与符号合成**（ROADMAP S11a 落地，纯 P2 步）：**AppliedWrappers 升级 `WrapperApplication` 记录**（Wrapper——Entity wrapper 恰一泛型参数时为 TTarget 代入宿主的构造类型（M79 遗留「TTarget 显形」兑现）+ Syntax 注解节点（init 实参挂载点，绑定归 S11b）+ HiddenField 槽；NameResolver 新 `allowBareGenericDefinition` 开关——wrapper 注解裸名命中泛型定义放行，元数校验归形状检查器）；**`Resolution/ProxyCheckers.cs`（ProxyShapeChecker）新 P2 阶段**：泛型元数（Entity 至多一/Value·Method 零）+ 类别矩阵（Entity：specific 四类 + wildcard 四类；Value 仅 `.proxy.get`/`.proxy.set`；Method 仅 `.proxy.call`）+ wildcard canonical shape 逐参数校验（参数名属 ABI、泛型参数名自由）+ specific get/set 形状 + `.proxy.call` 双形态（§14.4）；落地修订两项——Entity 泛型元数由「恰一」放宽「至多一」、§14.3「必须实现 get」不强制执行（纯状态 wrapper 是 §14.5 合法用法，SYNTAX §14.2 同步修订）；**`Resolution/ProxyDispatchResolver.cs` 新 P2 阶段**：`.wrapper.` 隐藏字段合成（§5.3 命名 + priv + `FieldSymbol.IsCompilerGenerated` 新标记 + interface 自身不合成、传染到实现者且 TTarget 按实现者重建构造 + Value 实例字段挂宿主 + 同名冲突诊断）+ Entity 派发链计算（§14.6：specific 名中且形状全等（TTarget 代入后逐项比较）优先、名中形状不符即诊断、名未命中落类别 wildcard、泛型成员只参与 wildcard、双未命中 inert 静默）+ 逐组合特化 fn（`.proxy.<序>.<成员键>`）与原始体 fn（`.wrapped.<成员键>`）合成（签名全拷贝：泛型参数/参数符号新实例、类型逐层代入）+ `ProxySpecializationInfo` 元数据槽（proxy 声明/应用/类别/目标成员/原始体）；**发射闸门**（合成符号零泄漏，CLI 冒烟实测）：「.」前缀名的声明（LocalSymbolEmitters）与 fn 定义（EmittingDriver）跳过归 S11d，IsCompilerGenerated 字段声明跳过归 S11c；测试 +48（形状负例 20 + 隐藏字段/链符号断言与 canonical 黄金 28） | ✅ | 2026-08-06 | 3026/3026 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M81 | **S11 proxy 烘焙细化 + ext 三事裁决**（纯文档里程碑，仿 M67；烘焙形态与 #26 共五项决策用户定稿）：SEMANTIC_ROADMAP S11 剩余细化为 **S11a–S11g** 七子步（每步带验收）——S11a P2 形状校验与符号合成（proxy canonical shape 校验/wrapper 应用实参登记/`.wrapper.` 隐藏字段与逐组合特化符号合成）→ S11b P3 proxy 体逐组合绑定（self=宿主角色 this/inner=下一环普通调用/proxy 体 this→wrapper place + 转发壳与解包 shim 合成 + 诊断去重）→ S11c P4a/P4b wrapper place 成员访问（解 M79 归口：读=get.wrapper 值拷贝+get.field/写=set.wrapper.field/调用 receiver=值拷贝）→ S11d P4b 合成 fn 发射（specific/wildcard 烘焙端到端出合法 BIL）→ S11e `call???` 降级全链（P3 降级判定 + 胖值 ABI + router 合成 + bootstrap Any.call???）→ S11f 派发链诊断工具（CLI `--explain-dispatch`）→ S11g 复核收尾（M79 遗留两项 + #26 代码落地）；**烘焙形态定稿**（用户决策）：① 声明侧烘焙骑 vtable（RUNTIME §14 字面语义，调用点零改动）② 特化体为带 `wrapper-proxy(PROXY_KIND)` 修饰符的独立合成 fn，编译器不做文本内联、最终内联归 Middleware ③ 特化按 (proxy × 目标成员) 组合在 P2 合成符号（Freeze 前）、P3 逐组合绑定、P4 零 wrapper 语义；**技术债 #26 三事裁决勾销**：ext static 合法（§4.4 明文补例）/ priv·protected ext 可见性按声明位置（§4.4/§16.1，ext 体不放开目标私有成员）/ ext 泛型目标禁止静默接受（元数 + 歧义诊断，隐式泛型参数留候补）；规范修订：SYNTAX §14.2（Entity wrapper 恰一 TTarget/specific proxy 形状全等/宿主创建时安装）+ BIL §8.4（PROXY_KIND 四值 specific/wildcard/router/original）+ §5.1（合成保留名 .proxy./.wrapped./.wrapper.）+ RUNTIME §14（特化 fn 独立发射）+ §15（--explain-dispatch 形态）+ ARCH §6.1（脱糖清单 wrapper 两行 + pass 归属注记）+ §5.2（proxy 体绑定规则条） | ✅ 文档 | 2026-08-06 | 2978/2978 + fuzz 6000 + 语义 fuzz 3000（43 套件，纯文档无增删） |
| M80 | **S11 ext 收尾**（按序推进，无用户决策项）：P4b 修复——`EmitBuiltinExtMembers` 随迁访问器声明（内建 ext 字段 + 访问器此前声明缺失被 §21.2 拒绝落盘，SYNTAX §4.4 示例形态实测复现后修复）；P2 两闸门收口（ExtensionRegistrar，违规不注册与判重同口径）——① ext 字段禁注 interface（§11 成员禁令的 ext 路径绕行收口）② ext 实例字段同受 §3.1.1 闭包表约束（新 `FieldClosureChecker.CheckExtensionField` 入口完整复用 CheckClosureField）；端到端样例勾销技术债 #22④（`Tests/BilEmitterTests.Ext.cs` 新 partial 五组——实例字段读写/方法调用/backing 访问器/内建 computed 访问器/static 三形态/复合赋值）+ DeclarationResolver 两闸门 +9；priv/protected ext 可见性、ext static 明文、ext 泛型目标三事登记技术债 #26 待裁决；SYNTAX §4.4 补成员语义注记 | ✅ | 2026-08-06 | 2978/2978 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M79 | **S11 wrapper place 绑定与只读禁令**（纯 P3 步，SYNTAX §14.1/§14.5 兑现，规范零修订；ROADMAP S11 后续施工第一项）：新 `BoundWrapperAccessExpression`（Receiver + Wrapper 符号，Type = Wrapper 定义——只读 place 只作成员访问接收者（字段读写/方法调用/索引），永不作为路径绑定结果产出，下游零新消费点）；PathVisitors 两处 Colon 归口解开——新 `BindWrapperSegment`（双源同池查找：宿主来源符号 AppliedWrappers（字段/局部——Value wrapper）+ 宿主静态类型 AppliedWrappers（构造回退定义——Entity wrapper），按段名匹配，零命中/同名歧义均诊断；nullable 宿主拒绝与普通段同口径；**只读禁令全拦截面**：链末无后缀 Colon 段即整体赋值/取值，按 forAssignment 分措辞——取值逃逸（初始化/实参/返回/推断源/运算与类型检查操作数/插值段……）全经路径绑定结果一处收口；带后缀（索引成员）与非链末（字段/方法段继续消费）即合法接收者）+ 容器路径泛化（Colon 切分——`Type.staticField:W` 静态字段宿主、`Type:W` 无值宿主诊断）；局部变量 wrapper 应用 P3 登记（WrapperCheckers「栈上声明归 P3」注记兑现：`LocalSymbol.AppliedWrappers` 槽 + LocalDeclarationVisitor 注解解析（类别/内建注解/非 wrapper 诊断措辞与 P2 对齐；§14.9 矩阵 C 恒合法免 shared 检查）+ 解构声明注解归口）；下游接线——AsyncGates 遍历收编（防腐化 default 抛 CompilerInternalException，新增 Bound 节点必须显式登记）+ LowerDispatchers P4 显式归口（`get/set.wrapper.field` 与 `.wrapper.` 隐藏字段声明归 proxy 烘焙步）+ BoundDescribe 支持；测试：BinderTests.Wrappers 新 partial 四组 +35（正例 17：Entity 读/写/方法调用/链式 `s:Outer:Inner`/this 宿主/局部 Value/静态字段 Value（shared × 静态矩阵）/索引后缀 + 登记引用相等断言；只读禁令 7；负例 8；P4 归口 3）；遗留：泛型参数 receiver 的 with 约束 place（`param:W`）与泛型 wrapper 实参代入（TTarget 显形）归 proxy 烘焙复核 | ✅ | 2026-08-06 | 2946/2946 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M78 | **存疑项裁决批次**（review 存疑清单六项用户决策全部落地，四组并行 + kwargs 闭环收口）：**① BilVerifier TypesCompatible 收紧 canonical 全等**（`BilVerificationContext.NormalizeTypeRef` + 内建别名表（`.i32`↔`core::i32`、`.pair`↔`core::Pair` 等构造头）——其余一律严格全等：构造类型递归逐实参、跨命名空间同名不兼容、`Wrap` ≠ `Wrap\<T\>`；协变（in/out）注释预留归后续里程碑；`HostMatches` 专用辅助把 IsAssignableTo 宿主归属判定切定义级比较防误伤）；**② kwargs 体内视角闭环**（体内视角 `Array\<String\>` → `Array\<Pair\<String, T\>\>`（PathVisitors/CallVisitors 统一 `VariadicParameterViewType` 设施——`.array<T>` 即 `Array\<T\>` 的 BIL 投影 §7.1）+ bootstrap `Array\<T\>` 补 `getAtIndex`/`setAtIndex` operator（S8c 索引绑定内建目标，P4b 直发 §13.6 不走 invoke）+ P4a variadic 索引装箱/拆箱物化（ABI 元素类型设施组（vargs → Any / kwargs → Pair\<String, Any\>，IsNamedVariadic 先判——named 双标记同置）：读形态 Type 覆盖 + 外包拆箱 cast、写形态 EnsureDeclaredType 按 ABI 类型装箱、复合赋值剥壳物化贯通——`nums[0]`/`options[0].key`/`nums[0] += 1` 端到端出合法 BIL））；**③ 简单赋值求值序对齐**（AssignmentEmitter set.field/set.array 的 receiver/index 物化移到 Value 之前——与复合赋值同规则；SYNTAX §13.2 补 UB 句「使用者不应假设该顺序，依赖即未定义行为」）；**④ 前端三件套**（`>` 系列/复合赋值重组统一相邻性校验（pendingOperatorEnd offset 比较——`a > = b` 不再合并）；科学计数法修复（LiteralParserLayer 状态机吸收 `e/E` 后可选符号 + 指数数字（`3.14e-5`/`2e3`/`1.5e3f`），非法形态报完整已拼内容，进制前缀排除 E 歧义——SYNTAX §3.3 增补形态说明）；一元 `+` 删除（IsPrefixUnaryOperator 移除 + P3 UnaryVisitor 分支清理——§13.2 表本就只有一元 `-`））；**⑤ P1/P2 六项规则补齐**（方法判重键加泛型元数（`foo(i32)` 与 `foo\<T\>(i32)` 合法共存，对齐 M74 类型规则）；interface 字段禁止（ModifierChecker）；重复 implements 定义级去重诊断（同名与同定义不同构造同拦）；static operator 禁止（任何使用点不可达的死声明）；具名 import 同名修正（失效条目跳过继续找 + 双有效报 Ambiguous import + 同路径重复豁免）；显式泛型实参拦截全可变包候选（§4.3 包实参不显式书写，混合形态不误伤））；**遗留登记**：混合泛型形态显式实参个数（`f\<T, TArgs...\>` 按全列表匹配 vs spec 固定参数个数）归技术债；测试 +94（BinderTests.KwView 9 + BilVerifierTests TypeCompat 8 + BilEmitterTests/LowererTests variadic 索引 20 + DeclarationCollector/Resolver 38 + BinderTests.Overloads 3 + 前端 17 + 既有黄金更新），2817 → 2911 | ✅ | 2026-08-05 | 2911/2911 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M77 | **S11 enum case 全链 + init 映射赋值合成**（三阶段串行施工 + 一项既有缺口裁决落地，SYNTAX §12/§9.3 + RUNTIME §16 + BIL §8.5/§12.3/§14.3 全兑现——**enum case 端到端出合法 BIL**）：**阶段 1（P1+P2 结构级）**：`EnumCaseSymbol` 符号家族（Owner/Discriminant（long?，null=auto）+ ResolvedInit/HoleParameters 两模板槽（P3 声明点落定，首例 P3 写符号——声明侧元数据且 P4b §8.5 必须消费）+ `EnumCaseHoleParameter{Name, Type, InitParameterIndex}`）+ `TypeSymbol.Cases` 表 + `CanonicalSymbolPrinter.PrintCase`（`com.example::RequestResult.Failed` 形态）；P1 CollectEnumCases 建壳 + 重名防御；P2 新阶段 `Resolution/EnumCaseResolver.cs`（洞独占性（`_` 必须独占实参位置，switch pattern `_` 子树排除）+ case 名复核 + 判别值落定，注册于 TypeReferenceResolver 后）；**阶段 2（P3）**：BindingDriver 新阶段 1.5 声明点模板绑定（结构过滤 → 固定实参绑定与适用性决胜（单候选带目标类型/多候选静默过滤，OverloadResolution 先例）→ 洞 pub 规则（§12.2）→ 落定符号两槽 + 固定实参缓存 BindEnvironment（仿 ParameterDefaults 先例；无显式 init 零实参 case 走默认零参构造——HoleParameters 空列表为成功标记）；泛型 enum 归口）；Bound 两节点（`BoundEnumCaseExpression{Case, Arguments（规范序洞实参）, Type=Owner}` + `BoundTypeCheckExpression` 增 IsCase Kind 与 Case 第三槽——ConditionFacts 只认 Is+TargetType 天然不触发 smart cast §12.3）；使用侧三形态（裸 `.Success` expectedType 上下文推断（无上下文/非 enum 落 §12 诊断）/`.Failed(404)` 底座+Call 后缀特判（PathVisitor expectedType 通道打通 + 洞实参绑定——位置按序具名归位）/`is .Case` 解归口（操作数定义级 enum + case 名解析），switch `(_ is .Case)` pattern 通道自动可用 + 值匹配保持常量限定 + `new EnumType(...)` 永久规则措辞 §12.2）；**阶段 3（P4）**：Lowered 两节点（恒等 + 洞实参按洞签名 EnsureDeclaredType cast 物化——§14.3 严格匹配落点 P4a）+ 声明段遍历 Cases 发 `BilCaseDeclaration`（洞签名投影 + 显式判别值登记 i32 标量资源发 res(R)/auto 发 auto——与 M75 S11Module 逐点一致）+ `NewCaseInstruction`/`IsCaseInstruction` 值发射（switch pattern 降级路径自动贯通）+ LoweredDescribe/BoundDescribe 支持；**init 映射赋值合成（§9.3 落地缺口，既有 bug 裁决）**：ParameterSymbol 加 `MappedField` 槽（P2 TypeReferenceResolver 两分支（省类型/显式类型）回写——显式类型分支此前完全不做字段检查）+ BindingDriver 合成（无体 init 产 BoundFunctionBody（映射赋值序列/空块——普通 class 与 enum case 模板同愈，§21.2 落盘修复）+ 有体 init 映射赋值前插（stdlib core::Pair 空体 init 的 key/value 构造写入闭环——潜伏语义 bug）+ 直接构造 bound 节点（自动访问器先例，Syntax 回指声明节点，const 字段经 P3/验证器双 init 豁免）+ LocalSymbolEmitters 删除 enum 无体 init 跳过分支（声明 + fn 定义统一发射，`_ -> field` 映射保留 BIL 供 S14 VM case 入口消费）；测试 +118（DeclarationCollector 建壳反转 6 + DeclarationResolver EnumCaseStructure 15 + 映射槽 3 + Binder EnumCases 48 + InitMappingSynthesis 9 + Lowerer EnumCases 15 + BilEmitter EnumCases 14 + InitMappingEmission 8，CLI 冒烟五样例（固定/参数化/显式判别值/嵌套 enum/映射合成）），2699 → 2817 | ✅ | 2026-08-05 | 2817/2817 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M76 | **全仓库 review + 39 项 bug 修复批次**（用户决策：不推进语言特性，先回补质量——六模块并行 review（Semantic/Binding、Resolution+Symbols、Lowering、Bil、前端、Core/Tests）出 30+ 确认发现，两波九组并行修复 + 主代理收口）：**Bil 验证器 5 误报修复**（合法程序拒绝落盘——namespaced 全局函数调用误吞 receiver（owner `::` 结尾排除，对齐 fn 定义侧）；invoke 实参比对按 §7.2 调用序重写（值包在普通参数之后，普通+包混合形态）；loop.rev DA 按 §16.4 body 至少一次（judge/循环出口取 body 出口态）；try-finally 无 catch 空 catch 表判终止；TypeDeclarations 反查索引键加元数（S10 同名不同元数共存，`Wrap`/`Wrap\<T\>` 各带 init 互不遮蔽）+ AllInstructions BlockSet 过滤/IsLocalIdentifier 前缀余部校验/VerifyWrapperFieldChain 降级一致）；**P1/P2/Symbols 7 修复**（架构级——构造类型 `BaseType` 「创建即代入 + InheritanceResolver 后统一回填」（SymbolGraph.Substitute 单源上移 + BackfillConstructedBaseTypes，先入表再代入防自引用重入；CreatesCycle 改定义级比较）修复泛型基类两跳断链全部症状（构造类型 receiver 成员查找误拒/泛型循环继承漏报/跨构造基类抽象未实现漏报/字段两跳代入泄漏）；TypeReferenceResolver 与 InheritanceResolver 阶段换序（init 映射 FindField 可达继承的内建字段——`class E : Exception { init(_ -> message) }` 落地）；ext 注册重复/遮蔽检测（字段同名/方法同签名 SameSignature）；OverrideChecker 覆写泛型元数比较；命名空间非类型落袋拦截（NameResolver 单点 + 具名 import）与裸名命中泛型定义报元数（ApplyTypeArguments 同口径）；ext native 视同成员 static 闸门 + 重载按目标类型成员表）；**Lowering 7 修复**（?. Access 降级前置语句收进 thenBlock——miscompile，§3.4「receiver 为空则整体不求值」；Nullable\<泛型参数\> null 资源按 §7.5 canonical `.generic<$.generic.T>` 投影（泛型函数内 ?./if?/T? 初始化全灭修复）；IsSideEffectFree 收紧（索引恒非纯 + 字段按 Field.Getter——§13.2 单次求值，a[i].c += 1/getter 链不再双重求值）；variadic 参数引用 Type 透传 P3 定型 Array\<元素\>（多余 cast 物化消除）；variadic 写映射与读侧统一（EmittingFacility.ValueVariableName 共享）；具名包结果类型对齐 .kwargs 契约 `.array<core::Pair\<.string, .any\>\>`；ext 字段 ext 修饰符 + LoweredLoop.Enumerator 死代码删除）；**前端 4 修复**（多行字符串引号串紧跟转义的内容顺序错乱（反斜杠分支补 FlushQuoteRun——唯一静默数据腐蚀 bug）；多行插值首段 span 修正起死回生（移 PushToken 后，对齐单行串）；访问器尾随游离修饰符报错（CloseBlock 查 current 未提交）；JSONL 反序列化单节点成员落位类型校验（裸 ArgumentException → CompilerInternalException 契约）+ InterpolationStartToken span 注释修正/ext·init 映射三处 IsIdentifier 统一/ImportParserLayer 多导入孤儿节点消除）；**Core/Tests 3 修复**（LoggerTests 保存/还原 CLI 日志状态（`test --all --log-to` 不再被截断 18 套件日志）；--sema-only ↔ --emit-bil 互斥（静默假成功修复）；--dump-ast/--emit-bil 输出路径异常友好报错非零退出 + CheckFnShape 崩溃改 FAIL/StdlibSources 零匹配抛 CompilerInternalException/TypeShort 提取共享）；**P3 流分析 6 修复**（三个不 sound 收窄复活——无 else 非 guard 合并改 MergeNarrowed 纯交集、循环出口恢复后 ClearRoot 体赋值根、TryVisitor 收窄快照/分支恢复/出口合并（finally 不参与交集，finally 体赋值根统一 ClearRoot）；null 字面量非 Nullable 上下文落诊断（§3.4——`var s: String = null`/`return null`/非空形参 `f(null)` 静默通过修复，泛型参数放行）；值块 GuaranteesValueReturn 补裸 return 终止（§6.1 穿透）；return@ 收集下钻表达式子树（嵌套值块类型不统一漏诊断修复））；**P3 调用/路径 9 修复**（复合赋值 place 剥 SmartCast 壳（收窄区域内 `s += "b"` 误报修复）；写模式索引宿主代入（SymbolLookup.SubstituteForReceiver 设施——`box[0] = 5`（Box\<i32\>）误报修复）；Type.instanceMethod() 补 this 前链检查（含接口闭包）；泛型参数 null 判等放行（t == null 定型 T）；裸名调用宿主代入（receiverType 传入）；可变参数链头 Array\<元素\> 包装；索引复合赋值 set 元素形参校验；显式泛型实参访问控制（AccessChecker 接入）；泛型 backing value 别名放行 + forAssignment 不包收窄/SubstituteFieldType 非空化）；**P3 闸门/重载 4 修复**（async 闸门 2 可变参数包逐元素判定 + 闸门 5 GenericPack 推导类型并入（带包 async 调用误报/泛型包漏查修复）；泛型 backing 自动访问器三处同型修复（getter 合成/setter 隐含赋值/return 全路径检查口径统一）；显式泛型实参约束逐候选判定（SatisfiesConstraints 静默剔除 + 全剔回放首候选诊断）；歧义诊断列 winners 子集/TypeReferences 误诊消息修正/ConditionFacts.Empty 死代码/Binder.cs 头注释过时清单）；**§21.8 init 豁免 + const/var 开闸**（init 方法体内写实例 const 字段放行（对齐 P3 ConstFieldRules 边界——实例写入 + fn 声明 init 修饰符，静态不豁免）；EmitFieldDeclaration 发射 const/var（§8.3 表序：访问 → const/var → ext）；BIL_STANDARD §21.8 文本同步）；**主代理收口**：OverloadResolution 非显式路径统一走 ViewOf 宿主代入（非泛型方法签名引用宿主泛型参数的代入——`box.get()`/`box[0]` 返回 T → i32）；P4b AssignmentEmitter place 剥 LoweredCastExpression 壳 + P4a 复合赋值写回值按 place 声明类型物化 cast（§6.5——收窄区域内复合赋值端到端出合法 BIL）；测试 +202（BinderTests 新 FlowFixes/CallFixes/GateFixes 三 partial + BilVerifierTests §21 逐类用例 + BilEmitterTests.Fixes/LowererTests.Fixes + DeclarationResolverTests 构造基类回填组 + 前端各套件 + BilVerifier §21.8 init 豁免手工模块），2497 → 2699 | ✅ | 2026-08-05 | 2699/2699 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M75 | **S11 BIL 规范定稿 + 模型增补**（S11 首段施工，纯 Bil 层零 P3/P4 行为变化；同批修复 M74 遗留两处——BilEmitterTests.S10.cs:51 误引用 `BilMemberDeclaration.Symbol`（基类无此属性，`BilCaseDeclaration` 用 QualifiedName，验证器经 MemberSymbolOf 分派）与 TestDisposableEmission 的 IDisposable 实现缺 `override`（OverrideChecker §9.2.1 禁止静默隐藏））：BIL_STANDARD 三处定稿——① §12.3 增补 `type.is.case VALUE case(CASE_SYMBOL) RESULT`（enum 判别比较：隐藏判别字段与 case 编译期判别常量整数相等，非子类型检查不访问 TypeSheet 不比较 payload 不改静态类型，RUNTIME §16.3 承载；VALUE 严格等于 case 的 enum 类型、case 必须带完整 enum 前缀、结果 .bool、判别宽度 u16/u32 是布局内部细节不暴露字段符号）；② §12.4 修订 + §13.3 增补嵌套字段访问 嵌套字段 place（当时读/写共用链指令，后读侧统一值拷贝+get.field、写侧 set.wrapper.field）（ARCH §7.1 只读 place 缺口兑现：get.wrapper 保留为 lowering/VM 内部能力——只读 place 成员读取 = 值拷贝 + get.field；写成员与 proxy 体内 this 原地访问用 set.wrapper.field——HOST 必须是 §5.3 wrapper 隐藏字段、INNER 必须是该 wrapper 实例字段、set 要求可写；obj:W = ... 仍是源码层编译错误故无整体写回指令）；③ §8.5/§19.1 判别值注记（整数标量资源、非负唯一、auto 按声明序从 0、宽度按 RUNTIME §16.1 选择）；SEMANTIC_ARCHITECTURE §7.1 缺口标记已兑现；模型：`Bil/BilComputeInstructions.cs` 新 `IsCaseInstruction`（type.is.case，复用 §14.3 BilCaseOperand）+ `Bil/BilDataInstructions.cs` 新 `SetWrapperFieldInstruction`（读侧旧指令已移除）（双 BilFieldOperand，指令自渲染 Writer 零改动）+ BilVerifier §21.3 增补（VerifyCaseCheck：case 可解析/最后 . 切分 enum 类型/宿主必须 enum-struct/VALUE 严格相等；VerifyWrapperFieldChain：宿主符号可解析/字段名段 `.wrapper.` 前缀/宿主字段类型必须 wrapper/宿主对象可赋值到 host owner/内层可解析且实例/内层沿继承链可赋值 + ClassifyVariables 三 case 读写分类）；测试：BilWriterTests 指令黄金 +3 行（is.case/set.wrapper.field 文本形态）、BilVerifierTests 手工模块（S11Module 基线正例：Logged wrapper + Service 宿主隐藏字段 + RequestResult enum 两 case + main(svc,e) 参数免 DA）+ 负例 11（case 不可解析/操作数类型/结果非 bool/声明宿主非 enum/discriminant 资源未登记/宿主字段不可解析/非隐藏字段名/宿主类型非 wrapper/内层不可解析/内层静态/get·set 类型不符/宿主对象不符/const 写入——S11 定稿 §12.3/§13.3 全规则逐条） | ✅ | 2026-08-05 | 2497/2497 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M74 | **S10 core.latte 载入机制 + stdlib 扩充**（用户决策四件套）：① P1/P2 类型名唯一性按「名 + 泛型元数」判定（DeclarationCollector 重复检测 + NameResolver 查找分流——`Task` 与 `Task\<TResult\>` 同名共存，SYNTAX §15.3）；② `stdlib/core/coroutine.latte` 自举声明 coroutine 运行时面（Task/Task\<TResult\>/Executor 家族/PollingAlarm/EventAlarm/CoroutineLocal\<TValue\> 全 shared abstract 空壳 + `sleep` native 全局函数 + PollingAlarm.isReady abstract）+ native 返回类型放宽（SYNTAX §4.6：基本类型或用户引用类型 class/interface，FFI ABI 归 Middleware；P2 NativeDeclarationChecker 同步）；③ `stdlib/core/exceptions.latte` 四异常子类（RuntimeException/IOException/CastException/NoSuchMethodException，`: core.Exception` + 自持 init）+ bootstrap Exception 根程序化携带 protected message 字段 + pub native getMessage()（SYNTAX §8.1，String 初始化顺序修正）；④ `stdlib/core/disposable.latte`（core.IDisposable）+ P3 async 调用返回类型改写（调用点 = Task\<T\>/Task，SYNTAX §4.5 表兑现——CallFacility.AsyncResultType + 值位置 void 检查改 ResultType==null，await 仍归 S13）；P4b 同步：async 方法声明 §8.4 async 修饰符 + 语句位置 async 调用发 invoke 非 invoke.noret（§15.2）+ BilVerifier 预定义符号表补 Exception$getMessage/Exception#message + 类型判重键加元数 + async invoke 结果形态校验（§21.3）；测试：StdlibSourcesTests 三源→六源结构断言 + Binder 新 TestAsyncResultTypes 7 用例 + DeclarationCollector/DeclarationResolver 元数区分用例 + BilEmitterTests.S10 新文件 3 组端到端（异常/IDisposable/async Task）+ native 返回放宽用例更新 | ✅ | 2026-08-05 | 2467/2467 + fuzz 6000 + 语义 fuzz 3000（43 套件） |
| M73 | **S9d-2 泛型可变参数 + S9f stdlib 泛型化 + 技术债勾销**（S9 收官）：bootstrap MapDefinition（.map<K, V> 标准构造）；P3 BoundGenericVarArgsArgument + 三调用节点 GenericPack 槽 + OverloadResolution 候选池放宽（无显式实参时泛型参数全为可变的泛型方法参与——包推导是其固有形态）+ DerivePack 推导（位置包 ← 归包位置实参静态类型序列、具名包 ← 归包具名实参「名 → 类型」映射，归包判定与 BindArguments 同源；逐推导类型约束检查定位到实参；多可变泛型参数/包内 null 归口诊断）+ Resolve 四元组返回；P4 EmittingDriver .args 泛型包形态（.generic.TArgs = .array<.typeid<.any>>/.generic.TValues = .map<.string, .typeid<.any>>，§7.2 序固定泛型 → 泛型包 → 普通 → 值包）+ GenericVarArgsEmitter 调用点打包（位置包 new .array<.typeid<.any>> 逐项 getid.type；具名包 pair 逐项 → new .map）——**泛型可变参数端到端出合法 BIL**；S9f collections 泛型化（#15④）：RangeEnumerator\<T\> 泛型抽象基类（协议级状态机骨架 value_/end_/started_ + current() 实现 + abstract moveNext；start_ 归具体实现——步进策略细节；无 super 构造语法故基类不写 init）+ RangeEnumeratorI32 具体实现继承（i32 运算在具体类上下文），for 范围循环走泛型路径端到端；同批修复三处泛型宿主缺口：InheritanceResolver 基类/接口子句类型引用解析以类型自身为宿主（顶层类 entry.DeclaringType 为 null——`Sub\<T\> : Base\<T\>`/`C\<T\> : I\<T\>` 的泛型实参解析）、OverrideChecker 泛型方法覆写签名同构比较（#22⑥——两侧泛型参数按声明序对应，嵌套构造递归）与接口闭包构造实参代入（接口声明在泛型基类上，沿宿主链 Substitute）、SymbolLookup.IsAssignable 沿基类链接口判定 + SubstituteHost 设施；技术债勾销：#18②（is/supers/with 动态形态值路径带泛型实参走使用侧解析后正常绑定）、#23③（多泛型参数/宿主泛型参数转换运算符按不适用回退内建复核——BIL §12.1 第 3 条兜底，测试固化）、#23④（AsyncGates 闸门 5 调用点泛型实参实际类型共享安全检查）；泛型 operator 名字调用放开（SYNTAX §4.2 定稿「名字形式与普通方法同规则」——FindInstanceMethods 收 Operator，运算符位置/for 头/索引仍走专用解析）；测试：Binder +11（TestGenericVarArgs 8 组 + TestOperatorNameCalls 3 组 + 闸门 5/转换回退各 2 组）+ StdlibSources collections 结构更新（6 声明含 abstract 基类）+ BilEmitter TestGenericVarArgsEmission 端到端 + 资源断言基线更新（stdlib 移除 i32 0 字面量） | ✅ | 2026-08-05 | 2413/2413 + fuzz 6000 + 语义 fuzz 3000（43 套件） |

---

## 2. 当前可解析语法

```latte
// 字面量
42, 0xFF, 100L, 3.14, 0.1f, "Hello ${x}", """多行字符串""", true, null, 'A', '\n'

// 类型引用（含 \< 泛型、嵌套、可空）
i32, String?, List\<T>, Map\<K,V>, List\<Map\<String, i32>>?

// 变量声明（含完整初始化表达式）
var x = 42
const name: String = "Hello"
var v = foo(1, name = 2)
var v = foo().bar[0]
var v = new User(id = 42)
var v = a.b\<i32>(x)
var r = 1 + (2 * 3)          // 无优先级规则已强制：1 + 2 * 3 报错

// 类型操作（is/supers/with 检查，as/as? 转换，typeOf）
obj is String, obj supers Animal, obj with Serializable
obj as String, obj as? String
var t = typeOf(box)
result is .Failed          // is 右侧 enum case（§12.3，M34；as/supers/with 右侧仍只收类型）

// if / switch 表达式（分支体统一为代码块，M33：单表达式分支隐式取值是
// 「块内恰好一条 ExpressionStatement」的语义规则；多语句分支 return@_ / named 取值）
var r = if (x > 0) { x } else { opposite(x) }          // 必须有 else
var r = if (x > 0) named check {
    seq { return@check x }                             // named + return@标签 穿透内层块
} else {
    return@_ opposite(x)                               // 匿名分支体默认标签是 _
}
var r = switch(expr) {
    (1) -> { "one" }                                   // 值匹配
    (_ > 10) -> {                                      // 模式匹配（_ 引用 expr）
        logBig(expr)
        return@_ "big"                                 // 多语句分支体显式取值
    }
    default -> { "other" }                             // 必须有 default
}
var r = switch(expr) named match { (1) -> { return@match 1 } default -> { return@match 0 } }

// switch 语句（M33：语句形态，结果值被丢弃；分支体为完整代码块，必须有 default）
switch(expr) {
    (1) -> { handleOne() }
    (_ > 10) -> {
        logBig(expr)
        handleBig()
    }
    default -> { handleOther() }
}

// Lambda（含泛型、async、trailing；体为单表达式或多语句块，M33）
var f = func{(x: i32): i32 -> (x + 1)}
var f = func{(width: TSize)\<TSize extends Size>: TSize -> width}
var loader = async func{(id: i32): SharedUser -> loadUserNow(id)}
list.map{(item: String): i32 -> item.length}           // 脱糖为调用实参
var f = func{(x: i32): i32 -> {                        // 多语句块体
    const doubled = (x * 2)
    return@_ doubled                                   // 块体必须显式 return@；裸 return 是编译错误
}}
var f = func{(x: i32): i32 -> named calc { return@calc (x * 2) }}

// 代码块与语句（语句以换行或 } 结束）
{
    var x = 1
    x = (1 + 2)                                        // 赋值
    x += 1                                             // 复合赋值（M34：+=/-=/*=//=/<<=/>>=/>>>=/&=/|=/^=）
    foo().field = v
    return x                                           // return / return@_ value
    break@outer                                        // break/continue[@标签]
}

// if 语句（else 可选，支持 else if 链）
if (x > 0) { foo() } else if (y > 0) { bar() } else { baz() }

// 循环（for-each / 范围 / while / do-while / named 标签）
for (item in collection) { print(item) }
for (i in 0 to 10) named outer { break@outer }
while (condition) { doSomething() }
do { doSomething() } while (condition)

// try-catch-finally（SYNTAX.md §8）：完整异常处理系统
try {
    riskyOperation()
} catch (e: IOException) {
    handleIO(e)
} catch (_: RuntimeException) {
    // 丢弃异常变量
} finally(e) {
    // e 为异常或 null
    cleanup()
}

// throw 语句：抛出异常
throw new IOException("File not found")
throw getError()
if (invalid) {
    throw new ValidationError()
}

// seq 块（SYNTAX.md §6）：作用域/using 资源管理/named 标签/表达式形态
seq {
    var temp = compute()
}

volatile seq {
    // volatile 操作
}

seq using(const file = new File("path"))
using(var stream = new FileInputStream(file))
named readFile {
    process(stream)
}

// seq 作为表达式（return@_/return@标签，匿名默认标签为 _，M33）
const result = seq {
    const ac = a * c
    const discriminant = (b * b) - (4.0 * ac)
    return@_ sqrt(discriminant)  // 返回值
}

var r = seq named calc {
    return@calc getValue()
}

// await/yield 协程操作（SYNTAX.md §7.5）
const user = await loadUser(42)
await flushLogs()
yield                         // 裸 yield
yield sleep(1000)             // 带 alarm

// 类型声明（class/interface/struct/wrapper + 修饰符/继承/implements/嵌套）
pub open class Dog : Animal implements Drawable, Serializable {
    pub var name: String
    pub init(x: i32) {}
    pub func speak(): String { return "Woof!" }
    pub class Inner {}                 // 嵌套类型，与顶层同一路径
}
pub rich struct Entry {}
pub shared rich struct SharedEntry {}
wrapper Logged {}

// like 委托（§9.6，仅 class）与 ext 扩展成员（§4.4，限定名 Type.member）
pub class Apple : Fruit like pear {
    pub var pear: Pear = Pear()
}
pub ext func String.reversed(): String { ... }
pub ext var String.isEmpty: bool { get(_: _) { return (this.length == 0) } }

// enum struct 的 [case 列表]（§12：固定/参数化 case、_ 参数洞、显式判别值）
pub enum struct RequestResult {
    pub const errorCode: i32
    pub init(code: i32)
}[
    Success(-1) -> 0,
    Failed(errorCode = _) -> 1
]

// 全局字段与全局函数（与类成员走同一条解析路径）
pub const MAX: i32
var counter: i32
func add(a: i32, b: i32): i32 { return (a + b) }
pub static func helper()

// 属性访问器（§9.4：类字段/全局变量/栈上变量三类位置同一条路径）
var width: i32 {
    pub get(value: _) { return value }       // backing field + 自定义体
    priv set(value: _) { log(value) }
} = 100
var height: i32 {
    pub get                                  // 编译器生成实现（无参无体）
    priv set
} = 200
var area: i32 { get(_: _) { return (width * height) } }   // 计算属性（无 backing field）

// @ 注解 / wrapper 应用（§14.5：可叠加，挂所有声明；含编译器内建 @WrapperTarget）
@WrapperTarget(.Entity)
pub wrapper Logged\<TTarget> { ... }
@WrapperTarget(.Value)
pub wrapper Clamped { ... }
@Logged("DEBUG")
@Serializable()
pub class MyService { ... }
@Timed()
pub func heavyComputation(): i32 { ... }
@Clamped(0, 100)
var health: i32 = 50

// wrapper proxy 成员（§14.2：specific + 四类 wildcard；同类 wildcard 唯一，§14.6）
operator .proxy.doSomething(arg: i32): String { ... }       // specific 方法代理
operator .proxy.opr.plus(another: TTarget): TTarget { ... } // specific 运算符代理
operator .proxy.get.name\<TField>(value: TField): TField { ... }
operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, ...): TReturn { ... }
operator .proxy.get.*\<TValue>(symbol: String, value: TValue): TValue { ... }
operator .proxy.set.*\<TValue>(symbol: String, value: TValue) { ... }
operator .proxy.opr.*\<named TNamedArgs..., ...>(...): TReturn { ... }
operator .proxy.call(.name: String, args: named Any...): Any { ... }  // method canonical + .name 保留参数名（§14.4，M34）

// 前导点 enum case 引用（§12：固定/参数化 case）
const result: RequestResult = .Success
const failed: RequestResult = .Failed(404)

// import（§15.2：单个/多个/全部三种形态；多个导入共享前缀路径）
import core.collections.List
import core.collections.{List, Map}
import core.collections.*

// namespace 声明（§15.1：顶层单行声明）
namespace com.example.myapp

// wrapper 路径访问（§14.1/§3：与成员访问同属路径后缀链，链式左结合）
var logger = service:Logged
var w = obj:A:B
var l = foo().bar[0]?.length:MyWrapper
var t = service:Logged.level

// 声明上的泛型参数（类型/函数/operator，含型变/约束/可变参数）
class Container\<TElement> { ... }
class Cache\<out TElement extends Comparable> { ... }
func transform\<TInput, TResult>(input: TInput): TResult { ... }
func update\<named TValues... with Serializable>(configs: named TValues...): bool { ... }
pub operator plus\<TAnother extends Addable>(another: TAnother): V { ... }

// 函数形参列表（已接入 func/operator/init 声明）
(a: i32, b: String = "x", rest: named i32...)

// init 参数映射（§9.3：_ 同名映射 / 显式名 / 默认值 / 与普通参数混合）
pub class Point {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
    pub init(_ -> x = 0, _ -> y = 0)
    pub init(horizontal: i32 -> x, vertical: i32 -> y)
}
```

---

## 3. 组件状态详表

| 组件 | 状态 | 测试 | 说明 |
|------|------|------|------|
| LiteralParserLayer | ✅ | 78/78 | 全部字面量（M31：0b/0o/下划线补齐，`3.` 报错）；字符字面量（M34：CharLexerLayer + CharToken + CharLiteralASTNode）；多行字符串经 StringToken 复用零改动接入（M32）；插值段序列状态机（M53：InterpolationStart 委托 ExpressionParserLayer 就地填充段 Root，allowBareReturn 传染）+ 结构/span/JSONL 往返 + 错误路径（M51/M53） |
| MultilineStringLexerLayer（+ QuoteLexerLayer 分流） | ✅ | 48/48（M32 新套件） | SYNTAX §3.3 Swift 风格严格多行：开界换行剥除、闭界独占行定缩进基准、转义与单行一致（StringEscape 单源）、两阶段施工（按行缓冲 + 闭界时剥缩进/转义）、插值标记词法期判定（`\${` 不误报）；M53 插值词法帧机制：累积阶段挂起 $ 判定 + 段结算产出（原文暂存保序）+ 闭界统一回填（剥缩进 + 转义；词法先于解析全量完成，回填天然安全）+ 首段 span 修正；无插值时保持单 token 与含闭界 span 行为 |
| TypeReferenceParserLayer | ✅ | 17/17 | M31 重写为真实套件（独立层驱动 + 集成 + 结构断言） |
| VariableDeclarationParserLayer | ✅ | 25/25 | Initializer 经 ExpressionRootASTNode 直挂；访问器块委托 PropertyAccessorParserLayer（M16）；M31 保留字/标识符校验；解构声明三分支状态（M52，SYNTAX §18：var (a, b) = pair，DestructureNames 互斥字段） |
| ExpressionParserLayer | ✅ | 137/137 | roadmap #4 全部落地；前导点 enum case（M20）、wrapper 路径访问 `:`（M21）；M31：位运算符 `<<`/`&`/`\|`/`^`、`in` 移除、insideParens 续行、复合赋值 10 运算符（M34）、span 含关键字；**M42 路径表达式统一**：后缀链就地施工单一 `PathExpressionASTNode`（首段 + 段 + 后缀，原 SymbolReference/Call/Index/MemberAccess/WrapperAccess 五节点删除）；M52 中缀 `if` 重组为 `if?` 空值回退运算符（IfNullFallbackSeen 态，参照 as? 模式） |
| ArgumentListParserLayer | ✅ | 12/12（M31 新套件） | 位置/具名/混合实参；M31：续行、空索引拒绝 |
| LambdaExpressionParserLayer | ✅ | 37/37 | roadmap #21 提前落地；体双形态（M33）：单表达式 / 多语句块体（named 标签、裸 return 边界） |
| SwitchStatementParserLayer | ✅ 两种形态 | 35/35（SwitchExpression 套件） | 表达式 + 语句形态（M33，新 SwitchStatementASTNode）；分支体统一代码块、named 标签、两形态强制 default |
| TypeOfExpressionParserLayer | ✅ | 7/7 | typeOf(expr) |
| CodeBlockParserLayer | ✅ | 29/29 | 语句识别与分发；return/break/continue 内联子状态；@ 注解声明分发（M20）；switch 语句路由与 allowBareReturn 裸 return 检查（M33） |
| IfStatementParserLayer | ✅ 两种模式 | 含于各套件 | 表达式模式强制 else；语句模式 else 可选 + else if 链；表达式分支体统一代码块 + named 标签（M33） |
| LoopParserLayer | ✅ | 15/15 | for-each/范围/while/do-while/named 标签 |
| TryCatchFinallyParserLayer | ✅ | 9/9 | roadmap #10；多 catch 子句、finally(e)、嵌套 try |
| SeqBlockParserLayer | ✅ | 17/17 | roadmap #11；volatile/using/named；语句+表达式双形态 |
| ThrowStatement（内联） | ✅ | 10/10 | throw expression；配合 try-catch 构成完整异常系统 |
| CoroutineOps（await/yield） | ✅ | 13/13 | roadmap #12；await 一元前缀运算符，yield 语句 |
| GenericParametersParserLayer | ✅ | 21/21 | 声明/约束/型变/可变参数；已接入类型/函数/operator 声明（M15） |
| ParameterListParserLayer | ✅ | 14/14 | 普通/默认/可变/具名可变；已接入 func/operator/init 声明；init 参数映射 `_ -> field`（M18，allowMapping 开关） |
| PathParserLayer | ✅ | 16/16（M31 新套件） | 符号路径 + `\<` 泛型实参；M31：尾点/双点/未闭合泛型报错（allowVariadicDots 保留 `...`）；M42 起收窄为**类型引用与 import 路径**专用（表达式路径由 ExpressionParserLayer 就地施工） |
| RootParserLayer | ✅ | 含于各套件 | 顶层分发（声明统一委托 DeclarationParserLayer） |
| DeclarationParserLayer | ✅ 统一声明层 | 94/94（TypeDeclaration 套件） | 任何位置任何声明的唯一入口：全局/成员/嵌套共用一套状态机；声明泛型参数（M15）、enum `[]` case 列表（M17）、like 委托与 ext 限定名（M19）、@ 注解与 wrapper `.proxy.*` 代理成员（M20）已接入 |
| PropertyAccessorParserLayer | ✅ | 17/17 | §9.4 访问器块 `{ get... set... }`；backing field 判定与 get/set 一致性校验；三类定义位置经 VariableDeclaration 汇聚 |
| ImportParserLayer | ✅ | 14/14 | §15.2 三种形态（单个/`.{}` 多个/`.*` 全部）；前缀路径复用 PathParserLayer（M21 重建） |
| NamespaceParserLayer | ✅ | 7/7 | §15.1 顶层单行声明；路径复用 PathParserLayer（M22） |
| ASTIntegrityValidator | ✅ | 含于各套件 | Parse 成功后自动验证 AST 不变量（M23）；M24 重写为 Attribute 驱动遍历；M28 基于 ASTVisitor 统一遍历重写 + span 校验与类型审计；M31：Required 子节点校验、基类链字段审计、[AstCarrier] 递归审计；失败抛 CompilerInternalException |
| ASTIntegrityValidatorTests | ✅ | 10/10 | 手工构造 AST 直调 Validate：合法树通过 + 结构破坏/span 破坏/类型审计违规拒绝（M24/M28） |
| ASTVisitor | ✅ | 含于 Validator/Serializer 套件 | 统一 AST 遍历基建（AST/ASTVisitor.cs）：[ChildAstNode] 子节点枚举唯一实现，Validator 与 Serializer 共用（M28） |
| LexerFuzzTests | ✅ | 32/32 + fuzz 6000 | Slash/EOF/注释固定用例 + 位置精确性用例（M28；M31 起左闭右开）+ 块注释吞字符/跨行分段/`\r\n` 归一/字符字面量报错用例（M31）+ 纯随机/结构化/变异 fuzz（固定种子，不变量含 sourceName/offset/范围不颠倒）+ Parser 注释跳过集成（M25） |
| TokenDispositionTests | ✅ | 4/4 | Push/Pop × Consume/Replay 四组合协议测试（M23） |
| Logger | ✅ | 7/7（LoggerTests） | 统一日志出口（Core/Logger.cs）：Verbose/Warning/Error 三级；控制台默认只显示 Warning+，`--verbose` 子命令放开 Verbose；`--log-to PATH` 全量（含 Verbose）JSONL 落盘（M26） |
| AstJsonlSerializer | ✅ | 86/86 | AST 树 JSONL 序列化 v2（M31：carrier 记录化、Nullable 标量、先过滤再取值、循环保护）+ `AstJsonlDeserializer` 完整反序列化（字段名键控、产物过 Validator、往返逐行一致）；`compile --dump-ast PATH` 输出（M26）；M33 新结构（值块/switch 语句/lambda 块体）经反射驱动零改动接入 |
| CommandLine | ✅ | 50/50（CommandLineParserTests） | CLI 内核（Core/CommandLine.cs + Core/Commands.cs）：CommandLineMask 自描述元数据驱动解析与 help 生成；`<COMMAND> [--sub-cmd...]` 结构（compile/test/help），交互菜单已删（M27）；compile 子命令含 `--parse-only`/`--dump-ast`/`--emit-bil`/`--sema-only`（M44 接入语义管线与 BIL 发射，诊断经 Logger 走 stderr、有 Error 退出码 1） |
| DiagnosticBag（中端，S0） | ✅ | 15/15（DiagnosticsTests） | 中端可恢复诊断基建（M36，`Semantic/Diagnostics.cs`）：`Diagnostic{Severity/Phase/Span?/Message}` + `DiagnosticBag`（全编译单元单实例、只追加、HasErrors 阶段推进门槛）；`CheckSemanticError` 断言入 TestHarness |
| SymbolGraph（中端，S1） | ✅ | 45/45（SymbolGraphTests） | 语义符号图内核（M37，`Semantic/Symbols/`）：SemanticSymbol 家族（Namespace/Type/Field/Method/Parameter/GenericParameter，引用相等即身份）、构造泛型驻留 cache（同 (定义, 实参) 必同实例、T? = Nullable\<T>）、Freeze 机制；BootstrapSymbols 硬编码 SYNTAX §3.1 层级 + §3.2 基本类型 + 特权关系（Box\<T\> <: Object、Nullable shared 按 T 推导）+ 基元 intrinsic 键空间（BIL §11） |
| CanonicalSymbolPrinter（中端，S1） | ✅ | 22/22（CanonicalSymbolPrinterTests） | 符号图 → BIL §5.2 canonical 字符串（M37）：类型/方法/字段/运算符/getter/setter 五形态 + BIL 类型引用投影（固定别名 > 标准构造 > canonical/闭合泛型，null 返回 → .void）；打印串对照 §5.2/§8.1/§20 示例逐条断言 |
| BilModel + BilWriter（中端，S4） | ✅ | 11/11（BilWriterTests） | BIL 对象模型与文本生成（M38，`Bil/` 五文件）：Module/Metadata/Resources（§19 全形态）/类型与成员声明（§8）/Function/.args/.vars/Block（§9）/指令与操作数（§10–§16，§17 协程暂缓）；对中端零依赖、字符串身份、Origin 以 object? 占位；writer 只输出标准 spelling、全段输出、§20 黄金示例逐行一致（含 wrapper 隐藏字段续行形态）；M43 起符号段允许 §8.4.1 裸成员条目（BilSymbolSectionEntry）；**M57 全模型对象化重构**（去魔法 string）：指令强类型子类族（`BilInstructions.cs` 基类 + Compute/Data/ControlFlow 三文件——opcode 拼写/操作数序/switch/try 多行排版由类固定）+ `BilSpellings` 拼写唯一定义点 + 六枚举（BilTypeKind/BilMemberKind/BilBlockModifier/BilAccessibility/BilKeyword/BilScalarType）+ BilModifier 子类族 + BilSwitchTableResource/BilCatchTableResource 专用资源类（header/元素自渲染）+ blk/res 操作数持对象引用；BilWriter 删除 opcode switch（指令自渲染 WriteTo）；行为零变化（黄金文本逐字节一致） |
| BilVerifier（中端，提前自 S12） | ✅ | 74/74（BilVerifierTests） | BIL 验证器（M58，`Bil/BilVerifier*.cs` 五文件，对 Semantic/AST 零依赖）：消费 BilModule 对象模型，覆盖 §21.1–21.8 静态可判子集（§21.9 VM 语义除外）——词法语法/符号（fn↔声明一一对应、native 规则、static 一致性、资源类型可解析）/类型（逐指令严格相等，读写分类唯一表）/保守 DA/控制流（entrypoint 结构化终止、块成员资格、token 作用域、结构环拒绝）/breakid capability/声明侧修饰符矩阵；防误报降级（.generic< 跳过、基名大小写不敏感、IsAssignableTo extends/implements 链、查不到声明降级通过）；预定义符号表对齐 BootstrapSymbols（内建不声明决策的可解析性闭合，收技术债 #16）；指令双 switch default 防腐化 |
| DeclarationCollector（中端 P1，S2） | ✅ | 83/83（DeclarationCollectorTests） | 声明收集（M39）：`Semantic/CompilationUnit.cs`（多源文件 + DiagnosticBag + SymbolGraph）+ `Semantic/DeclarationCollector.cs`（DeclarationCollector + DeclarationCollection + FileContext）——类型/变量/可调用/参数/泛型参数符号壳（默认基类建壳即定、rich/shared/static 只读标记位）、namespace 逐段驻留与跨文件合并、import 上下文登记、ext 拆名待注册、重复声明诊断（类型/变量同名、方法 P1 文本级签名，重载不误报）；getter/setter 与 enum case 壳按需增补（S8/S11） |
| DeclarationResolver（中端 P2，S3） | ✅ | 179/179（DeclarationResolverTests） | 声明解析（M40，`Semantic/DeclarationResolver.cs`）：类型引用解析（泛型参数 → NestedTypes → namespace 父链 → 全局 → imports → core 隐式查找序；T?→Nullable\<T\>；失败绑 ErrorTypeSymbol 毒化静默）；init 映射参数沿字段类型；继承/implements 图（种类匹配、open/abstract 可继承性、class/interface 双环检测）；修饰符合法性（Parser 的 rich/shared/open 即死拦截与重复/互斥校验移交于此，可恢复诊断）；rich/shared 单向传染 + 字段闭包七行表（直接分类违规即报、放行才展开泛型实参递归）；共享安全闸门（全局/静态/ext静态）；泛型约束声明侧（Target 必本声明泛型参数、with 边界必 wrapper）；ext 注册（Owner 改写挂目标类型）+ wrapper 适用性（@WrapperTarget、§14.9 矩阵 A/B/D、interface 实现者传染）；M43 增补 native 声明校验子任务（SYNTAX §4.6 全规则 + @NativeLibrary/@NativeSymbol 解析写符号 + wrapper 应用检查豁免）与 Accessibility 写符号（§16，BIL 发射与 S8 消费）；结束 Freeze 符号图 |
| Binder（中端 P3，S5） | ✅ | 393/393（BinderTests） | 函数体分析（M41，`Semantic/Binder.cs` + `Semantic/Bound/` + `Semantic/NameResolver.cs`）：分析单位 BoundFunctionBody{Method, Locals, BoundBlock}；字面量定型（null 走可空上下文）、var 推断、LocalSymbol、二元/一元 bootstrap intrinsic 键查询（结果类型维度：比较 bool、余同操作数）、赋值与 definite assignment 最小版、无重载直接调用（具名实参归位规范参数序）、new/init 匹配、return 所有路径显式返回检查；值/调用查找序 块 → 参数 → **宿主类型成员（M43 落地，沿 BaseType 链；字段裸名归后续）** → 命名空间链字段/函数 → 通配 import；多段路径 = 容器 + 末段成员（首段命中局部/参数判实例路径暂拒）；IsAssignable（严格相等/可空提升/BaseType 链/直接 interface，显式 cast 归 P4a）；S7b 增补 if 语句（else if 链包单语句 BoundBlock）/if 表达式（BoundValueBlock 值块：M33 隐式取值判定、显式 return@ 标签栈解析、产值类型统一、GuaranteesValueReturn）/复合赋值（读语义 unassigned + intrinsic 检查）+ definite assignment 分支合并（before∪(setT∩setF)）+ GuaranteesReturn 双分支 if 升级；S7c-1 增补 while/do-while 循环绑定（BoundLoop 施工壳 + 循环标签栈解析 break/continue 标签：无标签栈顶/named 从内向外、循环外与未定义标签诊断；for 报 not supported yet (S7c-2)；条件 bool 检查与 if 共用 CheckBoolCondition）+ DA 循环两规则（while 后 = before、do-while 后 = 体尾集合）+ 值块内 break/continue 穿透（GuaranteesValueReturn 视其为路径终止，BIL §16.5 动态结构作用域）+ return@ 隔循环边界拦截（值块栈记 LoopDepth，脱糖无法表达跳出中间循环）；S7c-2 增补 this（宿主统一 method.Owner——ext 方法 Owner = 目标类型；静态上下文诊断）+ 实例成员链上色（实例方法调用/实例字段访问：receiver 静态类型沿 BaseType 链查找，接口 receiver 查接口成员，ext 注册成员同路径；`?.`/wrapper `:` 段仍归口 S7f/S11）+ 裸名实例成员补 this（FindField/FindMethods 宿主链改 method.Owner）+ for 双形态（范围循环 = EnumerateInRange ext operator 实例调用 + for-each 协议判定（实现 core.collections.IEnumerable\<TItem\>，含「Iterable 类型自身即构造」分支；协议三方法符号挂 BoundLoop——P4 不做名字分析；循环变量 const 只读默认；DA for 后 = before）；访问控制（priv/protected）检查不做（归 S8，命中即放行）；S7d 增补 switch 语句/表达式绑定（BoundSwitchStatement/BoundSwitchExpression/BoundThrowStatement：switch 占位 `_` 栈——BindPath 单段 `_` 命中栈顶 selector 回指占位、值匹配限编译期常量且类型严格等于 selector 类型、pattern 必须 bool、分支产值类型统一与值块构造名区分 if/switch、GuaranteesReturn 终止口径扩展（throw 与全分支 return 的 switch 视为终止）、DA 分支合并复用 if 基建）+ throw 绑定（IsAssignable 到 bootstrap 异常根 core.Exception）；S7e 增补 cast 绑定（BoundCastExpression：as 结果即目标类型、as? 结果 Nullable\<T\>——P3 定型 P4 不再区分包装，可转性不做静态拒绝：as 失败是运行时 core.CastException、castTo/castFrom 名字分析归后续，ErrorType 毒化静默）+ try/catch/finally 绑定（BoundTryStatement/BoundCatchClause：catch 类型 IsAssignable 到 Exception、catch 变量与 finally(e) 变量 const 只读且命中即 assigned——finally 变量类型 Nullable\<Exception\>，DA 合并 = before∪(try∩全 catch)∪finally）+ seq 双形态（BoundSeqStatement 直通 BindBlock、不压值块标签栈——return@ 指向语句 seq 报未定义标签；BoundSeqExpression 复用 BindValueBlock 值块语义 + IsVolatile 置位 + 必须产值检查；using 拦截归 S13）；S7f-1 增补字符串插值绑定（BindStringInterpolation：字面量段复用字面量机器、非 String 段包 toString() 实例调用（Any 承诺沿 BaseType 链查最近声明）、全 String 段左结合 + 链（String.Add intrinsic——bootstrap 同步开放 "a"+"b"），绑定即规范化、P4 零新增节点）；S7f 收官增补 `?.`（BindInstanceChain SafeDot 分派：receiver 必须 Nullable\<T\>、段经 BoundSafeAccessReceiverExpression 占位叶子在非空 T 上绑定、结果不二次包装；普通段遇可空 receiver 专门诊断）+ `if?`（BindNullFallback：左 Nullable\<T\>、右 IsAssignable 到 T、结果恒 T）+ 解构（BindDestructuring：沿 BaseType 链找 core.Pair 构造、名字数恒 2、分量类型 = 构造实参；构造类型成员查找 ConstructedFrom 回退（FindInstanceField/FindInstanceMethods/FindField/FindMethods 统一）+ 泛型字段类型最小替换 SubstituteFieldType——S9 前置特判）；S8c 增补索引访问（BoundIndexExpression 读绑 getAtIndex/写绑 setAtIndex + FindInstanceOperators）与表达式底座链泛化（PathVisitors 重构，解开全部 S8 归口诊断）+ 赋值/复合赋值 place 扩展；S8d 增补重载解析（OverloadResolution 新设施——结构过滤/类型适用性/最具体胜出 + 平局打破，调用/init/索引读共用，MatchSingleCandidate 删除）+ 默认参数（ParameterSymbol.DefaultValue + P2 顺序检查 + BindingDriver 阶段 1 声明点绑定（IsDefaultValueContext 隔离形参与 this）+ BindEnvironment.ParameterDefaults 记忆化按需填充——前向依赖声明顺序无关）+ 位置实参覆盖具名占位修复；其余控制流/泛型等遇之报 P3 诊断（归 S8e–S13） |
| StdlibSources（中端，S6/S10 机制） | ✅ | 78/78（StdlibSourcesTests） | stdlib 内嵌源载入（M43，`Semantic/StdlibSources.cs`）：`stdlib/**/*.latte` 以 EmbeddedResource 内嵌、编译时取出解析为 RootASTNode 注入编译单元（sourceName 为 `<stdlib>/...` 映射形，含点开头文件名反推），与用户源同走 P1–P4；M74 起六源（按逻辑名 Ordinal 排序）：`stdlib/.bootstrap.latte`（基元自举源，SYNTAX §15.3：ext operator i32.EnumerateInRange + core.Pair 解构协议根）、`stdlib/core/Console.latte`（core.io::Console）、`stdlib/core/collections.latte`（core.collections 双接口 + RangeEnumerator\<T\> 泛型抽象基类 + RangeEnumeratorI32/RangeI32）、`stdlib/core/coroutine.latte`（S10：core.coroutine 类型面——Task/Task\<TResult\>/Executor 家族/PollingAlarm/EventAlarm/CoroutineLocal\<TValue\> 全 shared abstract + sleep native，SYNTAX §15.3）、`stdlib/core/disposable.latte`（S10：core.IDisposable，§6.2）、`stdlib/core/exceptions.latte`（S10：RuntimeException/IOException/CastException/NoSuchMethodException 四异常子类，§8.1） |
| Lowerer + BilEmitter（中端 P4a/P4b，S6+S7a+S7b+S7c+S7d+S7e+S7f） | ✅ | 157/157（BilEmitterTests）+ 139/139（LowererTests） | P4（M44/M45，`Lowering/`）：`Lowered/` 节点集（S6 五类 + S7a 补齐八类：局部声明/表达式语句/赋值/字段引用/二元/一元/带返回值调用/new，Origin 必填回指 BoundNode，LoweredExpression.Type 透传不冗余）+ `Lowerer`（S5 全部 Bound 节点恒等重写 + S7b 起 session 化：前置语句机制 + 合成局部 `.sN`（BIL §5.1 编译器保留名）+ bool 短路 and/or 按 §11.3 展开为 if 块、值块降级与 if 转换、复合赋值脱糖为前置赋值，新增 LoweredIfStatement/LoweredConstantExpression 节点、合成节点 Origin 指最近语法来源；S7c-1 循环降级：LoweredLoop/LoweredLoopControl 新节点、条件求值移入 Judge 块（条件内短路/if 表达式前置语句随块走）、合成 bool 条件局部 .sN + 合成 .breakid 局部 .bN（LocalSymbol.Type 可空方案——null 仅限 .breakid capability，emitter 侧 .vars 投影 .breakid §9.3）、BoundLoop → BreakId 映射栈、break/continue 真跳转对 if 转换零改动；S7c-2 实例成员恒等降级（LoweredThis/LoweredInstanceCall（Type 自带——for 脱糖合成节点 Origin 是语句）/LoweredFieldAccess 三节点）+ for 脱糖（前置 iterate() 写合成枚举器局部（GetConstructedType(IEnumerator, TItem)）+ Judge=moveNext + Body 头=current——产物复用 LoweredLoop，P4b 零新增；协议三方法取 P3 挂在 BoundLoop 的产物）；S7d switch/throw 降级（LoweredSwitch（全值匹配恒等，携 .breakid 合成局部 + DefaultBody）/LoweredSwitchCase/LoweredThrowStatement 三节点、Any(IsPattern) 分流——pattern 链降级：selector 物化 `.sN` 前置 + 递归嵌套 if 链、值分支条件 = 合成 cmp.eq（Origin 指 match 常量，`LoweredBinaryExpression` 可选显式 Type——透传会是错的）、switch 表达式结果局部 `.sN` + 前置语句；同批修复 M46 else-if 链值块编织 miscompile——TransformStatements 重写为 continuation 编织：终止分支织空 continuation、非终止分支织 rest）；S7e cast/try/seq 降级（LoweredCastExpression（Type 自带——as? 的 Nullable 结果 P3 已定型）/LoweredTryStatement/LoweredTryCatch/LoweredSeqBlock 四节点：try 合成 ExceptionSlot——finally(e) 时 slot 即 finally 变量、否则合成 `.sN`（Nullable\<Exception\>）；有名 catch 体头编织「变量 = cast slot」合成赋值（Origin 指 BoundCatchClause）；seq 语句/volatile 恒等、seq 表达式脱糖为前置 seq 块写合成结果局部 + 原位置读局部；try+finally 部分终止的值块编织拦截——TransformWithContinuation 深处触发 transformFailed 标记、诊断落袋后跳过函数体），未覆盖节点 P4 Error + 跳过函数体）+ `BilEmitter`（LoweredTree → BilModule：LocalSymbols 全量平铺含全局裸条目与 native/entrypoint 修饰符、extends 与种类默认基类相同则省略、Resources §19.1 标量全形态提取去重（string/int 系列/bool/char/f32/f64/null type(...)）、fn 定义 .args/.vars/单 entry block、临时变量 `.t0` 前缀、set.var/get/set.field.static/§11 运算单点映射/invoke/new 发射、void 末尾补 ret、S7b 多 block（§16.2 `if $c blk(then) blk(else)`、无 else 用 none、block id `if0-then` 形态无点号、分支块落尾不补 ret、合成 bool 常量与字面量同键去重）、S7c-1 循环发射（§16.3/§16.4 `loop`/`loop.rev` 操作数序 cond/body/none/judge/breakid、块 id `loop0-body`/`loop0-judge` 递增、§16.5 `break`/`continue` 携 breakid、.vars 的 .breakid 条目）、S7c-2 实例发射开闸（实例方法 fn 定义 .args 插 .this = OwnerType（§9.2/§7.3，ext 同形态）、实例 invoke receiver 首实参（接口方法 canonical 分派归 Middleware）、get.field/set.field（§13.3）、this → $.this 零指令、init/operator §8.4 声明形态（init / operator(名) / ext 修饰符）、EmitBuiltinExtMembers——内建类型 ext 成员以 §8.4.1 裸条目输出）、S7d switch/throw 发射（§16.6 `switch` 五操作数 selector/res(表)/[blk item 表]/blk(default)/breakid、块 id `switch0-itemN`/`switch0-default`、§19.4 `switch-table<T>` 单行资源同（header, 元素序列）跨 fn 去重——RegisterSwitchTable 复用 resourceKeys 字典与 RenderLiteral 渲染、§16.9 `throw` 单操作数）、S7e cast/try/seq 发射（§12.1/§12.2 `cast`/`cast.safe` 三操作数 SOURCE RESULT type(TARGET_TYPE)、§16.1 `call blk(seqN)` 不建栈帧 + volatile → §9.6 block 修饰符、§16.7 `try` 四操作数 blk(body)/$slot/res(表)/blk(finally)|none——块 id `try0-body`/`try0-catchN`/`try0-finally`、§19.5 `catch-table` 多行资源元素 `type(T) -> blk(...)` 保序——RegisterCatchTable 复用 resourceKeys 同序列去重）、Origin 塞 LoweredNode）；hello world 黄金输出逐行一致 + S7a/S7b/S7c-1/S7d 各形态 fn 指令与多 block 精确比对 + Origin 调试链断言；S7f-1 增补子类型 cast 物化（ARCH §6.1 首个落地：EnsureDeclaredType 统一五位置——实例/void 调用 receiver（≠ 方法宿主的装箱/基类视图，如 i32 调 Any.toString）、调用/new 实参（≠ 形参）、局部初始化、赋值、return；类型相同/ErrorType/非 TypeSymbol 直通，插值经 P3 绑成 toString/+ 链后恒等降级；hello world 黄金文本两处 return 严格化为显式 cast，BIL §6.5 合规）；S7f 收官增补 `?.`/if?/解构脱糖（LowerSafeAccess：物化 receiver .sN + 结果局部前置 null + if(cmp.ne recv, null) + 占位映射栈替换 unwrap cast + wrap cast，嵌套逐层命中；LowerNullFallback：物化左侧 + if/else 双分支（unwrap/回退）延迟求值；LowerDestructuring：pair 物化 + 逐字段读取——LoweredConstantExpression 扩展 null 常量，BilEmitter 发射 RegisterNullResource 统一 null 资源（§19.1 类型语义 .nullable\<T\>，与 cmp.ne 满足 §11.5）；LoweredFieldAccessExpression/LoweredLocalDeclarationStatement origin 放宽 BoundNode 承载合成路径）；`Tests/LoweredDescribe.cs` 为唯一 Lowered 树描述器 |

---

## 4. 关键架构决策（摘要）

- **施工目标协议**（M23 大扫除）：Parser Layer 栈只传递控制权；父 Layer 在 Push 前确定施工目标（具体节点或 ExpressionRootASTNode 等附加目标），子 Layer 原地施工或向目标附加节点；Pop 不传递任何数据。原 `IResultProducer`/`IResultConsumer`/`pendingResultHandler` 已全部删除
- **TokenDisposition**：Push/Pop 的 token 处置使用具名枚举（Consume/Replay），替代原 `bool shouldKeepToken`
- **ExpressionRootASTNode**：Syntax AST 中所有表达式位置的统一稳定挂载点；一次性 Attach、禁止替换；ASTNode.Parent 只能设置一次、**禁止任何形式重挂**（无 reparent）；「归属后知」场景以创建时归属即定的容器承载（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）或延迟一次性 AttachTo（注解）；解析成功后经 `ASTIntegrityValidator` 自动验证不变量
- **EOF 正式 Token**：`EndOfFileToken` 由 Lexer 在输出末尾追加（M25；Parser 仅对绕过 Lexer 的调用方保持追加兼容），只由 RootParserLayer 消费；非 Root 层遇 EOF 要么 Pop(Replay) 层层上交，要么报 "Unexpected end of file"
- **注释集中跳过**（M25）：CommentToken 由 Parser 主循环分发时统一跳过，各 Layer 不再自行处理；行注释不再吞掉结尾换行（回流由 Base 层产出 LineBreakToken）
- **SlashLexerLayer**（M25）：`/`、`/=`、`//`、`/*` 统一分流入口；输入结束以虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException
- **泛型语法 `\<...>`**：`<` 仅作小于号；Lexer 不合并 `>` 系列，`>=`/`>>`/`>>>` 由表达式层重组（详见 `SYNTAX.md` §3.6）
- **表达式路径统一**（M42，SYNTAX §1.4）：表达式位置的符号引用、调用、索引、成员访问（含 `?.`）、wrapper 访问（`:`）统一施工为单一 `PathExpressionASTNode`——首段（符号名或表达式底座）+ 段序列（连接符 + 成员名 + 泛型实参 + 调用/索引后缀）；原 SymbolReference/Call/Index/MemberAccess/WrapperAccess 五节点删除。「首段身份」与各段语义（实例成员/静态成员/wrapper）是语义上色问题，全部归 P3；`PathParserLayer` 收窄为类型引用与 import 路径专用
- **独立 Layer 可测性**：`Parser.Parse(tokens, baseLayer, entryLayer)` + `TestRootParserLayer`（只接受 EOF）支持任意 Layer 独立驱动测试，且拒绝被测 Layer 漏消费 token
- **统一声明层**（M14，依据 SYNTAX.md §14.8）：canonical symbol 的类名段可为空、`.static.` 只是标记位，因此全局函数与成员方法结构同构——`DeclarationParserLayer` 一套状态机覆盖全局/成员/嵌套任何声明；`CallableDeclarationASTNode` 单节点覆盖 func/operator/init；成员挂各节点语义容器（M29 起：`RootASTNode.Declarations` / `CodeBlockASTNode.Statements` / 类型节点 `Members`；基类共有 `Children` 已删除）
- **日志系统**（M26）：`Core/Logger` 是唯一日志出口（Verbose/Warning/Error）；Lexer/Parser 的 ContextImpl 经 Logger 输出，禁止直接 `Console.WriteLine`；控制台门槛默认 Warning+，`--verbose` 子命令放开 Verbose；`--log-to` 把全量日志（含 Verbose）以 JSONL 落盘，文件不过滤级别，便于 grep 诊断
- **AST JSONL 序列化**（M26）：`AstJsonlSerializer` 复用 Validator 的 [ChildAstNode] 反射下钻，深度优先每节点一行（id/parent/via/type/fields），`compile --dump-ast` 输出，供结构诊断
- **CLI 插件化**（M27）：用法 `<COMMAND> [--sub-cmd [args...]...]`，COMMAND 为 help/compile/test；每个 COMMAND 与 --sub-cmd 都是插件，暴露 `CommandLineMask`（名称/描述/参数个数/互斥）自描述元数据；解析器与 `help` 文本完全由 Mask 注册表数据驱动、程序生成；交互菜单已删除
- **位置信息单源化**（M28）：`CharRange.sourceName` 是源名唯一来源（`CharPosition` 不再携带）；`CharPosition.offset` 为 0 起始字符索引（修复恒 0 bug）；换行算当前行最后一列（修复第二行起列号 +1）；token 头跳过空白（修复缩进行 token Start 落在前导空格）；EOF 冲刷帧占虚拟位置（修复 EOF 处 token End 少算/倒置）
- **AST Source Span**（M28）：`ASTNode.Span`（`CharRange?`）记录节点源码范围；层目标由 Parser 主循环按 token 流计算、经 `ISpanReceiver.ReceiveSpan` 在层弹出时回填（`??=` 只填空），层内自建节点由所在层显式设置（创建记 Start、完成封 End，经 `ParserLayerContext.GetPreviousLocation()`）；`ExpressionRootASTNode` 透明继承内容表达式的 span；Validator 校验每节点 span 非空、sourceName 非空、End 不早于 Start
- **ASTVisitor 统一遍历**（M28）：`AST/ASTVisitor.cs` 是 [ChildAstNode] 子节点枚举的唯一实现，ASTIntegrityValidator 与 AstJsonlSerializer 共用（via 统一为 `member[i]`/`member[i](Carrier.Field)` 格式）；Validator 新增类型审计——装 ASTNode 的成员（字段/自动属性）必须带 [ChildAstNode]/[ParentAstNode]，[ChildAstNode] 标在非 AST 成员上同样拒绝
- **AST 容器语义化**（M29）：`ASTNode` 基类只保留 `Parent`/`Span`，共有 `Children`/`Annotations` 删除——顶层条目挂 `RootASTNode.Declarations`、块语句挂 `CodeBlockASTNode.Statements`、类型成员挂各类型节点 `Members`（均标 [ChildAstNode]，Validator/ASTVisitor/Serializer 零改动）；注解列表仅 7 种声明节点持有，经 `IWrapperAttachable` 访问，并按 SYNTAX §14 三类目标以 `IEntity/IMethod/IValueWrapperAttachable` 分类标记（挂载校验留待语义阶段）；`DeclarationParserLayer` 构造函数改收 `(parent, targetList)`
- **Span 左闭右开**（M31）：所有 `CharRange`（token 与 AST 节点 span）统一为 `[Start, End)`——Start 指向首个字符，End 指向最后一个字符的下一位置；相邻 token 首尾相接，EOF 为零宽范围
- **续行规则**（M31，SYNTAX §1.1）：`()`/`[]` 未闭合时换行按空白处理——实参/索引/形参列表层全状态跳过；`ExpressionParserLayer.insideParens` 括号语境（分组/实参/条件/迭代）在结构等待态透明化换行，右操作数与一元操作数继承
- **数字字面量单源**（M31）：`Parser/NumericLiteral.cs` 是进制（0x/0b/0o）/下划线/后缀判定与解析的唯一实现，Literal/Root/Expression 三层共用
- **JSONL v2 与往返**（M31）：AST JSONL 字段名键控不依赖顺序；carrier（ImportItem）记录化（独立产行、标量字段入 fields）；`AstJsonlDeserializer` 完整反序列化（三阶段：类型定位/实例挂接/回填，产物强制过 Validator），Parse→Serialize→Deserialize→Serialize 往返逐行一致
- **测试基建单源**（M31）：`Tests/AstDescribe.cs`（统一 AST 描述器）与 `Tests/TestHarness.cs`（统一驱动+断言）是全部套件的唯一描述/驱动实现；断言对象约定：除查的就是命令行/日志/token 流/层协议行为的套件外，一律断言 AST 树产物（描述串 + 结构断言）
- **值块统一与裸 return 边界**（M33）：if/switch 表达式分支体与 lambda 体统一为 CodeBlockASTNode——「单表达式分支隐式取值」是「块内恰好一条 ExpressionStatement」的语义规则，解析层无特判（取值留待语义阶段）；多语句值块经 `return@_`（匿名默认标签）/ `return@标签`（`named` 命名）取值；lambda 是裸 return 边界——`CodeBlockParserLayer.allowBareReturn` 标记沿施工链（if/循环/try/seq/switch 子块与表达式深处的分支体）全链传染，lambda 体一律下传 false，遇无 @标签 return 抛 ParserException；副作用：值块内的 if/switch 一律按语句分发，作值须写 `return@_ if ...`

---

## 5. 下一步计划

**Parser/PDA 大扫除（M23）已完成**：控制流系统与 AST 施工系统分离，
施工目标协议、ExpressionRootASTNode、EOF 正式化、AST 完整性验证全部落地。

**AST 结构标注与 Validator 重写（M24）已完成**：结构关系以
[ChildAstNode]/[ParentAstNode]/[AstCarrier] 显式标注，Validator 改为
Attribute 驱动并新增父子指针一致性校验，借此修复 5 个历史结构 bug；
ASTNodeType 枚举删除，节点类型判断全面改用 CLR 类型。

**日志系统与 AST JSONL 序列化（M26）已完成**：Logger 统一日志出口、
verbose 默认关闭（`--verbose` 子命令打开），`--log-to` 全量 JSONL 落盘；
AST 树可经 `compile --dump-ast` 序列化为 JSONL 供诊断。

**CLI 插件化重构（M27）已完成**：交互菜单删除，用法统一为
`<COMMAND> [--sub-cmd [args...]...]`（help/compile/test 三个 COMMAND）；
选项以 `CommandLineMask` 自描述、插件化注册，帮助文本程序生成；
CI 入口改为 `dotnet run -- test --all`。

**Lexer 位置修复 + AST Source Span + ASTVisitor（M28）已完成**：
Lexer 的 offset/列号/token 头/EOF 冲刷位置全部修复，sourceName 单源化到
CharRange；每个 AST 节点携带源码范围 Span（层目标由主循环经 ISpanReceiver
回填、层内节点显式设置、ExpressionRoot 透明继承），JSONL 输出 span 键；
Validator 与 Serializer 遍历统一为 ASTVisitor，Validator 新增 span 校验与
「未标注 AST 成员」类型审计。

**AST 容器重构（M29）已完成**：`ASTNode` 基类共有的 `Children`/`Annotations`
删除，只留 `Parent`/`Span`；子节点容器下放为语义字段——
`RootASTNode.Declarations`、`CodeBlockASTNode.Statements`、5 个类型节点各自
`Members`；注解列表仅 7 种声明节点持有，按 SYNTAX §14 三类 wrapper 目标
抽象为 `IWrapperAttachable` + `IEntity/IMethod/IValueWrapperAttachable` 接口。

**Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档清理（M30）已完成**：
`Core/Utilities.cs` 按语义拆为 7 个文件（`Lexer/Tokens.cs`、`Lexer/Notations.cs`、
`Parser/Keywords.cs`、`Core/Exceptions.cs`、`AST/ASTNode.cs`、`AST/SymbolNodes.cs`、
`AST/ImportNodes.cs`），同命名空间纯搬移、零调用点改动；ASTVisitor 的
`VisitNode`/`EnumerateChildren` 改为 virtual（默认仍走 [ChildAstNode] 反射）；
文档中已删除代码的引用（FrontendTypesExtension 全系、AcquisitionExpressionASTNode、
CharLiteralASTNode 等）与过时表述已清理，`FRONTEND_ARCHITECTURE.md` 删除。

**前端大修（M31）已完成**：全量 review 驱动的 40+ 项修复——测试基建统一
（AstDescribe/TestHarness，21 套件迁移 + 2 新套件）、Span 左闭右开、
Lexer 块注释重写（吞字符修复 + 换行不吞）、续行规则、位运算符、
0b/0o/下划线字面量（NumericLiteral 单源）、Keywords 大扫除（幽灵词清除 +
保留字补齐）、修饰符组合与标识符合法性校验、JSONL v2（carrier 记录化）+
完整反序列化器往返无损、Validator Required 子节点与基类链审计、
Logger 改走 stderr。详见「里程碑历史」M31 段落。

**多行字符串（M32）已完成**：SYNTAX §3.3 多行字符串规范定稿
（Swift 风格严格多行：开界 `"""` 后换行剥除、闭界独占一行且其缩进为剥除基准、
转义与单行一致），Lexer 新增 `QuoteLexerLayer` 引号分流（`"`/`""`/`"""` 统一入口，
与 `SlashLexerLayer` 同模式）与 `MultilineStringLexerLayer` 两阶段施工
（原文按行缓冲，闭合时先剥缩进再统一转义），转义表收编为 `StringEscape` 单源；
插值标记改词法期判定（`StringToken.HasInterpolation`，修复 `\${` 误报）；
Parser/AST 经 `StringToken` 复用近零改动。

**值块统一（M33）已完成**：if/switch 表达式分支体与 lambda 体统一为
CodeBlockASTNode（多语句 + `return@_`/`named` 取值，单表达式分支隐式取值规则不变、
下沉为语义规则）；switch 语句形态落地（新 `SwitchStatementASTNode`，两形态强制
default）；lambda 体内裸 return 成为编译错误（`allowBareReturn` 标记全链传染）；
seq 匿名默认标签从 `seq` 迁移为 `_`（纯注释与快照迁移，解析层无特判）。
详见「里程碑历史」M33 段落。

**技术债清扫（M34）已完成**：字符字面量落地（`CharLexerLayer` 三态状态机 +
`CharToken` + `CharLiteralASTNode`，转义复用 `StringEscape` 单源）；复合赋值
10 运算符（`CompoundAssignmentExpressionASTNode`，ExpressionParserLayer 遇
op+`=` 在左操作数 Attach 前定形构造，红线合规）；`is` 右侧 enum case
（`TypeCheckExpressionASTNode` 的 `TargetType`/`TargetCase` 双字段互斥，
仅 `is` 允许 `.`，switch 模式匹配同路径通吃）；wrapper `.name` 保留参数名
（ParameterListParserLayer `DotNameExpected` 状态，Name 原样存 `.name`）；
import `{}` 列表项单标识符禁令规则化报错（SYNTAX §15.2）。详见「里程碑历史」
M34 段落。

**中端阶段开篇（M35）已完成——文档层**：M34 收官前端（Lexer + Parser），
M35 起项目进入**中端（语义分析 + BIL 生成）阶段**。本里程碑只交付文档：
架构定稿 `compiler/semantic/SEMANTIC_ARCHITECTURE.md`（P1 声明收集 /
P2 声明解析 / P3 Binder→BoundTree / P4a Lowerer→LoweredTree /
P4b BilEmitter→BilModule 四 pass 分工，Roslyn 风格双 Bound Tree、
驻留符号对象图、bootstrap 与 core.latte 边界、诊断模型）与路线图
`compiler/semantic/SEMANTIC_ROADMAP.md`（S0–S14，近细远粗）；
同批修订语言规范（String 归非 rich 值类型、wrapper 恒 rich struct、
共享安全类型与两条逃逸闸门、rich/shared 单向传染）。
详见「里程碑历史」M35 段落。

**S2 P1 声明收集（M39）已完成**：`Semantic/CompilationUnit.cs`（编译单元 =
多源文件 RootASTNode + 全局 DiagnosticBag + 唯一 SymbolGraph）+
`Semantic/DeclarationCollector.cs`（遍历声明骨架建符号壳，不进函数体；
namespace 逐段驻留跨文件合并、import 上下文登记、ext 拆名待注册、
重复声明诊断累积不中断）。符号模型按需增补：NamespaceSymbol 容器成员表、
TypeSymbol.NestedTypes、MethodSymbol/FieldSymbol.ExtTargetPath、
MethodSymbol.GenericParameters、SymbolGraph.GlobalNamespace + GetNamespace
驻留。详见「里程碑历史」M39 段落。

**S3 P2 声明解析（M40）已完成**：`Semantic/DeclarationResolver.cs`
（`DeclarationResolver.Resolve(unit, decls)` 入口 + 私有 ResolveSession）
落地七个子任务——类型引用解析（含 ErrorTypeSymbol 毒化静默）、
init 映射参数沿字段类型、继承/implements 图与双环检测、修饰符合法性、
rich/shared 单向传染与字段闭包七行表、共享安全闸门、泛型约束声明侧、
ext 注册与 wrapper 适用性（§14.9 矩阵 A/B/D）。Parser 两处越权即死拦截
（CreateTypeNode 的 rich/shared/open 校验、OnModifiers 的重复/互斥校验）
移交 P2 可恢复诊断；修复 Parser 约束裸名参数（`T extends Bound`）只进
Constraints 不进 Parameters 的历史 bug（现双注册）；bootstrap 全部内建类型
补登 `Core.Types`（裸名 `i32`/`String`/`Object` 可解析）。符号图结束
Freeze。详见「里程碑历史」M40 段落。

**S5 P3 最小闭环（M41）已完成**：`Semantic/Binder.cs` 以函数体为独立
分析单位产出 BoundTree（`Semantic/Bound/`：BoundNode.Syntax 必填回指、
BoundExpression.Type 定型、BoundFunctionBody{Method, Locals, BoundBlock}），
落地 S5 全部约定范围——字面量定型、var 推断、作用域链、intrinsic 键查询、
无重载直接调用（规范参数序）、new/init、return 与 definite assignment。
同批完成两件基建：P2 名字解析核心提取为 `Semantic/NameResolver.cs`
（P2/P3 按 Phase 各自实例化，P2 委托后 138 用例零回归）与
`Tests/BoundDescribe.cs`（唯一 bound 树描述器，仿 AstDescribe）。
发现两个前端事实并适配：括号产生 GroupExpressionASTNode（透明下钻）、
`c.m()` 是路径形态而非 MemberAccess（首段命中局部/参数判实例路径暂拒）。
详见「里程碑历史」M41 段落。

**路径表达式统一（M42）已完成**：SYNTAX §1.4 的语言观忠实落地——
表达式位置的符号/调用/索引/成员（含 `?.`）/wrapper（`:`）后缀链统一
施工为单一 `PathExpressionASTNode`（首段 + 段序列 + 后缀，原五节点
删除），语法层只表达形态事实，「首段身份/段语义」上色全部归 P3。
ExpressionParserLayer 后缀链重写（段/后缀就地生长 + 表达式底座包装 +
seed 改路径形态）、Binder 改为 BindPath 单点上色（消除 M41 的
「双形态双路径」妥协）、泛型实参统一走 TypeReference（中段可空实参
合法化，能力取并集）、171 用例快照迁移零行为回归（Binder 套件 90
用例未动即绿，验证 bound 产物与源码语义一致）。详见「里程碑历史」
M42 段落。

**stdlib 内嵌源（M43）已完成**：`Semantic/StdlibSources.cs` 把
`stdlib/**/*.latte` 以 EmbeddedResource 内嵌进程序集，编译时取出解析为
RootASTNode 注入编译单元，与用户源同走 P1–P4；首个内容
`stdlib/core/Console.latte`（core.io::Console：priv static native
print/printErr + pub static println，println 在 Latte 层包装）。

**S6 P4 最小闭环（M44）已完成**：`Lowering/` 落地——Lowered 节点集
（五类，Origin 必填回指 BoundNode）+ Lowerer（P4a 恒等重写，未覆盖
节点报 P4 Error 并跳过函数体）+ BilEmitter（P4b 发射：LocalSymbols
平铺、Resources 字面量提取、fn 定义与临时变量物化、Origin 调试链
接通）。**中端四 pass（P1/P2/P3/P4）全部打通**：hello world 从源码
走到合法 BIL 文本（黄金输出逐行比对）。详见「里程碑历史」M44 段落。

**S7a P4 基础发射补齐（M45）已完成**：P3（S5）能绑定的全部 Bound
节点过 P4——Lowered 补齐八类节点、Lowerer 恒等重写全覆盖、
BilEmitter 新发射 set.var / get/set.field.static / §11 运算（单点
opcode 映射）/ 带返回值 invoke / new / §19.1 标量资源全形态
（bool/char/f32/f64/null type(...)）；`Tests/LoweredDescribe.cs`
（唯一 Lowered 描述器）与 LowererTests 套件落地。详见「里程碑历史」
M45 段落。

**S7b（M46）已完成**：if 语句/表达式 + 值块 + 短路 and/or + 复合赋值，
P3/P4 同步落地——P3 新增五个 Bound 节点（BoundIfStatement/
BoundValueBlock/BoundIfExpression/BoundReturnValueStatement/
BoundCompoundAssignmentExpression）与值块标签栈 return@ 绑定、
definite assignment 分支合并、GuaranteesReturn 双分支升级；P4a
Lowerer session 化（前置语句机制 + 合成局部 `.sN`）落地短路展开、
值块降级与 if 转换、复合赋值脱糖；P4b BilEmitter 出多 block BIL
（§16.2 if 指令）。详见「里程碑历史」M46 段落。

**S7c-1（M47）已完成**：while/do-while/break/continue 三 pass 落地
（循环协议定稿随批：SYNTAX §7.3 范围循环半开 [a,b)、to 即
EnumerateInRange、IEnumerable 双接口）——P3 新增 BoundLoop（施工壳）
/BoundLoopControl 与循环标签栈、definite assignment 循环两规则、
值块内 break/continue 穿透、return@ 隔循环边界拦截；P4a 循环降级
（条件求值移入 Judge 块 + 合成 .breakid 局部 `.bN`，LocalSymbol.Type
可空方案）；P4b 发射 loop/loop.rev 三 block 与 break/continue +
.vars 的 .breakid 条目。详见「里程碑历史」M47 段落。

**S7c-2（M48）已完成**：实例成员最小闭环 + core.collections 迭代
协议 + for 双形态统一脱糖——P3 落地 this/实例成员链上色/裸名
实例成员补 this/for 绑定（范围循环 = EnumerateInRange ext operator
调用 + for-each 协议判定，协议三方法挂 BoundLoop，循环变量 const）；
P4a for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext +
Body 头=current）；P4b 开闸 .this/实例 invoke/get.field/set.field/
init/operator §8.4 声明形态 + EmitBuiltinExtMembers；stdlib 三源
（.bootstrap 基元自举 + collections 双接口）全量过 P1–P4。
详见「里程碑历史」M48 段落。

**S7d（M49）已完成**：switch 语句/表达式 + throw 三 pass 落地——
异常根 `core.Exception` 定稿进 bootstrap（`IsOpen`，具体子类归
S10 stdlib）；P3 落地 switch 绑定（占位 `_` 栈 + 值匹配/pattern
显式分类 + 产值类型统一 + DA 分支合并复用）与 throw 绑定（
IsAssignable 到 Exception）；P4a 常量 switch 恒等 + pattern 链
降级（selector 物化 + 嵌套 if 链）；P4b 发射 switch 指令（§16.6）
+ §19.4 switch-table 单行资源（同表跨 fn 去重）+ throw（§16.9）；
同批修复 M46 else-if 链值块编织 miscompile（TransformStatements
重写为 continuation 编织）。详见「里程碑历史」M49 段落。

**S7e（M50）已完成**：cast 最小闭环（提前自 S8）+ try/catch/finally
+ seq 三 pass 落地——P3 落地 cast 绑定（as/as? 定型，可转性不做
静态拒绝）、try 绑定（catch 类型 IsAssignable 到 Exception、
catch/finally(e) 变量 const、DA 合并）与 seq 双形态（语句直通 /
表达式值块复用 + 必须产值）；P4a try 合成 ExceptionSlot + catch 头
cast 编织 + seq 表达式脱糖 + try-finally 部分终止编织拦截；P4b 发射
cast/cast.safe（§12.1/§12.2）+ call blk(seqN)（§16.1）+ try 四
操作数（§16.7）+ §19.5 catch-table 多行资源。SYNTAX §7 控制流
全部贯通。详见「里程碑历史」M50 段落。

**S8a（M54）已完成**：is/supers/with + typeOf 三 pass 落地（S8 已
细化为 S8a–S8f 进 ROADMAP）——规范定稿（SYNTAX §3.5/§3.7 右侧
双形态解析规则 + with wrapper 限定 + typeOf 双形态定型 + castFrom
笔误修正 + §9.2 补 override 行）；P3 新增 BoundTypeCheckExpression
/BoundTypeOfExpression 两节点与不落袋试探双形态解析（is/supers/with
先类型后值、typeOf 单段裸名先值后类型，reportErrors: false）；
P4a 恒等重写；P4b 首次发射 BIL §12.3（type.is/type.supers/
type.with + 三 .indirect 动态形态）与 §12.5（getid.var/getid.type）。
is .Case 归 S11 落归口诊断。详见「里程碑历史」M54 段落。

**M55 已完成（架构重构）**：中端三树 visitor 化——Binder/Lowerer/
BilEmitter 三个 session 巨石（2890/1331/1045 行）按用户方案全部
重写为 CRTP visitor 架构（协议基类 + 静态 Visit 统一入口 +
Enter/Exit 生命周期模板 + 双协议 + context 方言接口视图 + 类别
分派器唯一 switch + 结构 visitor 簇级分文件，`Semantic/Binding/`、
`Lowering/`、`Lowering/Rewriters/`、`Lowering/Emitting/` 新组织）；
FlowState 提取为独立组件（S8b 收窄表的家）。行为零变化（三树
各自完成时全量测试全绿，BIL 黄金文本逐字节一致，测试零改动）。
架构定稿与进度见 `compiler/semantic/VISITOR_REWRITE.md`；同批
完成 S8b smart cast 语言规则专项定稿（`compiler/semantic/
SMART_CAST_DESIGN.md`：Q1=B 含 const 字段收窄/Q2=A guard 模式/
Q3=A and-or-not 全支持/Q4=A switch 占位收窄，并发现 `x == null`
无法绑定的前置缺口）。详见「里程碑历史」M55 段落。

**S8b（M56）已完成**：smart cast 三 pass 全通——SYNTAX §3.5 完整
规则 + §3.4 null 判等落地；FieldSymbol.IsConst 前置与 const 赋值
检查（init 豁免）；FlowState 收窄事实表（NarrowKey 根+const 字段链，
纯交集合并）+ ConditionFactsExtractor 真/假边提取 +
BoundSmartCastExpression 标记；guard 反向传播/and-or 右侧上下文/
while 体真边+体赋值根剔除/switch `(_ is T)` selector 收窄/赋值失效/
字段链稳定判定全部落地；P4a 物化 LoweredCastExpression（P4b 零新增）。
SmartCastTests 55 用例 + CLI 端到端三样例 BIL 核对。
详见「里程碑历史」M56 段落。

**BIL 生成全模型对象化重构（M57）已完成**：Bil/ 从「opcode 字符串 +
位置操作数列表」迁移为强类型对象模型（用户决策：去魔法 string）——
指令子类族固定拼写/操作数序/多行排版、`BilSpellings` 拼写唯一定义点、
六枚举与 BilModifier 子类族、switch-table/catch-table 专用资源类、
blk/res 操作数持对象引用；BilWriter 删除 opcode switch；P4b 值发射
契约 string → BilVariableOperand；行为零变化（BIL 黄金文本逐字节一致）。
详见「里程碑历史」M57 段落。

**S8c（M59）已完成**：索引访问 + 实例成员完整化三 pass 全通——
BoundIndexExpression（读形态绑 getAtIndex、写形态绑 setAtIndex）+
FindInstanceOperators（BaseType 链 + ConstructedFrom 回退 +
Kind/参数个数过滤）+ PathVisitors 重构解开全部 S8 归口诊断
（表达式底座绑定/首段与段后缀折叠/`this[i]`/容器末段成员后缀）+
赋值与复合赋值 place 扩展；多参数索引 `a[i, j]` 定稿为编译错误、
具名索引实参与普通调用同规则（SYNTAX §13.2 已同步）；P4a
LoweredIndexExpression 恒等降级；P4b §13.6 get.array/set.array
发射 + BilVerifier 严格三元组查询（§6.4 精确匹配，禁止隐式转换）。
详见「里程碑历史」M59 段落。

**S8d（M60）已完成**：重载解析 + 默认参数 + 具名参数三件套落地
（纯 P3）——SYNTAX §4.2 规则定稿（结构过滤 → 类型适用性 → 最具体
胜出 + 默认值填充数平局打破；实例/ext 同池；泛型/可变参数归口）；
P3 新设施 `OverloadResolution`（source-level ranking 唯一落点，
BIL §3.3）统一承载调用/init/索引读三处候选解析；默认参数声明点
绑定（BindContext.IsDefaultValueContext 隔离形参与 this）+ 调用点
规范序填充（BindEnvironment.ParameterDefaults 记忆化按需绑定，
前向依赖声明顺序无关）；同批修复位置实参静默覆盖具名占位。
详见「里程碑历史」M60 段落。

**S8f（M66）已完成**：castTo/castFrom 名字分析 + async 边界五项
闸门（纯 P3 步，S8 收官）——P1 `MethodSymbol.IsAsync`；P2 新
`ConversionOperatorChecker`（castTo/castFrom 声明形状）+ 新
`AsyncGateChecker`（声明侧闸门 2/3/5 + async 仅函数收口）；P3
`BoundCastExpression.Conversion` 槽 + `FindConversionOperator` 三级
转换优先级（源 castTo → 目标 castFrom → 内建）+ `Binding/AsyncGates.cs`
调用点闸门 1/2 + `LambdaVisitor` 闸门 4（async lambda 捕获扫描）。
详见「里程碑历史」M66 段落。

**下一步**：**S11g 已收口**（M88 会话）——#26 代码落地 + M88 架构反转
（烘焙归 Middleware）+ #27⑧/M79 param:W 落地；M89 已收口 **#28③④**。
M90 已按用户裁决收口 **#27⑦**（可变泛型包以 `.generic.<Pack>` 在
`invoke fn(..inner)` 值包前显式透传）。M91 已收口 M84 的字段-Value 调用/索引读
与深层纯字段写穿；局部/静态 wrapper 存储按用户裁决继续显式归口（与 init
实参 BIL 承载同批设计），字段-Value 索引写保持拒绝。M92 已收口
**#28①**（显式泛型实参进入降级请求 symbol）；
**#28②** 维持规范既定行为（getter/setter/operator 未声明请求不降级，类别
路由体归 Middleware）。局部访问器用户决策
（2026-08-06）走路线 C 随 S13 lambda 闭包机制落地，移出 S11 序列
（技术债 #22①）。M97–M101 已完成 S13 `await`、`yield`、using 两种形态与普通
lambda P3 Slice A：
Task/Alarm/IDisposable 绑定、挂起点收窄失效、BIL §17 发射与 nested try/finally
verifier 校验均已落地；动态/可挂起 dispose 清理、复杂 value-block continuation、
lambda P4 closure environment/cell、局部访问器和 Middleware 状态机仍未实现。**后续 ROADMAP 大方向**
（见 `SEMANTIC_ROADMAP.md`）：S13 动态/可挂起 dispose 清理、复杂 value-block
continuation、lambda closure P4 与 closure / Middleware 协程 runtime /
S14 VM 验收与 runtime 面。
S11 全序列已落地：BIL 定稿（M75）、enum
case（M77）、wrapper place（M79）、ext 收尾（M80）、proxy 细化与
#26 裁决（M81 文档）、S11a–S11f（M82–M87）、架构反转 M88。前端
进入维护状态，仅在中端暴露缺口时回补。

---

## 6. 技术债务与已知限制

1. ~~实参位置的 `a.b` 存在 MemberAccess/Symbol 双形态~~（M42 已消除：表达式路径统一为 PathExpressionASTNode，语义上色归 P3 单点）
2. `3.`/`3.foo` 在 M31 起为编译错误（点后缺数字；`3.foo` 形态规范未定义，需要成员访问时请写 `(3).foo`）
3. ~~值块取值规则（M33 起解析层无特判）留待语义校验~~（M46 已落地 if 表达式值块：M33 隐式取值判定、显式 return@ 全路径检查（GuaranteesValueReturn）、产值类型统一；M49 已落地 switch 表达式分支体的同类校验；M50 已落地 seq 表达式的同类校验——复用 BindValueBlock + 必须产值检查）
4. ~~复合赋值的语义推导留待语义/后端阶段~~（M46 已落地：P3 绑定 BoundCompoundAssignmentExpression（读语义 unassigned 检查 + intrinsic 检查）、P4a 脱糖为前置赋值，表达式值为写回后值）；M34 起解析层接受全部 10 个运算符不变
5. `.name` 保留参数名（M34 起解析层接受，Name 原样存 `.name`）的上下文约束（仅 method wrapper canonical 形态可用）与语义规范化留待语义阶段
7. 编译 0 警告（大扫除消除了原 `Core/Utilities.cs` 的 nullable 警告）
8. BIL 待补（M35 登记，不属前端）：wrapper 改为 rich struct 后，`BIL_STANDARD.md` §12.4 缺**只读 place 的接收者形态**；async 协程 §17 已由 M97 专项定稿并实现 `await` 首切片，`yield`/可挂起清理仍归 S13。两项均记于 `compiler/semantic/SEMANTIC_ARCHITECTURE.md` §7/§7.1，wrapper 形态与 S11/Middleware 边界、协程剩余形态归后续专项
9. P2 推断规则（M40 登记，规范未明写）：wrapper 缺 `@WrapperTarget` 即诊断（规范只定义了三类目标的标注形态）；init 映射 `_ -> field` 的目标字段无类型标注即诊断（沿字段类型无从谈起）。若后续规范给出默认行为，回到 DeclarationResolver 放宽
10. P2 边界（M40 登记）：无类型标注字段（`var x = expr`）的类型推断归 P3，其闭包/闸门判定需在 P3 补一轮复核；§14.9 矩阵 C 行（栈上局部变量的 Value wrapper 检查）归 P3；P1 文本级方法签名重复判定的签名级精确化（类型解析后判定真正重载冲突）留待后续里程碑
11. P3 边界（M41 登记，S5 最小闭环的已知留口）：~~无 init 零参 `new` 按「默认构造」放行（规范未明写默认构造规则）~~（M60 规范定稿 §9.3：未声明显式 init 隐含零参公有默认构造，字段取初始化器或零值——实现行为即规范语义；字段初始化器并入「全局字段初始化器」既有留口）；~~全局字段作赋值目标的 const 判定缺「符号 → 声明 AST」反向映射~~（M56 已兑现：FieldSymbol.IsConst + init 豁免检查）；~~有默认值的形参在缺失时报 Missing argument（默认参数填充归 S8）~~（M60 已兑现：声明点绑定 + 调用点规范序填充）；局部变量遮蔽参数/外层变量按放行处理（规范未明）；IsAssignable 的 interface 判定只看直接实现（接口继承链递归与数值提升规则待规范明确后收紧）
12. P4 边界（M44 登记，S6 最小闭环的已知留口）：~~实例方法（需 `.this` receiver，BIL §7.3）发射报 P4 Error 跳过（归 S8）~~（M48 S7c-2 已开闸 .this/实例 invoke，M59 S8c 补齐 §13.6 get.array/set.array 索引发射）；~~`Kind != Regular` 的方法成员（init/operator/getter/setter）符号段声明报 P4 Error 跳过（归 S8/S11）~~（init/operator §8.4 声明形态 M48 已开闸；getter/setter M63 已开闸——§8.4 getter(FIELD)/setter(FIELD) 由字段槽驱动发射）；~~Resources 只提取 string 与整数字面量~~（§19.1 标量全形态已落地，M45；复合资源随需要增补）
13. P3/P4 边界（M46 登记，S7b 的已知留口）：① IntrinsicOpcode（M57 起为 MapBinaryOp/MapUnaryOp）的 And/Or 直接发射表项保留——仅 and/or 被用户重载的不短路场景合法（S8+），内建 bool 短路已走 P4a 展开；② 值块穿透 return@ 的 GuaranteesValueReturn 按「路径终止」处理（更精细的路径类型分析留待后续）；③ 值块 if 转换双终止丢弃语句中的 LocalSymbol 仍全量平铺进 .vars（无害，verifier 阶段再核）
14. P3/P4 边界（M47 登记，S7c-1 的已知留口）：① return@ 隔循环边界拦截为诊断（当前脱糖只写值块局部、无法表达跳出中间循环；若规范另有意图如自动生成 break 链，回到 Binder 放宽——拦截点在 BindReturn 的 LoopDepth 比较）；② definite assignment 对 break/continue 后的同块语句不做流处理（按顺序继续绑定，不截断不改 assigned；不精确方向为保守，do-while 体尾集合可能多算 break 后的赋值）；③ GuaranteesReturn 对循环保守 false（`while (true)` 无 break 恒循环特例留口）；④ do-while 条件内的赋值效果（条件表达式含复合赋值时）保守丢弃——循环后状态 = 体尾集合，不含条件求值效果
15. P3/P4 边界（M48 登记，S7c-2 的已知留口）：① ~~循环变量 const 为只读默认（规范未明）~~（M60 规范定稿 §7.3：一律 const——实现即规范语义）；② init `_ -> field` 映射 P3 不落隐式赋值（stdlib 以显式赋值 init 规避，映射语义化留待后续——BindBody 对映射参数无处理）；③ ~~接口方法 override 匹配校验与访问控制（priv/protected/internal）检查归 S8（命中即放行；override 修饰符不进 BIL 声明——符号模型无标记位，§8.4 修饰符为可选集）~~（M63 已落地：OverrideChecker 签名匹配（构造宿主 Substitute 代入）+ AccessChecker 全使用点检查 + MethodSymbol 三标记 + override/abstract 投影进 BIL §8.4）；④ ~~其余数值类型（i8–u64/float/double）的 EnumerateInRange 与泛型 RangeEnumerator\<T\> 留 S9/后续（stdlib 当前仅 i32）~~（M73 已落地：RangeEnumerator\<T\> 泛型抽象基类进 collections（协议级状态机骨架 + abstract moveNext），RangeEnumeratorI32 继承实现——比较/步进留在具体类型上下文；其余数值类型的具体枚举器随需要扩充）；⑤ `?.`（SafeDot）与 wrapper `:`（Colon）段在实例链上色中仍拦截（归 S7f/S11）；⑥ 实例字段 const 赋值判定同全局字段技术债（符号 → 声明 AST 反向映射缺失）
16. P3/P4 边界（M49 登记，S7d 的已知留口）：① 混合 switch（常量与 pattern case 共存）整体走 pattern 链降级（§16.6 允许「降为 if 或多个结构化判断」；纯常量形态才用 switch-table——「常量段用表 + pattern 段用链」的混合 lowering 留待需要时优化，分流点在 LowerSwitchCore 的 Any(IsPattern)）；② 内建非基元类型 core::Exception 经 new/类型引用投影进 BIL 文本但不进 LocalSymbols（内建不声明是 M38 架构决策——基元靠别名投影闭合，Exception 是首个被引用的内建 class；声明策略归 verifier（S12）前定稿——**M58 已落定**：LocalSymbols 仍不声明，可解析性由 BilVerifier 预定义符号表闭合（bootstrap 符号即 BIL 内建环境））
17. P3/P4 边界（M50 登记，S7e 的已知留口）：① try+finally 部分终止的值块编织拦截为 P4 Error（TransformWithContinuation 穿过 try-finally 且 rest 非空——完整支持需 finally 复制编织，留待需要时落地，拦截点在 WeaveContinuation 的 try 分支；M61 起 return@语句seq 同路径拦截）；② ~~语句 seq 不压值块标签栈——return@ 指向语句 seq 报未定义标签~~（M60 规范定稿 §6.1 + M61 落地：BoundSeqExitStatement + SeqLabels 标签栈（named 专属、隔循环/隔值块拦截）+ LoweredSeqExitStatement 标记 + TransformStatements 消费/传播编织）；③ ~~catch 变量与 finally(e) 变量只读默认 const（规范未明）~~（M60 规范定稿 §8：一律 const——实现即规范语义）；④ ~~core.CastException 未进 stdlib（as 失败是运行时 CastException，P3 对可转性不做静态拒绝；具体异常子类归 S10，与 M49 异常根注记同源）~~（M74 已落地：CastException/NoSuchMethodException/IOException/RuntimeException 进 `stdlib/core/exceptions.latte`，`: core.Exception` + 自持 init，SYNTAX §8.1）
18. P3 边界（M54 登记，S8a 的已知留口）：① typeOf 的多段类型形态（`typeOf(ns.Type)`）未支持——仅单段裸名操作数做值/类型不落袋分类，多段路径按值形态直通（报 cannot be used as a value；多段类型支持留待需要时落地，分类点在 BindTypeOf 的裸名判定）；② ~~is/supers/with 右侧动态形态的值路径带泛型实参按未命中处理~~（M73 已勾销 #18②：值路径元素带泛型实参时实参先经 NameResolver 静默解析（M69 使用侧泛型已落地），成功即正常绑定值——值路径无类型实参消费点，实参不参与绑定；失败仍落统一目标诊断）；③ typeOf 非裸名操作数一律值形态（`typeOf((expr))` 经透明分组等同值形态，符合 spec 先值后类型口径）
19. M55 架构重构备注：① 协议 v2 修正——绑定遍历的 `scope`（词法环境）与 `expectedType`（期望类型）是 CRTP 基类 v1 签名遗漏的固有下传参数，定稿三基类承载（VISITOR_REWRITE §3）；② Lowerer 多趟 rewriter 链（ARCH §6.1 远期愿景）记为演进方向——趟间契约需重定义，S8 收官或 S13 时评估；③ context 方言接口初期从粗（BindContext + IFlowContext 一角），随 S8b 收窄表落地按需 extract 切细；④ 旧 LowerSession 的 SafeReceivers 手工压弹无 finally 保护（深层异常时栈泄漏），visitor 化时已修固为 try/finally；⑤ ~~`x == null`/`x != null` 无法绑定~~（M56 已落地：BinaryVisitor null 判等特例 + 装箱 cast §12.1）
20. P3/P4 边界（M59 登记，S8c 的已知留口）：① ~~索引复合赋值 receiver/index 多重求值~~（M60 规范定稿 §13.2 单次求值（含字段——同批用户决策）+ M61 落地：副作用目标物化 .sN、纯读取 IsSideEffectFree 直通零物化）；② 容器中间段带后缀（`ns.Foo().bar` 形态）仍未支持（保留 S8 归口诊断）；③ 值调用（`local(0)(1)` 等函数值调用）未支持（Call-on-value 报 "P3: calling a value is not supported yet (S8)"）；④ 读索引要求 getAtIndex、写索引要求 setAtIndex（只读/只写索引器按各自存在性检查）；⑤ `(a+b).c` 端到端未覆盖——语言尚无用户二元运算符重载（P3 未落地），链式形态已由 `cb[0].value`、`makeBag()[9]` 覆盖
21. P3 边界（M60 登记，S8d 的已知留口）：① 写模式索引 operator 重载仍归口（RHS 类型在赋值侧才可知，BindIndexAccess 写路径无 ranking——若需要，把 RHS 绑定前移至 place 折叠或做两段式解析）；② 默认值表达式不含局部声明（值块/lambda 内 `var` 归口诊断——P4 无法物化跨函数局部，规范 §4.2 已明写 M60 限制）；③ 可变参数（`i32...`/`named String...`）方法不参与调用绑定（归口诊断，调用绑定归后续里程碑）；④ 具名实参求值序为规范参数序（形参声明序）而非源码序（与 M41 起 BindArguments 既有行为一致——SYNTAX §4.2 已明写）；⑤ 多候选路径实参无目标类型预绑（expectedType 仅 null 字面量消费——若后续引入 lambda 实参目标类型推断，重载解析需先按结构过滤再逐候选定型）；⑥ 默认值依赖环（`f(a = g())`/`g(x = f())`）经 in-flight 集合保守拦截，诊断措辞为级联 Missing argument 而非「cyclic default value」专用款（fuzz 观察记录；行为正确不崩溃，措辞待需要时专项化）
22. P3/P4 边界（M63 登记，S8e 的已知留口）：① 栈上局部 var/const 访问器归 S11（自洽实现需闭包抬升或内联展开，超 S8e 体量；P3 归口诊断后按普通局部降级绑定不中断）——**M80 后用户决策（2026-08-06）：实现路线定为 C，随 S13 lambda 闭包机制落地（闭包抬升，捕获语义开放），不做内联展开，移出 S11 序列——M83 起 HANDOVER.md 按约定删除，本注记为决策承载**；② 访问器体内 value 别名的两处边角：CallForm 首段多段调用（`value.x()`）与 getter 体内复合赋值（`value += 1`）不拦截（主形态普通赋值已拦 `Cannot assign to 'value' in a getter`）；③ 命名空间全局字段的 `.static.` canonical 形态既有 gap（声明 `.field` 无 static 标记 vs 指令 get.field.static——M63 前已存在，根治牵动 P1/P3 黄金，单独立项）；④ ~~ext 字段 + 访问器组合发射路径已通（访问器随 AttachToExtTarget 随迁）但无端到端样例（stdlib 无此用法）~~（M80 已勾销：BilEmitterTests.Ext 新 partial 五组端到端样例——实例字段读写/方法调用/backing 访问器/内建 computed 访问器/static 三形态/复合赋值；同批修复内建 ext 访问器声明缺失的 P4b 缺口）；⑤ 带访问器字段必须显式类型标注（字段类型推断与访问器不共存，P2 诊断——若规范另定默认行为，回 AccessorChecker 放宽）；⑥ ~~构造宿主覆写签名比对的泛型精确性（约束匹配等）归 S9~~（M73 已勾销 #22⑥：OverrideChecker 签名比较改同构判定——两侧泛型参数按各自方法声明序对应（覆写 V 与基类 U 是同构的不同符号），嵌套构造递归逐实参；接口闭包沿宿主链 Substitute 构造实参（接口声明在泛型基类上）——`RangeEnumerator\<T\> : IEnumerator\<T\>` 继承到 `RangeEnumeratorI32` 后按 `IEnumerator\<i32\>` 比对）
23. P3 边界（M66 登记，S8f 的已知留口）：① castTo/castFrom 名字分析只做适用判定与记录（BoundCastExpression.Conversion），不重写调用——P4 仍发 `cast`，转换经 BIL §12.1 语义运行时自行分派（S14 VM 验收）；as? + 用户转换的运行时语义（castTo 抛异常 → as? 产 null？）待 S14 定稿；② castFrom 的调用形态未定稿（声明在目标类型上的实例 operator 但转换时无目标实例——receiver 槽语义归 lowering/运行时定稿，名字分析不承诺）；③ ~~转换运算符的多泛型参数/宿主泛型参数形态按不适用回退内建~~（M73 已勾销 #23③：复核确认回退正确——多泛型参数无法静态代入、宿主泛型参数无构造实参可代，回退 BIL §12.1 第 3 条内建兜底；测试固化两类形态）；④ ~~async 闸门 5 的调用点实际实参检查归 S9~~（M73 已勾销 #23④：AsyncGates.CheckAsyncCall 增闸门 5——BoundCall 携带的泛型实参逐项查共享安全（M69 后使用侧泛型落地，泛型参数自身/ErrorType 毒化跳过），测试覆盖违反/合法两例）；⑤ async lambda 捕获扫描为 AST 级粗粒度（体内局部声明名全量排除——块内「先引用、后声明」形态漏判捕获，保守漏报方向安全；lambda 绑定归 S13 后收窄为符号级）；⑥ async lambda 自身形参与返回类型的闸门未查（spec 检查点只列捕获；lambda 调用点检查随 S13）；⑦ 闸门 2 调用点检查为防御性兜底（shared 单向传染 ⇒ 可赋值即共享安全，静态不可达违反——保留作不变量防线）
24. S10/S13 边界（M74 登记，M100 更新）：① core.coroutine 运行时面的成员形状未定稿——Task 无成员（await 是运算符）、Executor 选择 API、CoroutineLocal get/set 与 PollingAlarm 之外的内建 Alarm 面全部随 S13 Middleware 专项定稿（当前仅类型面 + sleep/isReady）；② async 调用返回类型、`await Task`/`await Task<T>`、裸 `yield`/`yield Alarm` 与 using 语句/表达式形态 P3/P4 规则已落地；动态/可挂起 dispose、复杂 value-block continuation 的运行时语义仍由 S13/Middleware 汇合；③ native 返回用户引用类型的 FFI 参数/返回值 ABI 未定（归 Middleware，SYNTAX §4.6/RUNTIME §26）；④ getMessage 是 bootstrap 硬编码 native 方法（BIL 预定义符号表闭合），VM 实现归 S14；⑤ 异常根 message 字段为 protected（子类 init 赋值 + getMessage 读取），用户代码对根字段的直接读写不可见（§16.1 语义，非缺陷）
25. P3 边界（M78 登记）：① 混合泛型形态（`f\<T, TArgs...\>` 固定 + 可变混合）的显式实参个数按「泛型参数全列表」匹配（需 2 个显式实参），与 SYNTAX §4.2「实参个数必须与泛型参数列表一致（泛型可变参数除外）」字面有出入——spec 口径应 = 固定参数个数；M78 的全可变包候选拦截不误伤该形态，行为修正归后续里程碑（落点在 OverloadResolution 显式路径 matching 过滤）；② BilVerifier TypesCompatible 的协变（in/out 泛型 variance）已由 M96 补齐，BIL `.type generic(...)` 保留方向并由调用签名检查消费
26. ~~ext 边界（M80 登记，ext 收尾的规范未明三事）~~（**M81 已裁决勾销**，规范文本同批落地）：① ext static 合法——SYNTAX §4.4 明文补例（追认 M80 实现与端到端样例）；② priv/protected ext 可见性**按声明位置**判定（顶层 ext 适用 §16.1 顶层规则，private = 仅声明文件），ext 方法/访问器体不放开目标私有成员访问——§4.4/§16.1 成文；③ ext 泛型目标禁止静默接受——裸名命中泛型定义报元数诊断、同名不同元数报歧义，「隐式获得目标泛型参数」留作语言候补。~~代码落地（② AccessChecker/BindFunctionFrame 容器判定修订 + ③ ResolveDottedPath 元数/歧义诊断）归 ROADMAP S11g~~（**M88 已代码落地**：NameResolver.ResolveDottedPath 收口裸名泛型元数/同名不同元数歧义诊断；AccessChecker 对 ExtTargetPath 成员走顶层规则；ModifierChecker 禁 ext protected；9 新用例）
27. S11a 边界（M82 登记，proxy 烘焙的暂缓面）：：① Value/Method wrapper 链（`.proxy.get`/`.proxy.set`/`.proxy.call` 拦截）未计算——Value/Method 烘焙归后续里程碑（「只实现 get 只适用于只读变量」的适用性判定随链计算落地）；② 无访问器字段的 get/set 拦截跳过（需自动访问器合成 + P4 接线，归 S11c/S11d 复核）；③ interface 实现者的链继承与 override 覆写链未定稿（骑 vtable 语义下 override 是否继承烘焙待裁决）；④ async 成员拦截与 S13 async lowering 的交互未定稿；⑤ 全局/静态目标的 wrapper 存储（全局隐藏静态字段形态）未定稿（**M91 用户裁决继续归口**：局部 `.args/.vars` 尚无 `wrapped(W)`/init 实参载体，避免先锁定半套 ABI）；⑥ ~~proxy 声明体当前仍按普通 operator 体绑定（体内 `inner`/`self` 报 Undefined function/name）——逐组合绑定（语境：self=宿主角色 this、inner=下一环、this→wrapper place）归 S11b~~（**M83 已勾销**：阶段 2 分流收集 + 阶段 2.5 逐组合绑定三件套落地，非 proxy 语境 self/inner 改专门诊断）；~~⑦ 可变参数（vargs/kwargs）成员链缺 Bound 层包透传表达~~（**M90 已消解**：当前 proxy 方法的可变泛型包按声明序显式携带于 Bound/Lowered，P4b 前置为 `invoke fn(..inner) [$.generic.<Pack>..., 值实参...]`；specific/wildcard 可匹配可变值参数成员，BilVerifier 校验包前缀；解包/shim/烘焙仍归 Middleware）；~~⑧ proxy 声明泛型参数的体内类型引用代入（`var x: TReturn`/`as TField`）暂缓（M83 登记：未接线——典型 proxy 体（inner 转发/value 直通）不消费，需要时在 TypeReferences 解析路径加代入映射，归 S11g 复核）~~（**M88/#27⑧ 已勾销**：模板态下 proxy 声明泛型参数即绑定作用域符号，`var x: TReturn`/`as TField` 天然可解析（测试固化）；同批 M79 param:W——BindWrapperSegment 增第三源（泛型参数 with 约束 wrapper 匹配，只读禁令同 M79 族），BoundWrapperAccessExpression Application 槽适配约束推导应用）
28. S11e/`call???` 边界（M86 登记，M92 后仅 ② 为规范既定行为）：~~① 显式泛型实参的降级请求 symbol 不含泛型信息（`service.fetch\<i32>(x)` 降级照常但 symbol 串不带 `.generic.*` 段——胖值自描述 typeid 可运行时补足；M88 后 symbol 串由 frontend 在调用点物化，格式扩仍归专项）~~（**M92 已消解**：采用方案 A，在方法名后按显式实参书写序编码 `<.i32,.string>` canonical 类型段；P3 复用 `ResolveGenericArguments` 做使用点解析/访问检查，`Any.call???` 三参 ABI 不变，具体类型进入 symbol string 资源）；② getter/setter/operator 的未声明请求不降级（保持现有编译错误——SYNTAX §14.7 末条类别路由；M88 后 router fn 已删，类别路由体归 Middleware，frontend 仅资格判定 + invoke Any.call???——类别细分插入点改 Middleware）；~~③ interface 传染宿主的降级资格漏报~~（**M89 已消解**：`IsDowngradeEligible` 只读遍历 receiver/BaseType/Interfaces 传递闭包，定义级去重防环，不回写实现者应用槽）；~~④ 类型兼容豁免不含 `if?` 右操作数/throw 操作数/复合赋值与索引写值位置~~（**M89 已消解**：P3 仅放行直接 call??? 结果，P4a 在各目标位置按声明类型物化 §6.5 cast；索引写复用既有赋值路径）；⑤ ~~Any.call??? 体合成依赖 stdlib NoSuchMethodException~~（**M88 已消解**：call??? 改为 bootstrap 内建 + §22.5 VM hook（toString 先例），不再合成 throw 体，无 stdlib 依赖）

29. M88 边界登记（架构反转后 frontend 已知留口）：① proxy 模板 fn 的 BIL canonical 名走 `$$` operator 形态（wrapper 类型名 + `$$` + proxy 声明名——与旧 `.proxy.<序>.` 合成名不同，Middleware 按模板+应用标记烘焙）；② DispatchExplainer 瘦身后面覆盖面 = 应用登记 × 名命中预览 + 降级资格（不再枚举合成特化 fn/原始体/router 链——报告「Middleware 将烘焙的链」；调用点级过滤仍为预留）；③ 调用点 invoke 原名不改（被修饰成员仍以源名 invoke，链替换归 Middleware——与 M81① 骑 vtable 字面一致，但合成 fn 不再由 frontend 产出）；④ Value/Method wrapper 链与 #27①–⑤ 暂缓面在新架构下主体归 Middleware 烘焙，frontend 形状校验（ProxyShapeChecker）仍覆盖三类 proxy 声明；⑤ ~~M84 三项遗留全部保持归口~~（**M91 部分消解**：字段-Value 方法调用/索引读与深层纯字段写穿已落地；索引写因值拷贝无写回语义继续拒绝；局部/静态存储按用户裁决继续归口）

---

## 7. 里程碑历史

### 2026-08-08 · M102 S13 lambda P4 B0：无捕获函数句柄

- **P4a/P4b**：无捕获普通 lambda 现在进入 synthetic `BoundFunctionBody`/`LoweredFunctionBody`
  管线，局部值降级为强类型 `.methodid<(参数类型...)@返回类型>` 句柄；调用点复用
  lambda `MethodSymbol` 的参数绑定，发射 `getid.method` 与 `invoke.indirect`，void
  返回使用 `invoke.indirect.noret`。
- **BIL/verifier**：新增 `GetIdMethodInstruction`、`InvokeIndirectInstruction`、
  `InvokeIndirectNoResultInstruction` 和 `BilMethodIdType`，methodid canonical
  签名解析按参数/返回逐项校验，严格检查 indirect 调用参数个数、参数类型和有无
  返回的指令形态；synthetic lambda 方法声明进入 `LocalSymbols`，不绕过 §21.2。
- **边界**：捕获 lambda 仍在 `LambdaRewriter` 报明确 P4 pending；closure environment/
  cell、可变捕获、函数值比较、字段/静态存储、async lambda spawn 与局部访问器仍未实现。
- **测试**：Binder 验证 captured lambda 只在 P3 记录捕获；Lowerer 验证无捕获 lambda
  与 synthetic body、捕获 pending；BilEmitter 验证 synthetic declaration、methodid、
  `getid.method`、indirect invoke；BilVerifier 验证 void/non-void methodid 与负例。
  最终 3441/3441 + Lexer fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告。
- **下一步**：S13 closure environment/cell，先按值捕获再处理可变 `var` cell。

### 2026-08-08 · M101 S13 普通 lambda P3 Slice A

- **匿名 callable 产物**：新增不进入用户符号图的 `LambdaTypeSymbol` 与
  `BoundLambdaExpression`；lambda 参数、返回类型、单表达式体以及多语句显式
  `return@_`/named 体均在 P3 绑定，块体不再错误地隐式取值。
- **隔离与捕获**：lambda 使用独立 `Scope/BindContext`，入口只继承外层已赋值
  DA，不继承 smart-cast，lambda 内声明不会污染外层流状态；捕获按符号身份记录
  `LocalSymbol`、`ParameterSymbol` 与 `this`，嵌套 lambda 的传递捕获向外累积。
- **async 闸门**：async lambda 复用符号级捕获检查，并补形参与返回类型共享安全
  检查；async lambda 的 eager spawn、closure environment 和 P4 ABI 不在本切片。
- **P4 边界**：BoundLambda 在 Lowerer 侧保持明确的 closure pending 诊断，不生成
  半套闭包 BIL 或伪造合成类型声明；局部访问器 closure cell 同样后置。
- **测试**：Binder 覆盖匿名 callable 类型不入用户图、DA、单/块体、捕获符号、
  嵌套传递捕获、未定义名、返回类型、显式返回与 async 闸门。最终 3429/3429 +
  Lexer fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告。
- **下一步**：S13 P4 closure environment/cell 与 lambda invoke lowering。

### 2026-08-08 · M100 S13 `using` 表达式形态切片

- **P3**：`BoundSeqExpression` 携带 `BoundUsingBinding` 列表；表达式 using 与
  语句 using 共用资源作用域、IDisposable 兼容性、无参 dispose 解析、只读资源槽
  和 async/open/abstract dispose 保守边界。using 初始化器的显式类型标注保留
  普通局部声明的降级调用结果豁免。
- **P4a**：先完成值块结果局部的正常写入，再将完整值块作为 protected body
  包入按源码资源序初始化、按逆序 dispose 的 nested try/finally；finally 不改
  结果局部，call blk 返回后读取结果局部。P4b/BIL/verifier 复用既有 try/invoke，
  并修正无 catch try/finally 的 DA 正常出口传播。
- **边界**：表达式 using 穿越外层 value-block continuation 且后续仍有 continuation
  时，保留 S7e 的 P4 拦截，避免生成错误控制流；动态/可挂起 dispose 清理游标仍归
  Middleware/S13 后续。
- **测试**：Binder、Lowerer、BilEmitter、BilVerifier 增加表达式 using 的结果局部、
  多资源顺序/逆序、BIL `.vars`/`ret`、类型和 dispose 负例及复杂 continuation
  拦截覆盖。最终 3415/3415 + Lexer fuzz 6000 + 语义 fuzz 3000 全绿，build 0
  错误 0 警告。
- **下一步**：S13 普通 lambda 闭包与局部访问器 closure cell。

### 2026-08-08 · M99 S13 `using` 语句形态切片

- **P3 绑定**：开放语句形态 `seq using(...)`，逐绑定按源码顺序建立资源局部；
  初始化器可引用前序资源，资源类型必须兼容 `core.IDisposable`，并把无参
  `dispose` 的 `MethodSymbol` 固化在 `BoundUsingBinding`。表达式形态仍以明确
  的 S13 pending 诊断拒绝。
- **资源安全边界**：using 资源槽即使源代码写 `var` 也禁止普通/复合重赋值，避免
  finally dispose 错误对象；async/open/abstract dispose 保守拒绝，普通 dispose
  体内的 await/yield 仍可在当前 Coroutine 中挂起。
- **P4a**：按资源源码序生成声明，再从最后一个资源向前包裹嵌套
  `LoweredTryStatement`；每层 finally 只 dispose 已成功建立的资源，天然实现
  初始化异常前缀门控与逆序清理。P4b/BIL/verifier 复用现有 try/invoke，无新增
  清理 opcode。
- **测试**：Binder 覆盖资源绑定、顺序、IDisposable 负例、重赋值和 dispose 边界；
  Lowerer/BilEmitter 覆盖单/多资源嵌套 try/finally、dispose 逆序、body 内 await/yield
  与验证器合法性。最终 3394/3394 + Lexer fuzz 6000 + 语义 fuzz 3000 全绿，build
  0 错误 0 警告。
- **下一步**：S13 表达式形态 using 与可挂起/动态 dispose 的完整 lowering。

### 2026-08-08 · M98 S13 `yield` 垂直切片

- **P3**：新增 `BoundYieldStatement` 与 `YieldVisitor`；裸 `yield` 零操作数，
  `yield ALARM` 接受可赋值到 `core.coroutine.PollingAlarm` 或 `EventAlarm` 的
  值，含用户子类与带 `extends` 约束的泛型参数。普通函数、async 函数与
  `main` 均可挂起，不新增 async-only 闸门。
- **流分析**：yield 后复用 `FlowState.ClearNarrowed` 清除 smart-cast，DA 集合
  保持不变；循环、try/finally 和 seq 中均按非终止语句透传。同期补全
  `ConditionFacts.ContainsAwait` 对索引、字段、类型检查、安全访问、值块等
  子表达式的递归，避免 guard 内嵌 await 恢复挂起前收窄。
- **P4/BIL**：新增 `LoweredYieldStatement`、`YieldRewriter`、`YieldEmitter` 与
  `YieldInstruction`，发射 `yield [ALARM]`；Alarm 表达式先物化，裸 yield
  无读写操作数。
- **验证器**：§21.3 按模块继承图复核 Alarm 子类兼容，声明不完整时遵守既有
  防误报降级，明确标量仍拒绝；§21.4 复用变量读前已赋值检查。
- **测试**：Binder/BilVerifier/BilEmitter/Lowerer 新增 22 个确定性用例，覆盖
  裸 yield、sleep/EventAlarm、Polling/Event 子类、泛型约束、非 Alarm、未赋值
  Alarm、跨挂起点收窄、循环、try/finally 与端到端合法 BIL。最终 3371/3371 +
  Lexer fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告。
- **下一步**：S13 `using` 可挂起逆序清理。

### 2026-08-08 · M97 S13 `await` 首个垂直切片

> S13 专项设计先行，确定 BIL §17 保留强类型 `await`/`yield` 指令；async
> `invoke` 保持 eager spawn，Middleware 负责状态机、continuation、waiter
> 和 GC ownership fence，frontend 不伪造私有 runtime ABI。

- **设计与规范**：新增 `docs/compiler/semantic/ASYNC_LOWERING_DESIGN.md`，定稿
  状态机切分、continuation 内容、Task eager spawn、await waiter 原子登记、yield
  调度、using 可挂起清理、closure environment 与局部访问器路线 C；同步修订
  `BIL_STANDARD.md` §17 与 `SEMANTIC_ARCHITECTURE.md` §7，并更新 ROADMAP S13。
- **P3**：新增 `BoundAwaitExpression`；只接受精确
  `core.coroutine.Task`/`Task<T>`，`Task<T>` await 产 `T`，无结果 `Task` 仅允许
  表达式语句。await 之后清除 smart-cast 收窄但保留 definite assignment；值块、
  guard、括号透明分组等边界均有明确诊断或回归覆盖。
- **P4/BIL**：新增 `LoweredAwaitExpression` 与 `AwaitInstruction`，发射严格的
  `await TASK [RESULT]`；BilVerifier 增补 §21.3 Task/结果类型检查及普通读写 DA
  分类。验证器测试覆盖 `Task`、`Task<T>` 正例，以及缺结果、错误结果、非 Task 负例。
- **测试**：新增 Binder、Lowerer、BilEmitter、BilVerifier 回归；3349 个确定性用例、
  Lexer fuzz 6000、语义 fuzz 3000 全绿，`dotnet build` 0 错误 0 警告。
- **遗留**：`yield`、`using` 可挂起清理、普通 lambda 闭包、局部访问器 closure cell、
  async lambda 和 Middleware 状态机仍归 S13 后续切片。

### 2026-08-07 · M92 `call???` 技术债 #28① 收口

> 用户确认采用方案 A：显式泛型实参进入降级请求 symbol 的方法名段，
> 格式为 `Host$method<.i32,.string>(参数段)@.any`。无显式泛型实参的
> 既有 symbol 逐字不变。

- **P3 接线**：降级分支复用 `ResolveGenericArguments`，按普通显式泛型
  调用同口径执行类型引用解析与使用点访问控制；解析失败落既有诊断并停止
  当前调用。成功结果只供请求 symbol 使用，不写入 `CallBinding.TypeArguments`
  （`Any.call???` 仍是固定三参胖值 ABI，避免错误发射隐藏泛型操作数）。
- **canonical symbol**：`CanonicalSymbolPrinter.PrintDowngradeRequest`
  新增类型实参列表，按源码顺序用 `PrintTypeReference` 编码为方法名后的
  `<...>` 段；参数段、`.any` 返回段与宿主定义级 canonical 名保持原规则。
- **P4/运行时契约**：symbol 继续作为 string 资源传入 `Any.call???`；BIL
  invoke 形态与验证器预定义签名不变，泛型信息由 symbol 字符串供
  Middleware 路由消费。
- **测试**：canonical printer、Binder 显式泛型降级、BIL string resource
  黄金与三参 invoke 形状回归；3271 个确定性用例、Lexer fuzz 6000、语义
  fuzz 3000 全绿，build 0 错误 0 警告。

### 2026-08-07 · M91 M84 wrapper place 遗留裁决与两项解归口

> 用户裁决：字段-Value 调用/索引读允许取 wrapper 值拷贝；深层字段写穿
> 不新增一条专用字节码，改由多个 get/set 嵌套展开；局部/静态存储维持
> 归口，等待应用 init 实参与变量标记 ABI 一并定稿。

- **字段-Value receiver**：新增 BIL `get.wrapper.field OBJECT
  field(HOST_FIELD) type(W) RESULT` 与 `LoweredGetFieldWrapperExpression`；
  方法调用和索引读物化值拷贝后复用普通 `invoke` / `get.array`，索引写因
  无原地写回语义继续诊断。BilVerifier 校验 W 为 wrapper、HOST_FIELD 为
  带 `wrapped(W)` 的实例字段、OBJECT 可赋值到 owner 且 RESULT = W。
- **字段应用寻址消歧**：复用 §13.3 已有 `field(F)|wrapper(W)` 两态，把
  字段-Value 应用编码为相邻 `field(HOST_FIELD), wrapper(W)`；同一 owner
  上多个字段应用同一 W 仍能精确寻址。`LoweredWrapperFieldExpression`
  的链升级为 FieldSymbol/TypeSymbol 顺序链，verifier 按位置类型、字段应用
  标记与最终成员 owner 连续验证；没有新增 wrapper chain operand/opcode。
- **深层纯字段写穿**：`place.a.b... = rhs` 与复合赋值先正向逐字段 get 并
  物化，随后求值 RHS、写叶，再从内向外仅对值类型中间层 set 写回，遇引用
  中间层停止；最外层必要写回复用现有 `set.wrapper.field`。const、无 setter、
  不可见或运行时类别未知的非具体中间类型明确诊断；含索引/调用的深写仍拒绝。
- **共享写路径宿主稳定性**：`MaterializeSharedWriteHost` 统一深写/深层
  复合/直接 wrapper 字段复合三处——仅局部/参数引用与 `this` 直通；可变
  普通字段（即便无 getter）、字段引用、调用、索引等一律 RHS 前物化合成
  局部，避免 RHS 替换宿主字段后 set.wrapper.field 写回落到不同对象。一般
  `CompoundAssignmentRewriter` 的 `IsSideEffectFree` 全局策略不变。
- **局部/静态边界**：继续 P4 诊断；frontend 不在 `.args/.vars` 尚无
  `wrapped(W)` 与 init 实参载体时锁定半套 ABI，存储合成仍归 Middleware。
- **规范**：SYNTAX §14.5、RUNTIME §14、BIL §12.4/§13.3/§21.3 与 ARCH
  §6.1/§7.1 同步值拷贝、字段应用寻址和多 get/set 写回契约。
- **测试 +59**（3208 → 3267）：BilWriter +1、BilVerifier +8、Binder +8、
  BilEmitter +42；覆盖字段-Value 调用/索引读、索引写归口、同 owner 双字段
  同 W、值/引用混合边界、一/两层值写回、深层复合赋值、求值序、const/
  无 setter、共享宿主稳定性与 verifier 指令/链正负例。最终 44 套件 3267/3267 + Lexer fuzz
  6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告。
- 下一步：技术债 #28①（显式泛型实参进入降级请求 symbol）。

### 2026-08-07 · M90 #27⑦ 可变参数成员链显式包透传

> 用户裁决：`invoke fn(..inner)` 沿用现有 `[ARGS]`，不增加源语法或独立 BIL
> 操作数段；可变泛型包以 `.generic.<Pack>` 按声明序显式前置，随后才是
> 源码 `inner(...)` 的值实参（含 `.kwargs.*` / `.vargs.*` 整包引用）。

- **P3/P4a 数据契约**：`CallBinding`、`BoundInnerCallExpression` 与
  `LoweredCallInnerExpression` 新增 `ForwardedGenericPacks`，只收当前 proxy
  方法的可变泛型参数（固定泛型不入列），并由 Path/Declaration 两个
  inner 创建点完整透传；BoundDescribe/LoweredDescribe 同步可观察。
- **P4b/BIL**：`CallInnerEmitter` 复用 `MaterializeTypeId` 把包符号映射为
  `$.generic.<Name>` 零指令引用，前置于现有值实参。标准 wildcard 模板
  产物为 `invoke fn(..inner) $.t0 [$.generic.TNamedArgs, $.generic.TUnnamedArgs,
  $.kwargs.namedArgs, $.vargs.unnamedArgs]`；源码 `inner(...)` 不变。
- **成员匹配**：`ProxyMatchChecker` 不再跳过可变值参数成员；specific
  shape 比较新增 `IsVariadic` / `IsNamedVariadic` 同形约束，泛型成员仍按
  既有规则落 wildcard。包解包、shim 与特化链接全部保留在 Middleware。
- **验证器**：§21.3 对 `invoke fn(..inner)(.noret)` 校验可变泛型包前缀必须与
  当前 fn `.args` 中的包声明完整、同序、同型；固定 `.generic.T` 不得混入，
  未声明变量仍由统一变量引用检查报告。
- **规范**：SYNTAX §14.6、RUNTIME §14、BIL §15.4/§15.6、ARCH §5.2
  同步显式包契约与 frontend/Middleware 边界。
- **测试 +25**（3183 → 3208）：Binder +11、DeclarationResolver +4、
  BilEmitter +4、BilVerifier +2、DispatchExplainer +4；覆盖包声明序、固定
  泛型排除、specific variadic 正/负与端到端、wildcard 可变/泛型成员预览、
  未声明/错序 generic 包拒绝。最终 44 套件 3208/3208 + Lexer fuzz 6000 +
  语义 fuzz 3000 全绿，build 0 错误 0 警告。
- 下一步：M84 三项专项；其语义未定部分先行用户裁决。

### 2026-08-07 · M89 `call???` 技术债 #28③④ 收口

- **#28③ interface 传染资格**：`ProxyMatching.IsDowngradeEligible` 从仅查
  receiver 的 BaseType 链，扩展为 receiver + 每层 Interfaces 传递闭包；
  构造形态回退定义级，`HashSet<TypeSymbol>` 定义级去重并防接口环。查询
  只读 `AppliedWrappers`，不向实现者回写应用记录，保持 M88「frontend
  携带标记、Middleware 烘焙」边界。
- **#28④ 类型兼容与物化**：P3 的 if? 右操作数、throw 操作数、复合赋值
  RHS 增 `BoundAnalysis.IsDowngradeCallResult` 豁免（索引写已走普通赋值
  同一豁免）；P4a 的 if? else 分支、throw、一般/Wrapper place 复合赋值
  与索引写路径经 `EnsureDeclaredType` 物化目标类型 cast。普通 Any 不豁免，
  throw 仅对 bootstrap `Any.call???` 结果新增 cast，既有异常子类型形态不变。
- **测试 +33**（3150 → 3183）：Binder +12（interface 直接/传递闭包/
  specific 负例、四位置豁免与普通 Any 负例）；BilEmitter +21（interface
  标记不回写 + invoke call???、四位置 cast + BilVerifier）。最终 44 套件
  3183/3183 + Lexer fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告。
- 下一步：按用户确认顺序进入 #27⑦，随后处理 M84 三项；#28① 保持
  后续归口，#28② 维持规范既定错误行为。

### 2026-08-07 · M88 wrapper 烘焙架构反转

> 用户决策推翻 M81 定稿①③。理由：烘焙（派发链合成 / inner 链接 /
> 原始体替换 / 隐藏存储 / call??? 类别路由体）与 getter/setter 内联
> 本就同属 Middleware 职责；frontend 继续合成数百行特化 fn/隐藏字段/
> router 与「最终内联归 Middleware」目标重复且把 ABI 细节锁进编译器。
> 新边界：**编译器只携带标记，Middleware 负责全部烘焙**。分两段落地——
> M88a 纯文档规范修订、M88b 代码迁移 + 测试重构。同会话前置批次落地
> 技术债 #26 代码侧 + #27⑧/M79 param:W（见下「同批」）。

- **决策背景**：M81 定稿①声明侧烘焙骑 vtable、③特化按 (proxy×成员)
  在 P2 合成符号——使 S11a–S11e 在 frontend 堆出 ProxyDispatchResolver
  （~500 行）与 WrapperChain/WrappedBody/ProxySpecialization/
  DowngradeRouter 等合成槽。用户裁决：这些与 Middleware 内联/布局
  职责重叠，架构反转为「标记 + 模板」。
- **规范修订（M88a）**：
  - RUNTIME §14/§15 重写——frontend 三类标记：① `wrapped(W)` 应用
    修饰符；② proxy 模板 fn（`wrapper-proxy(specific|wildcard)`，
    体内 `inner`→`invoke fn(..inner)`、`self`→`get.self` 占位指令）；③ 降级
    调用点 `invoke core::Any$call???`。`call???` 改为 Any 的 bootstrap
    内建方法 + §22.5 VM hook（toString 先例），不再合成 router/特化体。
  - BIL §5.1/§5.3——隐藏字段名退出 BIL 文本，命名约定归 Middleware ABI；
    §8.3.1 隐藏字段声明删除→`wrapped(W)` 应用标记（init 实参承载留白）；
    §8.4 PROXY_KIND 两态 specific|wildcard（删 router/original）；
    新 §12.5 `get.self` / 新 §15.4 `invoke fn(..inner)`；§13.3 wrapper 写链元素
    改 `field(F)|wrapper(W)` 两态；§15.5 降级 invoke 恒
    `core::Any$call???`；§21.2/§21.3/§21.8 适配；§22.5 hook 表加 call???。
  - ARCH §5.2 重写（P2 零合成符号、P3 模板态绑定——self=TTarget 泛型
    参数、this=wrapper 实例自身（不再重写 BoundWrapperAccessExpression）、
    inner=占位调用节点（specific 按声明签名/wildcard 按 canonical 去
    symbol）、P4 模板 fn 发射）/§6.1/§7.1。
  - SYNTAX 仅「编译器生成隐藏字段/router」措辞改 Middleware，语言语义
    零变化。
- **代码迁移（M88b）**：
  - **删除**：`ProxyDispatchResolver` 与全部合成符号槽
    （WrapperChain/WrappedBodySymbol/ProxySpecialization/
    DowngradeRouter/DowngradeChain/WrapperApplication.HiddenField/
    ProxySpecializationInfo/ProxyLinkKind/FieldSymbol.IsCompilerGenerated）。
  - **P2 新**：`Resolution/ProxyMatching.cs`（形状匹配 +
    ProxyMatchChecker——名中形状不符诊断逐字保留，零合成）。
  - **bootstrap**：`Any.call???`（`BootstrapSymbols.CallWildcard`，
    pub native `latte_rt`/`call???`，签名非泛型胖值 ABI，
    `EnsureCallWildcard` 幂等；NamedPackType 设施迁入）。
  - **P3**：BindingDriver 删阶段 2 分流/2.5/2.6（转发壳/特化体/解包
    shim/router 体/Any.call??? 体合成全删）；proxy 声明体改模板态绑定；
    新 `BoundSelfExpression`/`BoundInnerCallExpression`；
    `ProxyBodyState` 瘦身为模板语境（IsActive + SelfType）；降级判定
    改资格判定（receiver 类型链 AppliedWrappers 含方法类别 `.proxy.*`）
    + `CallBinding{Method=CallWildcard}`；`IsDowngradeCallResult` 改
    引用相等 bootstrap CallWildcard（六消费点签名传 env）。
  - **Bil**：`BilProxyKind` 两态 + `BilWrappedModifier` +
    `GetSelfInstruction`/`InvokeInstruction`/`InvokeNoResultInstruction`；
    wrapper 链指令链元素两态化。
  - **P4a**：`LoweredGetSelfExpression`/`LoweredCallInnerExpression` +
    `LoweredWrapperFieldExpression.HiddenFields`→`WrapperChain` +
    `WrapperPlaceLowering.BuildWrapperFieldPlace` 产 `wrapper(W)`（局部/静态/
    interface 传染归口保留）。
  - **P4b**：`GetSelfEmitter`/`CallInnerEmitter` + `wrapped(W)` 声明
    投影（outer→inner=列表序）+ `wrapper-proxy` 投影。
  - **BilVerifier**：§21.2/§21.3/§21.8 新规则（模板 fn 双向一致/
    get.self·invoke fn(..inner) 仅模板内/wrapped 位置校验）。
  - **DispatchExplainer**：数据源改应用登记 × ProxyMatching（报告
    「Middleware 将烘焙的链」+ 降级资格）。
  - **同批修复**：wildcard inner 形状匹配（体内视角
    VariadicParameterViewType）与 EmittingDriver `.args` §7.2 序
    （`IsVariadic && !IsNamedVariadic` 防双记）。
- **同批（S11g 范围，先于/并行 M88）**：
  - **#26 代码落地**（M81 裁决兑现）——ext 泛型目标元数/歧义诊断
    （`NameResolver.ResolveDottedPath`：裸名命中泛型定义报
    `'Box' expects 1 type argument(s), got 0`、同名不同元数报
    `Ambiguous extension target: 'Box'`）+ ext 成员可见性按声明位置
    （AccessChecker 对 ExtTargetPath 走顶层规则；ModifierChecker 禁
    ext protected：`'protected' cannot be applied to extension members`）；
    9 新用例。
  - **#27⑧ + M79 param:W**——模板态下 `var x: TReturn`/`as TField`
    天然可解析（测试固化）；`BindWrapperSegment` 增第三源（泛型参数
    with 约束 wrapper 匹配，只读禁令同 M79 族）。
  - 用户裁决（2026-08-07）：#27⑦ / M84-a/b/c / #28 五项**保持归口**，
    下一对话专项处理。
- **测试重构**：`BilEmitterTests.Wrappers` 整文件按 M88 重写（place
  8 组 + proxy 模板 3 组 + 降级 5 组端到端）；BinderTests.Wrappers
  wrapper place 描述与局部归口文案；DeclarationResolverTests
  PrintDowngradeRequest 黄金；删除全部隐藏字段/合成 fn/router/
  `.wrapped.`/`.proxy.unwrap.` 断言形态。最终 **44 套件全绿**
  （确定性用例 3150 + Lexer fuzz 6000 + 语义 fuzz 3000）；CLI
  冒烟三样例（specific/wildcard/降级）端到端出合法 BIL。
- 下一步：#27⑦ / M84 三项 / #28 四项（⑤已消解）下一对话专项；
  其后 S12/S13/S14 按 ROADMAP。

### 2026-08-06 · M87 S11f 派发链诊断工具

> ROADMAP S11f 落地（按既定序列推进，无用户决策项）。验收达成：
> CLI `compile --file <src> --explain-dispatch` 报告编译单元内全部
> 被修饰成员的烘焙链（outer→inner 每层命中 specific|wildcard +
> canonical symbol）与存在 `.proxy.*` 类型的降级路由（router +
> 链 + Any.call???）；调用点级过滤为预留扩展（本次不实现）。数据源
> = S11a/S11e 符号产物 + CanonicalSymbolPrinter（ARCH §4.4），
> 规范零修订。

- **报告器**（`Semantic/DispatchExplainer.cs`）：遍历符号图命名空间
  树 + 嵌套类型，筛选带烘焙产物的宿主（AppliedWrappers / 成员
  WrapperChain / DowngradeRouter），按类型 canonical 名 Ordinal
  排序分组输出——`applied:` outer→inner 应用列表（定义名@应用类型）/
  `member <canonical>` 逐层 `[i] specific|wildcard <proxy 声明名>
  <特化 fn canonical>` + `wrapped <原始体 canonical>` / `downgrade`
  段（`router` + `[i] .proxy.<序>.???` + `end Any.call???`）；无
  任何产物输出明示行 `(no dispatch chains)`。
- **CLI**（`Core/Commands.cs`）：`--explain-dispatch` 无参标志，
  与 `--parse-only`/`--emit-bil`/`--sema-only` 互斥；管线同
  `--sema-only` 只跑 P1–P3，诊断有 Error 退出码 1、无错把报告
  写 stdout（日志/诊断仍走 stderr）；跳过「解析成功」噪声以免
  污染数据流。
- **测试 +24**（3196 → 3220，43 → 44 套件）：
  - 新套件 `DispatchExplainerTests`（第 44）20 用例——空报告/
    specific 单环黄金/wildcard 单环 + 降级/多层 outer→inner/
    specific-only 无 downgrade 段/双环降级/CLI 标志注册与互斥；
  - `CommandLineParserTests` 增 4（子命令齐全表 + 三互斥 + 合法
    解析）。
- CLI 冒烟：带 wrapper 源文件输出 applied/member 链/downgrade
  全段；无 wrapper 源文件输出 `(no dispatch chains)`。
- 下一步：S11g（复核收尾）。

### 2026-08-06 · M86 S11e `call???` 降级全链

> ROADMAP S11e 落地（按既定序列推进，无用户决策项）。验收达成：
> 未声明方法降级端到端出合法 BIL（invoke `call???` router → 降级
> 特化链 → Any.call??? 默认实现）。规范落地修订一项：RUNTIME §14.2
> 的 `call???` 泛型签名（`<TResult, named TNamedArgs...,
> TUnnamedArgs...>`）实质化为**非泛型胖值签名**——结构性必然：
> 双泛型包（单 GenericPack 槽）、双值包（BindArguments 单末位包 +
> §7.2 序）、包整体转发（归 S11g）均无 Bound 层表达；Any 胖值
> 自描述 typeid（RUNTIME §2），独立 typeid 包无消费者。规范文本
> 修订落 SYNTAX §14.8（请求 symbol 格式）+ RUNTIME §14.2（实质化
> 注记）+ BIL §15.4/§15.5（落地形态）与 §21.2/§21.8（验证器适配）。

- **统一胖值签名**：三合成符号（Any.call??? 默认实现 / 每宿主一个
  router / 逐应用降级特化）统一 `(symbol: String, namedArgs:
  Array\<Pair\<String, Any\>\>, unnamedArgs: Array\<Any\>): Any`——
  与 M85 wildcard 包 ABI（§14.7）同型，invoke 两端严格匹配；
  core::Pair 缺席降级 `Array\<Any\>`（既有 NamedPackType 规则复用）。
- **P2 符号合成**（`Semantic/Resolution/ProxyDispatchResolver.cs`
  第三步 `ComputeDowngradeChains` + `SemanticSymbol.cs` 两槽 +
  `CanonicalSymbolPrinter.PrintDowngradeRequest`）：宿主 wrapper
  链含方法类别 `.proxy.*` 时合成——router（宿主成员，名
  `call???`：`?` 非标识符字符用户不可声明，零冲突；
  `TypeSymbol.DowngradeRouter` 槽指认）+ 逐应用（outer→inner）
  降级特化 `.proxy.<序>.???`（`ProxySpecialization.Kind=Wildcard`、
  **`TargetMember` 可空化**（降级链无目标成员——per-member 路径
  取用加 `!` 并注释「降级链不经此路径」）、`OriginalBody` 槽复用
  为链末 inner 目标 = Any.call???）+ `EnsureCallWildcard` 幂等合成
  `Any.call???`（bootstrap Any 成员，Public，全编译单元至多一次）。
  请求 symbol 格式定稿（§14.8 增补）：`{定义级宿主}${名}({参数段})
  @.any`——位置实参只写静态类型、具名写 `名:类型`、按调用点书写
  序、返回段恒 `.any`（转换在物化点插入，非 symbol 语义）。
- **P3 降级判定**（`CallVisitors.BindInstanceMethodCall`
  candidates 空分支）：同名字段保持 "is not a method"；否则沿
  receiver 类型链（每步定义级回退）找 `DowngradeRouter`——无则
  "Undefined member" 逐字不变，有则降级合成：实参无目标类型
  预绑（失败即落袋返回）→ symbol 字符串面量 + 双包
  `BoundVarArgsArgument`（具名 NamedPackType/位置 `Array\<Any\>`）
  → `CallBinding{Method=router, ResultType=Any}`（TypeArguments
  空、GenericPack null——非泛型签名零 hidden args）；显式泛型
  实参照常降级（symbol 不含泛型信息，技术债 #28①）。
- **inner 自动补 symbol**（`BindCall` inner 分支）：canonical
  wildcard 体写 `inner(namedArgs=namedArgs, unnamedArgs=
  unnamedArgs)`（双具名不传 symbol）——降级链 innerTarget 三参
  （symbol 在首）会结构过滤拒绝；定稿：innerTarget 首形参名
  `symbol` 且用户实参未绑定时，合成具名实参
  `ArgumentASTNode(name="symbol")`（引用当前 fn 的 symbol 形参）
  追加进实参副本后走正常 OverloadResolution——per-member 链
  （shim 双参）天然不触发。
- **P3 体绑定**（BindingDriver 阶段 2.6 `BindDowngradeBodies`）：
  router 体直接构造（`return invoke 链首(this, 三形参引用)`，
  SynthesizeRouterShell 先例）；降级特化体 = proxy 声明体
  **零前奏**绑定（形参同名直通——per-member 链的 symbol/
  namedArgs/unnamedArgs 前奏物化整段不需要；self/inner/this
  上色与诊断去重同 S11b 口径；inner=下一环特化或 Any.call???）；
  Any.call??? 体合成 `throw new NoSuchMethodException(symbol)`
  （core 命名空间查找 + BoundNewExpression + BoundThrowStatement；
  无 stdlib 测试驱动跳过体合成，技术债 #28⑤）。
- **类型兼容豁免五位置**（`BoundAnalysis.IsDowngradeCallResult`
  单一定义点——识别 BoundInstanceCallExpression 且 Method 即
  宿主 DowngradeRouter）：声明初始化/赋值/return/实参
  （OverloadResolution.IsApplicable + Materialize +
  BindArguments 三处）对降级结果 Any 豁免 IsAssignable 拒绝——
  SYNTAX §14.7「返回值在调用点按期望类型插入一次转换，不符抛
  core.CastException」由 P4a `EnsureDeclaredType`（§6.5，Any→T
  引用不等即物化 §12.1 cast）字面兑现；`if?`/throw/复合赋值
  位置不豁免（无 cast 物化点，豁免会产非法 BIL，技术债 #28④）。
- **P4 发射**：前两步的临时双闸门（LocalSymbolEmitters 声明 +
  EmittingDriver 定义）删除——router/降级特化/Any.call??? 全量
  平铺；`EmitMethodDeclaration` 增 router 投影（
  `method.Owner?.DowngradeRouter == method` 引用相等 →
  `wrapper-proxy(router)`；降级特化经 `IsBakedProxyProduct`
  既有路径投 wildcard）；Any.call??? 宿主 IsBuiltin 不进符号段，
  只产 fn 定义（体 = new + throw）。
- **BilVerifier 适配**：§21.8 放行 `call???` 名 + kind↔名一致
  增补（`call???` ↔ router、`.proxy.`/`.wrapped.` 名不得
  router）；§21.2 builtin 宿主 fn 定义豁免（内建类型不进符号段
  是结构性事实——`IsPredefinedTypeHost` 判定）；预定义符号表补
  `core::Any$call???`（invoke 可解析性闭合）。
- **测试 +69**（3127 → 3196）：
  - `DeclarationResolverTests` TestDowngradeChains 18（单环合成
    签名与元数据/双应用双环/无 `.proxy.*` 无链/
    PrintDowngradeRequest 黄金）；
  - `BinderTests.Wrappers` TestDowngradeBinding 11 用例（降级
    绑定形态与 symbol 串/具名实参/负例无链/字段同名优先/
    router·特化·Any.call??? 三体形态/语句位置/值位置 Any/
    return 与实参（单多候选）豁免/普通 Any 赋值不误伤）；
  - `BilEmitterTests.Wrappers` TestDowngradeEmission 5 组端到端
    （单环全链黄金（调用点 invoke `Service$call???` + symbol
    字符串资源 + 双包构造 + router 声明 `wrapper-proxy(router)`
    + 特化 `wrapper-proxy(wildcard)` + Any.call??? 体 throw）/
    值位置 cast 物化（§12.1 到声明类型）/语句位置/双 wrapper
    双环/无链负例）；
  - `BilVerifierTests` router 3 例（call??? + router 正例/
    非 call??? 名带 router 拒/call??? 带 specific kind 不符拒）；
  - CLI 冒烟：`--emit-bil` 落盘验证器零错误，BIL 文本含
    `.method Service$call???...` router 声明与全链 invoke。
- 下一步：S11f（派发链诊断工具 `compile --explain-dispatch`，
  RUNTIME §15）。

### 2026-08-06 · M85 S11d P4b 合成 fn 发射，烘焙端到端

> ROADMAP S11d 落地（按既定序列推进，无用户决策项）。验收达成：
> specific 与 wildcard 声明侧烘焙端到端出合法 BIL（invoke 原名 →
> 特化链 → 原始体）；规范零修订——BIL §8.4 `wrapper-proxy(PROXY_KIND)`
> 与 §5.1 合成保留名的消费端兑现。

- **Bil 模型**（`Bil/BilSymbols.cs` + `BilSpellings.cs`）：新
  `BilProxyKind` 四态枚举（specific/wildcard/router/original——
  router 为 S11e `call???` 路由体预留）与 `BilWrapperProxyModifier`
  （`wrapper-proxy(KIND)` 自渲染，拼写入 BilSpellings 唯一定义点）。
- **P4b 开闸**（`Lowering/Emitting/LocalSymbolEmitters.cs`）：
  「.」前缀闸门改分流——烘焙产物照常发射声明（特化 fn =
  `ProxySpecialization` 槽非空、原始体 fn = `.wrapped.` 名段、
  wildcard 解包 shim = `.proxy.unwrap.` 名段）；wrapper 类型内的
  proxy 声明模板（`.proxy.<名>`/`.proxy.*` 等）是编译期模板（体只
  经逐组合绑定进特化 fn、自身无 fn 定义），不进 BIL。
  `EmitMethodDeclaration` 投影 `wrapper-proxy(KIND)`：特化按
  `ProxySpecialization.Kind` 取 specific/wildcard；原始体取
  original；shim 取 wildcard（wildcard 环的解包辅助）。转发壳
  （被修饰成员原名 fn）是普通成员声明，不标本修饰符（§8.4 注记
  「转发壳即被修饰成员原名 fn，其 body 为对特化链首的普通
  invoke」）。
- **双驱动闸门删除**（`LoweringDriver`/`EmittingDriver`）：特化
  fn/原始体 fn/转发壳/解包 shim 全量平铺发射——P4a 侧 proxy 体经
  M84 `WrapperPlaceLowering` 同路径出 get.wrapper/wrapper 写链，
  无新增 Lowered 节点。
- **BilVerifier 适配**（§21.8，`BilVerifier.Symbols.cs`）：
  wrapper-proxy 只允许在 `.proxy.`/`.wrapped.` 保留名方法上
  （§5.1）；保留名方法必须带本修饰符（缺失即非烘焙产物——用户
  伪造保留名同此拦截）；kind 与名段一致（`.wrapped.` ↔ original、
  `.proxy.` 不得 original）；修饰符重复检查。名段经新
  `MethodNameSegment` 提取（`$` 后、参数段/`@` 前）。
- **同批修复 M82/M83 潜伏类型不符**（发射暴露，§21.3 拦下）：
  wildcard 前奏具名包局部定型 `Array\<Any\>`，而 P4b
  `VarArgsEmitter` 具名打包产物恒为
  `.array<core::Pair<.string, .any>>`（§14.7 胖值 ABI）——set.var
  两端型不一致。修复：具名包按 §14.7 ABI 定型
  `Array\<Pair\<String, Any\>\>`，前奏物化局部
  （`BindingDriver.PackMemberArguments`）与 shim `namedArgs` 形参
  （`ProxyDispatchResolver.SynthesizeUnwrapShim`，canonical 同步）
  两处同规则同改；core::Pair 缺席（无 stdlib 的测试驱动）降级
  `Array\<Any\>`（PathVisitors.VariadicParameterViewType 先例），
  无 stdlib 套件黄金零变化。
- **测试 +33**（3094 → 3127）：
  - `Tests/BilEmitterTests.Wrappers.cs` 三组端到端：specific 单环
    （四 fn 黄金——转发壳 invoke 链首/原始体用户体/特化 inner 调用
    /caller invoke 原名（调用点零改动，烘焙骑 vtable）+ 声明形态
    （`priv wrapper-proxy(original|specific)`、壳无修饰符）+ proxy
    声明模板不进段断言）；wildcard 单环（前奏三形参物化（symbol
    常量 + 双包打包——具名包 pair-array ABI 形态）+ shim cast
    解包黄金 + `wrapper-proxy(wildcard)` 声明）；get 访问器链
    （字段槽壳声明 + 成员表两合成 fn + 三 fn 黄金）；
  - `Tests/BilVerifierTests.cs` 修饰符规则 6 例（正例三 kind +
    非保留名带修饰符/保留名缺修饰符/kind↔名段不符两态/修饰符
    重复，ExternalSymbols 聚焦声明侧同 IndexModule 先例）；
  - `Tests/BinderTests.Wrappers.cs` M83「P4 闸门」用例**转正**为
    开闸贯通断言（三件套 lowering 产物齐备 + 转发壳/特化体
    LoweredDescribe 黄金）；
  - CLI 冒烟：specific/wildcard/访问器链三样例 `--emit-bil` 落盘，
    验证器零错误，BIL 文本含
    `.method Service$.proxy.0.doSomething(arg:.i32)@.string priv
    wrapper-proxy(specific)` 与全链 invoke。
- 下一步：S11e（`call???` 降级全链——P3 降级判定 + 胖值 ABI +
  router 合成 fn（`wrapper-proxy(router)` 模型已就位）+ bootstrap
  `Any.call???` 默认实现）。

### 2026-08-06 · M84 S11c P4a/P4b wrapper place 成员访问

> ROADMAP S11c 落地（解 M79 P4 归口；无用户决策项——按既定序列
> 推进）。BIL §12.4 注记/§13.3/§8.3.1 三处定稿的消费端兑现，
> 规范零修订。使用点与 proxy 体内共用同一 lowering 路径
>（`WrapperPlaceLowering` 唯一设施，proxy fn 体的发射闸门归
> S11d）。

- **P3**：`BoundWrapperAccessExpression` 携带命中应用记录
  （`Application` 槽，`Wrapper` 降为派生属性）——BindWrapperSegment
  与 BindThisPath（proxy 体 this 重写）两处构造点直通，P4 经
  `Application.HiddenField` 取 `.wrapper.` 隐藏字段符号，零重复
  查找。
- **P4a**（新 `Lowering/Rewriters/WrapperPlaceLowering.cs`）：按
  应用类别分派（经 place.Receiver 形状与 Application 引用相等比对
  还原类别——P3 已保证命中唯一）：
  - **Entity 应用**（隐藏字段挂宿主类型）：字段读/方法调用/索引读
    = `get.wrapper` 值拷贝链（新 `LoweredGetWrapperExpression`，
    嵌套链 `s:Outer:Inner` 逐级物化合成局部）；字段写 =
    `set.wrapper.field` 链；
  - **字段-Value 应用**（隐藏字段挂字段宿主类型）：字段读 =
    `get.wrapper(.field)+get.field（读侧；旧嵌套读指令已移除）` 链、字段写 = `set.wrapper.field` 链——
    宿主值取字段访问的 **receiver**（wrapper 实例按字段槽挂在属主
    对象上，字段值本身寻址不到隐藏字段；读取跳过字段 getter，
    语义正确）；
  - **归口**：局部/静态目标（栈帧/静态存储合成归后续里程碑，
    HiddenField 为 null）、字段-Value 方法调用与索引（get.wrapper
    的 VALUE 规则只为 Entity 形态定义、BIL 无 专用索引指令）、
    深层写穿（`place.a.b`——写穿中间拷贝会丢失）、索引写
    （`place[i] = x`）——全部显式 P4 诊断，不静默 miscompile；
  - **复合赋值** `place.field op= x` 专用路径（读写分离 + 终极宿主
    单次求值共享——读路径产物是值拷贝，写后重读不到新值，表达式
    位直取写回值结果局部，与一般路径「重读 place」可观察等价）。
  - 消费方五处挂钩：FieldAccessRewriter（读）/InstanceCallRewriter
    （调用，跳过宿主 cast——物化产物类型即 wrapper 类型本身）/
    IndexRewriter（索引读）/CallStatementRewriter（void 调用）/
    AssignmentRewriter（写）/CompoundAssignmentRewriter（复合）。
- **P4b**：新 `GetWrapperEmitter`（§12.4 get.wrapper）与
  `WrapperFieldEmission` 走链设施（Emitting/ValueEmitters.cs——
  链长 1 直达；≥2 逐级 `get.wrapper(.field)+get.field（读侧；旧嵌套读指令已移除）` 取内层 wrapper 原地
  别名（每步跨两级），末段读 = get.field / 写 = set.field 或
  set.wrapper.field）+ AssignmentEmitter set.wrapper.field 写分支（宿主
  先行物化，求值序不变）+ **`.wrapper.` 隐藏字段声明开闸**
  （§8.3.1：LocalSymbolEmitters 的 IsCompilerGenerated 跳过闸门
  删除，EmitFieldDeclaration 增分支发 `priv var backing
  compiler-generated` 形态——BilVerifier §21.2/§21.3 既有规则
  直接闭合）。
- **测试 +37**（3057 → 3094）：
  - M79 P4 归口用例**转正**（BinderTests.Wrappers
    TestWrapperPlaceLoweringGate → TestWrapperPlaceLowering）：
    P4a 形态断言四组（Entity 读（值拷贝 + get.field）/Entity 写
    （wrapper 写链）/字段-Value 读（wrapper 写链，宿主 = 属主对象）/
    局部归口）；
  - `Tests/BilEmitterTests.Wrappers.cs` 新 partial 八组：Entity
    字段读/字段写/方法调用/嵌套链读/索引读、字段-Value 读写、
    复合赋值端到端（CheckBilValid + CheckFnShape 黄金）+ 隐藏
    字段声明形态断言（§8.3.1 修饰符序列）+ 归口负例五例
    （局部/静态/字段-Value 调用/索引写/深层写穿）；
  - CLI 冒烟：Entity 读+写+调用样例 `--emit-bil` 落盘（验证器
    零错误），BIL 文本含 `.field Service#.wrapper.Logged@Logged
    priv var backing compiler-generated` 与 get.wrapper/
    set.wrapper.field 指令。
- **遗留归 S11g 复核**：字段-Value 方法调用/索引（需规范澄清
  get.wrapper VALUE 形态或新指令）、深层写穿（值类型中间层的
  写回语义）、局部/静态 wrapper 存储合成（栈帧隐藏局部 +
  构造安装——依赖 wrapper init 实参绑定）、interface 传染
  接收者的写路径（实现者各自持有隐藏字段，无单一符号可引）。
  下一步：S11d（P4b 合成 fn 发射，`wrapper-proxy(PROXY_KIND)`
  修饰符 + 特化/原始体/转发壳平铺——烘焙端到端出合法 BIL）。

### 2026-08-06 · M83 S11b P3 proxy 体逐组合绑定

> ROADMAP S11b 落地（纯 P3 步，M82 符号产物消费；无用户决策项——
> 按 HANDOVER 施工指引推进）。被修饰成员的 wrapper 逻辑经三件套
> BoundFunctionBody 落成普通 fn 与 invoke 链（P4 零 wrapper 语义，
> M81 烘焙形态定稿③兑现）。

- **驱动改道（BindingDriver）**：阶段 2 骨架遍历分流——`.proxy.`
  前缀名的 proxy 声明体不按普通 operator 体绑定（`inner`/`self`
  上色依赖组合语境），收集 `(声明符号 → AST/FileCtx)` 归阶段 2.5；
  被拦截成员（`WrapperChain != null`，含访问器——BindAccessorBodies
  同步重定向）的用户体改挂 `WrappedBodySymbol` 绑定（产物 Method =
  原始体符号，P4 发射闸门已就位）。**阶段 2.5 逐组合绑定**三件套：
  - **转发壳**（被修饰成员原名 fn）：body = invoke 链首（实参 = 本
    fn 形参逐一引用，固定泛型参数逐位转发——P4b 物化 `$.generic.T`
    零指令，M71 先例；含可变泛型参数包不带显式实参，归 S11g 复核）；
    直接构造 bound 节点（M77 init 映射合成先例——Syntax 回指声明
    节点、无 DA/return 问题）。
  - **特化体**（`.proxy.<序>.<成员键>`）：proxy 声明 AST 体经块
    分派绑定，组合语境挂新组件 **`Binding/ProxyBodyState.cs`**
    （BindContext 第六组件，M65 组件化先例——Specialization/
    InnerTarget（下一环特化或链末 OriginalBody；wildcard 普通/
    operator 环 = 解包 shim）/SelfType（应用记录 Wrapper 构造实参，
    零泛型 wrapper 为 null）/MaterializedLocals（前奏物化局部表））；
    **前奏物化**（M81 定稿「wildcard 特化签名 = 成员签名」的配套）：
    proxy 声明形参与特化 fn 形参同名者直通（裸名查找命中特化符号
    形参，BIL .args 一致），不同名者物化合成局部（const）前插体首
    ——wildcard 的 `symbol` = canonical 字符串常量（PrintMethod，
    §14.8）/`namedArgs`/`unnamedArgs` = 自 fn 形参打包（复用 M72
    `BoundVarArgsArgument`，Type = `Array\<Any\>` 构造——包类型是
    烘焙链内部约定，§14.8 名值对 ABI 归 call??? 的 S11e）、get 类别
    的 `value` = invoke 下一环的无参调用结果。
  - **wildcard 解包 shim**（`.proxy.unwrap.<序>.<成员键>`，S11a 同批
    P2 合成符号：双包参签名 + 成员泛型拷贝；本步合成 body）——逐
    元素 `cast`（Any → 形参类型，拆箱，§14.7 同款 CastException
    语义；目标即 Any 省 cast 直通）自 `unnamedArgs` 索引取出后
    invoke 下一环。
- **self/inner/this 上色**（SYNTAX §14.2/§14.5，ARCH §5.2 兑现）：
  - `self`（PathVisitors 新 BindSelfPath）：proxy 语境绑定为宿主
    角色的 this（`BoundThisExpression(SelfType)`，后缀/段链与 this
    同构）；wrapper 零泛型参数时报「'self' is not available here:
    the wrapper declares no TTarget generic parameter (§14.2)」。
  - `inner`（CallVisitors.BindCall 开头拦截）：proxy 语境候选集
    替换为 `[InnerTarget]` 走既有重载解析（单候选——实参匹配/
    默认值/具名归位全复用；目标泛型参数全固定时逐位转发当前 fn
    泛型参数），receiver = 宿主 this（链内符号 Owner 恒为宿主）。
  - proxy 体内 `this`（BindThisPath 重写）：`BoundWrapperAccess
    Expression(this(宿主), 应用.Wrapper)`——与使用点 `obj:W` 同构
    的只读 place，成员访问（`this.level`/`this.tag()`）经既有实例
    链上色；链末裸 this 按赋值/取值分措辞报只读禁令（与 M79
    BindWrapperSegment 同族）。
  - **非 proxy 语境**的 `self`/`inner` 报专门诊断（"'self'/'inner'
    is only available in a wrapper proxy body (§14.2)"，取代原
    Undefined name/function——ARCH §5.2「三者在非 proxy 语境出现
    是编译错误」兑现）；proxy 声明体在阶段 2 不再常规绑定，M82 前
    的 Undefined 报错路径随分流消失。
- **诊断去重**：同一 proxy 声明体跨组合绑定（(proxy × 目标成员)
  逐组），体内同一错误按 (proxy 声明, span 引用, 消息) 去重——
  BindEnvironment 增去重集 + CurrentProxy 标记（驱动单趟顺序
  执行，try/finally 复位）；return 全路径检查消息改用 proxy 声明
  名（各组合一致，天然去重）。
- **P2 同批扩展（ProxyDispatchResolver）**：wildcard 普通/operator
  环合成解包 shim 符号（`.proxy.unwrap.<序>.<成员键>`，签名 =
  `(namedArgs: Array\<Any\>, unnamedArgs: Array\<Any\>)` → 成员返回
  类型拷贝，成员泛型参数同款拷贝——shim body 经 TypeArguments 向
  下一环转发）；`ProxySpecializationInfo` 增 `UnwrapShim` 槽
  （specific 环与 get/set wildcard 环为 null——其 inner 形态与下一
  环签名直通）；可变参数（vargs/kwargs）成员跳过建链（转发壳的
  包展开在 Bound 层无表达，登记技术债 #27⑦）。
- **P4 闸门**（合成 fn 零泄漏，S11b 中间态）：LoweringDriver 与
  EmittingDriver 同步跳过 `.` 前缀名 fn 与转发壳（`WrapperChain !=
  null`）——lowering 无消费者且 proxy 体内 wrapper place 成员访问
  降级归 S11c；CLI 行为：`--sema-only` 端到端零诊断（specific/
  wildcard/双环链/get 访问器链实测）；`--emit-bil` 对含链源码被
  BilVerifier §21.2 正确拦截不落盘（转发壳 fn 定义归 S11d——与
  M79「正确归口不落盘」同惯例的中间态）。
- **测试 +31**（DeclarationResolver +4：shim 符号合成/canonical
  黄金/specific 环无 shim/单环序号；Binder 新组
  `TestProxyBodyBinding` +27：specific 三件套形态（转发壳/原始体/
  特化体 + 形参引用相等断言）、self/this 上色（含 wrapper 方法经
  this 调用）、wildcard 前奏物化与 shim 解包黄金、双环链
  outer→inner 序、get 访问器链（自动访问器合成体改挂 + value
  物化）、负例六项（非 proxy 语境 self/inner、零泛型 self、裸 this
  取值、inner 实参不符、诊断跨组合去重）、P4 闸门）：43 套件
  3057/3057 + fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告。
  下一步：S11c（P4a/P4b wrapper place 成员访问，解 M79 归口）。
- **边界登记**：proxy 声明泛型参数的体内类型引用代入（`var x:
  TReturn`）暂缓——未接线（典型 proxy 体（inner 转发/value 直通）
  不消费，需要时在 TypeReferences 解析路径加代入映射），归 S11g
  复核；
  `docs/HANDOVER.md` 按约定删除（局部访问器路线 C 决策由技术债
  #22① 注记与 ROADMAP S11 段承载）。

### 2026-08-06 · M82 S11a P2 proxy 形状校验与符号合成

> ROADMAP S11a 落地（纯 P2 步，M81 细化首子步）。无用户决策项；
> 落地修订两处（元数放宽与「必须实现 get」不强制，见下）经既有
> fixture 与 §14.5 用法实证后定稿。

- **AppliedWrappers 升级 `WrapperApplication` 记录**：元素由裸
  TypeSymbol 升级为 `Wrapper {Wrapper, Syntax, HiddenField}`——
  Entity wrapper 恰一泛型参数时 Wrapper 为 TTarget 代入宿主的
  构造类型（M79 遗留「TTarget 显形」兑现）；Syntax 携带 init
  实参（`@W(...)` 实参此前零语义消费，绑定归 S11b）；HiddenField
  由 ProxyDispatchResolver 回填。`NameResolver.ResolveSymbolPath`
  新 `allowBareGenericDefinition` 开关（wrapper 注解裸名命中泛型
  定义放行，元数校验归形状检查器）；`with` 约束匹配/OverloadResolution
  镜像/PathVisitors 双源查找/局部登记与两处既有断言全量适配
  （构造回退定义比较走 `WrapperDefinition`）。
- **`Resolution/ProxyCheckers.cs`（ProxyShapeChecker，P2 新阶段）**：
  泛型元数（Entity 至多一、恰一即 TTarget 角色；Value/Method 零）+
  类别矩阵（Entity：specific 四类 + wildcard 四类；Value 仅
  `.proxy.get`/`.proxy.set`；Method 仅 `.proxy.call`）+ wildcard
  canonical shape 逐参数校验（参数名 symbol/namedArgs/unnamedArgs/
  value 属 ABI，泛型参数名不校验）+ specific get/set 形状（恰一
  泛型参数 + 唯一 value 参数）+ `.proxy.call` 双形态（§14.4：
  `.name` 首参即 wildcard 形态，否则恰一泛型参数作返回类型）。
  **落地修订**（经既有 fixture 与 §14.5 实证）：Entity 元数由 M81
  文本「恰一」放宽为「至多一」（零个时 `self` 不可用，SYNTAX §14.2
  同步修订）；§14.3「必须至少实现 get」不强制执行——不带 proxy 的
  纯状态 wrapper 是 §14.5 合法用法（`obj:W.member`）。
- **`Resolution/ProxyDispatchResolver.cs`（P2 新阶段）**：
  ① `.wrapper.` 隐藏字段合成（§5.3 命名 `.wrapper.` + wrapper
  canonical 全名（构造含实参段）、priv + `IsCompilerGenerated`
  新标记；interface 自身不合成（无实例），传染到每个实现者且
  TTarget 按实现者重建构造；Value 应用的实例字段挂字段宿主；
  同名冲突（同一 wrapper 多次应用）诊断）；② Entity 派发链计算
  （§14.6：specific 名中且形状全等（TTarget 代入宿主后参数名/
  参数类型/返回类型逐项一致）优先；名中形状不符即诊断；名未命中
  落类别 wildcard；泛型成员只参与 wildcard；双未命中 inert 静默）；
  ③ 逐组合特化 fn（`.proxy.<序>.<成员键>`）与原始体 fn
  （`.wrapped.<成员键>`）合成——签名全拷贝（泛型参数与参数符号
  新实例、类型逐层代入）+ `ProxySpecializationInfo` 元数据槽。
- **发射闸门**（合成符号零泄漏）：「.」前缀名的声明
  （LocalSymbolEmitters）与 fn 定义（EmittingDriver）跳过归 S11d，
  IsCompilerGenerated 字段声明跳过归 S11c——CLI 冒烟（被修饰类
  端到端 --emit-bil）实测零泄漏、验证器零错误。
- **边界登记**（技术债 #27）：Value/Method wrapper 链、无访问器
  字段拦截、interface 实现者链继承/override 链、async 交互、
  全局/静态存储、proxy 体逐组合绑定（`inner`/`self`）归后续
  （S11b 及以后）。
- 测试 +48（DeclarationResolver 形状负例 20 + 隐藏字段/链符号
  断言与 canonical 黄金 28）：43 套件 3026/3026 + fuzz 6000 +
  语义 fuzz 3000 全绿，build 0 错误 0 警告。下一步：S11b（P3
  proxy 体逐组合绑定：self/inner/this 语境 + 转发壳与解包 shim）。

### 2026-08-06 · M81 S11 proxy 烘焙细化 + ext 三事裁决（纯文档）

> ROADMAP S11 剩余两项（proxy 烘焙 lowering、派发链诊断工具）此前
> 仅有单行范围，无子任务/验收/落点清单——按 S8/S9 细化先例
> （M54/M67）出纯文档里程碑。烘焙形态三项决策与 ext 两事经用户
> 定稿（2026-08-06）。

- **细化**：S11 剩余拆为 **S11a–S11g** 七子步（ROADMAP S11 段，
  每步带验收标准）——S11a P2 形状校验与符号合成 / S11b P3 proxy
  体逐组合绑定 / S11c P4a/P4b wrapper place 成员访问（解 M79
  归口）/ S11d P4b 合成 fn 发射（烘焙端到端）/ S11e `call???`
  降级全链 / S11f 派发链诊断工具 / S11g 复核收尾（M79 遗留两项
  复核 + #26 代码落地 + 交叉引用清理）。
- **烘焙形态定稿（用户决策）**：① 声明侧烘焙——wrapper 逻辑
  编译期进入被修饰成员方法体、骑 vtable（RUNTIME §14 字面语义），
  调用点零改动；② proxy 特化体为带 `wrapper-proxy(PROXY_KIND)`
  修饰符的独立合成 fn，编译器不做文本内联，最终内联归
  Middleware；③ 特化按 (proxy × 目标成员) 组合在 P2 合成符号
  （Freeze 前），P3 对 proxy 声明体逐组合绑定（`self` = 宿主角色
  this、`inner` = 下一环普通调用、proxy 体 `this` →
  BoundWrapperAccessExpression），P4 零 wrapper 语义——烘焙产物
  在 BIL 层即普通 fn 与 invoke 链。
- **技术债 #26 三事裁决（规范文本同批落地，代码归 S11g）**：
  ① ext static 合法，SYNTAX §4.4 明文补例（追认 M80 实现与
  样例）；② priv/protected ext 可见性**按声明位置**判定（顶层
  ext 适用顶层规则；现状「目标类型容器判定」使 priv ext 声明
  文件不可见，属未设计的意外），ext 体不放开目标私有成员访问
  （封装不因扩展开口）；③ ext 泛型目标禁止静默接受——裸名命中
  泛型定义报元数诊断、同名不同元数报歧义（现状静默收进且
  先者胜），「隐式获得目标泛型参数」留作语言候补。
- **规范修订清单**：SYNTAX §4.4（三事成文 + static 示例）/
  §14.2（Entity wrapper 恰一 TTarget = self 类型来源、specific
  proxy 形状与被代理成员全等、宿主创建时安装隐藏字段）/ §16.1
  （ext 可见性句）；BIL §8.4（PROXY_KIND = specific/wildcard/
  router/original 四值）+ §5.1（合成保留名 `.proxy.`/`.wrapped.`/
  `.wrapper.`）；RUNTIME §14（特化 fn 独立发射、内联归
  Middleware）+ §15（`compile --explain-dispatch` 形态）；ARCH
  §6.1（脱糖清单补 wrapper place 成员访问与 `call???` 降级两行 +
  pass 归属注记）+ §5.2（proxy 体绑定规则条）；ROADMAP S11
  （细化块 + 施工序更新）。
- 纯文档零代码改动：基线复核 build 0 错误 0 警告，43 套件
  2978/2978 + fuzz 6000 + 语义 fuzz 3000 全绿。下一步：S11a
  （P2 proxy 形状校验与符号合成）。

### 2026-08-06 · M80 S11 ext 收尾

> ROADMAP S11 后续施工第二项（按序推进，无用户决策项）：ext 机制收尾——
> 一处确认的 P4b 发射缺口修复 + P2 两处校验盲区收口 + 端到端样例补齐
> （勾销技术债 #22④）。规范零修订（两闸门均为既有规则 §11/§3.1.1 的
> ext 路径兑现，SYNTAX §4.4 补一句成员语义注记）。

- **P4b 修复（实测复现）**：`LocalSymbolEmitters.EmitBuiltinExtMembers`
  只发内建 ext 字段/方法本身、不随迁访问器声明（对齐 EmitNamespace/
  EmitTypeDeclaration 的字段槽驱动形态）——修复前 SYNTAX §4.4 原文示例
  形态（`pub ext var String.isEmpty: bool { get(_: _) {...} }`）的访问器
  fn 定义照常发射但声明缺失，§21.2「fn 定义在 LocalSymbols 中没有对应
  方法声明」拒绝落盘（CLI 实测复现后修复）。
- **P2 两闸门收口**（ExtensionRegistrar，违规即不注册——与判重同口径）：
  ① ext 字段禁注 interface 目标（§11 interface 不得声明字段——M78
  ModifierChecker 禁令的 ext 路径绕行收口；ext 方法不受限）；
  ② ext 实例字段与目标体内声明同受 §3.1.1 闭包表约束（新
  `FieldClosureChecker.CheckExtensionField` 入口——完整复用
  CheckClosureField 直接分类 + 泛型实参展开；FieldClosureChecker 阶段
  运行在注册之前，此前 ext 字段永不被覆盖；静态 ext 字段仍归
  SharedSafetyGateChecker 闸门 1，不参与实例闭包）。
- **端到端样例**（`Tests/BilEmitterTests.Ext.cs` 新 partial 五组）：
  ext 实例字段读写 + 实例方法调用（用户类型，fn 形状黄金）/ext 字段 +
  backing 访问器（用户类型：backing/readable/writable/compiler-generated
  + getter(FIELD)/setter(FIELD) + ext 修饰，访问器 fn 与使用点形状）/
  ext 字段 + computed 访问器（内建 String——M80 修复锁定，§8.4.1 裸条目
  声明随迁）/ext static 字段/常量/方法（.static-field/.static-method +
  .static. canonical + get/set.field.static/invoke 无 receiver）/ext 字段
  复合赋值（单次求值脱糖贯通）。DeclarationResolver 两闸门用例 +9
  （负例 4 + 不注册断言 2 + 正例 3）。
- **行为确认**：ext 成员无访问修饰符按 §16.1 成员语义默认 private——
  使用点在目标类型体外即不可见（SYNTAX 示例的 `pub ext` 形态即正解）；
  priv/protected ext 的可见性语义、ext static 合法性明文、ext 目标
  泛型定义（裸名命中泛型定义的元数/歧义）三事规范未明，登记技术债
  #26 待裁决。
- **测试**：2946 → 2978（+32），43 套件全绿 + fuzz 6000 + 语义 fuzz
  3000，build 0 错误 0 警告；CLI 实测五样例（内建 ext 访问器/实例字段
  与方法/访问器/static/复合赋值）--emit-bil 过 BilVerifier 落盘。

### 2026-08-06 · M79 S11 wrapper place 绑定与只读禁令

> ROADMAP S11 后续施工第一项（按序推进，无用户决策项）：P3 wrapper
> place 绑定 + 只读禁令全拦截面（SYNTAX §14.1/§14.5 兑现，规范零修订）。
> P4 发射（get/set.wrapper.field + `.wrapper.` 隐藏字段声明）归 proxy
> 烘焙步。

- **Bound 节点**：`BoundWrapperAccessExpression`（Receiver + Wrapper
  符号，Type = Wrapper 定义）——只读 place 只作成员访问接收者（字段
  读写/方法调用/索引），永不作为路径绑定结果产出：P3 绑定期封死，
  下游（AsyncGates/P4/描述器）零新消费点之外的行为变化。
- **PathVisitors 两处 Colon 归口解开**：BindInstanceChain Colon 分支 →
  新 `BindWrapperSegment`。wrapper 查找双源同池：宿主来源符号
  AppliedWrappers（字段/局部——Value wrapper）+ 宿主静态类型
  AppliedWrappers（构造回退定义——Entity wrapper），按段名匹配；零命中
  「has no wrapper applied」/多命中「Ambiguous wrapper」均诊断；
  nullable 宿主拒绝（与普通段同口径）。**只读禁令全拦截面**：链末
  无后缀 Colon 段即整体赋值/取值，按 forAssignment 分措辞——取值逃逸
  （变量初始化/实参/返回值/推断源/运算与类型检查操作数/插值段……）
  全部经路径绑定结果，收口于一处；带后缀（索引成员访问）与非链末
  （字段/方法段继续消费）即合法接收者。
- **容器路径泛化**：Colon 段切分——容器只消费到首个 Colon 段之前
  （`Type.staticField:W` 静态字段宿主），剩余段交实例链；`Type:W`
  无值宿主诊断「requires a value host」。
- **局部变量 wrapper 应用 P3 登记**（WrapperCheckers「栈上声明归 P3」
  注记兑现）：`LocalSymbol.AppliedWrappers` 槽 + LocalDeclarationVisitor
  注解解析登记（@WrapperTarget/内建注解/非 wrapper 类型/Entity·Method
  类别不符——诊断措辞与 P2 WrapperCheckers 逐字对齐；§14.9 矩阵 C
  恒合法免 shared/宿主检查）；解构声明上的注解归口。
- **下游接线**：AsyncGates 遍历收编（防腐化 default 抛
  CompilerInternalException——新增 Bound 节点必须显式登记）；
  LowerDispatchers P4 显式归口「P4: wrapper place lowering is not
  supported yet (S11)」（get/set.wrapper.field 与 `.wrapper.` 隐藏字段
  声明随 proxy 烘焙落地）；BoundDescribe 节点支持。
- **测试**：BinderTests.Wrappers.cs 新 partial 四组 +35（2911 → 2946）——
  正例 17（Entity 字段读/写/方法调用/链式 `s:Outer:Inner`/this 宿主/
  局部 Value/静态字段 Value（shared wrapper × 静态目标矩阵）/索引后缀
  + 局部应用登记引用相等断言）；只读禁令 7（赋值/推断源/实参/返回值/
  复合赋值/is 操作数/插值段）；负例 8（未应用/nullable 宿主/类型宿主/
  局部 Entity 类别/非 wrapper 类型/@WrapperTarget 挂局部/解构归口/
  局部无应用）；P4 归口 3（绑定零诊断 + 归口消息 + 函数体跳过）。
- **遗留**：泛型参数 receiver 的 with 约束 place（`param:W`，
  `T with W` 场景——泛型参数符号无 AppliedWrappers）归后续；泛型
  wrapper 应用的实参代入（TTarget 显形）与 Method wrapper 无 place
  访问形态（方法非值——BindInstanceFieldAccess 自然拦截）随 proxy
  烘焙复核。43 套件 2946 全绿 + fuzz 6000 + 语义 fuzz 3000，build
  0 错误 0 警告；CLI 实测（wrapper 字段读/写样例 --sema-only 零诊断、
  --emit-bil 正确归口不落盘）。

### 2026-08-05 · M78 存疑项裁决批次

> **用户决策**：M76 review 的存疑清单六项全部落地（四组并行 + kwargs
> 闭环收口），然后 commit 停工。规范增补两处（SYNTAX §3.3 科学计数法
> 形态 + §13.2 求值序 UB 句）。

- **① BilVerifier TypesCompatible 收紧 canonical 全等**：原「剥基名比较」
  对任意类型引用生效（`.array<.i32>` ≡ `.array<.string>`、跨命名空间同名
  互判兼容），§6.4 严格相等整体失守。重写为 `NormalizeTypeRef` + 内建
  别名表（同一类型两种拼写归一）后严格全等（构造类型递归逐实参）；协变
  （in/out）注释预留归后续里程碑；`HostMatches` 专用辅助把 IsAssignableTo
  宿主归属判定切定义级比较（防符号宿主段与声明侧 ExtendsType 形态差
  误伤）。
- **② kwargs 体内视角闭环**：体内视角 `Array\<String\>` →
  `Array\<Pair\<String, T\>\>`（名+值对，§7.1 ABI 对齐——`.array<T>` 即
  `Array\<T\>` 投影）；bootstrap `Array\<T\>` 补 `getAtIndex`/`setAtIndex`
  operator（索引绑定内建目标，P4b 直发 §13.6 不走 invoke）；P4a variadic
  索引装箱/拆箱物化——ABI 元素类型设施组（vargs → Any / kwargs →
  Pair\<String, Any\>，IsNamedVariadic 先判（named 双标记同置））：读形态
  Type 覆盖 + 外包拆箱 cast、写形态装箱、复合赋值剥壳物化贯通。
- **③ 简单赋值求值序对齐**：编译器确定为与复合赋值同规则（接收者先、
  右值后——AssignmentEmitter receiver/index 物化前移）；SYNTAX §13.2 补
  UB 句（使用者不应假设该顺序，依赖即未定义行为）。
- **④ 前端三件套**：`>` 系列/复合赋值重组统一相邻性校验（`a > = b`
  不再合并）；科学计数法修复（LiteralParserLayer 状态机吸收指数——
  `3.14e-5`/`2e3`/`1.5e3f` 合法，`3.14e-`/`3.14e+x` 报完整已拼内容；
  SYNTAX §3.3 增补）；一元 `+` 删除（§13.2 表本就只有一元 `-`）。
- **⑤ P1/P2 六项规则补齐**：方法判重键加泛型元数（`foo(i32)` 与
  `foo\<T\>(i32)` 共存）；interface 字段禁止；重复 implements 定义级
  去重；static operator 禁止；具名 import 同名修正（失效跳过 + 双有效
  歧义 + 同路径豁免）；显式泛型实参拦截全可变包候选（§4.3）。
- **测试**：+94（2817 → 2911）——BinderTests.KwView 9 + BilVerifierTests
  TypeCompat 8 + variadic 索引 20 + DeclarationCollector/Resolver 38 +
  Overloads 3 + 前端 17；既有黄金 1 处预期更新（索引读写求值序）。
- **遗留登记**：混合泛型形态显式实参个数（`f\<T, TArgs...\>` 按全列表
  匹配 vs SYNTAX §4.2「泛型可变参数除外」字面出入）归技术债 #25。

### 2026-08-05 · M77 S11 enum case 全链 + init 映射赋值合成

> **S11 施工首项（enum case 先行，M75 已定稿 BIL 侧）**：SYNTAX §12/§9.3 +
> RUNTIME §16 + BIL §8.5/§12.3/§14.3 全兑现——enum case 端到端出合法 BIL。
> 三阶段串行施工（P1+P2 结构级 → P3 → P4）+ 一项既有缺口（§9.3 映射
> 赋值）裁决落地。规范零修订（BIL 三处定稿 M75 已备）。

- **P1+P2 结构级**：`EnumCaseSymbol`（Owner/Discriminant（long?，null=auto）+
  ResolvedInit/HoleParameters 两模板槽——首例 P3 写符号：声明侧元数据且
  P4b §8.5 必须消费）+ `TypeSymbol.Cases` 表 + `PrintCase`
  （`com.example::RequestResult.Failed`）；P1 建壳 + 重名防御；P2 新阶段
  `EnumCaseResolver`（洞独占性（`_` 必须独占实参位置，switch pattern `_`
  子树排除）+ case 名复核 + 判别值落定）。case → init 模板绑定按架构决策
  放 P3 声明点（Resolution 无表达式绑定能力，仿 S8d 参数默认值先例）。
- **P3**：BindingDriver 阶段 1.5 声明点模板绑定（结构过滤 → 固定实参绑定
  与适用性决胜 → 洞 pub 规则（§12.2）→ 落定符号；无显式 init 零实参 case
  走默认零参构造，HoleParameters 空列表为成功标记；泛型 enum 归口）；
  `BoundEnumCaseExpression{Case, Arguments（规范序洞实参）, Type=Owner}` +
  `BoundTypeCheckExpression` 增 IsCase Kind 与 Case 第三槽（ConditionFacts
  只认 Is+TargetType，天然不触发 smart cast §12.3）；使用侧三形态——裸
  `.Success` expectedType 上下文推断、`.Failed(404)` 底座+Call 后缀特判
  （PathVisitor expectedType 通道打通 + 洞实参绑定）、`is .Case` 解归口；
  switch `(_ is .Case)` pattern 通道自动可用，值匹配保持常量限定，
  `new EnumType(...)` 永久规则措辞（§12.2）。
- **P4**：Lowered 两节点（恒等 + 洞实参按洞签名 cast 物化——§14.3 严格
  匹配落点 P4a）；声明段遍历 Cases 发 `BilCaseDeclaration`（洞签名投影 +
  显式判别值登记 i32 标量资源/auto 发 auto——与 M75 S11Module 逐点一致）；
  `NewCaseInstruction`/`IsCaseInstruction` 值发射（switch pattern 降级路径
  自动贯通）。
- **init 映射赋值合成（§9.3 落地缺口，既有 bug 两症状）**：无体 init 无
  fn 定义（§21.2 拒绝落盘，实测复现）+ 有体 init 映射赋值从未合成
  （stdlib core::Pair 空体 init 字段从未写入，潜伏语义 bug）。
  ParameterSymbol 加 `MappedField` 槽（P2 两分支回写——显式类型分支此前
  完全不做字段检查）；BindingDriver 合成（无体产 BoundFunctionBody + 有体
  前插，直接构造 bound 节点，Syntax 回指声明节点）；LocalSymbolEmitters
  删除 enum 无体 init 跳过分支——声明 + fn 定义统一发射，`_ -> field`
  映射保留 BIL 供 S14 VM case 入口消费。
- **测试**：+118（2699 → 2817）——DeclarationCollector 反转 6 +
  DeclarationResolver 18 + Binder EnumCases 48/InitMappingSynthesis 9 +
  Lowerer EnumCases 15 + BilEmitter 22（含 CLI 冒烟五样例）；套件数不变
  （43）。
- **S11 剩余**：wrapper place 绑定与只读禁令（PathVisitors Colon 归口）→
  ext 收尾 + 局部访问器解归口 → proxy 烘焙 lowering（specific → wildcard →
  `call???` 降级）→ 派发链诊断工具；泛型 enum case 归口待后续。

### 2026-08-05 · M76 全仓库 review + 39 项 bug 修复批次

> **用户决策**：不推进语言特性，先回补质量。六模块并行 review
> （Semantic/Binding、Resolution+Symbols、Lowering、Bil、前端、Core/Tests）
> 出 30+ 确认发现（多数经 review 实测复现），两波九组并行修复 + 主代理收口。
> 全部修复带回归测试；无既有测试失败（三处既有断言按修正后行为更新：
> 泛型定义裸名构造诊断提前至 NameResolver、具名包结果类型契约、CommandLine
> 互斥表）。

- **Bil 验证器 5 误报修复**（合法程序拒绝落盘方向，与「零误报优先」直接
  冲突）：namespaced 全局函数调用误吞 receiver（owner `::` 结尾排除）；
  invoke 实参比对按 §7.2 调用序重写（`.this → 固定泛型 → 泛型包 → 普通 →
  .vargs → .kwargs`，普通+包混合形态）；loop.rev DA 按 §16.4 body 至少一次；
  try-finally 无 catch 空 catch 表判终止；TypeDeclarations 反查索引键加元数
  （S10 同名不同元数共存互不遮蔽，MemberEntries.OwnerType 同步改反查键）。
- **P1/P2/Symbols 7 修复**：架构级——构造类型 `BaseType` 「创建即代入 +
  InheritanceResolver 后统一回填」（`SymbolGraph.Substitute` 单源上移 +
  `BackfillConstructedBaseTypes`；先入表再代入防自引用重入；CreatesCycle 改
  `ConstructedFrom ?? t` 定义级比较），修复泛型基类两跳断链全部症状（成员
  查找误拒/循环继承漏报/抽象未实现漏报/代入泄漏——stdlib 形态恰好绕开故
  全绿无覆盖）；TypeReferenceResolver 与 InheritanceResolver 阶段换序（init
  映射 FindField 可达继承的内建字段，确认无反向依赖）；ext 注册判重；
  override 泛型元数；命名空间非类型落袋拦截 + 裸名命中泛型定义报元数；
  ext native 视同成员 static 闸门。
- **Lowering 7 修复**：?. Access 降级前置语句收进 thenBlock（miscompile——
  §3.4「receiver 为空则整体不求值」）；Nullable\<泛型参数\> null 资源
  §7.5 canonical 投影；IsSideEffectFree 收紧（索引恒非纯 + 字段按
  Field.Getter——§13.2 单次求值）；variadic 引用 Type 透传 P3 定型；variadic
  写映射统一（`EmittingFacility.ValueVariableName`）；具名包结果类型对齐
  .kwargs 契约；ext 字段修饰符；LoweredLoop.Enumerator 死代码删除。
- **前端 4 修复**：多行字符串引号串紧跟转义的内容顺序错乱（反斜杠分支补
  `FlushQuoteRun()`——本次 review 唯一静默数据腐蚀 bug）；多行插值首段
  span 修正移 PushToken 后（单行串先例）；访问器尾随游离修饰符报错；
  JSONL 反序列化落位类型校验（契约内错误类型）。
- **Core/Tests 3 修复**：LoggerTests 保存/还原 CLI 日志状态（`test --all
  --log-to` 不再被截断）；--sema-only ↔ --emit-bil 互斥；--dump-ast/
  --emit-bil 输出异常友好报错。
- **P3 流分析 6 修复**（S8b smart cast 收官后盲区，三个不 sound 收窄
  复活——运行时 CastException 级）：无 else 非 guard 合并改纯交集；循环
  出口恢复后 ClearRoot；TryVisitor 收窄快照/分支恢复/出口合并（finally
  不参与交集，finally 体赋值根统一 ClearRoot）；null 字面量非 Nullable
  上下文落诊断（§3.4，与 P4「null 定型 Nullable\<T\>」契约对齐；泛型参数
  放行）；值块 GuaranteesValueReturn 补裸 return；return@ 收集下钻表达式
  子树。
- **P3 调用/路径/闸门 13 修复**（S9 泛化系列收官后盲区）：复合赋值 place
  剥 SmartCast 壳；写模式索引宿主代入（`SymbolLookup.SubstituteForReceiver`
  设施）；Type.instanceMethod() 补 this 前链检查（含接口闭包）；泛型参数
  null 判等；裸名调用宿主代入；可变参数链头 Array\<元素\> 包装；索引复合
  赋值 set 元素形参校验；显式泛型实参访问控制 + 约束逐候选判定；泛型
  backing 访问器四处（value 别名/自动 getter 合成/setter 隐含赋值/return
  检查口径）；async 闸门 2 包逐元素 + 闸门 5 GenericPack 推导类型。
- **§21.8 init 豁免 + const/var 开闸**：init 方法体内写实例 const 字段
  放行（对齐 P3 `ConstFieldRules` 实际边界——实例写入 + fn 声明 init
  修饰符，静态不豁免）；EmitFieldDeclaration 发射 const/var（§8.3 表序：
  访问 → const/var → ext）；`BIL_STANDARD.md` §21.8 文本同步。
- **主代理收口**：OverloadResolution 非显式路径统一走 ViewOf 宿主代入
  （非泛型方法签名引用宿主泛型参数的代入）；P4b AssignmentEmitter place
  剥 LoweredCastExpression 壳 + P4a 复合赋值写回值按 place 声明类型物化
  cast（§6.5）——收窄区域内复合赋值端到端出合法 BIL。
- **测试**：+202（2497 → 2699）——BinderTests 新 FlowFixes/CallFixes/
  GateFixes 三 partial 文件、BilVerifierTests §21 逐类用例与 §21.8 init
  豁免手工模块、BilEmitterTests.Fixes/LowererTests.Fixes 两新 partial、
  DeclarationResolverTests 构造基类回填组、前端各套件增补；套件数不变
  （43），新用例全部挂现有套件。

### 2026-08-05 · M74 S10 core.latte 载入机制 + stdlib 扩充（用户决策四件套）

> **ROADMAP S10**（载入机制本体早已在 S6 落地；本里程碑完成 stdlib 扩充与
> bootstrap/core.latte 边界定稿）。四项用户决策全部落地：
> ① 类型名唯一性按「名 + 泛型元数」判定；② coroutine 运行时面 Latte 自举 +
> 最小 native API + native 返回类型放宽；③ 异常子类清单 + message 挂根 +
> 子类自持 init + toString 不覆写；④ P3 async 调用返回类型改写提前。
> 兼作前端常驻回归测试。

- **P1/P2 元数区分**：`DeclarationCollector` 重复检测改「名 + 泛型参数个数」
  （`scope.Types.Any(t => t.Name == name && t.GenericParameters.Count == arity)`）；
  `NameResolver` 新增 `FindTypeIn(types, name, arity)`——arity >= 0 时优先精确
  元数匹配、回退同名任意（带实参元数不匹配者落入 ApplyTypeArguments 既有
  诊断；裸名只有泛型定义者回退定义本身，行为不回归）；首段/末段按该段泛型
  实参个数分流（单段路径 arity = generics.Count，多段路径首段与中间段 -1
  不筛）；`Task`（非泛型）与 `Task\<TResult\>`（泛型）合法共存。
- **bootstrap 异常根**：`BootstrapSymbols.Exception` 程序化携带 protected
  `message: String` 字段 + `pub native getMessage(): String`（lib/symbol
  latte_rt，VM S14 实现；构造顺序修正——String 属性初始化后才可引用，置于
   toString 机制块之后）。子类 init 直接赋值继承字段（当时尚无 super；M95 后
   可直接赋值或可选调用 super(...)，
RangeEnumerator 先例；参数名避开字段名防遮蔽）。
- **stdlib 三新文件**（全部无 Latte 函数体，最小化 BIL 资源基线变动）：
  `core/coroutine.latte`（9 个 shared abstract 类型面 + `sleep` native 全局
  函数 + PollingAlarm.isReady abstract 实例方法——native 不能实例方法，
  以 abstract 表达运行时实现面）；`core/exceptions.latte`（四子类
  `: core.Exception`，层级 RuntimeException 为基）；`core/disposable.latte`
  （`pub interface IDisposable { func dispose() }`）。
- **native 返回类型放宽**（SYNTAX §4.6）：基本类型或用户声明的引用类型
  （class/interface，含构造类型）；参数仍限基本类型；FFI ABI 归 Middleware。
  P2 `NativeDeclarationChecker` 返回检查同步（消息更新）。
- **P3 async 返回类型改写**（SYNTAX §4.5 表兑现）：`CallFacility.AsyncResultType`
  ——调用点类型 = `core.coroutine.Task\<TResult\>` 构造 / 非泛型 `Task`；
  值位置 void 检查改 `binding.ResultType == null`（async 无结果调用可作值）；
  语句位置仍 BoundCallStatement（fire-and-forget）；泛型/实例/裸名调用四形态
  全接；await 仍 S13 归口。
- **P4b**：方法声明 §8.4 `async` 修饰符发射；语句位置 async 调用发 `invoke`
  产 Task 丢弃（非 invoke.noret，§15.2）；BilVerifier 类型判重键加泛型元数、
  预定义符号表补 `core::Exception$getMessage()@.string` 与
  `core::Exception#message@.string`、§21.3 async invoke 结果形态校验
  （`core.coroutine::Task\<TResult\>` / `core.coroutine::Task`）与
  invoke.noret 豁免。
- **规范定稿**：SYNTAX §4.6（native 返回放宽）、§8.1（异常层级/message 面/
  子类自持 init）、§15.3（stdlib 六源清单 + bootstrap/core.latte 边界）；
  BIL §15.2 既有 async 结果形态落实现。
- **测试**：StdlibSourcesTests 三源→六源（数量/sourceName/三个新文件结构
  断言）+ Binder 新 TestAsyncResultTypes 7 用例（值/标注/无结果/语句/
  普通/泛型/实例）+ DeclarationCollectorTests TestArityDistinction 4 用例 +
  DeclarationResolverTests TestArityLookup 5 用例 + 新 BilEmitterTests.S10.cs
  3 组端到端（异常 throw/catch/getMessage/catch-table、IDisposable 实现、
  async Task 声明与 invoke 形态）+ native 返回放宽用例更新（Object/接口正例、
  struct 负例）。43 套件 2467/2467 + fuzz 6000 + 语义 fuzz 3000 全绿；
  build 0 错误 0 警告；CLI `--emit-bil` 端到端样例（用户异常 + async Task）
  过 BilVerifier 正常落盘。

### 2026-08-05 · M66 S8f castTo/castFrom 名字分析 + async 边界五项闸门（纯 P3）

> **S8 收官步**（ROADMAP S8f，SYNTAX §3.5/§4.5，BIL §12.1 语义第 1、2 条）：
> 转换运算符声明侧形状检查 + 使用点名字分析（源 castTo → 目标 castFrom →
> 内建三级优先）+ async 五项闸门（声明侧 2/3/5、调用点 1/2、lambda 捕获 4）。
> 纯 P3 步无 P4 面：转换分析产物仅记录在 Bound 节点，P4 仍发 `cast`
> （BIL §12.1 语义含 castTo/castFrom，运行时自行分派）。

- **P1**：`MethodSymbol.IsAsync` 标记位（建壳读修饰符即定，检查点分派依据；
  init/operator 置位由 P2 拒绝）。
- **P2**：新阶段 `Resolution/ConversionOperatorChecker.cs`（castTo/castFrom
  声明形状：castTo 必须零参数、castFrom 必须恰一参数、两者都必须声明返回
  类型——形状违反 = 名字分析恒不可选中的死声明，声明处拒绝）+ 新阶段
  `Resolution/AsyncGateChecker.cs`（声明侧闸门 2/3/5：参数与返回类型必须
  IsSharedSafe、泛型参数约束边界必须共享安全——具化泛型下 typeid 与实际值
  一同跨边界，约束界非共享安全 ⇒ 一切调用都违反；ErrorType 毒化静默、
  void 返回与无约束泛型参数合法；随附 async 修饰符合法性收口——init/
  operator/类型声明上的 async 拒绝，ModifierChecker 只拦了字段）。
  阶段序：AsyncGateChecker 置于 GenericConstraintChecker 之后（闸门 5 依赖
  约束边界已解析，初版放前导致约束界检查静默失效，测试暴露）。
- **P3 名字分析**：`BoundCastExpression.Conversion: MethodSymbol?` 新槽
  （null = 内建兜底）；`SymbolLookup.FindConversionOperator` 核心查询——
  沿宿主 BaseType 链（含 ext 注册成员）找实例 operator，适用判定 = 至多
  单泛型参数 G 时把签名中的 G 代入后与目标匹配（castTo：0 参、返回类型
  代入后 == 目标；castFrom：1 参、参数类型代入后 == 源类型且返回类型
  == 目标，构造目标允许 == 其泛型定义；宿主泛型参数不可代入按不适用）；
  多泛型参数/宿主泛型参数（S9 使用侧未落地）按不适用回退内建，不落诊断。
  CastVisitor 按转换优先级解析：castTo 适用 → castFrom 兜底 → 内建；
  as? 同分析（结果类型仍 Nullable\<T\>）。
- **P3 async 调用点闸门 1/2**：新 `Binding/AsyncGates.cs` 后置遍历（函数体
  绑完后对 BoundTree 的完整语句/表达式遍历，单一落点覆盖全部调用形态——
  BoundCallExpression/BoundCallStatement/BoundInstanceCallExpression）：
  async 调用 receiver 与每个实际实参必须 IsSharedSafe。闸门 2 调用点检查
  为防御性兜底（声明侧已收口——可赋值 ⇒ 必共享安全：shared 单向传染保证
  子类型同标，分析侧证明调用点实参不可违反）。闸门 5 实际实参检查归 S9
  （泛型调用使用侧未落地，无实参可查）。访问器体同挂（getter 可调 async）。
- **P3 async lambda 捕获闸门 4**：新 `Binding/Visitors/LambdaVisitors.cs`
  （LambdaVisitor）+ Dispatchers 注册——lambda 绑定归 S13，捕获分析为 AST
  级粗粒度扫描：收集体内全部裸路径头名，排除 lambda 自身形参与体内任意
  嵌套深度的局部声明名/嵌套 lambda 形参（内层作用域名不算外层捕获），
  剩余名经外层词法作用域链（局部）或宿主形参表（参数）解析，命中即捕获
  并检查共享安全；随后照常落 S13 归口诊断（"P3: lambda expressions are
  not supported yet (S13)"，替代原泛型 unsupported-kind 消息）。
- **测试**：DeclarationResolverTests 新 TestConversionOperators/
  TestAsyncDeclarationGates 两组（合法形态 + 逐条形状/闸门违反 + 普通函数
  同名不检查 + async init/operator/类型声明拒绝，CheckP2Error 双断言）；
  BinderTests 新 partial `BinderTests.AsyncConversions.cs`（TestConversionOperators
  ——castTo 非泛型/泛型两形态、castFrom 兜底、优先级 castTo 胜出、不适用
  回退内建（返回不匹配/参数不匹配）、as? 记录转换、无转换 null；
  TestAsyncGates——闸门 1 receiver 违反与 shared 正例、调用点闸门 2 合法
  正例、闸门 4 捕获局部/宿主参数违反与 String 正例、体内声明名不判捕获）。
  43 套件 2342/2342 + fuzz 6000 + 语义 fuzz 3000 全绿（+51 用例），build
  0 错误 0 警告；CLI 端到端实测（castFrom/castTo 样例 --emit-bil 过
  BilVerifier 正常落盘——use 点 `cast type(Celsius)` 不变、operator(castFrom)/
  operator(castTo) 声明就位；async 违规样例 --sema-only 闸门 1/4 诊断就位）。

### 2026-08-05 · M65 函数级 Context 组件化拆分

> 用户决策的纯重构批次：三树函数级可变状态平板巨石 narrow 化为
> 「组合根 + 职责组件类」，兑现 M55 context 方言预留（「按真实隔离需求
> 拉组件、禁止切多个独立状态对象」）。零行为变化、测试零改动。

- **动机与约束**：BindContext（14 公有成员）/LowerContext（11）/
  EmitContext（8）是全暴露平板——任何 visitor/设施可摸任意状态，
  新特性持续塞字段（AccessorField/SeqLabels 等）。递归透传约束：
  visitor 经分派器互调必须透传全能力 ctx，单 TContext 协议下 visitor
  级窄化数学上不可达（需求并集 = 全能力）——故窄化落点为
  「组件类即方言」（visitor 访问路径收窄为组件边界）+ 设施层签名收窄
  （非递归静态设施，真编译期边界）。
- **Bind 侧**：新 `Semantic/Binding/BindFunctionFrame.cs`（只读函数帧——
  Method/FileCtx/DeclaringType/IsDefaultValueContext 构造一次性赋值 +
  HasThis/CanAccess 计算）、`AccessorBodyState.cs`（Field/IsSetter 只读
  消费 + Set 仅 BindingDriver 调）、`BindLabelState.cs`（四栈私有化：
  值块（压栈自记 LoopDepth）/循环/switch 占位/seq 标签（自记两深度），
  条目元组封装为命名 readonly struct；FindValueBlock/FindLoop/
  CurrentSelector/FindSeqLabel + LoopDepth/ValueBlockDepth——return@
  隔层拦截比较基准与 break/continue 命中查找自 visitor 逐字收编，
  诊断仍留原 visitor 落袋）；BindContext 变薄为 Frame/Accessor/Labels/
  Flow/Locals 五成员组合根（构造签名不变，三处 new 零改动）；
  `IFlowContext.cs` 删除（无消费者——组件类型取代接口方言，头注释
  设计说明移入 BindContext）。
- **设施窄化**（真编译期边界）：MemberLookup/TypeReferences/
  ConstFieldRules 三方法/ConditionFacts 全链/NarrowKey.TryFromFieldAccess
  的 BindContext 实参收窄为 BindFunctionFrame；PathFacility.ApplyNarrowing
  收窄为 FlowState、SwitchStatementVisitor.ApplyCaseNarrowing 收窄为
  (BindFunctionFrame, FlowState)；跨组件消费（PathFacility.BindPath、
  OverloadResolution）保持组合根。
- **Lower 侧**：新 `SynthLocalFactory.cs`（.sN/.bN 独立计数统一登记——
  顺序即 .vars 发射顺序，实现逐字迁移 + ReferenceTo 静态随迁 27 调用点）、
  `LowerOutputState.cs`（前置语句机制封装：Push 双形态——建空列表/压入
  调用方已有列表（seq 编织与 pattern 链需同一引用）/Pop/Current/Add）、
  `LowerTargetState.cs`（五映射栈私有化 + Find* 命中查找收编——未命中
  CompilerInternalException 文本逐字留原 Facility）；LowerContext 变薄为
  Method/TransformFailed + Synth/Output/Targets 五成员。
- **Emit 侧**：新 `TempVarTable.cs`（.tN 工厂自 EmittingFacility 原样收编，
  ValueEmitters 13 处改经组件）、`BlockIdAllocator.cs`（NextIf/NextLoop/
  NextSwitch/NextSeq/NextTry——后缀自增语义与原 ctx.XxxCount++ 取值一致，
  编号次序逐字节保真）；EmitContext 变薄为 Function + Temps/BlockIds。
- **验证**：dotnet build 0 错误 0 警告；43 套件 2291/2291 + fuzz 6000 +
  语义 fuzz 3000 全绿（BIL 黄金文本逐字节一致——块 id 与 .vars 次序
  敏感点经 BilEmitterTests/BilWriterTests 验证）；Tests/ 零改动。

### 2026-08-05 · M64 hint 提示指令 + BIL 全文重编号

> 用户决策的规范定稿与落地批次：BIL 新增 `hint` 提示指令（§18），
> BIL_STANDARD 全文重编号，两份过时文档移除。

- **规范（BIL_STANDARD.md）**：新增 §18 提示指令章——`hint res(RESOURCE_ID)`，
  仅 block 内出现；RESOURCE_ID 必须引用本模块 `string` 资源，内容是一段
  JSON 文本（schema 留白，生产/消费方自行约定）；纯位置标记——无结果变量、
  不读写变量、不参与 DA、不是终结指令；核心不变量「删除全部 hint 后
  §22.2 可观察行为完全不变」；VM no-op；Middleware 可用可忽略，内容不得
  影响可观察语义、解析失败必须忽略而非拒绝编译。§18–§26 重编号为
  §19–§27（Resources/黄金示例/验证器/VM/Middleware 边界/职责关系/Legacy/
  扩展两章），§4.2 补 hint 为资源第三消费方，§26 三处「未来将要加入的
  hint」改现在时，§27 扩展清单移除 hint（仅剩 tail call）。
- **代码**：`Bil/BilHintInstruction.cs`（HintInstruction 强类型模型，
  res 操作数持对象引用，指令自渲染 `hint res(R_X)`）；BilVerifier 接入——
  ClassifyVariables 零读写（DA/控制流穿透）+ §21.2 资源归属本模块 +
  §21.3 string 标量限定；BilVerifier 错误码字面量 "20.x"→"21.x"（114 处）。
- **全仓库引用同步**（35+ 文件）：Bil/、Lowering/、Tests/、Semantic/ 代码
  注释与 AGENTS.md、PROGRESS_REPORT、SEMANTIC_ARCHITECTURE、
  SEMANTIC_ROADMAP、SYNTAX、RUNTIME 的 BIL §18+ 引用统一 +1；
  SYNTAX/RUNTIME 自指引用与「§20.1–20.8」区间尾部等逐点甄别处理。
- **文档移除**：VISITOR_REWRITE.md 与 SMART_CAST_DESIGN.md 删除（用户决策，
  内容已吸收——visitor 协议见 SEMANTIC_ARCHITECTURE §6 内联摘要与各
  visitor 基类文件头，smart cast 规则见 SYNTAX §3.5）；活文档与代码注释
  引用全部改写，本报告历史段落中的提及保留为编年史。
- **测试**：BilWriterTests §18 hint 黄金（自足模块 + 验证器零错误）+
  BilVerifierTests 三用例（string 资源正例 / 非 string 资源 / 模块外资源）。
  全量 2291/2291 + fuzz 6000 + 语义 fuzz 3000（43 套件），0 新警告。

### 2026-08-04 · M63 S8e 访问控制 + getter/setter + override 检查

> 路线图 S8e 三项一次落地（计划批准方案 A：字段/全局两类访问器全链路、
> 局部归口 S11、接口默认实现隐式继承、P4 声明段小开闸）。

**规范定稿**（SYNTAX 四处）：

- §16.1 可见性判定规则：private 顶层声明 = 同文件可见、private 成员 =
  声明类型及其嵌套类型（递归）内可见；`protected` = 子类体内（基类链
  可达）或同包（同命名空间驻留实例，不含子命名空间）；`internal`
  单编译单元恒可见；接口成员默认 `pub`（接口即契约）；
  bootstrap 硬编码符号统一 Public（使用点判定以符号级别为准）。
- §9.4.1 访问器绑定语义：访问器修饰符白名单仅访问级别；可见性 =
  显式修饰 ?? 字段声明级别 ?? private；backing 形态（`value: _`）体内
  `value` 为 backing 别名（getter 只读/setter 可写，进入时隐含
  `backing = value`）；自动访问器（无体）编译器合成实现（getter =
  `return value`、setter = 空体），无体 computed 拒绝；`const` + setter
  拒绝；仅有 get 不可写/仅有 set 不可读；带访问器字段不参与 smart cast
  收窄；栈上局部访问器归 S11。
- §9.2.1 `override` 配套：`open`/`abstract`/`override` 仅普通成员方法
  （字段/init/operator/getter/setter/static 禁，接口内 open/abstract 冗余）；
  override 必须在基类链或接口表找到签名匹配（名 + 参数类型序列 +
  返回类型严格相等）的 open/abstract 方法或接口成员；禁止静默隐藏；
  abstract 方法必须在 abstract 类内且不能有体；接口外无体方法必须
  abstract 或 native；具体类必须实现继承链全部 abstract 成员与无体
  接口成员；`new` abstract 类拒绝。
- §11 注记：显式委托语法 `-> InterfaceName` 归后续，带默认实现的
  接口成员当前隐式继承。

**符号与 P1**：`MethodSymbol.IsOpen/IsAbstract/IsOverride/HasBody` +
`FieldSymbol.Getter/Setter/HasBackingStorage` 六槽 + `SemanticSymbol.SourceFile`
文件身份（RootASTNode 引用；构造类型随定义传播 Accessibility/SourceFile）；
P1 访问器壳收集（Kind=Getter/Setter、Name=字段名、宿主/静态同字段、
setter 带唯一 value 参数壳）——访问器符号**不进容器 Methods 表**
（避免污染按名查找），P3 体枚举经字段反查、P4 声明发射由字段槽驱动；
`FileContext` 增 `File` 属性（「同文件可见」判定身份）。

**P2**（`Semantic/Resolution/`）：`AccessChecker` 共享设施（`Semantic/`
根，P2/P3 同一份判定；统一诊断措辞 `'X' is inaccessible due to its
accessibility level`）+ 声明侧接入（TypeReferenceResolver 字段/返回/参数、
InheritanceResolver 基类/接口、GenericConstraintChecker 约束）+
`AccessorChecker`（白名单/重复互斥/可见性落定/const+set/无体 computed/
签名回填 getter.ReturnType 与 setter value 参数 = 字段类型）+
`OverrideChecker`（覆写关系 + abstract 位置与体 + 具体类待实现成员——
构造宿主签名经 `ResolveEnvironment.Substitute` 按定义 → 构造代入实参
（stdlib 双接口协议 `IEnumerator\<i32\>` 即依赖此路径）；内建类型参与
覆写关系（Object.toString open 默认实现是合法覆写目标）但不产待实现
成员）+ ModifierChecker 三标记位置合法性 + ExtensionRegistrar 访问器
随字段随迁宿主。

**P3**（`Semantic/Binding/`）：使用点访问控制——`BindContext.CanAccess`
统一入口（useHost = DeclaringType 词法宿主，与 P2 口径一致；ext 方法
不获目标私有访问权），接入实例/静态/裸名调用、实例/全局字段、init、
索引 operator、函数体内类型引用（TypeReferences 唯一收口点；不落袋
试探的 is/typeOf 不接）；**候选过滤先于 ranking**（不可见候选不参与
重载解析，全不可见报首个候选）；编译器内部机制（for 协议/插值
toString/解构/默认值）不接。访问器读写检查——读 getter 存在性 +
 可见性（`'x' has no getter`）、写 `ConstFieldRules.CheckWritable`
（赋值与复合赋值两处 place 共用）；Bound 节点形态完全不变（BIL
get.field/set.field 语义承载访问器，§8.3/§8.4）。访问器体绑定——
BindingDriver 增 visitAccessors 第二回调走普通函数体同一通道；
`value` 别名在 BindPath 首段作用域链之前拦截（实例补 this/静态全局
直引；getter 体内赋值拒绝）；backing setter 体首隐含赋值与自动
访问器体合成全部复用既有 Bound 节点（P4a 恒等降级零新增）；
`ConstFieldRules.IsNarrowable` 排除带访问器字段（兑现 S8b 留口）；
局部访问器归口诊断（`P3: local variable accessors are not supported
yet (S11)`）后按普通局部降级绑定不中断。

**P4 声明段开闸**（BIL §8.3/§8.4 已定稿形态，无访问器样例黄金逐字节
一致）：`BilAccessorModifier`（零 Semantic 依赖字符串身份，仿
BilOperatorModifier）+ 访问器 `.method getter(FIELD)/setter(FIELD)`
声明（字段槽驱动、get→set 紧随字段声明、命名空间全局与类型字段两
处覆盖）+ 字段 `backing`/`computed`/`readable`/`writable`/
`compiler-generated` 修饰 + Regular 方法 `override`/`abstract` 投影；
fn 定义经 EmittingDriver 数据驱动零改动（setter `.args` 含 value、
实例访问器 `.this`、void 补 ret）；`BilVerifier` §21.8 增补（访问器
修饰合法性（FIELD 可解析 + getter/setter 形态一致）/backing×computed
互斥/setter 签名特判）+ 命名空间宿主前缀误判修复（验证器
Symbols/Types 两处——兑现 FieldOwnerRef 注释预留点）。

**测试**：+110 用例（新 `Tests/BinderTests.Access.cs` 三组 47——
TestAccessControl 多文件 10 例/TestAccessors 22 例（读写绑定
BoundDescribe 黄金 + 体合成断言 + smart cast 对照 + 局部归口）/
TestOverride 15 例；DeclarationResolver +31（访问器 P2/修饰符位置/
声明侧访问控制）；BilEmitter 新 partial +24（访问器发射 CheckBilValid
+ 形状黄金 + 结构断言、override/abstract 投影）；BilVerifier +8
（§21.8 负例））。**同批修复测试暴露的真 bug**：`SymbolLookup.
FindInstanceMethods` 沿基类链收集时 override 与被覆写基类成员同进
候选池致同签名平局歧义（`s.area()` ambiguous）——override 遮蔽去重
（签名严格相等口径同 OverrideChecker）。既有夹具五处按新规范收紧
修正（跨文件 priv 类型引用补 pub、接口实现补 override ×2、私有
成员跨类型调用补 pub ×7——均为真越界）。43 套件 2286/2286 +
fuzz 6000 + 语义 fuzz 3000 全绿，build 0 错误 0 警告，CLI
`--emit-bil` 访问器样例（backing/computed/自动/全局/override/abstract）
端到端验证落盘。

### 2026-08-04 · M62 巨石拆解批次：DeclarationResolver visitor 化 + Lexer/测试三套件分文件

> 用户决策的纯重构批次（零行为变化、零用例增删、无新特性）：
> 继 M55 三树 visitor 化之后，清除仓库剩余的文件级巨石——
> 最大生产文件（1327 行）与最大测试文件（2832 行）全部拆解。

- **DeclarationResolver visitor 化**（1327 行 → 瘦入口 55 行 +
  `Semantic/Resolution/` 12 文件）：协议同 VISITOR_REWRITE §3
  （静态 Visit 唯一入口 + Enter/Exit 生命周期 finally 配对），
  落地形态为**阶段级 visitor**——P2 遍历本质是「阶段 × 条目
  平铺」（CollectEntries 唯一 AST 递归，产出 entries/typeEntries/
  entryOfSymbol 三表；其余 12 步全部平铺 foreach），不生搬
  Binder 的 node/scope 深递归签名：`ResolverVisitor` CRTP 基类 +
  `ResolveEnvironment` 只读环境（unit/declarations/NameResolver +
  三表 IReadOnly 暴露 + Error 落袋 + ModifiersOf/FindField/
  Substitute 等共享设施）+ `DeclEntry`（private nested →
  internal）+ `EntryCollector` 骨架收集静态设施 + 九簇阶段
  visitor（Import/TypeReference/Inheritance/Modifier/Native/
  Contagion 三 checker/GenericConstraint/Wrapper 三 resolver）；
  13 步执行顺序与诊断消息逐字保持（71 处 Error 调用计数对齐、
  20 组诊断消息特征模式计数一致）；Enter 唯一落地——native
  参数类型白名单构建上移（NativeDeclarationChecker）；Exit 无
  落地（本 pass 无栈类上下文，符合预期）
- **Lexer/LexerLayers.cs（763 行）→ 每类一文件 11 个**：十个层类
  各自成文件 + 共享转义表 `StringEscape` 单独成文件，对齐
  Parser「每层一文件」惯例；原文件删除（字节级 diff 对账）
- **测试三套件 partial 分文件**（仿 `Bil/BilVerifier` partial
  先例，平铺点号后缀命名）：BinderTests（2832 行，全仓库最大）
  → 主文件（RunAll 35 调用顺序不变 + 四个共享 helper）+ Basics/
  Conditionals/Loops/SwitchThrow/Members/TrySeq/Types/Overloads
  八 partial；BilEmitterTests（1391）→ 主文件 + Basics/
  ControlFlow/Members/TrySeq/Values 五 partial；LowererTests
  （1268）→ 主文件 + 五 partial（FutureBoundStatement/
  FutureLoweredStatement 负例嵌套类随 TestUnsupportedNode(s)
  各归 Basics）
- **验证**：build 0 错误 0 警告；43 套件 2162/2162 + fuzz 6000 +
  语义 fuzz 3000 全绿（纯搬迁，用例零增删；分文件搬运经行数
  守恒/逐字节重组 diff 对账）

### 2026-08-04 · M61 规范定稿兑现：复合赋值单次求值 + 语句 seq 作 return@ 目标

> 用户决策的规范定稿批次：默认构造（§9.3）、循环变量与
> catch/finally(e) 变量一律 const（§7.3/§8）、复合赋值目标单次
> 求值（§13.2 通用规则，含字段与索引）、语句 seq 可作 return@
> 目标（§6.1 明确化）。前两项实现本即规范语义（勾销技术债
> #11/#15①/#17③）；后两项本里程碑落地实现（勾销 #20①/#17②）。
> P3 对复合赋值零改动（纯 P4a 脱糖变化）。

- **复合赋值单次求值**（`Lowering/Rewriters/ExpressionRewriters.cs`
  CompoundAssignmentRewriter 重写）：Target 降级一次后按形态
  分派——实例字段 receiver / 索引 receiver+index 降级产物物化
  合成局部（前置「.sN = expr」赋值，求值序先于右值）；赋值左/
  运算左/表达式位三处复用同一物化节点（Lowered 节点无父链
  不可变，continuation 编织共享先例）；**纯读取直通**——
  IsSideEffectFree 判定（局部/参数/静态字段/字面量/常量/this
  及其链式组合）无副作用目标零物化，既有黄金零变化
- **语句 seq 作 return@ 目标**（三 pass）：P3——BoundSeqStatement
  加 Label（仅显式 named；`_` 默认标签值块专属）+ 施工壳模式
  （体回填）+ 新节点 BoundSeqExitStatement；BindContext.SeqLabels
  标签栈（记录压栈时循环/值块深度）；SeqStatementVisitor 压栈
  绑体；ReturnVisitor return@ 查找扩展（值块栈未命中查 seq 栈：
  必须不携带值 + 隔循环/隔值块拦截）；DA 不做流处理（与 break
  同保守策略，#14② 同族注记）。P4a——新节点
  LoweredSeqExitStatement（纯控制流标记，不产指令）+
  LowerContext.SeqTargets 栈（**所有** seq 降级压栈——无名 seq
  不压会让栈顶指向外层 seq 导致误消费）；SeqStatementRewriter
  手动压栈收集（编织需可变 List），体含 exit 才跑编织（零行为
  变化闸门 ContainsSeqExit）；ValueBlockFacility 三处扩展（exit
  终止判定 + 命中本层消费删除/命中外层保留传播）。P4b 零新增
  （exit 不残留，EmitDispatcher default 防腐）
- **测试**：BinderTests +TestSeqExit（绑定形态 + 带值拒绝/隔循环/
  隔值块/匿名非目标四负例）；LowererTests 复合赋值单次求值两
  用例（索引/字段物化黄金）+ return@outer 编织与嵌套无名 seq
  传播两用例；CLI 端到端冒烟（副作用函数调用计数验证单次求值；
  switch/try/值块内 seq exit；try+finally 部分终止拦截与 S7e
  同路径）
- **验证**：build 0 错误 0 警告；43 套件 2162/2162 + fuzz 6000 +
  语义 fuzz 3000 全绿

### 2026-08-04 · M60 S8d 重载解析 + 默认参数 + 具名参数

> 路线图 S8d 落地（纯 P3，无 P4 面）：重载规则定稿补进 SYNTAX §4.2
> （三步解析 + 平局打破），P3 新设施 `OverloadResolution` 统一承载
> 调用/init/索引读三处候选解析（source-level ranking 唯一落点，
> BIL §3.3——之后各层不再 ranking）；默认参数声明点绑定 + 调用点
> 规范序填充；具名参数重排（M41 既有能力）纳入 ranking 流程。
> BoundCall 产物恒为规范参数序（ARCH §2），P4 零改动。

- **规范定稿**（SYNTAX §4.2 扩写）：实参映射（位置实参按源码序
  占位、具名按形参名归位、重复填充即错误；实参求值序为规范参数
  序而非源码序——与 M41 起绑定产物一致）；默认参数（首个默认值
  之后的形参必须全部携带默认值（P2 声明侧检查）；默认值表达式
  在声明点作用域绑定——看不到形参、无 this、可引用全局可见符号；
  类型必须可赋给形参类型；暂不允许含局部声明（P4 无法物化跨
  函数局部）；调用缺省时填充，每次调用重新求值）；重载解析三步
  ——①结构过滤（个数 ∈ [必填数, 总数]、具名存在、不重复；静默）
  → ②类型适用性（IsAssignable；null 字面量仅 Nullable\<T\> 形参
  适用；静默）→ ③最具体胜出（逐实参形参类型两两比较取唯一
  极大元；平局打破——本次调用填充默认值个数更少者优先；仍平则
  二义错误）。实例方法与 ext 扩展方法同池；泛型方法（S9）与可变
  参数方法调用归口诊断；init 构造同一规则
- **OverloadResolution**（`Semantic/Binding/OverloadResolution.cs`
  新设施）：预处理剔除泛型/可变参数候选（全剔除时各自归口
  诊断）；单候选（含结构过滤后唯一）走 BindArguments 快路径
  （逐实参带目标类型绑定，既有诊断语义不变）；多候选路径——
  TryMapArguments 静默结构映射 → 实参无目标类型预绑（null 字面量
  占位；任一失败即整体失败，避免级联误诊）→ IsApplicable 静默
  类型过滤（形参类型为泛型参数/ErrorType 保守剔除）→ IsBetter
  两两比较取极大元 + 平局打破 → Materialize 落定（预绑实参按
  映射归位 + null 以胜者形参类型定型 + 缺省形参填默认值 Bound
  复用——P4a 每次降级独立合成，「每次调用重新求值」自然成立）
- **默认参数**（P1–P3 协同）：ParameterSymbol 增 DefaultValue/
  IsVariadic/IsNamedVariadic（P1 填充；AST 引用挂符号——Semantic
  → AST 依赖方向合法）；P2 子任务 1 加顺序检查；P3 声明点绑定——
  BindingDriver 分两阶段（①默认值 ②函数体），新
  BindContext.IsDefaultValueContext 隔离（形参引用跳过 +
  HasThis 属性统一三处实例上色判定——默认值上下文视同静态）；
  BindEnvironment.ParameterDefaults **记忆化按需绑定**——前向依赖
  （`f(a = h())` 声明先于 h 且缺省用 h 的默认值）经调用点查表
  递归触发被依赖参数绑定，声明顺序不影响语义；in-flight 集合
  拦截默认值依赖环；失败缓存 null 防重复诊断
- **集成点**：CallVisitors 的 BindCall/BindInstanceMethodCall/
  NewVisitor 三处接 Resolve（BindCallee → ResolveCallee 只返回
  候选集，实例 receiver 补 this 判定后移至落定后；
  MatchSingleCandidate 删除）；PathVisitors 索引读模式接 Resolve
  （多候选按索引实参类型 ranking），写模式保持归口（RHS 类型在
  赋值侧才可知，技术债 #21①）
- **同批修复**：位置实参静默覆盖具名占位（`f(a = 1, 2)` 旧行为
  后者覆盖前者）——统一 Duplicate 诊断；泛型函数声明的
  「must return a value on all code paths」级联（fuzz 观察收口——
  ReturnType 为泛型参数时 return 值绑定必然 S9 归口失败，
  GuaranteesReturn 检查只产噪音，现跳过）
- **测试**：BinderTests 新 TestDefaultParameters（12 用例）+
  TestOverloadResolution（15 用例）——ranking 规则逐条 + 默认/
  具名规范序 BoundDescribe 断言（S8d 验收达成）；两既有用例迁移
  （实参个数不符消息改 Missing argument 精确款；getAtIndex 重载
  归口用例转 ranking 正例——以返回类型差异见证命中版本）；
  语义 fuzz 新套件 SemanticsFuzzTests（注册表 #43，随机重载声明
  + 调用组合，固定种子 3000 用例：编译器零崩溃 + 778 干净用例
  全过 BilVerifier + 诊断双编译逐字一致——零发现编译器 bug）
- **验证**：build 0 错误 0 警告；全量测试全绿
  （`dotnet run -- test --all`）；CLI `--emit-bil` 端到端实测——
  默认值填充/具名跳位/子类 ranking/值块默认值（调用点物化 .sN
  合成局部与 if 块）出合法 BIL（验证器通过）

### 2026-08-03 · M59 S8c 索引访问 + 实例成员完整化

> 路线图 S8c 落地：索引读写（getAtIndex/setAtIndex 运算符）与表达式
> 底座链（`(a+b).c`/`foo().c` 等）三 pass 全通，解开 PathVisitors 现行
> 全部 S8 归口诊断；多参数索引定稿为编译错误并同步 SYNTAX §13.2。

- **P3 索引绑定**（`Semantic/Bound/BoundExpressions.cs` +
  `Semantic/Binding/Visitors/PathVisitors.cs`）：新 Bound 节点
  `BoundIndexExpression`（Receiver/Index/Operator/Type——读形态绑
  getAtIndex（Type = 返回类型），写形态绑 setAtIndex（Type = 元素
  形参类型））；`SymbolLookup.FindInstanceOperators(type, name,
  parameterCount)` 新增（BaseType 链 + ConstructedFrom 回退 +
  Kind/参数个数过滤），原 FindInstanceOperator 以其重写（行为不变）；
  BindIndexAccess——nullable receiver 拒绝、0 候选报
  "does not define an index operator"、多候选按 S8d 重载措辞、
  读复用 CallFacility.BindArguments、写模式定稿诊断
- **P3 实例成员完整化**（PathVisitors 重构）：表达式底座绑定
  （`(a+b).c`/`foo().c`/`new X().c`/`foo()?.bar`）；首段后缀折叠
  FoldSuffixes（Index → BindIndexAccess；Call-on-value 报
  "P3: calling a value is not supported yet (S8)"）；段后缀折叠
  （`.foo()[0]`/`.foo[0]`/`a?.b[0]`）；`this[i]` 读写；容器末段
  成员后缀折叠（`Type.staticField[0]`）；CallVisitors 的
  CallForm.TryGet 收紧——Call 后缀必须在链尾（`foo().c` 由此落入
  「调用结果底座」折叠）
- **P3 赋值 place 扩展**：place switch 加 BoundIndexExpression 分支
  （DeclarationVisitors.cs：无 const 检查/MarkAssigned/收窄失效）；
  复合赋值 place（BinaryVisitors.cs：要求 2 参 setAtIndex 存在 +
  元素类型 intrinsic 检查）
- **定稿结论**：多参数索引 `a[i, j]` 是编译错误（§13.2 签名固定单
  TIndex 参数，诊断经实参个数检查产生）；具名索引实参与普通调用
  同规则（按形参名归位）；SYNTAX §13.2 已同步。附带行为变化：
  多段 `_.x`（switch 占位）从容器路径报错改善为 selector 成员访问；
  `this(...)` 诊断措辞变为 calling-a-value 款
- **P4a**（`Lowering/Lowered/LoweredExpressions.cs` +
  `Rewriters/ExpressionRewriters.cs`）：新 Lowered 节点
  `LoweredIndexExpression`（Receiver/Index，Type 透传，不携带
  Operator——§13.6 指令无符号操作数）；IndexRewriter 恒等降级；
  赋值无需新语句节点（Target 走表达式分派）；复合赋值既有脱糖对
  索引目标自然成立（receiver/index 多重求值，与字段复合既有行为
  一致）
- **P4b**（`Bil/BilDataInstructions.cs` + `Lowering/Emitting/`）：
  新指令 `SetArrayInstruction`（§13.6：Collection/Index/Element，
  "set.array"）；ValueEmitters 加 IndexAccessEmitter（get.array）；
  StatementEmitters 的 AssignmentEmitter 加 LoweredIndexExpression
  分支（set.array，求值序仿 set.field：ELEMENT 先物化）；
  EmitDispatchers 注册；operator 的 §8.4 声明发射 S7c-2 已开闸
  （`$$名` canonical + operator(名) 修饰符），本步零改动
- **BilVerifier**（`Bil/BilVerifier.Types.cs`）：ClassifyVariables 加
  set.array 分类（reads Collection/Index/Element）；GetArray 非
  `.array<T>` 分支从报错替换为 §13.6 严格三元组查询（新
  VerifyIndexOperator 辅助：collection 类型不可解析或 extends 链断
  → 防误报降级通过；链完整则查 `$$getAtIndex`（恰 1 参）/
  `$$setAtIndex`（恰 2 参）——无候选报错、无 §6.4 精确匹配报错
  （禁止隐式转换）、多精确命中报不唯一、get 唯一命中再验
  RESULT ≡ 返回类型）；SetArray 对称分支（.array<T> 时
  ELEMENT ≡ 元素 T）；Flow.cs 无需动
- **测试**：BinderTests 加 TestIndexAccess（12 正例 + 10 负例）；
  LowererTests 加 TestIndexLowering；BilEmitterTests 加
  TestIndexEmission（CheckBilValid + CheckFnShape 黄金 + operator
  声明形态结构断言）；BilVerifierTests 加索引正例 + 四负例
  （手工模块基建 IndexModule/IndexOperator/BothIndexOperators）——
  全部扩既有套件，注册表未动
- **验证**：build 0 错误 0 警告；42 套件全绿
  （`dotnet run -- test --all`）；CLI `--emit-bil` 端到端实测
  （用户类双 operator + 读/写/复合索引 → 合法 BIL，
  get.array/set.array 行形态正确）
- **留口**（技术债 #20）：① 索引复合赋值 receiver/index 多重求值
  （同字段复合既有行为；单次求值需另立脱糖，属语言语义决策）；
  ② 容器中间段带后缀（`ns.Foo().bar` 形态）仍未支持（保留 S8 归口
  诊断）；③ 值调用（`local(0)(1)` 等函数值调用）未支持；④ 读索引
  要求 getAtIndex、写索引要求 setAtIndex（只读/只写索引器按各自
  存在性检查）；⑤ `(a+b).c` 端到端未覆盖——语言尚无用户二元运算符
  重载（P3 未落地），链式形态已由 `cb[0].value`、`makeBag()[9]` 覆盖

### 2026-08-03 · M58 BIL 验证器 BilVerifier + BIL 测试迁移（提前自 S12）

> 用户决策：不推进语言特性（S8c 暂缓），回补质量基建——落地 BIL
> 验证器并把全部 BIL 测试迁移到验证器框架，方便随时检查产出是否
> 正确。`Bil/BilInstructions.cs` 文件头预留的「结构化理解归
> BilVerifier（S12）」提前兑现；验证器只依赖 `Bil/` 目录
> （ARCH §6.3 字符串身份决策不变）。

- **验证器本体**（`Bil/` 五新文件，消费 BilModule 对象模型——
  模型层已保证 opcode/操作数形态与 blk/res 无悬空引用，验证器管
  模型无法表达的检查）：`BilVerifier.cs`（入口 `Verify(BilModule)`
  → `IReadOnlyList<BilVerificationError>`（Rule 取 § 号 + Context
  定位 + Message）+ §21.1 词法语法）+ `BilVerificationContext.cs`
  （模块级资源/类型/成员符号索引、函数级变量类型环境、canonical
  符号解析（方法/字段签名按 <> 深度切分）、类型引用工具、
  预定义符号表）+ `BilVerifier.Symbols.cs`（§21.2 符号——fn↔
  LocalSymbols 一一对应（native/接口/abstract 豁免）、static
  标记一致性、资源类型引用可解析、discriminant 资源登记 +
  §21.7 参数包顺序与签名一致 + §21.8 声明侧修饰符矩阵）+
  `BilVerifier.Types.cs`（§21.3：指令读写变量位置分类唯一表 +
  逐指令类型 switch——严格相等按 §6.4，invoke/new/init 签名匹配，
  继承字段/方法宿主经 IsAssignableTo）+ `BilVerifier.Flow.cs`
  （§21.4 保守 DA + §21.5 控制流——entrypoint 唯一与结构化
  终止、块成员资格、token 作用域、结构环拒绝 + §21.6 breakid
  capability——绑定位唯一、禁止普通读写、continue 不指 switch
  token）。覆盖 §21.1–21.8 静态可判子集（§21.9 VM 语义除外）
- **防误报降级原则**（宁可漏报不可误报）：含 `.generic<` 的
  typeid 位置表达式不做严格匹配；类型兼容剥基名大小写不敏感
  （`.i32` ↔ `core::i32`、`.any` ↔ `core::Any`）；extends/
  implements 链判定（IsAssignableTo）；查不到声明一律降级通过；
  DA 保守（loop/switch/try 子块出口取进入态；loop condition
  按 §21.4 由 judge 赋值建模，进入循环不要求已赋值）
- **预定义符号表**（技术债 #16 收口）：bootstrap 符号
  （core::Any/Object/Exception 等 + Any/Object$toString）经
  CanonicalSymbolPrinter 投影进 BIL 文本但不进 LocalSymbols——
  M38「内建不声明」架构决策不变，可解析性由验证器内建环境闭合
  （预定义类型 23 + 方法 2，仅注入符号集合，不生成声明对象，
  声明侧规则对其降级通过）
- **entrypoint 结构化终止判定**：§9.4「不得落尾」按结构递归——
  全分支 return 的 switch/if/try 之后发射器不补 ret 是 M46–M50
  的既定合法形态，末指令为这些结构且全分支终止即视为终止
- **防腐化**：指令分派双 switch（读写分类 + 类型规则）default
  均报「验证器未覆盖指令类型」——Bil 新增指令子类时强制提醒
  补规则（惯例同 ASTIntegrityValidator 类型审计）
- **测试基建** `Tests/BilTestHarness.cs`：`EmitBilUnit` 全管线
  驱动（自 BilEmitterTests 私有提升共享，与 CLI 管线同序）+
  `CheckBilValid`/`CheckBilInvalid` 验证器断言 + **res 重编号
  形状黄金**（`CheckFnShape`/`CheckResShape`——资源名按出现
  顺序重编号 `res(#0)`/`res(#1)`，消除 stdlib 基线 R_0..R_4
  偏移这一最脆弱点；指令序列/操作数/.tN 编号/block id 仍逐字节
  锁定，回归精度不降级）
- **测试迁移**：新套件 `Tests/BilVerifierTests.cs`（56 用例——
  15 个全管线正例零错误 + 最小手工模块基线 + 24 个 §21 逐类
  负例）；`BilEmitterTests` 全量迁移（157 用例——私有
  RenderFn/RenderFnAllBlocks/RenderResources/EmitUnit 删除，
  全模块黄金改验证器 + 结构断言，fn 级紧凑黄金改形状黄金，
  每个用例前置 CheckBilValid，TestGoldenOutput 的排版锁职责
  归 BilWriterTests）；`BilWriterTests` 黄金保留（排版是本职），
  §20 两自足模块补验证，四个排版抽样用例注明不过验证器；
  TestUnsupportedNodes 夹具修补（手工 MethodSymbol 未经 P1
  收集，验证器正确抓到声明缺失——验证器价值的首个实证）
- **CLI**：`--emit-bil` 在 BilWriter 前接 BilVerifier——产出非法
  即逐条 Logger.Error 输出、不落盘、退出码 1（验证失败 =
  编译器 bug，响亮失败）
- **验证**：build 0 错误 0 警告（同批修复 StdlibSourcesTests
  既有 CS8602——M57 段登记的「不在本次范围」此项收口）；
  42 套件 2074/2074 + fuzz 6000 全绿；CLI 端到端 hello world
  样例 `--emit-bil` 通过验证正常落盘；中端行为零变化（验证器
  为纯增量检查层，P1–P4 与 BilWriter 路径零改动，BIL 黄金文本
  不受影响）

### 2026-08-03 · M57 BIL 生成全模型对象化重构（去魔法 string）

> 用户决策的架构重构：P4b 与 Bil 模型从「opcode 字符串 + 位置操作数
> 列表」迁移为强类型对象模型。动机——opcode 字面量散落 50+ 处
> （拼错编译照过）、操作数位置式零类型检查（§13.3 get/set.field
> 不对称序易写反）、BilWriter 按 opcode 字符串 switch 排版、
> 修饰符/种类/资源头字符串散落生成器侧、blk/res 裸 id 悬空引用
> 不可静态发现。行为零变化（同 M55 迁移铁律）。

- **指令模型**（`Bil/BilInstructions.cs` 基类 + `BilComputeInstructions.cs`
  §11–§12 + `BilDataInstructions.cs` §13–§15 + `BilControlFlowInstructions.cs`
  §16，按规范章节分文件仿 Bound/Lowered）：`BilInstruction` 抽象基类
  （Origin 保持 object? + Opcode/Operands internal 抽象 + WriteTo
  统一单行/多行排版——§16.6 switch/§16.7 try 续行形态由
  FirstLineOperandCount 声明）+ 强类型子类族：每种指令的 opcode
  拼写、操作数个数/类型/顺序由构造签名固定（§13.3 读取 OBJECT TARGET
  序 vs 写入 SOURCE OBJECT 序等不对称序编译期锁死）；§11 运算
  `BilBinaryOp`/`BilUnaryOp` 枚举（intrinsic 映射归 EmittingFacility
  MapBinaryOp/MapUnaryOp 单点）；§12.3 TypeCheck 双形态拆
  Direct/Indirect 两子类（.indirect 后缀派生）；可选 block 位置
  （if-else/loop-enum/try-finally）为可空 BilBlock 属性，null →
  none 操作数由 Operands 合成——生成方不再接触 none。不建模
  §13.5/§14.2/§15.2–15.4/§17（简洁优先，随各自里程碑按同模式增补）
- **拼写唯一定义点**：`Bil/BilSpellings.cs`——全部枚举 → 标准
  拼写映射集中一处（运算/类型检查/类型种类/成员关键字/block
  修饰符/访问/关键字修饰符/标量类型），模型与 writer 不再出现
  拼写字面量
- **声明与资源对象化**：`BilTypeKind`/`BilMemberKind`/`BilBlockModifier`/
  `BilAccessibility`/`BilKeyword`/`BilScalarType` 六枚举 +
  `BilModifier` 子类族（访问/关键字/operator(名)/symbol("...")/
  lib("...")——getter/setter/enum-case/wrapper-proxy 修饰符随
  S8e/S11/S14 增补）；`BilSwitchTableResource`（§19.4 selector
  类型引用自渲染 header）与 `BilCatchTableResource`（§19.5
  `BilCatchEntry` 类型化条目自渲染 `type(T) -> blk(id)`）——
  EmittingFacility 的 `"switch-table<...>"`/`"type(...) -> blk(...)"`
  字符串拼接删除；`BilCollectionResource` 保留通用形态（array/pair/
  map，测试与未来里程碑用）；Metadata 与标量资源的类型关键字同枚举化
- **操作数对象引用**：`BilBlockOperand` 持 `BilBlock`、
  `BilResourceOperand` 持 `BilResource`（悬空 blk/res 引用构造期
  即不可能）；EmitEnvironment 资源去重表按种类四分（标量/null/
  switch-table/catch-table），值为资源对象
- **P4b 契约类型化**：EmitValueDispatcher 与各值 emitter 产物
  string → `BilVariableOperand`（§10.1 物化契约）；NewTemp 返回
  变量操作数；"<error>" 占位同形态保留；EmittingDriver 补 ret
  判定 `is not RetInstruction`
- **BilWriter**：opcode switch 删除——指令自渲染（WriteTo），
  种类/修饰符经 BilSpellings/Render()；排版行为逐字节不变
- **验证**：build 0 错误（`Tests/StdlibSourcesTests.cs` 一处 CS8602
  为 M57 前既有警告——未触碰文件的 AST API  nullable 流分析，
  不在本次范围）；41 套件 1979/1979 + fuzz 6000 全绿
  （BilWriterTests/BilEmitterTests 用例数不变——构造与查询改强
  类型，黄金文本一字未动）；CLI 端到端 hello/switch 两样例迁移
  前后 `--emit-bil` 输出 diff 字节一致 + try/插值样例合法 BIL
  核对；Bil/ 对 Semantic/Lowering/AST 零依赖保持（ARCH §6.3
  字符串身份决策不变——canonical 符号仍以字符串承载于
  fn/type/field/case 操作数）

### 2026-08-03 · M56 S8b smart cast 三 pass 全通（M55 定稿落地）

> M55 的专项定稿（`SMART_CAST_DESIGN.md`）按既定实现映射落地：
> P3 分析与标记 + P4a 显式 cast 物化（P4b 零新增）。SYNTAX §3.5
> 「支持智能转换」一句正式扩写为完整规则，§3.4 补 null 判等。

- **规范落地**（`docs/SYNTAX.md`）：§3.5 smart cast 完整规则（触发
  表/目标安全规则/不触发清单/失效/guard/循环/switch/await-lambda
  边界先行）；§3.4 新增「null 判等」段（`==`/`!=` 一侧 null 字面量
  合法 bool、非 nullable 放行恒 false/true、`null == null` 报错、
  BIL = cmp + null 资源 §19.1）
- **前置**：`FieldSymbol.IsConst`（P1 收集写入）+ P3 const 字段
  赋值检查（init 构造方法体内 this 实例字段豁免——构造期一次性
  赋值；重复赋值精确检查留后续）——兑现 M41 技术债 11 的 const
  判定部分；null 判等绑定（BinaryVisitor 特例：null 定型
  Nullable\<T0\>、非空侧装箱视图 cast §12.1、两侧统一满足 §11.5
  严格相同）——兑现 M55 技术债 19⑤
- **P3 核心**：`FlowState` 收窄事实表（NarrowKey = 根（局部/参数/
  this/静态字段符号）+ const 字段链；**纯交集合并**——收窄非单调
  （失效移除键），before 中被分支失效的键不得复活，与 DA 的
  before∪∩ 规则相区分）+ `ConditionFactsExtractor`（绑定后 Bound
  条件 → 真/假边事实对：is 真边/null 判等双边/and∪∩/or∩∪/not
  翻转）+ `BoundSmartCastExpression` 标记节点（Type = 收窄类型，
  成员解析自然在其上进行）
- **收窄应用点**：if 语句（分支入口事实 + **guard 反向传播**——
  分支终止取对边流；DA 规则不变保行为兼容）与 if 表达式（无
  guard，纯交集）；and/or 右侧绑定上下文（`(x is String) and
  (x.length > 0)` 右侧在收窄类型上解析）；while（体入口真边 +
  「体赋值根」保守剔除——回边规则；出口不收窄）与 do-while/for
  （剔除体赋值根）；switch `(_ is T)` 分支体 selector 收窄（Q4——
  分支体内 `_` 不可用是 §7.2 既有语义，覆盖 selector 可收窄场景）；
  var 局部/参数赋值与复合赋值失效（含以其为根的字段链）；字段链
  稳定判定（每层 const + IsNarrowable + init 体内排除；var 根允许
  ——赋值失效覆盖；带访问器属性的排除判定归 S8e 细化）
- **P4a 物化**：`BoundSmartCastExpression → LoweredCastExpression`
  （isSafe: false——T? → T unwrap 与子类型收窄同属 §12.1 形态，
  复用 M51/M52 模式）；**P4b 零新增**（cast 发射 S7e 已备）
- **验证**：新套件 `SmartCastTests` 55 用例（注册表 #41——定稿
  规则逐条：is/null 判等/and-or-not/guard return+throw/失效/const
  字段/var 字段不收窄/while/do-while/switch 占位/不触发形态/分支
  合并/null 判等绑定/物化）；全量 41 套件 1979/1979 + fuzz 6000
  全绿；CLI 端到端三样例（guard、and 组合、switch 占位）逐行核对
  BIL（unwrap cast 物化 + null 资源 cmp + type.is 六形态）合法；
  开发期发现并修复两处收窄遗漏（多段路径头与调用链头 receiver
  未包装——测试驱动捕获）
- **文档**：SMART_CAST_DESIGN 落地偏差记录（Q4 澄清/var 根允许/
  访问器判定归 S8e）；ROADMAP S8b 标完成

### 2026-08-03 · M55 中端三树 visitor 化重构（Binder/Lowerer/BilEmitter）+ S8b smart cast 专项定稿

> S8b 前置架构重构（用户决策）：Binder（2890 行）/Lowerer（1331 行）/
> BilEmitter（1045 行）三个 session 巨石类全部 visitor 化——任何方法可碰
> 任何栈的状态污染面已构成维护性灾难风险。同批完成 S8b smart cast 的
> 语言规则专项定稿（`docs/compiler/semantic/SMART_CAST_DESIGN.md`），
> 落地本身归下一里程碑。

- **架构方案（用户提出，讨论定稿，`docs/compiler/semantic/VISITOR_REWRITE.md`）**：
  - **CRTP 协议基类**：所有结构 visitor 继承 `XxxVisitor<TSelf, TResult, TContext>`
    （`where TSelf : ..., new()`）；基类静态 `Visit` 为唯一入口——创建子类
    实例 + 模板化管理生命周期（Enter/Exit 配对，栈压/弹 finally 固化，
    杜绝手工配对泄漏）；外部调用者（上一层 visitor 或驱动器）经分派器路由
  - **双协议**：`Visit → TResult?`（上行合成，常态——Bound/Lowered 树
    自下而上组装）+ `VisitInto(shell)`（施工壳填充：值块/循环壳先建压栈
    给 return@/break 命中、体绑完回填）；否定「target+result 单签名统一」
    （90% 调用点会传无意义 target）
  - **context 方言**：CRTP 泛型参数指定上下文类型；`BindEnvironment`
    （只读，全编译期不变）固定共享；方言 = **同一函数级状态对象的接口
    视图**（禁止多方言对象状态分裂——DA/标签栈是跨 visitor 共享可变
    状态）；初期从粗（一个 BindContext + IFlowContext 一角），按需
    extract 切细。函数级状态对象化：每函数体 new 一个 BindContext/
    LowerContext/EmitContext——旧 session 的「防御性清空」由此消失
  - **类别分派器 + 结构 visitor 两层**：分派器（Expression/Statement/
    Block）是唯一 switch 所在（对应旧 BindExpression/BindStatement/
    EmitStatement/EmitValue）；结构 visitor 之间不直接互调，一律经
    分派器；簇级分文件（每文件 1–4 个 visitor，仿 Parser 层文件惯例）
  - **被否定的方案**：Roslyn 式超大 partial class（文件拆分但单类状态
    共享，污染面仍在）；60 个绑定器对象的组合式（互递归遍历下组合退化
    为全互联，样板倍增）；Lowerer 多趟 rewriter 链（ARCH §6.1 远期
    愿景）记为演进方向（趟间契约需重定义，值块编织区有 miscompile
    风险），S8 收官或 S13 时再评估
  - **迁移策略**：停线重写 + Binder 先行全链验证协议后复制（用户决策）；
    跨会话交接文档先行（两份设计底稿达「另一会话读文档即可续作」标准）
- **协议 v2 修正**（动工时发现）：绑定遍历有两个 v1 签名遗漏的固有
  下传参数——`scope`（当前词法环境，随块嵌套变化，等价 Parser 施工
  目标）与 `expectedType`（期望类型下传：null 字面量定型、return/
  赋值/实参目标类型传播，仅表达式消费）；定稿三基类（通用
  BinderVisitor/表达式 ExpressionVisitor 追加 expectedType/壳
  BinderShellVisitor）。Lowerer/Emit 侧无此两参（无词法作用域概念）
- **Binder 落地**（`Semantic/Binding/`：23 文件 3373 行，原 2890）：
  骨架（BinderVisitor 三基类/BindEnvironment/BindContext/IFlowContext/
  FlowState/Scope/Dispatchers/BoundAnalysis/BindingDriver）+ 共享设施
  （SymbolLookup 实例成员与泛型字段替换/MemberLookup 名字解析查找序/
  TypeReferences）+ `Visitors/` 11 簇（Literal/Declaration/Conditional/
  Loop/Switch/TrySeq/Binary/Path/Call/TypeCheck）。**FlowState 提取**
  （assigned DA 集 + Snapshot/Restore/MergeIfBranches/MergeBranches——
  S8b 收窄表的家，DA 与收窄同为流敏感事实同生命周期）；壳协议首验
  （ValueBlockShell 包装承载诊断构造名，Bound 节点零改动）；
  SwitchMatchContext 载荷 + visitor 实例字段记录压栈（任务局部状态
  对象化范例）；复合赋值迁移中修复三处保真偏差（毒化静默位置/诊断
  消息文本——「先完整读旧实现再写」纪律确立）
- **Lowerer 落地**（`Lowering/`：14 文件 1847 行，原 1331）：LoweredVisitor
  基类（无 scope/expectedType——Lowered 遍历无词法作用域）+
  LowerContext（outputStack 前置语句机制/值块/循环/switchTemps/
  safeReceivers 四映射栈/transformFailed）+ `Rewriters/` 8 簇。
  continuation 编织（TransformStatements 家族）归 ValueBlockFacility
  静态设施（LoweredTree 就地变换，非 visitor 遍历）；LoopRewriter/
  ForLoopRewriter 合成顺序保真（`.sN` 先于 `.bN`——SynthLocals 顺序即
  `.vars` 发射顺序，BIL 文本快照敏感，Enter 内按旧序合成）；旧代码
  SafeReceivers 无 finally 手工压弹修固为 try/finally（架构升级红利）
- **BilEmitter 落地**（`Lowering/`：9 文件 1474 行，原 1045）：EmitVisitor
  基类（签名带施工目标 `BilBlock target`——本树天然下行填充，三树中
  与协议贴合度最高）+ 分层上下文（EmitEnvironment 模块级：Module +
  ResourceKeys 跨 fn 去重；EmitContext 函数级：TempVars/各 block 计数）
  + Unit 占位（语句发射 TResult）+ `Emitting/` 3 簇
- **验证（行为零变化铁律）**：三树各自完成时 `test --all` 40 套件
  1924/1924 全绿（Binder 重写一次通过；BilEmitter 120 用例黄金文本
  逐字节一致）；build 0 错误、重写引入 0 新警告（StdlibSourcesTests
  CS8602 为 HEAD 预先存在）；**测试零改动**；旧参考文件（Legacy*.txt）
  全部删除。BilEmitter 由子代理执行、主代理抽查验收（协议一致性 +
  复杂 emitter 保真度 + 全量测试）
- **S8b smart cast 专项定稿**（`SMART_CAST_DESIGN.md`，2026-08-03 用户
  逐条拍板）：Q1=**B**（v1 即含字段收窄——仅 const 字段 + backing
  field 直访 + receiver 稳定链，var 字段别名赋值不可控不收窄，与
  Kotlin 同口径）/Q2=A（guard 模式：终止分支反向传播）/Q3=A（and/or/
  not 全支持，含 and/or 右侧绑定上下文）/Q4=A（switch pattern `_ is T`
  占位收窄）；无争议项（supers/with/动态 is 不触发、is 真边蕴含非空、
  else 边无差类型、while 体带真边 do-while 不带、lambda/await 规范先行
  实现归 S13）；**前置缺口发现**：`x == null`/`x != null` 现状无法绑定
  （BindBinary 同类型规则 + null 无上下文），§3.4 null 判等补规范+
  绑定随 S8b 落地；实现映射：FlowState 收窄表 + ConditionFactsVisitor
  （条件事实提取器，天然 visitor）+ BoundSmartCastExpression 标记 +
  P4a LoweredCastExpression 物化（P4b 零新增）

### 2026-08-01 · M54 S8 细化 + S8a（is/supers/with + typeOf 三 pass）

> S8「P3 完整化」按「近细远粗」约定细化为 S8a–S8f（SEMANTIC_ROADMAP，
> 2026-08-01）；S8a 落地类型谓词与运行时类型三 pass——BIL §12.3
> （`type.is`/`type.supers`/`type.with` + 三 `.indirect`）与 §12.5
> （`getid.var`/`getid.type`）首次发射。前端零改动（TypeCheck/
> TypeOf AST 节点 M8/M34 已备），Bil/ 零改动（通用 opcode 模型
> + BilOp.Type/Var 操作数直接承载新指令）。

- **规范定稿**（`docs/SYNTAX.md`）：
  - §3.5 新增「is/supers/with 的右侧解析规则」：右侧可为类型引用或
    `Type\<T>` 值（动态类型测试）——名字先按类型引用解析，失败再按
    值绑定且值必须承载 `Type\<T>`，同名时类型优先；`with` 类型引用
    必须 wrapper 类型；`is`/`supers` 不做静态不可能性拒绝（与 as 的
    可转性口径一致，`12 is String` 不报错）；
  - §3.5 注记：`is .Case` 是隐藏判别字段整数比较（RUNTIME §16.3）
    而非类型检查，不触发 smart cast；
  - §3.7 typeOf 补双形态定稿：操作数先按值绑定（常态，返回
    `Type\<T静态\>`，T 为类型边界——BIL §6.3 `.typeid<TBound>` 语义），
    无法绑为值且可解析为类型引用时取类型形态（`Type\<T\>`）；
  - 笔误修订：§3.5 castFrom 示例返回类型（TSource → 目标类型，
    示例包入宿主 `class Celsius`）；§9.2 修饰符表补 `override` 行
    （§19 关键字表本已有）。
- **P3**（`Semantic/Bound/BoundExpressions.cs` + `Semantic/Binder.cs`
  + `Tests/BoundDescribe.cs`）：
  - BoundTypeCheckExpression：Kind 三态（Is/Supers/With）+ Operand +
    TargetType（静态）/TargetValue（动态，Type 为 `Type\<T\>`）互斥
    双槽（构造时恰一个非 null，CompilerInternalException 守卫），
    Type 恒 bool；BoundTypeOfExpression：Operand（值形态）/TargetType
    （类型形态）互斥，Type = `Type\<T\>` 构造类型
    （SymbolGraph.GetConstructedType 驻留）；
  - BindTypeCheck：静态形态经 ResolveSymbolPath（reportErrors: false）
    不落袋试探（ErrorTypeSymbol 系 TypeSymbol 子类，显式排除——本步
    抓出的初版误编译缺陷）；`T?` 目标包 `Nullable\<T\>`；with 静态
    目标 Kind != Wrapper 诊断；动态形态 BindTypeCheckTargetValue
    不落袋纯查找（单段：局部（含 DA 未赋值检查）→ 参数 → FindField
    全链；多段：静默容器解析 + 末段字段），命中后正常构造值引用，
    类型必须 ConstructedFrom == TypeDefinition；
  - BindTypeOf：单段裸名操作数（非 this、无泛型/后缀/段）先值后
    类型不落袋分类，其余形态一律值形态；两不沾走 BindPath 自然报
    Undefined name；ErrorType 毒化静默；
  - `is .Case`（TargetCase 非 null）落 P3 归口诊断（S11）；
  - switch pattern 中 `_ is X` 自然贯通（占位 `_` 作 Operand 不特殊
    处理）。
- **P4a**（`Lowering/Lowered/LoweredExpressions.cs` + `Lowerer.cs`
  + `Tests/LoweredDescribe.cs`）：LoweredTypeCheckExpression/
  LoweredTypeOfExpression 同构两节点（Type 自带，仿
  LoweredCastExpression），恒等重写（TargetValue/Operand 递归降级，
  无脱糖）。
- **P4b**（`Lowering/BilEmitter.cs`）：EmitValue 加两 case（同 cast
  模式，NewTemp 物化 `.tN` + Origin 塞 LoweredNode）——静态
  `type.X VALUE type(TARGET) RESULT`，动态先物化 typeid 变量再
  `type.X.indirect VALUE TYPEID_VAR RESULT`；typeOf 值形态
  `getid.var VALUE RESULT`、类型形态 `getid.type type(SYMBOL) RESULT`；
  `Type\<T\>` 经 bilStandardConstructor 投影 `.typeid<T>` 进签名。
- **测试**：BinderTests +TestTypeCheck/TestTypeOf（双形态快照 +
  结构断言（符号 ReferenceEquals、互斥槽、恒 bool）+ 诊断四例）；
  LowererTests +TestTypeCheckLowering/TestTypeOfLowering（快照 +
  Origin 回指 + 递归降级断言）；BilEmitterTests +TestTypeCheckEmission
  /TestTypeOfEmission（六种形态黄金 BIL 逐行——先 `--emit-bil` 冒烟
  取真实输出核对 §12.3/§12.5 后落断言）。

### 2026-08-01 · M53 插值词法帧机制（前端回补：Lexer 层栈嵌套解析）

> 按用户决策对 M51 的插值前端做架构重构：**插值解析从「Parser 侧拆分」
> 改为「Lexer 层栈嵌套」**——字符串层遇 `${` 压基础层正常词法，驱动按
> token 层大括号计数配平弹回。StringInterpolationSplitter（配平扫描/
> 子词法/span rebase/子解析，~280 行）与 StringToken.RawContent/
> MultilineIndent/IsMultiline 全部删除；SYNTAX §3.8 的「单行宿主内插值
> 不得含未转义 `"`」限制解除。AST 与 P3/P4 全链零改动（段结构同构）。

- **Lexer**（`Lexer/Tokens.cs` + `Lexer/Lexer.cs` + `Lexer/LexerLayers.cs`）：
  - InterpolationStartToken（`${`）/ InterpolationEndToken（配平 `}` 改发）
    两标记 token；
  - 驱动插值帧栈（ContextImpl.interpolationDepths）：PushToken 单点维护
    ——开始标记压帧、帧内 `{`/`}` 记号计数（**token 层计数使字符串/
    字符/注释内容天然豁免**，它们不产生记号 token）、归零改发结束标记
    并请求弹帧根 Base 层；嵌套插值经开始标记递归压帧；EOF 帧未归零
    先于冲刷报 "Unclosed interpolation"；
  - StringLexerLayer：挂起 `$` 延迟判定（下一字符是 `{` 即引导，否则
    补入内容——`\$`/闭界/`\` 边界统一）+ 帧触发（段产出 + 开始标记 +
    压 BaseLexerLayer）+ 首段 span 修正（开界引号不属于段内容）与段尾
    修正（引导 `$` 不属于段内容）；
  - MultilineStringLexerLayer：累积阶段挂起 `$` 同款判定 + 段结算产出
    （原文暂存保序，**闭界确定缩进基准后统一回填**——词法先于解析
    全量完成，回填天然安全；仅首段的段首行参与剥缩进）+ 首段 span
    修正；无插值时保持单 token 与含闭界 span 的既有行为。
- **Parser**（`Parser/LiteralParserLayer.cs`）：段序列状态机
  （AwaitInterpolationStartOrDone / InterpolationEndExpected /
  AwaitSegmentOrStartOrDone）——InterpolationStart 建插值节点并委托
  ExpressionParserLayer 就地填充段 Root（主 Parser 流内正常委托，
  allowBareReturn 传染）；文本段解码后为空的跳过；普通单段路径不变。
- **删除**：`Parser/StringInterpolationSplitter.cs`（整文件）、
  StringToken.RawContent/MultilineIndent/IsMultiline/HasInterpolation
  （词法标记随帧机制退役；AST 的 HasInterpolation 由 Parser 设置）。
- **SYNTAX §3.8 修订**：插值表达式按普通 Latte 词法解析——任意字面量
  （嵌套字符串两态宿主均可）、嵌套 `{}`（lambda/seq）、注释、跨行
  （§1.1 续行规则）全允许；`"a${"b"}c"` 自此合法。
- **测试**：6 新增（单行嵌套字符串/嵌套插值/lambda 大括号/多段连续 +
  多行嵌套插值/lambda）+ 迁移——错误路径四例改帧机制消息（Unclosed
  interpolation/Unexpected token at start of expression/Expected '}' to
  close interpolation/嵌套字符串就近报）、词法断言改 token 流形态
  （InterpolationStartToken 存在性）、span 断言对齐段 span 修正
  （段不含引号与引导 `$`）。

### 2026-08-01 · M52 S7f 收官：`?.` 安全调用 + `if?` 空值回退 + 解构声明

> ROADMAP S7f 收官（剩余三项全部落地，P3/P4 同步 + Parser 回补），
> 同批定稿 nullable 的 BIL 语义空白（null 检查形态、装箱/展开 cast）
> 与泛型成员访问的最小规范条款。**S7f 四项（插值/`?.`/`if?`/解构）
> 全部端到端出合法 BIL**。

- **spec 修订**：BIL §19.1 定稿 `null type(T)` 资源的类型语义即
  `.nullable<T>`（null 检查 = `cmp.eq`/`cmp.ne` + null 资源，天然满足
  §11.5 严格相同，§3.4「nullable 检查」落地）；§12.1 补 nullable
  装箱/展开为内建引用视图转换（展开遇 null 抛 core.CastException——
  Latte 层 `nullableVar as T` 同语义）；§13.3 补泛型宿主字段访问的
  实参替换判定条款；SYNTAX §3.4 补 `?.`/`if?` 定型规则（`?.` 结果
  不二次包装、可空值无隐式成员访问须逐段标注；`if?` 左 Nullable\<T\>
  右可赋值到 T 结果恒 T、右侧延迟求值）。
- **`?.`（P3/P4）**：BoundSafeAccessExpression（Receiver/Placeholder/
  Access）+ BoundSafeAccessReceiverExpression 占位叶子（仿 S7d switch
  `_` 占位先例）；BindInstanceChain 分派 SafeDot 段——receiver 必须
  Nullable\<T\>，段在非空 T 上经占位绑定（字段/单调用后缀），结果
  类型成员已可空则原样否则包 Nullable；普通段遇可空 receiver 报
  「use '?.'」专门诊断；P4a LowerSafeAccess 脱糖（物化 receiver
  `.sN` + 结果局部前置 null + `if (cmp.ne recv, null)` + 占位映射栈
  替换为 unwrap cast + wrap cast——嵌套 `a?.b?.c` 逐层命中）。
- **`if?`（Parser 回补 + P3/P4）**：ExpressionParserLayer 中缀 `if`
  重组（IfNullFallbackSeen 态期待 `?`，参照 as? 模式；`a if? b if? c`
  按无优先级报错；HandleCompleted 同认 if）；BinaryExpressionASTNode
  Operator `"if?"`；P3 BindNullFallback（左 Nullable\<T\>、右
  IsAssignable 到 T、结果恒 T）；P4a LowerNullFallback（物化左侧 +
  `if (cmp.ne) { cast unwrap } else { 回退值 }`——延迟求值由 if 结构
  保证）。
- **解构（stdlib + Parser + P3/P4）**：core.Pair\<TKey, TValue\> 进
  `.bootstrap.latte`（namespace core 头 + open class + key/value
  字段，用户决策的自举位；编译器按 canonical 名硬编码参照，同 M48
  IEnumerable 先例）；Parser 解构分支（VariableDeclarationParserLayer
  三新状态，`(` 分流；DestructuringDeclarationASTNode 不新建——
  VariableDeclarationASTNode.DestructureNames 互斥字段，同 TypeCheck
  双槽先例）；P3 BindDestructuring（沿 BaseType 链找 Pair 构造、
  名字数恒 2、分量类型 = 构造实参特判）+ **构造类型成员查找
  ConstructedFrom 回退**（FindInstanceField/FindInstanceMethods/
  FindField/FindMethods 统一——构造器不复制成员列表的既有空白）+
  **泛型字段类型最小替换**（SubstituteFieldType：声明类型是宿主泛型
  参数时按 receiver 链构造实参定型——S9 前置特判，Pair 子类 init
  写基类字段因此可用）；P4a LowerDestructuring（pair 物化 `.sN` +
  逐字段读取，BIL §3.4「精确字段读取」；
  LoweredFieldAccessExpression/LoweredLocalDeclarationStatement origin
  放宽 BoundNode 承载合成路径）。
- **测试**：51 新增——Expression `if?` 重组（快照 + 优先级/缺 `?`
  错误）+ VariableDeclaration 解构（快照 + 结构 + 三错误路径）+
  Binder 三分区（`?.` 字段/方法/链式 + 两诊断；`if?` 单用/组合 + 两
  诊断；解构闭环 + init 替换断言 + 三诊断）+ Lowerer 三分区（`?.`
  字段/链式脱糖、if? 脱糖、解构脱糖快照）+ BilEmitter 三分区（
  null 资源/cmp.ne/unwrap/wrap cast 黄金 fn + 解构 get.field +
  init set.field）+ StdlibSources Pair 结构断言 + hello world 黄金
  文本 core::Pair 段迁移 + BoundDescribe/LoweredDescribe 新节点格式
  （SafeAccess/SafeReceiver/NullFallback/Destructuring/Const(null,T)）。

### 2026-08-01 · M51 S7f-1 字符串插值 + toString 机制 + String 拼接 + 子类型 cast 物化

> ROADMAP S7f 第一刀（字符串插值），P1–P4 全链路贯通；同批按用户
> 决策落地 **toString 机制 spec 定稿**（SYNTAX §3.8）与 **String 内建
> 拼接开放**（bootstrap Add intrinsic），并补齐 ARCH §6.1 规划已久的
> **子类型 cast 物化**（BIL §6.5）。

- **spec 修订**：SYNTAX 新增 §3.8（toString 全类型承诺挂 Any、基元
  标准文本、未覆写返回类型 canonical 名、插值 = 非 String 段 toString
  后拼接、单行宿主内插值表达式不得含未转义 `"`、表达式跨行同 §1.1
  续行规则）；RUNTIME §26 原生方法面扩为三个（`toString(value: Any):
  String` 内建承载：Any 接口承诺 + Object open 默认实现 native 路由，
  override 经虚派发绕开原生面）；BIL §22.5 hook 表加 `latte_rt.toString`
  + §11.2 `.string` 的 `add` 定义为内建字符串拼接。
- **Lexer**（`Lexer/Tokens.cs` + `Lexer/LexerLayers.cs`）：StringToken
  新增 RawContent（解码前源字符切片：单行逐字符含转义对、多行含缩进
  源行按 \n 拼接）/ MultilineIndent（闭界剥除基准）/ IsMultiline——
  插值拆分的定位底稿，span 可逐字符精确映射回源。
- **AST**（`AST/LiteralNodes.cs` + `AST/AstJsonlDeserializer.cs`）：
  StringInterpolationPart（[AstCarrier]，互斥双字段：Text = 段级
  LiteralExpression 子结构（与普通字符串字面量同构，P3/P4 复用字面量
  机器）/ Expression = ExpressionRootASTNode）+
  StringLiteralASTNode.InterpolationParts（null = 无插值）；JSONL 反
  序列化器支持 null 列表成员按需创建（GetOrCreateList）。
- **Parser**（`Parser/StringInterpolationSplitter.cs` 新文件 +
  `Parser/LiteralParserLayer.cs`）：RawContent 一体化扫描（转义对
  解码、多行行首剥缩进、未转义 `${` 引导）→ 表达式配平截取（嵌套
  `{}` 计数，字符串/字符/行注释/块注释内容不计）→ 子词法 → span
  rebase（首行加列偏、offset 平移）→ 子解析（私有 EOF 垫底层 +
  ExpressionParserLayer；allowBareReturn 按 M33 规则经
  LiteralParserLayer 传染）。单行宿主内插值含 `"` 在词法层即闭合
  宿主（语言固有限制，已写进 §3.8；嵌套字符串请用多行宿主）。
- **bootstrap**（`Semantic/Symbols/BootstrapSymbols.cs`）：String
  intrinsicOps 加 Add（`"a" + "b"` 与 `s += "b"` 同步开放）；
  Any.Methods 挂 toString 接口承诺（无体）+ Object.Methods 挂 open
  默认实现（native `latte_rt.toString`，符号直造同 Exception 先例）。
- **P3**（`Semantic/Binder.cs`）：BindStringInterpolation——绑定即
  规范化（参照 M48 `a to b` 先例）：字面量段走普通字面量绑定；非
  String 段包 toString() 实例调用（FindInstanceMethods 沿 BaseType 链
  静态绑定最近声明，运行期虚派发）；全 String 段左结合折叠为
  BoundBinaryExpression(Add) 链；段表达式正常参与绑定（未定义名照常
  诊断、void 段报「必须产值」）。**P4 对插值零新增节点**。
- **P4a**（`Lowering/Lowerer.cs`）：子类型 cast 物化（ARCH §6.1
  「source-level 子类型赋值/传参 → 显式 cast」首个落地）——统一
  EnsureDeclaredType：receiver（≠ 方法宿主，如 i32 调 Any.toString 的
  装箱 cast）/ 实参（≠ 形参）/ 局部初始化 / 赋值 / return 五位置；
  hello world 黄金文本两处 return 因此严格化（RangeI32 →
  IEnumerable\<i32\> 等显式 cast，BIL §6.5 合规）。
- **测试**：41 新增——Literal 插值分区（多段/单段/调用段 + 结构/span
  断言 + JSONL 往返 + 错误路径四例）+ MultilineString 多行插值（剥
  缩进共存/跨行表达式/嵌套字符串）+ Binder 插值分区（拼接链/toString
  包装/String + 开放/显式调用/void 段）+ Lowerer 插值与 cast 物化分区
  （装箱 cast/return/初始化三位置）+ BilEmitter 插值端到端（黄金
  fn 快照：cast .any + invoke Any.toString + add 链）；快照迁移
  `Str("...",interp)` → `StrInterp(...)`（拆分段序列描述）。

### 2026-08-01 · M50 S7e cast 最小闭环 + try/catch/finally + seq

> ROADMAP S7e 落地，P3/P4 同步推进；cast（as/as?）最小闭环按用户
> 决策提前自 S8（「没法脱离 cast 实现其他功能」）。try/catch/finally
> 与 seq 双形态全链路贯通——**自此 SYNTAX §7 控制流（if/switch/
> 循环/try/seq/throw）全部端到端过 P1–P4**。

- **P3 cast/try/seq 绑定**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：BoundCastExpression（as/as? 统一承载，IsSafe 区分；
    as 结果即目标类型、as? 结果 Nullable\<T\>——P3 定型，P4 不再
    区分包装）+ BoundTryStatement/BoundCatchClause +
    BoundSeqStatement/BoundSeqExpression + BoundValueBlock.IsVolatile；
  - cast：可转性不做静态拒绝（as 失败是运行时 core.CastException，
    castTo/castFrom 名字分析归后续）；ErrorType 毒化静默；
  - try：catch 类型 IsAssignable 到 bootstrap Exception；catch 变量
    与 finally(e) 变量 const 只读且命中即 assigned（finally 变量
    类型 Nullable\<Exception\>）；DA 合并 = before∪(try∩全 catch)
    ∪finally；
  - seq：语句形态直通 BindBlock（作用域/assigned 语义与裸块相同），
    不压值块标签栈（return@ 指向语句 seq 报未定义标签，规范未明，
    登记技术债）；表达式形态复用 BindValueBlock 值块语义（标签同源
    Label ?? "_"）+ IsVolatile 置位 + 必须产值检查；using 拦截归 S13；
  - GuaranteesReturn/GuaranteesValueReturn/EnumerateStatements 扩展
    覆盖 try/catch/finally 与 seq 块。
- **P4a 降级**（`Lowering/`）：LoweredCastExpression（Type 自带）/
  LoweredTryStatement/LoweredTryCatch/LoweredSeqBlock 四节点；
  try 合成 ExceptionSlot——finally(e) 时 slot 即 finally 变量、
  否则合成 `.sN`（Nullable\<Exception\>）；有名 catch 体头编织
  「变量 = cast slot」合成赋值（Origin 指 BoundCatchClause）；
  seq 语句/volatile 恒等、seq 表达式脱糖为前置 seq 块写合成结果
  局部 + 原位置读局部（复用 S7b session 前置语句基建）；
  try+finally 部分终止的值块编织拦截——TransformWithContinuation
  深处触发 transformFailed 标记（void 链路无法返回值传播），诊断
  落袋后 LowerValueBlock 放弃产物、函数体跳过。
- **P4b 发射**（`Lowering/BilEmitter.cs`）：`cast`/`cast.safe`
  三操作数 SOURCE RESULT type(TARGET_TYPE)（§12.1/§12.2，结果
  物化 .t 临时变量）；seq → `call blk(seqN)`（§16.1 不建栈帧，
  块落尾自然返回续 call 的下一条），volatile → §9.6 block 修饰符；
  `try` 四操作数 blk(body)/$slot/res(表)/blk(finally)|none
  （§16.7，块 id `try0-body`/`try0-catchN`/`try0-finally`）；
  §19.5 `catch-table` 多行资源（BilCollectionResource multiline，
  元素 `type(T) -> blk(...)` 保序——表序即匹配序，空 catch 列表
  出空表）——RegisterCatchTable 复用 resourceKeys 同元素序列去重；
  BilWriter 零改动（try 多行排版与多行资源打印已随 S4 就位）。
- **测试**：Binder 套件 221 → 270（TestCast/TestTry/TestSeq：as/as?
  定型/catch 类型兼容/catch 变量 const/seq 必须产值/using 拦截
  各负例 + 快照）；Lowerer 套件 84 → 112（TestCastLowering/
  TestTryLowering/TestSeqLowering/TestTryWeaving：slot 合成三形态/
  catch 头 cast Origin/脱糖形态/编织拦截）；BilEmitter 套件
  82 → 97（TestCastEmission/TestTryEmission/TestSeqEmission 黄金
  多 block 文本 + try 四操作数/call 单操作数/catch-table 保序与
  多行形态结构断言）；CLI 端到端样例（try/catch/finally(e) +
  seq 语句/表达式 + as/as?）逐行核对 §12.1/§12.2/§16.1/§16.7/§19.5
  一致。

### 2026-08-01 · M49 S7d switch 语句/表达式 + throw

> ROADMAP S7d 落地，P3/P4 同步推进：switch 语句/表达式（值匹配 +
> `_` pattern 双分类）与 throw 全链路贯通，异常根 `core.Exception`
> 定稿进 bootstrap（`IsOpen` 可继承——具体异常子类归 S10 stdlib，
> 边界注记同步 ROADMAP S7d/S10）。自此 SYNTAX §7 控制流仅剩
> try/catch/finally 与 seq（S7e）未通。同批修复 M46 遗留的
> else-if 链值块编织 miscompile。

- **异常根定稿**（`Semantic/Symbols/BootstrapSymbols.cs`）：
  `Exception` 属性（TypeKind.Class、baseType: Object、isBuiltin、
  `IsOpen = true`），注册进 Core.Types 列表——throw 兼容性检查的
  锚点；ROADMAP S7d「进 bootstrap 还是 stdlib 在本步定稿」的抉择
  落地为 bootstrap（与 Object/ValueType 层级根同列），S10 只收
  具体子类。
- **P3 switch/throw 绑定**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：BoundSwitchStatement/BoundSwitchCase/
    BoundSwitchExpression/BoundSwitchExpressionCase/
    BoundThrowStatement/BoundSwitchPlaceholderExpression（`_` 占位——
    Selector 回指 selector 表达式做嵌套消歧，Type = selector 类型）；
  - switch 绑定：switchSelectors 占位栈（BindPath 单段 `_` 命中
    栈顶）；BindSwitchMatch 值匹配/pattern 显式分类（值匹配限
    编译期常量且类型严格等于 selector 类型；pattern 必须 bool）；
    两形态强制 default（RequireSwitchDefault）；分支产值类型统一
    与 DA 分支合并复用 if 基建（MergeBranches = before ∪
    (∩全部分支)）；ContainsSwitchPlaceholder 走
    AstStructureReflection.EnumerateChildren 统一下钻；
  - throw 绑定：IsAssignable 到 bootstrap Exception，不兼容即诊断；
  - GuaranteesReturn/GuaranteesValueReturn 终止口径扩展：throw 与
    全分支（含 default）return 的 switch 视为终止；
    EnumerateStatements 递归 switch 分支体；
    BindValueBlock/CollectBranchValueType 加 construct 参数区分
    "if expression"/"switch expression" 消息。
- **P4a switch 降级**（`Lowering/`）：LoweredSwitch（全值匹配
  恒等，携 .breakid 合成局部 + DefaultBody）/LoweredSwitchCase/
  LoweredThrowStatement 三节点；LowerSwitchCore 以 Any(IsPattern)
  分流——常量形态恒等，pattern 形态走 LowerPatternSwitch（selector
  物化 `.sN` 前置 + switchTemps 映射栈）+ BuildPatternChain 递归
  嵌套 if 链（值分支条件 = 合成 cmp.eq，Origin 指 match 常量——
  LoweredBinaryExpression 加可选显式 Type 参数，透传会是错的）；
  switch 表达式结果局部 `.sN` + 前置语句复用 S7b session 基建；
  throw 恒等。
- **M46 miscompile 修复**（必要前置，同批落地）：else-if 链值块
  在 if 转换中后续分支错误覆盖结果局部（CLI 实证 c2 路径 .s0 被
  覆盖为 x）；TransformStatements 重写为 continuation 编织——
  TransformWithContinuation/WeaveContinuation/HasTerminatingPath/
  BlockTerminates（终止口径含 throw+switch），终止分支织空
  continuation、非终止分支织 rest、顶层值块写入与 throw 截断后续；
  LowererTests 加 TestElseIfChainTransform 回归。
- **P4b switch/throw 发射**（`Lowering/BilEmitter.cs`）：switchCount
  块编号（`switch0-itemN`/`switch0-default`）；switch 指令 §16.6
  五操作数（selector/res(表)/[blk item 表]/blk(default)/breakid，
  操作数序即 BilWriter 规范排版序）；RegisterSwitchTable——§19.4
  `switch-table<T>` 单行资源（BilCollectionResource，Multiline:
  false），元素经 RenderLiteral 复用 §19.1 渲染（表元素只进表不
  产标量资源），同（header, 元素序列）跨 fn 去重（复用
  resourceKeys 字典，TypeKeyword = "switch-table" 与标量键不冲突）；
  throw §16.9 单操作数（异常值物化后 `throw $e`，entry 块 throw
  终止不补 ret）。
- **测试**：Binder 套件 198 → 221（TestSwitch/TestThrow：值匹配
  常量限定/类型严格相等/pattern bool/产值统一/default 强制/throw
  兼容性各负例 + 快照）；Lowerer 套件 69 → 84（TestSwitchLowering/
  TestThrowLowering/TestElseIfChainTransform）；BilEmitter 套件
  66 → 82（TestSwitchEmission 黄金 + 跨 fn 同表去重/
  TestPatternSwitchEmission 无 switch 指令断言/TestThrowEmission）；
  CLI 端到端三样例（常量 switch/pattern switch 表达式/throw new
  core.Exception()）逐行核对 §16.6/§16.9/§19.4 一致。

### 2026-08-01 · M48 S7c-2 实例成员最小闭环 + IEnumerable + for 双形态

> ROADMAP S7c-2 落地，P3/P4 同步推进：实例成员最小闭环（this/
> 实例调用/实例字段访问）+ core.collections 迭代协议 stdlib +
> for 双形态统一脱糖。自此 Latte 的面向对象面（实例方法/实例字段/
> 构造）与第一种「库级协议驱动」的语言结构（for-each）全链路
> 贯通——stdlib 三源（.bootstrap 基元自举 + Console + collections）
> 全量同走 P1–P4，自身即最完整的端到端用例。

- **第 0 步早期验证**（四项，全部一次通过）：ext+operator Parser
  组合直接合法（无需动 Parser）；`.bootstrap.latte` 被 csproj 通配
  正常内嵌、LogicalName/sourceName 反推无异常（无需改 csproj）；
  §8.4 init/operator 声明形态读取（operator 用 `$$名` canonical +
  `operator(名)` 修饰符，init 用普通 canonical `$init@.void` +
  `init` 修饰符）；init `_ -> field` 映射 P3 无任何处理（stdlib
  以显式赋值 init 规避，技术债登记）；接口无体方法 P1/P2 零拦截
  （无需开闸）。
- **P3 实例成员与 for 绑定**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：BoundThisExpression/BoundInstanceCallExpression/
    BoundFieldAccessExpression；BoundLoop 扩展 For 路径（Condition
    可空——形态互斥注释约定；LoopVariable(const)/Iterable/协议
    三方法符号挂好，P4 不做名字分析，ARCH §11.3 纪律）；
    BoundCallStatement 加 Receiver?；
  - this：宿主统一取 method.Owner（普通成员 = 声明类型，ext 方法
    = 目标类型——AttachToExtTarget 后即定）；静态上下文诊断；
  - 实例链上色：receiver 静态类型沿 BaseType 链查找（接口
    receiver 查接口成员；ext 注册成员同路径——P2 已挂目标类型
    成员表）；裸名实例字段/方法补 this（FindField/FindMethods
    宿主链改 method.Owner）；MatchSingleCandidate 提取共享；
    ContainsGenericParameter 统一 S9 拦截；访问控制归 S8；
  - for 双形态：范围循环 a to b → 类型一致检查 + EnumerateInRange
    ext operator 实例调用（Iterable 节点）；for-each 协议判定——
    实现 core.collections.IEnumerable\<TItem\>（**含「Iterable 类型
    自身即构造」分支**：operator 返回类型的静态类型就是接口）；
    循环变量 const（只读默认，规范未明）；DA for 后 = before。
- **P4a for 脱糖**（`Lowering/`）：实例三节点恒等降级（
  **LoweredInstanceCallExpression 的 Type 自带**——for 脱糖合成
  节点的 Origin 是 BoundLoop 语句，无法走 Origin 透传）；for 统一
  脱糖：前置 `.e = iterable.iterate()`（枚举器类型经
  GetConstructedType(IEnumerator 定义, TItem)，定义取
  MoveNextMethod.Owner）+ LoweredLoop 复用（Judge =
  `.c = .e.moveNext()`、Body 头 = `item = .e.current()`、
  Enumerator = null）——P4b 零新增。
- **P4b 实例发射开闸**（`Lowering/BilEmitter.cs`）：实例方法 fn
  定义（.args 顺序 .return → **.this = OwnerType 投影** → 普通
  参数，§9.2/§7.3——ext 成员同以 .this 表示被扩展值）；实例
  invoke（receiver 求值作首实参；接口方法 canonical 引用，分派
  归 Middleware）；get.field/set.field（§13.3 操作数序）；
  this → `$.this` 零指令；init/operator §8.4 声明形态开闸
  （getter/setter 仍跳过）；**EmitBuiltinExtMembers**（定稿外
  新增）：内建类型（IsBuiltin 不声明）的 ext 成员以 §8.4.1 裸
  条目输出——否则其 fn 定义引用未声明符号（反射枚举
  BootstrapSymbols 公共 TypeSymbol 属性，新内建类型自动覆盖）。
- **stdlib 三源**（与用户源同走 P1–P4）：`.bootstrap.latte`
  （基元自举源，SYNTAX §15.3：ext operator i32.EnumerateInRange）
  + `core/collections.latte`（IEnumerable\<T\>/IEnumerator\<T\>
  双接口 + RangeEnumeratorI32（**started_ 标志防 start-1 下溢**）
  + **RangeI32**（定稿外新增——operator 声明返回 IEnumerable 而
  枚举器只实现 IEnumerator，类型不兼容且双接口语义要求每次
  iterate() 产独立枚举器；RangeI32 正是可重入的枚举源））。
  **形态区分**：Latte 源码一律点号路径（`core.collections.IEnumerable`，
  对齐 §13.2 的 `core.ComparisonResult` 惯例）；`::` 仅属
  canonical/BIL 侧（§14.8）。
- **测试**：Binder 168→198、Lowerer 58→69、BilEmitter 57→66、
  StdlibSources 30→47（「恰好 1 棵」重写为三源结构断言）；既有
  黄金基线按 stdlib 新资源段（R_0–R_4 固定）重算 10 处 + hello
  world 黄金全文重生（含 stdlib 六 fn：ext operator/init×2/
  moveNext/current/iterate）；全量 1634/1634 + fuzz 6000/6000
  （40 套件）。端到端验证：临时 `s7c2_check.latte`（范围循环
  println + RangeI32 for-each）经 `--emit-bil` 出双 loop 多
  block BIL（合成局部独立、iterate 前置、协议 invoke 接口
  canonical），逐行核对后已删。

### 2026-08-01 · M47 S7c-1 while/do-while/break/continue

> ROADMAP S7c-1 落地，P3/P4 同步推进，同批完成循环协议定稿
> （SYNTAX §7.3/§13.2/§15.3：范围循环半开 [a,b)、`to` 即
> EnumerateInRange、IEnumerable\<T\>/IEnumerator\<T\> 双接口、
> 基元实现归 SDK 自举源——for 双形态本身归 S7c-2）。本步交付
> while/do-while/break/continue 的全链路：循环是第一种「条件求值
> 必须重复执行」的结构，由此确立 Judge 块机制与 .breakid capability
> 的合成局部表达。

- **P3 循环绑定**（`Semantic/Bound/BoundStatements.cs` +
  `Semantic/Binder.cs`）：
  - 新节点：`BoundLoop{Kind, Label, Condition, Body}`（Kind 复用 AST
    LoopKind；**施工壳**——循环标签栈要求壳先于体绑定存在，体内
    break/continue 引用命中壳，仿 BoundValueBlock 先例）+
    `BoundLoopControl{IsBreak, Target}`（引用相等即身份）；
  - 循环标签栈（BindSession）：体绑定前压壳、绑完弹栈回填；
    break/continue 无标签命中栈顶（栈空报 outside of loop）、named
    从内向外查（未命中报未定义标签）；穿透值块命中外层循环合法
    （BIL §16.5 动态结构作用域）；for 报 not supported yet (S7c-2)；
  - 条件 bool 检查与 if 共用 CheckBoolCondition（加 construct 参数）；
  - definite assignment 循环两规则（S7c-1 定稿）：while 后 = before
    （体可能零次执行，条件赋值效果同保守）；do-while 后 = 体尾集合
    （体至少一次，条件在体后求值、其效果保守丢弃）；
  - GuaranteesValueReturn 扩展：末语句 BoundLoopControl 视为路径
    终止（该语句必以值块外循环为目标）；GuaranteesReturn 循环保守
    false（`while (true)` 特例留口，注释标记）；
  - **return@ 隔循环边界拦截**（定稿外新增）：值块栈记 LoopDepth，
    return@ 命中时当前循环更深即诊断——脱糖只写值块局部，无法
    表达跳出中间循环，放行会产语义错误的 BIL（S7c 技术债）。
- **P4a 循环降级**（`Lowering/`）：`LoweredLoop{IsRev, Judge,
  Condition, Body, Enumerator?, BreakId}` + `LoweredLoopControl
  {IsBreak, BreakId}` 新节点；
  - Judge 块：条件表达式经复用的 LowerAssignInNewBlock 在独立块
    上下文降级并写合成 bool 条件局部（.sN）——条件内短路/if
    表达式的前置语句天然落 Judge（§16.3：每次读取 CONDITION 前
    由 JUDGE_BLOCK 赋值）；
  - **.breakid 局部的 LocalSymbol 表达方案**：`LocalSymbol.Type`
    放宽为 `TypeSymbol?`（null 仅限 .breakid capability，§9.3 无
    对应 TypeSymbol），.bN 独立计数器命名；5 处连锁——emitter
    .vars 平铺 null → `.breakid`、Lowered 值引用透传 null →
    CompilerInternalException（capability 不可读）、Binder 构造
    Bound 值引用 `local.Type!`、两描述器 TypeShort 收可空显示
    `.breakid`；
  - BoundLoop → BreakId 映射栈（仿值块映射栈）；break/continue 是
    BIL 真跳转，穿透值块零展开——**对 if 转换与值块截断逻辑零
    改动**（块内其后指令自然不可达；IsValueBlockWrite 不误判）。
- **P4b 循环发射**（`Lowering/BilEmitter.cs`）：`loop`/`loop.rev`
  （§16.3/§16.4：操作数序 cond/body/none/judge/breakid，块 id
  `loop0-body`/`loop0-judge` 函数内递增，Enumerator 非 null 时
  `blk(loop0-enum)` 分支已写全、本步恒 none）+ `break`/`continue`
  （§16.5）+ `.vars` 的 `.breakid` 条目（§9.3）。
- **测试**：Binder 142→168、Lowerer 43→58、BilEmitter 45→57；全量
  1567/1567 + fuzz 6000/6000（40 套件）。端到端验证：临时
  `s7c1_check.latte`（while 累加 + continue/break@outer + do-while
  + println）经 `--emit-bil` 出 loop0/loop1 + if0/if1 多 block BIL，
  逐行核对 §16.3/§16.4 操作数序、§16.5 跳转、§9.3 `.breakid` 条目、
  §9.4 落尾规则后已删。

### 2026-08-01 · M46 S7b if 语句/表达式 + 值块 + 短路 and/or + 复合赋值

> ROADMAP S7b 落地，P3/P4 同步推进：P3 新增五个 Bound 节点与值块
> 标签栈 return@ 绑定、definite assignment 分支合并、GuaranteesReturn
> 双分支升级；P4a Lowerer 由静态恒等重写改为 session 化（前置语句
> 机制 + 合成局部 `.sN`），落地短路展开、值块降级与 if 转换、复合
> 赋值脱糖；P4b BilEmitter 出多 block BIL（§16.2 if 指令）。控制流
> 的第一种形态端到端全通。

- **P3 节点与绑定规则**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：`BoundIfStatement{Condition, ThenBlock, ElseBlock?}`
    （else if 链包成单语句 BoundBlock）/`BoundValueBlock{Label, Block,
    Type}`（值块：M33 隐式取值判定——单 ExpressionStatement 块隐式
    取值，多语句块必须显式 return@）/`BoundIfExpression{Condition,
    ThenValue, ElseValue}`（产值类型统一，双分支必须兼容）/
    `BoundReturnValueStatement{Label, Value}`（显式 return@ 取值）/
    `BoundCompoundAssignmentExpression{Op, Target, Value}`；
  - 值块标签栈：Binder 维护值块标签栈，return@/return@_ 沿栈解析
    目标值块，未命中报 P3 诊断；GuaranteesValueReturn 检查多语句
    值块所有路径显式取值（穿透 return@ 按「路径终止」处理）；
  - definite assignment 分支合并：if 后状态 = before ∪ (setThen ∩
    setElse)，无 else 分支 = before；GuaranteesReturn 升级识别双
    分支均终止的 if；
  - 复合赋值：读语义（unassigned 检查）+ intrinsic 键查询（同二元
    运算），写回目标必须是可写局部/字段。
- **P4a 脱糖与 if 转换**（`Lowering/Lowerer.cs`，静态类改实例
  session：前置语句列表 + 值块映射栈 + 合成局部计数器）：
  - 短路 and/or 按 §11.3 展开为 if 块：左操作数只求值一次，结果
    物化到合成局部 `.sN`（BIL §5.1 编译器保留名，与用户标识符零
    冲突）；
  - **算法决策——`LoweredConstantExpression` 的由来**：展开备选
    「`s = a` 再判 s」会让左操作数二次求值、副作用重复，故否决；
    引入常量节点承载 bool 字面量初始化（常量自带 Type，不走
    Origin 透传；S7b 仅 bool 一种形态）；
  - 值块降级与 if 转换：「值块写入」判定 = 赋值目标是值块映射栈
    中的 `.s` 局部（短路/if 表达式结果局部不在栈上，互不混淆）；
    单分支终止时把后续语句移入不终止分支末尾，无 else 则新建，
    双终止全丢弃；嵌套块递归处理；
  - 复合赋值脱糖为前置赋值语句（表达式值 = 写回后值）。
- **P4b 多 block 发射**（`Lowering/BilEmitter.cs`）：§16.2
  `if $c blk(then) blk(else)`，无 else 用 none 操作数；block id 取
  `if0-then`/`if0-else` 形态（无点号，与 `.entry` 区分）；分支块
  落尾不补 ret；合成 bool 常量与字面量同键去重进 Resources。
- **测试**：Binder 98→142、Lowerer 26→43、BilEmitter 31→45；全量
  1514/1514 + fuzz 6000/6000（40 套件）。端到端验证：临时
  `s7b_check.latte`（`var r = if ((x > 0)) { return@_ 1 } else {
  return@_ 2 }` + `(r > 0) and (x > 0)` + 无 else if 语句 + `x += r`）
  经 `--emit-bil` 出 if0/if1/if2 多 block BIL，人工逐行核对后已删。

### 2026-08-01 · M45 S7a P4 基础发射补齐

> ROADMAP S7a 落地：P3（S5）能绑定的全部 Bound 节点在本步过 P4——
> Lowered 节点补齐、Lowerer 恒等重写全覆盖、BilEmitter 新发射
> 赋值/运算/带返回值调用/new 与 §19.1 标量资源全形态。控制流的前置
> 就此就绪：没有赋值/运算/调用/new 的发射，任何控制流端到端用例都
> 写不出来。`Tests/LoweredDescribe.cs`（唯一 Lowered 树描述器，仿
> BoundDescribe）同步落地，供 S7b+ 脱糖用例消费。

- **`Lowering/Lowered/` 补齐八类节点**（字段仿对应 Bound 节点，Origin
  必填机制不变）：语句 `LoweredLocalDeclarationStatement{Local,
  Initializer?}` / `LoweredExpressionStatement{Expression}` /
  `LoweredAssignmentStatement{Target, Value}`；表达式
  `LoweredFieldReferenceExpression{Field}` /
  `LoweredBinaryExpression{Op, Left, Right}` /
  `LoweredUnaryExpression{Op, Operand}` /
  `LoweredCallExpression{Method, Arguments}` /
  `LoweredNewExpression{Init?, Arguments}`。
- **`Lowering/Lowerer.cs`**：分发覆盖 S5 全部 Bound 语句与表达式
  （六语句 + 七表达式），仍为恒等重写——bool 短路 and/or 等脱糖归
  S7b+ 独立 rewriter；未覆盖节点保持 P4 Error + 跳过函数体行为。
- **`Lowering/BilEmitter.cs` 新发射**（统一风格：表达式求值结果先物化
  到 `.t` 临时变量，再经 set.var 写入目标）：
  - 局部声明：无初始化器 → 无指令（.vars 已声明）；有初始化器 →
    求值 + `set.var $t $x`（§13.2）；赋值：局部目标 `set.var`、
    全局/static 字段目标 `set.field.static $src type(OWNER)
    field(FIELD)`（§13.4）；
  - 字段读取 `get.field.static $t type(OWNER) field(FIELD)`；OWNER
    投影：static 字段 = 宿主类型 canonical，命名空间全局字段 =
    命名空间全名（§13.4 未规定全局字段宿主形态，以命名空间全名
    投影，verifier（S12）再核）；根全局命名空间字段无宿主可投影——
    规范空白，报 P4 Error 而不发明语法；
  - §11 运算：`BilIntrinsicOp` → opcode 单点静态映射表（add/sub/mul/
    div/opposite/and/or/not/bin.*/shift.*/cmp.* 全 21 成员），形态
    `add $a $b $t` / `opposite $a $t`。**已知过渡**：内建 bool 短路
    and/or 当前直接发 and/or——§11.3 要求的「if + 临时变量」展开是
    S7b P4a 脱糖职责；
  - 带返回值调用 `invoke fn(CANONICAL) $t [args]`（§15.1）；new
    `new type(TYPE) $t [args]`（§14.1，init 选择归 Middleware，发射
    不写 init 符号）；表达式语句求值物化后结果丢弃；
  - §19.1 标量资源全形态：bool（`bool true`）、char（`char 'A'`，
    转义表同字符串外加单引号）、f32/f64（round-trip 格式保精度，
    f32 先收窄回 float 再打印避免双精度尾巴）、null（`null
    type(CANONICAL)`，CANONICAL = P3 定型的 Nullable\<T\> 之 T 的
    canonical，走 BilNullResource；去重键机制不变）。
- **测试**：`Tests/LoweredDescribe.cs`（唯一 Lowered 描述器，格式与
  BoundDescribe 对齐）+ `Tests/LowererTests.cs`（26 用例：每类节点
  Lowered 形态快照 + Origin 回指/字段与 init 符号引用相等结构断言 +
  未覆盖节点负例——S7a 后源码无法触达 default 分支，以测试私有
  Bound 子类注入模拟「未来节点」）；`Tests/BilEmitterTests.cs` 扩至
  31 用例（声明+初始化器、var 推断、赋值、算术+比较、一元、带返回值
  invoke、表达式语句、new、bool/f64/f32/char/null 资源、static 字段
  读写，均走 EmitUnit 全管线 + fn 指令/.vars/Resources 精确文本比对；
  原「二元运算未覆盖」负例已失效，改为实例方法 .this receiver 负例）；
  TestRunner 注册表尾部追加 ("Lowerer", LowererTests.RunAll)。

### 2026-07-31 · M44 S6 P4 最小闭环（端到端 hello world 出 BIL）

> ROADMAP S6 落地：P4 = Lowerer（P4a 恒等重写）+ BilEmitter（P4b 发射），
> 中端四 pass（P1 声明收集 / P2 声明解析 / P3 函数体分析 / P4 降级与发射）
> 全部打通——hello world 从 Latte 源码端到端产出合法 BIL 文本，
> 黄金输出与 BilWriter 排版逐行一致。本里程碑建立在 M43 stdlib 内嵌源
> （core.io::Console 与用户源同走 P1–P4）之上；CLI `--emit-bil` /
> `--sema-only` 接线在本里程碑收尾落地，S6 就此收官。

- **`Lowering/Lowered/`（三文件，仿 `Semantic/Bound/` 分文件风格）**：
  `LoweredNode{Origin: BoundNode}`（必填回指，ARCHITECTURE §6.1）+
  `LoweredExpression`（`Type` 直接透传 `((BoundExpression)Origin).Type`，
  不冗余存储）+ `LoweredStatement` + `LoweredFunctionBody{Method, Locals,
  Body}`（非 LoweredNode，仿 BoundFunctionBody）。节点只收 S6 最小集
  五类：`LoweredBlock` / `LoweredCallStatement{Method, Arguments}` /
  `LoweredReturnStatement{Value?}` / `LoweredLiteralExpression`（无额外
  字段，值经 Origin.Syntax 的 LiteralExpressionASTNode.Literal 取）/
  `LoweredValueReferenceExpression{Symbol}`——不镜像 Bound 全部节点，
  其余种类随 S7+ 脱糖落地增补。
- **`Lowering/Lowerer.cs`（P4a）**：`Lowerer.Lower(unit, bodies)` 静态
  入口，恒等重写五类节点（含嵌套 BoundBlock 与 return 值表达式）；
  遇未覆盖节点 → P4 Error 诊断（消息含 "not supported by minimal
  lowering (S7)"）并跳过整个函数体（不产出 LoweredFunctionBody）——
  函数体之间诊断互不阻断，与 P3 同原则。§6.1 脱糖清单（短路展开、
  cast 插入、复合赋值、字符串插值等）随 S7+ 逐项落地为独立 rewriter。
- **`Lowering/BilEmitter.cs`（P4b）**：`BilEmitter.Emit(unit, bodies,
  moduleName) → BilModule`，私有 EmitSession 承载全部状态：
  - **Metadata**：`module = string "<moduleName>"`（§4.1）。
  - **LocalSymbols**：从 GlobalNamespace 递归平铺（类型含 NestedTypes
    → 子命名空间 → 本空间全局字段/函数裸条目 §8.4.1）；跳过 IsBuiltin
    （bootstrap 基元经 .string/.i32 别名投影，不是符号引用）与 ErrorType。
    kind 映射五种类；修饰符 = 访问（全显式 pub/protected/internal/priv）
    + open/abstract/singleton + rich/shared（wrapper 恒 rich 也显式输出，
    §8.2）；**extends 与种类默认基类相同则省略**（确认 P2 行为：P1 建壳
    即填默认基类 class→Object / struct→ValueType / enum struct→Enum /
    wrapper→Wrapper，P2 仅覆盖显式继承），不同才输出
    `extends <PrintType>`；implements 逐个输出。方法成员：IsNative →
    `native symbol("...") lib("...")` 三件套（§8.4）；entrypoint 判定 =
    全局命名空间裸 main；`Kind != Regular`（init/operator/getter/setter）
    第一版报 P4 Error 跳过该成员。ExternalSymbols 本阶段恒为空段。
  - **Resources**：字面量提取（§4.2 指令不得内联字面量）——键 =
    (BIL 资源类型关键字, 字面量原文) 去重，名 = R_0/R_1... 按
    （bodies 顺序 + 树内先序）首次出现编号。字符串值取 Syntax 解码后
    Value，按 Lexer StringEscape 同集逆向重新转义为 BIL 原文
    （`\n` 输出为转义形态）；整数按 IntType 八值映射关键字（i32 等，
    §19.1 类型关键字无前导点）。float/bool/char/null 第一版报 P4 Error。
  - **Functions**：每 LoweredFunctionBody → BilFunction——`.args`
    （`.return` 在前，void 写 `.void`；非 static 实例方法需 `.this`，
    第一版报 P4 Error 跳过）；`.vars`（Locals 在前、临时变量在后）；
    单 `.block entry entrypoint`：LoweredCallStatement → 实参从左到右
    EmitValue 物化 → `invoke.noret fn(...) [$a, ...]`；return →
    `ret` / `ret $x`；嵌套块语句平铺。EmitValue：字面量 → 登记资源 +
    新临时变量（`.t0`/`.t1` 编译器保留前缀，§5.1 与用户变量零冲突）
    → `load res(R_k) $.tN`；值引用 → 直接 `Symbol.Name`。void 函数
    末尾无 ret 补 `ret`（§9.4：entrypoint block 不得落到末尾——
    stdlib println 体无显式 return）。每条指令 Origin = 对应
    LoweredNode（语句级；load 的 Origin = 字面量 LoweredNode），
    BilInstruction.Origin 保持 object? 不收窄（Bil 对中端零依赖，§6.3）。
  - 符号引用一律经 CanonicalSymbolPrinter 投影；有 P4 Error 时 Emit
    仍返回模块（§8 门槛：调用方不推进写盘，测试只跑无错路径）。
- **`Tests/BilEmitterTests.cs`（新套件，注册表 39 号）**：全管线
  helper（StdlibSources.ParseAll + 用户源组 CompilationUnit → P1 →
  P2 → P3 → P4a → P4b → BilWriter；用户源带文件名经
  `TestHarness.ParseRoot(code, sourceName)` 重载——注意是重载而非
  可选参数：方法组 `Select(TestHarness.ParseRoot)` 的类型推断依赖
  单签名）。覆盖：hello world 黄金输出逐行精确比对（Console 类型
  声明单行无 extends——与默认基类相同即省略的断言内嵌其中；两个
  fn 定义——println 实参直接 `$text` 无临时变量、末尾补 ret；main
  load/invoke.noret/load/ret）；Origin 调试链（invoke.noret.Origin
  is LoweredCallStatement → .Origin is BoundCallStatement → Syntax
  非空 → Span.sourceName == 用户文件名；load.Origin is
  LoweredLiteralExpression）；资源去重（相同字面量只登记一次、两处
  load 引用同一 R_1）；负例（`return (1 + 2)` → P4 Error 含
  "not supported"，main 无 fn 定义而 stdlib println 照常发射）。
  TestHarness 增 `Lines(...)` 黄金文本拼装（与 BilWriterTests 同源风格）。
- **CLI 接线（S6 收官，`Core/Commands.cs`）**：新增 `--emit-bil <路径>`
  （1 参）与 `--sema-only`（0 参）子命令（均与 `--parse-only` 互斥，
  注册进 `CompileCommand.SubCommands`，help 文本程序生成）；`Execute`
  的 `!parseOnly` 分支接入完整语义管线——stdlib（在前）+ 用户源组
  CompilationUnit → P1 → P2 → P3 → 诊断经 Logger 输出（格式
  `{sourceName}:{行}:{列} [{Phase}] {Message}`，走 stderr 不污染
  stdout）→ 有 Error 退出码 1 不发射；`--sema-only` 到此为止；
  无 Error 且 `--emit-bil` → P4 → BIL 文本写盘（UTF-8 无 BOM），
  moduleName 取首个 `--file` 文件名去扩展名。端到端实测：hello.latte
  发射产物与 BilEmitterTests 黄金输出逐行一致；P2/P3 错误路径
  诊断格式与退出码正确。CommandLineParser 套件增补子命令清单与
  互斥用例（46 → 50）。
- **未改动**：Bil/ 与 Semantic/ 现有文件零改动（依赖方向
  `Lowering → Semantic`、`Lowering → Bil` 单向保持）。

### 2026-07-31 · M43 native 函数机制 + stdlib 内嵌源载入

> 取代 ROADMAP S6 原「硬编码 `core::Console.println` external 符号」临时
> 措施（用户定稿）：Latte 获得正式的 native 函数语法，stdlib 以 .latte
> 源文件内嵌随编译器载入（S10 机制最小子集提前），println 在 Latte 层
> 包装运行时原生方法面 `latte_rt` 的 print/printErr；BIL VM 未来经
> 内建 hook 表直接执行，无需原生库即可跑通 hello world。四份规范文档
> 同步修订（SYNTAX / BIL_STANDARD / RUNTIME / SEMANTIC_ROADMAP）。

- **语言规范（`docs/SYNTAX.md` §4.6 新增）**：`native` 函数修饰符 +
  `@NativeLibrary("...")`（必填）/ `@NativeSymbol("...")`（可省，缺省
  取函数名）编译器内建注解（非 wrapper 体系）。规则：必须无函数体；
  成员形态必须 static；禁止 init/operator/getter/setter/async/泛型/
  重载；参数与返回类型白名单（§3.2 整数、浮点、bool、char、String）；
  注解实参必须各为一个字符串字面量。§9.2 修饰符表与 §19 关键字表
  同步收录 `native`。
- **BIL 规范（`docs/BIL_STANDARD.md`）**：§8.4 方法修饰符表新增
  `native` / `symbol("...")` / `lib("...")`（native 声明不得有 fn 定义、
  symbol/lib 必随 native 各出现一次）；§8.4.1 新增全局函数/全局字段
  的裸 `.method`/`.field` 段内声明形态（补规范空白）；§21.2 补 verifier
  条目；§22.5 新增 VM 内建 hook 表（`(latte_rt, print)` → stdout、
  `(latte_rt, printErr)` → stderr，表外组合拒绝执行）。
- **运行时约定（`docs/RUNTIME.md` §26 新增）**：native 互操作 =
  C 编写的 `latte_rt` shim 库（libc ↔ Latte 调用约定，暂定 fastcall，
  细则归 Middleware 阶段）；第一版原生方法面仅 `print`/`printErr`
  两个定参函数（不做可变参数 printf_s）；VM 不链接原生库、经内建
  hook 执行。
- **前端**：`Parser/Keywords.cs` 加 `NATIVE`（入 DeclarationDescriptors
  白名单）——无体函数与 `@注解` 路径前端本已具备（interface 无体方法
  先例），Parser 层零改动；TypeDeclaration 套件加 3 个 native 用例
  （95 → 98）。
- **符号与校验（P1/P2）**：`MethodSymbol.IsNative`（P1 建壳读标记位，
  仿 IsStatic）+ `NativeSymbol`/`NativeLibrary`（P2 读注解后填，仿
  WrapperTarget）；`DeclarationResolver` 新增 `CheckNativeDeclarations`
  子任务（挂 CheckModifiers 后）：§4.6 全规则校验 + 注解解析写符号
  （@NativeSymbol 缺省取函数名）；`CheckWrapperApplications` 为两个
  内建注解加豁免（否则误报 "is not a wrapper type"）。同批落地
  `SemanticSymbol.Accessibility`（Public/Protected/Internal/Private，
  P2 由声明修饰符写入、默认 Private 与 §16 一致）——BIL 发射
  （pub/priv 投影）与 S8 使用点访问控制的前置。DeclarationResolver
  套件 138 → 179（native 全规则正反用例 + Accessibility 组）。
- **Binder（P3）**：`FindMethods` 查找序补「宿主类型成员」一环
  （沿 BaseType 链，先于命名空间链，对齐 ARCHITECTURE §2 既定查找序
  「块 → 参数 → 成员 → 全局 → import」）——stdlib println 体内裸名
  调用同类静态方法 print 由此绑定；实例方法命中由 BindCallee 既有
  静态性检查自然拦截。Binder 套件 90 → 98（裸名静态调用 / 实例拦截 /
  类容器多段路径调用 / 遮蔽优先级）。
- **Bil 模型**：`BilSymbolSectionEntry` 抽象落地 §8.4.1
  （BilTypeDeclaration 与 BilMemberDeclaration 均归之），
  `LocalSymbols`/`ExternalSymbols` 放宽为段条目列表；BilWriter
  WriteSymbolSection 分派裸成员输出（段内一级缩进，类型体内输出
  逐字不变）；native 修饰符即字符串列表项（`"native"`,
  `"symbol(\"print\")"`, `"lib(\"latte_rt\")"`），模型零结构改动。
  BilWriter 套件 6 → 7。
- **stdlib 载入**：`stdlib/core/Console.latte`（core.io::Console：
  priv static native print/printErr（lib `latte_rt`）+ pub static
  println 两次 print 包装，避开 String 拼接依赖）经
  `<EmbeddedResource Include="stdlib/**/*.latte">` 内嵌；
  `Semantic/StdlibSources.cs` 的 `ParseAll()` 按逻辑名排序取出解析为
  RootASTNode（sourceName 映射 `<stdlib>/...`），CLI 与测试共用入口；
  stdlib 文件即前端常驻回归测试（新 StdlibSources 套件 30 断言，
  注册表 38 号）。

### 2026-07-31 · M42 路径表达式统一（SYNTAX §1.4 忠实落地）

> 用户拍板的架构重构：表达式位置的「符号引用/调用/索引/成员访问/
> wrapper 访问」五种 AST 节点统一为单一路径表达式节点——语法层只表达
> §1.4 的形态事实（一条完整路径链恰一个节点），「首段是什么」与各段
> 语义的上色全部归 P3。消除 M41 暴露的双形态债（`c.m()` 与
> `core.Console.println()` 同构导致 Binder 双路径处理，原技术债第 1 条）。

- **AST**（`AST/ExpressionNodes.cs`）：删除 `SymbolReferenceASTNode` /
  `CallExpressionASTNode` / `IndexExpressionASTNode` / `MemberAccessASTNode` /
  `WrapperAccessASTNode`；新增 `PathExpressionASTNode`（`Head` +
  `Segments`）/ `PathHeadASTNode`（符号名或表达式底座，互斥）/
  `PathSegmentASTNode`（`PathConnector{Dot,SafeDot,Colon}` + 成员名 +
  泛型实参 + 后缀）/ `PathSuffixASTNode`（`PathSuffixKind{Call,Index}` +
  实参）。前导点 `.Failed`（EnumCaseExpression）保持独立——不是路径。
- **Parser**：`ExpressionParserLayer` 后缀链重写——符号起点不再委托
  PathParserLayer，直接创建 PathExpression；`.`/`?.`/`:` 追加段、
  `(`/`[` 追加后缀、`\<` 挂当前段/首段、trailing lambda 脱糖为 Call
  后缀；非符号起点（分组/字面量/new/enum case）遇路径后缀经
  `EnsurePathExpression` 包装为表达式底座。WrapperNameExpected 状态
  并入 MemberNameExpected（连接符区分，错误消息保持原样）。
  `ArgumentListParserLayer` 具名判别 seed 改为路径形态（技术债第 1 条
  的「seed 双形态」一并消除）。Span 施工：首段记名/底座范围、段记
  连接符到名尾（泛型在 `>` 处扩）、后缀经 currentPathTail 生长封口。
- **能力并集**：表达式路径的泛型实参统一走 TypeReferenceParserLayer
  ——中段泛型的可空实参 `a.b\<String?>.c` 从语法错误变合法
  （原 Symbol 形态不支持可空、MemberAccess 支持，统一取并集）。
- **Binder 适配**：BindSymbolReference/BindCallee 双入口合并为
  `BindPath` 单点上色——表达式底座/泛型段/索引后缀/安全访问/wrapper
  段各自归口诊断（S7/S8/S9/S11）；纯调用形态经 `TryGetCallForm`
  判定（全 Dot 段 + 唯一 Call 后缀）走直接调用；纯值路径（无后缀）
  走单段/多段查找序。顺手修复 M41 遗留：一元 `+x` 前缀合法但
  Binder 抛内部异常（正号按恒等处理）。
- **测试**：`AstDescribe` 新 Path 格式（`Path(head, [.seg, ?.seg, :seg])`，
  段带 `<T>` 与 `(args)/[args]`）；171 用例期望串迁移（14 套件，
  全部为格式替换，零行为变化——结构断言/错误消息断言零修改）；
  **Binder 套件 90 用例未动即绿**——bound 产物与源码语义在重构前后
  完全一致，是对「纯形态重构」的最强验证。TypeOf/ASTIntegrity/
  ExpressionParser 三处手工构造 AST 适配新节点。
- **文档**：`EXPRESSION_ARCHITECTURE.md`（后缀链框架、统一路径段、
  两个施工流程示例、PathParserLayer 职责收窄）与 `FRONTEND_TYPES.md`
  （节点表）同步。

### 2026-07-31 · M41 S5 P3 最小闭环（BoundTree 起步）

> ROADMAP S5 落地：函数体分析（Binder）上线，AST → BoundTree。
> 中端四 pass 已通其三（P1/P2/P3），下一步 S6 出 BIL。

- **`Semantic/Binder.cs`**（`Binder.Bind(unit, decls) →
  IReadOnlyList<BoundFunctionBody>` 静态入口 + 私有 BindSession，
  全程可恢复诊断、函数体间互不阻断）：
  - **分析单位**：遍历声明骨架（五类类型声明递归 + CallableDeclaration
    有体者）逐函数绑定；每函数产物
    `BoundFunctionBody{MethodSymbol, Locals, BoundBlock}`
    （ARCHITECTURE §5.1）；全局字段初始化器/getter/setter 体 S5 不分析。
  - **BoundTree 节点集**（`Semantic/Bound/` 三文件）：BoundNode
    （Syntax 必填回指）/ BoundExpression（Type 定型）/ BoundStatement；
    字面量、BoundValueReference（LocalSymbol/ParameterSymbol 合一）、
    全局字段引用、二元/一元 intrinsic、BoundCallExpression（有值）/
    BoundCallStatement（void 调用语句，两节点分开保住 Type 非空契约）、
    BoundNewExpression、块/局部声明/表达式语句/赋值/return。
  - **定型与推断**：字面量按种类映射 bootstrap（IntType 八值、
    float/double、String/char/bool；null 走 expectedType 可空上下文）；
    var 推断、标注与初始化兼容检查（IsAssignable：严格相等、
    `T → Nullable\<T>` 可空提升、BaseType 链、直接 interface；
    ErrorType 毒化静默；显式 cast 物化归 P4a，BIL §6.5）。
  - **intrinsic 键查询**（BIL §11）：二元/一元运算符文本 →
    BilIntrinsicOp 全表映射；两操作数严格同型 + 操作数类型键集命中
    才合法；结果类型维度——比较 bool、余同操作数（bool and/or 在此
    仅定型，短路展开归 P4a，§11.3）。
  - **调用与构造**：无重载直接调用（候选按实参个数唯一匹配，
    多候选报「重载归 S8」）；具名实参按形参归位，产物即规范参数序
    （默认填充归 S8）；new 解析类型（class/struct 可构，interface/
    enum struct/wrapper 各自诊断）+ init 个数匹配。
  - **return 与 definite assignment**：返回类型兼容（void/非 void
    互斥诊断）；「所有路径显式返回」按末语句递归判定（S5 无控制流，
    if/loop 接入后扩展）；局部变量未赋值使用诊断、赋值即登记
    （赋值目标绑定走 forAssignment 免查——目标不是「使用」）。
  - **名字解析**：值/调用查找序为 块作用域链 → 参数 → 命名空间链
    字段/函数 → 通配 import 容器；多段路径 = 前 N-1 段容器
    （NameResolver）+ 末段成员；**首段命中局部/参数即实例成员路径，
    报「归 S8」诊断**——`c.m()` 在前端是路径形态（§1.4），与
    `core.Console.println()` 同构，只能靠绑定期首段解析区分。
  - **明确不做**（遇之报 P3 诊断而非崩溃）：控制流全家（S7）、
    成员访问/receiver（S8）、重载 ranking 与默认填充（S8）、泛型
    使用侧（S9）、enum case（S11）、字符串插值（S7）、await（S13）。
- **`Semantic/NameResolver.cs`（共享设施提取）**：P2 的名字解析核心
  （ResolveSymbolPath/ResolveDottedPath/ResolveFirstSegment/
  ApplyTypeArguments 等约 220 行）从 ResolveSession 私有方法提取为
  internal 设施，诊断按构造传入的 Phase 落袋；P2 改为委托
  （138 用例零回归），P3 以 DiagnosticPhase.P3 实例化复用——
  函数体内的类型引用（局部标注、new）与声明骨架同一条解析路径。
- **符号家族增补**：`LocalSymbol`（P3 产生，挂 BoundFunctionBody.
  Locals，不进符号图容器表、不受 Freeze 约束；参数仍归 ParameterSymbol）。
- **前端事实适配**（两件，测试暴露）：① 括号表达式产生
  `GroupExpressionASTNode`（无优先级语言里组合运算必括号）——Binder
  透明下钻不落节点；② 裸嵌套块 `{ ... }` 不是合法语句（前端只认
  固定位置的代码块）——嵌套作用域测试改为单层规则断言，BindBlock
  的嵌套能力留待 S7 控制流接入。
- **`Tests/BoundDescribe.cs`**：唯一 bound 树描述器（仿 AstDescribe，
  字面量值经 Syntax 回指取、定型类型短名 Nullable\<T\> → `T?`）；
  `Tests/BinderTests.cs`（90 用例，37 号套件）：11 组——字面量、
  局部声明、值引用、二元/一元运算、赋值、调用、new、return、
  作用域、诊断累积；含结构性事实断言（符号引用相等、Syntax 回指、
  Locals 独立）与三类诊断（类型不匹配/未定义名字/未赋值使用）用例。

### 2026-07-31 · M40 S3 P2 声明解析

> ROADMAP S3 落地：P1 的符号壳全部获得真实类型与合法身份。
> Parser 的语义即死拦截正式移交中端可恢复诊断，P1→P2 链路闭环。

- **`Semantic/DeclarationResolver.cs`**（约 1400 行）：
  `DeclarationResolver.Resolve(unit, decls)` 静态入口 + 私有
  `ResolveSession`（全程可恢复诊断，结束 `unit.Symbols.Freeze()`），
  七个子任务全部落地：
  1. **类型引用解析**：查找序为泛型参数（方法 → 宿主类型链）→ 宿主
     NestedTypes → 文件命名空间父链 → 全局命名空间 → imports →
     core 隐式；`T?` 脱糖 `Nullable\<T\>`；解析失败绑
     `ErrorTypeSymbol`（毒化静默——后续检查遇 ErrorType 一律放行，
     单点报错不级联）。
  2. **init 映射**（§9.3）：`_ -> field` 参数类型节点为空时沿字段
     解析后类型；字段无类型标注即诊断（推断规则，§6 第 9 条）。
  3. **继承/implements 图**：种类匹配（class 承 class、interface 实现
     interface、struct 承 struct）、open/abstract 可继承性、基 struct
     必 open rich、struct 禁 implements；class/interface 双环检测
     （沿基类链与接口集 DFS）。
  4. **修饰符合法性**：rich 仅 struct 系（含 enum struct）、shared 仅
     class/struct 系、open 仅 class/struct、open interface 合法、
     重复修饰符、访问互斥、async 仅函数、ext 限定名与全局位置——
     全部可恢复诊断。
  5. **单向传染 + 字段闭包**（§3.1.1 七行表）：HolderCategory ×
     FieldCategory 逐格判定；直接分类违规即报不展开，放行才展开泛型
     实参代入递归（Substitute + visited 去重）；
     `Nullable\<泛型参数\>` 分类 Unknown。
  6. **共享安全闸门**：全局变量/静态字段/ext 静态成员持非共享安全
     类型即诊断；ext 实例成员不闸门；含泛型参数实参跳过。
  7. **泛型约束 + ext 注册 + wrapper 适用性**：约束 Target 必须本声明
     泛型参数、with 边界必须 wrapper；ext 成员 Owner 改写挂目标类型；
     wrapper 缺 @WrapperTarget 即诊断（推断规则，§6 第 9 条）、
     类别匹配、宿主可内嵌性、§14.9 shared 矩阵 A/B/D、interface
     实现者传染；栈上变量 C 行归 P3。
- **Parser 两处越权拦截移交 P2**（ARCHITECTURE §2/§8 可恢复诊断分工）：
  `DeclarationParserLayer.CreateTypeNode` 的 M31 三条即死校验
  （rich 仅 struct 系 / shared 仅 class/struct 系 / open 仅 class/struct——
  误杀合法的 shared wrapper）与 `OnModifiers` 的重复修饰符、
  open×abstract 互斥校验全部删除，同规则在 P2 以诊断重现。
- **Parser 修复：约束裸名参数双注册**（`GenericParametersParserLayer.
  HandleTargetParsed`）：`T extends Bound` 形态的裸名 Target 此前只进
  Constraints 不进 Parameters，P2 无法解析参数符号；现裸名 Target 先
  `CommitParameterFromTarget` 再 `StartConstraint`（非裸名保留纯约束
  目标归 P2 诊断），新增 `IsBareIdentifier` helper（不抛错版
  TryConvertToParamName 同规则）。GenericParameters/Lambda/
  TypeDeclaration 三套件期望串同步（参数在前约束在后）。
- **bootstrap 补登 `Core.Types`**：19 个内建类型此前只有直造属性未入
  容器成员表，裸名 `i32`/`String`/`Object` 路径解析全部失败——
  `BootstrapSymbols` 末尾统一注册（Span/Box 约束写法同步适配新
  Constraints 列表模型）。
- **符号模型增补**（`Semantic/Symbols/`，S1 家族按需扩展）：
  `ErrorTypeSymbol`（SymbolGraph.ErrorType 单例）、
  `WrapperTargetKind{Entity,Value,Method}`、`GenericConstraintInfo`
  （复用 AST 的 GenericConstraintKind）、TypeSymbol 增
  Interfaces/WrapperTarget/AppliedWrappers/IsOpen/IsAbstract/IsSingleton、
  Field/Method/Parameter 类型字段放宽 `SemanticSymbol?`（泛型参数可作
  类型）、`GenericParameterSymbol.Constraint`→`Constraints` 列表 +
  IsVariadic/IsNamedVariadic、Field/MethodSymbol 增 AppliedWrappers 与
  `AttachToExtTarget`（Owner/Namespace 改 private set）。
- **测试**：`Tests/DeclarationResolverTests.cs`（138 用例、13 组：
  类型解析/init 映射/继承图/修饰符/传染/闭包七行逐行/闸门/约束/ext/
  wrapper 矩阵 A/B/D 逐格/Freeze），注册为 TestRunner 第 36 号套件。
- 全量：1204/1204 + fuzz 6000（36 套件）。

### 2026-07-31 · M39 S2 P1 声明收集

> ROADMAP S2 落地：符号图的首个真实消费者——编译单元全部声明骨架
> （不进函数体）建壳入库，跨文件前向引用就此成立。

- **`Semantic/CompilationUnit.cs`**：编译单元模型（ARCHITECTURE §3）——
  多源文件 `RootASTNode` 集合 + 全局 `DiagnosticBag` + 唯一 `SymbolGraph`。
- **`Semantic/DeclarationCollector.cs`**：`DeclarationCollector.Collect(unit)`
  静态入口 + P1 产物 `DeclarationCollection`（声明 AST 节点 → 符号映射
  `SymbolOf`、每文件 `FileContext`（命名空间 + import 列表）、
  ext 待注册列表 `PendingExtMembers`）。
- **建壳规则**：五种类型声明 → `TypeSymbol`（默认基类建壳即定——
  class→Object / struct→ValueType / enum struct→Enum / wrapper→Wrapper，
  保证 `IsValueTypeBranch` 构造期传播正确，显式基类留 P2 覆盖）；
  变量声明 → `FieldSymbol`；可调用声明 → `MethodSymbol`（Kind 映射 +
  参数/泛型参数壳）；rich/shared/static 只读标记位建壳，合法性检查
  一律归 P2；wrapper 恒 rich（§14.9）。getter/setter 与 enum case
  不建壳（符号家族按需增补，S8/S11）。
- **namespace**：`SymbolGraph.GlobalNamespace`（空名单例）+
  `GetNamespace` 逐段驻留——同路径必同实例，多文件同 namespace 声明
  天然合并；bootstrap 的 core 挂入全局命名空间树（用户 `namespace core.*`
  与 bootstrap 共享驻留路径）；`FullName` 拼段跳过空名父级。
  §15.1 唯一性与位置约束落地（P1 诊断：每文件至多一个 namespace、
  须先于任何类型/成员声明；§6 技术债务第 1 条勾销）。
- **ext 拆名登记**（§4.4）：限定名最后一段为成员名、前缀为
  `ExtTargetPath` 原文；ext 壳不进声明容器表，进 `PendingExtMembers`
  待 P2 解析注册到目标类型（`MethodSymbol`/`FieldSymbol` 增
  `ExtTargetPath` 属性）。
- **重复声明诊断**（累积不中断）：同容器类型同名、变量同名、
  方法 P1 文本级签名（同名 + 同参数名序列 + 同参数类型源码文本）
  全同必为重复——重载不误报，签名级精确判定依赖类型解析归 P2；
  重复符号不进容器表（保留第一个）但仍登记 `SymbolOf` 映射。
  收集期容器视图 `Scope` 借用宿主符号三张成员表 + 方法签名 key 表，
  生命周期 = 容器成员收集全程（修复：每声明新建临时 Scope 导致
  MethodKeys 随建随丢、方法重复检测失效）。
- **符号模型增补**（`Semantic/Symbols/`，S1 家族按需扩展）：
  `NamespaceSymbol` 容器成员表（ChildNamespaces/Types/Fields/Methods）、
  `TypeSymbol.NestedTypes`、`MethodSymbol.GenericParameters`。
- **测试**：`Tests/DeclarationCollectorTests.cs`（83 用例：全局/类型/
  嵌套/namespace/import/重复/ext/namespace 诊断/跨文件九组，
  符号断言一律引用相等，canonical 路径经 CanonicalSymbolPrinter
  验证），注册为 TestRunner 第 35 号套件。
- 全量：1066/1066 + fuzz 6000（35 套件）。

### 2026-07-31 · M38 S4 BIL 对象模型 + BilWriter

> ROADMAP S4 落地：BIL 生态基座（对中端零依赖）。中端三条基建线
> （S0 诊断 / S1 符号图 / S4 BIL 模型）至此全部就位。

- **`Bil/BilModule.cs`**：`BilModule`（`BIL "1.1"` 版本头 + Metadata +
  Resources + LocalSymbols + ExternalSymbols + Functions，§4 段顺序与
  全段输出）+ `BilMetadataEntry`（§4.1）+ 资源家族（§19 全形态：
  `BilScalarResource` 标量/raw、`BilNullResource`、`BilCollectionResource`
  array/pair/map/switch-table/catch-table 单行与多行排版）。
- **`Bil/BilSymbols.cs`**：类型声明（§8.2：kind/extends/implements/
  修饰符，多行续行形态；`generic(...)` 子句注记 S9 增补）+
  `BilSimpleMemberDeclaration`（.field/.static-field/.method/
  .static-method 共形态：keyword + canonical symbol + 修饰符，
  `ModifiersOnNextLine` 复现 §20 wrapper 示例续行）+
  `BilCaseDeclaration`（§8.5：参数段 + discriminant auto/res(R)）。
- **`Bil/BilFunction.cs`**：`BilFunction`（§9.1）+ `.args` 条目
  （名 = 类型，保序）+ `.vars` 条目（类型 名）+ `BilBlock`
  （§9.4–§9.6：id + entrypoint/volatile 修饰符）。
- **`Bil/BilInstructions.cs`**：`BilInstruction`（opcode + 操作数列表
  通用形态承载 §10–§16 全部标准指令；结构化理解留给 S12 verifier）
  + 操作数家族（§10.1 全集：`$var`/fn/field/type/case/blk/res/none/
  `[列表]`，none 单例）+ `BilOp` 便捷构造。协程指令 §17 暂缓
  （ARCHITECTURE §7 待修订清单，S13 专项）。`Origin` 以 `object?`
  占位（S6 接通后收窄为 `LoweredNode?`）。
- **`Bil/BilWriter.cs`**：模型 → 标准 BIL 文本（只输出标准 spelling，
  §5.6；4 空格缩进、段间空行、条目尾逗号、switch/try 规范多行排版；
  换行统一 `\n` 不随平台漂移）。
- **架构纪律**：`Bil/` 不引用 Semantic/Lowering/AST 任何类型——
  类型引用与符号一律以 canonical 字符串承载（字符串即 BIL 世界身份，
  S1 CanonicalSymbolPrinter 的投影即其来源）；verifier/VM 未来只依赖
  本目录。
- **测试**：`Tests/BilWriterTests.cs`（6 用例：§20 完整黄金示例逐行
  一致 + §20 wrapper 隐藏字段示例 + §19 资源全形态 + §8.2/§8.5 声明
  形态 + §10–§16 指令形态抽样 + Origin 默认 null；黄金文本经
  `Lines(...)` 显式拼 `\n`，autocrlf 免疫），注册为 TestRunner
  第 34 号套件。
- 全量：983/983 + fuzz 6000（34 套件）。

### 2026-07-31 · M37 S1 符号图内核 + bootstrap + CanonicalSymbolPrinter

> ROADMAP S1 落地：中端符号对象图的内核（SEMANTIC_ARCHITECTURE §4）。

- **`Semantic/Symbols/SemanticSymbol.cs`**：符号家族——基类
  `SemanticSymbol`（引用相等即身份，禁止按名字字符串比较）+
  `NamespaceSymbol`（多段路径嵌套）/ `TypeSymbol`（Kind 五类 +
  BaseType/IsRich/IsShared/IsBuiltin/IsValueTypeBranch +
  GenericParameters/Fields/Methods + 构造类型双字段
  ConstructedFrom/TypeArguments）/ `MethodSymbol`（MethodKind：
  Regular/Init/Operator/Getter/Setter）/ `FieldSymbol` /
  `ParameterSymbol` / `GenericParameterSymbol`（extends 约束）。
  构造期两阶段：P2 填充字段（BaseType/ReturnType/FieldType/Type/
  Constraint）internal set。
- **`Semantic/Symbols/SymbolGraph.cs`**：符号图容器——构造泛型类型
  驻留 cache（键 = (定义, 实参列表)，一律引用相等；同键必同实例；
  实参数组复制防外部改写）+ `GetNullable`（T? = Nullable\<T>，SYNTAX
  §3.4）+ `Freeze`（P2 结束冻结标记；驻留 cache 为幂等派生物不受限）。
- **`Semantic/Symbols/BootstrapSymbols.cs`**：硬编码 bootstrap
  （ARCHITECTURE §4.3）——SYNTAX §3.1 完整层级（Any/Object/ValueType/
  Enum/Wrapper 根 + i8–u64/float/double/bool/char/String 基元 +
  Type\<T\>/Span\<T\>/Nullable\<T\>/Box\<T\> 泛型内建，全部挂 `core`
  命名空间）；三条易错层级事实落实（String/Wrapper 在 ValueType 分支
  且 String 非 rich、Wrapper 恒 rich；Nullable/Box 在 Object 分支；
  Box\<T\> <: Object 经 BaseType 链直接表达）；`BilIntrinsicOp` 枚举 +
  每基元登记 intrinsic 集（BIL §11：整数=算术+位+比较、浮点=算术+比较、
  无符号无 Opposite、bool=逻辑+相等、char=比较全集、String=仅相等；
  char/String 边缘集注记 S5 按 §13.2 再核）；`IsSharedSafe()` 实现
  SYNTAX §3.1.1 白名单（含 Nullable 按 T 推导，经
  DerivesSharedSafetyFromTypeArgument 定义级特权标记，无字符串比较）。
- **`Semantic/Symbols/CanonicalSymbolPrinter.cs`**：BIL §5.2 五形态
  （类型 `ns::Outer.Inner` / 方法 `$[.static.]名(参:型,...)@返回` /
  字段 `#[.static.]名@型` / 运算符 `$$名` / getter-setter `$.get.名`
  `$.set.名`）+ BIL 类型引用投影（固定别名 `.i32`/`.f32`/`.any` 等 >
  标准构造 `.nullable<T>`/`.typeid<T>`/`.array<T>` > canonical/闭合
  泛型 `core::Box<.i32>`；null 返回类型 → `.void`；泛型参数实参 →
  `.generic<$.generic.T>` §7.5）。投影提示经 TypeSymbol.BilAlias /
  BilStandardConstructor 登记（core.latte 的 Array/Map/Pair 未来
  经同机制接入）。
- **测试**：`Tests/SymbolGraphTests.cs`（45 用例：驻留同一引用、
  bootstrap 层级、分支/rich 标记、shared-safe 推导、泛型约束、
  intrinsic 键空间、Freeze）+ `Tests/CanonicalSymbolPrinterTests.cs`
  （22 用例：§5.2/§8.1/§20 示例逐条对照 + 嵌套/构造/全局/泛型实参
  形态），注册为 TestRunner 第 32/33 号套件。
- 全量：977/977 + fuzz 6000（33 套件）。

### 2026-07-31 · M36 S0 诊断基建 + ROADMAP 文件级细化

> 中端第一段代码（ROADMAP S0 落地）；同批完成 `SEMANTIC_ROADMAP.md`
> 的 S0–S6 文件级施工清单细化。

- **ROADMAP 细化（先于代码）**：S0/S1/S4 细化为文件级施工清单（每个
  文件含哪些类型、对应哪个测试套件），S2/S3/S5/S6 细化到任务级，
  S7+ 保持远粗。细化中钉死的对接决策：**Bil 模型的类型引用以字符串
  承载**，即 S1 `CanonicalSymbolPrinter` 的 BIL 类型引用投影形式
  （基元 → `.i32` 等固定别名、`Nullable\<T>` → `.nullable<T>`、用户
  类型 → canonical）——S1 与 S4 因此无顺序依赖。
- **`Semantic/Diagnostics.cs`**（新增顶层目录 `Semantic/`）：
  `DiagnosticSeverity { Error, Warning }`、`DiagnosticPhase { P1–P4 }`、
  `Diagnostic { Severity, Phase, Span: CharRange?, Message }`（暂不建
  错误码编号体系，ARCHITECTURE §8）、`DiagnosticBag`（全编译单元单
  实例贯穿 P1–P4、只追加；`HasErrors` 阶段推进门槛；可遍历供断言）。
  与前端 LexerException/ParserException（单发即死）严格不同：中端
  诊断累积、尽量继续，一次编译报出尽可能多的错误。
- **`Tests/TestHarness.cs`**：新增 `CheckSemanticError(label, bag,
  msgPart)`——断言存在 Error 级且消息含片段的诊断（沿用消息子串
  惯例，与 CheckParseError 一致）。
- **测试**：新增 `Tests/DiagnosticsTests.cs` 套件（15 用例：空袋、
  Warning 不触发门槛 / Error 触发门槛、Severity/Phase/Span/Message
  字段携带、多错累积顺序保持、CheckSemanticError 命中），注册为
  TestRunner 第 31 号套件。
- 全量：910/910 + fuzz 6000（31 套件）。

### 2026-07-29 · M35 中端阶段开篇：语义分析架构定稿 + shared/rich/wrapper/String 规范修订

> **阶段转折点**：M1–M34 是前端（Lexer + Parser）阶段，随 M34 技术债清扫收官；
> M35 起项目进入**中端（语义分析 + BIL 生成）阶段**。本里程碑是中端的第一个
> 里程碑，交付物**全部是文档**——架构、路线图与作为其前提的语言规范修订；
> 中端代码从下一个里程碑（ROADMAP S0 诊断基建）开始写。
> 因此测试数量与前端组件状态相对 M34 无任何变化。

**一、中端架构定稿（新增 `docs/compiler/semantic/`）**

- `SEMANTIC_ARCHITECTURE.md`：四 pass 分工确定为 **P1 声明收集 → P2 声明解析
  → P3 Binder→BoundTree → P4a Lowerer→LoweredTree → P4b BilEmitter→BilModule**。
  用户拍板的五项：① P3 与 P4 分开（不合并为单遍）；② Roslyn 风格**独立的
  Bound Tree**（不在 AST 上挂语义字段）；③ 驻留符号对象图（同一符号同一引用，
  含构造泛型类型驻留）；④ bootstrap 硬编码 + core.latte 混合供给根类型；
  ⑤ 新错误模型（`Diagnostic` + `DiagnosticBag`，多错不互断）。
  另含 Origin 调试链（Bil→Lowered→Bound→AST.Span）与 BIL 待修订清单。
- `SEMANTIC_ROADMAP.md`：S0–S14 计划序号，近细远粗（S0–S6 已细化到验收标准）。
  验收总原则：**BIL 模型/writer → lowering 最小闭环 → verifier → VM**。
  **async 深度 lowering 归 S13**——async/await 物化为标准库 Task 调用，
  Task 再调 stdlib 要求 Middleware 暴露的 Native 方法，因此 Middleware 完全
  不关心上层异步模型（备选的浅 lowering 方案会让 P4 与 BIL/VM 捆绑，
  违反关注点分离，已否决）。
- 目录重命名：`docs/compiler/frontend/` → `docs/compiler/syntax/`
  （与新增的 `semantic/` 并列，三份前端文档随迁）。

**二、语言规范修订（用户拍板，作为中端检查项的前提）**

- **String 改为非 rich 值类型基元**（原属 Object 分支）：可观察语义是
  **严格深拷贝**；实现可引入用户透明的 CoW/驻留/native 计数优化，但源码语义、
  编译器分析、用户代码一律**不得假设其存在**——类比「BIL 永远不应假设 GC
  模型和 GC 行为」。因此 `struct Label { text: String }` 不需要 `rich`。
- **wrapper 恒为 rich struct**，对象树上 `MyWrapper → Wrapper → ValueType`：
  值语义、unique ownership，正是生命周期能绑定被修饰实体/方法/值的原因。
  `rich` 由 `wrapper` 声明形式隐含，**源码显式书写是编译错误**；BIL 作为
  显式 IR 反之**必须**显式带 `rich`。
- **`obj:Wrapper` 是只读 place**：只能作成员访问的接收者（读写字段、调用方法，
  一律原地作用于宿主持有的那份）；**不可整体赋值**（wrapper 只能由 `@W(...)`
  在宿主创建时安装）、**不可整体取值**（作实参/返回值/推断源皆非法）。
  两条禁令都直接来自「与宿主同生共死」的不变量，在语法层封死而非靠约定。
  wrapper 字段自身的可写性仍由 `var`/`const` 与可见性决定。
- 新增 SYNTAX §14.9「wrapper 的 rich/shared 规则与目标矩阵」：宿主可内嵌性
  （宿主必须能内嵌 rich struct ⇒ 非 rich struct、基元类型、String、Span、
  Type 不可被任何 wrapper 修饰）、shared wrapper 可修饰全部目标但字段受
  shared 约束、非 shared wrapper 只能修饰 A–D 四类非 shared 目标、
  interface 实现者传染校验。
- 新增**「共享安全类型」**统一概念（shared class ∪ shared rich struct/wrapper
  ∪ 非 rich ValueType ∪ T 共享安全的 `Nullable\<T>`），让两条逃逸闸门共用
  同一张白名单：① 全局/静态字段类型必须共享安全（singleton 因此必须 shared）；
  ② async 五项边界（receiver / 参数 / TResult / lambda 捕获 / 泛型实参）。
- **rich/shared 单向传染**：基类 rich/shared ⇒ 子类必须同标；反向可收紧
  （shared 子类可继承 local 基类），安全性由「含继承字段的完整闭包重校验」兜底。
- **非 rich struct（含非 rich enum struct）不得标记 `open`/`abstract`** ⇒
  不可能有子类型 ⇒ 封死「声明 rich/shared 子类型绕过闭包检查」的路径。
  **不引入 `final` 关键字**。

**三、同步与冲突处理**

- `RUNTIME.md`：三个运行时域的归属重划（String 入非 rich ValueType 域、
  全部 wrapper 入 rich ValueType 域、shared wrapper 入 shared rich 域）+
  新增「String 的表示」与「wrapper 值的表示」两段 + `CoroutineLocal\<TValue>`
  是 per-coroutine 语义的唯一机制。
- `BIL_STANDARD.md` 只做**机械同步**两处：`.string` 归 ValueType 域（§6.2）、
  §8.2 修饰符合法性 4 条 → 7 条。**指令集未动**：wrapper 从 Object 变 rich
  struct 后，§12.4 只有值语义的 `get.wrapper`，缺**只读 place 的接收者形态**
  （以宿主那份为 receiver、proxy 体内 `this`）——沿用 async 的既定做法定性为
  **待补而非待改**，记入 SEMANTIC_ARCHITECTURE §7.1 + ROADMAP S11。
- `CLAUDE.md` / `AGENTS.md` / `PARSER_ROADMAP.md` 示例与表格对齐；顺手修掉
  两处因本次修订而自相矛盾的旧示例（`rich struct { label: String }`、
  `pub open struct Point3D : Point`）。
- **前端零改动**：`Parser/Keywords.cs` 的 `DeclarationDescriptors` 已含
  RICH/SHARED/OPEN/ABSTRACT/SINGLETON，新规则全部是语义期 P2 的合法性检查
  （落 ROADMAP S3），Parser 只负责收修饰符。
- 测试：895/895 + fuzz 6000（30 套件，无增删——纯文档里程碑）。

### 2026-07-28 · M34 技术债清扫：字符字面量 / 复合赋值 / is enum case / `.name` 参数 / import 禁令

> 集中清扫 §6 技术债清单中的 5 项解析层缺口（规范依据：§3.3 字符规则定稿、
> §13.2 复合赋值既有、§12.3 is enum case 既有、§14.4 `.name` 既有、§15.2 禁令新增）。

- **字符字面量**：Lexer 新增 `CharLexerLayer`（三态状态机
  AwaitContent/EscapeSeen/ContentSeen，`BaseLexerLayer` 遇 `'` 分流，
  与 StringLexerLayer 同文件同风格）；新增 `CharToken`（TokenType.Char，
  char 无插值概念、不复用 StringToken）；AST 新增 `CharLiteralASTNode`；
  转义复用 `StringEscape` 单源；`''`/`'ab'`/未知转义/未闭合（含 EOF 冲刷帧
  虚拟换行）/反斜杠后换行均编译错误；JSONL 两端加 char ↔ 单字符字符串分支
  （往返必炸点：Deserializer 的 IsPrimitive 原走 GetInt64）；LexerFuzz 固定
  用例同步（原「'A' 未实现」错误断言反转）；RootParserLayer 顶层分派补
  `case CharToken`（ParseFirstDecl 必经之路）
- **复合赋值（§13.2，10 个全集）**：新增 `CompoundAssignmentExpressionASTNode`
  （Target/Operator/Value，Operator 不含 `=`）；`ExpressionParserLayer.
  HandleOperatorSeen` 在 pending 运算符后遇 `=` 且组合属全集时构造节点——
  左操作数仍在层内未挂载（Attach 只在 CompleteExpression 执行），一次性
  Attach 无替换、红线合规；`>>>=` 与 `>=`/`>>`/`>>>` 比较重组天然有序共存；
  非全集组合（如 `== =`）不再特判、落正常二元流程报错；复合赋值整体是
  表达式节点（普通赋值 `a = b` 维持 ExpressionStatement 双 Root 槽现状）；
  M31 的两个「尚未支持」错误用例反转为正例
- **`is` 右侧 enum case（§12.3）**：`TypeCheckExpressionASTNode` 改
  `TargetType`（可空，填充时创建——预创建会留无 span 空壳过不了 Validator）
  与 `TargetCase` 双字段互斥；`HandleTypeOperatorSeen` 仅 `is` 遇 `.` 分流
  走既有前导点逻辑（`EnumCaseExpressionASTNode` 加 parent 构造参数，
  归属即定）；`as`/`as?`/`supers`/`with` 右侧仍只收类型；switch 模式匹配
  `(_ is .Success)` 同路径通吃
- **wrapper `.name` 保留参数名（§14.4）**：`ParameterListParserLayer` 新增
  `DotNameExpected` 状态，`.` 后必须是标识符（`.123`/裸 `.` 报错）；
  `ParameterASTNode.Name` 原样存 `.name`（最小侵入，描述/JSONL 零改动）；
  解析层不限制上下文（语义阶段约束）；`operator .proxy.call(.name: String,
  args: named Any...): Any` canonical 全形解析通过
- **import `{}` 禁令（§15.2）**：`ImportParserLayer.OnAfterListItem` 遇 `.`
  报规则化错误（列表项只能是单标识符、不同子路径写多条 import）；
  顺手修正类注释与实际行为不符处（SymbolLayer 空名元素不残留）
- **测试**：895/895 + fuzz 6000（30 套件；Literal +23、Expression +28、
  ParameterList +5、TypeDeclaration +1、Import +1）

### 2026-07-28 · M33 值块统一：多语句分支体 + switch 语句形态 + lambda 裸 return 边界

> 依据 SYNTAX §5.1/§6.1/§7.1/§7.2 三项规范变更（值块统一）落地：
> 值块（seq 块、if/switch 表达式分支体、lambda 块体）形态与取值规则统一，
> 匿名默认标签为 `_`；lambda 体内裸 return 成为编译错误。

- **if/switch 表达式分支体统一为代码块**：`IfExpressionASTNode.ThenExpression/
  ElseExpression` → `ThenBody/ElseBody`（CodeBlockASTNode，创建即定）；
  `SwitchCaseASTNode.Body`、`SwitchExpressionASTNode.DefaultBody` 同样块化；
  分支体改由 `CodeBlockParserLayer` 施工。「单表达式分支隐式取值」下沉为
  「块内恰好一条 ExpressionStatement」的语义规则（解析层无特判，取值留待
  语义阶段）；多语句分支体经 `return@_`（匿名默认标签）/`return@标签` 取值
- **named 标签**：if/switch 头部 `)` 后、lambda `->` 后可写 `named 标识符`；
  `IfExpressionASTNode`/`SwitchExpressionASTNode`/`LambdaExpressionASTNode`
  新增 `Label string?`（写法参照 `SeqBlockParserLayer.HandleNamed`）
- **switch 语句形态**：新增 `SwitchStatementASTNode`（与 IfStatementASTNode
  对称，Selector/Cases/DefaultBody，分支体一律代码块）；
  `SwitchStatementParserLayer` 实现双模式（语句模式从 switch 关键字进入，
  表达式模式关键字已由 ExpressionParserLayer 消费）；
  `CodeBlockParserLayer.HandleStatementDispatch` 新增 SWITCH 路由；
  两形态统一强制 default（规范同步修订），Validator 的 default 规则
  对称覆盖新节点。副作用：语句位置的 switch 不再落成表达式语句
- **lambda 体双字段互斥**：`->` 后遇 `{` → 块形态（新 `BlockBody`
  CodeBlockASTNode），否则维持单表达式（`Body` ExpressionRoot 改可空，
  创建时定，参照 ExpressionStatementASTNode 双 Root 槽先例）；两字段互斥
- **lambda 体内裸 return 编译错误**：`CodeBlockParserLayer` 新增
  `allowBareReturn` 构造标记（默认 true），遇无 @标签 return 且标记为
  false 时抛 ParserException；标记沿施工链全链传染（lambda 体内的
  seq/if 语句块/循环体/try/switch/变量初始化/表达式深处的 if/switch
  表达式分支体——ExpressionParserLayer 及 If/Switch/Loop/TryCatch/Seq/
  VariableDeclaration/ArgumentList/TypeOf 各层逐一传递）；lambda 块体
  与单表达式体一律下传 false（lambda 是边界，内嵌 lambda 仍是 false）；
  if/switch 表达式分支体继承父上下文标记（不是 lambda 边界）
- **return@_ 迁移**：seq 匿名默认标签 `seq` → `_`（SYNTAX §6.1）；
  纯注释与测试快照迁移（标签只是字符串，解析层无特判）
- **行为变化（规范使然）**：值块内的 if/switch 一律按语句分发——
  `if (a) { if (b) {1} else {2} } else {3}` 的 then 分支是 IfStmt，
  内层 if 作分支值须写 `return@_ if (b) {1} else {2}`
- **同步面**：AstDescribe 描述器（If/Switch/Lambda/SwitchStmt 块化 + named）、
  AstJsonlSerializer/Deserializer（反射驱动，新节点/新字段零改动接入，
  往返套件补 switch 语句 + 多语句分支 + lambda 块体用例）
- **测试**：838/838 + fuzz 6000（30 套件；IfExpression/SwitchExpression/
  Lambda 套件迁移并扩充：多语句分支 return@_、named + return@标签、
  switch 语句形态（含多语句分支与边界交还）、lambda 块体、裸 return
  报错（直接/嵌套 seq/if/循环/switch/单表达式体分支/内嵌 lambda）；
  无新增测试类，用例全部加进现有套件）

### 2026-07-28 · M32 多行字符串 `"""`：规范定稿 + 全栈落地

> 技术债清理：SYNTAX §3.3 的多行字符串从「仅列示例」到规范定稿 + 实现。
> 规范语义经语言设计者确认（此前按「不猜测规范」原则挂起，见 M31 记录）。

- **规范定稿（SYNTAX §3.3）**：Swift 风格严格多行——开界 `"""` 后必须紧跟换行
  （剥除，同行写内容即编译错误）；闭界 `"""` 必须独占一行（其前仅空白，
  闭界前换行不属于内容）；闭界行缩进量 = 剥除基准，内容行前导空白不足即
  编译错误，全空白内容行输出空行；转义与单行同一套（剥除先于转义），
  行内 `"`/`""` 免转义，内容中的 `"""` 须写 `\"""`（闭界出现在内容行中间
  即编译错误）；内容换行恒为 `\n`（行尾归一在词法入口完成）；
  `${}` 插值与单行一致
- **Lexer 引号分流**：新增 `QuoteLexerLayer`——`"` 家族统一入口
  （与 `SlashLexerLayer` 同模式：按第二/三字符分流单行串 / 空串 `""` /
  多行 `"""`，字符串层以持有实例转发、同步弹出）；
  `BaseLexerLayer` 不再直推 `StringLexerLayer`
- **`MultilineStringLexerLayer` 两阶段施工**：原文按行缓冲（反斜杠只用于让
  `\"` 不参与引号计数，不展开转义），闭合时先剥缩进、再统一处理转义；
  反斜杠后紧跟真实换行立即报错（不支持行接续）；未闭合报
  "Unterminated multi-line string literal"（冲刷帧栈检查，单行串行为不变）
- **转义表单源化**：`StringEscape.TryProcess` 静态表供单行/多行共用，
  `StringLexerLayer` 内联 switch 收编
- **Parser/AST 近零改动**：多行字符串产出复用 `StringToken`，
  `StringLiteralASTNode`/JSONL 序列化路径完全不动
- **插值标记词法期判定（顺带修复）**：`StringToken.HasInterpolation` 由字符串层
  在转义处理时判定（未转义的 `$` 后紧跟 `{` 才算；`\$` 转义的字面 `$` 不构成
  插值引导，多行内 `${` 须同行相邻），`LiteralParserLayer` 不再用
  `Content.Contains("${")` 猜测——修复 `\${` 误报插值（单行串既有 bug，
  转义信息在 Content 拼装后已丢失，Parser 侧无法回补）
- **测试**：790/790 + fuzz 6000（30 套件；新增 MultilineString 套件 43 例：
  内容拼装 / 错误路径 / 引号分流回归 / token span / 插值标记 / AST 六组）

### 2026-07-28 · M31 前端大修：全量 review 驱动的 40+ 项修复

> 不改 Latte 语法；对 Lexer/Parser/AST/Core/Tests 五层做了一次全量 review
> （5 路并行 + 端到端复现验证），修复全部确认 bug 与改进建议，
> 并落地三项硬性要求：Span 左闭右开统一、JSONL 字段名键控 + 完整反序列化器、
> 测试基建统一。计划与执行的八个阶段：A 测试基建 → B Span → C Lexer →
> D 表达式/字面量 → E 声明层/关键字 → F Core/AST 基建 → G 文档 → H 回归。

- **测试基建统一（阶段 A，安全网先行）**：
  - 新建 `Tests/AstDescribe.cs`（统一 AST 描述器，替代 13+ 份分叉方言：
    `Access(obj, .m)`/`MemberAccess(obj.m)`、`var x = ...`/`Var(x = ...)` 等收敛为
    单一信息无损格式）与 `Tests/TestHarness.cs`（统一 Parse 驱动 +
    Check/CheckTrue/CheckParseError 断言与计数）
  - 21 个套件全部迁移到统一基建；断言对象约定：除查的就是命令行/日志/
    token 流/层协议行为的套件（Logger/CommandLineParser/LexerFuzz/TokenDisposition）
    外，一律断言 AST 树产物（描述串 + 结构断言）
  - 弱断言修复：`LiteralParserTests`（原只查节点类型名）与
    `VariableDeclarationTests`（原第一条件恒假）改为全串精确比对 + 结构断言；
    `TypeReferenceParserTests` 占位测试重写为 17 个真实用例；
    新增 `PathParserLayerTests`（16 例）、`ArgumentListParserLayerTests`（12 例），
    注册为套件 28/29；结构断言（Root 填充/Parent 链/无共享）普及到表达式类套件
  - `test --all` 退出码 clamp 到 255（防 Unix 8 位退出码回绕假绿）
- **Span 左闭右开统一（阶段 B）**：所有 `CharRange` 改为 `[Start, End)`——
  Start 指向首个字符，End 指向最后一个字符的下一位置
  （`Lexer.ContextImpl.PushToken` 经 `Advance` 计算；Parser 层 span 自然继承
  token 开区间；EOF 保持零宽）。相邻 token 首尾相接；Validator/序列化器/
  文档同步声明。位置断言全部更新（LexerFuzz.TestPositions）
- **Lexer 修复（阶段 C）**：
  - 块注释层重写：删除无规范依据的反斜杠转义机制（修复 `/* a*b */` 吞 `*`、
    `/* a\b */` 吞 `\`）；换行不吞——注释按行分段、换行以 LineBreakToken 入流
    （与行注释一致，两条语句间唯一的分隔换行在块注释内时语句分隔不丢失）
  - 字符字面量 `'` 由 Base 层明确报错（此前被静默当字符串收下）；
    多行字符串 `"""` 仍未实现（已知限制，规范语义未定义不猜测）
  - 行尾归一手写化：只把 `\r\n`/`\r` 归一为 `\n`（ReplaceLineEndings 此前会
    误伤字符串内的 `\f`/`\x85`/`\u2028`/`\u2029`）
  - 复合赋值策略统一：Lexer 不再合并 `*=`/`/=`（与 `>=` 同策略拆 token，
    将来 Parser 重组）；删除 `++`/`--`（规范不存在）与 `#` 命名误导常量
  - 未闭合字符串/块注释错误信息友好化（不再暴露内部层类名）
- **表达式与字面量修复（阶段 D）**：
  - 续行规则落地（SYNTAX §1.1）：`()`/`[]` 未闭合时换行按空白处理——
    调用/索引/形参列表全状态跳过换行；ExpressionParserLayer 新增
    `insideParens` 括号语境（分组/实参/条件/迭代表达式，右操作数与一元操作数继承）
  - 位运算符 `<<`/`&`/`|`/`^` 接入（`>>`/`>>>` 维持重组）；删除 `in` 二元运算符
    （只属于 for 循环头）；复合赋值遇 `=` 报「尚未支持，请展开为 a = a op b」
  - 数字字面量补全（SYNTAX §3.3）：`0b`/`0o` 前缀、下划线分隔
    （不连续/不开头结尾）；判定收敛为 `Parser/NumericLiteral.cs` 共享 helper
    （原三份规则不一致）；`IntLiteralASTNode.IsHex` 布尔改为
    `LiteralIntBase` 枚举（Decimal/Hex/Binary/Octal）
  - `3.`/`3.foo` 吞点修复：点后非数字明确报错（不再吞 `.` 伪装成员访问）；
    `a[]` 空索引拒绝（`foo()` 空参保持合法）；`throw` 后换行报错（与
    return/yield 行为一致）；`in` 从二元运算符移除后 `a in b` 在表达式位置报错
  - PathParserLayer 吞尾修复：`foo.` + 换行/EOF、`List\<i32` + EOF 报错
    （此前静默吞并）；`foo..bar` 双点报错（类型/参数语境经
    `allowVariadicDots` 保留 `...` 交还）；import 尾点报错自然获得
  - Span 一致性：表达式形态 if/switch/typeOf/lambda 的 span 含起始关键字；
    Loop/If 终态不消费换行（span 不拖尾换行符，无 else 的 if 经
    ElseCheckEntryEnd 封 End）；变量声明初始化后换行改 Replay（同语法
    不再两种 span）；EOF 规则 6 字面合规（结构完整态 Pop(Replay)）
- **声明层与关键字修复（阶段 E）**：
  - Keywords 大扫除（对齐 SYNTAX §19）：删除
    elif/foreach/when/case/private/public/final/extension/base 幽灵词
    （`private class Foo {}` 等不再被静默接受）；保留字数组补齐
    switch/return/break/continue/throw/yield/do/to/in/supers/with/typeOf/await/
    self/seq/using/import/namespace；import/namespace 移出声明路由数组
    （修复路由遮蔽）
  - 修饰符组合校验：rich 仅 struct/enum struct；shared 仅 class 或 rich struct；
    open 仅 class/struct（enum struct 明确禁止）；open+abstract 互斥；
    重复修饰符报错；`enum` 后必须 `struct`
  - 标识符合法性统一：`Keywords.IsIdentifierStart/IsIdentifier/IsReservedKeyword`
    共享实现，铺到变量名/参数名/类型名/callable 名/循环变量/catch 参数/
    using 绑定/enum case 名/成员名/符号路径元素/import 列表项
    （`class 123 {}`、`func f(123: i32)`、`for (123 in c)`、`var return = 5`、
    `foo(return = 5)` 全部报错；标签位置只查首字符——return@seq 合法）
  - 注解名空校验（`@(1)`/`@`+换行报错）；`named` 必须带 `...`；
    enum 判别值支持 0x 等进制与 long 范围（`DiscriminantValue` int?→long?）
- **Core/AST 基建（阶段 F）**：
  - Logger 控制台输出改走 stderr（诊断不污染 stdout——`compile --parse-only`
    的 JSONL 输出流纯净）
  - JSONL 序列化修复：int?/long? 等 Nullable 字段不再静默丢弃；
    严格先按声明类型过滤再取值（未填充属性不再被提前求值）；循环保护；
    字段枚举沿基类链（基类 private 字段反射盲区修复）
  - **JSONL v2 + 完整反序列化器**：carrier（ImportItem）记录化
    （独立产行、标量字段入 fields——修复 importAll 往返必丢）；
    新建 `AST/AstJsonlDeserializer.cs`：三阶段重建（类型定位/实例挂接
    （预创建子容器复用、carrier struct 回写）/fields 与 span 回填），
    字段名键控不依赖顺序，产物强制过 ASTIntegrityValidator；
    往返测试（Parse→Serialize→Deserialize→Serialize 逐行一致）12 例
  - Validator 补强：`[ChildAstNode(Required = true)]` 必需子节点校验
    （LiteralExpressionASTNode.literal、CatchClauseASTNode.ExceptionType）；
    类型审计沿基类链；[AstCarrier] 类型递归审计（carrier 装 ASTNode 的
    属性会逃出遍历，拒绝）
  - CompileCommand：CompilerInternalException 单独报告（与用户语法错误区分）；
    多文件 dump/parse-only 输出 `{"file":...}` 元记录分隔；全失败不再误报 dumped
  - import 多导入形态 SymbolElement 按引用共享改深拷贝（防语义阶段交叉污染）
- **记录在案的技术债务（本轮不改代码）**：实参位置 `a.b` 的
  MemberAccess/Symbol 双形态；`is` 右侧 enum case（§12.3）；method wrapper
  `.name` 保留参数（§14.4）；switch 语句形态（规范未定义）；多行字符串
  `"""`（规范语义未定义）；复合赋值语义实现（token 策略已统一）；
  三态合并与 ExpectNotation 提取经评估不更简洁，放弃
- **测试**：746/746 + fuzz 6000（29 套件；新增 Path/ArgumentList 两套件，
  AstJsonlSerializer 扩至 84 例含往返与格式 v2）

### 2026-07-27 · M30 Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档幽灵清理

> 不改 Latte 语法；把 `Core/Utilities.cs` 按语义拆分为 7 个文件，
> ASTVisitor 遍历逻辑开放重载，并清理文档中已删除代码的引用与过时表述。

- **Utilities.cs 拆分**（同 `LatteCompiler` 命名空间纯搬移，零调用点改动，
  原文件删除）：
  - `Lexer/Tokens.cs`：Token 基类 + TokenType + 6 个 token 类
    （顺带修正 EndOfFileToken 上 M25 前的过时注释——EOF 现由 Lexer 追加）
  - `Lexer/Notations.cs`：符号常量；`Parser/Keywords.cs`：关键字常量
    （使用方 100% 在 Parser）
  - `Core/Exceptions.cs`：LexerException / ParserException
  - `AST/ASTNode.cs`：ASTNode 基类 + RootASTNode；
    `AST/SymbolNodes.cs`：Symbol 家族 + SymbolASTNode；
    `AST/ImportNodes.cs`：ImportASTNode + [AstCarrier] ImportItem
  - 原文件的幽灵 using（`System.Linq`/`System.Threading.Tasks`）随之清除
- **ASTVisitor 遍历可重载**：`VisitNode`（遍历骨架）与 `EnumerateChildren`
  （子节点来源）改为 protected virtual，默认仍走 [ChildAstNode] 反射；
  Validator/Serializer/测试零改动
- **文档幽灵清理**：
  - `docs/compiler/frontend/FRONTEND_TYPES.md` 全量修订：删除幽灵引用——
    `Core/FrontendTypesExtension.cs` 全系（SymbolTable/SymbolInfo/TypeInfo/
    SemanticException 等，代码已不存在）、AcquisitionExpressionASTNode、
    CharLiteralASTNode；修正过时表述（enum case 列表与 wrapper proxy 的
    「待实现」标注、阶段标记），文件位置更新到 M30 拆分后布局，
    Token 表补 EndOfFileToken 行
  - `docs/compiler/frontend/FRONTEND_ARCHITECTURE.md` 删除：全文围绕已删除的
    FrontendTypesExtension.cs，残余内容与 AGENTS.md §3、FRONTEND_TYPES 重复
  - `PARSER_ROADMAP.md` 头部加注：正文为大扫除前原始计划记录，代码草图勿照搬
  - `AGENTS.md`/`CLAUDE.md` 同步新文件布局，移除「Utilities.cs 残留」条目
- **测试**：27 套件全绿（用例无增删）+ fuzz 6000；build 0 错误 0 警告

### 2026-07-27 · M29 AST 容器重构：语义字段 + wrapper 挂载接口

> 不改 Latte 语法；纯 AST 结构重构。基类共有字段 `Children`/`Annotations`
> 删除，子节点容器改为各节点上语义明确的 [ChildAstNode] 字段；注解挂载
> 能力按 SYNTAX §14 三类 wrapper 目标抽象为接口。

- **基类瘦身**（`Core/Utilities.cs`）：`ASTNode` 只保留 `Parent`
  （[ParentAstNode]）与 `Span`；`Children`、`Annotations` 两个共有字段删除
- **语义容器字段**（均标 [ChildAstNode]；Validator/ASTVisitor/AstJsonlSerializer
  走 Attribute 反射，零改动）：
  - `RootASTNode.Declarations`：顶层条目（全局声明/import/namespace/顶层字面量）
  - `CodeBlockASTNode.Statements`：块内语句（局部声明同挂）
  - 5 个类型声明节点（Class/Interface/Struct/EnumStruct/Wrapper）各自的 `Members`
- **wrapper 挂载接口**（`AST/DeclarationNodes.cs`）：`IWrapperAttachable` 基接口
  （`Annotations` 属性）+ 三个分类标记接口——`IEntityWrapperAttachable`
  （5 个类型节点，§14.2）、`IMethodWrapperAttachable`（Callable，§14.4）、
  `IValueWrapperAttachable`（Variable，§14.3）；挂载合法性校验留待语义阶段
- **DeclarationParserLayer**：构造函数改收 `(ASTNode parent, List<ASTNode> target)`
  （parent 供节点 Parent 指针、target 供挂接；与 ArgumentListParserLayer 收
  目标列表的先例一致）；新增 `GetMembers` 集中 switch（仿 `GetModifiers`）；
  `AttachAnnotations` 改经 `IWrapperAttachable` 访问
- **测试**：19 个测试文件机械改名（`root.Declarations`/`block.Statements`/
  类型节点 `.Members`），无用例增删、无期望变化；AstJsonlSerializerTests 的
  via 期望串同步（`Children[0]` → `Declarations[0]`）；全部 27 套件 0 失败
  + fuzz 6000/6000

### 2026-07-27 · M28 Lexer 位置修复 + AST Source Span + ASTVisitor 统一遍历

> 不改 Latte 语法；修复 Lexer 位置计量的五个 bug，给每个 AST 节点挂上
> 源码范围 Span（JSONL 同步输出），并把 Validator 与 Serializer 的遍历
> 统一为 ASTVisitor 基建，Validator 新增 span 校验与类型审计。

- **Lexer 位置修复**（`Lexer/Lexer.cs`）：
  - `CharPosition.offset` 恒为 0 —— 主循环从未把字符索引写进位置（已修，
    0 起始字符索引）
  - 普通 token 的 `CharRange.sourceName` 从未设置（仅 EOF 有）；sourceName
    单源化到 `CharRange.sourceName`，`CharPosition.sourceName` 删除
  - 换行即切下一行 col 1 —— 第二行起所有列号 +1（已修：换行算当前行
    最后一列，下一行首字符 col 1）
  - 空白字符被记为 token 头 —— 缩进行 token 的 Start 落在前导空格上
    （已修：token 头跳过空白，换行除外——它是 LineBreakToken）
  - EOF 冲刷帧不占位置 —— EOF 处 Word/Slash 层 token 的 End 少算一个
    字符、换行后甚至范围倒置（已修：虚拟换行占末尾虚拟位置）
- **AST Source Span**：`ASTNode.Span`（`CharRange?`）。填充分两级——
  层目标由 Parser 主循环按层栈跟踪 token 流计算，层弹出时经新接口
  `ISpanReceiver.ReceiveSpan` 回填（约定 `target.Span ??= span` 只填空；
  换行处弹出不拖尾换行符）；层内自建节点由所在层显式设置（创建记
  Start、完成经 `ParserLayerContext.GetPreviousLocation()` 封 End；
  ExpressionParserLayer 以后缀包装/运算符/完成三处封口）。
  `ExpressionRootASTNode` 覆写 Span getter 透明继承内容表达式
  - **JSONL**：`AstJsonlSerializer` 每节点一行新增 `span` 键
    （`{source,startLine,startCol,startOffset,endLine,endCol,endOffset}`）
  - **Validator 新检查**：每节点 span 非空、sourceName 非空、End 不早于
    Start；类型审计——装 ASTNode 的成员（字段/自动属性，经 backing 字段
    识别）必须带 [ChildAstNode]/[ParentAstNode]，[ChildAstNode] 标在非
    AST 成员上同样拒绝
- **ASTVisitor 统一遍历**（`AST/ASTVisitor.cs`）：[ChildAstNode] 子节点
  枚举的唯一实现（含 carrier 下钻、集合下标 via），ASTIntegrityValidator
  与 AstJsonlSerializer 各删一份重复反射；via 格式统一为
  `member[i]`/`member[i](Carrier.Field)`
- **测试**：LexerFuzzTests 新增位置精确性用例 3 例 + 不变量扩展
  （sourceName 非空、offset 不回退、范围不颠倒）；ASTIntegrityValidatorTests
  新增 span 破坏/类型审计用例 5 例；AstJsonlSerializerTests 新增 span 键
  结构断言 4 例；全量 556/556 + fuzz 6000 通过

### 2026-07-27 · M27 CLI 插件化重构（help/compile/test）+ 交互菜单删除

> 不改 Latte 语法；把 M26 的平铺 `--选项` 参数与用户交互菜单统一重构为
> `<COMMAND> [--sub-cmd [args...]...]` 结构，选项全部插件化、帮助程序生成。

- **用法定稿**：`dotnet run -- <COMMAND> [--sub-cmd [args...]...]`，
  COMMAND 三个——`compile`（`--file <路径...>`、`--parse-only`、
  `--dump-ast <路径>`、`--verbose`、`--log-to <路径>`）、`test`（`--all`、
  `--run [编号...]`、`--verbose`、`--log-to`）、`help`（无参概览；
  `help compile` 单 COMMAND；`help compile.file` 单个子命令，名不带 `--`）
- **CommandLineMask**（Core/CommandLine.cs）：选项自描述元数据——Name/
  Description/ArgsHint/MinArgs/MaxArgs（int.MaxValue 表任意个数）/
  MutuallyExclusive；解析器（`--x=v` 与空格两形态、个数/互斥/重复/游离
  参数校验）与 help 文本完全由注册表数据驱动，无手写帮助页
- **插件承载行为**：COMMAND 插件带 `Execute`（解析结果 → 退出码）；
  `--verbose`/`--log-to` 是 compile 与 test 共享的子命令插件类；
  `--all` 与 `--run` 互斥；`test` 裸用或 `--run` 无编号打印套件菜单
- **交互菜单删除**：Program.cs 从 304 行瘦身为 19 行薄入口（解析→分发→
  退出码）；`--test-all` 更名 `test --all`，`--enable-verbose` 更名
  `--verbose`；`--parse-only` 时 AST JSONL 默认输出到 stdout，
  `--dump-ast` 指定文件；编译错误走 stderr，单文件失败不阻断后续文件，
  退出码非零
- **CI 同步**：`.github/workflows/ci.yml` 改为 `dotnet run -- test --all`
- 测试：544/544（27 套件）+ fuzz 6000/6000；新增 CommandLineParserTests
  （46 用例：注册表完整性、COMMAND/子命令匹配、两形态、参数个数、互斥、
  游离参数、重复子命令）

### 2026-07-26 · M26 日志系统（Logger 分级 + JSONL 落盘）+ AST JSONL 序列化

> 不改 Latte 语法；基础设施大扫除：日志分级与分流（控制台/文件），
> 以及 AST 树的 JSONL 序列化诊断能力。

- **Core/Logger.cs**（唯一日志出口）：`Verbose/Warning/Error` 三级；
  控制台门槛默认 Warning+（verbose 默认关闭），`--enable-verbose` 放开；
  `--log-to PATH` 把**全量**日志（含 Verbose）以 JSONL 落盘
  （`{"ts","level","source","message"}` 每行一条，System.Text.Json 序列化），
  文件不过滤级别——诊断时从一大坨日志里 grep 所需
- **接入点收口**：Lexer/Parser 的 `ContextImpl.Log/LogWarning` 改经 Logger
  输出（此前直接 `Console.WriteLine`）；删除 LexerFuzzTests 的
  `Console.SetOut(TextWriter.Null)` 屏蔽 hack（verbose 默认关闭后无意义）
- **AST/AstJsonlSerializer.cs**：AST 树深度优先序列化为 JSONL，每节点一行
  `{id,parent,via,type,fields}`；遍历复用 Validator 的 [ChildAstNode] 反射
  下钻（含 private 字段、IEnumerable 下标、[AstCarrier] 展开）；fields 收集
  标量成员（Symbol 渲染为点分串），排除 Parent/索引器，先按声明类型过滤
  再取值以避开未填充 ExpressionRoot 的抛异常属性
- **Program.cs 参数解析**：从只认 `args[0]=="--test-all"` 扩为循环解析
  （`--test-all` 单独使用行为不变，CI 不受影响）；新增 `--enable-verbose`、
  `--log-to`、`--dump-ast`（支持 `--name=value` 与 `--name value` 两形态；
  未知参数/缺路径 stderr 提示 + 退出码 2）；`--dump-ast` 在交互菜单
  解析文件成功后写出 AST JSONL
- 测试：498/498（26 套件）+ fuzz 6000/6000；新增 LoggerTests（7 用例：
  JSONL 落盘与级别门控）与 AstJsonlSerializerTests（38 用例：JSON 合法性、
  id/parent 链一致性、via/fields 内容断言）

### 2026-07-26 · M25 Lexer 修复：SlashLexerLayer、Lexer EOF、注释集中跳过 + fuzz 基建

> 不改 Latte 语法；修复 Lexer 三个结构性缺陷（除法不可用、EOF 由 Parser
> 伪造、注释处理散落），并建立 Lexer fuzz 测试基建。

- **SlashLexerLayer**：`/`、`/=`、`//`、`/*` 统一分流入口——M25 前 Base 层
  见到 `/` 后若下一字符非注释开头直接报错，**除法与 `/=` 完全不可用**；
  注释形态转发给持有的注释层实例（Delegate, don't implement），
  注释层弹出时本层同步弹出
- **Lexer 输出 EOF**：`EndOfFileToken` 改由 `Lexer.Tokenize` 在输出末尾追加
  （零长度 CharRange，位于文件末尾），Parser 不再自行伪造（仅对绕过 Lexer
  的调用方保持追加兼容）；输入结束以虚拟换行冲刷帧（FlushLayers）驱动各层
  弹栈、不产生任何 token；冲刷后栈不收敛（未闭合字符串/块注释）即
  LexerException；顺带修复空输入 `content[0]` 越界与 `Tokenize(string)` 的
  AggregateException 包装（改 `GetAwaiter().GetResult()` 原样抛出）
- **注释集中跳过**：CommentToken 由 Parser 主循环分发时统一跳过，各
  ParserLayer 不再自行处理（RootParserLayer 的 Comment 分支已删）；
  行注释不再吞掉结尾换行（keepChar 回流，由 Base 层产出 LineBreakToken——
  此前行尾注释会让下一条语句粘行）
- **LexerFuzzTests**（固定用例 23 + fuzz 6000，固定种子可复现）：
  除法/注释/EOF 精确 token 序列断言；纯随机/结构化片段/合法源码变异三类
  fuzz 校验不变量（不崩——只允许 LexerException、EOF 存在且唯一、
  位置单调不回退）；Parser 集成验证注释任意位置不炸语法
- 测试：453/453（24 套件）+ fuzz 6000/6000

### 2026-07-26 · M24 AST 结构标注 + Validator 重写 + 删除 ASTNodeType + 父子指针 bug 修复

> 本次重构不改 Latte 语法；把 AST 结构关系从「约定」变为「显式标注」，
> Validator 改为 Attribute 驱动，并借此抓出并修复 5 个真实的父子指针 bug
> （根因均为「先解析后决定归属 → 事后搬家」，与大扫除的施工协议相违）。

- **结构标注 Attribute**（AST/ASTStructureAttributes.cs）：`[ChildAstNode]`
  标记装子节点的字段/属性（单节点/节点集合/carrier 集合，含 private 字段如
  ExpressionRootASTNode.expression）；`[ParentAstNode]` 标记父指针
  （ASTNode.Parent）；`[AstCarrier]` 标记携带 ASTNode 的非节点对象
  （ImportItem struct）——配合容纳它的成员上的 [ChildAstNode]，
  Validator 深入其公共字段完成子节点遍历
- **Validator 重写**：遍历只走 [ChildAstNode] 成员；新增父子指针一致性校验
  （每个子节点的 Parent 必须指向持有者，carrier 情形为持有集合的节点）；
  原「Expression.Parent 指向 Root」检查被通用校验覆盖；Root 未填充 /
  节点无共享 / Parent 链无环 / switch default 规则保留
- **彻底删除 ASTNodeType**：枚举本体、ASTNode.NodeType 抽象属性、约 50 处
  override、ASTNodeTypeExtensions（无任何使用）全部删除；节点类型一律用
  CLR 类型判断（is / GetType()）
- **无 reparent 原则**：AttachTo 保持一次性；「归属后知」场景一律改用
  创建时归属即定的结构，禁止任何形式的 Parent 重挂
- **修复 Validator 抓出的 5 个父子指针 bug**：
  1. Range 收养（LoopParserLayer）→ 删除 RangeExpressionASTNode，拍平为
     LoopStatementASTNode.Iterable（起点）+ RangeTo（终点，null = 非范围循环）
  2. Assign 收养（CodeBlockParserLayer）→ 删除 AssignStatementASTNode，
     表达式语句与赋值语句统一为 ExpressionStatementASTNode
     （Expression + AssignValue?，两个 Root 槽创建时 Parent 即定）
  3. 注解 Parent 指向声明的父容器 → 构造时 parent 为 null，声明节点创建时
     一次性 AttachTo（DeclarationParserLayer.AttachAnnotations 统一挂接点）
  4. 泛型约束 Target 搬家（GenericParametersParserLayer）→ StartConstraint
     把符号数据（Symbol 为纯数据）灌进 constraint 自带 Target 节点，
     不再挂接外部已建成节点
  5. 注解实参 Parent 指向父容器 → ArgumentListParserLayer 的 parentNode
     改传注解节点本身
- 测试：430/430（23 套件）；新增 ASTIntegrityValidatorTests（5 用例：
  合法树通过 + Parent 指错/carrier 指错/Root 未填充/节点共享拒绝）；
  各套件 Describe 类型分派更新，快照期望保持不变

### 2026-07-26 · M23 Parser/PDA 大扫除（架构重构，依据 great_clean_plan.md）

> 本次重构只调整 Parser 内部架构、AST 构造协议、Token 流转协议与测试基础设施，
> **不修改 Latte 的任何既有语法与语义**（417 个语法用例逐一保持原断言并通过）。

- **TokenDisposition**：`ParserLayerResult.PushLayer/PopLayer` 的 `bool shouldKeepToken`
  全部机械替换为具名枚举 `TokenDisposition.Consume/Replay`（约 260 处调用点）
- **彻底删除 Layer 返回值**：`IResultProducer`/`IResultConsumer`/`GetResult()`/
  `OnChildResult()`/`pendingResultHandler` 全部删除（grep 验收 0 处）；Parser 主循环
  不再包含任何 AST 结果传递逻辑，Layer 之间只传递控制权
- **施工目标协议**：每个 Layer 的构造函数接收明确、强类型的施工目标；
  父层创建/选择目标并传入子层构造函数，子层原地填充或向目标附加子节点；
  数据流严格单向（父→子），禁止任何形式的回传替代机制
- **ExpressionRootASTNode**：Syntax AST 中所有表达式位置的统一稳定挂载点
  （§6.4 清单 30+ 个字段全部迁移）；一次性 `Attach`、禁止替换、禁止附加已有父节点的
  表达式；可选表达式以 null Root 表示；`ASTNode.Parent` 改为只读（只能设置一次）；
  表达式经「未挂载子树包装」组合，ExpressionParser 只在表达式完成时 Attach 最终外层节点；
  赋值目标与范围起点经「Root 收养」转移逻辑归属（不搬家）
- **GroupExpression 独立 NodeType**：`ASTNodeType.GroupExpression`，
  `ValueExpressionRoot` 专属于 ExpressionRootASTNode
- **LiteralASTNode 基类**：6 个字面量节点统一继承；
  `LiteralExpressionASTNode.AttachLiteral` 一次性附加
- **EOF 正式化**：`EndOfFileToken`（TokenType.EndOfFile）由 Parser 在输入**本地副本**
  末尾追加（不再修改调用者列表）；只由 RootParserLayer 消费；非 Root 层遇 EOF：
  结构完整 → Pop(Replay) 层层上交，不完整 → "Unexpected end of file"；
  换行哨兵与 guard 收尾循环（`while stack.Count > 1 && guard++ < 64`）删除，
  解析结束强制 `stack.Count == 1`
- **ParserLayerContext 收缩**：删除 `GetRootNode()` 与 `ContextImpl.current`；
  Parser 直接持有 RootASTNode，Layer 无法经 Context 触碰全局根
- **AST 完整性验证**：新增 `ASTIntegrityValidator`（AST/ASTIntegrityValidator.cs），
  Parse 成功后自动运行：Root 均已填充、Expression.Parent 指向 Root、节点无共享、
  Parent 链无环、switch default 规则；失败抛 `CompilerInternalException`（内部错误，
  与用户语法错误区分）
- **测试基础设施**：
  - `dotnet run -- --test-all` 单命令全量（`TestRunner` 注册全部套件，
    任意失败非零退出码，输出失败套件名）；新增最小 CI（`.github/workflows/ci.yml`：
    `dotnet build` + `--test-all`）
  - `TestRootParserLayer`：独立 Layer 测试改为 `Parse(tokens, TestRoot, entryLayer)`
    驱动——被测 Layer 提前结束或漏消费普通 token 立即失败（14 处调用全部迁移）
  - `TokenDispositionTests`：假 Layer 验证 Push/Pop × Consume/Replay 四种组合的
    token 接收序列（4 用例，菜单 23）
  - 表达式套件新增 AST 结构断言（§13.4：Root 存在/已填充/Expression 类型/
    Parent 链/子 Root 填充/无共享，4 用例，字符串快照不再是唯一验证方式）
- **消除全部编译警告**（原 `Core/Utilities.cs` 的 nullable 警告随 `null!` 模式消失）
- 测试总数 417 → 425（+4 结构断言、+4 TokenDisposition），22 个套件全绿

### 2026-07-26 · M22 namespace 声明（§15.1）—— P5 收官
- 新增 `NamespaceParserLayer`（小 Layer，与 ImportParserLayer 同款结构）：
  `namespace` + 路径（复用 `PathParserLayer`）+ 换行收尾；空路径与路径后多余 token 报错；
  唯一性与位置约束（应在文件首部）留待语义阶段
- `RootParserLayer` 新增 `namespace` 分发（与 import 同款）；新增
  `NamespaceDeclarationASTNode` 与 `Keywords.NAMESPACE`
- 新增 NamespaceTests 7 用例（基本/与 import 组合/3 错误用例）并注册菜单 22；
  全量回归 2–22 无 FAIL
- 测试总数 410 → 417
- **P5 全部完成，Parser 前端规划（roadmap P0–P5）收官**；下一阶段：语义分析、BIL 输出

### 2026-07-26 · M21 模块系统 import + wrapper 路径访问（`:`）
- `ImportParserLayer` 重建（SYNTAX §15.2，roadmap P5）——三种规范形态：
  单个导入 / `.{A, B}` 多个导入（共享前缀，展开为独立完整路径 `ImportItem`）/ `.*` 全部导入；
  前缀路径复用 `PathParserLayer`（遇 `*`/`{`/换行弹出并交还，末尾空名元素统一清理——
  与 GenericParameters 层对 `...` 的既有处理同款）；旧骨架的 `as` alias 与逗号分隔
  多导入不在现行规范内，按规范移除
- wrapper 路径访问（SYNTAX §14.1/§3）：`:` 接入 `ExpressionParserLayer` 后缀链
  （与 `.`/`?.` 同框架——base 为任意路径表达式，如规范示例 `foo().bar[0]?.length:MyWrapper`；
  链式 `obj:A:B` 左结合逐层嵌套）；新增 `WrapperAccessASTNode`
  - 架构判断：`PathParserLayer` 遗留的 `ValuePath`/`AcquisitionExpressionASTNode` 模式
    仅支持纯符号 base，与后缀链架构不兼容，未接入（记入技术债务待清理）
- 新增 ImportTests 14 用例并注册菜单 21；Expression 71 → 75
  （+4：wrapper 访问基本/链式/规范 §3 完整路径/后续成员后缀）
- 测试总数 392 → 410

### 2026-07-26 · M20 P5 起步：wrapper 主体（@ 注解 + `.proxy.*` 代理成员 + 前导点 enum case）
- `@` 注解 / wrapper 应用（SYNTAX §14.5）：`@Name` / `@Name(args)`，可叠加，挂所有声明节点
  - `ASTNode` 基类新增 `Annotations` 列表（仿 M14 `Children` 上移先例）；
    新增 `AnnotationASTNode`（符号路径名 / HasArguments / Arguments）
  - `DeclarationParserLayer` 新增 `AnnotationName` 状态：注解名复用 `PathParserLayer`、
    实参复用 `ArgumentListParserLayer`；声明本体创建时统一挂接暂存注解
  - `RootParserLayer` 的 `@` 预留入口就此接通；`CodeBlockParserLayer` 新增同款 `@` 分发
    （栈上注解变量，§14.3）
  - 规范判断：`@WrapperTarget(.Entity/.Value/.Method)` 即 wrapper 类型标识（§14.2–14.4），
    与普通 wrapper 应用同一语法形态；roadmap 示例中的 `wrapper X entity` 后缀写法以规范为准，不实现
- 前导点 enum case 引用（§12）：`ExpressionParserLayer` 新增 `EnumCaseNameExpected` 状态 +
  `EnumCaseExpressionASTNode`；参数化 case 调用（`.Failed(404)`）由后缀链自然脱糖为 Call，零新增代码
- `.proxy.*` 代理成员（§14.2/§14.6）：operator 名允许 `.proxy.<category?>.<name|*>` 限定名
  （仅 wrapper 体内、仅 operator）；复用 `CallableNameDot` 状态拼接，首段必须为 `proxy`、
  wildcard `*` 必须收尾；wrapper 体结束时校验同类 wildcard 唯一（与 enum case 校验同一先例）
- 顺带修复：`CodeBlockASTNode` 隐藏了基类 `Children` 字段（M14 上移时的漏网之鱼）——
  以 `ASTNode` 静态类型挂入的声明在块视角下不可见；删除隐藏字段，`Children` 回归基类唯一来源
- 测试：TypeDeclaration 77 → 94（+17：@ 注解 7、proxy 10）、Expression 67 → 71（+4：enum case）、
  CodeBlock 27 → 29（+2：栈上注解变量）；全量回归 2–20 无 FAIL
- 测试总数 369 → 392

### 2026-07-26 · M19 like 委托 + ext 扩展成员 —— P3/P4 收官
- `like` 委托（§9.6，roadmap #13 剩余项）：统一声明层新增 LikeExpected/AfterLike
  两个状态，`class A : Base implements I like field {` 与省略基类的 `class A like x {`
  均可解析；仅 class 可委托（struct/interface/wrapper 报错）；AST 为
  `ClassDeclarationASTNode.LikeTarget`（可空字符串，不加节点）
- `ext` 扩展成员（§4.4）：补 `ext` 关键字（Keywords.EXT + DeclarationDescriptors）；
  限定名 `Type.member`（可多段路径）——Callable 侧在 ParamsExpected 加 `.` 分支
  （extSeen 门控，非 ext 自然落错误分支），var/const 侧经
  `VariableDeclarationParserLayer(allowExtension)` 同构支持；与 M16 会师：
  `pub ext var String.isEmpty: bool { get(_: _) { ... } }` 完整可解析
- 测试：TypeDeclaration 68 → 77（+9：like 规范形态/省略基类/2 错误、ext 函数/字段/2 错误）；
  全量回归 2–20 无 FAIL
- 测试总数 360 → 369
- **P3（#13–16）与 P4（#17–19）全部完成**；下一阶段 P5：wrapper 主体、模块系统、
  wrapper 路径访问（`:`）

### 2026-07-26 · M18 init 参数映射（_ -> field，SYNTAX §9.3）
- `ParameterListParserLayer` 增加 `allowMapping` 开关（默认 false，仅 init 传入 true）：
  `_ -> x`（同名映射）、`_ -> x = 0`（带默认值）、`horizontal: i32 -> x`（显式名 + 类型）、
  与普通参数混合；新增 MappedFieldExpected / AfterMappedField 两个状态
- `ParameterASTNode` 增加 `MappedFieldName`（可空）；映射参数省略类型时
  Type 保持空引用节点（沿用字段类型，语义阶段回填）
- 非 init 形参列表出现 `->` 自然落到既有错误分支（func/lambda/operator 均拒绝）
- 与 M17 会师：§12 Direction 规范示例（`priv init(_ -> degrees)` + `[case 列表]`）完整可解析
- 测试：TypeDeclaration 61 → 68（+7：§9.3 全形态、混合、struct/enum 集成、3 个错误用例）；
  全量回归 2–20 无 FAIL
- 测试总数 353 → 360
- 已知缺口：§9.3 语法行中的 `[modifier...]` 形参修饰符无任何规范示例，暂不支持（出现时按普通标识符报错）

### 2026-07-26 · M17 enum struct 的 [] case 列表（SYNTAX §12）
- 在统一 `DeclarationParserLayer` 内联扩展（不新建 Layer）：enum 体 `}` 后进入
  case 列表子状态机（EnumCaseListOpen/EnumCaseStart/EnumAfterName/EnumAfterArgs/
  EnumDiscriminant/EnumAfterCase 六个状态）
- 形态全覆盖：固定 case（`North(0)`、无参 `Red`）、参数化 case（`_` 参数洞 +
  具名实参 `Failed(errorCode = _)`）、显式判别值（`-> N`，§12.4）；
  case 实参复用 `ArgumentListParserLayer`（开括号由本层消费——与调用点既有约定一致）
- 解析期校验：case 名唯一、判别值唯一、「全显式或全分配」不得混用（§12.4）、
  判别值必须是非负整数字面量
- `[` 必须与 `}` 同行：换行即声明结束（与 callable 体 `{` 的既有约定一致），
  同时保证 EOF 哨兵下无 case 的 enum 能正常收敛
- 测试：TypeDeclaration 51 → 61（+10：固定/参数化/判别值/无 case 缺省 + 4 个错误用例）；
  全量回归 2–20 无 FAIL
- 测试总数 343 → 353

### 2026-07-26 · M16 属性访问器 getter/setter（§9.4，roadmap #23）
- 新增 `PropertyAccessorParserLayer` 解析变量声明后的 `{ get... set... }` 访问器块；
  三类定义位置（类/struct 字段、全局变量、栈上 var/const）不经任何改动即覆盖——
  它们早已统一汇聚到 `VariableDeclarationParserLayer`，只需在其 NameSeen/TypeSeen
  状态各加一个 `{` 分支（Delegate, don't implement）
- 访问器形态全覆盖：编译器生成（`pub get` 无参无体）、`(value: _) + 自定义体`
  （backing field）、`(_: _) + 自定义体`（计算属性）；访问器体复用 CodeBlockParserLayer
- 解析期校验：同块重复 get/set 报错、空块报错、`(_: _)` 与 `(value: _)` 混用报错
  （§9.4「get 和 set 在是否需要 backing field 上必须保持一致」）
- 自动访问器以换行或 `}` 收尾——与成员声明的换行分隔规则一致
- AST：新增 `PropertyAccessorASTNode`（Kind/HasBackingField/Body），
  `VariableDeclarationASTNode` 增加 Getter/Setter 两个可空字段，不加包装层
- 测试：新增 PropertyAccessorTests 17 用例（形态/跨行/三类位置/5 个错误用例），
  注册菜单选项 20；全量回归 2–20 无 FAIL
- 测试总数 326 → 343

### 2026-07-26 · M15 声明泛型参数接入统一声明层
- 类型/函数声明接入既有 `GenericParametersParserLayer`（roadmap P3 收尾第 1 项，不新建任何 Layer）：
  - Callable（func/operator/init 共用）：`ParamsExpected` 状态识别 `\`，push 泛型层后**保持原状态**，列表弹出后仍等待 `(`
  - 类型（class/interface/struct/enum struct/wrapper 共用）：`AfterTypeName` 状态识别 `\`，同样保持原状态，列表弹出后仍等待 `:` / `implements` / `{`
  - 两处接入均不新增状态机状态，与 LambdaExpressionParserLayer 的既有接入模式一致（简洁优先三问：复用已有轮子，不加状态）
  - 5 个类型节点的同名 `GenericParameters` 字段集中分派（新增 `SetGenericParameters`，与 `SetTypeName` 同一 pattern）
- 覆盖 SYNTAX §3.6 全形态：多参数、out/in 型变、extends/supers/with 约束、可变/具名可变参数、型变+约束组合；全局函数与成员方法/operator 同一条路径；泛型列表位置与继承子句的先后关系符合规范（`Name\<T> : Base implements I {}`）
- 测试：TypeDeclaration 36 → 51（+15：泛型类型/接口/struct/wrapper、泛型+继承+implements、全局与成员泛型函数、泛型 operator、约束、可变参数、型变+约束）；全量回归 2–19 无 FAIL
- 测试总数 311 → 326

### 2026-07-26 · M14 统一声明层：全局/成员/嵌套共用一套 infra
- 核心依据 SYNTAX.md §14.8：canonical symbol 的类名段可为空、`.static.` 只是标记位，因此"全局函数"与"成员方法"结构同构——三类位置合并为一条代码路径，而不是各造轮子
- `DeclarationParserLayer` 成为任何位置任何声明的唯一入口：
  - 全局字段 / 类字段 → 复用 `VariableDeclarationParserLayer`
  - 全局函数 / 方法 / static / operator / init → 同一套 Callable 状态，只改 `Kind`
  - 参数列表 → 复用 `ParameterListParserLayer`；返回类型 / 基类 / 接口 → 复用 `TypeReferenceParserLayer`；函数体 → 复用 `CodeBlockParserLayer`
  - 嵌套类型 → 类型体内递归 push 本层自身（与顶层同一路径）
- 配套简化（删冗余，不加轮子）：
  - 新增 `CallableDeclarationASTNode` 一个节点覆盖 func/operator/init（`CallableKind` 枚举）
  - 删除 `DeclarationASTNode` 包装层（其 type 枚举与 Declaration 指针只是对 C# 节点类型的重复表达）
  - 删除 5 个类型节点各自的 `CodeBlockASTNode Body` 字段，成员统一挂 `ASTNode.Children`（Children 从 RootASTNode 上移到基类）
  - RootParserLayer 的 var/const 专用分支合并进通用声明分支
- `Parser.cs` 修复 EOF 收尾：嵌套委托后父层仍需一个终止 token 才能收敛，原逻辑只喂一个哨兵便判定 `stack.Count > 1` → "Unexpected End"；改为反复喂哨兵直到栈收敛或无进展（带 guard 防死循环）
- 规范判断：interface 的 `: Base` 按 SYNTAX §11 是父接口，落 `BaseInterfaces` 而非 `Interfaces`
- 测试：TypeDeclaration 16 → 36（新增全局字段/全局函数、类成员、init/operator、继承与 implements 列表、三层嵌套类型）；全量回归 2–19 全绿（其中 5/6/9/10/11/12 由 EOF 修复恢复）
- 测试总数 275 → 311

### 2026-07-26 · 修复：TypeDeclaration 测试的 double-root 问题
- 根因：`Parser.Parse` 内部已压入 RootParserLayer，测试又把 `new RootParserLayer(root)` 当 entryLayer 传入，栈里出现两个永不 pop 的 root 层，循环结束时 `stack.Count > 1` 触发 "Unexpected End"
- 修复：测试改用 `parser.Parse(tokens)` 并取其返回值作为根节点（一行改动）
- 同提交把「简洁优先三问」原则写入 CLAUDE.md / AGENTS.md（含项目内已验证的复用范例表）
- 修完 TypeDeclaration 16/16 通过，套件 2–19 全量回归无失败

### 2026-07-26 · M13 P3 类型声明解析基础（DeclarationParserLayer 重构）
- 前置提交：5 个类型声明 AST 节点（Class/Interface/Struct/EnumStruct(+EnumCase)/Wrapper）+ 关键字补齐（pub/priv/open/abstract/singleton/shared/rich/implements/like/init/get/set 等）
- 扩展现有 `DeclarationParserLayer` 骨架（不新建 Layer）解析类型声明头部：修饰符 → 类型名 → 继承/接口 → 体
- 修饰符识别：pub, priv, open, abstract, singleton, shared, rich, static, override, async
- 更新关键字数组：DeclarationKeywords/TypeKeywords 添加 enum，DeclarationDescriptors 添加全部新修饰符
- 新增 TypeDeclarationTests 16 用例（简单声明、带修饰符、5 种类型），菜单注册选项 19
- 提交时测试未过（WIP），由随后的 double-root 修复转绿

### 2026-07-26 · M12 CoroutineOps：await/yield（roadmap #12，P2 完成）
- **await**：一元前缀运算符，在 ExpressionParserLayer 中处理（`IsPrefixUnaryOperator` 添加 `Keywords.AWAIT`）
- await 产生 UnaryExpressionASTNode，operator 为 "await"
- await 可在变量初始化、if 条件、return 等任意表达式位置使用
- **yield**：语句，在 CodeBlockParserLayer 中内联处理（类似 return/throw）
- YieldStatementASTNode：包含可选的 Alarm 表达式
- 裸 yield（不带表达式）：直接结束当前执行段
- yield alarm（带表达式）：委托 ExpressionParserLayer 解析 alarm
- 添加 Keywords.AWAIT 和 Keywords.YIELD
- 测试 262 → 275（+13：await 表达式、yield 语句、await+yield 组合、不同上下文）
- **P2 语句系统全部完成！**

### 2026-07-26 · M11 throw 语句
- 在 CodeBlockParserLayer 中内联处理 throw 语句（类似 return/break/continue）
- ThrowStatementASTNode：包含异常表达式，必须紧跟 throw 关键字
- throw 后委托 ExpressionParserLayer 解析异常表达式
- 状态机：throw 已读 → ThrowValue（解析表达式）→ StatementEnd
- 配合 try-catch-finally 构成完整异常处理系统
- 测试 252 → 262（+10：简单 throw、throw 表达式、throw 构造、配合 try-catch、不同上下文、错误用例）
- 异常处理系统完整：try-catch-finally（捕获）+ throw（抛出）

### 2026-07-26 · M10 SeqBlockParserLayer（roadmap #11，含表达式形态）
- SeqBlockExpressionASTNode：从 StatementNode 移至 ExpressionNode，继承自 ExpressionASTNode，实现语句+表达式双形态
- SeqBlockParserLayer 实现 IResultProducer，支持作为表达式返回节点（通过结果传递机制）
- ExpressionParserLayer 集成：在 Primary 状态识别 seq/volatile 关键字，委托给 SeqBlockParserLayer（shouldKeepToken: true）
- seq 作为表达式：`var result = seq { return@seq compute() }`、`var r = seq named calc { return@calc getValue() }`
- 架构洞察：seq/lambda/方法等的代码块共用 CodeBlockParserLayer 基建，解析逻辑统一
- 测试 249 → 252（+3：seq 作为表达式的 3 个用例）
- 完整覆盖：简单 seq、volatile、using（单个/多个/带类型）、named、组合、表达式形态、错误用例

### 2026-07-26 · M10 SeqBlockParserLayer（roadmap #11）
- SeqBlockParserLayer 完整实现：seq 块的作用域、volatile 修饰符、using 资源绑定、named 标签
- using 绑定支持多个资源，每个绑定为 `using(const/var name[:Type] = initializer)`
- using 绑定委托 VariableDeclarationParserLayer 的子集逻辑：类型标注委托 TypeReferenceParserLayer，初始化委托 ExpressionParserLayer
- seq 代码块委托 CodeBlockParserLayer 解析，支持所有已实现语句类型
- 新增 AST 节点：SeqBlockStatementASTNode、UsingBindingASTNode
- 新增 ASTNodeType：SeqBlockStatement、UsingBinding
- 新增关键字：seq、using、volatile
- CodeBlockParserLayer 集成 seq/volatile 语句分发
- 测试 235 → 249（+14：简单 seq、volatile、using 单个/多个/带类型、named、组合、4 个错误用例）
- 限制：seq 作为表达式（return@seq/return@label）需要在 ExpressionParserLayer 中集成，当前仅支持语句形态

### 2026-07-26 · M9 TryCatchFinallyParserLayer（roadmap #10）
- TryCatchFinallyParserLayer 完整实现：try 块、多个 catch 子句（异常变量可为 _ 表示丢弃）、finally(e) 参数（e 为异常或 null）
- catch 异常类型委托 TypeReferenceParserLayer 解析，支持完整类型引用（含泛型、可空）
- catch/finally 代码块委托 CodeBlockParserLayer 解析，支持所有已实现语句类型
- 新增 AST 节点：TryCatchFinallyStatementASTNode、CatchClauseASTNode
- 新增 ASTNodeType：TryCatchFinallyStatement、CatchClause
- 新增关键字：throw（常量定义，解析留待后续）
- CodeBlockParserLayer 集成 try 语句分发：try 关键字触发 TryCatchFinallyParserLayer
- 测试 226 → 235（+9：简单 try-catch、多 catch、丢弃变量、try-finally、try-catch-finally、嵌套 try、3 个错误用例）
- 验证通过：至少一个 catch 或一个 finally、catch 后必须有类型、finally 后必须有参数

### 2026-07-18 · M7 P2 语句系统核心（CodeBlock / if 语句 / 循环 / return / 赋值）
- CodeBlockParserLayer 重写：语句识别与分发中枢；语句以换行或 `}` 结束；var/const、if、for/while/do 委托专门层，return/break/continue 以内部子状态直接处理，表达式语句后跟 `=` 转为赋值
- IfStatementParserLayer 扩展语句模式：else 可选、支持 else if 链（ElseBranch 为块或嵌套 IfStatement）
- LoopParserLayer（roadmap #9）全形态：for-each/范围（`0 to 10` → RangeExpressionASTNode）/while/do-while/named 标签
- 新增 AST/StatementNodes.cs（CodeBlock/IfStatement/Loop/Return/LoopControl/Assign）；Keywords 新增 return/break/continue/to/do
- VariableDeclarationParserLayer 修复：`}` 可终止块内末语句（三个结束状态）
- 顺带修复：return 带值时 handler 捕获已置空字段的 NRE；@标签/named 标签增加标识符首字符校验（拒绝数字）
- 测试 184 → 226（+42：CodeBlock 27、Loop 15）

### 2026-07-18 · M8 P1 收尾（Lambda / if / switch 表达式 + typeOf/as/is）
- LambdaExpressionParserLayer（roadmap #21 提前落地）：完整/泛型/async lambda、trailing lambda（脱糖为以 lambda 为唯一实参的调用）；形参/泛型形参/返回类型分别复用 ParameterList/GenericParameters/TypeReference 层
- IfStatementParserLayer / SwitchStatementParserLayer（roadmap #7/#8 表达式模式）：if 表达式强制 else、switch 表达式强制 default；`_` 模式匹配按普通符号解析
- TypeOfExpressionParserLayer；is/supers/with/as/as? 改为专用 AST 节点（CastExpressionASTNode/TypeCheckExpressionASTNode），右侧委托 TypeReferenceParserLayer，is/as 不再按二元运算符处理；as? 安全转换标记在类型操作等待态消费
- 结构化 Layer 统一允许跨行书写：结构性等待状态跳过换行，表达式内部仍由 ExpressionParserLayer 按行终止
- 新增 ASTNodeType：IfExpression/SwitchExpression/TypeOfExpression/CastExpression/TypeCheckExpression；新增 Keywords：async/switch/typeOf
- 测试 145 → 184（+39：表达式套件 +13、Lambda 15、if 8、switch 6、typeOf 7）；roadmap #4 全部完成，P1 收官

### 2026-07-17 · M6 ParameterListParserLayer
- 形参全态：普通/默认/可变/具名可变；默认值委托 ExpressionParserLayer，类型委托 TypeReferenceParserLayer
- 修复 `...` 解析在符号末尾残留空名元素的问题（提交前清理）
- 测试 14/14；roadmap #5 完成

### 2026-07-17 · M5 表达式后缀链 + ArgumentListParserLayer
- 后缀链：调用 `(`、索引 `[`、成员 `.`、安全访问 `?.`、泛型实参 `\<`，任意链式组合
- 实参列表支持位置/具名/混合；new 构造参数接入
- 纯符号路径保持 Symbol 形态（存量测试零破坏）
- 表达式套件扩至 54/54；roadmap #4 大部分完成

### 2026-07-17 · M4 GenericParametersParserLayer
- 泛型声明全态：参数/out/in 型变/可变参数/extends/supers/with 约束
- 新增 `Parser.Parse(tokens, entryLayer)` 重载，任意 Layer 可独立测试
- 测试 21/21；roadmap #22 完成（P6 提前落地）

### 2026-07-17 · M3 泛型语法迁移 `\<...>`
- 语言修订：泛型列表统一 `\<` 开启、`>` 闭合；`<` 解放为小于号
- 文档全量迁移（SYNTAX/RUNTIME/BIL 引用/ROADMAP/活文档）
- Lexer 不合并 `>` 系列；表达式层重组 `>=`/`>>`/`>>>`
- SymbolLayer 以 `\` + `<` 进入泛型模式；嵌套泛型 `>>` 修复
- 测试 18/18（GenericParsingTests）

### 2026-07-17 · M2 结果传递机制
- IResultProducer/IResultConsumer 接口 + 弹层自动传递
- VariableDeclaration.Initializer 真正保存；ExpressionParserLayer 接入（字面量包装/分组/一元/二元右操作数回填）
- 无优先级规则强制：`1 + 2 * 3`、`not not x`、`-x + y` 均报错
- 顺带修复：二元 Completed 状态未处理、十六进制 `0xFF` 后缀误判（值为 15 的隐藏 bug）
- 表达式测试 29/29

### 2026-07-17 · M1 P0 核心基础
- LiteralParserLayer（15/15）、TypeReferenceParserLayer（3/3）、VariableDeclarationParserLayer（10/10）
- 层栈式 Parser 架构验证可行（PushLayer/PopLayer/Continue 协议）

---

**格式说明**：后续里程碑在「里程碑历史」**顶部**追加新段落（倒序），并同步更新 §1 总览、§2 可解析语法、§3 组件状态、§5 下一步、§6 技术债务。
