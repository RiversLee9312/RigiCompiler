namespace LatteCompiler.Bil
{
    // BIL 文本拼写唯一定义点（M57）：全部枚举 → 标准拼写的映射集中于此，
    // 模型与 BilWriter 不得再出现拼写字面量。未知枚举值一律
    // CompilerInternalException（内部错误，与用户源码错误区分）。
    internal static class BilSpellings
    {
        // §11 运算 opcode（§5.6：不带前导点）
        public static string Of(BilBinaryOp op)
        {
            return op switch
            {
                BilBinaryOp.Add => "add",
                BilBinaryOp.Sub => "sub",
                BilBinaryOp.Mul => "mul",
                BilBinaryOp.Div => "div",
                BilBinaryOp.And => "and",
                BilBinaryOp.Or => "or",
                BilBinaryOp.BinAnd => "bin.and",
                BilBinaryOp.BinOr => "bin.or",
                BilBinaryOp.BinXor => "bin.xor",
                BilBinaryOp.ShiftLeft => "shift.left",
                BilBinaryOp.ShiftRight => "shift.right",
                BilBinaryOp.ShiftRightUnsigned => "shift.right.unsigned",
                BilBinaryOp.CmpEq => "cmp.eq",
                BilBinaryOp.CmpNe => "cmp.ne",
                BilBinaryOp.CmpLt => "cmp.lt",
                BilBinaryOp.CmpLe => "cmp.le",
                BilBinaryOp.CmpGt => "cmp.gt",
                BilBinaryOp.CmpGe => "cmp.ge",
                _ => throw new CompilerInternalException("未知 BilBinaryOp: " + op),
            };
        }

        public static string Of(BilUnaryOp op)
        {
            return op switch
            {
                BilUnaryOp.Opposite => "opposite",
                BilUnaryOp.Not => "not",
                BilUnaryOp.BinNot => "bin.not",
                _ => throw new CompilerInternalException("未知 BilUnaryOp: " + op),
            };
        }

        // §12.3 类型检查 opcode 前缀（动态形态由指令类追加 .indirect）
        public static string Of(BilTypeCheckKind kind)
        {
            return kind switch
            {
                BilTypeCheckKind.Is => "type.is",
                BilTypeCheckKind.Supers => "type.supers",
                BilTypeCheckKind.With => "type.with",
                _ => throw new CompilerInternalException("未知 BilTypeCheckKind: " + kind),
            };
        }

        // §8.2 类型种类
        public static string Of(BilTypeKind kind)
        {
            return kind switch
            {
                BilTypeKind.Class => "class",
                BilTypeKind.Struct => "struct",
                BilTypeKind.EnumStruct => "enum-struct",
                BilTypeKind.Interface => "interface",
                BilTypeKind.Wrapper => "wrapper",
                _ => throw new CompilerInternalException("未知 BilTypeKind: " + kind),
            };
        }

        // §8.3/§8.4 成员声明关键字（与符号中 .static. 标记的一致性由生成方
        // 保证，verifier 复核）
        public static string Of(BilMemberKind kind)
        {
            return kind switch
            {
                BilMemberKind.Field => ".field",
                BilMemberKind.StaticField => ".static-field",
                BilMemberKind.Method => ".method",
                BilMemberKind.StaticMethod => ".static-method",
                _ => throw new CompilerInternalException("未知 BilMemberKind: " + kind),
            };
        }

        // §9.6 block 修饰符
        public static string Of(BilBlockModifier modifier)
        {
            return modifier switch
            {
                BilBlockModifier.Entrypoint => "entrypoint",
                BilBlockModifier.Volatile => "volatile",
                _ => throw new CompilerInternalException("未知 BilBlockModifier: " + modifier),
            };
        }

        // §8.2/§8.3/§8.4 访问修饰符
        public static string Of(BilAccessibility accessibility)
        {
            return accessibility switch
            {
                BilAccessibility.Public => "pub",
                BilAccessibility.Protected => "protected",
                BilAccessibility.Internal => "internal",
                BilAccessibility.Private => "priv",
                _ => throw new CompilerInternalException("未知 BilAccessibility: " + accessibility),
            };
        }

        // §8.2/§8.3/§8.4 关键字修饰符
        public static string Of(BilKeyword keyword)
        {
            return keyword switch
            {
                BilKeyword.Open => "open",
                BilKeyword.Abstract => "abstract",
                BilKeyword.Singleton => "singleton",
                BilKeyword.Rich => "rich",
                BilKeyword.Shared => "shared",
                BilKeyword.Ext => "ext",
                BilKeyword.Init => "init",
                BilKeyword.Native => "native",
                BilKeyword.Entrypoint => "entrypoint",
                BilKeyword.Const => "const",
                BilKeyword.Var => "var",
                BilKeyword.Backing => "backing",
                BilKeyword.Computed => "computed",
                BilKeyword.Readable => "readable",
                BilKeyword.Writable => "writable",
                BilKeyword.CompilerGenerated => "compiler-generated",
                BilKeyword.Override => "override",
                BilKeyword.Async => "async",
                _ => throw new CompilerInternalException("未知 BilKeyword: " + keyword),
            };
        }

        // §8.4 访问器类别（getter(...)/setter(...) 修饰符拼写）
        public static string Of(BilAccessorKind kind)
        {
            return kind switch
            {
                BilAccessorKind.Getter => "getter",
                BilAccessorKind.Setter => "setter",
                _ => throw new CompilerInternalException("未知 BilAccessorKind: " + kind),
            };
        }

        // §4.1/§18.1/§18.3 标量类型关键字（无前导点）
        public static string Of(BilScalarType type)
        {
            return type switch
            {
                BilScalarType.String => "string",
                BilScalarType.Bool => "bool",
                BilScalarType.Char => "char",
                BilScalarType.F32 => "f32",
                BilScalarType.F64 => "f64",
                BilScalarType.I8 => "i8",
                BilScalarType.I16 => "i16",
                BilScalarType.I32 => "i32",
                BilScalarType.I64 => "i64",
                BilScalarType.U8 => "u8",
                BilScalarType.U16 => "u16",
                BilScalarType.U32 => "u32",
                BilScalarType.U64 => "u64",
                BilScalarType.RawHex => "raw.hex",
                BilScalarType.RawBin => "raw.bin",
                _ => throw new CompilerInternalException("未知 BilScalarType: " + type),
            };
        }
    }
}
