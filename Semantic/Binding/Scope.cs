namespace LatteCompiler
{
    // 词法作用域链（自旧 BindSession 内部类原样迁移）：
    // 块嵌套时下传，局部声明沿链向外查找（内层遮蔽外层）。
    internal sealed class Scope
    {
        private readonly Scope? parent;
        private readonly Dictionary<string, LocalSymbol> locals = new Dictionary<string, LocalSymbol>();

        public Scope(Scope? parent)
        {
            this.parent = parent;
        }

        public bool DeclaresHere(string name)
        {
            return locals.ContainsKey(name);
        }

        public void Declare(LocalSymbol local)
        {
            locals[local.Name] = local;
        }

        public LocalSymbol? Lookup(string name)
        {
            for (var scope = this; scope != null; scope = scope.parent)
            {
                if (scope.locals.TryGetValue(name, out var local)) return local;
            }
            return null;
        }
    }
}
