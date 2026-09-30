using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 构造类型收集（MW5 c2-a）：从已过门禁的 BilModule 收集需物化的
    /// 构造类型 canonical（MwTypeKey.Normalize 归一），传递闭包 + 环保护。
    /// 来源：new/new.case/new.array 的 Type；new.indirect 实参静态类型
    ///（argSheets 物化需要 sheet）；fn .vars/.args；getid.type /
    /// getid.var 操作数静态类型与结果类型、cast 目标、type.is/supers/with
    /// 静态目标；get.array/set.array 元素类型；构造类型代入后的
    /// extends/implements；构造实参内层递归。.typeid<X> 按构造收集（不擦除）。
    /// </summary>
    public static class ConstructedTypeCollector
    {
        private const int MaxConstructedTypes = 4096;
        private const int MaxConstructedDepth = 32;
        public static IReadOnlyList<string> Collect(MwContext context)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            var queue = new Queue<string>();
            foreach (var typeRef in ConstructedCallCollector.Collect(context))
                Enqueue(typeRef, seen, order, queue);

            foreach (var function in context.Module.Functions)
            {
                var locals = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var arg in function.Args)
                {
                    locals[arg.Name] = arg.TypeRef;
                    Enqueue(arg.TypeRef, seen, order, queue);
                }
                foreach (var varDecl in function.Vars)
                {
                    locals[varDecl.Name] = varDecl.TypeRef;
                    Enqueue(varDecl.TypeRef, seen, order, queue);
                }
                foreach (var block in function.Blocks)
                {
                    WalkBlock(block, locals, seen, order, queue);
                }
            }

            // 非泛型类 implements I<i32>：接口构造不经 fn 局部也会出现
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                if (type.Declaration.ExtendsType is { } extends)
                {
                    Enqueue(extends, seen, order, queue);
                }
                foreach (var iface in type.Declaration.ImplementsTypes)
                {
                    Enqueue(iface, seen, order, queue);
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var typeArguments = TypeArgumentsOf(current);
                for (var argumentIndex = 0; argumentIndex < typeArguments.Count; argumentIndex++)
                {
                    // fieldid 的第三项是 instance/static 模式，不是类型参数。
                    if (TypeLayout.IsFieldIdCanonical(current) && argumentIndex >= 2) break;
                    var argument = typeArguments[argumentIndex];
                    Enqueue(argument, seen, order, queue);
                    // 泛型体可把隐藏实参 TypeId 装箱为 Any（例如 typeOf(T)）。
                    // 即使接收者经接口擦去了静态类型，其真实实参仍需对应的
                    // 不变 Type<T> 元数据。仅为已闭合构造的实参建视图；
                    // Type 本身不再递归引入 Type<Type<...>>。
                    if (GenericAbi.IsClosedConstructed(current)
                        && !TypeLayout.IsTypeIdCanonical(current))
                    {
                        Enqueue(".typeid<" + argument + ">", seen, order, queue);
                    }
                }
                var template = context.Symbols.FindTypeByRef(current);
                if (template == null)
                {
                    continue;
                }
                var substitution = BuildSubstitution(current, template.Declaration);
                if (template.Declaration.ExtendsType is { } extends)
                {
                    Enqueue(Substitute(extends, substitution), seen, order, queue);
                }
                foreach (var iface in template.Declaration.ImplementsTypes)
                {
                    Enqueue(Substitute(iface, substitution), seen, order, queue);
                }
            }

            // ---- b4-2 typeof 装箱视图完备性 ----
            // getid.var 的运行时结果是「操作数值的实际类型」X 的 TypeSheet*；
            // 该 Type<X> 值再装箱为 Any（typeOf(v).toString()、存入 Any 槽、
            // 传 Any 形参）时，BoxEmitter 的 TypeId 装箱路径
            // （ResolveTypeIdViewSheet）需要 `.typeid<X>` 视图 sheet 做身份
            // 选择，否则运行时抛 CastException（VM 符号侧无此问题）。X 是
            // 运行期身份，静态收集只看得见 getid.var 操作数的声明类型
            // （.any），无法靠使用点枚举——保守取全量：全部已收集构造
            // 类型 + 全部非 External 声明类型（非泛型按裸符号，泛型具化已
            // 在构造清单）+ 内建标量/String。X 自身为 .typeid<...> 的不递归
            // （Type<Type<...>> 不具化，与泛型实参视图规则同）；含开放
            // 占位 (.generic<) 的跳过；`.typeid<.null>` 已按 getid.var 使用
            // 点在上方登记。
            // 守卫：只有 X 自身 sheet 保证存在时才补视图（镜像
            // ConstructedLayout 的具化判定）——视图表 emission 要写
            // typeIdBound（指向 X 的 sheet），X 无 sheet 会在
            // ModuleBuilder.Build 以「闭合 TypeSheet 缺失」响亮失败
            // （Middleware 手写 BIL 用例实证：order 里可有永不具化的类型）。
            foreach (var x in order.ToArray())
            {
                if (!GuaranteedSheet(context, x))
                {
                    continue;
                }
                Enqueue(".typeid<" + x + ">", seen, order, queue);
            }
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal
                    || type.Declaration.GenericParameters.Count != 0
                    || TypeLayout.IsTypeIdCanonical(type.Canonical))
                {
                    continue;
                }
                Enqueue(".typeid<" + type.Canonical + ">", seen, order, queue);
            }
            foreach (var builtin in BuiltinScalarAndStringRefs)
            {
                Enqueue(".typeid<" + builtin + ">", seen, order, queue);
            }

            Logger.Verbose("Middleware", $"构造类型收集 {order.Count} 项");
            return order;
        }

        // typeof 装箱视图需要覆盖的内建标量/String（BIL 别名形态，与
        // BuiltinSheetCanonical 对译；装箱标量经 Any 槽流过 getid.var 时
        // 其视图必须已在布局计划中）。
        private static readonly string[] BuiltinScalarAndStringRefs =
        {
            ".bool", ".char", ".i8", ".u8", ".i16", ".u16",
            ".i32", ".u32", ".i64", ".u64", ".f32", ".f64", ".string",
        };

        // X 的布局 sheet 是否保证存在（b4-2 typeof 视图补充的守卫）：
        // 镜像 ConstructedLayout 各类具化/合成路径的准入条件——Nullable /
        // Span / SharedSpan / TypeId 走合成路径恒出；其余闭合构造须模板
        // 可解析且通过 ShouldMaterialize。不含开放占位（调用方另查）。
        // 内建 Array 的闭合具化例外：ShouldMaterialize 有意排除 Array
        // 模板（数组走内建数组布局，不经模板 plan），但其 sheet 由同一
        // 内建路径对每个闭合实参恒出——`.typeid<X>` 视图必须同样覆盖，
        // 否则运行时对已物化数组的 typeOf 装箱（isSbRepresentable /
        // typeOf(v).toString()）会因视图缺失抛 CastException。视图收集
        // 不得依赖无关表达式里的 getid.type 使用点碰巧覆盖。
        private static bool GuaranteedSheet(MwContext context, string canonical)
        {
            if (canonical.IndexOf(".generic<", StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            if (BilVerificationContext.StripTypeArguments(canonical)
                == TypeLayout.ArrayTypeCanonical)
            {
                return true;
            }
            if (TypeLayout.IsTypeIdCanonical(canonical)
                || BilVerificationContext.StripTypeArguments(canonical)
                    == TypeLayout.NullableTypeCanonical
                || TypeLayout.IsSpanCanonical(canonical)
                || TypeLayout.IsSharedSpanCanonical(canonical))
            {
                return GenericAbi.IsClosedConstructed(canonical);
            }
            return GenericAbi.IsClosedConstructed(canonical)
                && context.Symbols.FindTypeByRef(canonical) is { IsExternal: false } template
                && GenericAbi.ShouldMaterialize(template);
        }

        // 是否为需入队的构造类型（.generic 占位不是构造类型）
        public static bool IsConstructed(string typeRef)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            var angle = normalized.IndexOf('<');
            if (angle < 0 || !normalized.EndsWith(">", StringComparison.Ordinal))
            {
                return false;
            }
            var head = normalized.Substring(0, angle);
            return !head.Equals(".generic", StringComparison.Ordinal);
        }

        public static IReadOnlyList<string> TypeArgumentsOf(string typeRef)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            var angle = normalized.IndexOf('<');
            if (angle < 0 || !normalized.EndsWith(">", StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }
            return BilVerificationContext.SplitTopLevel(
                normalized.Substring(angle + 1, normalized.Length - angle - 2));
        }

        // 构造实参代入模板签名（VM BuildSubstitution 同语义）
        public static Dictionary<string, string>? BuildSubstitution(string typeRef,
            BilTypeDeclaration declaration)
        {
            if (declaration.GenericParameters.Count == 0)
            {
                return null;
            }
            var arguments = TypeArgumentsOf(typeRef);
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

        public static string Substitute(string typeRef, Dictionary<string, string>? substitution)
        {
            if (substitution == null
                || !typeRef.Contains(".generic<", StringComparison.Ordinal))
            {
                return typeRef;
            }
            // 单次匹配替换，避免 Dictionary 迭代顺序与替换值中的占位符
            // 触发二次污染。
            return Regex.Replace(typeRef,
                @"\.generic<(?:\$\.generic\.)?([^<>]+)>", match =>
                    substitution.TryGetValue(match.Groups[1].Value, out var value)
                        ? value : match.Value,
                RegexOptions.CultureInvariant);
        }

        private static void Enqueue(string? typeRef, HashSet<string> seen, List<string> order,
            Queue<string> queue)
        {
            if (string.IsNullOrEmpty(typeRef) || !IsConstructed(typeRef))
            {
                return;
            }
            var key = MwTypeKey.Normalize(typeRef);
            var depth = 0;
            var maximum = 0;
            foreach (var ch in key)
            {
                if (ch == '<') maximum = Math.Max(maximum, ++depth);
                else if (ch == '>') depth--;
            }
            if (depth != 0 || maximum > MaxConstructedDepth)
            {
                throw new MwNotSupportedException(
                    $"构造类型嵌套超过上限 {MaxConstructedDepth}: {key}");
            }
            if (!seen.Add(key))
            {
                return;
            }
            if (order.Count >= MaxConstructedTypes)
            {
                throw new MwNotSupportedException(
                    $"模块构造类型数量超过上限 {MaxConstructedTypes}");
            }
            order.Add(key);
            queue.Enqueue(key);
        }

        private static void WalkBlock(BilBlock block, Dictionary<string, string> locals,
            HashSet<string> seen, List<string> order, Queue<string> queue)
        {
            var blocks = new Stack<BilBlock>();
            var visited = new HashSet<BilBlock>(ReferenceEqualityComparer.Instance);
            blocks.Push(block);
            while (blocks.Count > 0)
            {
                var current = blocks.Pop();
                if (!visited.Add(current)) continue;
                foreach (var inst in current.Instructions)
                {
                    switch (inst)
                    {
                        case NewInstruction n:
                            Enqueue(n.Type.TypeRef, seen, order, queue);
                            break;
                        case NewIndirectInstruction n:
                            foreach (var argument in n.Arguments)
                            {
                                Enqueue(Lookup(locals, argument.Name), seen, order, queue);
                            }
                            break;
                        case NewCaseInstruction n:
                            Enqueue(n.Type.TypeRef, seen, order, queue);
                            break;
                        case NewWrappedInstruction n:
                            Enqueue(n.Type.TypeRef, seen, order, queue);
                            break;
                        case GetIdTypeInstruction g:
                            Enqueue(g.TargetType.TypeRef, seen, order, queue);
                            break;
                        case GetIdVarInstruction g:
                            Enqueue(Lookup(locals, g.Value.Name), seen, order, queue);
                            // 可空胖值的实际身份可为 .null；Type<Nullable<T>>
                            // 不能冒充 Type<null>，仍须登记独立精确视图。
                            Enqueue(".typeid<.null>", seen, order, queue);
                            break;
                        case CastInstruction c:
                            Enqueue(c.TargetType.TypeRef, seen, order, queue);
                            break;
                        case DirectTypeCheckInstruction check:
                            Enqueue(check.TargetType.TypeRef, seen, order, queue);
                            break;
                        case TryInstruction t:
                            if (t.CatchTable is BilCatchTableResource catchTable)
                                foreach (var entry in catchTable.Entries)
                                    Enqueue(entry.ExceptionType.TypeRef, seen, order, queue);
                            break;
                        case GetArrayInstruction g:
                            Enqueue(ElementTypeOf(locals, g.Array.Name), seen, order, queue);
                            Enqueue(Lookup(locals, g.Target.Name), seen, order, queue);
                            break;
                        case SetArrayInstruction s:
                            Enqueue(ElementTypeOf(locals, s.Collection.Name), seen, order, queue);
                            Enqueue(Lookup(locals, s.Element.Name), seen, order, queue);
                            break;
                    }
                    foreach (var child in BilVerifier.ReferencedBlocks(inst)) blocks.Push(child);
                }
            }
        }

        private static string? Lookup(Dictionary<string, string> locals, string name) =>
            locals.TryGetValue(name, out var typeRef) ? typeRef : null;

        private static string? ElementTypeOf(Dictionary<string, string> locals, string arrayName)
        {
            if (!locals.TryGetValue(arrayName, out var arrayType))
            {
                return null;
            }
            var args = TypeArgumentsOf(arrayType);
            return args.Count > 0 ? args[0] : null;
        }
    }
}
