# RigiCompiler 架构指南

> 面向维护者及其 coding agents 的公共架构指南，说明代码库结构与核心设计决策；改动代码前必读。开发约定见 [DEVELOPMENT.md](../../DEVELOPMENT.md)，专题与规范见 [文档索引](../README.md)。

标准库扩展的范围与分层见 [标准库 MVP 设计](../STDLIB.md)；该文档区分既有语义、建议基线和待定公共契约。

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
├── RigiCompiler.csproj      # net10.0，Exe，Nullable enable
├── Tests/TUnit/              # 唯一 source-generated TUnit/MTP 宿主，编入全部 Tests/；生产排除测试
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
│   ├── PathParserLayer.cs       # 静态符号路径（类型引用、import、namespace 与注解名；表达式路径由表达式层施工）
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
│   └── DeclarationParserLayer.cs        # 统一声明层：全局/成员/嵌套任何声明（语法阶段）
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
│   ├── Commands.cs              # CLI 插件：compile/test/vm/help；native 在 Middleware/Cli，module 在 Modules
│   ├── TestHostForwarder.cs     # test 兼容命令仅转发独立 TUnit 宿主
│   ├── Frontend.cs              # 每文件独立解析、按输入次序回放日志与错误
│   ├── CompilerJobs.cs          # indexed worker 调度、共享预算和阶段 join
│   ├── ResourceBudget.cs        # 严格 FIFO 加权资源授予，测试与编译共用
│   ├── PerformanceMetrics.cs    # 默认关闭的阶段/子进程遥测
│   └── Logger.cs                # 唯一日志出口：Verbose/Warning/Error 分级；verbose 默认关闭，
│                                #   --verbose 开控制台 verbose，--log-to 全量 JSONL 落盘
├── Modules/                  # module.yaml 独立模块、API/BIL 产物、构建缓存、hook 与发布事务
├── Semantic/                 # 中端 P1–P3 + 符号图 + 诊断
│   ├── Diagnostics.cs           # 可恢复诊断模型：Diagnostic{Severity/Phase/Span?/Message}
│   │                            #   + DiagnosticBag（编译单元主袋 + job 私袋，按输入次序合并；只追加、HasErrors 门槛）
│   ├── CompilationUnit.cs       # 编译单元模型：多源文件 RootASTNode + DiagnosticBag + SymbolGraph
│   ├── DeclarationCollector.cs  # P1 声明收集：符号壳 + DeclarationCollection/FileContext
│   │                            #   + namespace 驻留合并 + import 登记 + ext 待注册 + 重复诊断
│   ├── DeclarationResolver.cs   # P2 瘦入口：有依赖顺序的阶段 visitor
│   │                            #   启动各阶段 visitor + Freeze；语义规则见各 visitor 文件头
│   │                            #   注释与 docs/compiler/semantic/PIPELINE_AND_SYMBOLS.md 当前职责
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
│   │                            #   注释与 docs/compiler/semantic/PIPELINE_AND_SYMBOLS.md 当前职责）
│   ├── Binding/                 # P3 visitor 化基建：
│   │   ├── BinderVisitor.cs        # CRTP 三基类（通用/ExpressionVisitor 追加
│   │   │                           #   expectedType 下传/BinderShellVisitor 壳填充）——
│   │   │                           #   静态 Visit 唯一入口 + Enter/Exit 生命周期模板
│   │   ├── BindEnvironment.cs      # 共享只读输入 + job 局部合成 delta（unit/declarations/NameResolver/诊断落袋；
│   │   │                           #   参数默认值记忆化表——Driver 串行准备，worker 使用 job delta）
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
│   │   ├── BindingDriver.cs        # 串行准备参数默认值（按需记忆化，前向依赖不依声明顺序）与 enum init 选择/洞签名、
│   │   │                           #   wrapper cell/默认构造/like/Serialization/companion 合成；
│   │   │                           #   body job 独占 Context 与 Environment delta，嵌套 lambda 留在所属 job；
│   │   │                           #   placeOf body 固定序提前绑定，按 ordinal join 后合并 body/诊断/generic use/合成；
│   │   │                           #   return 全路径检查 + init 映射赋值（无体产 body/有体前插）；
│   │   │                           #   proxy 声明体按 ProxyBodyState 模板态绑定，降级资格 + CallWildcard；
│   │   │                           #   全局初始化按源声明序绑定，最后追加 lambda/cell 体并执行晚期检查
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
│   │   ├── CellClassFactory.cs     # 统一 cell 存储：逐变量合成 ..cell..稳定摘要 隐藏子类
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
│                                #   （从 stdlib/.intrinsics.rg 绑定固定 ABI 类型身份；
│                                #   Any/Object 默认方法、索引运算符和 wrapper 能力
│                                #   均来自源码声明，不用硬编码成员登记器。
│                                #   IntrinsicDeclarationChecker 核对固定布局；
│                                #   Array 索引 P4b 直发 §13.6；Any.call??? 的
│                                #   默认错误处理为普通源码体，路由由后续降级负责）、
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
│   ├── EmitEnvironment.cs       # 每函数 job 独占 Module/资源池；join 按 ordinal 重放驻留键并切片
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
│   ├── BilInstructions.cs       # 指令基类（Origin(object?) 可空调试附加值 + WriteTo 排版协议）+
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
│   ├── BilModuleMerger.cs       # 多文件模块合并（§17 切片消费侧：符号/函数重复即失败，
│   │                            #   同名同内容资源去重；Metadata 同键去重；
│   │                            #   vm 与 Middleware Gate 共用）
│   ├── BilScalarLiteral.cs      # §19.1 标量资源字面量唯一解码点（转义/整族解析；
│   │                            #   VM 与 Middleware Emit 共用）
│   ├── BilVm.cs                 # VM 入口
│   └── Vm/                      # VM（行为参考实现）：VmContext/VmDispatch（按职责 partial）/
│                                #   VmCoroutine/VmAlarm/VmException/VmHooks/
│                                #   VmTypeSheet/VmTypeOps/VmWrapperDispatch/VmDisposal + Values/ 值模型；
│                                #   Task/Dispatcher/Executor 策略在 stdlib/core/coroutine.rg
├── Middleware/               # BIL → 原生可执行（架构 docs/compiler/middleware/
│                             #   MIDDLEWARE_ARCHITECTURE.md；依赖方向 Middleware → Bil 单向，
│                             #   后端托管依赖：LLVMSharp.Interop + libLLVM 20.1.2；
│                             #   native 运行时还需 libuv/mimalloc，模块 YAML 依赖见 DEVELOPMENT）
│   ├── NativeBuildOptions.cs    # executable/static-library/dynamic-library 请求、C 导出闭包与固定标量 ABI
│   ├── MwContext.cs             # 会话中枢（每模块一个，贯穿各层，逐层挂载产物）
│   ├── MwNotSupportedException.cs # 合法 BIL 超出现阶段实现面的受控失败（CLI 转退出码 2，
│   │                            #   与 CompilerInternalException 严格区分）
│   ├── Gate/BilGate.cs          # MW0 门禁：BilReader 接线 + BilVerifier 全规则
│   │                            #   （BIL §23：类型非法 BIL 必拒；多文件经 BilModuleMerger 合并）
│   ├── Symbols/                 # MW1 驻留符号表（canonical 字符串 intern 为对象，
│   │                            #   引用相等即身份相等；MwSymbol/MwSymbolTable +
│   │                            #   CanonicalSignature）
│   ├── Binding/                 # 实现绑定：ImplBinding + ImplBinder；MW10
│   │                            #   WrapperApplicationIndex / ProxyMatcher /
│   │                            #   SingletonPlanner（查询设施）
│   ├── Mir/                     # MIR 模型 + MirBuilder 瘦驱动（BIL→MIR）+ FlowBuilder
│   │                            #   组合根 + MirLowerDispatchers 唯一 switch + 簇 CRTP
│   │                            #   （ControlFlow/Call/Data/TypeOps/WrapperVisitors +
│   │                            #   MW11a CoroutineVisitors：await/yield 直译）+
│   │                            #   MirReachability + TryExpander.cs（BIL §16.7 try 十步展开）
│   ├── Pipeline/                # IMwStage + MwPipeline：初始 Layout → MirBuild → 语言语义改写
│   │                            #   （可达派发先消费 vtable 计划）；wrapper 在 Accessor 与 RC 之间，
│   │                            #   CoroutineSplit 在 singleton/内建分派/self 归一之后、RC 之前
│   ├── Passes/                  # MIR 改写（IndexOperator / Accessor 内部类隔离；
│   │                            #   MW10 wrapper 烘焙五 pass：FieldProxyBaking /
│   │                            #   MethodProxyBaking / ProxyBaking /
│   │                            #   CallWildcardLowering / SingletonLowering +
│   │                            #   ProxyBakeSupport / ProxyWildcardAbi；BuiltinToStringDispatch /
│   │                            #   WrapperSelfParameter；
│   │                            #   CoroutineSplitPass：async fn → stub + resume +
│   │                            #   frame 合成类型；RcInjection CFG 分析内核，
│   │                            #   非逐指令翻译）
│   ├── Layout/                  # LayoutEngine 瘦驱动 + ClassLayout / ValueTypeLayout /
│   │                            #   VTablePlanner / RefMapBuilder / ConstructedLayout /
│   │                            #   LayoutShells / HiddenStoragePlanner / WrapperAbi +
│   │                            #   MW11a SyntheticTypePlanner（协程 frame 合成类型通道）；
│   │                            #   TypeLayout：canonical → LLVM 唯一映射
│   │                            #   （RUNTIME §2 胖引用 128-bit/16 字节对齐）+ 数组前缀；
│   │                            #   TypeSheetAbi / CallAbi：字段序与调用约定；ConstructedCallCollector
│   │                            #   沿实际调用/storage事实收集闭合形，TypeLayoutPlan 持纯布局决策。非翻译 visitor
│   ├── Emit/                    # LlvmHost + ModuleBuilder 瘦驱动（MIR→LLVM）+
│   │                            #   LlvmEmitEnvironment/Context + LlvmEmitDispatchers +
│   │                            #   簇 CRTP（*Emitter；NativeCall / VirtualCall / New /
│   │                            #   TypeId / Nullable / Wrapper + MW11a
│   │                            #   CoroutineEmitter（Create/FailureLoad/Done/ResumeCall）+ LlvmBitcode /
│   │                            #   ExceptionEmitter / ObjectEmitter；FatValueSlotAbi 生成 Cell/Func 槽适配
│   ├── Toolchain/               # ToolchainResolver（--toolchain → RIGI_LLVM →
│   │                            #   tools/.llvm/<rid> → PATH）+ ExternalProcess
│   │                            #   外部进程封装；LibuvResolver / MimallocResolver 静态库解析，
│   │                            #   ToolchainIdentity / NativeLinkRecord / LinkerThreads 记录身份与线程参数
│   ├── Cache/                  # runtime/object 完整目录原子缓存、稳定 key 文件锁与 whole-program 对象身份
│   ├── Runtime/               # RigiRtBuilder：EmbeddedResource → 实际预处理快照身份 →
│   │                            #   同快照 clang -emit-llvm -c bitcode 编译（unity build）；
│   │                            #   RuntimeFaces 维护普通运行时面，协程专用 helper 不走 String 编组
│   └── Cli/NativeCommand.cs     # native COMMAND（--file/--out/--emit-obj/--emit-ll/
│                                #   --toolchain/--libuv-dir/--mimalloc-dir/--link；库请求另经 NativeBuildOptions）
├── rigi_rt/                  # 运行时 shim 库（C，EmbeddedResource 内嵌，clang 现场编
│                             #   bitcode 合并进模块；架构同上文档）
│   └── shim.c                   # MW1 最小面：rigi_string {data,len} UTF-8 / rigi_print /
│                                #   rigi_print_err / rigi_string_concat / main → rigi_entry
│   └── memtrack.c              # mi_malloc_aligned/mi_free 包装、16B 台账头/TLS 节拍聚合与退出零泄漏
│   └── arc.c/.h                # 原子 RC、值语义/胖引用 ARC、对象头与 region 嵌套
│   └── macrogc.c/.h             # MW12 Bacon-Rajan 收集器（显式 trace 栈）+ 候选账本 +
│                                #   专用 OS collector 线程/fence；local/shared 均原子、同全局账本
│                                #   （pin-before-sub/PURPLE +1）；per-协程 lgc_pass 登记面停用封存，
│                                #   保留终止/shutdown 收干与 promote detach + 子图 promote walker；
│                                #   env：RIGI_RT_GC_THRESHOLD/OFF/TRACE
│   └── gexc.c/.h                # MW12b §25.2 全局异常通道（undisposed 事件队列 +
│                                #   注册表 + atexit flush）
│   └── eh.c/.h                  # MW9a checked-flag 便携异常传输：TLS pending 槽三面
│                                #   （rigi_exc_raise/pending/take）+ 顶层 reporter
│                                #   （rigi_type_name_of/rigi_exc_halt）
│   └── coroutine.h              # MW11c 瘦身：RigiFatRef / RigiResumeCode 共享 ABI
│   └── cohandle.c/.h            # 协程句柄原语（create/resume/destroy + lane + 轮询）
│   └── shell.c/.h               # 3b-β Handle 壳（shellID 注册表 + 归零转移 + 释放消息 +
│                                #   teardown 过户，RUNTIME §28）
│   └── worker.c/.h              # Worker 原语（线程/入队/park/同步 Mutex/定时器/TLS）
│   └── failreg.c                # 未观察失败注册表
├── tools/                    # 工具链脚本（Fetch-Libuv/Mimalloc 在 CI publish 前预取）：
│   ├── Fetch-LlvmToolchain.ps1  # 开发机 LLVM 工具链获取（钉 20.1.2 + SHA256 校验，
│   │                            #   选择性提取 clang/lld/内建头文件 → tools/.llvm/ 缓存，
│   │                            #   gitignored；CI 用 runner 预装 clang/lld 不跑本脚本，
│   │                            #   见 docs/compiler/middleware/TOOLCHAIN_AND_CACHE.md §2）
│   ├── Fetch-Libuv.ps1          # MW11b libuv 获取（钉 v1.52.1 源码 tarball + SHA256
│   │                            #   校验，cmake 本地构建静态库 → tools/.libuv/<rid>/ 缓存，
│   │                            #   gitignored；解析序 --libuv-dir → RIGI_LIBUV →
│   │                            #   tools/.libuv → 编译器旁 .libuv，§2 获取链定稿）
│   ├── Fetch-Mimalloc.ps1       # 钉版包与 SHA256，缓存 tools/.mimalloc/<rid>/；
│   │                            #   --mimalloc-dir → RIGI_MIMALLOC → tools/.mimalloc → 编译器旁 .mimalloc；
│   │                            #   native 缺失编译期拒绝（libuv 同纪律）
│   ├── Watch-Command.ps1        # shell 层看门狗（development.md 测试策略节）：Job Object
│   │                            #   KILL_ON_JOB_CLOSE 灭整树 + stdin 断开 + 超时退出码 124
│   │                            #   + -CleanupOrphans 孤儿清扫；所有可能挂死的测试/产物
│   │                            #   进程运行必须经它带超时拉起
├── stdlib/                   # 编译器自携标准库源（EmbeddedResource 内嵌，见 StdlibSources；
│                             #   与用户源同走 P1–P4）
│   ├── .bootstrap.rg         # 基元 ext 自举（EnumerateInRange）、ComparisonResult、
│   │                            #   lambda 对象模型四家族与 Cell/ReadonlyCell（SYNTAX §15.3）
│   ├── .intrinsics.rg        # 内建类型源码声明、Pair 解构协议根、Any/Object 默认方法体
│   │                            #   与私有 any_hash/any_to_string（SYNTAX §18/§15.3/§3.8）
│   └── core/                    # Console.rg（core.io::Console）+ collections.rg
│                                #   （IEnumerable/IEnumerator 双接口 + RangeI32/
│                                #   RangeEnumeratorI32 + List\<T>/Map\<K, V\> 最小集合面）
│                                #   + coroutine.rg（core.coroutine 类型面——Task/
│                                #   Task\<TReturn>/Executor 家族/PollingAlarm/EventAlarm/
│                                #   CoroutineLocal\<TValue> 具体 shared class + sleep，
│                                #   SYNTAX §15.3）+ time.rg（core.time）+ disposable.rg（core.IDisposable，
│                                #   §6.2）+ exceptions.rg（RuntimeException/IOException/
│                                #   CastException/NoSuchMethodException/DividedByZeroException/
│                                #   OutOfBoundException/IllegalStateException 等异常子类，§8.1）
│                                #   + global_exceptions.rg（core.UndisposedResourceException +
│                                #   GlobalExceptionHandler 全局异常通道，§25.2/MW12b）
│                                #   + serialization.rg（core.serialization：@Serializable/
│                                #   @SerializationBase/@Temporary/@Terminal + Parcel，SYNTAX §20）
│                                #   + place.rg（Place 身份与 Handle 能力，unsafe 边界）
│                                #   + atomic.rg（Atomic + AtomicStruct，复用 Mutex）
│                                #   + atomic_collections.rg（安全异步 AtomicArray/List/Map 与独立快照）
│                                #   + messaging.rg（core.messaging：纯 Rigi MessageQueue + 安全 AtomicList/Mutex +
│                                #   Reader/Receiver/Messenger 高层 API，RUNTIME §27）
├── Tests/                    # 全部测试 provider/工具，只编入独立 TUnit 项目
│   ├── AstDescribe.cs           # 统一 AST 描述器（全部套件共用）
│   ├── BoundDescribe.cs         # 统一 BoundTree 描述器（P3 套件共用，仿 AstDescribe）
│   ├── LoweredDescribe.cs       # 统一 LoweredTree 描述器（P4a 套件共用，仿 BoundDescribe）
│   ├── BilTestHarness.cs        # BIL 测试基建：EmitBilUnit 全管线驱动 +
│   │                            #   CheckBilValid/CheckBilInvalid 验证器断言 +
│   │                            #   res 重编号形状黄金 CheckFnShape/CheckResShape
│   ├── CompilerTestTools.cs     # 无状态解析、AST 定位与黄金文本工具
│   ├── CaseAssertions.cs        # 每 worker AsyncLocal scope 记录真实断言（含 CheckSemanticError）
│   ├── TestSuiteCatalog.cs      # 纯 provider 数据及套件编号/菜单兼容，无整套执行委托
│   ├── StaticTestProviders.cs   # 显式方法组到 Spec 的发现映射，兼容 NativeAOT
│   ├── CaseCatalog.cs           # 全 provider/种子批次稳定 ID、真实粒度与动作执行
│   ├── CaseSelection.cs         # 编号/标签/区间兼容选择与资源/截止纯映射
│   ├── CaseWorkerClient.cs      # 同测试宿主隔离 worker、请求临时根、灭树与排空
│   ├── CaseResultJournal.cs     # 逐 ID 结果与内部断言证据，供全量完整性校验
│   ├── TestRootParserLayer.cs   # 独立 Layer 测试垫底层（只接受 EOF）
│   ├── TokenDispositionTests.cs # Token 流转协议测试（四种组合）
│   ├── ASTIntegrityValidatorTests.cs # Validator 直调测试（合法树 + 结构破坏拒绝）
│   ├── LexerFuzzTests.cs        # Lexer fuzz 测试（Slash/EOF/注释 + 6000 随机用例）
│   ├── BilVmTests.Serialization.cs / BilVmTests.Messaging.cs  # MW11d 序列化与
│   │                            #   消息行为电池（BilVm 套件 partial；消息含 §28
│   │                            #   capability/EOS/broadcast/Receiver 电池）
│   ├── BinderTests.Mw11d.cs     # MW11d 修饰器/with 约束 P3 正反例
│   └── e2e/rigi/mw11dd_*.rg     # Messenger/Reader 负例语料（E2e 套件）
└── docs/                     # 语言/BIL/运行时规范、实现专题与历史档案（legacy 不作为当前实现依据）
```

### 3.1 关键文件

同一类型按职责使用 partial 文件组织，主文件保留入口、共享状态及有顺序依赖的初始化。
`Bil/Vm/VmDispatch.*.cs` 分别承载 Task/await、Worker、协程、NativeRc、时钟事件、
标准输入及 FileSystem 原语；`VmContext.*.cs` 分别承载符号成员查询、初始化、异常、
销毁检查、运算符和泛型匹配。它们仍共享同一宿主实例、句柄空间与锁，文件边界不建立新的运行时实例。
`Middleware/Passes/CoroutineSplitPass.*.cs` 按 Runtime/Taint/Liveness/Planning、
resume 与各挂起点发射、Completion/ColdTasks 分文件，`Run` 的阶段顺序统一留在主文件。
`Semantic/Binding/Visitors/PathVisitors.cs` 保留 visitor 与路径绑定入口，
Values/Calls/Fields/Segments/Indexing 文件承载相应路径设施。

`MiddlewareTests.*.cs` 与 `BilVmTests.*.cs` 按被测阶段或行为组织，主文件保留注册目录。
`NativeE2ETests.Cases.*.cs` 提供有序用例分组，主文件显式按原顺序组装目录；
初始化依赖的共享表仍在主文件内先声明。Execution、Fixtures 与指令专项文件共用原驱动，
拆文件不得改变用例标签、编号、门控或注册副作用顺序。

| 文件 | 用途 | 重要性 |
|------|------|--------|
| `docs/SYNTAX.md` | **语言语法规范（最权威）**（索引文档，正文在同名子目录） | ⭐⭐⭐ 有歧义时以此为准，不要猜语法 |
| `docs/RUNTIME.md` | 运行时模型与类型系统（索引文档，正文在同名子目录） | ⭐⭐⭐ |
| `docs/BIL_STANDARD.md` | BIL 中间语言规范（索引文档，正文在同名子目录） | ⭐⭐ |
| `docs/legacy/PARSER_ROADMAP.md` / `docs/legacy/PROGRESS_REPORT.md` | Parser 路线图与进度（历史档案，不再更新） | ⭐⭐ |
| `docs/compiler/syntax/README.md` | Lexer、Parser、AST 与表达式架构专题 | ⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md` | 语义/Lowering 分专题索引，保留原 § 编号 | ⭐⭐⭐ |
| `docs/legacy/SEMANTIC_ROADMAP.md` | 语义分析路线图（历史档案，不再更新） | ⭐⭐ |
| `docs/compiler/vm/BIL_VM_DESIGN.md` | VM 值/派发、执行/生命周期、指令/Hook与验证专题索引 | ⭐⭐⭐ |
| `Modules/ModuleCommand.cs` / `Modules/ModuleBuildService.cs` | module CLI 与依赖 DAG 构建 | ⭐⭐ |
| `Modules/ModuleInterface.cs` / `Modules/ModuleInterfaceImporter.cs` | 无 AST 的版本化接口导出/导入 | ⭐⭐ |
| `Modules/ModuleProductPublication.cs` / `Modules/ModuleBundle.cs` | 请求级产品事务与 ZIP 安装 | ⭐⭐ |
| `Parser/Parser.cs` | 层栈式 Parser 的核心协议 | ⭐⭐⭐ |
| `Lexer/Tokens.cs` / `Parser/Keywords.cs` / `AST/ASTNode.cs` | Token/关键字/AST 基类等核心数据结构 | ⭐⭐⭐ |

---

Place/Handle 的编译链入口为 `Semantic/Binding/Visitors/PlaceOfVisitor.cs`
与 `Semantic/Binding/UnsafeGates.cs`；稳定存储复用既有 Cell 工厂。
`Lowering/Rewriters/PlaceOfRewriter.cs` 构造 Place；Handle/MutableHandle
保留普通具化类身份，通过标准库私有 ObjectHandleStorage 访问 Rigi 存储；
`Bil/BilVerifier.Unsafe.cs` 验证权限及保留构造入口。原生侧以计数壳管理
`.handle`（capability）的目标存活与释放：归零转移经属主通道、属主终止
过户、GC 代理边（RUNTIME §28），不建立独立 MQ 注册表。
该 Rigi 对象能力与 NativeRcHandle 的 native 互操作生命周期设施无关。


### 3.2 独立模块、接口与发布

`Modules/ModuleConfiguration`、`ModulePaths`、`ModuleResolver` 固定配置、路径与依赖身份；`ModuleSources`、`ModuleBuildContext` 决定各模块的源码、profile 和子环境。`ModuleBuildService` 在依赖完成后取得本模块缓存锁，仅编译自有源码，消费依赖 API/BIL。

`ModuleBuildIdentity`、`ModuleArtifactCache`、`ModuleArtifactEnvelope` 维护内容身份与 receipt；`ModuleSymbolRegistry`、`ModuleInterfaceImporter`、`ModuleLateHelpers`、`ModuleApplicationLinker` 维护声明 ID、跨模块 helper 和最终链接；`ModuleHooks`、`ModuleProductPublication`、`ModuleNativePublication`、`ModuleBundle` 完成 hook、Native 产品、资源事务与可复现包。模块 CLI 由 `Core/CommandLine` 注册 `ModuleCommand`，具体操作、路径限制与事务边界见 [DEVELOPMENT.md](../../DEVELOPMENT.md) 的模块章节。

## 4. 核心设计决策（改动代码前必须理解）

### 4.1 ⚠️ Rigi 没有运算符优先级

```rigi
var result = 1 + 2 * 3       // ❌ 编译错误：歧义
var result = 1 + (2 * 3)     // ✅ 必须加括号
```

对 Parser 的影响：不需要优先级表；遇到未括号化的连续运算符必须报错。实现表达式相关功能时不要引入优先级概念。

### 4.2 ⚠️ `rich` / `shared` 是类型**声明**修饰符，不是类型引用修饰符

- `rich`：**仅用于 struct / enum struct / wrapper**（class 不能用）。允许值类型持有引用，但仍是值语义、unique ownership（类似 `unique_ptr`，**不是** `shared_ptr`）。wrapper 默认非 rich，按需显式声明 rich；特殊宿主回指 self 不属于普通字段，不受 rich 持有规则限制。
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

完整驱动、Consume/Replay、施工目标与层分工统一维护在 [Parser 架构](../compiler/syntax/parser.md)；表达式起点、无优先级开关、路径与运算符组合见 [表达式 Parser](../compiler/syntax/expressions.md)。

### 4.7 ⚠️ Parser 架构规则（必须遵守）

[施工规则与上下文传递](../compiler/syntax/parser.md) 定义不回传 AST、明确施工目标、EOF、注释统一跳过、独立层测试及 allowBareReturn 传递。新增功能遵循该协议。

[AST 归属、Span、完整性与 JSONL](../compiler/syntax/ast.md) 定义一次性 Parent/Attach、ExpressionRoot、禁止共享与环、反射标注审计、Required、Span 回填和往返；节点与载体清单也集中于该页。

### 4.5 ⚠️ 简洁优先：新增代码前必须自问的三个问题

三问、已有层的复用范例及新建 Layer 的职责边界集中在 [Parser 复用与增量设计](../compiler/syntax/parser.md)。通用开发工作流见 [DEVELOPMENT.md](../../DEVELOPMENT.md)。

### 4.6 Lexer 的特点

字符驱动、词法层、Token 契约、斜杠/引号分流、字符标量、多行文本解码、插值帧、EOF 与位置计量统一维护在 [Lexer 架构](../compiler/syntax/lexer.md)；源码范围细节见 [AST](../compiler/syntax/ast.md)。


### 4.8 测试资源与进程边界

`Core/ResourceBudget` 提供不可变 `ResourceRequest`/`ResourceLease` 和严格 FIFO 加权队列。队头无法满足时预留资源，避免大请求被后来的小请求饿死；超过容量立即拒绝，排队取消移除节点并继续推进，lease 幂等释放。CPU 容量取 .NET 有效值、Linux affinity/cpuset/可见祖先 quota 的较小值，内存取 GC 与可见 cgroup 限制的较小值后给父宿主留余量。配置只限制资源授予，不声称限制 OS 总线程数。

独立 `Tests/TUnit` 宿主是唯一全量执行入口。`TestSuiteCatalog`/`StaticTestProviders` 只提供编号、Spec 与显式动作，`CaseCatalog` 从同一 provider 建立全量框架目录，TUnit 为普通方法组或种子批次生成发现行。`Core/TestHostForwarder` 仅把 `rigic test` 兼容请求转交宿主；`CaseSelection` 只映射编号/标签/区间，不保留旧套件执行循环或整套退出码 worker。每个发现行进入同宿主的隔离 worker，禁止嵌套派生；`CaseWorkers` 仅供协议契约同时提交隔离请求。`CaseAssertions` 每请求 AsyncLocal scope 记录实际断言，平台/fixture 不可用由 `RecordSkip` 进入 typed Skip，部分可用动作保留真实断言和跳过诊断。

`RIGI_TEST_RIGIC` 只定位被测编译器 CLI，`RIGI_TEST_HOST` 定位测试 EXE/DLL 与隔离 worker；AOT worker 默认使用当前测试进程，不能把测试宿主当编译器。e2e/native 语料由测试项目显式复制，压力源与 C fixture 头文件随测试输出同行；发布验收关闭源码回退。全量 `--compat --all` 自动生成新鲜 TRX，精确核对全部 provider 完成 ID（含 Skip）、TRX 唯一完成行及已声明框架契约身份，包含闭合泛型实际通过；不能仅用一个 pilot 数量门槛替代完整性。本机每个平台串行跑一次完整 AOT 全量，不再叠加旧全量或额外 Generic 运行。

CI 使用 `CiShardSelection` 的独立 `--ci-shard index count` 入口，各 suite 按稳定 ID 的 Ordinal 顺序轮转给 16 个独立 runner，不拆单动作或 fuzz seed batch，`CaseCatalog.All` 始终完整。`CiShardEvidence` 在执行前导出完整 inventory，并将 SHA/RID/run/attempt、目录摘要和预算写入 manifest/journal；仅片 0 执行全部框架契约。MTP 重启 testhost 时传递小分片上下文，父入口读取本次 child 的新鲜结构化 journal，不能用自己的空字典或旧文件判定。`Verify-CiShards.ps1` 汇总每 RID 的精确全集与互斥性，核对 TRX 身份/状态及契约，缺片或取消/失败不能通过。同一 runner 不并发启动多个宿主；CI 每片外层 330 分钟，本机完整外层 24 小时，内部截止和既有种子预算不变。协议与命令见 [DEVELOPMENT.md](../../DEVELOPMENT.md)。

完整源码 E2e、多轮 BilVmStress 与 NativeE2E 各输入独立执行。默认截止策略由选择和 ID 解码共用，直接 case 客户端复用同一策略；显式调用方覆盖优先。NativeE2E 的进程内 whole-program O2 使用有限重型窗口，JSON 写侧冷编译的独立内存与截止 profile 保留；其余普通方法组保持轻型截止，共享状态的单方法仍保留完整生命周期。已删除的旧完整整套 ID 不再有独立窗口，框架适配不以固定短截止截断排队或重型 worker。完成、失败与跳过由 TUnit 和逐 ID journal 汇总，进度输出不能替代最终证据。

Semantics fuzz 稀疏批次仍按固定种子从全局 0 推进生成前缀，只执行所选序号；Stress 按原全局 i 调用 `Generate(i)`。确定性检查仍按原 i 模 60，默认预算、CI 预算和慢门控不变。LexerFuzz 先按固定种子生成全部输入，再按稳定全局序号以最多 100 例小批进入隔离 worker；目录与执行共用 provider，不能改随机序列或原预算。native/VM 并发 profile 至少保留四 Compute Worker；可在较少 CPU slots 上独占共享，GC/IO 线程不计入 Compute 数。

`tools/PerfBaseline/ProcessIsolation.cs` 是性能工具、case client 与同测试宿主 worker 的共用受管启动实现。Linux 独立 setsid session/group，负 pgid 清理后代；Windows 使用 `CreateProcessW` 与 STARTUPINFOEX 的 Job/stdio 白名单，旧系统回退 CREATE_SUSPENDED→AssignJob→ResumeThread。只有 child 端 stdio 可继承，属性值活到 DeleteAttributeList，挂起期间固定 root 进程句柄，正常结束也杀剩余后代。根等待、管道排空与请求临时根清理按顺序执行。Linux 无法约束主动 setsid 逃组，Windows 实测由可用 Windows/CI 环境承担，Linux 构建不能替代该验证。

`RIGI_LLD_THREADS` 显式设置时在 1..254 严格校验，LLVM 20 ELF/COFF 均追加 `-Wl,--threads=N` 并进入真实 link record；不改变 O2 object key，每次请求仍 relink。未设置时沿用 lld 默认。COMP-003 的完整 LLVM lease 与缓存锁顺序保持不变，同进程 LLVM exclusive 不扩展成跨进程静态共享锁。

### 4.9 编译器内部并行边界

前端、P1 声明事实、P2 字段/方法签名、P3 独立函数体、P4a 函数重写与 P4b
函数发射共用 `Core/CompilerJobs` 的 indexed 队列和阶段 join。符号驻留、
P2 依赖检查、P3 预合成/global cell 提升/全局初始化、P4b 声明与资源回放
保持确定的串行边界；详细身份、delta 和资源引用契约见
[语义架构 §1.1](../compiler/semantic/SEMANTIC_ARCHITECTURE.md#11-有界并行与发布屏障)。
已获得外层测试 lease 的 child 通过 `RIGI_RESOURCE_LEASE_CPU_SLOTS` /
`RIGI_RESOURCE_LEASE_MEMORY_MIB` 标记内部上限，清除父进程测试内存配置，
不再次扣除外层父宿主预留；GC/实际 cgroup/affinity 上限仍参与取最小值。
共享预算仅属于当前进程，不能将父 lease 与 child 内部阶段 lease 混同。
DeclarationResolver（P2）/Binder/BilEmitter/Lowerer/SmartCast/StdlibSources
中的真实 provider 方法组若包含多个同时存活的命名局部图，其内存 profile
依据该生命周期，而非单个新编译的工作集。兼容定向组映射为这些已发现动作，
不回退完整整套 ID。单 E2e 编译仍使用自己的 profile；二者都经同一共享预算
授予，child GC 上限仍是所得 lease 的一半。

默认入口已按真实方法组枚举调度，不应把整个套件称为不可枚举；默认组与单 E2e 编译使用各自的 profile。
