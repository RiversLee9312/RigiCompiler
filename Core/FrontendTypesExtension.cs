using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    // ============================================================================
    // 这个文件扩展现有的 Utilities.cs 中的类型系统
    // 添加完整的 AST 节点和类型系统支持
    // ============================================================================

    // ============================================================================
    // 扩展：整数后缀（用于字面量解析）
    // ============================================================================

    public enum IntegerSuffix
    {
        None,    // i32 默认
        L,       // i64
        S,       // i16
        B,       // i8
        U,       // u32
        UL,      // u64
        US,      // u16
        UB       // u8
    }

    // ============================================================================
    // 扩展的 AST 节点类型枚举
    // ============================================================================

    // 这些扩展了 Utilities.cs 中已有的 ASTNodeType
    public static class ASTNodeTypeExtensions
    {
        // 类型声明
        public const string ClassDecl = "ClassDecl";
        public const string StructDecl = "StructDecl";
        public const string InterfaceDecl = "InterfaceDecl";
        public const string EnumStructDecl = "EnumStructDecl";
        public const string WrapperDecl = "WrapperDecl";

        // 成员声明
        public const string FieldDecl = "FieldDecl";
        public const string MethodDecl = "MethodDecl";
        public const string ConstructorDecl = "ConstructorDecl";
        public const string OperatorDecl = "OperatorDecl";

        // 类型节点
        public const string PrimitiveType = "PrimitiveType";
        public const string NamedType = "NamedType";
        public const string NullableType = "NullableType";
        public const string GenericTypeRef = "GenericTypeRef";
        public const string ArrayType = "ArrayType";

        // 语句
        public const string VarDecl = "VarDecl";
        public const string IfStmt = "IfStmt";
        public const string ForStmt = "ForStmt";
        public const string WhileStmt = "WhileStmt";
        public const string ReturnStmt = "ReturnStmt";
        public const string ThrowStmt = "ThrowStmt";
        public const string TryStmt = "TryStmt";
        public const string YieldStmt = "YieldStmt";

        // 表达式
        public const string BinaryOp = "BinaryOp";
        public const string UnaryOp = "UnaryOp";
        public const string CallExpr = "CallExpr";
        public const string IndexExpr = "IndexExpr";
        public const string MemberAccess = "MemberAccess";
        public const string CastExpr = "CastExpr";
        public const string NewExpr = "NewExpr";
        public const string TypeOfExpr = "TypeOfExpr";
    }

    // ============================================================================
    // 符号表（语义分析用）
    // ============================================================================

    /// <summary>
    /// 符号表 - 管理作用域内的符号
    /// 注意：这个 Symbol 类与 Utilities.cs 中的 Symbol 不同
    /// Utilities.cs 的 Symbol 用于 AST，这个 SymbolInfo 用于语义分析
    /// </summary>
    public class SymbolTable
    {
        private readonly Dictionary<string, SymbolInfo> _symbols = new();
        private readonly SymbolTable? _parent;

        public SymbolTable? Parent => _parent;

        public SymbolTable(SymbolTable? parent = null)
        {
            _parent = parent;
        }

        public void Define(SymbolInfo symbol)
        {
            if (_symbols.ContainsKey(symbol.Name))
            {
                throw new SemanticException($"Symbol '{symbol.Name}' already defined in current scope");
            }
            _symbols[symbol.Name] = symbol;
        }

        public SymbolInfo? Resolve(string name)
        {
            if (_symbols.TryGetValue(name, out var symbol))
            {
                return symbol;
            }
            return _parent?.Resolve(name);
        }

        public SymbolInfo? ResolveInCurrentScope(string name)
        {
            _symbols.TryGetValue(name, out var symbol);
            return symbol;
        }

        public IEnumerable<SymbolInfo> GetAllSymbols() => _symbols.Values;
    }

    // ============================================================================
    // 符号信息（用于语义分析，区别于 Utilities.cs 的 Symbol）
    // ============================================================================

    public enum SymbolKind
    {
        Variable,
        Parameter,
        Function,
        Class,
        Struct,
        Interface,
        Enum,
        Wrapper,
        Field,
        Method
    }

    public abstract class SymbolInfo
    {
        public string Name { get; set; }
        public TypeInfo Type { get; set; }
        public SymbolKind Kind { get; set; }
        public CharRange DefinitionRange { get; set; }

        protected SymbolInfo(string name, TypeInfo type, SymbolKind kind, CharRange range)
        {
            Name = name;
            Type = type;
            Kind = kind;
            DefinitionRange = range;
        }
    }

    public class VariableSymbolInfo : SymbolInfo
    {
        public bool IsConst { get; set; }
        public bool IsMutable { get; set; }

        public VariableSymbolInfo(string name, TypeInfo type, bool isConst, CharRange range)
            : base(name, type, SymbolKind.Variable, range)
        {
            IsConst = isConst;
            IsMutable = !isConst;
        }
    }

    public class FunctionSymbolInfo : SymbolInfo
    {
        public List<TypeInfo> ParameterTypes { get; set; }
        public TypeInfo ReturnType { get; set; }
        public bool IsAsync { get; set; }

        public FunctionSymbolInfo(string name, List<TypeInfo> paramTypes, TypeInfo returnType,
            bool isAsync, CharRange range)
            : base(name, new FunctionTypeInfo(paramTypes, returnType, isAsync), SymbolKind.Function, range)
        {
            ParameterTypes = paramTypes;
            ReturnType = returnType;
            IsAsync = isAsync;
        }
    }

    public class ClassSymbolInfo : SymbolInfo
    {
        public bool IsShared { get; set; }

        public ClassSymbolInfo(string name, TypeInfo type, bool isShared, CharRange range)
            : base(name, type, SymbolKind.Class, range)
        {
            IsShared = isShared;
        }
    }

    public class StructSymbolInfo : SymbolInfo
    {
        public bool IsRich { get; set; }
        public bool IsShared { get; set; }

        public StructSymbolInfo(string name, TypeInfo type, bool isRich, bool isShared, CharRange range)
            : base(name, type, SymbolKind.Struct, range)
        {
            IsRich = isRich;
            IsShared = isShared;
        }
    }

    // ============================================================================
    // 类型系统
    // ============================================================================

    public enum PrimitiveKind
    {
        I8, I16, I32, I64,
        U8, U16, U32, U64,
        Float, Double,
        Bool, Char, String,
        Void
    }

    /// <summary>
    /// 类型信息基类
    /// </summary>
    public abstract class TypeInfo
    {
        public abstract bool IsCompatibleWith(TypeInfo other);
        public abstract bool IsStrictlyEqual(TypeInfo other);

        public virtual bool IsValueType => false;
        public virtual bool IsObjectType => false;
        public virtual bool IsShared => false;
        public virtual bool IsRich => false;

        public abstract override string ToString();
    }

    public class PrimitiveTypeInfo : TypeInfo
    {
        public PrimitiveKind Kind { get; }

        public PrimitiveTypeInfo(PrimitiveKind kind)
        {
            Kind = kind;
        }

        public override bool IsValueType => true;

        public override bool IsCompatibleWith(TypeInfo other)
        {
            return other is PrimitiveTypeInfo prim && prim.Kind == Kind;
        }

        public override bool IsStrictlyEqual(TypeInfo other)
        {
            return other is PrimitiveTypeInfo prim && prim.Kind == Kind;
        }

        public override string ToString() => Kind.ToString().ToLower();
    }

    public class ClassTypeInfo : TypeInfo
    {
        public string Name { get; }
        public List<TypeInfo> GenericArgs { get; }
        private readonly bool _isShared;

        public ClassTypeInfo(string name, List<TypeInfo> genericArgs, bool isShared)
        {
            Name = name;
            GenericArgs = genericArgs;
            _isShared = isShared;
        }

        public override bool IsObjectType => true;
        public override bool IsShared => _isShared;

        public override bool IsCompatibleWith(TypeInfo other)
        {
            if (other is ClassTypeInfo ct)
            {
                if (Name != ct.Name) return false;
                if (GenericArgs.Count != ct.GenericArgs.Count) return false;
                for (int i = 0; i < GenericArgs.Count; i++)
                {
                    if (!GenericArgs[i].IsCompatibleWith(ct.GenericArgs[i]))
                        return false;
                }
                return true;
            }
            return false;
        }

        public override bool IsStrictlyEqual(TypeInfo other)
        {
            if (other is ClassTypeInfo ct)
            {
                if (Name != ct.Name || _isShared != ct._isShared) return false;
                if (GenericArgs.Count != ct.GenericArgs.Count) return false;
                for (int i = 0; i < GenericArgs.Count; i++)
                {
                    if (!GenericArgs[i].IsStrictlyEqual(ct.GenericArgs[i]))
                        return false;
                }
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            var shared = _isShared ? "shared " : "";
            var generics = GenericArgs.Count > 0
                ? $"<{string.Join(", ", GenericArgs)}>"
                : "";
            return $"{shared}{Name}{generics}";
        }
    }

    public class StructTypeInfo : TypeInfo
    {
        public string Name { get; }
        public List<TypeInfo> GenericArgs { get; }
        private readonly bool _isRich;
        private readonly bool _isShared;

        public StructTypeInfo(string name, List<TypeInfo> genericArgs, bool isRich, bool isShared)
        {
            Name = name;
            GenericArgs = genericArgs;
            _isRich = isRich;
            _isShared = isShared;
        }

        public override bool IsValueType => true;
        public override bool IsRich => _isRich;
        public override bool IsShared => _isShared;

        public override bool IsCompatibleWith(TypeInfo other)
        {
            if (other is StructTypeInfo st)
            {
                if (Name != st.Name) return false;
                if (GenericArgs.Count != st.GenericArgs.Count) return false;
                for (int i = 0; i < GenericArgs.Count; i++)
                {
                    if (!GenericArgs[i].IsCompatibleWith(st.GenericArgs[i]))
                        return false;
                }
                return true;
            }
            return false;
        }

        public override bool IsStrictlyEqual(TypeInfo other)
        {
            if (other is StructTypeInfo st)
            {
                if (Name != st.Name || _isRich != st._isRich || _isShared != st._isShared)
                    return false;
                if (GenericArgs.Count != st.GenericArgs.Count) return false;
                for (int i = 0; i < GenericArgs.Count; i++)
                {
                    if (!GenericArgs[i].IsStrictlyEqual(st.GenericArgs[i]))
                        return false;
                }
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            var modifiers = "";
            if (_isShared) modifiers += "shared ";
            if (_isRich) modifiers += "rich ";
            var generics = GenericArgs.Count > 0
                ? $"<{string.Join(", ", GenericArgs)}>"
                : "";
            return $"{modifiers}struct {Name}{generics}";
        }
    }

    public class FunctionTypeInfo : TypeInfo
    {
        public List<TypeInfo> ParameterTypes { get; }
        public TypeInfo ReturnType { get; }
        public bool IsAsync { get; }

        public FunctionTypeInfo(List<TypeInfo> parameterTypes, TypeInfo returnType, bool isAsync)
        {
            ParameterTypes = parameterTypes;
            ReturnType = returnType;
            IsAsync = isAsync;
        }

        public override bool IsCompatibleWith(TypeInfo other)
        {
            if (other is FunctionTypeInfo ft)
            {
                if (IsAsync != ft.IsAsync) return false;
                if (!ReturnType.IsCompatibleWith(ft.ReturnType)) return false;
                if (ParameterTypes.Count != ft.ParameterTypes.Count) return false;
                for (int i = 0; i < ParameterTypes.Count; i++)
                {
                    if (!ParameterTypes[i].IsCompatibleWith(ft.ParameterTypes[i]))
                        return false;
                }
                return true;
            }
            return false;
        }

        public override bool IsStrictlyEqual(TypeInfo other)
        {
            if (other is FunctionTypeInfo ft)
            {
                if (IsAsync != ft.IsAsync) return false;
                if (!ReturnType.IsStrictlyEqual(ft.ReturnType)) return false;
                if (ParameterTypes.Count != ft.ParameterTypes.Count) return false;
                for (int i = 0; i < ParameterTypes.Count; i++)
                {
                    if (!ParameterTypes[i].IsStrictlyEqual(ft.ParameterTypes[i]))
                        return false;
                }
                return true;
            }
            return false;
        }

        public override string ToString()
        {
            var async_ = IsAsync ? "async " : "";
            var params_ = string.Join(", ", ParameterTypes);
            return $"{async_}({params_}) -> {ReturnType}";
        }
    }

    public class ErrorTypeInfo : TypeInfo
    {
        public override bool IsCompatibleWith(TypeInfo other) => true;
        public override bool IsStrictlyEqual(TypeInfo other) => other is ErrorTypeInfo;
        public override string ToString() => "<error>";
    }

    public class UnknownTypeInfo : TypeInfo
    {
        public override bool IsCompatibleWith(TypeInfo other) => false;
        public override bool IsStrictlyEqual(TypeInfo other) => other is UnknownTypeInfo;
        public override string ToString() => "<unknown>";
    }

    // ============================================================================
    // 新增异常类型
    // ============================================================================

    public class SemanticException : Exception
    {
        public CharRange? Range { get; }

        public SemanticException(string message, CharRange? range = null)
            : base(message)
        {
            Range = range;
        }
    }

    public class TypeCheckException : SemanticException
    {
        public TypeInfo? ExpectedType { get; }
        public TypeInfo? ActualType { get; }

        public TypeCheckException(
            string message,
            CharRange? range = null,
            TypeInfo? expectedType = null,
            TypeInfo? actualType = null)
            : base(message, range)
        {
            ExpectedType = expectedType;
            ActualType = actualType;
        }
    }
}
