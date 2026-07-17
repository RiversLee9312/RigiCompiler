using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // 变量声明 AST 节点
    public class VariableDeclarationASTNode : ASTNode
    {
        public bool IsConst;                           // true = const, false = var
        public string Name;                            // 变量名
        public TypeReferenceASTNode? TypeAnnotation;   // 类型标注（可选）
        public ExpressionASTNode? Initializer;         // 初始化表达式（可选）

        public VariableDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            IsConst = false;
            Name = "";
            TypeAnnotation = null;
            Initializer = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.Declaration;
    }

    // 泛型型变修饰（SYNTAX.md §3.6，同 Kotlin 的 in/out）
    public enum GenericVariance
    {
        None,
        Out,
        In
    }

    // 泛型约束种类
    public enum GenericConstraintKind
    {
        Extends,    // 上界：TItem extends Comparable
        Supers,     // 下界：Serializable supers BaseType
        With        // wrapper 修饰要求：TItem with Serializable
    }

    // 泛型参数声明（\<...\> 列表中的声明子句）
    public class GenericParameterASTNode : ASTNode
    {
        public string Name;                    // 参数名（T 前缀驼峰，如 TElement）
        public GenericVariance Variance;       // 型变修饰
        public bool IsVariadic;                // 位置可变参数 TArgs...
        public bool IsNamedVariadic;           // 具名可变参数 named TArgs...

        public GenericParameterASTNode(ASTNode? parent) : base(parent)
        {
            Name = "";
            Variance = GenericVariance.None;
            IsVariadic = false;
            IsNamedVariadic = false;
        }

        public override ASTNodeType NodeType => ASTNodeType.Declaration;
    }

    // 泛型约束子句：<Target> extends/supers/with <Bound>
    // 注意：Target 为裸标识符（如 TItem）时，该标识符即泛型参数
    // （见 SYNTAX.md §3.6 示例：process\<TItem extends Comparable, ...>）
    public class GenericConstraintASTNode : ASTNode
    {
        public TypeReferenceASTNode Target;    // 被约束的类型（通常是参数名）
        public GenericConstraintKind Kind;
        public TypeReferenceASTNode Bound;     // 约束边界

        public GenericConstraintASTNode(ASTNode? parent) : base(parent)
        {
            Target = new TypeReferenceASTNode(this);
            Kind = GenericConstraintKind.Extends;
            Bound = new TypeReferenceASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.Declaration;
    }

    // 泛型参数列表（\<...>），挂在类型/函数/wrapper/lambda 声明上
    public class GenericParameterListASTNode : ASTNode
    {
        public List<GenericParameterASTNode> Parameters;     // 参数声明子句
        public List<GenericConstraintASTNode> Constraints;   // 约束子句

        public GenericParameterListASTNode(ASTNode? parent) : base(parent)
        {
            Parameters = new List<GenericParameterASTNode>();
            Constraints = new List<GenericConstraintASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.Declaration;
    }

    // 函数形参（roadmap #5）：
    // name: Type [= default] [...] 或 name: named Type...
    public class ParameterASTNode : ASTNode
    {
        public string Name;
        public TypeReferenceASTNode Type;
        public bool IsVariadic;                // 位置可变：numbers: i32...
        public bool IsNamedVariadic;           // 具名可变：options: named String...
        public ExpressionASTNode? DefaultValue;

        public ParameterASTNode(ASTNode? parent) : base(parent)
        {
            Name = "";
            Type = new TypeReferenceASTNode(this);
            IsVariadic = false;
            IsNamedVariadic = false;
            DefaultValue = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.Declaration;
    }

    // 函数形参列表（(...)），挂在函数/lambda/运算符/init 声明上
    public class ParameterListASTNode : ASTNode
    {
        public List<ParameterASTNode> Parameters;

        public ParameterListASTNode(ASTNode? parent) : base(parent)
        {
            Parameters = new List<ParameterASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.Declaration;
    }
}
