using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // 表达式基类
    public abstract class ExpressionASTNode : ASTNode
    {
        protected ExpressionASTNode(ASTNode? parent) : base(parent) { }
    }

    // 二元运算表达式
    public class BinaryExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Left;
        public ExpressionASTNode Right;
        public string Operator;  // +, -, *, /, and, or, ==, !=, etc.

        public BinaryExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Left = null!;
            Right = null!;
            Operator = "";
        }

        public override ASTNodeType NodeType => ASTNodeType.TwoValueExpression;
    }

    // 一元运算表达式
    public class UnaryExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Operand;
        public string Operator;  // -, not, await
        public bool IsPrefix;    // true = 前缀, false = 后缀

        public UnaryExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Operand = null!;
            Operator = "";
            IsPrefix = true;
        }

        public override ASTNodeType NodeType => ASTNodeType.OneValueExpression;
    }

    // 字面量表达式（包装已有的字面量节点）
    public class LiteralExpressionASTNode : ExpressionASTNode
    {
        public ASTNode LiteralNode;  // IntLiteralASTNode, StringLiteralASTNode, etc.

        public LiteralExpressionASTNode(ASTNode? parent, ASTNode literalNode) : base(parent)
        {
            LiteralNode = literalNode;
        }

        public override ASTNodeType NodeType => LiteralNode.NodeType;
    }

    // 符号引用表达式（变量、函数调用等）
    public class SymbolReferenceASTNode : ExpressionASTNode
    {
        public SymbolASTNode Symbol;

        public SymbolReferenceASTNode(ASTNode? parent) : base(parent)
        {
            Symbol = new SymbolASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.Symbol;
    }

    // 括号分组表达式
    public class GroupExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode InnerExpression;

        public GroupExpressionASTNode(ASTNode? parent) : base(parent)
        {
            InnerExpression = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.ValueExpressionRoot;
    }

    // new 表达式
    public class NewExpressionASTNode : ExpressionASTNode
    {
        public TypeReferenceASTNode Type;
        public List<ArgumentASTNode> Arguments;

        public NewExpressionASTNode(ASTNode? parent) : base(parent)
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
        public ExpressionASTNode Value;

        public ArgumentASTNode(ASTNode? parent, ExpressionASTNode value, string? name = null) : base(parent)
        {
            Name = name;
            Value = value;
        }

        public override ASTNodeType NodeType => ASTNodeType.Argument;
    }

    // 函数调用表达式
    public class CallExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Callee;  // 被调用的表达式
        public List<ArgumentASTNode> Arguments;

        public CallExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Callee = null!;
            Arguments = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.InvokeStatemnt;
    }

    // 索引访问表达式
    public class IndexExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Object;
        public List<ArgumentASTNode> Indices;

        public IndexExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Object = null!;
            Indices = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.AcquisitionExpression;
    }

    // 成员访问表达式
    public class MemberAccessASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Object;
        public string MemberName;
        public bool IsSafeAccess;  // ?. 安全访问
        public List<TypeReferenceASTNode> GenericArguments;  // 泛型实参（foo().bar\<i32>）

        public MemberAccessASTNode(ASTNode? parent) : base(parent)
        {
            Object = null!;
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
        public ExpressionASTNode Body;                    // lambda 体（单表达式）

        public LambdaExpressionASTNode(ASTNode? parent) : base(parent)
        {
            IsAsync = false;
            Parameters = new ParameterListASTNode(this);
            GenericParameters = null;
            ReturnType = new TypeReferenceASTNode(this);
            Body = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.LambdaExpression;
    }

    // if 表达式（SYNTAX.md §7.1）：if (cond) { then } else { else }
    // 作为表达式时必须有 else 分支；分支当前仅支持单表达式
    public class IfExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Condition;
        public ExpressionASTNode ThenExpression;
        public ExpressionASTNode ElseExpression;

        public IfExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Condition = null!;
            ThenExpression = null!;
            ElseExpression = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.IfExpression;
    }

    // switch 表达式的一个分支：(pattern) -> { body }
    // 不含 _ 的分支为值匹配（编译期常量）；含 _ 的为模式匹配（结果为 bool）
    public class SwitchCaseASTNode : ASTNode
    {
        public ExpressionASTNode Pattern;
        public ExpressionASTNode Body;

        public SwitchCaseASTNode(ASTNode? parent) : base(parent)
        {
            Pattern = null!;
            Body = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.SwitchExpression;
    }

    // switch 表达式（SYNTAX.md §7.2）：
    // switch(expr) { (pattern) -> { body } ... default -> { body } }
    // 作为表达式时必须有 default 分支；分支体当前仅支持单表达式
    public class SwitchExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Selector;
        public List<SwitchCaseASTNode> Cases;
        public ExpressionASTNode? DefaultBody;

        public SwitchExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Selector = null!;
            Cases = new List<SwitchCaseASTNode>();
            DefaultBody = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.SwitchExpression;
    }

    // typeOf 表达式（SYNTAX.md §3.7）：typeOf(expr)，返回 Type\<T>
    public class TypeOfExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Operand;

        public TypeOfExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Operand = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.TypeOfExpression;
    }

    // 类型转换表达式（SYNTAX.md §3.5）：
    // obj as String（失败抛 core.CastException）/ obj as? String（失败返回 null）
    public class CastExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Object;
        public TypeReferenceASTNode TargetType;
        public bool IsSafe;    // true = as? 安全转换

        public CastExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Object = null!;
            TargetType = new TypeReferenceASTNode(this);
            IsSafe = false;
        }

        public override ASTNodeType NodeType => ASTNodeType.CastExpression;
    }

    // 类型检查表达式（SYNTAX.md §3.5/§3.7）：
    // obj is String / obj supers Animal / obj with Serializable
    // 右侧也可以是 Type\<T> 值（词法上与类型名无歧义，统一按类型引用解析）
    public class TypeCheckExpressionASTNode : ExpressionASTNode
    {
        public ExpressionASTNode Object;
        public string Operator;    // is / supers / with
        public TypeReferenceASTNode TargetType;

        public TypeCheckExpressionASTNode(ASTNode? parent) : base(parent)
        {
            Object = null!;
            Operator = "";
            TargetType = new TypeReferenceASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.TypeCheckExpression;
    }
}
