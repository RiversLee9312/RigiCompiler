# RigiCompiler 架构指南

> 代码库结构与核心设计决策；改动代码前必读。开发约定见 [development.md](development.md)；本文件由 AGENTS.md §1/§3/§4 拆分而来。

---

## 1. 项目概述

Rigi 是一门现代的、类型安全的编程语言，本仓库是它的编译器。语言设计目标：

- **完全具化的泛型**：运行时类型信息完全保留（reified），不擦除
- **协程为核心**：从 `main` 开始的原生协程支持
- **值类型/引用类型分离**，`rich`/`shared` 类型声明修饰符
- **Wrapper 系统**：类似 Python 装饰器 + Java 注解的修饰器机制
- **无运算符优先级**：所有运算必须用括号明确指定（见 §4.1）

编译器目标架构：

```
Rigi 源码 (.rg) → Frontend (Lexer + Parser) → 语义分析
                    → BIL (Basic Intermediate Language)
                    → Middleware (LLVM IR Generator)
                    → LLVM 工具链 → 原生可执行文件
```

BIL VM 已是仓库的一部分（`Bil/Vm`，行为参考实现）。

---

## 3. 代码库结构

```
RigiCompiler/
├── Program.cs                # 薄入口：命令行解析 → 分发 → 退出码
├── RigiCompiler.csproj      # net8.0，Exe，Nullable enable
├── AST/                      # AST 节点定义（按类别分文件）
│   ├── ASTNode.cs               # AST 节点基类 + RootASTNode
│   ├── SymbolNodes.cs           # 符号结构（Symbol/SymbolElement/SymbolASTNode）
│   ├── ImportNodes.cs           # import 声明节点（ImportASTNode + [AstCarrier] ImportItem）
│   ├── LiteralNodes.cs          # 字面量节点（LiteralASTNode 基类 + Int/Float/String/Bool/Null 等）
│   ├── TypeNodes.cs             # 类型引用节点
│   ├── DeclarationNodes.cs      # 声明节点（变量声明等）
│   ├── ExpressionNodes.cs       # 表达式节点（含 ExpressionRootASTNode 挂载点、
│   │                            #   PathExpression 统一路径节点四件套）
│   ├── StatementNodes.cs        # 语句节点（代码块/if/循环/return/赋值等）
│   ├── ASTIntegrityValidator.cs # AST 完整性验证器（Parse 成功后自动运行，[ChildAstNode]/[AstCarrier] 标注驱动；
│   │                            #   含 Span 校验与「未标注 AST 成员」类型审计）
│   ├── ASTVisitor.cs            # 统一 AST 遍历基建（[ChildAstNode] 子节点枚举唯一实现）
│   ├── AstJsonlSerializer.cs   # AST 树 JSONL 序列化 v2（carrier 记录化、字段名键控，--dump-ast 输出）
│   └── AstJsonlDeserializer.cs # JSONL → AST 完整反序列化（产物强制过 Validator）
├── Parser/                   # Parser 层实现（每层一个文件）
│   ├── Parser.cs                # 核心协议：IParserLayer、ParserLayerResult、
│   │                            #   TokenDisposition、ParserLayerContext、Parser 主循环
│   ├── Keywords.cs              # 关键字常量（Lexer 不区分关键字，由 Parser 比对识别）
│   ├── RootParserLayer.cs       # 解析入口层，负责识别顶层结构并委托
│   ├── LiteralParserLayer.cs    # 字面量 + 插值段序列状态机（InterpolationStart
│   │                            #   委托 ExpressionParserLayer 就地填充段 Root，§3.8）
│   ├── TypeReferenceParserLayer.cs  # 类型引用（不含 rich/shared，见 §4.2）
│   ├── VariableDeclarationParserLayer.cs
│   ├── ExpressionParserLayer.cs # 表达式框架（识别 + 运算符 + 委托）
│   ├── PathParserLayer.cs       # 符号路径（收窄为类型引用与 import 路径专用）
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
│   ├── Tokens.cs                # Token 定义（TokenType + Word/String/Notation/Comment/LineBreak/EndOfFile）
│   ├── Notations.cs             # 符号常量（单字符/多字符记号）
│   ├── StringEscape.cs          # 共享转义表
│   └── 各 LexerLayer 每类一文件（CommentBlock/CommentLine/Word/String/Char/
│                                #   MultilineString/Notation/Slash/Quote/Base 十层）
├── Core/                     # 基础设施
│   ├── Exceptions.cs            # LexerException / ParserException（用户源码错误）
│   ├── CommandLine.cs           # CLI 内核：CommandLineMask（选项自描述元数据）、数据驱动解析器、
│   │                            #   注册表、帮助文本程序生成
│   ├── Commands.cs              # CLI 插件：compile/test/help 三个 COMMAND 及其 --sub-cmd
│   └── Logger.cs                # 唯一日志出口：Verbose/Warning/Error 分级；verbose 默认关闭，
│                                #   --verbose 开控制台 verbose，--log-to 全量 JSONL 落盘
├── Semantic/                 # 中端 P1–P3 + 符号图 + 诊断
│   ├── Diagnostics.cs           # 可恢复诊断模型：Diagnostic{Severity/Phase/Span?/Message}
│   │                            #   + DiagnosticBag（全编译单元单实例、只追加、HasErrors 门槛）
│   ├── CompilationUnit.cs       # 编译单元模型：多源文件 RootASTNode + DiagnosticBag + SymbolGraph
│   ├── DeclarationCollector.cs  # P1 声明收集：符号壳 + DeclarationCollection/FileContext
│   │                            #   + namespace 驻留合并 + import 登记 + ext 待注册 + 重复诊断
│   ├── DeclarationResolver.cs   # P2 瘦入口：13 步顺序
│   │                            #   启动各阶段 visitor + Freeze；语义规则见各 visitor 文件头
│   │                            #   注释与 docs/legacy/PROGRESS_REPORT.md 编年史
│   ├── Resolution/              # P2 visitor 化基建（协议同 Binding/ 三基类，阶段级
│   │                            #   visitor——P2 遍历为「阶段 × 条目平铺」非 Binder 深递归）：
│   │   ├── ResolverVisitor.cs      # CRTP 基类（静态 Visit 唯一入口 + Enter/Exit finally 配对）
│   │   ├── ResolveEnvironment.cs   # 只读环境（unit/declarations/NameResolver + entries 三表
│   │   │                           #   IReadOnly 暴露 + Error 落袋 + ModifiersOf/FindField/
│   │   │                           #   Substitute 等共享设施）
│   │   ├── DeclEntry.cs            # 声明条目模型（节点+符号+解析上下文+InGraph）
│   │   ├── EntryCollector.cs       # 骨架遍历收集静态设施（标记位/Accessibility 写符号）
│   │   └── 阶段簇级分文件          # Import/TypeReference/Inheritance/Modifier/Accessor/
│   │                               #   Override/Native/
│   │                               #   AsyncGate（声明侧闸门 2/3/5 +
│   │                               #   async 仅函数收口）/ConversionOperator
│   │                               #   （castTo/castFrom 声明形状）/
│   │                               #   Contagion（传染+字段闭包+shared 闸门三 checker）/
│   │                               #   GenericConstraint/Wrapper（三 resolver）/
│   │                               #   EnumCase（洞独占性/case 名复核/
│   │                               #   判别值落定——模板绑定归 P3 声明点）/
│   │                               #   ProxyShape/ProxyMatching（ProxyShapeChecker
│   │                               #   形状校验；ProxyDispatchResolver 删除→
│   │                               #   ProxyMatching/ProxyMatchChecker 形状匹配 +
│   │                               #   名中形状不符诊断，零符号合成；烘焙归 Middleware）
│   ├── NameResolver.cs          # 名字解析共享设施：符号路径/类型引用/泛型实参
│   │                            #   解析，诊断按构造传入的 Phase 落袋（P2/P3 各自实例化）
│   ├── AccessChecker.cs         # 使用点访问控制共享设施（SYNTAX §16.1）：
│   │                            #   P2 声明侧与 P3 函数体内同一份判定（internal 单编译
│   │                            #   单元恒可见）+ 统一诊断措辞
│   ├── Binder.cs                # P3 瘦入口（visitor 化；语义规则见各 visitor 文件头
│   │                            #   注释与 docs/legacy/PROGRESS_REPORT.md 编年史）
│   ├── Binding/                 # P3 visitor 化基建：
│   │   ├── BinderVisitor.cs        # CRTP 三基类（通用/ExpressionVisitor 追加
│   │   │                           #   expectedType 下传/BinderShellVisitor 壳填充）——
│   │   │                           #   静态 Visit 唯一入口 + Enter/Exit 生命周期模板
│   │   ├── BindEnvironment.cs      # 只读环境（unit/declarations/NameResolver/诊断落袋；
│   │   │                           #   参数默认值记忆化表——懒绑定回调由 Driver 注入）
│   │   ├── BindContext.cs          # 函数级状态组合根（Frame/Accessor/
│   │   │                           #   Labels/Flow/Locals 五成员；组件即方言）
│   │   ├── BindFunctionFrame.cs    # 只读函数帧（Method/FileCtx/DeclaringType/
│   │   │                           #   IsDefaultValueContext + HasThis/CanAccess）
│   │   ├── AccessorBodyState.cs    # 访问器体状态（value 别名：Field/IsSetter）
│   │   ├── ProxyBodyState.cs       # proxy 模板态绑定语境（IsActive + SelfType=
│   │   │                           #   TTarget 泛型参数；self/inner 占位，this=wrapper
│   │   │                           #   实例自身——不再重写 BoundWrapperAccess）
│   │   ├── BindLabelState.cs       # 控制流标签栈集（值块/循环/switch 占位/seq
│   │   │                           #   标签四栈封装 + 命中查找领域方法）
│   │   ├── FlowState.cs            # DA 流分析（分叉/合并/快照恢复原语；
│   │   │                           #   smart cast 收窄表的家——同生命周期）
│   │   ├── Scope.cs                # 词法作用域链
│   │   ├── Dispatchers.cs          # 类别分派唯一 switch（Expression/Statement/Block）
│   │   ├── BoundAnalysis.cs        # GuaranteesReturn/值块终止/语句平铺枚举/TypeDisplay
│   │   ├── BindingDriver.cs        # 声明骨架遍历 + 三阶段启动（①参数
│   │   │                           #   默认值声明点绑定（记忆化按需，前向依赖
│   │   │                           #   声明顺序无关）①.5 enum case 模板绑定（
│   │   │                           #   init 选择 + 洞签名落定符号）②逐函数体
│   │   │                           #   return 全路径检查 + init 映射赋值合成
│   │   │                           #   （§9.3：无体产 body/有体前插）；
│   │   │                           #   阶段 2.5/2.6 删除（合成体绑定取消）；
│   │   │                           #   proxy 声明体在阶段 2 按模板态绑定（ProxyBodyState）；
│   │   │                           #   降级调用点资格判定 + CallWildcard，无体合成）
│   │   ├── OverloadResolution.cs   # 重载解析设施（SYNTAX §4.2：结构过滤/
│   │   │                           #   类型适用性/最具体胜出 + 平局打破；source-level
│   │   │                           #   ranking 唯一落点 BIL §3.3；调用/init/索引读共用）
│   │   ├── SymbolLookup.cs         # 实例成员查找/泛型字段最小替换/IsAssignable
│   │   ├── MemberLookup.cs         # 名字解析查找序（宿主 BaseType 链 → 命名空间链
│   │   │                           #   → 通配 import；容器解析）
│   │   ├── TypeReferences.cs       # 函数体内类型引用解析（NameResolver 委托）
│   │   ├── NarrowKey.cs            # 收窄键（根（局部/参数/this/静态字段符号）
│   │   │                           #   + const 字段链，稳定链判定含 init 排除）
│   │   ├── ConditionFacts.cs       # 条件事实提取器（is/null 判等/and-or-not
│   │   │                           #   → 真/假边收窄事实对，纯函数式）
│   │   ├── ConstFieldRules.cs      # const 字段规则（赋值检查 init 豁免 +
│   │   │                           #   收窄资格 IsNarrowable）
│   │   ├── AsyncGates.cs           # async 边界闸门 P3 侧（§4.5）：
│   │   │                           #   调用点 1/2（BoundTree 后置遍历单落点）+
│   │   │                           #   async lambda 捕获 4（AST 级粗粒度扫描）
│   │   ├── CallableModel.cs        # Func/Action/Cell 抽象基类族查找与构造
│   │   ├── CellClassFactory.cs     # 统一 cell 存储：逐变量合成 ..cell..UUID 隐藏子类
│   │   │                           #   （pub value + wrapped(W) + override getValue/setValue）
│   │   └── Visitors/               # 结构 visitor 簇（Literal/Declaration/Conditional
│   │                               #   含值块壳/Loop/Switch/TrySeq/Binary/Path/Call/
│   │                               #   TypeCheck/Lambda（async 捕获闸门 4）——
│   │                               #   语义分析范围：
│   │                               #   作用域链查找序/var 推断/intrinsic/调用规范
│   │                               #   参数序/DA/if 值块/循环/switch 占位/
│   │                               #   try/seq/实例链上色/?. if?/插值规范化/
│   │                               #   is-supers-with 双形态/typeOf/索引绑定
│   │                               #   与表达式底座链/重载解析集成/
│   │                               #   wrapper place 绑定与只读禁令（
│   │                               #   双源同池查找 + 链末 Colon 一处收口））
│   ├── Bound/                   # BoundTree 节点集（按类别分文件仿 AST/）：
│   │                            #   BoundNode（Syntax 必填）/BoundExpression（Type）/语句节点 +
│   │                            #   BoundFunctionBody{Method, Locals, BoundBlock}；增补
│   │                            #   BoundIfStatement/BoundValueBlock 值块/BoundIfExpression/
│   │                            #   BoundReturnValueStatement/BoundCompoundAssignmentExpression；
│   │                            #   增补 BoundLoop（施工壳）/BoundLoopControl；
│   │                            #   增补 BoundThis/BoundInstanceCall/BoundFieldAccess
│   │                            #   三实例表达式节点 + BoundLoop For 路径（LoopVariable/
│   │                            #   Iterable/协议三方法）；增补 BoundSwitchStatement/
│   │                            #   BoundSwitchExpression/BoundThrowStatement/
│   │                            #   BoundSwitchPlaceholderExpression（`_` 占位）；
│   │                            #   增补 BoundCastExpression/BoundTryStatement/
│   │                            #   BoundCatchClause/BoundSeqStatement/BoundSeqExpression
│   │                            #   （含 BoundValueBlock.IsVolatile）；增补
│   │                            #   BoundTypeCheckExpression（Kind 三态 + TargetType/
│   │                            #   TargetValue 互斥双槽）/BoundTypeOfExpression
│   │                            #   （Operand/TargetType 互斥，Type = Type\<T> 构造）；
│   │                            #   增补 BoundSmartCastExpression（smart cast
│   │                            #   标记：Operand + NarrowedType，Type = 收窄类型，
│   │                            #   P4a 物化为显式 cast）；增补
│   │                            #   BoundIndexExpression（Receiver/Index/Operator——
│   │                            #   读绑 getAtIndex、写绑 setAtIndex）；增补
│   │                            #   BoundSeqExitStatement（return@语句seq，不带值）与
│   │                            #   BoundSeqStatement.Label（仅显式 named）；增补
│   │                            #   BoundCastExpression.Conversion（castTo/castFrom
│   │                            #   名字分析产物：适用转换运算符，null = 内建）；
│   │                            #   增补 BoundEnumCaseExpression（Case/规范序洞
│   │                            #   实参，Type=Owner）与 BoundTypeCheckExpression
│   │                            #   增 IsCase Kind + Case 第三槽（不触发 smart cast）；
│   │                            #   增补 BoundWrapperAccessExpression（
│   │                            #   §14.5 只读 place：Receiver + Application
│   │                            #   应用记录（Wrapper 为派生属性）——
│   │                            #   只作成员访问接收者，永不作
│   │                            #   路径绑定结果产出）；增补 BoundSelfExpression
│   │                            #   /BoundInnerCallExpression（proxy 模板占位→
│   │                            #   get.self/invoke fn(..inner)）
│   ├── StdlibSources.cs         # stdlib 内嵌源载入：stdlib/**/*.rg 以 EmbeddedResource
│   │                            #   内嵌、编译时取出解析注入编译单元，与用户源同走 P1–P4
│   ├── DispatchExplainer.cs     # 派发链诊断报告器（RUNTIME §15）：
│   │                            #   数据源 = 应用登记 × ProxyMatching 预览 + 降级资格
│   │                            #   （报告 Middleware 将烘焙的链；无合成 fn/router 槽）
│   └── Symbols/                 # 符号图内核（容器成员表/全局命名空间驻留）：
│                                #   SemanticSymbol 家族（引用相等即身份；
│                                #   Accessibility/SourceFile 与 MethodSymbol 三标记/HasBody、
│                                #   FieldSymbol 访问器三槽 Getter/Setter/HasBackingStorage；
│                                #   MethodSymbol.IsAsync——async 边界
│                                #   闸门检查点分派依据；EnumCaseSymbol
│                                #   （Owner/Discriminant + ResolvedInit/HoleParameters
│                                #   模板槽）+ TypeSymbol.Cases 表 + ParameterSymbol.
│                                #   MappedField（§9.3 init 映射目标，P2 落定））+
│                                #   LocalSymbol（P3 产生；
│                                #   Type 可空——null 仅限 P4a 合成 .breakid 局部；
│                                #   AppliedWrappers——局部变量 wrapper
│                                #   应用 P3 登记（栈上声明不进 P1/P2，§14.9
│                                #   矩阵 C 恒合法）+ CellStorage 统一 cell 存储槽
│                                #   （LocalSymbol/ParameterSymbol/FieldSymbol/
│                                #   TypeSymbol 回挂兼识别标记））、
│                                #   SymbolGraph 构造泛型驻留 + Freeze + Substitute
│                                #   单源与构造类型 BaseType 创建即代入/统一回填、BootstrapSymbols
│                                #   （String.Add intrinsic、Any.toString open 承诺
│                                #   + Object open override 默认实现；统一 Public；
│                                #   Array\<T> getAtIndex/setAtIndex operator——索引绑定
│                                #   内建目标，P4b 直发 §13.6 不走 invoke；删
│                                #   DowngradeRouter/DowngradeChain/ProxySpecialization
│                                #   等合成槽；BootstrapSymbols.CallWildcard =
│                                #   Any.call???（pub native rigi_rt/call???，§22.5
│                                #   VM hook，EnsureCallWildcard 幂等，胖值签名））、
│                                #   CanonicalSymbolPrinter
├── Lowering/                 # 中端 P4：依赖方向 Lowering → Semantic/Bil 单向
│   ├── ClosureStoragePlan.cs    # 统一 cell 存储判定表（§5.2 捕获 + §14.3 wrapper 值；读 getValue/写 setValue/对象引用 + init 豁免）
│   ├── Lowered/                 # LoweredTree 节点集（覆盖全部 Bound 节点，仿 Bound/ 分文件）：
│   │                            #   LoweredNode（Origin 必填回指 BoundNode）/LoweredExpression
│   │                            #   （Type 透传）/块/局部声明/表达式语句/void 调用/赋值/return/
│   │                            #   字面量/值引用/字段引用/二元/一元/带返回值调用/new + LoweredFunctionBody；
│   │                            #   增补 LoweredIfStatement/LoweredConstantExpression（脱糖合成节点，
│   │                            #   Origin 指最近语法来源）；增补 LoweredLoop/
│   │                            #   LoweredBreak/LoweredContinueStatement
│   │                            #   （Judge 块 + 合成 .breakid 局部 .bN——Type null 特例）；
│   │                            #   增补 LoweredThis/LoweredInstanceCall（Type 自带）/
│   │                            #   LoweredFieldAccess；增补 LoweredSwitch/
│   │                            #   LoweredSwitchCase/LoweredThrowStatement；
│   │                            #   增补 LoweredCastExpression（Type 自带）/
│   │                            #   LoweredTryStatement（ExceptionSlot）/LoweredTryCatch/
│   │                            #   LoweredSeqBlock（volatile → §9.6 block 修饰符）；
│   │                            #   增补 LoweredTypeCheckExpression/
│   │                            #   LoweredTypeOfExpression（Type 自带，恒等重写）；
│   │                            #   增补 LoweredIndexExpression（Receiver/Index，
│   │                            #   Type 透传，不携带 Operator）；增补
│   │                            #   LoweredStructuredExit（return@ source-level
│   │                            #   exit 标记，StructuredExitRouting pass 消费，
│   │                            #   pass 后不得残留）；增补
│   │                            #   LoweredEnumCaseExpression（Case/规范序洞实参，
│   │                            #   cast 物化按洞签名）与 LoweredTypeCheckExpression
│   │                            #   Case 槽（IsCase）；增补
│   │                            #   LoweredGetWrapperExpression（§12.4 值拷贝）
│   │                            #   与 LoweredWrapperFieldExpression（§13.3
│   │                            #   PlaceChain：FieldSymbol|TypeSymbol→field|wrapper，仅作写 place；
│   │                            #   读侧值拷贝后使用普通字段访问）；增补 LoweredGetSelfExpression
│   │                            #   /LoweredCallInnerExpression（§12.5/§15.4）
│   ├── Lowerer.cs               # P4a 瘦入口
│   ├── LoweredVisitor.cs        # P4a CRTP 基类（同 Binder 协议，无 scope/expectedType）
│   ├── LowerEnvironment.cs      # 只读环境（unit/诊断）
│   ├── LowerContext.cs          # 函数级状态组合根（Method
│   │                            #   + Synth/Output/Targets/ExitTargets 四组件）
│   ├── SynthLocalFactory.cs     # 合成局部工厂（.sN/.breakid .bN 独立计数统一
│   │                            #   登记——顺序即 .vars 发射顺序 + ReferenceTo）
│   ├── LowerOutputState.cs      # 前置语句机制（输出列表栈封装：Push 双形态/
│   │                            #   Pop/Current/Add）
│   ├── LowerTargetState.cs      # 降级目标映射栈集（循环/switch 占位/
│   │                            #   安全访问三栈封装 + 命中查找）
│   ├── StructuredExitTargetTable.cs # return@ 目标映射表（Stage B：值块/语句
│   │                            #   seq 目标 → 结果局部 + 目标 region breakId）
│   ├── StructuredExitRouting.cs # P4a 末尾 normalization pass（Stage B）：
│   │                            #   LoweredStructuredExit 展开为 route local
│   │                            #   + break/dispatcher else-if 链
│   ├── LowerDispatchers.cs      # 类别分派唯一 switch + LowerBlockVisitor（输出列表压弹）
│   ├── LoweringDriver.cs        # 逐函数体启动（合成局部收尾进 Locals）
│   ├── LoweringFacility.cs      # LowerArguments/EnsureDeclaredType（BIL §6.5 cast 物化）
│   │                            #   + variadic 索引 ABI 元素类型设施组（vargs → Any/
│   │                            #   kwargs → Pair\<String, Any>，读拆箱写装箱）
│   ├── Rewriters/               # 结构 visitor 簇（Statement/Loop（Judge 块）/Switch
│   │                            #   （常量表恒等 + pattern 链降级）/TrySeq/ValueBlock
│   │                            #   （值块降级——return@ 产 StructuredExit 标记）/
│   │                            #   Expression（短路/if 表达式/复合赋值脱糖）/NullSafety
│   │                            #   （?. if? unwrap/wrap）/Destructuring）+
│   │                            #   WrapperPlaceLowering（wrapper place
│   │                            #   降级——get.wrapper 值拷贝链与
│   │                            #   get.wrapper(.field)+get.field/set.wrapper.field 链按应用类别分派；
│   │                            #   局部/静态改 cell 根分派）+
│   │                            #   CellStorageLowering（静态/全局 cell 读写改写）
│   │                            #   ——脱糖范围覆盖全部 Bound 节点；未覆盖节点 P4 Error + 跳过函数体
│   ├── BilEmitter.cs            # P4b 瘦入口
│   ├── EmitVisitor.cs           # P4b CRTP 基类（签名带 BilBlock target 施工目标——
│   │                            #   下行填充，三树中与协议贴合度最高）
│   ├── EmitEnvironment.cs       # 模块级（Module/四类资源去重表跨 fn 共享，值为资源对象）
│   ├── EmitContext.cs           # 函数级组合根（Function + Temps/BlockIds）
│   ├── TempVarTable.cs          # 临时变量表（.tN 工厂，自 EmittingFacility 收编）
│   ├── BlockIdAllocator.cs      # 分支 block 编号分配器（if/loop/switch/seq/try）
│   ├── EmitDispatchers.cs       # 类别分派（语句 Unit/值 BilVariableOperand——
│   │                            #   物化契约类型化）+ EmitBlockVisitor
│   ├── EmittingDriver.cs        # 模块组装 + fn 定义（.args/.vars/entry block/void 补 ret）
│   ├── EmittingFacility.cs      # 资源登记/intrinsic 枚举映射/转义（§19.1 标量全形态/
│   │                            #   §19.4 switch-table/§19.5 catch-table）
│   └── Emitting/                # 结构 visitor 簇（LocalSymbols（§8 平铺 + ext 裸条目 +
│                                #   §8.5 .case 声明（洞签名 + 判别值 res/auto）+
│                                #   §8.3.1 wrapped(W) 应用标记投影（无隐藏字段
│                                #   声明）+ §8.4 wrapper-proxy(specific|wildcard)
│                                #   模板 fn 投影（proxy 声明体即模板进 BIL，
│                                #   无合成特化/原始体/shim））/
│                                #   Statement（§16 结构化指令发射：if/loop/switch/seq/try/
│                                #   break-continue/throw + set.var/get/set.field §13 +
│                                #   set.wrapper.field 写链）/
│                                #   Value（§11 运算/invoke/new/cast §12.1–12.2/type.is
│                                #   §12.3/getid §12.5/new.case §14.3 与 type.is.case
│                                #   §12.3 + get.wrapper §12.4 与
│                                #   get.wrapper(.field)+get.field 读链 + get.self §12.5
│                                #   与 invoke fn(..inner) §15.4））——发射范围覆盖
│                                #   全部 Bound 节点；全部强类型指令构造（无 opcode 字面量）
├── Bil/                      # BIL 生态（对中端零依赖：
│                            #   字符串身份，不引用 Semantic/AST）
│   ├── BilModule.cs             # Module/Metadata/Resources（§4/§19：标量/null/通用
│   │                            #   collection + switch-table/catch-table 专用资源类）
│   │                            #   + BilScalarType 枚举
│   ├── BilSymbols.cs            # 类型与成员声明（§8.2–§8.5）+ BilTypeKind/BilMemberKind/
│   │                            #   BilAccessibility/BilKeyword 枚举与 BilModifier 子类族
│   │                            #   （BilAccessorModifier：getter(FIELD)/setter(FIELD)；
│   │                            #   BilProxyKind 两态 specific|wildcard +
│   │                            #   BilWrapperProxyModifier（proxy 模板 fn 标记）+
│   │                            #   BilWrappedModifier（wrapped(W) 应用标记）；
│   │                            #   GetSelfInstruction 与 invoke 指令见
│   │                            #   Compute/Data 指令文件）
│   ├── BilFunction.cs           # Function/.args/.vars/Block（§9）+ BilBlockModifier 枚举
│   ├── BilInstructions.cs       # 指令基类（Origin(object?) 占位 + WriteTo 排版协议）+
│   │                            #   操作数模型（§10；blk/res 持对象引用）
│   ├── BilComputeInstructions.cs # §11–§12 指令（BilBinaryOp/BilUnaryOp/BilTypeCheckKind
│   │                            #   + get.self）
│   ├── BilDataInstructions.cs   # §13–§15 指令（load/get/set/new/invoke 全形态
│   │                            #   + §13.6 set.array + invoke fn(..inner)）
│   ├── BilControlFlowInstructions.cs # §16 指令
│   ├── BilCoroutineInstructions.cs   # §17 协程指令
│   ├── BilHintInstruction.cs     # §18 hint 提示指令（block 内可忽略 backend 提示，
│   │                            #   string 资源 JSON 负载，纯位置标记）
│   ├── BilSpellings.cs          # 全部枚举 → 标准拼写唯一定义点
│   ├── BilWriter.cs             # 模型 → 标准 BIL 文本（§20 黄金示例逐行一致；
│   │                            #   指令自渲染，无 opcode switch）
│   ├── BilVerifier.cs           # BIL 验证器（§21）：入口
│   │                            #   Verify(BilModule) + BilVerificationError + §21.1
│   ├── BilVerificationContext.cs # 验证器索引（资源/类型/成员符号 + 预定义符号表
│   │                            #   + 变量类型环境）与 canonical 符号/类型引用解析工具
│   ├── BilVerifier.Symbols.cs   # §21.2 符号 + §21.7 参数包 + §21.8 声明侧矩阵
│   ├── BilVerifier.Types.cs     # §21.3 类型（指令读写分类唯一表 + 逐指令 switch；
│   │                            #   §13.6 索引严格三元组查询）
│   ├── BilVerifier.Flow.cs      # §21.4 保守 DA + §21.5 控制流 + §21.6 breakid
│   ├── BilVm.cs                 # VM 入口
│   └── Vm/                      # VM 执行器（行为参考实现）：VmContext/VmExecutor/
│                                #   VmCoroutine/VmTask/VmAlarm/VmException/VmHooks/
│                                #   VmTypeOps + Values/ 值模型
├── stdlib/                   # 编译器自携标准库源（EmbeddedResource 内嵌，见 StdlibSources；
│                             #   六源，与用户源同走 P1–P4）
│   ├── .bootstrap.rg         # 基元自举源（EnumerateInRange + core.Pair\<TKey, TValue>
│   │                            #   解构协议根，SYNTAX §18/§15.3）
│   └── core/                    # Console.rg（core.io::Console）+ collections.rg
│                                #   （IEnumerable/IEnumerator 双接口 + RangeI32/
│                                #   RangeEnumeratorI32；RangeEnumerator\<T> 抽象基类）
│                                #   + coroutine.rg（core.coroutine 类型面——Task/
│                                #   Task\<TResult>/Executor 家族/PollingAlarm/EventAlarm/
│                                #   CoroutineLocal\<TValue> 全 shared abstract + sleep native，
│                                #   SYNTAX §15.3）+ disposable.rg（core.IDisposable，
│                                #   §6.2）+ exceptions.rg（RuntimeException/IOException/
│                                #   CastException/NoSuchMethodException 四异常子类，§8.1）
├── Tests/                    # 自研控制台测试（非 xUnit/NUnit，见 development.md 测试策略）
│   ├── AstDescribe.cs           # 统一 AST 描述器（全部套件共用）
│   ├── BoundDescribe.cs         # 统一 BoundTree 描述器（P3 套件共用，仿 AstDescribe）
│   ├── LoweredDescribe.cs       # 统一 LoweredTree 描述器（P4a 套件共用，仿 BoundDescribe）
│   ├── BilTestHarness.cs        # BIL 测试基建：EmitBilUnit 全管线驱动 +
│   │                            #   CheckBilValid/CheckBilInvalid 验证器断言 +
│   │                            #   res 重编号形状黄金 CheckFnShape/CheckResShape
│   ├── TestHarness.cs           # 统一驱动与断言基建（CheckSemanticError）
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
| `docs/SYNTAX.md` | **语言语法规范（最权威）**（索引文档，正文在同名子目录） | ⭐⭐⭐ 有歧义时以此为准，不要猜语法 |
| `docs/RUNTIME.md` | 运行时模型与类型系统（索引文档，正文在同名子目录） | ⭐⭐⭐ |
| `docs/BIL_STANDARD.md` | BIL 中间语言规范（索引文档，正文在同名子目录） | ⭐⭐ |
| `docs/legacy/PARSER_ROADMAP.md` / `docs/legacy/PROGRESS_REPORT.md` | Parser 路线图与进度（历史档案，不再更新） | ⭐⭐ |
| `docs/compiler/syntax/EXPRESSION_ARCHITECTURE.md` | 表达式架构专项设计 | ⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md` | 语义分析与 BIL 生成架构（中端） | ⭐⭐⭐ |
| `docs/legacy/SEMANTIC_ROADMAP.md` | 语义分析路线图（历史档案，不再更新） | ⭐⭐ |
| `docs/compiler/vm/BIL_VM_DESIGN.md` | BIL VM 设计 | ⭐⭐⭐ |
| `Parser/Parser.cs` | 层栈式 Parser 的核心协议 | ⭐⭐⭐ |
| `Lexer/Tokens.cs` / `Parser/Keywords.cs` / `AST/ASTNode.cs` | Token/关键字/AST 基类等核心数据结构 | ⭐⭐⭐ |

---

## 4. 核心设计决策（改动代码前必须理解）

### 4.1 ⚠️ Rigi 没有运算符优先级

```rigi
var result = 1 + 2 * 3       // ❌ 编译错误：歧义
var result = 1 + (2 * 3)     // ✅ 必须加括号
```

对 Parser 的影响：不需要优先级表；遇到未括号化的连续运算符必须报错。实现表达式相关功能时不要引入优先级概念。

### 4.2 ⚠️ `rich` / `shared` 是类型**声明**修饰符，不是类型引用修饰符

- `rich`：**仅用于 struct / enum struct / wrapper**（class 不能用）。允许值类型持有引用，但仍是值语义、unique ownership（类似 `unique_ptr`，**不是** `shared_ptr`）。wrapper 恒为 rich struct，`rich` 由声明形式隐含，显式书写是编译错误。
- `shared`：class、rich struct、wrapper 可用，表示允许跨协程共享；`singleton` class 必须 shared。
- 二者**单向传染**：基类 rich/shared ⇒ 子类必须同标，反向可收紧（详见 SYNTAX §3.1.1）。
- 使用类型时（变量声明、函数参数）**永远不写** `rich`/`shared`。因此 `TypeReferenceParserLayer` 不处理它们；它们属于 class/struct 声明解析的职责。
- 这些规则的**检查**全部属于语义期 P2（见 `docs/legacy/SEMANTIC_ROADMAP.md`），Parser 只负责收下修饰符。

### 4.3 ⚠️ 泛型列表必须以 `\<` 开启

泛型的声明与使用统一写作 `Name\<...>`（反斜杠 + 小于号开启，`>` 闭合）：

```rigi
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

### 4.7 ⚠️ Parser 架构规则（必须遵守）

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
   追加（Parser 对绕过 Lexer 的调用方保持追加兼容），只由
   RootParserLayer 消费；非 Root 层遇 EOF：结构完整 → Pop(Replay) 上交，
   不完整 → 抛 "Unexpected end of file"。禁止用换行伪装 EOF。
7. **新 Layer 必须有独立测试**（`TestRootParserLayer` 驱动，见 development.md 测试策略）。
8. **注释由 Parser 主循环统一跳过**：CommentToken 不参与语法，
   分发时直接跳过；各 Layer 不再自行处理注释。

解析成功后 `ASTIntegrityValidator` 自动验证 AST 不变量：遍历只走
`[ChildAstNode]` 标注的成员（`[AstCarrier]` 对象深入其公共字段），校验每个
子节点的 Parent 指向持有者，另含 Root 均已填充、节点无共享、Parent 链无环、
switch default 规则、**每节点 Span 合法（非空、sourceName 非空、
End 不早于 Start）**、**类型审计（装 ASTNode 的字段/自动属性必须带
[ChildAstNode]/[ParentAstNode] 标注）**；失败抛 `CompilerInternalException`（内部编译器错误，
与用户语法错误区分）。节点类型一律用 CLR 类型判断（无 ASTNodeType 枚举）。
「归属后知」的场景必须用创建时归属即定的结构承载
（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）
或延迟一次性 AttachTo（注解），**禁止任何形式的 Parent 重挂**。

**Span 施工**：每个 AST 节点都有源码范围 `ASTNode.Span`（`CharRange?`）。
约定：层目标节点由 Parser 主循环按 token 流计算 span，层弹出时经
`ISpanReceiver.ReceiveSpan` 回填（一律 `target.Span ??= span` 只填空）——
新 Layer 若有施工目标，应实现 `ISpanReceiver`；层内自建节点由所在层显式设置
（创建记 Start，完成经 `ParserLayerContext.GetPreviousLocation()` 封 End）；
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
**Span 统一为左闭右开 `[Start, End)`**：Start 指向首个字符，
End 指向最后一个字符的下一位置（token 与 AST 节点一致；EOF 为零宽范围）；
语句/声明的 span 不拖尾换行符到下一行（终态层不消费换行）。
Validator 与 AstJsonlSerializer 的 [ChildAstNode] 反射统一走
`AST/ASTVisitor.cs` 的 `AstStructureReflection`，禁止再写第三份反射下钻。

**JSONL 往返**：`AstJsonlSerializer`（v2：carrier 记录化、字段名键控）
与 `AstJsonlDeserializer`（完整反序列化，产物强制过 Validator）构成往返；
消费方按字段名取值，不依赖字段顺序。

**allowBareReturn 传染**：lambda 是裸 return 边界（SYNTAX §5.1）——
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

- 斜杠家族（`/`、`//`、`/*`）由专门的 `SlashLexerLayer` 分流；
- `EndOfFileToken` 由 `Lexer.Tokenize` 在输出末尾追加；输入结束时以
  虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException；
- 位置计量：`CharRange.sourceName` 是源名唯一来源
  （`CharPosition` 不携带）；`CharPosition.offset` 是 0 起始字符索引；
  行/列 1 起始，换行算当前行最后一列；token 头跳过空白字符；
  EOF 冲刷帧占一个末尾虚拟位置，保证冲刷 token 的 End 正确；
- **token 范围为左闭右开 [Start, End)**：End 是最后一个字符的下一位置；
- 块注释不吞字符、不吞换行（按行分段，换行以 LineBreakToken 入流）；
  行尾归一只把 `\r\n`/`\r` 归一为 `\n`；
- 复合赋值（`+=`/`*=` 等）不合并 token（与 `>=` 同策略，Parser 遇 op+`=` 重组为
  CompoundAssignmentExpressionASTNode）；字符字面量 `'` 已实现
  （CharLexerLayer + CharToken + CharLiteralASTNode，转义复用 StringEscape）；
  多行字符串 `"""` 已实现（SYNTAX §3.3：
  Swift 风格严格多行，QuoteLexerLayer 分流 `"`/`""`/`"""`，转义表 StringEscape 单源）；
- **字符串插值词法帧机制（SYNTAX §3.8）**：字符串层遇未转义的 `${`
  （挂起 `$` 延迟判定，`\$` 不算引导）产出文本段 + InterpolationStartToken
  并**压基础层**正常词法；驱动按 **token 层**大括号计数配平（字符串/字符/
  注释内容不产生记号 token，天然豁免），归零把 `}` 改发 InterpolationEndToken
  并弹回字符串层；嵌套插值经帧栈递归，EOF 帧未闭合先于冲刷报错。
  多行层段 token 以原文暂存保序，闭界确定缩进基准后统一回填解码内容。
  段 token 的 span 不含引号与引导 `$`（首段/段尾修正）。
