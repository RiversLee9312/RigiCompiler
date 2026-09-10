using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    // 只沿实际调用/构造点传播具化实参，不因出现 Box<A> 就扫描 Box 的全部成员。
    // 共享函数体内的隐藏类仍需独立闭合布局；类型依赖与机器代码可达性分开收集。
    internal sealed class ConstructedCallCollector
    {
        private const int MaxAnalysisFrames = 10_000;
        private const int MaxCallCombinations = 4096;
        private sealed record Frame(string Key, BilFunction Function, Dictionary<string, string> Substitution,
            Dictionary<string, string> Arguments);
        private readonly MwContext context;
        private readonly Dictionary<string, BilFunction> functions;
        private readonly Queue<Frame> queue = new();
        private readonly HashSet<string> visited = new(StringComparer.Ordinal);
        private readonly List<Frame> frames = new();
        private readonly Dictionary<string, HashSet<(string Type, string? Id)>> fields = new(StringComparer.Ordinal);
        private bool storageChanged;
        private readonly HashSet<string> types = new(StringComparer.Ordinal);
        private LayoutPlanTable? dispatchPlan;
        // 纯类型闭包可以独立验证继承环；只有真正查询调用槽时才要求完整布局。
        private LayoutPlanTable dispatch => dispatchPlan ??= LayoutEngine.Build(context.Symbols,
            Array.Empty<string>(), bodies, HiddenStoragePlanner.CollectMethodSlots(context.Module));
        private readonly HashSet<string> bodies;

        private ConstructedCallCollector(MwContext context)
        {
            this.context = context;
            functions = context.Module.Functions.ToDictionary(f => f.Symbol, StringComparer.Ordinal);
            bodies = new(functions.Keys, StringComparer.Ordinal);
        }

        internal static IEnumerable<string> Collect(MwContext context)
        {
            var collector = new ConstructedCallCollector(context);
            foreach (var fn in context.Module.Functions)
            {
                var member = context.Symbols.FindMember(fn.Symbol);
                if (member?.HasKeyword(BilKeyword.Entrypoint) == true
                    || fn.Symbol.StartsWith("$..globals.init(", StringComparison.Ordinal)
                    || (member?.Owner is { } owner && SingletonPlanner.IsSingleton(owner)
                        && member.HasKeyword(BilKeyword.Init)))
                    collector.Enqueue(fn, new(), new());
            }
            do
            {
                collector.storageChanged = false;
                while (collector.queue.TryDequeue(out var frame)) collector.Visit(frame);
                // 构造函数可能在读取字段的调用者之后分析；共享存储新增事实后重算已达帧。
                if (collector.storageChanged)
                    foreach (var frame in collector.frames) collector.queue.Enqueue(frame);
            } while (collector.queue.Count != 0);
            return collector.types;
        }

        private string Enqueue(BilFunction function, Dictionary<string, string> substitution,
            Dictionary<string, string> arguments)
        {
            var key = function.Symbol + "|" + string.Join(";", substitution.OrderBy(p => p.Key)
                .Select(p => p.Key + "=" + p.Value)) + "|" + string.Join(";", arguments.OrderBy(p => p.Key)
                .Select(p => p.Key + "=" + p.Value));
            if (visited.Add(key))
            {
                if (frames.Count >= MaxAnalysisFrames)
                    throw new MwNotSupportedException(
                        $"具化调用分析帧超过上限 {MaxAnalysisFrames}");
                var frame = new Frame(key, function, substitution, arguments);
                frames.Add(frame);
                queue.Enqueue(frame);
            }
            return key;
        }

        private string Resolve(string reference, Dictionary<string, string> substitution)
        {
            var resolved = MwTypeKey.Normalize(ConstructedTypeCollector.Substitute(reference, substitution));
            if (GenericAbi.IsClosedConstructed(resolved)) types.Add(resolved);
            return resolved;
        }

        private void Visit(Frame frame)
        {
            CollectColdTaskBody(frame);
            var locals = new Dictionary<string, string>(StringComparer.Ordinal);
            var facts = new Dictionary<string, HashSet<(string Type, string? Id)>>(StringComparer.Ordinal);
            var changed = true;
            foreach (var arg in frame.Function.Args) locals[arg.Name] = Resolve(arg.TypeRef, frame.Substitution);
            foreach (var local in frame.Function.Vars) locals[local.Name] = Resolve(local.TypeRef, frame.Substitution);
            var declared = new Dictionary<string, string>(locals, StringComparer.Ordinal);
            foreach (var pair in frame.Arguments) Assign(pair.Key, (pair.Value, null));
            foreach (var pair in frame.Substitution) Assign(".generic." + pair.Key, (".typeid", pair.Value));
            // BIL 的块声明顺序不是执行顺序；事实只增不减，回边与分支合流都求不动点。
            // 未调用的成员不会进入帧队列，不以类型出现推导成员体可达。
            while (changed)
            {
                changed = false;
                foreach (var block in frame.Function.Blocks)
                foreach (var inst in block.Instructions)
                {
                    switch (inst)
                    {
                        case RetInstruction { Value: { } returned }:
                            Store("return:" + frame.Key, returned.Name);
                            break;
                        case GetIdTypeInstruction id:
                            Assign(id.Target.Name, (locals[id.Target.Name], Resolve(id.TargetType.TypeRef, frame.Substitution)));
                            break;
                        case GetIdVarInstruction id:
                            // typeOf(Any) 取的是实参的动态身份，不能只登记 Type<Any>。
                            // 沿同一调用数据流收集精确视图；装箱仍要求真实 sheet 匹配。
                            foreach (var value in Values(id.Value.Name))
                            {
                                var view = Resolve(".typeid<" + value.Type + ">", frame.Substitution);
                                Assign(id.Target.Name, (view, value.Type));
                            }
                            break;
                        case SetVarInstruction copy:
                            Copy(copy.Source.Name, copy.Target.Name);
                            break;
                        case GetVarInstruction copy:
                            Copy(copy.Source.Name, copy.Target.Name);
                            break;
                        case SetFieldInstruction field:
                            foreach (var receiver in Values(field.Object.Name))
                                Store(receiver.Type + "|" + field.Field.Symbol, field.Source.Name);
                            break;
                        case SetFieldStaticInstruction field:
                            Store(field.Field.Symbol, field.Source.Name);
                            break;
                        case GetFieldInstruction field:
                            if (FieldAccessor(field.Field.Symbol, BilAccessorKind.Getter) is { } getter)
                            {
                                Invoke(getter.Canonical, new[] { field.Object }, field.Target.Name);
                                break;
                            }
                            foreach (var receiver in Values(field.Object.Name))
                                Read(receiver.Type + "|" + field.Field.Symbol, field.Target.Name);
                            break;
                        case GetFieldStaticInstruction field:
                            Read(field.Field.Symbol, field.Target.Name);
                            break;
                        case CastInstruction cast:
                            var target = Resolve(cast.TargetType.TypeRef, frame.Substitution);
                            // 向上投影不改变实际对象类型，接口/lambda 基类调用也需保留宿主实参。
                            foreach (var source in Values(cast.Source.Name))
                            {
                                // Any 可经容器/外部接口流转后才被 typeOf 消费。
                                // 在装箱点登记真实视图，不能要求消费者还能追溯
                                // 每一个存储槽；这只补充元数据，不放宽转换关系。
                                if (MirType.Of(target).IsAny)
                                    Resolve(".typeid<" + source.Type + ">", frame.Substitution);
                                var sourceSymbol = context.Symbols.FindTypeByRef(source.Type);
                                var preserves = source.Type == target
                                    || MirType.Of(target).IsAny
                                    || (sourceSymbol?.Declaration.Kind == BilTypeKind.Class
                                        && (MirType.Of(target).IsAnyOrObject
                                            || IsNominalUpcast(source.Type, target, new(StringComparer.Ordinal))));
                                Assign(cast.Target.Name, preserves ? source : (target, null));
                            }
                            break;
                        case NewInstruction creation:
                            Construct(creation.Type.TypeRef, creation.Arguments, creation.Target.Name);
                            break;
                        case NewWrappedInstruction creation:
                            Construct(creation.Type.TypeRef, creation.InitArguments, creation.Target.Name, creation.WrapperArguments);
                            break;
                        case InvokeInstruction call:
                            Invoke(call.Method.Symbol, call.Arguments, call.Target.Name);
                            break;
                        case InvokeNoResultInstruction call:
                            Invoke(call.Method.Symbol, call.Arguments, null);
                            break;
                        case InvokeIndirectInstruction call:
                            Indirect(call.CallTarget, call.Arguments, call.Target.Name);
                            break;
                        case InvokeIndirectNoResultInstruction call:
                            Indirect(call.CallTarget, call.Arguments, null);
                            break;
                    }
                }
            }

            (string Type, string? Id)[] Values(string name) => facts.TryGetValue(name, out var values)
                ? values.ToArray() : new[] { (locals[name], (string?)null) };

            MwMemberSymbol? FieldAccessor(string fieldName, BilAccessorKind kind)
            {
                // 访问器稍后才降级为调用；沿与降级相同的查询追踪其闭合构造。
                var current = context.Symbols.FindMember(frame.Function.Symbol);
                var field = context.Symbols.FindMember(fieldName);
                if (current == null || field == null
                    || Passes.AccessorLoweringPass.AccessorRules.IsCurrentOf(current, fieldName)
                    || Passes.AccessorLoweringPass.AccessorRules.IsFieldWrapped(field)
                    || Passes.AccessorLoweringPass.AccessorRules.IsHostWrapped(field)) return null;
                return ImplBinder.FindAccessor(context.Symbols, fieldName, kind, current.Canonical);
            }

            void Assign(string name, (string Type, string? Id) value)
            {
                if (!facts.TryGetValue(name, out var values)) facts[name] = values = new();
                changed |= values.Add(value);
            }

            IEnumerable<(string Type, string? Id)[]> Actuals(IReadOnlyList<BilVariableOperand> args)
            {
                var choices = args.Select(arg => Values(arg.Name)).ToArray();
                long count = 1;
                foreach (var values in choices)
                {
                    count = Math.Min(MaxCallCombinations + 1L, count * values.Length);
                }
                if (count <= MaxCallCombinations)
                {
                    IEnumerable<(string Type, string? Id)[]> combinations =
                        new[] { Array.Empty<(string Type, string? Id)>() };
                    foreach (var values in choices)
                        combinations = combinations.SelectMany(prefix => values
                            .Select(value => prefix.Append(value).ToArray())).ToArray();
                    return combinations;
                }
                // 组合爆炸时退化为逐参数独立扰动：保留每个参数的全部
                // 候选传播，但不再构造笛卡尔积。
                var baseline = choices.Select(values => values[0]).ToArray();
                var reduced = new List<(string Type, string? Id)[]> { baseline };
                for (var i = 0; i < choices.Length; i++)
                foreach (var value in choices[i].Skip(1))
                {
                    var variant = baseline.ToArray();
                    variant[i] = value;
                    reduced.Add(variant);
                }
                return reduced;
            }

            void Copy(string source, string target)
            {
                foreach (var value in Values(source)) Assign(target, value);
            }

            void Store(string field, string source)
            {
                if (!fields.TryGetValue(field, out var values)) fields[field] = values = new();
                foreach (var value in Values(source)) storageChanged |= values.Add(value);
            }

            void Read(string field, string target)
            {
                if (fields.TryGetValue(field, out var values))
                    foreach (var value in values) Assign(target, value);
            }

            void Construct(string reference, IReadOnlyList<BilVariableOperand> args, string target,
                IReadOnlyList<BilVariableOperand>? wrapperArgs = null)
            {
                var typeRef = Resolve(reference, frame.Substitution);
                Assign(target, (typeRef, null));
                if (typeRef.Contains(".generic<", StringComparison.Ordinal)
                    || context.Symbols.FindTypeByRef(typeRef) is not { } type
                    || type.IsExternal || TypeLayout.IsArray(MirType.Of(typeRef))) return;
                if (args.Count != 0 || SingletonPlanner.HasInitMember(type))
                {
                    var init = MirBuilder.ResolveInit(context.Symbols, type,
                        args.Select(a => declared[a.Name]).ToArray(), 0, typeRef);
                    foreach (var actual in Actuals(args))
                        Call(init.Canonical, new[] { typeRef }.Concat(actual.Select(a => a.Type)).ToArray(),
                            new string?[] { null }.Concat(actual.Select(a => a.Id)).ToArray());
                }
                var wrapper = MirBuilder.FindInitWrapper(context.Symbols, type, wrapperArgs?.Count ?? 0);
                if (wrapper != null)
                    foreach (var actual in Actuals(wrapperArgs ?? Array.Empty<BilVariableOperand>()))
                        Call(wrapper.Canonical, new[] { typeRef }.Concat(actual.Select(a => a.Type)).ToArray(),
                            new string?[] { null }.Concat(actual.Select(a => a.Id)).ToArray());
            }

            void Invoke(string symbol, IReadOnlyList<BilVariableOperand> args, string? target)
            {
                foreach (var actual in Actuals(args))
                {
                    var result = Call(symbol, actual.Select(a => a.Type).ToArray(), actual.Select(a => a.Id).ToArray());
                    if (target != null && result != null)
                        foreach (var returned in result) Assign(target, (returned, null));
                }
            }

            void Indirect(BilVariableOperand receiver, IReadOnlyList<BilVariableOperand> args, string? target)
            {
                var binding = ImplBinder.BindIndirectCall(context.Symbols, declared[receiver.Name],
                    args.Select(a => declared[a.Name]).ToArray(),
                    target == null ? null : declared[target], context.Module.Functions);
                Invoke(binding.CallOperator.Canonical, new[] { receiver }.Concat(args).ToArray(), target);
            }
        }

        // 冷 Task 的默认 bindColdBody 体由 CoroutineSplit 替换为 body 的调用。
        // 类型闭包先于该改写，因此须沿已到达 Task 的实际 body 存储事实继续
        // 分析；不能枚举所有 callable，也不能把未启动 Task 的全部成员当作可达。
        private void CollectColdTaskBody(Frame frame)
        {
            if (frame.Function.Symbol is not ("core.coroutine::Task$bindColdBody()@.void"
                or "core.coroutine::Task<TReturn>$bindColdBody()@.void")
                || !frame.Arguments.TryGetValue(".this", out var receiver)) return;
            var owner = context.Symbols.FindMember(frame.Function.Symbol)?.Owner;
            // 成员符号保留声明参数（Task<TReturn>），类型 Canonical 则为
            // 裸名；不能用裸名拼字段，否则只追踪到非泛型 Task 的 body。
            var memberOwner = frame.Function.Symbol[..frame.Function.Symbol.IndexOf('$')];
            var body = owner?.Members.FirstOrDefault(m =>
                m.Canonical.StartsWith(memberOwner + "#body@", StringComparison.Ordinal));
            if (body == null || !fields.TryGetValue(receiver + "|" + body.Canonical, out var values)) return;
            foreach (var value in values.ToArray())
            {
                var binding = ImplBinder.BindIndirectCall(context.Symbols, value.Type,
                    Array.Empty<string>(), receiver, context.Module.Functions);
                Call(binding.CallOperator.Canonical, new[] { value.Type }, new[] { value.Id });
            }
        }

        private TypeLayoutPlan? Plan(string type) =>
            ConstructedLayout.ResolveConstructed(type, context.Symbols, dispatch, new(StringComparer.Ordinal), bodies)
            ?? dispatch.Find(context.Symbols.FindTypeByRef(type)?.Canonical ?? type);

        // 数据流中的向上转换保留实际实现类型。按具化继承图比较，不能用
        // 裸模板布局表查询闭合类型，否则工厂返回接口会丢失实现类事实。
        private bool IsNominalUpcast(string source, string target, HashSet<string> seen)
        {
            source = MwTypeKey.Normalize(source);
            target = MwTypeKey.Normalize(target);
            if (source == target) return true;
            if (!seen.Add(source) || seen.Count > 64
                || context.Symbols.FindTypeByRef(source) is not { } type) return false;
            var map = ConstructedTypeCollector.BuildSubstitution(source, type.Declaration);
            if (type.Declaration.ExtendsType is { } parent
                && IsNominalUpcast(ConstructedTypeCollector.Substitute(parent, map), target, seen))
                return true;
            return type.Declaration.ImplementsTypes.Any(iface =>
                IsNominalUpcast(ConstructedTypeCollector.Substitute(iface, map), target, seen));
        }

        private IReadOnlyList<string>? Call(string symbol, string[] actual, string?[] ids)
        {
            if (actual.Length > 0 && context.Symbols.FindMember(symbol) is { Owner: { } dispatchOwner } target
                && ImplBinder.BindCall(target) is VirtualCallBinding or InterfaceCallBinding)
            {
                var plan = Plan(actual[0]);
                var ownerSlots = dispatch.GetVTableSlots(dispatchOwner.Canonical);
                var slot = ownerSlots?.ToList().IndexOf(target.Canonical) ?? -1;
                if (plan != null && slot >= 0)
                {
                    if (dispatchOwner.Declaration.Kind == BilTypeKind.Interface)
                    {
                        foreach (var (iface, offset) in plan.IMap)
                            if (context.Symbols.FindTypeByRef(iface)?.Declaration == dispatchOwner.Declaration)
                            { symbol = plan.VTableSlots[offset + slot]; break; }
                    }
                    else
                    {
                        // 闭合计划不驻留于仅模板的 dispatch 表；沿该计划自己的
                        // BasePlan 查槽所属声明，避免对抽象方法停止可达性分析。
                        for (var inherited = plan; inherited != null; inherited = inherited.BasePlan)
                        {
                            if (context.Symbols.FindTypeByRef(inherited.Symbol.Canonical)?.Declaration
                                != dispatchOwner.Declaration) continue;
                            symbol = plan.VTableSlots[slot];
                            break;
                        }
                    }
                }
            }
            if (!functions.TryGetValue(symbol, out var function)) return null;
            var member = context.Symbols.FindMember(symbol);
            var substitution = new Dictionary<string, string>(StringComparer.Ordinal);
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            if (member?.Owner is { } owner && actual.Length > 0
                && function.Args.Any(a => a.Name == ".this"))
            {
                var host = actual[0];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (seen.Add(host) && context.Symbols.FindTypeByRef(host) is { } hostType)
                {
                    var map = ConstructedTypeCollector.BuildSubstitution(host, hostType.Declaration);
                    if (hostType.Declaration == owner.Declaration)
                    {
                        if (map != null) foreach (var pair in map) substitution[pair.Key] = pair.Value;
                        break;
                    }
                    if (hostType.Declaration.ExtendsType is not { } parent) break;
                    host = MwTypeKey.Normalize(ConstructedTypeCollector.Substitute(parent, map));
                }
            }
            var index = 0;
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return" || GenericAbi.IsClassLevelTypeId(member, arg.Name)) continue;
                if (index >= actual.Length) break;
                if (arg.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    if (ids[index] is { } id) substitution[arg.Name[".generic.".Length..]] = id;
                }
                else arguments[arg.Name] = actual[index];
                index++;
            }
            // 不完整开放上下文不凭同名参数猜测；其已知静态类型仍由原收集器保留。
            if (function.Args.Any(a => a.Name.StartsWith(".generic.", StringComparison.Ordinal)
                && !substitution.ContainsKey(a.Name[".generic.".Length..]))) return null;
            var frameKey = Enqueue(function, substitution, arguments);
            var result = function.Args.FirstOrDefault(a => a.Name == ".return").TypeRef;
            if (result == null) return null;
            // 工厂返回抽象接口时仍须传播实际实现类型，供后续虚调用收集
            // 其闭合布局；返回事实增加后与字段事实一样重跑已达帧。
            if (fields.TryGetValue("return:" + frameKey, out var returned))
                return returned.Select(value => value.Type).ToArray();
            return new[] { Resolve(result, substitution) };
        }
    }
}
