namespace RigiCompiler
{
    public sealed partial class SymbolGraph
    {
        private readonly List<(GenericParameterSymbol[] Parameters, SemanticSymbol[] Arguments)> genericUses = new();
        private readonly AsyncLocal<GenericUseCapture?> genericUseCapture = new();
        internal sealed class GenericUseCapture : IDisposable
        {
            private readonly SymbolGraph graph;
            private readonly GenericUseCapture? previous;
            internal readonly List<(GenericParameterSymbol[] Parameters, SemanticSymbol[] Arguments)> Uses = new();
            internal GenericUseCapture(SymbolGraph graph)
            {
                this.graph = graph;
                previous = graph.genericUseCapture.Value;
                graph.genericUseCapture.Value = this;
            }
            public void Dispose() => graph.genericUseCapture.Value = previous;
            internal void Merge()
            {
                foreach (var (parameters, arguments) in Uses) graph.RecordGenericUse(parameters, arguments);
            }
        }
        internal GenericUseCapture CaptureGenericUses() => new(this);

        internal void RecordGenericUse(IReadOnlyList<GenericParameterSymbol> parameters,
            IReadOnlyList<SemanticSymbol> arguments)
        {
            if (genericUseCapture.Value is { } job)
            {
                if (parameters.Count != 0 && parameters.Count == arguments.Count)
                    job.Uses.Add((parameters.ToArray(), arguments.ToArray()));
                return;
            }
            lock (constructionGate)
            {
            if (parameters.Count == 0 || parameters.Count != arguments.Count) return;
            if (genericUses.Any(u => u.Parameters.SequenceEqual(parameters) && u.Arguments.SequenceEqual(arguments))) return;
            genericUses.Add((parameters.ToArray(), arguments.ToArray()));

            }
        }

        // 泛型函数体中的 List<Array<T>> 不一定出现在调用方的显式类型标注中。
        // 按实际调用的整组代入关系收集闭合形状，不对每个参数做笛卡尔积猜测。
        internal void MaterializeGenericUseTypes(Action<string> error)
        {
            lock (constructionGate)
            {
            // 只以源码已经出现的类型/代入边作为模板。新驻留的结果不能
            // 反过来成为新的声明边，否则未调用的 Box<T>.grow():Box<Box<T>>
            // 也会凭空扩张成无限链。真正的调用可达性仍由后续调用收集负责。
            var patterns = ConstructedTypeSnapshot();
            foreach (var type in patterns)
                if (Closed(type)) RecordGenericUse(type.ConstructedFrom!.GenericParameters, type.TypeArguments!);
            var originalUses = genericUses.ToArray();
            var paths = genericUses.Select(_ => new HashSet<int>()).ToList();
            var processed = new HashSet<int>();
            for (var round = 0; round < 64; round++)
            {
                var progress = false;
                for (var i = 0; i < genericUses.Count; i++)
                {
                    if (genericUses.Count > 4096 || constructedTypes.Count > 16384)
                    {
                        error("泛型类型收集超出资源预算");
                        return;
                    }
                    var use = genericUses[i];
                    if (!use.Arguments.All(Closed) || !processed.Add(i)) continue;
                    progress = true;
                    var substitution = use.Parameters.Select((p, at) => (p, value: use.Arguments[at]))
                        .ToDictionary(pair => pair.p, pair => pair.value);
                    SemanticSymbol Apply(SemanticSymbol type)
                    {
                        if (type is GenericParameterSymbol parameter)
                            return substitution.TryGetValue(parameter, out var value) ? value : parameter;
                        if (type is not TypeSymbol { ConstructedFrom: { } definition, TypeArguments: { } args }) return type;
                        return GetConstructedType(definition, args.Select(Apply).ToArray());
                    }
                    foreach (var type in patterns)
                        if (!Closed(type)) Apply(type);
                    for (var edge = 0; edge < originalUses.Length; edge++)
                    {
                        var other = originalUses[edge];
                        if (other.Arguments.All(Closed) || paths[i].Contains(edge)) continue;
                        var before = genericUses.Count;
                        RecordGenericUse(other.Parameters, other.Arguments.Select(Apply).ToArray());
                        if (genericUses.Count != before)
                            paths.Add(new HashSet<int>(paths[i]) { edge });
                    }
                }
                if (!progress) return;
            }
            error("泛型类型收集超过最大代入深度");

            }
        }

        private static bool Closed(SemanticSymbol symbol) => symbol is TypeSymbol type
            && (type.ConstructedFrom == null ? type.GenericParameters.Count == 0
                : type.TypeArguments!.All(Closed));
    }
}
