using RigiCompiler.Bil;

namespace RigiCompiler.Modules;

/// <summary>接口声明 ID 不以 canonical 或对象地址代替泛型参数的 owner/index 身份。</summary>
internal sealed class ModuleSymbolRegistry
{
    internal readonly Dictionary<SemanticSymbol, string> Ids = new();
    internal readonly Dictionary<string, SemanticSymbol> Symbols = new(StringComparer.Ordinal);
    internal readonly Dictionary<GenericParameterSymbol, (string Owner, int Index)> Generics = new();
    private readonly string moduleId;
    internal ModuleSymbolRegistry(string moduleId) { this.moduleId = moduleId; }
    internal string Id(SemanticSymbol symbol)
    {
        if (symbol is TypeSymbol { ConstructedFrom: { } definition }) return Id(definition);
        if (Ids.TryGetValue(symbol, out var existing)) return existing;
        var owner = symbol switch
        {
            TypeSymbol t when t.DeclaringType != null => Id(t.DeclaringType),
            MethodSymbol m when m.Owner != null => Id(m.Owner),
            FieldSymbol f when f.Owner != null => Id(f.Owner),
            EnumCaseSymbol c => Id(c.Owner),
            _ => (symbol.OriginModuleId ?? moduleId) + "/namespace/" + AccessChecker.ContainingNamespaceOf(symbol)?.FullName
        };
        var role = symbol switch
        {
            TypeSymbol t => "/type/" + t.Name + "/" + t.GenericParameters.Count,
            MethodSymbol m => "/method/" + m.Name + "/" + m.Kind + "/" + m.GenericParameters.Count + "/"
                + ModuleOrigin.Hash(CanonicalSymbolPrinter.PrintMethod(m)),
            FieldSymbol f => "/field/" + f.Name + "/" + f.IsStatic,
            EnumCaseSymbol c => "/case/" + c.Name,
            _ => throw new ModuleConfigurationException("接口不能登记无owner参数：" + symbol.Name)
        };
        Register(symbol, symbol.StableIdentity ?? owner + role);
        var gps = symbol switch { TypeSymbol t => t.GenericParameters, MethodSymbol m => m.GenericParameters, _ => null };
        if (gps != null)
            for (int i = 0; i < gps.Count; i++)
            {
                // 闭包/cell/companion 借用外层声明的 GP，身份仍归原声明。
                if (Generics.ContainsKey(gps[i])) continue;
                var gpId = Ids[symbol] + "/gp/" + i;
                Register(gps[i], gpId);
                Generics.Add(gps[i], (Ids[symbol], i));
            }
        // 形参由各方法内嵌按序写出；现有合成器可复用同一 bool flag 对象到
        // 多个接口方法，不能把这个借用对象误作独立全表声明。
        return Ids[symbol];
    }
    private void Register(SemanticSymbol symbol, string id)
    {
        if (Ids.TryGetValue(symbol, out var prior))
        { if (prior != id) throw new ModuleConfigurationException("接口 symbol ID 变更"); return; }
        if (!Symbols.TryAdd(id, symbol)) throw new ModuleConfigurationException("接口重复 ID：" + id);
        Ids.Add(symbol, id);
    }
    internal void Collect(CompilationUnit unit, IReadOnlyList<BoundFunctionBody> bodies)
    {
        void Type(TypeSymbol type)
        {
            if (type.ConstructedFrom != null) { Type(type.ConstructedFrom); return; }
            if (Ids.ContainsKey(type)) return;
            Id(type);
            foreach (var child in type.NestedTypes) Type(child);
            foreach (var field in type.Fields) Field(field);
            foreach (var method in type.Methods) Method(method);
            foreach (var item in type.Cases)
            {
                Id(item); if (item.CaseFactory != null) Method(item.CaseFactory);
                foreach (var helper in item.FixedArgumentFactories ?? []) if (helper != null) Method(helper);
            }
        }
        void Method(MethodSymbol method)
        {
            if (method.Owner != null && !Ids.ContainsKey(method.Owner)) Type(method.Owner);
            Id(method);
            foreach (var parameter in method.Parameters) if (parameter.DefaultFactory != null) Method(parameter.DefaultFactory);
        }
        void Field(FieldSymbol field)
        {
            Id(field);
            if (field.Getter != null) Method(field.Getter);
            if (field.Setter != null) Method(field.Setter);
            if (field.CellStorage != null) Type(field.CellStorage.CellClass);
            if (field.CompanionCellField != null) Field(field.CompanionCellField);
        }
        void Namespace(NamespaceSymbol ns)
        {
            foreach (var type in ns.Types) Type(type);
            foreach (var field in ns.Fields) Field(field);
            foreach (var method in ns.Methods) Method(method);
            foreach (var child in ns.ChildNamespaces) Namespace(child);
        }
        Namespace(unit.Symbols.GlobalNamespace);
        // 非公开顶层导入不进源名称查找容器；注册其身份供签名引用复用。
        foreach (var imported in unit.Symbols.ImportedSymbols.Values)
            if (imported is TypeSymbol t) Type(t);
            else if (imported is MethodSymbol m) Method(m);
            else if (imported is FieldSymbol f) Field(f);
        foreach (var body in bodies)
        {
            if (body.Method.Owner != null) Type(body.Method.Owner);
            Method(body.Method);
        }
        // 引用签名可能指向不在公开查找容器的 imported/link-only 定义。
        for (int i = 0; i < Symbols.Count; i++)
        {
            void Ref(SemanticSymbol? symbol)
            {
                if (symbol is TypeSymbol { ConstructedFrom: { } definition, TypeArguments: { } arguments })
                { Type(definition); foreach (var argument in arguments) Ref(argument); }
                else if (symbol is TypeSymbol definitionType) Type(definitionType);
            }
            var symbol = Symbols.Values.ElementAt(i);
            switch (symbol)
            {
                case TypeSymbol t: Ref(t.BaseType); foreach (var iface in t.Interfaces) Ref(iface); foreach (var app in t.AppliedWrappers) Ref(app.Wrapper); break;
                case MethodSymbol m: Ref(m.ReturnType); foreach (var p in m.Parameters) Ref(p.Type); foreach (var app in m.AppliedWrappers) Ref(app.Wrapper); break;
                case FieldSymbol f: Ref(f.FieldType); foreach (var app in f.AppliedWrappers) Ref(app.Wrapper); break;
                case GenericParameterSymbol gp: foreach (var constraint in gp.Constraints) Ref(constraint.Bound); break;
            }
        }
    }
}
