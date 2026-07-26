using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // 变量声明 AST 节点
    // 同一个节点覆盖：栈上局部变量、类/struct 字段、全局变量
    // （§14.8 canonical symbol 的类名段可为空 —— 全局与成员同构）
    public class VariableDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers = new List<string>();  // pub/priv/static/... 局部变量为空
        public bool IsConst;                           // true = const, false = var
        public string Name;                            // 变量名
        public TypeReferenceASTNode? TypeAnnotation;   // 类型标注（可选）
        public PropertyAccessorASTNode? Getter;        // 属性访问器块中的 get（§9.4，可选）
        public PropertyAccessorASTNode? Setter;        // 属性访问器块中的 set（§9.4，可选）
        public ExpressionASTNode? Initializer;         // 初始化表达式（可选）

        public VariableDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            IsConst = false;
            Name = "";
            TypeAnnotation = null;
            Getter = null;
            Setter = null;
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
    // init 参数映射（§9.3）：param_name[:type] -> field_name [= default]
    // （param_name 为 _ 时参数名与字段名相同；type 省略时沿用字段类型，
    //   此时 Type 保持为空引用节点、MappedFieldName 非空）
    public class ParameterASTNode : ASTNode
    {
        public string Name;
        public TypeReferenceASTNode Type;
        public bool IsVariadic;                // 位置可变：numbers: i32...
        public bool IsNamedVariadic;           // 具名可变：options: named String...
        public string? MappedFieldName;        // init 参数映射的目标字段（无映射为 null）
        public ExpressionASTNode? DefaultValue;

        public ParameterASTNode(ASTNode? parent) : base(parent)
        {
            Name = "";
            Type = new TypeReferenceASTNode(this);
            IsVariadic = false;
            IsNamedVariadic = false;
            MappedFieldName = null;
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

    // 属性访问器种类（SYNTAX.md §9.4）
    public enum AccessorKind
    {
        Get,
        Set
    }

    // getter/setter 访问器（SYNTAX.md §9.4），挂在变量声明的访问器块 { ... } 中
    //   (value: _) 或省略参数 → 需要编译器生成 backing field（HasBackingField = true）
    //   (_: _)               → 计算属性，无 backing field
    //   Body 为 null         → 编译器生成实现（如 `pub get` / `priv set` 仅定义访问控制）
    // get 与 set 的 HasBackingField 必须一致（解析期校验）
    public class PropertyAccessorASTNode : ASTNode
    {
        public List<string> Modifiers;         // pub/priv
        public AccessorKind Kind;
        public bool HasBackingField;
        public CodeBlockASTNode? Body;

        public PropertyAccessorASTNode(ASTNode? parent) : base(parent)
        {
            Modifiers = new List<string>();
            Kind = AccessorKind.Get;
            HasBackingField = true;
            Body = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.PropertyAccessor;
    }

    // ===== 可调用声明（P3）=====

    // 一个节点覆盖全部可调用声明形态：
    //   全局函数 / 实例方法 / 静态方法 / operator / init
    // 依据 §14.8：canonical symbol 的类名段可为空，static 只是一个标记位，
    // 因此"全局函数"与"成员方法"在结构上同构，不另立节点、不另立 Layer。
    public enum CallableKind
    {
        Func,       // func name(...)
        Operator,   // operator plus(...)
        Init        // init(...)
    }

    public class CallableDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers = new List<string>();
        public CallableKind Kind;
        public string Name;                          // init 时为 "init"
        public GenericParameterListASTNode? GenericParameters;
        public ParameterListASTNode Parameters;
        public TypeReferenceASTNode? ReturnType;     // 省略即无返回值
        public CodeBlockASTNode? Body;               // null = 抽象/接口无体声明

        public CallableDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Kind = CallableKind.Func;
            Name = "";
            GenericParameters = null;
            Parameters = new ParameterListASTNode(this);
            ReturnType = null;
            Body = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.CallableDeclaration;
    }

    // ===== 类型声明（P3）=====

    // 类声明（SYNTAX.md §9）
    // [modifiers] class Name [<generics>] [: BaseClass] [implements Interface1, Interface2] [like field] { ... }
    public class ClassDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers;                 // pub, open, abstract, singleton, shared, etc.
        public string ClassName;
        public GenericParameterListASTNode? GenericParameters;  // 可选泛型参数
        public TypeReferenceASTNode? BaseClass;        // 可选基类
        public List<TypeReferenceASTNode> Interfaces;  // implements 接口列表
        public string? LikeTarget;                     // like 委托的目标字段（§9.6，可选）
        // 成员（字段/方法/init/嵌套类型）直接挂在 ASTNode.Children 上，不另设容器

        public ClassDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Modifiers = new List<string>();
            ClassName = null!;
            GenericParameters = null;
            BaseClass = null;
            Interfaces = new List<TypeReferenceASTNode>();
            LikeTarget = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.ClassDeclaration;
    }

    // 接口声明（SYNTAX.md §11）
    public class InterfaceDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers;
        public string InterfaceName;
        public GenericParameterListASTNode? GenericParameters;
        public List<TypeReferenceASTNode> BaseInterfaces;  // interface 可以继承多个 interface

        public InterfaceDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Modifiers = new List<string>();
            InterfaceName = null!;
            GenericParameters = null;
            BaseInterfaces = new List<TypeReferenceASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.InterfaceDeclaration;
    }

    // struct 声明（SYNTAX.md §10）
    public class StructDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers;                 // pub, open, rich, shared, etc.
        public string StructName;
        public GenericParameterListASTNode? GenericParameters;
        public TypeReferenceASTNode? BaseStruct;       // struct 只能继承一个 struct
        public List<TypeReferenceASTNode> Interfaces;

        public StructDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Modifiers = new List<string>();
            StructName = null!;
            GenericParameters = null;
            BaseStruct = null;
            Interfaces = new List<TypeReferenceASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.StructDeclaration;
    }

    // enum struct 声明（SYNTAX.md §12）
    public class EnumStructDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers;
        public string EnumName;
        public GenericParameterListASTNode? GenericParameters;
        public List<EnumCaseASTNode> Cases;            // [] 中的 case 列表

        public EnumStructDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Modifiers = new List<string>();
            EnumName = null!;
            GenericParameters = null;
            Cases = new List<EnumCaseASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.EnumStructDeclaration;
    }

    // enum case 定义
    public class EnumCaseASTNode : ASTNode
    {
        public string CaseName;
        public List<ArgumentASTNode> Arguments;        // case 的参数（可能包含 _ 占位符）
        public int? DiscriminantValue;                 // 可选的显式判别值（-> N）

        public EnumCaseASTNode(ASTNode? parent) : base(parent)
        {
            CaseName = null!;
            Arguments = new List<ArgumentASTNode>();
            DiscriminantValue = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.EnumCase;
    }

    // wrapper 声明（SYNTAX.md §14）
    public class WrapperDeclarationASTNode : ASTNode
    {
        public List<string> Modifiers;
        public string WrapperName;
        public GenericParameterListASTNode? GenericParameters;

        public WrapperDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Modifiers = new List<string>();
            WrapperName = null!;
            GenericParameters = null;
        }

        public override ASTNodeType NodeType => ASTNodeType.WrapperDeclaration;
    }

    // 注解 / wrapper 应用（SYNTAX.md §14.5）：@Name 或 @Name(args)，可叠加多个。
    // 编译器内建 wrapper（@WrapperTarget(.Entity) 等）与用户 wrapper 应用
    // （@Logged("DEBUG")、@Clamped(0, 100)、@Timed()）共用同一语法形态。
    // 挂在声明节点的 ASTNode.Annotations 上（声明本体在注解之后解析，先暂存后挂接）。
    public class AnnotationASTNode : ASTNode
    {
        public SymbolASTNode Name;               // 注解名（可为 a.b 路径）
        public bool HasArguments;                // 是否写了 ()（区分 @Logged 与 @Timed()）
        public List<ArgumentASTNode> Arguments;  // 实参列表（复用调用实参结构）

        public AnnotationASTNode(ASTNode? parent) : base(parent)
        {
            Name = new SymbolASTNode(this);
            HasArguments = false;
            Arguments = new List<ArgumentASTNode>();
        }

        public override ASTNodeType NodeType => ASTNodeType.Annotation;
    }

    // namespace 声明（SYNTAX.md §15.1）：namespace com.example.myapp
    // 顶层单行声明；唯一性与位置约束（应在文件首部）留待语义阶段
    public class NamespaceDeclarationASTNode : ASTNode
    {
        public SymbolASTNode Name;               // 命名空间路径（a.b.c）

        public NamespaceDeclarationASTNode(ASTNode? parent) : base(parent)
        {
            Name = new SymbolASTNode(this);
        }

        public override ASTNodeType NodeType => ASTNodeType.NamespaceDeclaration;
    }
}
