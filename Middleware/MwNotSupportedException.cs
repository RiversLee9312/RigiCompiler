using System;

namespace RigiCompiler.Middleware
{
    // 合法 BIL 超出当前 MW 阶段实现面时抛出（门禁已通过的模块遇到
    // 尚未实现的指令/类型形态）——用户可读的受控失败，CLI 转退出码 2；
    // 与 CompilerInternalException（编译器 bug）严格区分。
    public sealed class MwNotSupportedException : Exception
    {
        public MwNotSupportedException(string message) : base(message) { }
    }
}
