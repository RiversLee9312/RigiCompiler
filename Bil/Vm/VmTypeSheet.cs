using System.Text;

namespace RigiCompiler.Bil.Vm
{
    // 逻辑 TypeSheet（RUNTIME §6–§9 的加载期拍平等价物）：每个类型声明一张
    // sheet，按声明 key 缓存。槽序 = 继承槽 → 自有槽 → 各接口段（§7）；克隆
    // 构建天然保证「继承中相同方法保持相同 offset」；接口派发 = iMap
    // （InterfaceBase）查段基址 + 接口内相对 offset（§8），拍平后派生 sheet
    // 自带继承槽与 iMap，调用期不上溯（§9）。本抽象不模拟物理位布局
    //（BIL_VM_DESIGN §1），只承载派发语义。

    // 单个虚槽：同一 SignatureKey 的 override 链在派生 sheet 中共享同一 offset
    internal sealed class VmSlot
    {
        // 归一化签名（名称 + 参数类型序列 + 返回类型；访问器 .get.名@T/.set.名@T）
        public string SignatureKey { get; }
        // 当前 sheet 宿主视角的代入形态符号（构造泛型基类/接口的实参已代入；
        // 仅供派生 sheet 克隆时再代入重算 SignatureKey，不参与派发寻址）
        public string SubstitutedSymbol { get; }
        // 基类链最根源声明符号（诊断用；恒为声明级原样，不做实参代入）
        public string SlotSymbol { get; }
        // 本类型视角下该槽的实现 fn 符号；null = 无体抽象（调用时才报错）
        public string? ImplSymbol { get; set; }

        public VmSlot(string signatureKey, string substitutedSymbol, string slotSymbol,
            string? implSymbol)
        {
            SignatureKey = signatureKey;
            SubstitutedSymbol = substitutedSymbol;
            SlotSymbol = slotSymbol;
            ImplSymbol = implSymbol;
        }
    }

    internal sealed class VmTypeSheet
    {
        // 拍平 vtable（§7 序：继承槽 → 自有槽 → 各接口段）
        public List<VmSlot> Slots { get; } = new List<VmSlot>();
        // 声明级方法/访问器符号 → offset（自有 + 继承拷贝 + 接口成员符号）
        public Dictionary<string, int> OffsetBySymbol { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
        // iMap（§8）：接口声明 key → 接口段 base offset（拍平：含继承来的条目）
        public Dictionary<string, int> InterfaceBase { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    internal static class VmTypeSheetBuilder
    {
        // 声明 key：canonical 符号 + 泛型元数（与 VmContext 类型索引同式）
        internal static string TypeKeyOf(BilTypeDeclaration declaration)
        {
            return declaration.Symbol + "`" + declaration.GenericParameters.Count;
        }

        // 成员符号 → 归一化签名键：剥 owner 前缀的签名段，参数名剔除、泛型
        // 参数按出现序归一为位置占位 #n、内建别名归一为 canonical——override
        // 双方的参数名/泛型参数名差异不影响匹配（SYNTAX §9.2.1 严格签名匹配）
        internal static string SignatureKeyOf(string symbol)
        {
            var dollar = symbol.IndexOf('$');
            if (dollar < 0)
            {
                return symbol;
            }
            var rest = ReplaceGenericPlaceholders(symbol.Substring(dollar + 1));
            if (rest.StartsWith(".static.", StringComparison.Ordinal))
            {
                return rest;
            }
            var open = rest.IndexOf('(');
            if (open < 0)
            {
                // 访问器形态 .get.名@T / .set.名@T（@T 对 getter 是返回类型、
                // 对 setter 是 value 参数类型；getter/setter 是独立多态单元）
                var at = rest.LastIndexOf('@');
                return at < 0 ? rest
                    : rest.Substring(0, at + 1) + NormalizeSegment(rest.Substring(at + 1));
            }
            var close = FindMatchingParen(rest, open);
            if (close < 0 || close + 1 >= rest.Length || rest[close + 1] != '@')
            {
                return rest;
            }
            var parameterTypes = new List<string>();
            foreach (var part in BilVerificationContext.SplitTopLevel(
                rest.Substring(open + 1, close - open - 1)))
            {
                var colon = part.IndexOf(':');
                parameterTypes.Add(NormalizeSegment(
                    colon < 0 ? part.Trim() : part.Substring(colon + 1).Trim()));
            }
            return rest.Substring(0, open) + "(" + string.Join(",", parameterTypes)
                + ")@" + NormalizeSegment(rest.Substring(close + 2));
        }

        // 构建 T 的拍平 sheet（记忆化由 VmContext.SheetOf 承担；沿 extends
        // 递归，building 记录在建链——环抛带类型名的 VmException）
        internal static VmTypeSheet Build(VmContext context, BilTypeDeclaration type,
            HashSet<string> building)
        {
            var key = TypeKeyOf(type);
            if (!building.Add(key))
            {
                throw new VmException("类型继承环，无法构建 TypeSheet：" + key);
            }
            var sheet = new VmTypeSheet();
            // 1. 继承槽：以基类 sheet 的克隆起步（§7 offset 不变量 + §9 拍平；
            //    基声明缺失（core::Exception 等预定义根不进符号段）按空基降级）。
            //    构造泛型基类（D : B<.i32>，含转发形态 D<T2> : B<T2>）：克隆时
            //    把基类槽的代入源按 extends 实参映射 {T_i → arg_i} 代入再重新
            //    归一化 SignatureKey（m(x:#0) → m(x:.i32)），D 里 override
            //    m(x: i32) 才能匹配替换而非追加新槽；SlotSymbol/ImplSymbol 与
            //    OffsetBySymbol 键保持声明级原样（派发时静态符号恒为声明级）
            if (type.ExtendsType != null)
            {
                var baseDeclaration = context.FindType(type.ExtendsType);
                if (baseDeclaration != null)
                {
                    var baseSheet = Build(context, baseDeclaration, building);
                    var substitution = BuildSubstitution(type.ExtendsType, baseDeclaration);
                    foreach (var slot in baseSheet.Slots)
                    {
                        var source = SubstituteGenericArguments(slot.SubstitutedSymbol,
                            substitution);
                        sheet.Slots.Add(new VmSlot(SignatureKeyOf(source), source,
                            slot.SlotSymbol, slot.ImplSymbol));
                    }
                    foreach (var pair in baseSheet.OffsetBySymbol)
                    {
                        sheet.OffsetBySymbol[pair.Key] = pair.Value;
                    }
                    foreach (var pair in baseSheet.InterfaceBase)
                    {
                        sheet.InterfaceBase[pair.Key] = pair.Value;
                    }
                }
            }
            // 2. 自有槽：有体成员替换全部同签名槽的实现（含继承来的接口段槽
            //    ——derived 重 override 必须同步接口槽）；无体（abstract 再声明）
            //    保持原实现；新签名追加槽
            foreach (var member in type.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || !IsVirtualMember(simple))
                {
                    continue;
                }
                var signatureKey = SignatureKeyOf(simple.Symbol);
                var impl = context.FindFunction(simple.Symbol) != null
                    ? simple.Symbol : null;
                var offset = -1;
                for (var i = 0; i < sheet.Slots.Count; i++)
                {
                    if (sheet.Slots[i].SignatureKey != signatureKey)
                    {
                        continue;
                    }
                    if (impl != null)
                    {
                        sheet.Slots[i].ImplSymbol = impl;
                    }
                    if (offset < 0)
                    {
                        offset = i;
                    }
                }
                if (offset < 0)
                {
                    sheet.Slots.Add(new VmSlot(signatureKey, simple.Symbol, simple.Symbol,
                        impl));
                    offset = sheet.Slots.Count - 1;
                }
                sheet.OffsetBySymbol[simple.Symbol] = offset;
            }
            // 3. 接口段：每个新接口占一段（InterfaceBase 记基址，§8）；槽 impl
            //    取类侧最派生实现 ?? 兼容回退 ?? 接口默认体 ?? null（无体接口
            //    成员 + 类未实现 = null，调用时才报错——verifier 保证具体类必实现）
            foreach (var interfaceRef in type.ImplementsTypes)
            {
                var interfaceDeclaration = context.FindType(interfaceRef);
                if (interfaceDeclaration == null)
                {
                    continue;
                }
                var interfaceKey = TypeKeyOf(interfaceDeclaration);
                if (sheet.InterfaceBase.ContainsKey(interfaceKey))
                {
                    continue;
                }
                sheet.InterfaceBase[interfaceKey] = sheet.Slots.Count;
                // 构造泛型接口（C : IFace<.i32>）：接口成员签名按 implements
                // 实参代入后再与类侧方法匹配（I<T>.m(x: T) → m(x:.i32)，
                // 与类侧 override 同 key 直中，不再依赖名字兼容回退）
                var interfaceSubstitution = BuildSubstitution(interfaceRef,
                    interfaceDeclaration);
                foreach (var member in interfaceDeclaration.Members)
                {
                    if (member is not BilSimpleMemberDeclaration simple
                        || !IsVirtualMember(simple))
                    {
                        continue;
                    }
                    var source = SubstituteGenericArguments(simple.Symbol,
                        interfaceSubstitution);
                    var signatureKey = SignatureKeyOf(source);
                    var impl = FindImplBySignature(sheet.Slots, signatureKey)
                        ?? FindCompatibleImpl(sheet.Slots, simple.Symbol)
                        ?? (context.FindFunction(simple.Symbol) != null
                            ? simple.Symbol : null);
                    sheet.Slots.Add(new VmSlot(signatureKey, source, simple.Symbol, impl));
                    sheet.OffsetBySymbol[simple.Symbol] = sheet.Slots.Count - 1;
                }
            }
            building.Remove(key);
            return sheet;
        }

        // vtable 成员资格：实例方法/访问器；排除 static 符号、init、ext（均不
        // 参与多态，SYNTAX §9.2.1）与除 $$call 外的运算符（$$ 双美元形态；
        // callable 协议例外：operator call 可 abstract/override，入表）
        private static bool IsVirtualMember(BilSimpleMemberDeclaration member)
        {
            if (member.Kind != BilMemberKind.Method)
            {
                return false;
            }
            if (VmContext.HasKeyword(member, BilKeyword.Init)
                || VmContext.HasKeyword(member, BilKeyword.Ext))
            {
                return false;
            }
            var dollar = member.Symbol.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            var rest = member.Symbol.Substring(dollar + 1);
            if (rest.StartsWith(".static.", StringComparison.Ordinal))
            {
                return false;
            }
            if (rest.Length > 0 && rest[0] == '$')
            {
                return rest.StartsWith("$call(", StringComparison.Ordinal);
            }
            return true;
        }

        // extends/implements 构造形态 → 代入映射 {泛型参数名 → 实参文本}
        // （B<.i32> + B 的 GenericParameters[0]="T" → {T → .i32}；转发形态
        // D<T2> : B<T2> 的实参是 .generic<$.generic.T2>，代入后经归一化仍
        // 归一为同序占位）。非构造形态/元数不齐（异常模块）返回 null。
        // internal：TryFindInit / super init 的运行期 init 匹配复用同一
        // 代入机制（定义级 init 签名 + 构造实参代入）
        internal static Dictionary<string, string>? BuildSubstitution(string typeRef,
            BilTypeDeclaration declaration)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">", StringComparison.Ordinal)
                || declaration.GenericParameters.Count == 0)
            {
                return null;
            }
            var arguments = BilVerificationContext.SplitTopLevel(
                typeRef.Substring(angle + 1, typeRef.Length - angle - 2));
            if (arguments.Count != declaration.GenericParameters.Count)
            {
                return null;
            }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < arguments.Count; i++)
            {
                map[declaration.GenericParameters[i]] = arguments[i];
            }
            return map;
        }

        // 符号文本按映射代入泛型占位（.generic<$.generic.T>/.generic<T> →
        // 实参文本）；null 映射原样返回。替换串以 '>' 收尾，参数名互为前缀
        // （T 与 T2）不会误伤；嵌套实参（.array<.i32>）按文本落入，归一化
        // 由 SignatureKeyOf 的 NormalizeSegment 完成。
        // internal：与 BuildSubstitution 同为运行期 init 匹配的复用点
        internal static string SubstituteGenericArguments(string symbol,
            Dictionary<string, string>? substitution)
        {
            if (substitution == null
                || !symbol.Contains(".generic<", StringComparison.Ordinal))
            {
                return symbol;
            }
            var result = symbol;
            foreach (var pair in substitution)
            {
                result = result.Replace(".generic<$.generic." + pair.Key + ">",
                    pair.Value, StringComparison.Ordinal);
                result = result.Replace(".generic<" + pair.Key + ">",
                    pair.Value, StringComparison.Ordinal);
            }
            return result;
        }

        // 同签名且已有实现的槽（类侧最派生实现）
        private static string? FindImplBySignature(List<VmSlot> slots, string signatureKey)
        {
            foreach (var slot in slots)
            {
                if (slot.SignatureKey == signatureKey && slot.ImplSymbol != null)
                {
                    return slot.ImplSymbol;
                }
            }
            return null;
        }

        // 泛型接口的实现是代入形态（I<T>.m(x: T) vs C.m(x: i32)），签名键不能
        // 直接匹配——按「名称 + 声明参数个数」回退（与旧名匹配派发等价的防御层）
        internal static string? FindCompatibleImpl(List<VmSlot> slots, string memberSymbol)
        {
            var name = VmContext.MethodNameOf(memberSymbol);
            var count = ParameterCountOf(memberSymbol);
            foreach (var slot in slots)
            {
                if (slot.ImplSymbol == null)
                {
                    continue;
                }
                if (VmContext.MethodNameOf(slot.SlotSymbol) == name
                    && ParameterCountOf(slot.SlotSymbol) == count)
                {
                    return slot.ImplSymbol;
                }
            }
            return null;
        }

        private static int ParameterCountOf(string symbol)
        {
            return BilVerificationContext.TryParseMethodSymbol(symbol,
                out _, out _, out var parameters, out _)
                ? parameters.Count : 0;
        }

        // .generic<$[.generic.]名> → #n（按名字首次出现序编号；override 双方
        // 的泛型参数名差异归一为位置占位）
        private static string ReplaceGenericPlaceholders(string text)
        {
            if (!text.Contains(".generic<", StringComparison.Ordinal))
            {
                return text;
            }
            var builder = new StringBuilder();
            var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 0;
            while (index < text.Length)
            {
                var hit = text.IndexOf(".generic<", index, StringComparison.Ordinal);
                if (hit < 0)
                {
                    builder.Append(text, index, text.Length - index);
                    break;
                }
                builder.Append(text, index, hit - index);
                var close = text.IndexOf('>', hit);
                if (close < 0)
                {
                    builder.Append(text.Substring(hit));
                    break;
                }
                var name = text.Substring(hit + ".generic<".Length,
                    close - hit - ".generic<".Length);
                if (name.StartsWith("$", StringComparison.Ordinal))
                {
                    name = name.Substring(1);
                }
                if (name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    name = name.Substring(".generic.".Length);
                }
                if (!placeholders.TryGetValue(name, out var placeholder))
                {
                    placeholder = "#" + placeholders.Count;
                    placeholders[name] = placeholder;
                }
                builder.Append(placeholder);
                index = close + 1;
            }
            return builder.ToString();
        }

        // 类型段归一（占位 #n 原样；其余经 NormalizeTypeRef 归一到 canonical）
        private static string NormalizeSegment(string typeRef)
        {
            return typeRef.Length == 0 || typeRef[0] == '#'
                ? typeRef
                : BilVerificationContext.NormalizeTypeRef(typeRef);
        }

        private static int FindMatchingParen(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '(')
                {
                    depth++;
                }
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
            return -1;
        }
    }
}
