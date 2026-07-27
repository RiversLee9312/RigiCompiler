using System.Collections.Generic;

namespace LatteCompiler
{
    // 符号结构：源码中的符号引用（如 a.b\<i32>），由 PathParserLayer 填充。
    // Symbol（elements: SymbolElementSet）→ SymbolElement（name + generics: SymbolSet）。

    public class SymbolElement
    {
        public string name = "";
        public SymbolSet generics = new();

        public SymbolElement()
        {
        }
    }
    public class Symbol
    {
        public SymbolElementSet elements = new();
    }
    public class SymbolElementSet : List<SymbolElement>{ }
    public class SymbolSet : List<Symbol> { }
    public class SymbolASTNode : ASTNode
    {
        public SymbolASTNode(ASTNode? parent) : base(parent){ }

        public Symbol symbol = new();
    }
}
