namespace RigiCompiler
{
    // 前置语句输出状态（M65 Lowering 侧组件化拆分，自 LowerContext
    // 迁出）：前置语句机制——当前块输出语句列表栈。块降级为每块建
    // 输出列表压栈；表达式降级向栈顶列表追加前置语句，自然排在属主
    // 语句之前。裸栈不外泄——压弹与追加一律经本类语义方法。
    internal sealed class LowerOutputState
    {
        private readonly Stack<List<LoweredStatement>> outputStack =
            new Stack<List<LoweredStatement>>();

        // 当前块输出列表（栈顶）：块降级逐语句降级收集目标。列表就地
        // 可变——seq 编织与 pattern if 链的就地变换需要持有同一引用
        // （仿 SwitchRewriters 先例），故暴露列表本身而非副本
        public List<LoweredStatement> Current => outputStack.Peek();

        // 块降级开始：建空输出列表压栈
        public void Push()
        {
            outputStack.Push(new List<LoweredStatement>());
        }

        // 压入调用方已建好的输出列表（就地收集与编织变换需要同一
        // 引用——seq 语句/pattern if 链/judge 块合成场景）
        public void Push(List<LoweredStatement> statements)
        {
            outputStack.Push(statements);
        }

        // 块降级完成：弹栈
        public void Pop()
        {
            outputStack.Pop();
        }

        // 表达式降级向栈顶列表追加前置语句（自然排在属主语句之前）
        public void Add(LoweredStatement statement)
        {
            outputStack.Peek().Add(statement);
        }
    }
}
