namespace RigiCompiler
{
    // 分支 block 编号分配器（M65 Lowering 侧组件化拆分，自 EmitContext
    // 迁出）：if/loop/switch/seq/try 结构化指令子块 id 的序号来源
    // （函数内唯一递增）。编号次序即 BIL 文本块 id（黄金文本逐字节
    // 敏感）——取号语义为后缀自增（返回自增前的值），与原
    // ctx.XxxCount++ 表达式取值一致。
    internal sealed class BlockIdAllocator
    {
        private int ifCount;

        private int loopCount;

        private int switchCount;

        private int seqCount;

        private int tryCount;

        // if 分支块编号（"ifN-then"/"ifN-else" 的 N）
        public int NextIf()
        {
            return ifCount++;
        }

        // 循环块编号（"loopN-body"/"loopN-enum"/"loopN-judge" 的 N）
        public int NextLoop()
        {
            return loopCount++;
        }

        // switch 块编号（"switchN-itemI"/"switchN-default" 的 N）
        public int NextSwitch()
        {
            return switchCount++;
        }

        // seq 块编号（"seqN" 的 N）
        public int NextSeq()
        {
            return seqCount++;
        }

        // try 块编号（"tryN-body"/"tryN-catchI"/"tryN-finally" 的 N）
        public int NextTry()
        {
            return tryCount++;
        }
    }
}
