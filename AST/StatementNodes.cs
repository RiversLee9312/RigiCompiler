using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // 语句 AST 节点（P2 语句系统）
    //
    // 约定：
    // - 语句不产生结果，直接挂入 CodeBlockASTNode.Children
    // - 表达式语句以裸 ExpressionASTNode 作为块的子节点，无包装节点

    // 代码块 { ... }：一组有序语句
    public class CodeBlockASTNode : ASTNode
    {
        public List<ASTNode> Children;

        public CodeBlockASTNode(ASTNode? parent) : base(parent)
        {
            Children = new List<ASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.CodeBlockExpression;
    }

    // if 语句（SYNTAX.md §7.1）：if (cond) { ... } [else { ... } / else if ...]
    // ElseBranch 为 CodeBlockASTNode（else 块）或嵌套 IfStatementASTNode（else if 链）
    public class IfStatementASTNode : ASTNode
    {
        public ExpressionASTNode Condition;
        public CodeBlockASTNode ThenBlock;
        public ASTNode? ElseBranch;

        public IfStatementASTNode(ASTNode? parent) : base(parent)
        {
            Condition = null!;
            ThenBlock = new CodeBlockASTNode(this);
            ElseBranch = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.IfStatement;
    }

    // 循环种类（SYNTAX.md §7.3）
    public enum LoopKind
    {
        For,        // for (item in collection) / for (i in 0 to 10)——范围由 RangeExpression 表达
        While,      // while (condition)
        DoWhile     // do { } while (condition)
    }

    // 循环语句（SYNTAX.md §7.3/§7.4）：
    // - For：VariableName + Iterable（RangeExpression 时为范围循环）
    // - While/DoWhile：Condition
    // - Label：named 标签，配合 break@标签 / continue@标签
    public class LoopStatementASTNode : ASTNode
    {
        public LoopKind Kind;
        public string? VariableName;           // 仅 For
        public ExpressionASTNode? Iterable;    // 仅 For：集合或 RangeExpression
        public ExpressionASTNode? Condition;   // 仅 While/DoWhile
        public string? Label;                  // named 标签（可选）
        public CodeBlockASTNode Body;

        public LoopStatementASTNode(ASTNode? parent) : base(parent)
        {
            Kind = LoopKind.For;
            VariableName = null;
            Iterable = null;
            Condition = null;
            Label = null;
            Body = new CodeBlockASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.LoopStatement;
    }

    // return 语句：return / return expr / return@标签 expr（SYNTAX.md §4.1/§6.1）
    public class ReturnStatementASTNode : ASTNode
    {
        public string? Label;                  // @标签（可选，如 return@seq value）
        public ExpressionASTNode? Value;

        public ReturnStatementASTNode(ASTNode? parent) : base(parent)
        {
            Label = null;
            Value = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.ReturnStatement;
    }

    // break / continue 语句（SYNTAX.md §7.4）：可带 @标签
    public class LoopControlStatementASTNode : ASTNode
    {
        public bool IsBreak;                   // true = break，false = continue
        public string? Label;

        public LoopControlStatementASTNode(ASTNode? parent) : base(parent)
        {
            IsBreak = true;
            Label = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.LoopControlStatement;
    }

    // 赋值语句：target = value（目标为符号/成员访问/索引表达式）
    public class AssignStatementASTNode : ASTNode
    {
        public ExpressionASTNode Target;
        public ExpressionASTNode Value;

        public AssignStatementASTNode(ASTNode? parent) : base(parent)
        {
            Target = null!;
            Value = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.AssignStatement;
    }

    // catch 子句：catch (varName: Type) { ... } 或 catch (_: Type) { ... }（SYNTAX.md §8）
    public class CatchClauseASTNode : ASTNode
    {
        public string? VariableName;           // 异常变量名（_ 时为 null，表示丢弃）
        public TypeReferenceASTNode ExceptionType;
        public CodeBlockASTNode Body;

        public CatchClauseASTNode(ASTNode? parent) : base(parent)
        {
            VariableName = null;
            ExceptionType = null!;
            Body = new CodeBlockASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.CatchClause;
    }

    // try-catch-finally 语句（SYNTAX.md §8）：
    // try { ... } [catch ...]* [finally(e) { ... }]
    // finally 的参数 e 代表 try/catch 中抛出的异常，无异常时为 null
    public class TryCatchFinallyStatementASTNode : ASTNode
    {
        public CodeBlockASTNode TryBlock;
        public List<CatchClauseASTNode> CatchClauses;
        public string? FinallyParameter;       // finally(e) 的参数名（可选）
        public CodeBlockASTNode? FinallyBlock;

        public TryCatchFinallyStatementASTNode(ASTNode? parent) : base(parent)
        {
            TryBlock = new CodeBlockASTNode(this);
            CatchClauses = new List<CatchClauseASTNode>();
            FinallyParameter = null;
            FinallyBlock = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.TryCatchFinallyStatement;
    }

    // using 资源绑定：seq 块的资源管理子句（SYNTAX.md §6.2）
    // using(const/var name = initializer)
    public class UsingBindingASTNode : ASTNode
    {
        public bool IsConst;                       // const 或 var
        public string VariableName;
        public TypeReferenceASTNode? Type;         // 可选类型标注
        public ExpressionASTNode Initializer;

        public UsingBindingASTNode(ASTNode? parent) : base(parent)
        {
            IsConst = false;
            VariableName = null!;
            Type = null;
            Initializer = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.UsingBinding;
    }

    // throw 语句：throw expression
    public class ThrowStatementASTNode : ASTNode
    {
        public ExpressionASTNode Exception;        // 要抛出的异常表达式

        public ThrowStatementASTNode(ASTNode? parent) : base(parent)
        {
            Exception = null!;
        }

        public override ASTNodeType NodeType => ASTNodeType.ThrowStatement;
    }
}
