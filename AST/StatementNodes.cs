using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // 语句 AST 节点（P2 语句系统）
    //
    // 约定：
    // - 语句不产生结果，直接挂入 CodeBlockASTNode.Children
    // - 表达式语句以 ExpressionRootASTNode 作为块的子节点（统一表达式挂载点）

    // 代码块 { ... }：一组有序语句
    // 语句容器直接用 ASTNode.Children（M14 已上移到基类），不再另设字段
    public class CodeBlockASTNode : ASTNode
    {
        public CodeBlockASTNode(ASTNode? parent) : base(parent)
        {
        }

        public override ASTNodeType NodeType => ASTNodeType.CodeBlockExpression;
    }

    // if 语句（SYNTAX.md §7.1）：if (cond) { ... } [else { ... } / else if ...]
    // ElseBranch 为 CodeBlockASTNode（else 块）或嵌套 IfStatementASTNode（else if 链）
    public class IfStatementASTNode : ASTNode
    {
        public ExpressionRootASTNode Condition { get; }
        public CodeBlockASTNode ThenBlock;
        public ASTNode? ElseBranch;

        public IfStatementASTNode(ASTNode? parent) : base(parent)
        {
            Condition = new ExpressionRootASTNode(this);
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
        public string? VariableName;              // 仅 For
        public ExpressionRootASTNode? Iterable;   // 仅 For：集合或 RangeExpression（有循环项时非 null）
        public ExpressionRootASTNode? Condition;  // 仅 While/DoWhile（非 null）
        public string? Label;                     // named 标签（可选）
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
        public string? Label;                     // @标签（可选，如 return@seq value）
        public ExpressionRootASTNode? Value;      // 可选返回值（裸 return 为 null）

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
    // targetRoot：代码块已解析出的赋值目标表达式的挂载 Root（由本节点收养，
    // 避免表达式在 Root 间搬家——Root 与表达式都只能附加一次）
    public class AssignStatementASTNode : ASTNode
    {
        public ExpressionRootASTNode Target { get; }
        public ExpressionRootASTNode Value { get; }

        public AssignStatementASTNode(ASTNode? parent, ExpressionRootASTNode targetRoot) : base(parent)
        {
            Target = targetRoot;
            Value = new ExpressionRootASTNode(this);
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
        public ExpressionRootASTNode Initializer { get; }

        public UsingBindingASTNode(ASTNode? parent) : base(parent)
        {
            IsConst = false;
            VariableName = null!;
            Type = null;
            Initializer = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.UsingBinding;
    }

    // throw 语句：throw expression
    public class ThrowStatementASTNode : ASTNode
    {
        public ExpressionRootASTNode Exception { get; }   // 要抛出的异常表达式

        public ThrowStatementASTNode(ASTNode? parent) : base(parent)
        {
            Exception = new ExpressionRootASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.ThrowStatement;
    }

    // yield 语句（SYNTAX.md §7.5）：
    // yield                    // 裸 yield
    // yield alarm              // 带 alarm 表达式
    public class YieldStatementASTNode : ASTNode
    {
        public ExpressionRootASTNode? Alarm;           // 可选的 alarm 表达式

        public YieldStatementASTNode(ASTNode? parent) : base(parent)
        {
            Alarm = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.YieldStatement;
    }
}
