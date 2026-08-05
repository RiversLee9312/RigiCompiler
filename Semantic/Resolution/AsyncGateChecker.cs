namespace LatteCompiler
{
    // ===== async 声明侧检查 + 边界闸门 2/3/5（SYNTAX §4.5，S8f）=====
    //
    // async 调用把一批值从当前协程送进新协程，以下类型必须是 §3.1.1
    // 共享安全类型（shared class / shared rich struct / wrapper、非 rich
    // ValueType、T 共享安全的 Nullable\<T>）：
    //   1. receiver（实例/扩展方法 this）——调用点检查（P3，AsyncGates）
    //   2. 参数（含默认/具名/可变展开后的每个实参）——声明处查声明类型 +
    //     调用点查实际类型（P3）
    //   3. 返回值（Task 结果类型 TResult）——声明处
    //   4. 捕获变量（async lambda）——lambda 处（P3）
    //   5. 泛型实参（typeid 与实际值一同跨边界）——声明处查约束边界 +
    //     调用点查实际实参（实际实参归 S9——泛型调用使用侧尚未落地，
    //     调用点检查随 S9 补）
    // 违反为编译错误，不存在运行时补救。
    //
    // 声明侧检查点（"在 async 声明处检查 2、3、5 的声明类型"）。错误类型
    // 毒化静默（P2 诊断已报）；泛型参数类型无法静态判定（归 S9，约束边界
    // 例外——见下）；void 返回合法。
    //
    // 随附 async 修饰符合法性收口（§9.2："async 仅适用于函数与 lambda"）：
    // init/operator/getter/setter 上的 async 在此拒绝（ModifierChecker 只
    // 拦了字段——本阶段补 init/operator 与类型声明；访问器块修饰符白名单
    // 已由 AccessorChecker 拦）。
    internal sealed class AsyncGateChecker : ResolverVisitor<AsyncGateChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                var modifiers = ResolveEnvironment.ModifiersOf(entry.Node);
                if (!modifiers.Contains(Keywords.ASYNC)) continue;
                if (entry.Symbol is not MethodSymbol method)
                {
                    // 字段上的 async 已由 ModifierChecker 拒绝（§9.2），
                    // 此处补类型声明（ModifierChecker 的类型检查无 async 行）
                    if (entry.Symbol is TypeSymbol)
                    {
                        env.Error(entry.Node.Span, "'async' can only be applied to functions");
                    }
                    continue;
                }
                // async 仅普通函数（§9.2：init/operator 不是函数）
                if (method.Kind != MethodKind.Regular)
                {
                    env.Error(entry.Node.Span, "'async' can only be applied to functions");
                    continue;
                }
                CheckGateParameters(method, entry, env);
                CheckGateResult(method, entry, env);
                CheckGateTypeArguments(method, entry, env);
            }
        }

        // 闸门 2（参数）：每个形参的声明类型必须共享安全。
        // 泛型参数（TSource 形态）跳过——其实际类型由调用点闸门约束（S9）
        private static void CheckGateParameters(MethodSymbol method, DeclEntry entry,
            ResolveEnvironment env)
        {
            foreach (var parameter in method.Parameters)
            {
                if (parameter.Type is not TypeSymbol type || type is ErrorTypeSymbol) continue;
                if (!type.IsSharedSafe())
                {
                    env.Error(entry.Node.Span,
                        $"Parameter '{parameter.Name}' of async function " +
                        $"'{method.Name}' must be a shared-safe type: " +
                        $"'{type.Name}'");
                }
            }
        }

        // 闸门 3（返回值）：TResult 必须共享安全；void（null）合法；
        // 泛型参数跳过（同闸门 2）
        private static void CheckGateResult(MethodSymbol method, DeclEntry entry,
            ResolveEnvironment env)
        {
            if (method.ReturnType is not TypeSymbol type || type is ErrorTypeSymbol) return;
            if (!type.IsSharedSafe())
            {
                env.Error(entry.Node.Span,
                    $"Return type '{type.Name}' of async function '{method.Name}' " +
                    "must be a shared-safe type");
            }
        }

        // 闸门 5（泛型实参，声明侧）：泛型参数的约束边界必须共享安全——
        // 具化泛型下 typeid 与实际值一同跨边界，类型实参必然落在约束界内，
        // 约束界非共享安全 ⇒ 一切调用都违反（早诊断）；无约束的泛型参数
        // 由调用点实际实参检查兜底（归 S9）
        private static void CheckGateTypeArguments(MethodSymbol method, DeclEntry entry,
            ResolveEnvironment env)
        {
            foreach (var genericParameter in method.GenericParameters)
            {
                foreach (var constraint in genericParameter.Constraints)
                {
                    if (constraint.Bound is not TypeSymbol bound
                        || bound is ErrorTypeSymbol) continue;
                    if (!bound.IsSharedSafe())
                    {
                        env.Error(entry.Node.Span,
                            $"Generic parameter '{genericParameter.Name}' of async " +
                            $"function '{method.Name}' must have a shared-safe " +
                            $"constraint bound: '{bound.Name}'");
                    }
                }
            }
        }
    }
}
