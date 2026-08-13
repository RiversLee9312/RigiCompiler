namespace RigiCompiler
{
    // ===== 泛型型变声明与使用位置检查（SYNTAX §3.6） =====
    //
    // 型变只属于类型声明的泛型参数。out 只能出现在结果/只读位置，in
    // 只能出现在参数/只写位置；可变字段、invariant 泛型容器以及同时存在
    // getter/setter 的字段都会把出现位置收紧为 invariant。
    internal sealed class VarianceChecker : ResolverVisitor<VarianceChecker>
    {
        private enum Polarity
        {
            Covariant,
            Contravariant,
            Invariant
        }

        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.Node is not CallableDeclarationASTNode callable
                    || callable.GenericParameters == null)
                {
                    continue;
                }

                foreach (var parameter in callable.GenericParameters.Parameters)
                {
                    if (parameter.Variance != GenericVariance.None)
                    {
                        env.Error(parameter.Span ?? entry.Node.Span,
                            $"Generic parameter '{parameter.Name}' variance is only allowed " +
                            "on type declarations");
                    }
                }
            }

            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph || entry.Symbol is not TypeSymbol type || type.IsBuiltin
                    || type.GenericParameters.Count == 0)
                {
                    continue;
                }

                foreach (var parameter in type.GenericParameters
                    .Where(p => p.Variance != GenericVariance.None))
                {
                    CheckTypeParameter(type, parameter, entry, env);
                }
            }
        }

        private static void CheckTypeParameter(TypeSymbol owner, GenericParameterSymbol parameter,
            DeclEntry entry, ResolveEnvironment env)
        {
            var expected = parameter.Variance == GenericVariance.Out
                ? Polarity.Covariant
                : Polarity.Contravariant;

            void Check(SemanticSymbol? symbol, Polarity polarity, string use)
            {
                if (symbol == null || ContainsInvalidUse(symbol, parameter, polarity, expected))
                {
                    if (symbol != null && ContainsParameter(symbol, parameter))
                    {
                        env.Error(entry.Node.Span,
                            $"Type '{owner.Name}' {VarianceText(parameter.Variance)} parameter " +
                            $"'{parameter.Name}' cannot be used in {use}");
                    }
                }
            }

            foreach (var field in owner.Fields)
            {
                if (field.FieldType == null) continue;
                if (field.Getter != null && field.Setter != null)
                {
                    Check(field.FieldType, Polarity.Invariant,
                        $"both getter and setter of field '{field.Name}'");
                }
                else if (field.Getter != null)
                {
                    Check(field.FieldType, Polarity.Covariant,
                        $"getter of field '{field.Name}'");
                }
                else if (field.Setter != null)
                {
                    Check(field.FieldType, Polarity.Contravariant,
                        $"setter of field '{field.Name}'");
                }
                else
                {
                    Check(field.FieldType,
                        field.IsStatic ? Polarity.Invariant
                            : field.IsConst ? Polarity.Covariant : Polarity.Invariant,
                        $"field '{field.Name}'");
                }
            }

            foreach (var method in owner.Methods)
            {
                foreach (var parameterSymbol in method.Parameters)
                {
                    Check(parameterSymbol.Type, Polarity.Contravariant,
                        $"parameter '{parameterSymbol.Name}' of method '{method.Name}'");
                }
                Check(method.ReturnType, Polarity.Covariant,
                    $"return type of method '{method.Name}'");
            }

            Check(owner.BaseType, Polarity.Covariant, "a base type");
            foreach (var iface in owner.Interfaces)
            {
                Check(iface, Polarity.Covariant, "an implemented interface");
            }
        }

        private static bool ContainsInvalidUse(SemanticSymbol symbol,
            GenericParameterSymbol target, Polarity polarity, Polarity expected)
        {
            if (ReferenceEquals(symbol, target)) return polarity != expected;
            if (symbol is not TypeSymbol { ConstructedFrom: not null,
                TypeArguments: { } arguments } constructed)
            {
                return false;
            }

            var definition = constructed.ConstructedFrom!;
            for (var i = 0; i < arguments.Count && i < definition.GenericParameters.Count; i++)
            {
                var nested = Compose(polarity, definition.GenericParameters[i].Variance);
                if (ContainsInvalidUse(arguments[i], target, nested, expected)) return true;
            }
            return false;
        }

        private static bool ContainsParameter(SemanticSymbol symbol, GenericParameterSymbol target)
        {
            if (ReferenceEquals(symbol, target)) return true;
            return symbol is TypeSymbol { TypeArguments: { } arguments }
                && arguments.Any(argument => ContainsParameter(argument, target));
        }

        private static Polarity Compose(Polarity outer, GenericVariance inner) =>
            outer == Polarity.Invariant || inner == GenericVariance.None
                ? Polarity.Invariant
                : inner == GenericVariance.Out
                    ? outer
                    : outer == Polarity.Covariant
                        ? Polarity.Contravariant
                        : Polarity.Covariant;

        private static string VarianceText(GenericVariance variance) =>
            variance == GenericVariance.Out ? "covariant" : "contravariant";
    }
}
