using System.Collections.Generic;

namespace LatteCompiler
{
    // 使用侧泛型约束检查（S9b，SYNTAX §3.6 定稿）：泛型实参对声明约束的
    // 满足性——extends（实参可赋给边界）、supers（边界可赋给实参）、
    // with（边界在实参的 wrapper 应用集合中，含 interface 传染——P2 已
    // 写入 AppliedWrappers）。覆盖调用点（OverloadResolution 泛型候选）
    // 与函数体内类型引用（TypeReferences.Resolve）。
    // 跳过规则（§3.6 ④）：边界或实参含未替换泛型参数（外层身份，由外层
    // 调用代入后自然满足）、实参为 ErrorType（毒化静默）。
    internal static class GenericConstraints
    {
        // 显式泛型实参的约束检查（调用点）：失败落诊断返回 false
        public static bool CheckArguments(IReadOnlyList<SemanticSymbol> typeArgs,
            IReadOnlyList<GenericParameterSymbol> generics, CharRange? span,
            BindEnvironment env)
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
                    if (!Satisfied(constraint.Kind, argument, bound, env))
                    {
                        env.Error(span,
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

        // 构造类型的实参约束检查（类型引用实例化点）：递归检查嵌套构造
        // （Box\<Box\<i32>> 内层同查）；失败落诊断返回 false
        public static bool CheckConstructedType(TypeSymbol type, CharRange? span,
            BindEnvironment env)
        {
            var ok = true;
            if (type.ConstructedFrom != null && type.TypeArguments != null)
            {
                if (!CheckArguments(type.TypeArguments, type.ConstructedFrom.GenericParameters,
                    span, env))
                {
                    ok = false;
                }
                foreach (var argument in type.TypeArguments)
                {
                    if (argument is TypeSymbol { ConstructedFrom: not null } inner
                        && !CheckConstructedType(inner, span, env))
                    {
                        ok = false;
                    }
                }
            }
            return ok;
        }

        // 单条约束的满足判定（§3.6）
        private static bool Satisfied(GenericConstraintKind kind, SemanticSymbol argument,
            SemanticSymbol bound, BindEnvironment env)
        {
            return kind switch
            {
                GenericConstraintKind.Extends => SymbolLookup.IsAssignable(argument, bound, env),
                GenericConstraintKind.Supers => SymbolLookup.IsAssignable(bound, argument, env),
                GenericConstraintKind.With => bound is TypeSymbol wrapper
                    && HasWrapper(argument, wrapper),
                _ => true,
            };
        }

        // with 判定：wrapper 在实参的 wrapper 应用集合中（构造类型回退定义）
        private static bool HasWrapper(SemanticSymbol argument, TypeSymbol wrapper)
        {
            var definition = argument as TypeSymbol;
            if (definition?.ConstructedFrom != null) definition = definition.ConstructedFrom;
            return definition != null
                && definition.AppliedWrappers.Any(w => ReferenceEquals(w, wrapper));
        }
    }
}
