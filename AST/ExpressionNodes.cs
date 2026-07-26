using System;
using System.Collections.Generic;

namespace LatteCompiler
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
        private ExpressionASTNode? expression;

        public ExpressionRootASTNode(ASTNode parent)
            : base(parent)
        {
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

        public override ASTNodeType NodeType =>
            ASTNodeType.ValueExpressionRoot;
    }

    // 二元运算表达式
    public class BinaryExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Left { get; }
        public ExpressionRootASTNode Right { get; }
        public string Operator;  // +, -, *, /, and, or, ==, !=, etc.

        public BinaryExpressionASTNode()
        {
            Left = new ExpressionRootASTNode(this);
            Right = new ExpressionRootASTNode(this);
            Operator = "";
        }

        public override ASTNodeType NodeType => ASTNodeType.TwoValueExpression;
    }

    // 一元运算表达式
    public class UnaryExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Operand { get; }
        public string Operator;  // -, not, await
        public bool IsPrefix;    // true = 前缀, false = 后缀

        public UnaryExpressionASTNode()
        {
            Operand = new ExpressionRootASTNode(this);
            Operator = "";
            IsPrefix = true;
        }

        public override ASTNodeType NodeType => ASTNodeType.OneValueExpression;
    }

    // 字面量表达式（包装一个字面量节点）
    public class LiteralExpressionASTNode : ExpressionASTNode
    {
        private LiteralASTNode? literal;

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

        public override ASTNodeType NodeType => Literal.NodeType;
    }

    // 符号引用表达式（变量、函数调用等）
    public class SymbolReferenceASTNode : ExpressionASTNode
    {
        public SymbolASTNode Symbol;

        public SymbolReferenceASTNode(ASTNode? parent = null) : base(parent)
        {
            Symbol = new SymbolASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.Symbol;
    }

    // 括号分组表达式
    public class GroupExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode InnerExpression { get; }

        public GroupExpressionASTNode()
        {
            InnerExpression = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.GroupExpression;
    }

    // new 表达式
    public class NewExpressionASTNode : ExpressionASTNode
    {
        public TypeReferenceASTNode Type;
        public List<ArgumentASTNode> Arguments;

        public NewExpressionASTNode()
        {
            Type = new TypeReferenceASTNode(this);
            Arguments = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.NewExpression;
    }

    // 调用/索引/构造实参（可具名，如 foo(name = 42)）
    public class ArgumentASTNode : ASTNode
    {
        public string? Name;           // 具名实参名；位置实参为 null
        public ExpressionRootASTNode Value { get; }

        public ArgumentASTNode(ASTNode? parent) : base(parent)
        {
            Name = null;
            Value = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.Argument;
    }

    // 函数调用表达式
    public class CallExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Callee { get; }  // 被调用的表达式
        public List<ArgumentASTNode> Arguments;

        public CallExpressionASTNode()
        {
            Callee = new ExpressionRootASTNode(this);
            Arguments = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.InvokeStatemnt;
    }

    // 索引访问表达式
    public class IndexExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Object { get; }
        public List<ArgumentASTNode> Indices;

        public IndexExpressionASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            Indices = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.AcquisitionExpression;
    }

    // 成员访问表达式
    public class MemberAccessASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Object { get; }
        public string MemberName;
        public bool IsSafeAccess;  // ?. 安全访问
        public List<TypeReferenceASTNode> GenericArguments;  // 泛型实参（foo().bar\<i32>）

        public MemberAccessASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            MemberName = "";
            IsSafeAccess = false;
            GenericArguments = new List<TypeReferenceASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.AcquisitionExpression;
    }

    // Lambda 表达式（SYNTAX.md §5）：
    // [async] func{(params)\<T>: ReturnType -> body}
    // body 当前仅支持单表达式，多语句块待 P2 CodeBlockParserLayer
    public class LambdaExpressionASTNode : ExpressionASTNode
    {
        public bool IsAsync;                              // async 修饰
        public ParameterListASTNode Parameters;           // 形参列表 (...)
        public GenericParameterListASTNode? GenericParameters;  // 泛型形参 \<...>（可选）
        public TypeReferenceASTNode ReturnType;           // 返回类型
        public ExpressionRootASTNode Body { get; }        // lambda 体（单表达式）

        public LambdaExpressionASTNode()
        {
            IsAsync = false;
            Parameters = new ParameterListASTNode(this);
            GenericParameters = null;
            ReturnType = new TypeReferenceASTNode(this);
            Body = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.LambdaExpression;
    }

    // if 表达式（SYNTAX.md §7.1）：if (cond) { then } else { else }
    // 作为表达式时必须有 else 分支；分支当前仅支持单表达式
    public class IfExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Condition { get; }
        public ExpressionRootASTNode ThenExpression { get; }
        public ExpressionRootASTNode ElseExpression { get; }

        public IfExpressionASTNode()
        {
            Condition = new ExpressionRootASTNode(this);
            ThenExpression = new ExpressionRootASTNode(this);
            ElseExpression = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.IfExpression;
    }

    // switch 表达式的一个分支：(pattern) -> { body }
    // 不含 _ 的分支为值匹配（编译期常量）；含 _ 的为模式匹配（结果为 bool）
    public class SwitchCaseASTNode : ASTNode
    {
        public ExpressionRootASTNode Pattern { get; }
        public ExpressionRootASTNode Body { get; }

        public SwitchCaseASTNode(ASTNode? parent) : base(parent)
        {
            Pattern = new ExpressionRootASTNode(this);
            Body = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.SwitchExpression;
    }

    // switch 表达式（SYNTAX.md §7.2）：
    // switch(expr) { (pattern) -> { body } ... default -> { body } }
    // 作为表达式时必须有 default 分支；分支体当前仅支持单表达式
    public class SwitchExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Selector { get; }
        public List<SwitchCaseASTNode> Cases;
        public ExpressionRootASTNode? DefaultBody;

        public SwitchExpressionASTNode()
        {
            Selector = new ExpressionRootASTNode(this);
            Cases = new List<SwitchCaseASTNode>();
            DefaultBody = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.SwitchExpression;
    }

    // typeOf 表达式（SYNTAX.md §3.7）：typeOf(expr)，返回 Type\<T>
    public class TypeOfExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Operand { get; }

        public TypeOfExpressionASTNode()
        {
            Operand = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.TypeOfExpression;
    }

    // 类型转换表达式（SYNTAX.md §3.5）：
    // obj as String（失败抛 core.CastException）/ obj as? String（失败返回 null）
    public class CastExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Object { get; }
        public TypeReferenceASTNode TargetType;
        public bool IsSafe;    // true = as? 安全转换

        public CastExpressionASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            TargetType = new TypeReferenceASTNode(this);
            IsSafe = false;
        }

        public override ASTNodeType NodeType => ASTNodeType.CastExpression;
    }

    // 范围表达式（SYNTAX.md §7.3）：for (i in 0 to 10) 中的 0 to 10
    // to 由 LoopParserLayer 直接消费（上下文关键字，不进表达式层）
    // fromRoot：循环层已解析的范围起点表达式的挂载 Root（由本节点收养，
    // 避免表达式在 Root 间搬家——Root 与表达式都只能附加一次）
    public class RangeExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode From { get; }
        public ExpressionRootASTNode To { get; }

        public RangeExpressionASTNode(ExpressionRootASTNode fromRoot)
        {
            From = fromRoot;
            To = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.RangeExpression;
    }

    // 类型检查表达式（SYNTAX.md §3.5/§3.7）：
    // obj is String / obj supers Animal / obj with Serializable
    // 右侧也可以是 Type\<T> 值（词法上与类型名无歧义，统一按类型引用解析）
    public class TypeCheckExpressionASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Object { get; }
        public string Operator;    // is / supers / with
        public TypeReferenceASTNode TargetType;

        public TypeCheckExpressionASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            Operator = "";
            TargetType = new TypeReferenceASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.TypeCheckExpression;
    }

    // seq 块表达式（SYNTAX.md §6）：
    // [volatile] seq [using(...)]* [named label] { ... }
    // 可作为语句（不产生值）或表达式（通过 return@seq/return@label 产生值）
    // 注：继承自 ExpressionASTNode，因此可以在表达式位置使用；
    //     在代码块中单独成行时，作为表达式语句（构造时传入块父节点）
    public class SeqBlockExpressionASTNode : ExpressionASTNode
    {
        public bool IsVolatile;                    // volatile 修饰符
        public List<UsingBindingASTNode> UsingBindings;  // using 资源绑定列表
        public string? Label;                      // named 标签（可选）
        public CodeBlockASTNode Body;

        public SeqBlockExpressionASTNode(ASTNode? parent = null) : base(parent)
        {
            IsVolatile = false;
            UsingBindings = new List<UsingBindingASTNode>();
            Label = null;
            Body = new CodeBlockASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.SeqBlockExpression;
    }

    // 前导点 enum case 引用（SYNTAX.md §12）：.Success / .Entity
    // 规范要求存在已确定 enum 类型的 receiver/期望类型上下文（语义阶段校验，
    // 解析期只识别形态）；参数化 case 的调用（.Failed(404)）由后缀链
    // 自然脱糖为 Call 节点，本节点不自带实参。
    public class EnumCaseExpressionASTNode : ExpressionASTNode
    {
        public string CaseName;

        public EnumCaseExpressionASTNode()
        {
            CaseName = "";
        }

        public override ASTNodeType NodeType => ASTNodeType.EnumCaseExpression;
    }

    // wrapper 访问表达式（SYNTAX.md §14.1）：obj:MyWrapper
    // 链式 obj:A:B 左结合（"obj 的修饰器 A 的修饰器 B"，逐层后缀生成嵌套节点）；
    // 与调用/索引/成员访问同属路径表达式后缀链（§3），在运算符之前整体形成
    public class WrapperAccessASTNode : ExpressionASTNode
    {
        public ExpressionRootASTNode Object { get; }
        public string WrapperName;

        public WrapperAccessASTNode()
        {
            Object = new ExpressionRootASTNode(this);
            WrapperName = "";
        }

        public override ASTNodeType NodeType => ASTNodeType.WrapperAccess;
    }
}
