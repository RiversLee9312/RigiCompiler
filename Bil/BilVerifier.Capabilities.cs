namespace RigiCompiler.Bil
{
    public static partial class BilVerifier
    {
        // 能力固定布局与保留入口是可验证 BIL 契约，不能凭 unsafe 任意伪造。
        private static void VerifyCapabilityDeclarations(BilVerificationContext context,
            List<BilVerificationError> errors)
        {
            foreach (var type in context.TypeDeclarations.Values)
            {
                if (type.Symbol.StartsWith(".handle", StringComparison.Ordinal)
                    && (type.Symbol != ".handle" || type.Kind != BilTypeKind.Class
                        || type.GenericParameters.Count != 0 || type.Members.Count != 0
                        || type.ExtendsType != null || type.ImplementsTypes.Count != 0
                        || !HasKeyword(type.Modifiers, BilKeyword.Shared)
                        || !HasKeyword(type.Modifiers, BilKeyword.Unsafe)
                        || !HasKeyword(type.Modifiers, BilKeyword.CompilerGenerated)))
                    errors.Add(new BilVerificationError("21.8", type.Symbol, ".handle 必须是无泛型无成员的固定 shared capability"));
                if (type.ExtendsType != null && BilVerificationContext.StripTypeArguments(type.ExtendsType) == ".handle")
                    errors.Add(new BilVerificationError("21.8", type.Symbol, ".handle 不可继承"));
            }
            foreach (var method in context.MethodDeclarations.Values)
            {
                var native = method.Modifiers.OfType<BilNativeSymbolModifier>().FirstOrDefault()?.Symbol;
                if (native?.StartsWith("handle_", StringComparison.Ordinal) != true) continue;
                var signature = native switch
                {
                    "handle_make" => "core::$handle_make(.generic.THandle:.typeid,target:.any,kind:.i32,mutable:.bool)@.any",
                    "handle_target" => "core::$handle_target(capability:.any)@.any",
                    "handle_kind" => "core::$handle_kind(capability:.any)@.i32",
                    "handle_is_mutable" => "core::$handle_is_mutable(capability:.any)@.bool",
                    "handle_type_is_value" => "core::$handle_type_is_value(.generic.T:.typeid)@.bool",
                    _ => null,
                };
                if (method.Symbol != signature
                    || !HasAccessibility(method, BilAccessibility.Private)
                    || !HasKeyword(method, BilKeyword.Unsafe)
                    || method.Modifiers.OfType<BilNativeLibraryModifier>().FirstOrDefault()?.Library != "rigi_rt")
                    errors.Add(new BilVerificationError("21.8", method.Symbol, "Handle native 机制入口必须为标准库私有保留函数"));
            }
            foreach (var field in context.FieldDeclarations.Keys)
                if (field.StartsWith(".handle#", StringComparison.Ordinal))
                    errors.Add(new BilVerificationError("21.8", field, ".handle 不允许声明或枚举隐藏字段"));
        }

        private static void VerifyCapabilityOperations(BilFunctionContext context,
            List<BilVerificationError> errors)
        {
            var instructions = context.AllInstructions().Select(p => p.Instruction).ToArray();
            // 只追踪单写临时的 cast 来源。多写局部不能伪装成已验证的稳定存储。
            var writes = new Dictionary<string, List<BilInstruction>>();
            foreach (var instruction in instructions)
            {
                var read = new List<BilVariableOperand>();
                var written = new List<BilVariableOperand>();
                ClassifyVariables(instruction, read, written);
                foreach (var value in written)
                {
                    if (!writes.TryGetValue(value.Name, out var list)) writes[value.Name] = list = new();
                    list.Add(instruction);
                }
            }
            foreach (var instruction in instructions)
            {
                switch (instruction)
                {
                    case NewInstruction create:
                        CheckNew(create.Type.TypeRef, create.Arguments);
                        break;
                    case NewWrappedInstruction create:
                        CheckNew(create.Type.TypeRef, create.InitArguments);
                        break;
                    case InvokeInstruction call:
                        CheckInvoke(call.Method.Symbol);
                        break;
                    case InvokeNoResultInstruction call:
                        CheckInvoke(call.Method.Symbol);
                        break;
                }
            }
            void Error(string message) => errors.Add(new BilVerificationError("21.8", context.Function.Symbol, message));
            void CheckInvoke(string symbol)
            {
                if (symbol.StartsWith("core::Place$init(", StringComparison.Ordinal))
                    Error("Place 保留构造不能作为普通方法调用");
                if (!context.Module.MethodDeclarations.TryGetValue(symbol, out var method)) return;
                var native = method.Modifiers.OfType<BilNativeSymbolModifier>().FirstOrDefault()?.Symbol;
                if (native?.StartsWith("handle_", StringComparison.Ordinal) != true) return;
                var caller = context.Function.Symbol;
                var allowed = native == "handle_make"
                    ? caller is "core::Place$expose()@.handle"
                        or "core::$handle_asMutable(capability:.any)@.handle"
                    : caller is "core::$handle_load(capability:.any)@.generic<$.generic.T>"
                        or "core::$handle_asMutable(capability:.any)@.handle"
                        or "core::$handle_store(capability:.any,value:.generic<$.generic.T>)@.void";
                if (!allowed) Error("Handle 隐藏机制不能由普通 BIL 函数调用");
            }
            void CheckNew(string typeRef, IReadOnlyList<BilVariableOperand> args)
            {
                var root = BilVerificationContext.StripTypeArguments(typeRef);
                if (root == ".handle") { Error(".handle 只能由 Place.expose 创建"); return; }
                if (root != "core::Place") return;
                if (args.Count is not (2 or 3)) { Error("Place 构造必须携带目标种类"); return; }
                if (!writes.TryGetValue(args[^1].Name, out var kindWrites) || kindWrites.Count != 1
                    || kindWrites[0] is not LoadInstruction { Resource: BilScalarResource resource }
                    || !int.TryParse(resource.LiteralText, out var kind) || kind is < 0 or > 2)
                { Error("Place 目标种类必须是编译期常量"); return; }
                var actual = OriginType(args[0].Name, new HashSet<string>());
                if (kind == 0)
                {
                    if (args.Count != 2 || !IsObject(actual)) Error("Place Object 目标必须是可证明的引用对象");
                }
                else if (!Derives(actual, kind == 1 ? "core::Cell" : "core::ReadonlyCell", typeRef))
                    Error("Place ValueType 目标必须使用原稳定 Cell/ReadonlyCell，不能装箱临时值");
            }
            string OriginType(string name, HashSet<string> seen)
            {
                if (seen.Add(name) && writes.TryGetValue(name, out var list) && list.Count == 1)
                {
                    if (list[0] is CastInstruction cast) return OriginType(cast.Source.Name, seen);
                    if (list[0] is GetVarInstruction get) return OriginType(get.Source.Name, seen);
                }
                return context.VariableTypes.GetValueOrDefault(name) ?? "";
            }
            bool Derives(string actual, string root, string placeType)
            {
                var seen = new HashSet<string>();
                while (seen.Add(actual))
                {
                    var normalized = BilVerificationContext.NormalizeTypeRef(actual);
                    if (BilVerificationContext.StripTypeArguments(normalized) == root)
                    {
                        var cellArg = normalized[(normalized.IndexOf('<') + 1)..^1];
                        var placeArg = placeType[(placeType.IndexOf('<') + 1)..^1];
                        return BilVerificationContext.NormalizeTypeRef(cellArg) == BilVerificationContext.NormalizeTypeRef(placeArg);
                    }
                    if (!context.Module.TryGetTypeDeclaration(actual, out var type) || type.ExtendsType == null) return false;
                    actual = type.ExtendsType;
                }
                return false;
            }
            bool IsObject(string actual)
            {
                var normalized = BilVerificationContext.NormalizeTypeRef(actual);
                if (normalized == "core::Any" || normalized.StartsWith(".generic<", StringComparison.Ordinal)
                    || BilVerificationContext.StripTypeArguments(normalized) == "core::Nullable") return false;
                if (context.Module.TryGetTypeDeclaration(actual, out var type))
                    return type.Kind == BilTypeKind.Class;
                return BilVerificationContext.StripTypeArguments(normalized) is "core::Object"
                    or "core::Array" or "core::Span" or "core::SharedSpan";
            }
        }
    }
}
