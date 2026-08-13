namespace RigiCompiler
{
    // 词法作用域链（自旧 BindSession 内部类原样迁移）：
    // 块嵌套时下传，局部声明沿链向外查找（内层遮蔽外层）。
    internal sealed class Scope
    {
        private readonly Scope? parent;
        private readonly Dictionary<string, SemanticSymbol> symbols =
            new Dictionary<string, SemanticSymbol>();

        public Scope(Scope? parent)
        {
            this.parent = parent;
        }

        public bool DeclaresHere(string name)
        {
            return symbols.ContainsKey(name);
        }

        public void Declare(LocalSymbol local)
        {
            symbols[local.Name] = local;
        }

        public void Declare(ParameterSymbol parameter)
        {
            symbols[parameter.Name] = parameter;
        }

        public LocalSymbol? Lookup(string name)
        {
            for (var scope = this; scope != null; scope = scope.parent)
            {
                if (scope.symbols.TryGetValue(name, out var symbol))
                    return symbol as LocalSymbol;
            }
            return null;
        }

        public SemanticSymbol? LookupSymbol(string name)
        {
            for (var scope = this; scope != null; scope = scope.parent)
            {
                if (scope.symbols.TryGetValue(name, out var symbol)) return symbol;
            }
            return null;
        }
    }
}
