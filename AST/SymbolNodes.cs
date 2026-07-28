using System.Collections.Generic;
using System.Linq;

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

        // 深拷贝（M31）：import 多导入形态展开共享前缀时使用，
        // 防止语义阶段对符号元素的原地规范化跨导入项交叉污染
        public SymbolElement DeepClone()
        {
            var copy = new SymbolElement { name = name };
            copy.generics.AddRange(generics.Select(s => s.DeepClone()));
            return copy;
        }
    }
    public class Symbol
    {
        public SymbolElementSet elements = new();

        // 深拷贝（M31）：elements 及其泛型实参递归复制
        public Symbol DeepClone()
        {
            var copy = new Symbol();
            copy.elements.AddRange(elements.Select(el => el.DeepClone()));
            return copy;
        }
    }
    public class SymbolElementSet : List<SymbolElement>{ }
    public class SymbolSet : List<Symbol> { }
    public class SymbolASTNode : ASTNode
    {
        public SymbolASTNode(ASTNode? parent) : base(parent){ }

        public Symbol symbol = new();
    }
}
