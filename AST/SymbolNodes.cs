using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler
{
    // 符号结构：源码中的符号引用（如 a.b\<i32>），由 PathParserLayer 填充。
    // Symbol（elements: SymbolElementSet）→ SymbolElement（name + generics）。
    // 泛型实参是完整类型引用节点（g1 修复：支持 i32? 内嵌可空实参，与表达式
    // 路径 foo\<i32?>(x) 同口径——实参统一委托 TypeReferenceParserLayer 解析）；
    // 实参节点的 Parent 指向持有本符号的 SymbolASTNode，经
    // AstStructureReflection 的 SymbolASTNode 特判纳入遍历与完整性校验。

    public class SymbolElement
    {
        public string name = "";
        public List<TypeReferenceASTNode> generics = new();

        public SymbolElement()
        {
        }

        // 深拷贝（M31）：import 多导入形态展开共享前缀时使用，
        // 防止语义阶段对符号元素的原地规范化跨导入项交叉污染。
        // owner：持有克隆符号的 SymbolASTNode——泛型实参是 AST 节点，
        // Parent 只能设置一次，克隆时必须以新宿主重挂（g1）
        public SymbolElement DeepClone(SymbolASTNode owner)
        {
            var copy = new SymbolElement { name = name };
            copy.generics.AddRange(generics.Select(g => g.DeepClone(owner)));
            return copy;
        }
    }
    public class Symbol
    {
        public SymbolElementSet elements = new();

        // 深拷贝（M31）：elements 及其泛型实参递归复制（owner 语义同上）
        public Symbol DeepClone(SymbolASTNode owner)
        {
            var copy = new Symbol();
            copy.elements.AddRange(elements.Select(el => el.DeepClone(owner)));
            return copy;
        }
    }
    public class SymbolElementSet : List<SymbolElement>{ }
    public class SymbolASTNode : ASTNode
    {
        public SymbolASTNode(ASTNode? parent) : base(parent){ }

        public Symbol symbol = new();
    }
}
