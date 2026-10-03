using System.Collections.Generic;

namespace RigiCompiler
{
    // 编译单元（SEMANTIC_ARCHITECTURE §3）：一次 compile 调用处理的全部源文件
    // （未来对应一个程序集 / 一个 BIL 文件）+ 全局诊断袋 + 唯一符号图。
    // P1/P2 面向整个编译单元一次性执行，跨文件前向引用因此天然成立；
    // P3 以函数体为独立分析单位。core.rg 载入（S10）后同样追加为源文件，
    // 走同一条 P1/P2 路径。
    public sealed class CompilationUnit
    {
        // 源文件语法树（每文件一棵 RootASTNode，中端全程只读）
        public IReadOnlyList<RootASTNode> SourceFiles { get; }

        // 全编译单元单实例，贯穿 P1–P4 只追加（ARCHITECTURE §8）
        public DiagnosticBag Diagnostics { get; }

        // 编译单元唯一符号对象图（ARCHITECTURE §4；构造含 bootstrap 硬编码）
        public SymbolGraph Symbols { get; }

        public string ModuleIdentity => Symbols.ModuleIdentity;
        public bool IsModuleCompilation { get; }
        internal bool IsFinalModuleApplication { get; set; }

        // 结构路径只读，不以线程到达次序或源码位置（可能为空）分配身份。
        internal string SyntaxIdentity(ASTNode node)
        {
            var path = new Stack<string>();
            while (node.Parent is { } parent)
            {
                var edge = AstStructureReflection.EnumerateChildren(parent)
                    .FirstOrDefault(child => ReferenceEquals(child.Child, node)).Via;
                path.Push(edge ?? node.GetType().Name);
                node = parent;
            }
            var file = node as RootASTNode;
            var index = -1;
            for (var i = 0; i < SourceFiles.Count; i++)
                if (ReferenceEquals(SourceFiles[i], file)) { index = i; break; }
            return ModuleIdentity + "/file/" + index + "/source/"
                + (file?.Span?.sourceName ?? "<synthetic>") + "/" + string.Join("/", path);
        }

        public CompilationUnit(params RootASTNode[] sourceFiles) : this("module", sourceFiles) { }

        public CompilationUnit(string moduleIdentity, params RootASTNode[] sourceFiles) : this(moduleIdentity, false, sourceFiles) { }

        public CompilationUnit(string moduleIdentity, bool moduleCompilation, params RootASTNode[] sourceFiles)
        {
            IsModuleCompilation = moduleCompilation;
            SourceFiles = sourceFiles;
            Diagnostics = new DiagnosticBag();
            Symbols = new SymbolGraph(moduleIdentity);
            ModuleOrigin.Register(this);
        }

        // 自举签名绑定复用已经建好的图，避免构造第二套内建身份。
        internal CompilationUnit(SymbolGraph symbols, RootASTNode source)
        {
            SourceFiles = new[] { source };
            Diagnostics = new DiagnosticBag();
            Symbols = symbols;
        }

        internal CompilationUnit(SymbolGraph symbols, IReadOnlyList<RootASTNode> sources)
        {
            SourceFiles = sources;
            Diagnostics = new DiagnosticBag();
            Symbols = symbols;
            IsModuleCompilation = true;
            ModuleOrigin.Register(this);
        }
    }
}
