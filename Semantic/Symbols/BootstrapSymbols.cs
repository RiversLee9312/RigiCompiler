using System.Collections.Generic;

namespace LatteCompiler
{
    // BIL §11 intrinsic 键空间的运算维度：内建类型的「精确键」运算
    // （完整键 = opcode + operand type + declared result type，§11.1；
    // 结果类型维度由消费侧判定——算术/位运算结果同操作数，比较结果为 bool）。
    public enum BilIntrinsicOp
    {
        // §11.2 算术
        Add, Sub, Mul, Div, Opposite,
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
    // 无处用源码声明，由符号图初始化时直接构造；core.latte（S10）负责其余
    // 标准库表层（core::Console、Task 等），走同一条 P1/P2 路径。
    // 层级事实（SYNTAX §3.1/§3.1.2，2026-07-29 修订后三条易错点）：
    //   String 与 Wrapper 都在 ValueType 分支下（String 非 rich、Wrapper 恒 rich）；
    //   Nullable\<T> 的 shared 属性由 T 推导而不是查声明修饰符；
    //   Wrapper 是全部 wrapper 声明的隐式基类。
    public sealed class BootstrapSymbols
    {
        public NamespaceSymbol Core { get; }

        // 类型层级根（SYNTAX §3.1）
        public TypeSymbol Any { get; }
        public TypeSymbol Object { get; }
        public TypeSymbol ValueType { get; }
        public TypeSymbol Enum { get; }
        // 全部 wrapper 声明的隐式基类；wrapper 恒 rich struct（SYNTAX §14.9）
        public TypeSymbol Wrapper { get; }
        // 异常根类型（S7d 定稿：根进 bootstrap——throw/catch 是语言级控制流，
        // 兼容性检查需要常驻参照点；CastException 等具体子类归 S10 stdlib 源）
        public TypeSymbol Exception { get; }

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
        public TypeSymbol SpanDefinition { get; }      // Span\<T extends ValueType>
        public TypeSymbol NullableDefinition { get; }  // Nullable\<T>（Object 分支）
        public TypeSymbol BoxDefinition { get; }       // Box\<T extends ValueType>（Object 分支）

        internal BootstrapSymbols(NamespaceSymbol globalNamespace)
        {
            // core 挂进全局命名空间树：用户文件的 namespace core.* 声明与
            // bootstrap 的 core 共享同一驻留路径（GetNamespace 逐段合并）
            Core = new NamespaceSymbol("core", globalNamespace);
            globalNamespace.ChildNamespaces.Add(Core);

            // 层级根。Any 是类型层级最顶端（RUNTIME：baseTypeId 仅 Any 为 NULL）——
            // 行为近似纯多态上限，Kind 记 Interface + IsBuiltin；
            // 用户类型与它的继承关系规则由 P2 特判
            Any = new TypeSymbol("Any", TypeKind.Interface, Core,
                isBuiltin: true, bilAlias: ".any");
            Object = new TypeSymbol("Object", TypeKind.Class, Core,
                baseType: Any, isBuiltin: true, bilAlias: ".object");
            ValueType = new TypeSymbol("ValueType", TypeKind.Struct, Core,
                baseType: Any, isBuiltin: true, isValueTypeBranch: true, bilAlias: ".valuetype");
            Enum = new TypeSymbol("Enum", TypeKind.EnumStruct, Core,
                baseType: ValueType, isBuiltin: true);
            Wrapper = new TypeSymbol("Wrapper", TypeKind.Wrapper, Core,
                baseType: ValueType, isRich: true, isBuiltin: true);
            // 异常根：Object 分支普通 class，open 供用户异常类型继承
            Exception = new TypeSymbol("Exception", TypeKind.Class, Core,
                baseType: Object, isBuiltin: true) { IsOpen = true };

            // 数值类型：整数 = 算术 + 位运算 + 比较；浮点 = 算术 + 比较
            // （无符号不含 Opposite——一元负号对无符号无意义）
            Int8 = Primitive("i8", ".i8", signedInteger: true);
            Int16 = Primitive("i16", ".i16", signedInteger: true);
            Int32 = Primitive("i32", ".i32", signedInteger: true);
            Int64 = Primitive("i64", ".i64", signedInteger: true);
            UInt8 = Primitive("u8", ".u8", unsignedInteger: true);
            UInt16 = Primitive("u16", ".u16", unsignedInteger: true);
            UInt32 = Primitive("u32", ".u32", unsignedInteger: true);
            UInt64 = Primitive("u64", ".u64", unsignedInteger: true);
            Float = Primitive("float", ".f32", floating: true);
            Double = Primitive("double", ".f64", floating: true);
            // bool：逻辑 + 相等；char：比较全集（按码点序）；
            // String：相等 + 拼接（S7f，SYNTAX §3.8：add 为内建字符串拼接，
            // BIL §11.2——字符串插值与用户书写的 "a" + "b" 共用此键）
            Bool = new TypeSymbol("bool", TypeKind.Struct, Core,
                baseType: ValueType, isBuiltin: true, bilAlias: ".bool",
                intrinsicOps: Ops(BilIntrinsicOp.And, BilIntrinsicOp.Or, BilIntrinsicOp.Not,
                    BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe));
            Char = new TypeSymbol("char", TypeKind.Struct, Core,
                baseType: ValueType, isBuiltin: true, bilAlias: ".char",
                intrinsicOps: Ops(BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe,
                    BilIntrinsicOp.CmpLt, BilIntrinsicOp.CmpLe, BilIntrinsicOp.CmpGt, BilIntrinsicOp.CmpGe));
            String = new TypeSymbol("String", TypeKind.Struct, Core,
                baseType: ValueType, isBuiltin: true, bilAlias: ".string",
                intrinsicOps: Ops(BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe,
                    BilIntrinsicOp.Add));

            // toString 机制（S7f，SYNTAX §3.8）：Any 承载全类型承诺（接口
            // 形态无体）；Object 提供 open 默认实现，body 路由 latte_rt.toString
            // （native 声明形态，BIL §21.5 内建 hook——基元标准文本、未覆写
            // 对象返回类型 canonical 名）；用户类型 override 后经虚派发执行
            // 自身实现，不再命中原生面
            Any.Methods.Add(new MethodSymbol("toString", MethodKind.Regular,
                owner: Any, returnType: String));
            Object.Methods.Add(new MethodSymbol("toString", MethodKind.Regular,
                owner: Object, isNative: true, returnType: String)
            {
                NativeLibrary = "latte_rt",
                NativeSymbol = "toString",
            });

            // 泛型内建（§3.1.2）：
            // Box\<T> <: Object 为内建事实（BaseType 链直接表达，不经 baseTypeId 证明）；
            // Nullable\<T> 属 Object 分支且 shared 按 T 推导（DerivesSharedSafetyFromTypeArgument）；
            // Type\<T>/Span\<T> 在 ValueType 分支
            var typeParamT = new GenericParameterSymbol("T");
            TypeDefinition = new TypeSymbol("Type", TypeKind.Struct, Core,
                baseType: ValueType, isBuiltin: true, bilStandardConstructor: ".typeid");
            TypeDefinition.GenericParameters.Add(typeParamT);

            SpanDefinition = new TypeSymbol("Span", TypeKind.Struct, Core,
                baseType: ValueType, isBuiltin: true);
            SpanDefinition.GenericParameters.Add(new GenericParameterSymbol("T"));
            SpanDefinition.GenericParameters[0].Constraints.Add(
                new GenericConstraintInfo(GenericConstraintKind.Extends, ValueType));

            NullableDefinition = new TypeSymbol("Nullable", TypeKind.Class, Core,
                baseType: Object, isBuiltin: true,
                derivesSharedSafetyFromTypeArgument: true,
                bilStandardConstructor: ".nullable");
            NullableDefinition.GenericParameters.Add(new GenericParameterSymbol("T"));

            BoxDefinition = new TypeSymbol("Box", TypeKind.Class, Core,
                baseType: Object, isBuiltin: true);
            BoxDefinition.GenericParameters.Add(new GenericParameterSymbol("T"));
            BoxDefinition.GenericParameters[0].Constraints.Add(
                new GenericConstraintInfo(GenericConstraintKind.Extends, ValueType));

            // 内建类型注册进 core 容器表（M40 补登：P2 名字解析经
            // 「core 命名空间隐式可见」消费——bootstrap 类型此前只有直造属性、
            // 未入容器表，裸名 i32/String/Object 无法经路径解析找到）
            foreach (var builtin in new[]
            {
                Any, Object, ValueType, Enum, Wrapper, Exception,
                Int8, Int16, Int32, Int64, UInt8, UInt16, UInt32, UInt64,
                Float, Double, Bool, Char, String,
                TypeDefinition, SpanDefinition, NullableDefinition, BoxDefinition,
            })
            {
                Core.Types.Add(builtin);
            }
        }

        // 内建基元直造（BaseType = ValueType 的 Struct + 固定别名 + intrinsic 集）
        private TypeSymbol Primitive(string name, string bilAlias,
            bool signedInteger = false, bool unsignedInteger = false, bool floating = false)
        {
            var ops = new HashSet<BilIntrinsicOp>();
            if (signedInteger || unsignedInteger || floating)
            {
                ops.UnionWith(new[]
                {
                    BilIntrinsicOp.Add, BilIntrinsicOp.Sub, BilIntrinsicOp.Mul, BilIntrinsicOp.Div,
                    BilIntrinsicOp.CmpEq, BilIntrinsicOp.CmpNe,
                    BilIntrinsicOp.CmpLt, BilIntrinsicOp.CmpLe, BilIntrinsicOp.CmpGt, BilIntrinsicOp.CmpGe,
                });
            }
            if (signedInteger || floating)
            {
                ops.Add(BilIntrinsicOp.Opposite);
            }
            if (signedInteger || unsignedInteger)
            {
                ops.UnionWith(new[]
                {
                    BilIntrinsicOp.BinAnd, BilIntrinsicOp.BinOr, BilIntrinsicOp.BinXor, BilIntrinsicOp.BinNot,
                    BilIntrinsicOp.ShiftLeft, BilIntrinsicOp.ShiftRight, BilIntrinsicOp.ShiftRightUnsigned,
                });
            }
            return new TypeSymbol(name, TypeKind.Struct, Core,
                baseType: ValueType, isBuiltin: true, bilAlias: bilAlias, intrinsicOps: ops);
        }

        private static IReadOnlySet<BilIntrinsicOp> Ops(params BilIntrinsicOp[] ops)
        {
            return new HashSet<BilIntrinsicOp>(ops);
        }
    }
}
