namespace RigiCompiler
{
    // 值类型布局环拒绝（P18/S2 配套，SYNTAX §10）：struct/enum struct/wrapper
    // 的实例字段按值内嵌——值类型字段闭包必须有限，否则布局无限大
    //（运行期零填充/拷贝无限递归）。自包含（struct Box { var next: Box }）
    // 与互包含（A↔B，含经泛型实参代入的环）在声明点拒绝；经引用类型或
    // Nullable\<T>（Object 分支）字段打断的环合法（布局在引用处截止）。
    //
    // 算法：以每个用户值类型定义为根做构造类型级 DFS——节点是构造类型
    //（SymbolGraph 驻留，引用相等即身份）或定义本身，边是「实例字段类型
    // 按宿主实参代入后仍属值类型分支」的字段；目标与栈上节点同布局身份
    //（同定义且任一侧为定义级、或构造引用相等）即环。环路径按定义名
    // 旋转规范化去重（A↔B 从两个根各发现一次，只报一条）。泛型实参
    // 发散链以深度上限兜底（纯防御，实际代码不会出现）。
    internal sealed class LayoutCycleChecker : ResolverVisitor<LayoutCycleChecker>
    {
        private const int MaxDepth = 64;

        protected override void VisitCore(ResolveEnvironment env)
        {
            var reported = new HashSet<string>();
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                // 定义级条目即可（构造类型的环由定义级 DFS 代入展开发现）
                if (type.ConstructedFrom != null || !IsCheckableValueType(type)) continue;
                var stack = new List<TypeSymbol>();
                var visited = new HashSet<TypeSymbol>();
                Expand(type, null, 0, entry, stack, visited, reported, env);
            }
        }

        private static bool IsCheckableValueType(TypeSymbol type)
        {
            return !type.IsBuiltin && type.IsValueTypeBranch
                && type.Kind is TypeKind.Struct or TypeKind.EnumStruct or TypeKind.Wrapper;
        }

        // 展开一个布局节点：逐实例字段找值类型边，递归下钻；命中栈即报环
        private static void Expand(TypeSymbol node, FieldSymbol? viaField, int depth,
            DeclEntry rootEntry, List<TypeSymbol> stack, HashSet<TypeSymbol> visited,
            HashSet<string> reported, ResolveEnvironment env)
        {
            if (depth > MaxDepth) return;   // 发散链防御性截断
            if (!visited.Add(node)) return; // 已完整展开过的节点不重走
            stack.Add(node);
            foreach (var (field, fieldType) in FieldClosureChecker.ClosureFieldsOf(node,
                env.Unit.Symbols))
            {
                if (field.IsStatic) continue;
                // 计算属性无存储，不占布局
                if ((field.Getter != null || field.Setter != null) && !field.HasBackingStorage)
                {
                    continue;
                }
                if (fieldType is not TypeSymbol target || target is ErrorTypeSymbol) continue;
                var targetDef = target.ConstructedFrom ?? target;
                if (!IsCheckableValueType(targetDef)) continue;
                var hit = stack.FindIndex(s => SameLayoutIdentity(s, target));
                if (hit >= 0)
                {
                    Report(stack, hit, target, field, rootEntry, reported, env);
                    continue;
                }
                Expand(target, field, depth + 1, rootEntry, stack, visited, reported, env);
            }
            stack.RemoveAt(stack.Count - 1);
        }

        // 布局身份：同一定义且任一侧为定义级（定义代表其全部构造的布局
        // 形状），或两侧为同一驻留构造（引用相等）
        private static bool SameLayoutIdentity(TypeSymbol stackNode, TypeSymbol candidate)
        {
            var stackDef = stackNode.ConstructedFrom ?? stackNode;
            var candidateDef = candidate.ConstructedFrom ?? candidate;
            if (!ReferenceEquals(stackDef, candidateDef)) return false;
            return stackNode.ConstructedFrom == null || candidate.ConstructedFrom == null
                || ReferenceEquals(stackNode, candidate);
        }

        // 报环：路径 = 栈上环段 + 闭合目标；按定义名旋转规范化去重。
        // 诊断锚点取闭合边的字段声明（取不到回落根类型声明）
        private static void Report(List<TypeSymbol> stack, int hit, TypeSymbol closing,
            FieldSymbol viaField, DeclEntry rootEntry, HashSet<string> reported,
            ResolveEnvironment env)
        {
            var cycle = stack.Skip(hit).Select(DefinitionOf).ToList();
            cycle.Add(DefinitionOf(closing));
            var names = cycle.Select(t => t.Name).ToList();
            // 旋转规范化：去掉重复闭合端后按最小名旋转，再补回闭合端
            var body = names.Take(names.Count - 1).ToList();
            var min = body.IndexOf(body.Min()!);
            var rotated = body.Skip(min).Concat(body.Take(min)).ToList();
            rotated.Add(rotated[0]);
            var path = string.Join(" -> ", rotated);
            if (!reported.Add(path)) return;
            var span = env.EntryOfSymbol.TryGetValue(viaField, out var fieldEntry)
                ? fieldEntry.Node.Span : rootEntry.Node.Span;
            env.Error(span,
                $"Value-type layout cycle: {path} — struct '{rotated[0]}' embeds itself " +
                $"by value through field '{viaField.Name}'; break the cycle with a reference " +
                "type or Nullable field (§10)");
        }

        private static TypeSymbol DefinitionOf(TypeSymbol type) => type.ConstructedFrom ?? type;
    }
}
