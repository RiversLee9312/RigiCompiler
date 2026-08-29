using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// MIR→LLVM 函数级上下文（组合根的函数组件，对照 P4b EmitContext）：
    /// 每个 MirFunction 发射期间新建/重置。当前 LLVM 函数、块映射、
    /// builder 与 alloca 槽表在此，不进模块级环境，避免跨函数状态污染。
    /// </summary>
    internal sealed class LlvmEmitContext
    {
        // 当前发射中的函数（EmitBody 逐函数置位；检查策略的 guard 块
        // 追加需要宿主函数）
        internal LLVMValueRef CurrentFunction { get; private set; }

        internal LLVMBuilderRef Builder { get; private set; }

        internal Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> Slots { get; private set; }
            = null!;

        internal void SetCurrentFunction(LLVMValueRef fn) => CurrentFunction = fn;

        // 当前发射中函数的 MIR 块 → LLVM 块映射（EmitBody 置位/清位；
        // MW9a 调用后 pending 检查的异常边目标查询用——继续块为内联
        // 合成块不入本映射，MIR 块 1:1 映射不受影响）
        internal IReadOnlyDictionary<string, LLVMBasicBlockRef>? CurrentBlocks { get; private set; }

        internal void SetCurrentBlocks(IReadOnlyDictionary<string, LLVMBasicBlockRef>? blocks) =>
            CurrentBlocks = blocks;

        internal void BeginBody(LLVMValueRef fn, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            IReadOnlyDictionary<string, LLVMBasicBlockRef> blocks)
        {
            CurrentFunction = fn;
            Builder = builder;
            Slots = slots;
            CurrentBlocks = blocks;
        }

        internal void EndBody()
        {
            CurrentBlocks = null;
            Slots = null!;
            Builder = default;
        }
    }
}
