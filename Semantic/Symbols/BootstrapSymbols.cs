using System.Collections.Generic;

namespace RigiCompiler
{
    // BIL §11 intrinsic 键空间的运算维度：内建类型的「精确键」运算
    // （完整键 = opcode + operand type + declared result type，§11.1；
    // 结果类型维度由消费侧判定——算术/位运算结果同操作数，比较结果为 bool）。
    public enum BilIntrinsicOp
    {
        // §11.2 算术
        Add, Sub, Mul, Div, Mod, Opposite,
        // §11.3 逻辑（两个输入均已求值的类型驱动形态；内建 bool 的短路
        // and/or 由 frontend 用 if + 临时变量表达，不得直接发 and/or）
        And, Or, Not,
        // §11.4 位运算
        BinAnd, BinOr, BinXor, BinNot,
        ShiftLeft, ShiftRight, ShiftRightUnsigned,
        // §11.5 比较
        CmpEq, CmpNe, CmpLt, CmpLe, CmpGt, CmpGe
    }

    // 硬编码 bootstrap（SEMANTIC_ARCHITECTURE §4.3）：类型层级根与基元类型
    // 无处用源码声明，由符号图初始化时直接构造；core.rg（S10）负责其余
    // 标准库表层（core::Console、Task 等），走同一条 P1/P2 路径。
    // 层级事实（SYNTAX §3.1/§3.1.2，2026-07-29 修订后三条易错点）：
    //   String 与 Wrapper 都在 ValueType 分支下（默认非 rich）；
    //   Nullable\<T> 的 shared 属性由 T 推导而不是查声明修饰符；
    //   Wrapper 是全部 wrapper 声明的隐式基类。
    // Exception 不在此直造：stdlib/core/exceptions.rg 源码声明，懒解析取参照点。
    public sealed class BootstrapSymbols
    {
        public NamespaceSymbol Core { get; }

        // 类型层级根（SYNTAX §3.1）
        public TypeSymbol Any { get; }
        public TypeSymbol Object { get; }
        public TypeSymbol ValueType { get; }
        public TypeSymbol Enum { get; }
        // 全部 wrapper 声明的隐式基类；rich 由具体声明决定（SYNTAX §14.9）
        public TypeSymbol Wrapper { get; }
        // 异常根（SYNTAX §8.1）：stdlib/core/exceptions.rg 源码声明；懒解析
        // 仿 PairDefinition / CallWildcard（构造期 stdlib 未载入）
        public TypeSymbol Exception
        {
            get
            {
                if (_exception != null) return _exception;
                var found = Core.Types.FirstOrDefault(t => t.Name == "Exception"
                    && t.GenericParameters.Count == 0);
                if (found == null)
                {
                    throw new CompilerInternalException(
                        "core::Exception 尚未载入（stdlib 未装载）");
                }
                _exception = found;
                return found;
            }
        }
        private TypeSymbol? _exception;

        // SYNTAX §3.2 基本类型（float/double 的 BIL 别名为 .f32/.f64，§6.2）
        public TypeSymbol Int8 { get; }
        public TypeSymbol Int16 { get; }
        public TypeSymbol Int32 { get; }
        public TypeSymbol Int64 { get; }
        public TypeSymbol UInt8 { get; }
        public TypeSymbol UInt16 { get; }
        public TypeSymbol UInt32 { get; }
        public TypeSymbol UInt64 { get; }
        public TypeSymbol Float { get; }
        public TypeSymbol Double { get; }
        public TypeSymbol Bool { get; }
        public TypeSymbol Char { get; }
        public TypeSymbol String { get; }

        // 泛型内建定义（SYNTAX §3.1.2 特权类型）
        public TypeSymbol TypeDefinition { get; }      // Type\<T>
        public TypeSymbol SpanDefinition { get; }      // Span\<T extends ValueType>（Object 分支）
        public TypeSymbol SharedSpanDefinition { get; } // SharedSpan\<T extends ValueType>（shared class）
        public TypeSymbol NullableDefinition { get; }  // Nullable\<T>（Object 分支）
        public TypeSymbol BoxDefinition { get; }       // Box\<T extends ValueType>（Object 分支）
        public TypeSymbol ArrayDefinition { get; }     // Array\<T>（Object 分支，.array<T>）
        public TypeSymbol MapDefinition { get; }       // Map\<K, V>（Object 分支，.map<K, V>）

        // Any.call???（M88，RUNTIME §14.2 / SYNTAX §14.7）：未声明方法降级
        // 的统一入口。bootstrap 声明 + VM 内建 hook（pub native，getMessage
        // 先例）；签名 = 非泛型胖值 ABI。参数类型在 EnsureCallWildcard 落定
        //（具名包依赖 stdlib core.Pair，构造期 Pair 尚未载入）
        public MethodSymbol CallWildcard { get; private set; } = null!;

        internal RootASTNode? DeclarationSource { get; }
        internal Dictionary<string, TypeSymbol> SourceTypes { get; } = new(StringComparer.Ordinal);

        internal BootstrapSymbols(NamespaceSymbol globalNamespace, bool artifactOnly = false)
        {
            Core = new NamespaceSymbol("core", globalNamespace);
            globalNamespace.ChildNamespaces.Add(Core);
            if (artifactOnly) CreateManifestTypes();
            else
            {
            DeclarationSource = StdlibSources.ParseIntrinsics();
            foreach (var node in DeclarationSource.Declarations)
            {
                var shape = node switch
                {
                    ClassDeclarationASTNode c => (c.ClassName, TypeKind.Class, c.GenericParameters),
                    StructDeclarationASTNode s => (s.StructName, TypeKind.Struct, s.GenericParameters),
                    InterfaceDeclarationASTNode i => (i.InterfaceName, TypeKind.Interface, i.GenericParameters),
                    EnumStructDeclarationASTNode e => (e.EnumName, TypeKind.EnumStruct, e.GenericParameters),
                    WrapperDeclarationASTNode w => (w.WrapperName, TypeKind.Wrapper, w.GenericParameters),
                    _ => ((string?)null, TypeKind.Class, (GenericParameterListASTNode?)null),
                };
                if (shape.Item1 is not { } name) continue;
                var modifiers = ResolveEnvironment.ModifiersOf(node);
                var alias = name switch
                {
                    "Any" => ".any", "Object" => ".object", "ValueType" => ".valuetype",
                    "float" => ".f32", "double" => ".f64", "String" => ".string",
                    "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64"
                        or "bool" or "char" => "." + name,
                    _ => null,
                };
                var standard = name switch
                {
                    "Type" => ".typeid", "Nullable" => ".nullable", "Array" => ".array", "Map" => ".map",
                    _ => null,
                };
                var builtin = alias != null || standard != null
                    || name is "Enum" or "Wrapper" or "Span" or "SharedSpan" or "Box";
                var symbol = new TypeSymbol(name, shape.Item2, Core,
                    isRich: modifiers.Contains(Keywords.RICH), isShared: modifiers.Contains(Keywords.SHARED),
                    isBuiltin: builtin, bilAlias: alias, bilStandardConstructor: standard,
                    isValueTypeBranch: shape.Item2 is TypeKind.Struct or TypeKind.EnumStruct or TypeKind.Wrapper,
                    derivesSharedSafetyFromTypeArgument: name is "Nullable" or "Array" or "Map",
                    intrinsicOps: IntrinsicsOf(name));
                if (shape.Item3 != null)
                    foreach (var gp in shape.Item3.Parameters)
                        symbol.GenericParameters.Add(new GenericParameterSymbol(gp.Name, gp.IsVariadic, gp.IsNamedVariadic,
                            gp.Variance) { RequiresSharedSafe = gp.RequiresSharedSafe });
                SourceTypes.Add(name, symbol);
                Core.Types.Add(symbol);
            }
            }
            Any = Require("Any"); Object = Require("Object"); ValueType = Require("ValueType");
            Enum = Require("Enum"); Wrapper = Require("Wrapper");
            Int8 = Require("i8"); Int16 = Require("i16"); Int32 = Require("i32"); Int64 = Require("i64");
            UInt8 = Require("u8"); UInt16 = Require("u16"); UInt32 = Require("u32"); UInt64 = Require("u64");
            Float = Require("float"); Double = Require("double"); Bool = Require("bool");
            Char = Require("char"); String = Require("String");
            TypeDefinition = Require("Type"); SpanDefinition = Require("Span"); SharedSpanDefinition = Require("SharedSpan");
            NullableDefinition = Require("Nullable"); BoxDefinition = Require("Box");
            ArrayDefinition = Require("Array"); MapDefinition = Require("Map");
            foreach (var symbol in SourceTypes.Values)
            {
                // 类型层级根与固定 ABI 的绑定仍由语言定义；声明修饰符来自源码。
                symbol.BaseType = ReferenceEquals(symbol, Any) ? null
                    : ReferenceEquals(symbol, Object) || ReferenceEquals(symbol, ValueType) ? Any
                    : symbol.Kind == TypeKind.Class ? Object
                    : symbol.Kind is TypeKind.Struct or TypeKind.EnumStruct or TypeKind.Wrapper ? ValueType : null;
            }
        }

        // 固定有序 compiler-owned 类型身份表；Pair 属于标准库 API，不是语言内建。
        // 此路径完全不解析 .intrinsics，也不绑定源码签名，能力由可信接口 overlay。
        private void CreateManifestTypes()
        {
            foreach (var name in new[] { "Any", "Object", "ValueType", "Enum", "Wrapper", "i8", "i16", "i32", "i64",
                "u8", "u16", "u32", "u64", "float", "double", "bool", "char", "String", "Type", "Span", "SharedSpan", "Nullable", "Box", "Array", "Map" })
            {
                var kind = name switch { "Any" => TypeKind.Interface, "Enum" => TypeKind.EnumStruct, "Wrapper" => TypeKind.Wrapper,
                    "Object" or "Span" or "SharedSpan" or "Nullable" or "Box" or "Array" or "Map" => TypeKind.Class, _ => TypeKind.Struct };
                var alias = name switch { "Any" => ".any", "Object" => ".object", "ValueType" => ".valuetype",
                    "float" => ".f32", "double" => ".f64", "String" => ".string",
                    "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or "bool" or "char" => "." + name, _ => null };
                var standard = name switch { "Type" => ".typeid", "Nullable" => ".nullable", "Array" => ".array", "Map" => ".map", _ => null };
                var type = new TypeSymbol(name, kind, Core, isBuiltin: true, isShared: name == "SharedSpan",
                    isValueTypeBranch: kind is TypeKind.Struct or TypeKind.EnumStruct or TypeKind.Wrapper,
                    derivesSharedSafetyFromTypeArgument: name is "Nullable" or "Array" or "Map",
                    bilAlias: alias, bilStandardConstructor: standard, intrinsicOps: IntrinsicsOf(name)) { Accessibility = Accessibility.Public };
                foreach (var gpName in name switch { "Map" => new[] { "TKey", "TValue" },
                    "Type" or "Span" or "SharedSpan" or "Nullable" or "Box" or "Array" => new[] { "T" }, _ => Array.Empty<string>() })
                    type.GenericParameters.Add(new GenericParameterSymbol(gpName));
                SourceTypes.Add(name, type); Core.Types.Add(type);
            }
        }

        private TypeSymbol Require(string name) => SourceTypes.TryGetValue(name, out var type) ? type
            : throw new CompilerInternalException("内建源码缺少类型声明：" + name);

        internal void BindSourceSignatures(SymbolGraph symbols)
        {
            // 早期仅绑定签名，让无 stdlib 函数体的分析工具也能使用基本类型。
            // 完整编译再次采用同一批类型/泛型身份，标记与函数体走普通 P2/P3。
            var unit = new CompilationUnit(symbols, DeclarationSource ?? throw new CompilerInternalException("artifact-only graph 禁止绑定源码签名"));
            var declarations = DeclarationCollector.Collect(unit);
            var env = EntryCollector.Collect(unit, declarations);
            TypeReferenceResolver.Visit(env);
            GenericConstraintChecker.Visit(env);
            if (unit.Diagnostics.HasErrors)
                throw new CompilerInternalException("内建源码签名无效：" +
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            RefreshCallWildcard();
        }

        internal void RefreshCallWildcard() => CallWildcard = Any.Methods.Single(m => m.Name == "call???");

        private static IReadOnlySet<BilIntrinsicOp> IntrinsicsOf(string name)
        {
            var ops = new HashSet<BilIntrinsicOp>();
            var signed = name is "i8" or "i16" or "i32" or "i64";
            var unsigned = name is "u8" or "u16" or "u32" or "u64";
            var floating = name is "float" or "double";
            if (signed || unsigned || floating)
                ops.UnionWith(new[] { BilIntrinsicOp.Add, BilIntrinsicOp.Sub, BilIntrinsicOp.Mul, BilIntrinsicOp.Div,
                    BilIntrinsicOp.Mod, BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe, BilIntrinsicOp.CmpLt,
                    BilIntrinsicOp.CmpLe, BilIntrinsicOp.CmpGt, BilIntrinsicOp.CmpGe });
            if (signed || floating) ops.Add(BilIntrinsicOp.Opposite);
            if (signed || unsigned)
                ops.UnionWith(new[] { BilIntrinsicOp.BinAnd, BilIntrinsicOp.BinOr, BilIntrinsicOp.BinXor,
                    BilIntrinsicOp.BinNot, BilIntrinsicOp.ShiftLeft, BilIntrinsicOp.ShiftRight, BilIntrinsicOp.ShiftRightUnsigned });
            if (name == "bool")
                ops.UnionWith(new[] { BilIntrinsicOp.And, BilIntrinsicOp.Or, BilIntrinsicOp.Not, BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe });
            if (name == "char")
                ops.UnionWith(new[] { BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe, BilIntrinsicOp.CmpLt,
                    BilIntrinsicOp.CmpLe, BilIntrinsicOp.CmpGt, BilIntrinsicOp.CmpGe });
            if (name == "String") ops.UnionWith(new[] { BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe, BilIntrinsicOp.Add });
            return ops;
        }

        // 具名包 ABI 类型（§14.7：Array\<Pair\<String, Any\>\>；core::Pair
        // 缺席——无 stdlib 的测试驱动——时降级 Array\<Any\>）。P2/P3 共用
        public static TypeSymbol NamedPackType(SymbolGraph symbols)
        {
            var pairDefinition = symbols.Bootstrap.Core.Types.FirstOrDefault(t => t.Name == "Pair"
                && t.GenericParameters.Count == 2);
            var bootstrap = symbols.Bootstrap;
            var elementType = pairDefinition == null
                ? bootstrap.Any
                : symbols.GetConstructedType(pairDefinition, bootstrap.String, bootstrap.Any);
            return symbols.GetConstructedType(bootstrap.ArrayDefinition, elementType);
        }

        // call??? 参数签名落定（幂等）：symbol: String + namedArgs 具名包 +
        // unnamedArgs: Array\<Any\> → Any。P1 后（stdlib Pair 可能已入图）
        // 由 BindingDriver / 首次降级绑定触发
        public void EnsureCallWildcard(SymbolGraph symbols)
        {
            if (CallWildcard.Parameters.Count > 0) return;
            var packType = symbols.GetConstructedType(ArrayDefinition, Any);
            CallWildcard.Parameters.Add(new ParameterSymbol("symbol", String));
            CallWildcard.Parameters.Add(new ParameterSymbol("namedArgs", NamedPackType(symbols)));
            CallWildcard.Parameters.Add(new ParameterSymbol("unnamedArgs", packType));
        }
    }
}
