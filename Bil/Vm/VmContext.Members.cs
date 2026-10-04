using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Members 职责；与主文件共享同一类型、字段及生命周期。

        public bool IsValueType(string typeRef)
        {
            var normalized = BilVerificationContext.NormalizeTypeRef(typeRef);
            if (normalized == "core::ValueType" || normalized.StartsWith("core::Type<", StringComparison.Ordinal)) return true;
            if (IsBuiltinScalar(typeRef) || typeRef == ".string" || typeRef == "core::String")
            {
                return true;
            }
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            return declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                or BilTypeKind.Wrapper;
        }

        public static bool IsArrayType(string typeRef, out string elementType)
        {
            elementType = "";
            if (TryStripConstructor(typeRef, ".array", out elementType)
                || TryStripConstructor(typeRef, "core::Array", out elementType))
            {
                return true;
            }
            return false;
        }

        public bool IsEnumStruct(string typeRef)
        {
            var declaration = FindType(typeRef);
            return declaration != null && declaration.Kind == BilTypeKind.EnumStruct;
        }

        public bool TryFindInit(string typeRef, IReadOnlyList<VmValue> arguments,
            IReadOnlyList<string> argumentStaticTypes, out string initSymbol)
        {
            initSymbol = "";
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return arguments.Count == 0;
            }
            var inits = new List<BilSimpleMemberDeclaration>();
            foreach (var member in declaration.Members)
            {
                if (member is BilSimpleMemberDeclaration simple
                    && HasKeyword(simple, BilKeyword.Init))
                {
                    inits.Add(simple);
                }
            }
            if (inits.Count == 0)
            {
                return arguments.Count == 0;
            }
            // 构造泛型宿主（§14.1 严格匹配的 VM 侧落地）：init 声明在定义级
            // 符号上，参数类型含 .generic<$.generic.T> 占位——按构造实参代入
            // 后再与实参**静态类型** TypesEqual（编译期已 cast 到声明类型；
            // 不是按运行期 typeid 可赋值性再 ranking）
            var substitution = VmTypeSheetBuilder.BuildSubstitution(typeRef, declaration);
            foreach (var init in inits)
            {
                if (!BilVerificationContext.TryParseMethodSymbol(init.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                if (substitution != null)
                {
                    parameters = parameters.ConvertAll(p =>
                        (p.Name, VmTypeSheetBuilder.SubstituteGenericArguments(
                            p.TypeRef, substitution)));
                }
                if (ParametersMatch(parameters, arguments, argumentStaticTypes))
                {
                    initSymbol = init.Symbol;
                    return true;
                }
            }
            return false;
        }

        public bool TryFindInitWrapper(string typeRef, out string symbol, out int parameterCount)
        {
            symbol = "";
            parameterCount = 0;
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return false;
            }
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple)
                {
                    continue;
                }
                if (MethodNameOf(simple.Symbol) != BilSpellings.InitWrapperMethodName)
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(simple.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                symbol = simple.Symbol;
                parameterCount = parameters.Count;
                return true;
            }
            return false;
        }

        public bool TryFindAccessor(string fieldSymbol, BilAccessorKind kind,
            string currentFunction, out string methodSymbol)
        {
            methodSymbol = "";
            var table = kind == BilAccessorKind.Getter ? _getters : _setters;
            if (!table.TryGetValue(fieldSymbol, out var found))
            {
                return false;
            }
            if (found == currentFunction)
            {
                return false;
            }
            methodSymbol = found;
            return true;
        }

        // 直查 getter/setter 表，不排除当前 fn（链末落点调 setter）
        public bool TryFindAccessorDirect(string fieldSymbol, BilAccessorKind kind,
            out string methodSymbol)
        {
            methodSymbol = "";
            var table = kind == BilAccessorKind.Getter ? _getters : _setters;
            if (fieldSymbol.Length == 0 || !table.TryGetValue(fieldSymbol, out var found))
            {
                return false;
            }
            methodSymbol = found;
            return true;
        }

        // 访问器符号 → 字段符号反查（getter(FIELD)/setter(FIELD) 修饰符正向
        // 表的逆向）：Entity wildcard 环 inner 以 $get/$set 访问器符号重路由
        // 为 Get/Set 链时恢复链末字段目标——链可能起自访问器方法调用的
        // 拦截（如构造期 init/..init.field.* 走 setter），FieldSymbol 原本为空
        public bool TryFindFieldForAccessor(string accessorSymbol, out string fieldSymbol)
        {
            if (_accessorFields.TryGetValue(accessorSymbol, out var found))
            {
                fieldSymbol = found;
                return true;
            }
            fieldSymbol = "";
            return false;
        }

        // 当前 fn 是否为 fieldSymbol 的 getter（含 cell getValue 回退）
        public bool IsGetterOf(string currentFunction, string fieldSymbol)
        {
            var member = FindMember(currentFunction);
            if (member != null)
            {
                foreach (var modifier in member.Modifiers)
                {
                    if (modifier is BilAccessorModifier accessor
                        && accessor.Kind == BilAccessorKind.Getter
                        && accessor.FieldSymbol == fieldSymbol)
                    {
                        return true;
                    }
                }
            }
            if (!BilVerificationContext.TryParseMethodSymbol(currentFunction,
                    out var owner, out _, out _, out _))
            {
                return false;
            }
            if (MethodNameOf(currentFunction) != "getValue" || !IsCellTypeRef(owner))
            {
                return false;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out _, out _, out var fieldType))
            {
                return false;
            }
            return fieldSymbol == owner + "#value@" + fieldType;
        }

        // ..value → 真实 backing 字段。非 ..value 返回 false；是 ..value 但
        // 不在 setter 上下文则抛
        public bool TryResolveBackingValue(string fieldSymbol, string currentFunction,
            out string backingField)
        {
            backingField = "";
            if (FieldSimpleName(fieldSymbol) != BilSpellings.BackingValueFieldName)
            {
                return false;
            }
            if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out var isStatic, out var fieldType))
            {
                throw new VmException("..value 字段符号不可解析：" + fieldSymbol);
            }
            var member = FindMember(currentFunction);
            if (member != null)
            {
                foreach (var modifier in member.Modifiers)
                {
                    if (modifier is not BilAccessorModifier accessor
                        || accessor.Kind != BilAccessorKind.Setter)
                    {
                        continue;
                    }
                    if (!BilVerificationContext.TryParseFieldSymbol(accessor.FieldSymbol,
                            out var fieldOwner, out var fieldStatic, out var declaredType))
                    {
                        continue;
                    }
                    if (TypesEqual(fieldOwner, owner) && fieldStatic == isStatic
                        && TypesEqual(declaredType, fieldType))
                    {
                        backingField = accessor.FieldSymbol;
                        return true;
                    }
                }
            }
            if (BilVerificationContext.TryParseMethodSymbol(currentFunction,
                    out var fnOwner, out _, out _, out _))
            {
                var name = MethodNameOf(currentFunction);
                if ((name == "setValue" || name == "getValue") && IsCellTypeRef(fnOwner))
                {
                    backingField = fnOwner + "#value@" + fieldType;
                    return true;
                }
            }
            throw new VmException("..value 只能出现在 setter 上下文：" + fieldSymbol);
        }

        // 类型名（去命名空间 / 泛型实参）是否为 cell 隐藏子类
        internal static bool IsCellTypeRef(string typeRef)
        {
            var name = typeRef;
            var generic = name.IndexOf('<');
            if (generic >= 0)
            {
                name = name.Substring(0, generic);
            }
            var sep = name.LastIndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
            {
                name = name.Substring(sep + 2);
            }
            return name.StartsWith("..cell..", StringComparison.Ordinal);
        }

        // 字段声明上的 wrapped(W) 应用标记，按声明序 outer→inner 收集
        // （§8.3.1：可重复出现，顺序即派发链嵌套序；无标记返回空列表）
        public IReadOnlyList<string> CollectWrappedWrappers(string fieldSymbol)
        {
            var field = FindField(fieldSymbol);
            var result = new List<string>();
            if (field == null)
            {
                return result;
            }
            foreach (var modifier in field.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    result.Add(wrapped.WrapperTypeRef);
                }
            }
            return result;
        }

        // 类型声明上的 wrapped(W) 应用标记（Entity wrapper），按声明序
        // outer→inner 收集（§8.3.1：与字段 Value wrapper 标记同源，挂类型声明）
        public IReadOnlyList<string> CollectEntityWrappers(string typeRef)
        {
            var result = new List<string>();
            var declaration = FindType(typeRef);
            if (declaration == null)
            {
                return result;
            }
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    result.Add(wrapped.WrapperTypeRef);
                }
            }
            return result;
        }

        // 字段符号的简单名（Host#name@T → name），用于 .proxy.get.<名>/.
        // proxy.set.<名> 的 specific proxy 命中
        public static string FieldSimpleName(string fieldSymbol)
        {
            var hash = fieldSymbol.IndexOf('#');
            var at = fieldSymbol.LastIndexOf('@');
            if (hash < 0 || at <= hash)
            {
                return "";
            }
            var name = fieldSymbol.Substring(hash + 1, at - hash - 1);
            if (name.StartsWith(".static.", StringComparison.Ordinal))
            {
                name = name.Substring(".static.".Length);
            }
            return name;
        }

        // 找 value wrapper 的 proxy 模板 fn 符号（.proxy.get / .proxy.set；无则 null）。
        // 仅匹配带 wrapper-proxy 修饰符的成员，防与普通同名方法误判
        public string? FindWrapperProxy(string wrapperType, string proxyName)
        {
            var declaration = FindType(wrapperType);
            if (declaration == null)
            {
                return null;
            }
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || simple.Kind != BilMemberKind.Method)
                {
                    continue;
                }
                if (MethodNameOf(simple.Symbol) != proxyName)
                {
                    continue;
                }
                foreach (var modifier in simple.Modifiers)
                {
                    if (modifier is BilWrapperProxyModifier)
                    {
                        return simple.Symbol;
                    }
                }
            }
            return null;
        }

        // 当前 fn 是否为给定字段所属类型的 init（声明带 Init 关键字修饰）。
        // 构造期一次性赋值豁免 proxy 链（§14.3 / §21.8：cell 子类的
        // init(value) 会写带 wrapped 标记的 value 字段，只带 get proxy 的
        // wrapper 也必须能初始化 const/ReadonlyCell）。
        // 新 init 原则（§9.7 修订）：编译器合成的构造期写入方法族
        // （..init.wrapper / ..init.field.*）同样豁免——子类合成方法写
        // 继承字段（字段 owner 为基类）是缝合协议的常态，不按 owner 限定
        public bool IsInitFunctionOf(string functionSymbol, string fieldOwner)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(functionSymbol,
                    out var owner, out _, out _, out _))
            {
                return false;
            }
            var name = MethodNameOf(functionSymbol);
            if (name == BilSpellings.InitWrapperMethodName
                || name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (!TypesEqual(owner, fieldOwner)
                && BilVerificationContext.StripTypeArguments(owner)
                    != BilVerificationContext.StripTypeArguments(fieldOwner))
            {
                return false;
            }
            if (!_members.TryGetValue(functionSymbol, out var member))
            {
                return false;
            }
            foreach (var modifier in member.Modifiers)
            {
                if (modifier is BilKeywordModifier keyword && keyword.Keyword == BilKeyword.Init)
                {
                    return true;
                }
            }
            return false;
        }

        public string? FindIndexOperator(string collectionType, bool isGet)
        {
            var operatorName = isGet ? "getAtIndex" : "setAtIndex";
            var needle = "$$" + operatorName + "(";
            var declaration = FindType(collectionType);
            var hosts = new HashSet<string>();
            var current = declaration;
            while (current != null)
            {
                hosts.Add(current.Symbol);
                if (current.ExtendsType == null)
                {
                    break;
                }
                current = FindType(current.ExtendsType);
            }
            hosts.Add(StripTypeArguments(collectionType));
            foreach (var member in _members.Values)
            {
                if (!member.Symbol.Contains(needle))
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(member.Symbol,
                        out var owner, out _, out _, out _))
                {
                    continue;
                }
                if (hosts.Contains(owner) || hosts.Contains(StripTypeArguments(owner)))
                {
                    return member.Symbol;
                }
            }
            return null;
        }

        public IReadOnlyList<BilSimpleMemberDeclaration> CollectInstanceFields(string typeRef)
        {
            var fields = new List<BilSimpleMemberDeclaration>();
            var seen = new HashSet<string>();
            var current = FindType(typeRef);
            while (current != null)
            {
                foreach (var member in current.Members)
                {
                    if (member is not BilSimpleMemberDeclaration simple
                        || simple.Kind != BilMemberKind.Field)
                    {
                        continue;
                    }
                    if (seen.Add(simple.Symbol))
                    {
                        fields.Add(simple);
                    }
                }
                if (current.ExtendsType == null)
                {
                    break;
                }
                current = FindType(current.ExtendsType);
            }
            var owner = StripTypeArguments(typeRef);
            foreach (var field in _fields.Values)
            {
                if (field.Kind != BilMemberKind.Field)
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out var fieldOwner, out var isStatic, out _))
                {
                    continue;
                }
                if (isStatic || fieldOwner != owner || !seen.Add(field.Symbol))
                {
                    continue;
                }
                fields.Add(field);
            }
            return fields;
        }

    }
}
