using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // GenericArguments 职责；与主文件共享同一类型、字段及生命周期。

        // 类级 .generic.* 未出现在 invoke 实参时，按 .this 构造形态的实参
        // 位序补齐（与 EmitFunction 外层类型参数在前、方法自有在后一致）。
        public static IReadOnlyList<VmValue> AlignGenericHiddenArgs(BilFunction function,
            IReadOnlyList<VmValue> arguments, VmContext? context = null)
        {
            var slots = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return") slots.Add(arg);
            }
            if (arguments.Count == slots.Count) return arguments;

            var genericSlots = new List<BilArgDeclaration>();
            var hasThis = false;
            var ordinaryCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    hasThis = true;
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    genericSlots.Add(slot);
                    continue;
                }
                ordinaryCount++;
            }
            if (genericSlots.Count == 0) return arguments;

            var thisOffset = hasThis ? 1 : 0;
            if (arguments.Count < thisOffset) return arguments;
            var restCount = arguments.Count - thisOffset;
            if (restCount < ordinaryCount) return arguments;
            var passedHidden = restCount - ordinaryCount;
            if (passedHidden < 0 || passedHidden > genericSlots.Count) return arguments;

            var inferred = new List<string>();
            if (hasThis && arguments.Count > 0)
            {
                var typeArgs = TypeArgsOf(arguments[0].TypeRef);
                if (typeArgs != null) inferred.AddRange(typeArgs);
            }

            // 嵌套类修正（review-20260910 #02）：.this 构造实参只覆盖类型
            // 自身的 GP（.type Ring.RingEnum = class generic(TItem)），帧槽
            // 却按「外层宿主链 → 自身 → 方法级」排列——位置直灌会把自身
            // 实参错绑到外层槽。有类型声明可查时改按名绑定：自身槽 ← 实例
            // 实参，外层槽 ← 构造点捕获（New 写入实例隐藏槽），其余（方法
            // 级/未捕获）走既有降级
            Dictionary<string, string>? ownBindings = null;
            HashSet<string>? ownNames = null;
            HashSet<string>? outerNames = null;
            List<string>? outerCaptured = null;
            if (context != null && hasThis && arguments.Count > 0 && inferred.Count > 0)
            {
                var declaration = context.FindType(arguments[0].TypeRef);
                if (declaration != null
                    && declaration.GenericParameters.Count == inferred.Count
                    && declaration.GenericParameters.Count < genericSlots.Count
                    && BilVerificationContext.TryParseMethodSymbol(function.Symbol,
                        out var ownerSymbol, out _, out _, out _))
                {
                    ownBindings = new Dictionary<string, string>(StringComparer.Ordinal);
                    ownNames = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < inferred.Count; i++)
                    {
                        ownBindings[declaration.GenericParameters[i]] = inferred[i];
                        ownNames.Add(declaration.GenericParameters[i]);
                    }
                    outerNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var name in OuterGenericParametersOf(context, ownerSymbol))
                    {
                        outerNames.Add(name);
                    }
                    if (arguments[0] is IVmFieldHost fieldHost
                        && fieldHost.TryReadHidden(HiddenOuterGenericsKey, out var captured)
                        && captured is VmArray capturedArray)
                    {
                        outerCaptured = new List<string>(capturedArray.Length);
                        for (var i = 0; i < capturedArray.Length; i++)
                        {
                            outerCaptured.Add(
                                capturedArray.GetAt(i) is VmString text ? text.Value : "");
                        }
                    }
                }
            }

            var result = new List<VmValue>(slots.Count);
            var restIndex = thisOffset;
            var inferredIndex = 0;
            var hiddenConsumed = 0;
            var outerIndex = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    result.Add(arguments[0]);
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    var genericName = slot.Name.Substring(".generic.".Length);
                    if (passedHidden == genericSlots.Count)
                    {
                        // 调用点显式传足隐藏 typeid：恒按位置直灌
                        result.Add(arguments[restIndex++]);
                    }
                    else if (ownBindings != null && ownNames!.Contains(genericName))
                    {
                        // 类型自身 GP：按名绑定实例构造实参
                        result.Add(new VmTypeId(ownBindings[genericName]));
                    }
                    else if (ownBindings != null && outerNames!.Contains(genericName))
                    {
                        // 外层宿主 GP：读构造点捕获；未捕获/未绑定回落 .any
                        var capturedText = "";
                        if (outerCaptured != null && outerIndex < outerCaptured.Count)
                        {
                            capturedText = outerCaptured[outerIndex];
                        }
                        outerIndex++;
                        result.Add(new VmTypeId(capturedText.Length > 0 ? capturedText : ".any"));
                    }
                    else if (ownBindings != null)
                    {
                        // 方法级 GP：调用点显式传的隐藏 typeid 按序消费
                        // （Repo<T>.mix<U> 只显式传 U 的份额），无传才降级 .any
                        if (restIndex < arguments.Count
                            && arguments[restIndex] is VmTypeId)
                        {
                            result.Add(arguments[restIndex++]);
                        }
                        else
                        {
                            result.Add(new VmTypeId(".any"));
                        }
                    }
                    else if (inferredIndex < inferred.Count
                        && hiddenConsumed < genericSlots.Count - passedHidden)
                    {
                        result.Add(new VmTypeId(inferred[inferredIndex++]));
                    }
                    else if (restIndex < arguments.Count
                        && arguments[restIndex] is VmTypeId)
                    {
                        result.Add(arguments[restIndex++]);
                    }
                    else if (inferredIndex < inferred.Count)
                    {
                        result.Add(new VmTypeId(inferred[inferredIndex++]));
                    }
                    else
                    {
                        result.Add(new VmTypeId(".any"));
                    }
                    hiddenConsumed++;
                    continue;
                }
                if (restIndex < arguments.Count)
                {
                    result.Add(arguments[restIndex++]);
                }
                else
                {
                    return arguments;
                }
            }
            return result;
        }

        // 按 fn .args 序在 .this 之后插入推断的 .generic.* typeid，
        // 使 PushFrame 实参个数与泛型 operator 签名对齐。
        public IReadOnlyList<VmValue> InjectOperatorTypeIds(string methodSymbol,
            IReadOnlyList<VmValue> args)
        {
            var function = FindFunction(methodSymbol);
            if (function == null) return args;
            var slots = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return") slots.Add(arg);
            }
            var genericCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    genericCount++;
                }
            }
            if (genericCount == 0) return args;

            var thisCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this") thisCount++;
            }
            var ordinaryArgs = new List<VmValue>();
            for (var i = thisCount; i < args.Count; i++)
            {
                ordinaryArgs.Add(args[i]);
            }
            var bindings = InferGenericBindings(function, ordinaryArgs);
            var result = new List<VmValue>(slots.Count);
            var ordinaryIndex = 0;
            var thisIndex = 0;
            foreach (var slot in slots)
            {
                if (slot.Name == ".this")
                {
                    result.Add(thisIndex < args.Count ? args[thisIndex++] : VmNull.Instance);
                    continue;
                }
                if (slot.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    var name = slot.Name.Substring(".generic.".Length);
                    result.Add(new VmTypeId(bindings.TryGetValue(name, out var typeRef)
                        ? typeRef : ".any"));
                    continue;
                }
                if (slot.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || slot.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                result.Add(ordinaryIndex < ordinaryArgs.Count
                    ? ordinaryArgs[ordinaryIndex++] : VmNull.Instance);
            }
            return result;
        }

        private static Dictionary<string, string> InferGenericBindings(BilFunction function,
            IReadOnlyList<VmValue> ordinaryArgs)
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            var ordinary = new List<BilArgDeclaration>();
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return" || arg.Name == ".this"
                    || arg.Name.StartsWith(".generic.", StringComparison.Ordinal)
                    || arg.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || arg.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(arg);
            }
            for (var i = 0; i < ordinary.Count && i < ordinaryArgs.Count; i++)
            {
                UnifyTypeRef(ordinary[i].TypeRef, ordinaryArgs[i].TypeRef, bindings);
            }
            return bindings;
        }

        private static void UnifyTypeRef(string pattern, string actual,
            Dictionary<string, string> bindings)
        {
            if (TryParseGenericPlaceholder(pattern, out var name))
            {
                if (!bindings.ContainsKey(name)) bindings[name] = actual;
                return;
            }
            var patternArgs = TypeArgsOf(pattern);
            var actualArgs = TypeArgsOf(actual);
            if (patternArgs == null || actualArgs == null
                || patternArgs.Count != actualArgs.Count)
            {
                return;
            }
            var patternHead = BilVerificationContext.NormalizeTypeRef(
                BilVerificationContext.StripTypeArguments(pattern));
            var actualHead = BilVerificationContext.NormalizeTypeRef(
                BilVerificationContext.StripTypeArguments(actual));
            if (patternHead != actualHead) return;
            for (var i = 0; i < patternArgs.Count; i++)
            {
                UnifyTypeRef(patternArgs[i], actualArgs[i], bindings);
            }
        }

        private static bool TryParseGenericPlaceholder(string typeRef, out string name)
        {
            name = "";
            const string hidden = ".generic<$.generic.";
            if (typeRef.StartsWith(hidden, StringComparison.Ordinal) && typeRef.EndsWith(">"))
            {
                name = typeRef.Substring(hidden.Length, typeRef.Length - hidden.Length - 1);
                return name.Length > 0 && name.IndexOf('<') < 0;
            }
            const string shortForm = ".generic<";
            if (typeRef.StartsWith(shortForm, StringComparison.Ordinal) && typeRef.EndsWith(">"))
            {
                name = typeRef.Substring(shortForm.Length, typeRef.Length - shortForm.Length - 1);
                return name.Length > 0 && name.IndexOf('<') < 0 && !name.StartsWith("$");
            }
            return false;
        }

        private static List<string>? TypeArgsOf(string typeRef)
        {
            var angle = typeRef.IndexOf('<');
            if (angle < 0 || !typeRef.EndsWith(">")) return null;
            return SplitTopLevelArgs(typeRef.Substring(angle + 1, typeRef.Length - angle - 2));
        }

        private static List<string> SplitTopLevelArgs(string inner)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(inner.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            if (start <= inner.Length)
            {
                var last = inner.Substring(start).Trim();
                if (last.Length > 0) parts.Add(last);
            }
            return parts;
        }

    }
}
