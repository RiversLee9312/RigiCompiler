namespace LatteCompiler
{
    // 声明条目：遍历 AST 骨架时收集的「节点 + 符号 + 名字解析上下文」。
    // InGraph = 符号进入了容器成员表（P1 重复声明的符号不在容器内，
    // 解析填充照做但检查阶段跳过，避免对同一声明重复报错）；
    // ext 成员（ExtTargetPath != null）恒为 true（P2 注册后进目标容器）。
    internal sealed class DeclEntry
    {
        public ASTNode Node = null!;
        public SemanticSymbol Symbol = null!;
        public FileContext Context = null!;
        public TypeSymbol? DeclaringType;
        public bool InGraph;
    }
}
