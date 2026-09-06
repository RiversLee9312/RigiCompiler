using System;
using System.Collections.Generic;

namespace RigiCompiler
{
    // 表达式基类
    // 表达式节点允许在施工期间暂时没有父节点（作为未挂载子树组合），
    // 最终通过某个 ExpressionRootASTNode.Attach() 设置父节点；
    // 语句位置直接挂接的表达式（如代码块中的 seq）可在构造时传入父节点。
    public abstract class ExpressionASTNode : ASTNode
    {
        protected ExpressionASTNode(ASTNode? parent = null) : base(parent) { }
    }

    // 表达式挂载点（Syntax AST 的正式节点）：
    // 表示「一个语法上要求出现表达式的位置，以及最终填入该位置的一棵表达式子树」。
    // 它不是临时回调对象，也不是 Parser 私有 slot；
    // 在语义分析和 BIL Lowering 中被视为透明容器。
    //
    // 强制不变量：
    // 1. 最多只能调用一次 Attach；2. 禁止替换已附加的表达式；
    // 3. 禁止附加已拥有父节点的表达式；4. 一个表达式节点只能属于一个 Root；
    // 5. 成功解析后的必需 Root 必须恰好包含一个表达式（缺失用 null Root 表示，
    //    禁止「非 null 但为空的 Root」）。
    public sealed class ExpressionRootASTNode : ASTNode
    {
        [ChildAstNode] private ExpressionASTNode? expression;

        public ExpressionRootASTNode(ASTNode parent)
            : base(parent)
        {
        }

        // Root 是透明容器：Span 未显式设置时透明继承内容表达式的范围
        // （覆盖 Attach 时表达式尚无 span 的情形，如 trailing lambda 的 Value Root）
        public override CharRange? Span
        {
            get => base.Span ?? expression?.Span;
            set => base.Span = value;
        }

        public bool IsAttached => expression is not null;

        public ExpressionASTNode Expression =>
            expression ?? throw new InvalidOperationException(
                "ExpressionRootASTNode has no attached expression.");

        public void Attach(ExpressionASTNode node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (expression is not null)
            {
                throw new InvalidOperationException(
                    "ExpressionRootASTNode already contains an expression.");
            }

            if (node.Parent is not null)
            {
                throw new InvalidOperationException(
                    "The expression node is already attached to another AST node.");
            }

            node.AttachTo(this);
            expression = node;
        }
    }

    // 二元运算表达式
    public class BinaryExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Left { get; }
        [ChildAstNode] public ExpressionRootASTNode Right { get; }
        public string Operator;  // +, -, *, /, and, or, ==, !=, etc.

        public BinaryExpressionASTNode()
        {
            Left = new ExpressionRootASTNode(this);
            Right = new ExpressionRootASTNode(this);
            Operator = "";
        }
    }

    // 复合赋值表达式（SYNTAX.md §13.2）：a += b 从对应运算符自动推导
    // （a = a + b 的语义糖）；全集 10 个：+= -= *= /= <<= >>= >>>= &= |= ^=（无 %=）。
    // 节点本身是表达式（可出现在表达式位置；语句位置由 ExpressionStatement 包装）。
    // Operator 存推导出的基础运算符（+、<<、>>> 等，不含 =）。
    public class CompoundAssignmentExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Target { get; }  // 被赋值的左操作数
        public string Operator;   // 基础运算符：+ - * / << >> >>> & | ^
        [ChildAstNode] public ExpressionRootASTNode Value { get; }   // 右操作数

        public CompoundAssignmentExpressionASTNode()
        {
            Target = new ExpressionRootASTNode(this);
            Value = new ExpressionRootASTNode(this);
            Operator = "";
        }
    }

    // 一元运算表达式
    public class UnaryExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Operand { get; }
        public string Operator;  // -, not, await
        public bool IsPrefix;    // true = 前缀, false = 后缀

        public UnaryExpressionASTNode()
        {
            Operand = new ExpressionRootASTNode(this);
            Operator = "";
            IsPrefix = true;
        }
    }

    // 字面量表达式（包装一个字面量节点）
    public class LiteralExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode(Required = true)] private LiteralASTNode? literal;

        // 整数字面量后紧跟成员访问的回退标记（SYNTAX §3.3，如 7.twice()）：
        // 字面量层在 '.' 之后确认后继是标识符而非数字时，把已消费的 '.'
        // 记在这里并弹栈交还标识符，外层表达式层据此按路径连接符处理
        internal bool MemberAccessDotConsumed;
        internal CharRange MemberAccessDotRange;

        public LiteralExpressionASTNode(ASTNode? parent = null) : base(parent)
        {
        }

        // 一次性附加字面量（重复附加抛异常；字面量节点的父节点即本节点）
        public void AttachLiteral(LiteralASTNode node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (literal is not null)
            {
                throw new InvalidOperationException(
                    "LiteralExpressionASTNode already contains a literal.");
            }

            if (node.Parent != this)
            {
                throw new InvalidOperationException(
                    "The literal node's parent must be this LiteralExpressionASTNode.");
            }

            literal = node;
        }

        public LiteralASTNode Literal =>
            literal ?? throw new InvalidOperationException(
                "LiteralExpressionASTNode has no attached literal.");
    }

    // ===== 路径表达式（SYNTAX §1.4，M42 统一）=====
    // 符号表达式经路径连接符（. / ?. / :）从左到右结合的整体——一条完整
    // 路径链恰一个节点，统一替代原 SymbolReference/Call/Index/MemberAccess/
    // WrapperAccess 五种碎裂形态。「首段是什么」（局部变量/参数/命名空间/
    // 类型）与各段语义（实例成员/静态成员/wrapper 访问）是语义上色问题，
    // 全部归 P3；语法层只表达 §1.4 的形态事实。

    // 路径连接符：. 普通成员 / ?. 安全成员 / : wrapper 访问
    public enum PathConnector
    {
        Dot,
        SafeDot,
        Colon
    }

    // 路径后缀种类：调用 () / 索引 []（参数表共用 ArgumentASTNode）
    public enum PathSuffixKind
    {
        Call,
        Index
    }

    // 路径表达式：首段 + 路径段序列（段序列可为空——纯符号 a、纯调用 a(1)
    // 也是路径）
    public class PathExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public PathHeadASTNode Head { get; }
        [ChildAstNode] public List<PathSegmentASTNode> Segments;

        public PathExpressionASTNode()
        {
            Head = new PathHeadASTNode(this);
            Segments = new List<PathSegmentASTNode>();
        }
    }

    // 路径首段：符号头（Name + 可选泛型实参 + 后缀序列，如 a / a\<i32> / a(1)[2]）
    // 或表达式底座（(a+b).c / foo()() / .Failed(404) 的底座，Expression 挂载点）。
    // Name 与 Expression 互斥（创建时定归属）。
    public class PathHeadASTNode : ASTNode
    {
        public string? Name;
        [ChildAstNode] public ExpressionRootASTNode? Expression;
        [ChildAstNode] public List<TypeReferenceASTNode> GenericArguments;
        [ChildAstNode] public List<PathSuffixASTNode> Suffixes;

        public PathHeadASTNode(ASTNode? parent) : base(parent)
        {
            Name = null;
            Expression = null;
            GenericArguments = new List<TypeReferenceASTNode>();
            Suffixes = new List<PathSuffixASTNode>();
        }
    }

    // 路径段：连接符 + 成员名 + 可选泛型实参 + 后缀序列（.bar / ?.length / :MyWrapper）
    public class PathSegmentASTNode : ASTNode
    {
        public PathConnector Connector;
        public string Name;
        [ChildAstNode] public List<TypeReferenceASTNode> GenericArguments;
        [ChildAstNode] public List<PathSuffixASTNode> Suffixes;

        public PathSegmentASTNode(ASTNode? parent) : base(parent)
        {
            Name = "";
            GenericArguments = new List<TypeReferenceASTNode>();
            Suffixes = new List<PathSuffixASTNode>();
        }
    }

    // 路径后缀：调用 () 或索引 []（trailing lambda 脱糖为 Call 后缀的唯一实参）
    public class PathSuffixASTNode : ASTNode
    {
        public PathSuffixKind Kind;
        [ChildAstNode] public List<ArgumentASTNode> Arguments;

        public PathSuffixASTNode(ASTNode? parent) : base(parent)
        {
            Arguments = new List<ArgumentASTNode>();
        }
    }

    // 括号分组表达式
    public class GroupExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode InnerExpression { get; }

        public GroupExpressionASTNode()
        {
            InnerExpression = new ExpressionRootASTNode(this);
        }
    }

    // new 表达式
    public class NewExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public TypeReferenceASTNode Type;
        [ChildAstNode] public List<ArgumentASTNode> Arguments;

        public NewExpressionASTNode()
        {
            Type = new TypeReferenceASTNode(this);
            Arguments = new List<ArgumentASTNode>();
        }
    }

    // 调用/索引/构造实参（可具名，如 foo(name = 42)；调用与索引参数表共用）
    public class ArgumentASTNode : ASTNode
    {
        public string? Name;           // 具名实参名；位置实参为 null
        [ChildAstNode] public ExpressionRootASTNode Value { get; }

        public ArgumentASTNode(ASTNode? parent) : base(parent)
        {
            Name = null;
            Value = new ExpressionRootASTNode(this);
        }
    }

    // Lambda 表达式（SYNTAX.md §5.1）：
    // [async] func{(params)[: ReturnType] -> [named 标签] body}
    // 省略 : ReturnType = 无返回值 void lambda（基类 core.Action 族，见 §5.2）
    // 体两形态互斥（创建时定，参照 ExpressionStatementASTNode 双 Root 槽先例）：
    // - Body：单表达式体（有返回值时隐式取值；void 时为表达式语句语义，语义层定）
    // - BlockBody：多语句代码块体（有返回值时所有路径须显式 return@_ / return@标签；
    //   块内禁止裸 return（§5.1，由 CodeBlockParserLayer 的 allowBareReturn 标记强制）
    // lambda 不支持泛型形参（§5.1）；泛型 callable 请显式声明类型
    public class LambdaExpressionASTNode : ExpressionASTNode, IMethodWrapperAttachable
    {
        public bool IsAsync;                              // async 修饰
        [ChildAstNode] public List<AnnotationASTNode> Annotations { get; } = new List<AnnotationASTNode>(); // lambda 头内部 @Name[(args)]（§14.4）
        [ChildAstNode] public ParameterListASTNode Parameters;           // 形参列表 (...)
        [ChildAstNode] public TypeReferenceASTNode? ReturnType;          // 返回类型（null = void）
        public string? Label;                             // named 标签（可选，-> 之后、体之前）
        [ChildAstNode] public ExpressionRootASTNode? Body;               // 单表达式体（与 BlockBody 互斥）
        [ChildAstNode] public CodeBlockASTNode? BlockBody;               // 多语句块体（与 Body 互斥）

        public LambdaExpressionASTNode()
        {
            IsAsync = false;
            Parameters = new ParameterListASTNode(this);
            ReturnType = null;
            Label = null;
            Body = null;
            BlockBody = null;
        }
    }

    // if 表达式（SYNTAX.md §7.1）：if (cond) [named 标签] { then } else { else }
    // 作为表达式时必须有 else 分支；分支体统一为代码块——「单表达式分支隐式取值」
    // 是「块内恰好一条 ExpressionStatement」的语义规则（取值留待语义阶段，解析层无特判）；
    // 多语句分支体必须显式 return@_（匿名默认标签）或 return@标签 产出分支值
    public class IfExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Condition { get; }
        [ChildAstNode] public CodeBlockASTNode ThenBody { get; }
        [ChildAstNode] public CodeBlockASTNode ElseBody { get; }
        public string? Label;                             // named 标签（可选，) 之后）

        public IfExpressionASTNode()
        {
            Condition = new ExpressionRootASTNode(this);
            ThenBody = new CodeBlockASTNode(this);
            ElseBody = new CodeBlockASTNode(this);
            Label = null;
        }
    }

    // switch 的一个分支：(pattern) -> { body }（表达式与语句两种形态共用）
    // 不含 _ 的分支为值匹配（编译期常量）；含 _ 的为模式匹配（结果为 bool）；
    // 分支体统一为代码块（取值规则同 if 表达式分支体，见 §7.1）
    public class SwitchCaseASTNode : ASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Pattern { get; }
        [ChildAstNode] public CodeBlockASTNode Body { get; }

        public SwitchCaseASTNode(ASTNode? parent) : base(parent)
        {
            Pattern = new ExpressionRootASTNode(this);
            Body = new CodeBlockASTNode(this);
        }
    }

    // switch 表达式（SYNTAX.md §7.2）：
    // switch(expr) [named 标签] { (pattern) -> { body } ... default -> { body } }
    // 必须有 default 分支；分支体为代码块，多语句分支必须显式 return@_ / return@标签
    public class SwitchExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Selector { get; }
        [ChildAstNode] public List<SwitchCaseASTNode> Cases;
        [ChildAstNode] public CodeBlockASTNode? DefaultBody;
        public string? Label;                             // named 标签（可选，) 之后）

        public SwitchExpressionASTNode()
        {
            Selector = new ExpressionRootASTNode(this);
            Cases = new List<SwitchCaseASTNode>();
            DefaultBody = null;
            Label = null;
        }
    }

    // placeOf 是专用前缀表达式；稳定存储与对象身份由 Binder 区分。
    public class PlaceOfExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Operand { get; }

        public PlaceOfExpressionASTNode()
        {
            Operand = new ExpressionRootASTNode(this);
        }
    }

    // typeOf 表达式（SYNTAX.md §3.7）：typeOf(expr)，返回 Type\<T>
    public class TypeOfExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Operand { get; }

        public TypeOfExpressionASTNode()
        {
            Operand = new ExpressionRootASTNode(this);
        }
    }

    // 类型转换表达式（SYNTAX.md §3.5）：
    // obj as String（失败抛 core.CastException）/ obj as? String（失败返回 null）
    public class CastExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Object { get; }
        [ChildAstNode] public TypeReferenceASTNode TargetType;
        public bool IsSafe;    // true = as? 安全转换

        public CastExpressionASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            TargetType = new TypeReferenceASTNode(this);
            IsSafe = false;
        }
    }

    // 类型检查表达式（SYNTAX.md §3.5/§3.7）：
    // obj is String / obj supers Animal / obj with Serializable
    // 右侧也可以是 Type\<T> 值（词法上与类型名无歧义，统一按类型引用解析）
    // 右侧两形态互斥（参照 ExpressionStatementASTNode 双 Root 槽先例，填充时定归属）：
    // - TargetType：类型引用（is/supers/with 通用）
    // - TargetCase：前导点 enum case（result is .Failed，仅 is 可用，SYNTAX §12.3）
    public class TypeCheckExpressionASTNode : ExpressionASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Object { get; }
        public string Operator;    // is / supers / with
        [ChildAstNode] public TypeReferenceASTNode? TargetType;
        [ChildAstNode] public EnumCaseExpressionASTNode? TargetCase;

        public TypeCheckExpressionASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            Operator = "";
            TargetType = null;
            TargetCase = null;
        }
    }

    // seq 块表达式（SYNTAX.md §6）：
    // [volatile] seq [using(...)]* [named label] { ... }
    // 可作为语句（不产生值）或表达式（单表达式隐式值，或多语句
    // return@_/return@标签 产生值；匿名默认标签为 _）
    // 注：继承自 ExpressionASTNode，因此可以在表达式位置使用；
    //     在代码块中单独成行时，作为表达式语句（构造时传入块父节点）
    public class SeqBlockExpressionASTNode : ExpressionASTNode
    {
        public bool IsVolatile;                    // volatile 修饰符
        public bool IsUnsafe;                      // unsafe 词法上下文
        [ChildAstNode] public List<UsingBindingASTNode> UsingBindings;  // using 资源绑定列表
        public string? Label;                      // named 标签（可选）
        [ChildAstNode] public CodeBlockASTNode Body;

        public SeqBlockExpressionASTNode(ASTNode? parent = null) : base(parent)
        {
            IsVolatile = false;
            UsingBindings = new List<UsingBindingASTNode>();
            Label = null;
            Body = new CodeBlockASTNode(this);
        }
    }

    // 前导点 enum case 引用（SYNTAX.md §12）：.Success / .Entity
    // 规范要求存在已确定 enum 类型的 receiver/期望类型上下文（语义阶段校验，
    // 解析期只识别形态）；参数化 case 的调用（.Failed(404)）由路径后缀链
    // 脱糖为表达式底座的 Call 后缀（PathExpressionASTNode），本节点不自带实参。
    // parent 参数形态供 is 右侧 TargetCase 槽使用（创建时归属即定，§12.3）。
    public class EnumCaseExpressionASTNode : ExpressionASTNode
    {
        public string CaseName;

        public EnumCaseExpressionASTNode(ASTNode? parent = null) : base(parent)
        {
            CaseName = "";
        }
    }
}
