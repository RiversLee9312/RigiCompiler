using System;
using System.Collections.Generic;

namespace RigiCompiler
{
    // 语句 AST 节点（P2 语句系统）
    //
    // 约定：
    // - 语句不产生结果，直接挂入 CodeBlockASTNode.Statements
    // - 表达式开头的语句（表达式语句/赋值语句）以 ExpressionStatementASTNode 承载

    // 代码块 { ... }：一组有序语句
    public class CodeBlockASTNode : ASTNode
    {
        // 块内语句容器（局部声明也是语句，同挂此处）
        [ChildAstNode] public List<ASTNode> Statements = new List<ASTNode>();

        public CodeBlockASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 表达式开头的语句：纯表达式语句（foo()）或赋值语句（target = value）。
    // 统一容器（大扫除 Validator 重写）：
    // 「表达式语句还是赋值目标」要等表达式解析完、看到后续 token 才能确定；
    // 两个 ExpressionRoot 槽在节点创建时归属即定（Parent = 本节点），
    // 杜绝「先解析后决定归属」导致的节点搬家 / Parent 重挂。
    public class ExpressionStatementASTNode : ASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Expression { get; }  // 表达式本体 / 赋值目标
        [ChildAstNode] public ExpressionRootASTNode? AssignValue;        // 赋值右侧（纯表达式语句为 null）

        public ExpressionStatementASTNode(ASTNode? parent) : base(parent)
        {
            Expression = new ExpressionRootASTNode(this);
            AssignValue = null;
        }
    }

    // if 语句（SYNTAX.md §7.1）：if (cond) { ... } [else { ... } / else if ...]
    // ElseBranch 为 CodeBlockASTNode（else 块）或嵌套 IfStatementASTNode（else if 链）
    public class IfStatementASTNode : ASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Condition { get; }
        [ChildAstNode] public CodeBlockASTNode ThenBlock;
        [ChildAstNode] public ASTNode? ElseBranch;

        public IfStatementASTNode(ASTNode? parent) : base(parent)
        {
            Condition = new ExpressionRootASTNode(this);
            ThenBlock = new CodeBlockASTNode(this);
            ElseBranch = null;
        }
    }

    // switch 语句（SYNTAX.md §7.2）：switch (expr) { (pattern) -> { ... } ... default -> { ... } }
    // 语句形态（与 IfStatementASTNode 对称）：出现在语句位置，结果值被丢弃；
    // 分支体为完整代码块，可写多条语句；必须有 default 分支（两形态同规则）
    public class SwitchStatementASTNode : ASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Selector { get; }
        [ChildAstNode] public List<SwitchCaseASTNode> Cases;
        [ChildAstNode] public CodeBlockASTNode? DefaultBody;

        public SwitchStatementASTNode(ASTNode? parent) : base(parent)
        {
            Selector = new ExpressionRootASTNode(this);
            Cases = new List<SwitchCaseASTNode>();
            DefaultBody = null;
        }
    }

    // 循环种类（SYNTAX.md §7.3）
    public enum LoopKind
    {
        For,        // for (item in collection) / for (i in 0 to 10)——范围由 RangeTo 表达
        While,      // while (condition)
        DoWhile     // do { } while (condition)
    }

    // 循环语句（SYNTAX.md §7.3/§7.4）：
    // - For：VariableName + Iterable（集合或范围起点；RangeTo 非 null 时为范围循环）
    // - While/DoWhile：Condition
    // - Label：named 标签，配合 break@标签 / continue@标签
    public class LoopStatementASTNode : ASTNode
    {
        public LoopKind Kind;
        public string? VariableName;              // 仅 For
        [ChildAstNode] public ExpressionRootASTNode? Iterable;   // 仅 For：集合或范围起点（有循环项时非 null）
        [ChildAstNode] public ExpressionRootASTNode? RangeTo;    // 仅 For 范围循环：范围终点（非范围为 null）
        [ChildAstNode] public ExpressionRootASTNode? Condition;  // 仅 While/DoWhile（非 null）
        public string? Label;                     // named 标签（可选）
        [ChildAstNode] public CodeBlockASTNode Body;

        public LoopStatementASTNode(ASTNode? parent) : base(parent)
        {
            Kind = LoopKind.For;
            VariableName = null;
            Iterable = null;
            RangeTo = null;
            Condition = null;
            Label = null;
            Body = new CodeBlockASTNode(this);
        }
    }

    // return 语句：return / return expr / return@标签 expr（SYNTAX.md §4.1/§6.1）
    public class ReturnStatementASTNode : ASTNode
    {
        public string? Label;                     // @标签（可选，如 return@_ value）
        [ChildAstNode] public ExpressionRootASTNode? Value;      // 可选返回值（裸 return 为 null）

        public ReturnStatementASTNode(ASTNode? parent) : base(parent)
        {
            Label = null;
            Value = null;
        }
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
    }

    // catch 子句：catch (varName: Type) { ... } 或 catch (_: Type) { ... }（SYNTAX.md §8）
    public class CatchClauseASTNode : ASTNode
    {
        public string? VariableName;           // 异常变量名（_ 时为 null，表示丢弃）
        [ChildAstNode(Required = true)] public TypeReferenceASTNode ExceptionType;
        [ChildAstNode] public CodeBlockASTNode Body;

        public CatchClauseASTNode(ASTNode? parent) : base(parent)
        {
            VariableName = null;
            ExceptionType = null!;
            Body = new CodeBlockASTNode(this);
        }
    }

    // try-catch-finally 语句（SYNTAX.md §8）：
    // try { ... } [catch ...]* [finally(e) { ... }]
    // finally 的参数 e 代表 try/catch 中抛出的异常，无异常时为 null
    public class TryCatchFinallyStatementASTNode : ASTNode
    {
        [ChildAstNode] public CodeBlockASTNode TryBlock;
        [ChildAstNode] public List<CatchClauseASTNode> CatchClauses;
        public string? FinallyParameter;       // finally(e) 的参数名（可选）
        [ChildAstNode] public CodeBlockASTNode? FinallyBlock;

        public TryCatchFinallyStatementASTNode(ASTNode? parent) : base(parent)
        {
            TryBlock = new CodeBlockASTNode(this);
            CatchClauses = new List<CatchClauseASTNode>();
            FinallyParameter = null;
            FinallyBlock = null;
        }
    }

    // using 资源绑定：seq 块的资源管理子句（SYNTAX.md §6.2）
    // using(const/var name = initializer)
    public class UsingBindingASTNode : ASTNode
    {
        public bool IsConst;                       // const 或 var
        public string VariableName;
        [ChildAstNode] public TypeReferenceASTNode? Type;         // 可选类型标注
        [ChildAstNode] public ExpressionRootASTNode Initializer { get; }

        public UsingBindingASTNode(ASTNode? parent) : base(parent)
        {
            IsConst = false;
            VariableName = null!;
            Type = null;
            Initializer = new ExpressionRootASTNode(this);
        }
    }

    // throw 语句：throw expression
    public class ThrowStatementASTNode : ASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode Exception { get; }   // 要抛出的异常表达式

        public ThrowStatementASTNode(ASTNode? parent) : base(parent)
        {
            Exception = new ExpressionRootASTNode(this);
        }
    }

    // yield 语句（SYNTAX.md §7.5）：
    // yield                    // 裸 yield
    // yield alarm              // 带 alarm 表达式
    public class YieldStatementASTNode : ASTNode
    {
        [ChildAstNode] public ExpressionRootASTNode? Alarm;           // 可选的 alarm 表达式

        public YieldStatementASTNode(ASTNode? parent) : base(parent)
        {
            Alarm = null;
        }
    }
}
