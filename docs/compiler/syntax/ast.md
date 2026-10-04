# AST 节点、归属与源码范围

AST 是语法事实模型；名称解析、类型检查和语义上色由 [语义阶段](../semantic/SEMANTIC_ARCHITECTURE.md) 完成。Parser 施工协议见 [parser.md](parser.md)。

## 位置信息

- **CharPosition**: 字符位置（行号、列号、偏移量），`Lexer/Lexer.cs`。
  行号 1 起始；列号 1 起始（换行算当前行最后一列）；`offset` 为 0 起始字符索引
- **CharRange**: 字符范围（起始位置、结束位置、源文件名），`Parser/Parser.cs`。
  `sourceName` 是源文件名的唯一来源（`CharPosition` 不再携带）

范围统一为左闭右开 `[Start, End)`。字符索引以 Lexer 归一后的 UTF-16 码元流为准，行列也按这个流计量；UTF-16 代理对占两个 offset，而 `char` 字面量的值只占一个 Unicode 标量。归一细节见 [Lexer](lexer.md)。

## AST 节点类型（语法分析器输出）

**ASTNode**: 抽象基类（`Parent` 指针、`Span`），`AST/ASTNode.cs`。
子节点容器不是基类共有字段：各节点以语义明确的 [ChildAstNode] 字段自持
（`RootASTNode.Declarations`、`CodeBlockASTNode.Statements`、类型节点 `Members`）。
`Span`（`CharRange?`）是节点的源码范围：层目标由 Parser 主循环按 token 流经
`ISpanReceiver.ReceiveSpan` 回填，层内自建节点由所在层显式设置，
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
节点类型一律用 CLR 类型判断（原 ASTNodeType 枚举已删除）；装子节点的字段/属性以
`[ChildAstNode]` 标注、父指针以 `[ParentAstNode]` 标注，携带 ASTNode 的非节点对象
（如 import 列表项 ImportItem struct）以 `[AstCarrier]` 标注——ASTVisitor（`AST/ASTVisitor.cs`）
以统一实现反射遍历（遍历骨架与子节点枚举均为 virtual 可重载），
ASTIntegrityValidator 校验父子指针一致性与 Span 合法性，
并审计「装 ASTNode 却未标注」的成员。

### 根与符号（`AST/ASTNode.cs` / `AST/SymbolNodes.cs` / `AST/ImportNodes.cs`）

- **RootASTNode**: 根节点（顶层条目挂 `Declarations`）
- **SymbolASTNode**: 符号节点（`symbol: Symbol`）
- **ImportASTNode**: import 声明（列表项为 `[AstCarrier]` struct **ImportItem**）
- 符号结构：**Symbol**（`elements: SymbolElementSet`）→ **SymbolElement**（`name` + `generics: List<TypeReferenceASTNode>`）。泛型实参是完整类型引用（含可空），Parent 指向持有符号的 SymbolASTNode；AstStructureReflection 对 SymbolASTNode 有专门枚举分支，使这些节点纳入遍历与完整性校验。
- **NamespaceDeclarationASTNode**: Name（SymbolASTNode）；namespace 的唯一性与首部位置约束留给语义阶段。

### 字面量（`AST/LiteralNodes.cs`）

- **IntLiteralASTNode**（Value（decimal，128 位十进制，可精确覆盖 u64 全范围）、IntType、Base；IntType 枚举：I32/I64/I16/I8/U32/U64/U16/U8；
  Base 为 LiteralIntBase 枚举（替代 IsHex 布尔）：Decimal/Hex/Binary/Octal，对应 0x/0b/0o 前缀；
  负号折叠（SYNTAX §3.3）：一元 `-` 直接作用于整数字面量时 Value 即负值（不产生 Unary 节点），
  解析期范围检查按目标类型完整有符号区间（`NumericLiteral.TryParseInt` 的 negative 语境））
- **FloatLiteralASTNode**（Value、IsFloat）
- **StringLiteralASTNode**（Value、HasInterpolation、InterpolationParts?）；无插值时 Value 为完整解码文本，有插值时 HasInterpolation=true、Value 为空串，按源码顺序使用 InterpolationParts。
- **StringInterpolationPart** 是 `[AstCarrier]` class，Text?（LiteralExpressionASTNode）与 Expression?（ExpressionRootASTNode）互斥；文本段用 LiteralExpression 包装 StringLiteral，表达式段用稳定 Root，节点 Parent 均以宿主 StringLiteral 为 owner，carrier 自身不是 ASTNode。空解码文本段跳过；段内表达式直接消费 Lexer 帧流，保留原源文件范围，不通过第二次分词映射位置。
- **CharLiteralASTNode**（Value: uint；SYNTAX §3.3：单引号内恰好一个字符或一个转义序列）；值为 U+0000–U+10FFFF 的 Unicode 标量，排除 U+D800–U+DFFF 代理区。
- **BoolLiteralASTNode**、**NullLiteralASTNode**

### 类型引用（`AST/TypeNodes.cs`）

- **TypeReferenceASTNode**: TypeSymbol（SymbolASTNode）、IsNullable
- 类型上的泛型实参挂在 `TypeSymbol.symbol.elements[*].generics`
- `rich` / `shared` 是类型声明修饰符，不是类型引用的字段或前缀。泛型统一以 `\<` 开启，以 `>` 关闭。
- `TypeReferenceASTNode.DeepClone(newParent)` 递归复制符号元素、完整泛型实参子树与 Span；每份克隆的 Parent 按新宿主一次确定。`Symbol.DeepClone(owner)` / `SymbolElement.DeepClone(owner)` 同样递归复制，避免 import 共享前缀展开后的原地规范化互相污染。

### 声明（`AST/DeclarationNodes.cs`）

- **VariableDeclarationASTNode**: Modifiers、IsConst、Name、DestructureNames?、TypeAnnotation?、Getter?、Setter?、Initializer?、Annotations（局部变量/字段/全局变量同一节点）。普通声明用 Name，解构声明用 DestructureNames 列表且 Name 为空串，两形态互斥。
- **CallableDeclarationASTNode**: Modifiers、Kind（CallableKind: Func/Operator/Init）、Name、GenericParameters?、Parameters、ReturnType?、Body?（全局函数/方法/静态方法/运算符/构造函数同一节点）
- **ClassDeclarationASTNode**: Modifiers、ClassName、GenericParameters?、BaseClass?、Interfaces、LikeTarget?（`like` 委托的目标字段；成员挂 `Members`）
- **InterfaceDeclarationASTNode**: Modifiers、InterfaceName、GenericParameters?、BaseInterfaces
- **StructDeclarationASTNode**: Modifiers、StructName、GenericParameters?、BaseStruct?、Interfaces
- **EnumStructDeclarationASTNode**: Modifiers、EnumName、GenericParameters?、Cases（`[]` case 列表）
- **EnumCaseASTNode**: CaseName、Arguments、DiscriminantValue?
- **WrapperDeclarationASTNode**: Modifiers、WrapperName、GenericParameters?（`@WrapperTarget` 类型标识与 `.proxy.*` 代理成员）
- **wrapper 挂载接口**：`IWrapperAttachable`（`Annotations` 属性）+ `IEntity/IMethod/IValueWrapperAttachable` 三个分类标记接口；Variable→Value、Callable→Method、5 个类型节点（含 enum struct）→Entity；5 个类型节点的成员容器均为 `Members`
- **GenericParameterListASTNode**: Parameters、Constraints
- **GenericParameterASTNode**: Name、Variance（GenericVariance: None/Out/In）、IsVariadic、IsNamedVariadic
- **GenericConstraintASTNode**: Target、Kind（GenericConstraintKind: Extends/Supers/With）、Bound
- **ParameterListASTNode**: Parameters
- **ParameterASTNode**: Name、Type、IsVariadic、IsNamedVariadic、DefaultValue?
- **PropertyAccessorASTNode**: Modifiers、Kind（AccessorKind: Get/Set）、HasBackingField、Body?；省略参数或 `(value: _)` 要求 backing field，`(_: _)` 是计算属性，Body=null 表示生成实现。get/set 的 HasBackingField 必须一致，由 Parser 校验。
- **AnnotationASTNode**: Name（SymbolASTNode，可为路径）、HasArguments、Arguments；区分 `@Logged` 与 `@Timed()`，内建 wrapper 标记和用户 wrapper 共用语法。声明创建前 parent=null，之后一次性 AttachTo 到声明，不重挂 Parent。
- wrapper 类别接口只表达挂载能力，实际合法性由语义阶段检查。Callable 与 Lambda 均是 Method attachable；五类类型节点是 Entity attachable，Variable 是 Value attachable。

### 表达式（`AST/ExpressionNodes.cs`）

基类 **ExpressionASTNode**，子类：

| 节点 | 关键字段 | 说明 |
|------|----------|------|
| **BinaryExpressionASTNode** | Left、Operator、Right | 二元运算 |
| **CompoundAssignmentExpressionASTNode** | Target、Operator（基础运算符）、Value | 复合赋值（§13.2，+= -= *= /= %= <<= >>= >>>= &= |= ^= 共 11 个）；本身可用于表达式位置 |
| **UnaryExpressionASTNode** | Operator、Operand、IsPrefix | 一元运算（含 await） |
| **LiteralExpressionASTNode** | Literal | 字面量包装（AttachLiteral 一次性附加，使字面量成为表达式） |
| **PathExpressionASTNode** | Head、Segments | 路径表达式（§1.4）：符号/调用/索引/成员（含 `?.`）/wrapper（`:`）后缀链统一为单一路径节点 |
| **PathHeadASTNode** | Name? / Expression?（互斥）、GenericArguments、Suffixes | 路径首段：符号头或表达式底座；非 Expression 子类 |
| **PathSegmentASTNode** | Connector（Dot/SafeDot/Colon）、Name、GenericArguments、Suffixes | 路径段；非 Expression 子类 |
| **PathSuffixASTNode** | Kind（Call/Index）、Arguments | 调用 `()` / 索引 `[]` 后缀；非 Expression 子类 |
| **GroupExpressionASTNode** | InnerExpression | 括号分组 |
| **NewExpressionASTNode** | Type、Arguments | new 构造 |
| **ArgumentASTNode** | Name?、Value | 调用/索引/构造实参（可具名）；非 Expression 子类 |
| **LambdaExpressionASTNode** | IsAsync、Annotations、Parameters、ReturnType?、Label?、Body? / BlockBody?（互斥） | lambda（体双形态：单表达式 Body / 多语句块 BlockBody，块内禁裸 return；ReturnType=null 为 void） |
| **IfExpressionASTNode** | Condition、ThenBody、ElseBody、Label? | if 表达式（强制 else，分支体为代码块，取值 return@_ / return@标签） |
| **SwitchExpressionASTNode** | Selector、Cases、DefaultBody、Label? | switch 表达式（强制 default，DefaultBody 为 CodeBlockASTNode?） |
| **SwitchCaseASTNode** | Pattern、Body | case 分支（Body 为代码块）；非 Expression 子类 |
| **TypeOfExpressionASTNode** | Operand | typeOf(expr) |
| **PlaceOfExpressionASTNode** | Operand | 专用前缀 `placeOf operand`；稳定存储与对象身份由 Binder 区分 |
| **CastExpressionASTNode** | Object、TargetType、IsSafe | as / as? 转换 |
| **TypeCheckExpressionASTNode** | Object、Operator、TargetType? / TargetCase?（互斥） | is / supers / with 检查；is 右侧可为 enum case（§12.3） |
| **EnumCaseExpressionASTNode** | CaseName | 前导点 enum case 引用（`.Success`） |
| **SeqBlockExpressionASTNode** | IsVolatile、IsUnsafe、UsingBindings、Label?、Body | seq 块（语句 + 表达式双形态，匿名默认标签 `_`；IsUnsafe 表达词法 unsafe 上下文） |

### 语句（`AST/StatementNodes.cs`）

| 节点 | 关键字段 | 说明 |
|------|----------|------|
| **CodeBlockASTNode** | Statements | `{ }` 代码块 |
| **IfStatementASTNode** | Condition、ThenBlock、ElseBranch? | if 语句（ElseBranch 为块或嵌套 if） |
| **SwitchStatementASTNode** | Selector、Cases、DefaultBody | switch 语句（分支体为代码块，强制 default；结果值被丢弃） |
| **LoopStatementASTNode** | Kind（LoopKind）、VariableName?、Iterable?、RangeTo?、Condition?、Label?、Body | for-each/范围/while/do-while（范围 = Iterable 起点 + RangeTo 终点） |
| **ReturnStatementASTNode** | Label?、Value? | return / return@label |
| **LoopControlStatementASTNode** | IsBreak、Label? | break / continue[@label] |
| **ExpressionStatementASTNode** | Expression、AssignValue? | 表达式开头的语句：纯表达式语句或赋值语句（合并，双 Root 槽创建时归属即定） |
| **TryCatchFinallyStatementASTNode** | TryBlock、CatchClauses、FinallyParameter?、FinallyBlock? | 异常处理 |
| **CatchClauseASTNode** | VariableName?、ExceptionType、Body | catch 子句（`_` 丢弃异常变量） |
| **UsingBindingASTNode** | IsConst、VariableName、Type?、Initializer | seq using 资源绑定 |
| **ThrowStatementASTNode** | Exception | throw 语句 |
| **YieldStatementASTNode** | Alarm? | yield / yield alarm |

## 表达式施工过程（ExpressionRootASTNode）

每个「语法上要求出现表达式的位置」由 `ExpressionRootASTNode` 作为稳定挂载点：

- 父层创建 Root（必需位置随宿主节点构造创建；可选位置出现时再创建，否则保持 null）；
- `ExpressionParserLayer` 构造时接收 Root 作为唯一最终挂载位置；
- 表达式层先在内部构造**未挂载**的完整子树（`currentExpression`）——
  后缀链与一元/二元包装通过「新包装节点.Root.Attach(旧子树)」逐层外卷；
- 表达式完整时执行且仅执行一次 `target.Attach(currentExpression)`，随后立即 Pop。

Root 的不变量：`Attach` 自身强制一次性 Attach、禁止替换、禁止附加已有父节点的表达式；`ASTIntegrityValidator` 在 Parse 成功后自动验证 Root 已填充、Root 中表达式的 Parent 必须指向 Root、节点无共享、Parent 链无环。`ExpressionRootASTNode` 是正式语法节点，在语义和 Lowering 中作为透明容器；存在但未填充的 Root 表示未完成施工，成功解析后拒绝，缺失的可选表达式用 null Root。

## 完整性、Span 与往返

解析成功后 `ASTIntegrityValidator` 自动验证 AST 不变量：遍历只走
`[ChildAstNode]` 标注的成员（`[AstCarrier]` 对象深入其公共字段），校验每个
子节点的 Parent 指向持有者，另含 Root 均已填充、节点无共享、Parent 链无环、
switch default 规则、**每节点 Span 合法（非空、sourceName 非空、
End 不早于 Start）**、**类型审计（装 ASTNode 的字段/自动属性必须带
[ChildAstNode]/[ParentAstNode] 标注）**；失败抛 `CompilerInternalException`（内部编译器错误，
与用户语法错误区分）。
「归属后知」的场景必须用创建时归属即定的结构承载
（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）
或延迟一次性 AttachTo（注解），**禁止任何形式的 Parent 重挂**。

**Span 施工**：每个 AST 节点都有源码范围 `ASTNode.Span`（`CharRange?`）。
约定：层目标节点由 Parser 主循环按 token 流计算 span，层弹出时经
`ISpanReceiver.ReceiveSpan` 回填（通常用 `target.Span ??= span` 只填空）——
新 Layer 若有施工目标，应实现 `ISpanReceiver`；层内自建节点由所在层显式设置
（创建记 Start，完成经 `ParserLayerContext.GetPreviousLocation()` 封 End）；
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
**Span 统一为左闭右开 `[Start, End)`**：Start 指向首个字符，
End 指向最后一个字符的下一位置（token 与 AST 节点一致；EOF 为零宽范围）；
语句/声明的 span 不拖尾换行符到下一行（终态层可 Replay 换行，也可 Consume 弹出，但主循环计算 Span 时排除换行）。
Validator 与 AstJsonlSerializer 的 [ChildAstNode] 反射统一走
`AST/ASTVisitor.cs` 的 `AstStructureReflection`，禁止再写第三份反射下钻。

**JSONL 往返**：`AstJsonlSerializer`（v2：carrier 记录化、字段名键控）
与 `AstJsonlDeserializer`（完整反序列化，产物强制过 Validator）构成往返；
消费方按字段名取值，不依赖字段顺序。

### 驱动计算 Span 的边界

`Parser.LayerFrame.FirstRange` 记录首个分发给本层的 Token。`ISpanReceiver` 收到从 FirstRange.Start 到本层最后所属 Token.End 的范围：Consume 弹出且 Token 不是换行时用当前 End；Replay 弹出或 Consume 换行弹出时用 `GetPreviousLocation().End`，注释跳过不更新该位置。未消费任何 Token 或 End 早于 Start 时钳为起点零宽范围。`RootParserLayer` 不弹栈，Root span 由 Parser 单独取原 token 列表首 Start 到末 End；手工空列表使用 `<empty>` 零宽范围。

表达式层不用 ISpanReceiver，显式“创建记 Start、包装沿用内层 Start、结构完成封 End”；路径段和后缀也各有范围。多数具体层的已有显式 Span 优先于层的 `??=` 回填，保证 if 等层预消费关键字仍能包含开头。例外是 `DeclarationParserLayer.ReceiveSpan`：它直接覆盖子声明范围，使声明整体包含前置注解与修饰符；不能把“只填空”当作所有层的通用硬规则。EOF 的范围是零宽，正常 Token/AST 采用同一个左闭右开约定。

### 遍历与校验的完整边界

[ASTVisitor.cs](../../../AST/ASTVisitor.cs) 的 `VisitNode` 与 `EnumerateChildren` 均可重载；不调用默认 VisitNode 可剪枝。`AstStructureReflection` 枚举 ChildAstNode 字段/属性、节点集合以及 AstCarrier 的公共字段；carrier 内节点 Parent 指向 AST 宿主而非 carrier。SymbolASTNode 的泛型实参以专门分支纳入，不能视作普通纯标量。

[ASTIntegrityValidator.cs](../../../AST/ASTIntegrityValidator.cs) 除上述结构规则，还检查 `[ChildAstNode(Required=true)]` 子节点非空（如字面量包装和 catch 的 ExceptionType）。类型审计沿基类链覆盖私有字段、自动属性，拒绝装着 AST 却未标注、标注在非 AST 数据上以及 carrier 中承载 AST 的属性（carrier 只下钻公共字段）。每种类型在一次校验中审计一次。声明类型完备性由语义分析检查，不能把合法的 init 映射空 Type 当作 AST 完整性错误。

### JSONL 记录与重建

[AstJsonlSerializer.cs](../../../AST/AstJsonlSerializer.cs) 深度优先输出 v2，每节点一行，字段包括 id（从 1 起）、parent（根为 null）、via（挂载成员路径，如 Left/Statements[2]）、type、span、fields。Span 字段为 source/startLine/startCol/startOffset/endLine/endCol/endOffset。carrier 独立成行，span=null，保留 ImportItem.importAll 等标量；其携带节点的记录 parent 指向 carrier 行，而节点的 AST Parent 仍指向宿主。子单元枚举复用 EnumerateChildUnits。

fields 按声明类型保存 primitive/string/decimal/enum、Nullable 包装和 List<string>；Symbol 为点分串。先过滤类型再读取值，排除 Parent、ChildAstNode 与索引器，避免读取尚未填充的 ExpressionRoot.Expression。消费方按字段名匹配，不按键顺序。

[AstJsonlDeserializer.cs](../../../AST/AstJsonlDeserializer.cs) 三步重建：解析记录并定位本程序集节点/carrier 类型；父先于子构建并挂接（复用构造时已有的同型子容器，连续集合索引追加，struct carrier 修改写回宿主）；按声明类型回填字段和 Span。空行与无 id 的 `{"file":"…"}` 分隔元记录跳过，输入必须代表单棵树。未知类型/字段、无法定位的 via、悬空引用或格式错误抛带行号原因的 CompilerInternalException，不接受部分成功；结束强制完整性校验。往返目标为 Parse→Serialize→Deserialize→Serialize 逐行一致。验证见 [ASTIntegrityValidatorTests.cs](../../../Tests/ASTIntegrityValidatorTests.cs)、[AstJsonlSerializerTests.cs](../../../Tests/AstJsonlSerializerTests.cs) 及插值结构断言。

## 表示方式

AST 节点为可变 class，以公有字段和具名属性承载状态，配合层栈式 Parser 的原地构建与施工目标协议。
