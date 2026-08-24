using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// MW Gate（Middleware 唯一入口门禁，MIDDLEWARE_ARCHITECTURE §1/§10）：
    /// BIL 文本 → 已过 BilVerifier 全规则验证的 BilModule。BIL §23：类型非法的
    /// BIL 必须被拒；Middleware 下游各层只允许消费经本门禁放行的模块。
    /// </summary>
    public static class BilGate
    {
        // 单文件门禁
        public static MwGateResult Accept(string text, string sourceName) =>
            Accept(new[] { (sourceName, text) });

        // 多文件门禁：逐份解析 → BilModuleMerger 合并为单模块 → BilVerifier
        // 全规则验证。任何一步失败即拒绝：解析错误带文件名与行号；验证错误
        // 逐条收集（§21 规则号 + 上下文，与 vm/compile 的报错形态一致）
        public static MwGateResult Accept(IReadOnlyList<(string SourceName, string Text)> inputs)
        {
            var module = new BilModule();
            foreach (var (sourceName, text) in inputs)
            {
                BilModule parsed;
                try
                {
                    parsed = BilReader.Read(text);
                }
                catch (BilParseException ex)
                {
                    return MwGateResult.Rejected($"解析失败 {sourceName}: {ex.Message}");
                }
                if (!BilModuleMerger.Merge(module, parsed, out var duplicate))
                {
                    return MwGateResult.Rejected($"合并失败 {sourceName}: 符号重复 \"{duplicate}\"");
                }
            }

            var errors = BilVerifier.Verify(module);
            if (errors.Count > 0)
            {
                return MwGateResult.Rejected(errors.Select(e => e.ToString()));
            }
            return MwGateResult.Accepted(module);
        }
    }

    /// <summary>
    /// 门禁结果：放行（Module 非空）或拒绝（Errors 非空、人类可读、逐条）。
    /// </summary>
    public sealed class MwGateResult
    {
        public BilModule? Module { get; }
        public IReadOnlyList<string> Errors { get; }
        public bool IsAccepted => Errors.Count == 0;

        private MwGateResult(BilModule? module, IReadOnlyList<string> errors)
        {
            Module = module;
            Errors = errors;
        }

        internal static MwGateResult Accepted(BilModule module) =>
            new(module, Array.Empty<string>());

        internal static MwGateResult Rejected(string error) =>
            new(null, new[] { error });

        internal static MwGateResult Rejected(IEnumerable<string> errors) =>
            new(null, errors.ToArray());
    }
}
