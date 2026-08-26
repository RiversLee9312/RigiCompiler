using System.Collections.Generic;

namespace RigiCompiler
{
    // 使用侧泛型约束检查（S9b，SYNTAX §3.6 定稿）+ g4「最悲观假设」
    // 填入点统一框架：每次调用方填入泛型实参（类型引用实例化、泛型调用
    // 显式实参），除显式声明约束（extends/supers/with）外，对构造类型
    // **自身**重跑隐式限制——
    //   a. 布局闭包（§3.1.1）：holder = 定义的 ClassifyHolder，字段类型
    //      代入实参后走 CheckDirectClosure 同口径（非 rich struct 不得
    //      持 Object、shared 持有者不得持 local 字段）；
    //   b. 静态字段 shared-safe 闸门（§3.1.1 闸门 1）：声明侧对含泛型
    //      参数的类型跳过，代入实参后在此收口；
    //   c. async 闸门 2/3（§4.5）：async 方法的形参/返回类型代入实参后
    //      必须共享安全（声明侧泛型参数跳过的部分）；
    //   d. 嵌套构造递归（Box\<Wrap\<User>> 内层同查）。
    // 跳过规则（§3.6 ④）：实参或边界含未替换泛型参数（声明体内，由
    // 外层代入后再查）、实参为 ErrorType（毒化静默）、内建构造
    // （Nullable/Box/Span/SharedSpan/Type/Array/Map——闭包属性由 §3.1.2
    // 特权规则覆盖；SharedSpan 另查元素「非 rich 或 shared rich ValueType」）。
    // 诊断按 (定义, 实参) 驻留对去重：单次填入检查内同一驻留
    // 对只查一次（引用相等 visited），不同填入点各自报告。
    // 挂点：P2 TypeReferenceResolver（字段/形参/返回类型标注，含显式
    // 界检查补齐）、P3 TypeReferences.Resolve（函数体内类型引用）、
    // P3 CallFacility.ResolveGenericArguments（泛型调用显式实参）。
    internal static class GenericConstraints
    {
        // ===== 显式约束（extends/supers/with，§3.6）=====

        // 显式泛型实参的约束检查（调用点，P3 BindEnvironment 版）：
        // 失败落诊断返回 false
        public static bool CheckArguments(IReadOnlyList<SemanticSymbol> typeArgs,
            IReadOnlyList<GenericParameterSymbol> generics, CharRange? span,
            BindEnvironment env)
        {
            return CheckArguments(typeArgs, generics, span, env.Unit.Symbols, env.Error);
        }

        // SymbolGraph + 诊断槽版（P2/P3 共用核心——P2 无 BindEnvironment）
        public static bool CheckArguments(IReadOnlyList<SemanticSymbol> typeArgs,
            IReadOnlyList<GenericParameterSymbol> generics, CharRange? span,
            SymbolGraph symbols, Action<CharRange?, string> error)
        {
            var ok = true;
            for (int i = 0; i < generics.Count && i < typeArgs.Count; i++)
            {
                var argument = typeArgs[i];
                if (argument is ErrorTypeSymbol) continue;
                if (SymbolLookup.ContainsGenericParameter(argument)) continue;
                foreach (var constraint in generics[i].Constraints)
                {
                    var bound = constraint.Bound;
                    if (bound == null) continue;
                    if (SymbolLookup.ContainsGenericParameter(bound)) continue;
                    if (!Satisfied(constraint.Kind, argument, bound, symbols))
                    {
                        error(span,
                            $"Type argument '{BoundAnalysis.TypeDisplay(argument)}' does not " +
                            $"satisfy the '{constraint.Kind} " +
                            $"{BoundAnalysis.TypeDisplay(bound)}' constraint of " +
                            $"'{generics[i].Name}'");
                        ok = false;
                    }
                }
            }
            return ok;
        }

        // ===== 统一填入点检查（显式约束 + 隐式限制闭包）=====

        // 检查过程状态：驻留对去重表 + 显式界失败标记（调用方据此决定
        // 是否拒绝——隐式限制违规只诊断不拒绝，避免级联误诊）
        private sealed class CheckState
        {
            public readonly HashSet<TypeSymbol> Visited =
                new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
            public bool ExplicitOk = true;
        }

        // 构造类型的填入点检查（类型引用实例化点，P3 BindEnvironment 版）：
        // 显式约束 + 构造类型自身的隐式限制闭包；失败落诊断返回 false
        public static bool CheckConstructedType(TypeSymbol type, CharRange? span,
            BindEnvironment env)
        {
            return CheckConstructedType(type, span, env.Unit.Symbols, env.Error, out _);
        }

        // 携显式界结果版：explicitConstraintsOk = 显式 extends/supers/with
        // 检查是否全部通过（拒绝型调用方据此返回 null）
        public static bool CheckConstructedType(TypeSymbol type, CharRange? span,
            BindEnvironment env, out bool explicitConstraintsOk)
        {
            return CheckConstructedType(type, span, env.Unit.Symbols, env.Error,
                out explicitConstraintsOk);
        }

        // SymbolGraph + 诊断槽版（P2/P3 共用核心）
        public static bool CheckConstructedType(TypeSymbol type, CharRange? span,
            SymbolGraph symbols, Action<CharRange?, string> error)
        {
            return CheckConstructedType(type, span, symbols, error, out _);
        }

        public static bool CheckConstructedType(TypeSymbol type, CharRange? span,
            SymbolGraph symbols, Action<CharRange?, string> error,
            out bool explicitConstraintsOk)
        {
            var state = new CheckState();
            var ok = CheckConstructedType(type, span, symbols, error, state);
            explicitConstraintsOk = state.ExplicitOk;
            return ok;
        }

        private static bool CheckConstructedType(TypeSymbol type, CharRange? span,
            SymbolGraph symbols, Action<CharRange?, string> error, CheckState state)
        {
            var ok = true;
            if (type.ConstructedFrom != null && type.TypeArguments != null)
            {
                // (定义, 实参) 驻留对单次填入只查一次——Pair\<X, X> 同实参
                // 双槽、嵌套递归再遇同一构造均不重复诊断
                if (!state.Visited.Add(type)) return true;
                if (!CheckArguments(type.TypeArguments, type.ConstructedFrom.GenericParameters,
                    span, symbols, error))
                {
                    ok = false;
                    state.ExplicitOk = false;
                }
                if (!CheckInstantiationLimits(type, span, symbols, error, state))
                {
                    ok = false;
                }
                foreach (var argument in type.TypeArguments)
                {
                    if (argument is TypeSymbol { ConstructedFrom: not null } inner
                        && !CheckConstructedType(inner, span, symbols, error, state))
                    {
                        ok = false;
                    }
                }
            }
            return ok;
        }

        // 构造类型自身的隐式限制（g4 框架本体）：实参全部代入（无 GP、
        // 无毒化）时，对布局闭包 / 静态字段闸门 / async 闸门重跑声明侧
        // 因泛型参数而跳过的检查
        private static bool CheckInstantiationLimits(TypeSymbol type, CharRange? span,
            SymbolGraph symbols, Action<CharRange?, string> error, CheckState state)
        {
            var def = type.ConstructedFrom!;
            var args = type.TypeArguments!;
            if (args.Any(a => a is ErrorTypeSymbol)) return true;
            if (args.Any(SymbolLookup.ContainsGenericParameter)) return true;
            // SharedSpan\<T\> 特权（RUNTIME §5）：T 须非 rich 或 shared rich ValueType
            // （IsSharedSafe：String/标量/非 rich struct 放行；含 local class 引用的 rich struct 拒绝）
            if (ReferenceEquals(def, symbols.Bootstrap.SharedSpanDefinition))
            {
                if (args.Count > 0 && args[0] is TypeSymbol element && !element.IsSharedSafe())
                {
                    error(span,
                        $"类型实参 '{BoundAnalysis.TypeDisplay(element)}' 不满足 " +
                        $"SharedSpan 的元素约束（须为非 rich 或 shared rich ValueType）");
                    state.ExplicitOk = false;
                    return false;
                }
                return true;
            }
            // 其余内建构造的闭包属性由 §3.1.2 特权规则覆盖（嵌套实参仍由
            // 调用方的实参递归检查）
            if (def.IsBuiltin) return true;
            var ok = true;
            var holder = FieldClosureChecker.ClassifyHolder(def);
            foreach (var (field, fieldType) in FieldClosureChecker.ClosureFieldsOf(type, symbols))
            {
                // a/b：布局闭包——代入后字段类型走直接闭包同口径；via 注记
                // 指明违规经哪个构造实参引入（字段声明类型被代入改写时）。
                // 静态字段不属于实例布局闭包（§3.1.1 闭包表管实例字段，
                // 静态字段归闸门 1——下方 c 收口），不参与 a/b
                if (holder != HolderCategory.None && !field.IsStatic
                    && FieldClosureChecker.CheckDirectClosure(holder, type, field.Name,
                        FieldClosureChecker.ClassifyFieldType(fieldType), span,
                        ViaNote(field.FieldType, fieldType, type), error))
                {
                    ok = false;
                }
                // 嵌套构造字段自身同样须满足隐式限制（Box 外的用户构造：
                // `class C\<T> { var w: Wrap\<T> }` 的 C\<User> 拦截
                // Wrap\<User> 自身闭包违规）
                if (fieldType is TypeSymbol { ConstructedFrom: not null } inner
                    && !CheckConstructedType(inner, span, symbols, error, state))
                {
                    ok = false;
                }
                // c：静态字段 shared-safe 闸门——只收声明侧因含 GP 跳过
                // 的形态（具体类型声明侧已判定，不重复诊断）
                if (field.IsStatic && fieldType is TypeSymbol staticType
                    && field.FieldType != null
                    && SymbolLookup.ContainsGenericParameter(field.FieldType)
                    && staticType is not ErrorTypeSymbol
                    && !staticType.IsSharedSafe())
                {
                    error(span,
                        $"Global or static field '{field.Name}' must have a shared-safe " +
                        $"type (SYNTAX §3.1.1) (via instantiation '{BoundAnalysis.TypeDisplay(type)}')");
                    ok = false;
                }
            }
            // d：async 闸门 2/3——async 方法签名代入实参后须共享安全；
            // 同样只收声明侧因含 GP 跳过的形态
            foreach (var method in def.Methods)
            {
                if (!method.IsAsync) continue;
                foreach (var parameter in method.Parameters)
                {
                    if (parameter.Type != null
                        && SymbolLookup.ContainsGenericParameter(parameter.Type)
                        && symbols.Substitute(parameter.Type, def, type) is TypeSymbol paramType
                        && paramType is not ErrorTypeSymbol
                        && !SymbolLookup.ContainsGenericParameter(paramType)
                        && !paramType.IsSharedSafe())
                    {
                        error(span,
                            $"Parameter '{parameter.Name}' of async function '{method.Name}' " +
                            $"must be a shared-safe type: '{BoundAnalysis.TypeDisplay(paramType)}' " +
                            $"(via instantiation '{BoundAnalysis.TypeDisplay(type)}')");
                        ok = false;
                    }
                }
                if (method.ReturnType != null
                    && SymbolLookup.ContainsGenericParameter(method.ReturnType)
                    && symbols.Substitute(method.ReturnType, def, type) is TypeSymbol returnType
                    && returnType is not ErrorTypeSymbol
                    && !SymbolLookup.ContainsGenericParameter(returnType)
                    && !returnType.IsSharedSafe())
                {
                    error(span,
                        $"Return type '{BoundAnalysis.TypeDisplay(returnType)}' of async " +
                        $"function '{method.Name}' must be a shared-safe type " +
                        $"(via instantiation '{BoundAnalysis.TypeDisplay(type)}')");
                    ok = false;
                }
            }
            return ok;
        }

        // 字段声明类型被实参代入改写时给出经由注记（「非 rich struct Wrap
        // 的字段 v 经实参 User 持有 Object」语义中的「经实参」部分）
        private static string? ViaNote(SemanticSymbol? declared, SemanticSymbol? substituted,
            TypeSymbol constructed)
        {
            return substituted != null && !ReferenceEquals(declared, substituted)
                ? $" (via type argument of '{BoundAnalysis.TypeDisplay(constructed)}')"
                : null;
        }

        // yield 的 Alarm 约束只接受 extends：这是唯一能证明 T 的每个实例
        // 都属于 Alarm 子类的约束形态。约束边界缺失或 Alarm 声明缺失时
        // 返回 false，让调用方按声明不完整原则保守放行。
        public static bool IsAlarmParameter(GenericParameterSymbol parameter,
            TypeSymbol? pollingAlarm, TypeSymbol? eventAlarm, BindEnvironment env)
        {
            if (pollingAlarm == null && eventAlarm == null) return false;
            foreach (var constraint in parameter.Constraints)
            {
                if (constraint.Kind != GenericConstraintKind.Extends
                    || constraint.Bound is not TypeSymbol bound) continue;
                if (pollingAlarm != null && SymbolLookup.IsAssignable(bound, pollingAlarm, env))
                    return true;
                if (eventAlarm != null && SymbolLookup.IsAssignable(bound, eventAlarm, env))
                    return true;
            }
            return false;
        }

        // 单条约束的满足判定（§3.6）
        private static bool Satisfied(GenericConstraintKind kind, SemanticSymbol argument,
            SemanticSymbol bound, SymbolGraph symbols)
        {
            return kind switch
            {
                GenericConstraintKind.Extends => SymbolLookup.IsAssignable(argument, bound, symbols),
                GenericConstraintKind.Supers => SymbolLookup.IsAssignable(bound, argument, symbols),
                GenericConstraintKind.With => bound is TypeSymbol wrapper
                    && HasWrapper(argument, wrapper),
                _ => true,
            };
        }

        // with 判定：wrapper 在实参的 wrapper 应用集合中（应用记录与判定边界
        // 双双取定义级——S11a 起应用携带构造代入结果，构造类型回退定义）
        private static bool HasWrapper(SemanticSymbol argument, TypeSymbol wrapper)
        {
            var definition = argument as TypeSymbol;
            if (definition?.ConstructedFrom != null) definition = definition.ConstructedFrom;
            return definition != null
                && definition.AppliedWrappers.Any(w => ReferenceEquals(w.WrapperDefinition, wrapper));
        }
    }
}
