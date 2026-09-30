namespace RigiCompiler.Bil
{
    // BIL 文本拼写唯一定义点（M57）：全部枚举 → 标准拼写的映射集中于此，
    // 模型与 BilWriter 不得再出现拼写字面量。未知枚举值一律
    // CompilerInternalException（内部错误，与用户源码错误区分）。
    internal static class BilSpellings
    {
        // §15.4：proxy 模板内由 Middleware 链接的保留调用目标。
        // super 暂留给后续 ABI，当前仅实现 inner。
        public const string InnerReservedFunction = "..inner";
        public const string SuperReservedFunction = "..super";

        // §9.7：实体 wrapper 初始化方法保留名（方法简单名精确匹配）
        public const string InitWrapperMethodName = "..init.wrapper";

        // §9.7：字段初始化器方法保留名族前缀（..init.field.<字段名>）——
        // 每个带声明初始值的实例字段一个可覆写合成方法；子类同名字段
        // override 时生成同族 override 版，虚派发选中最高派生实现
        public const string InitFieldMethodPrefix = "..init.field.";

        // MW11d-B2：@Serializable 宿主合成方法（走正常 BIL 产物流）。
        // ..toParcel / ..fromParcel 是 ..ISerializable 的实现槽；
        // ..init.serializable 是特权构造（token 实参，不依赖用户 init）。
        public const string ToParcelMethodName = "..toParcel";
        public const string EncodeGraphMethodName = "..encode.graph";
        public const string DecodeGraphMethodName = "..decode.graph";
        public const string GraphIdKey = "..id";
        public const string GraphReferenceKey = "..ref";
        public const string GraphPayloadKey = "..data";
        public const string FromParcelMethodName = "..fromParcel";
        public const string InitSerializableMethodName = "..init.serializable";
        // 含 const 字段 class 的反序列化构造器（SerializationSynthesis.Parcel
        // FillClassConstructorDecoder 合成）：与 init.serializable 同属重建
        // 协议，wrapper 方法链一律豁免（review-20260910 #04）
        public const string InitDeserializeMethodName = "..init.deserialize";
        public const string SerializableIfaceName = "..ISerializable";
        public const string SerializableTokenName = "..serializable.token";
        // Map wire 物化的 Parcel.typeName 判别锚点（§4.6.3/D3：所有 Map 统一
        // 为有序键值条目序列后，合成器不再直摊平业务键、不再发射该 typeName；
        // 常量保留作手动构造 Parcel 的类型名判别与文档锚点）
        public const string MapParcelTypeName = "core.collections.Map";

        // §9.3/§8.4.1：全局（及静态）字段初始化器函数保留名——编译器合成
        // 的无参 void 全局 fn，main 前由 VM 同步执行（参照 §8.7 companion
        // 统一设计：静态初值的执行时机归 VM 启动序列）
        public const string GlobalsInitFunctionName = "..globals.init";

        // §13.3：setter 体内直读直写 backing 的保留字段名（不绕 wrapper 链）
        public const string BackingValueFieldName = "..value";

        // §8.7：静态 companion 类型名（声明类的嵌套类，无 UUID；canonical
        // 形态为「命名空间::外层...companion」）
        public const string CompanionTypeName = "..companion";

        // §14.4：全局函数 Method wrapper 的宿主 singleton 类型名（顶层合成
        // 类，每命名空间一个，无 UUID；canonical 形态为「命名空间::..globals.host」
        // ——全局函数无宿主类型可嵌套 companion，故提升为命名空间级 singleton）
        public const string GlobalMethodHostTypeName = "..globals.host";

        // §14.2/§15.6：call??? 降级请求的方法符号宿主段（完整符号 =
        // core::Any$call???(symbol:.string,namedArgs:...,unnamedArgs:...)@.any；
        // 无符号段声明的预定义符号，Middleware 由 CallWildcardLoweringPass 改写）
        public const string CallWildcardMethodHead = "core::Any$call???";

        // ==/!= 判等（SYNTAX §13.2，用户裁定）：Any 承诺 operator equals 的
        // 默认体合成 fn（LocalSymbolEmitters.EmitSynthesizedEqualsDefaultBody
        // 发射；$$ 即 operator canonical）——默认体 = 双虚调 core::Any$hash
        // 比较（equals-or-hash 判等链，绝不涉 toString）。内建宿主不进
        // LocalSymbols：VM DispatchUserBinary fallback 经函数表直查、
        // Middleware 静态 Any 直调臂/泛型占位末臂共用此键；native 侧两 fn
        // 由 MirReachability 恒收编建 MIR（体内 Any$hash invoke 经
        // FlowBuilder 重定向 + BuiltinToStringDispatchPass 得 override 感知）
        public const string AnyEqualsCanonical = "core::Any$$equals(other:.any)@.bool";
        public const string ObjectEqualsCanonical = "core::Object$$equals(other:.any)@.bool";

        // call??? 方法符号判定（宿主段精确匹配，签名段之前；VM
        // BilDataInstructions.IsCallWildcardSymbol 同口径）
        public static bool IsCallWildcardMethod(string methodSymbol)
        {
            var cut = methodSymbol.IndexOf('(');
            var head = cut < 0 ? methodSymbol : methodSymbol.Substring(0, cut);
            return head == CallWildcardMethodHead;
        }


        // §11 运算 opcode（§5.6：不带前导点）
        public static string Of(BilBinaryOp op)
        {
            return op switch
            {
                BilBinaryOp.Add => "add",
                BilBinaryOp.Sub => "sub",
                BilBinaryOp.Mul => "mul",
                BilBinaryOp.Div => "div",
                BilBinaryOp.Mod => "mod",
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
                BilBlockModifier.Unsafe => "unsafe",
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
                BilKeyword.Unsafe => "unsafe",
                BilKeyword.NativeBorrow => "native-borrow",
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

        // §8.4 wrapper-proxy(...) 修饰符的 PROXY_KIND 两态拼写（M88）
        public static string Of(BilProxyKind kind)
        {
            return kind switch
            {
                BilProxyKind.Specific => "specific",
                BilProxyKind.Wildcard => "wildcard",
                _ => throw new CompilerInternalException("未知 BilProxyKind: " + kind),
            };
        }

        // §4.1/§19.1/§19.3 标量类型关键字（无前导点）
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
