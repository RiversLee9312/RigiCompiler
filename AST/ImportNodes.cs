using System.Collections.Generic;

namespace RigiCompiler
{
    // import 列表项（SYNTAX §15.2）：携带一个符号路径节点。
    // 不是 ASTNode（struct），以 [AstCarrier] 标注；配合 ImportASTNode.importedSymbols
    // 上的 [ChildAstNode]，Validator 会深入本类型公共字段，把 symbolNode
    // 视为 ImportASTNode 的子节点校验。
    [AstCarrier]
    public struct ImportItem
    {
        public SymbolASTNode symbolNode;
        public bool importAll;
    }
    public class ImportASTNode : ASTNode
    {
        [ChildAstNode] public List<ImportItem> importedSymbols = new();
        public ImportASTNode(ASTNode? parent) : base(parent){ }
    }
}
