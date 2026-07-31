using System.Collections.Generic;

namespace LatteCompiler
{
    // 中端可恢复诊断（P1–P4 通用，SEMANTIC_ARCHITECTURE §8）。
    // 与前端 LexerException/ParserException（单发即死）严格不同：中端诊断
    // 累积、尽量继续——函数体之间、声明之间的错误互不阻断，一次编译报出
    // 尽可能多的错误；任一 pass 结束时存在 Error 即停止推进到下一 pass 的
    // 发射性工作。CompilerInternalException 语义不变：编译器自身 bug，
    // 永不用于用户源码错误。

    public enum DiagnosticSeverity
    {
        Error,
        Warning
    }

    // 诊断来源阶段：P1 声明收集 / P2 声明解析 / P3 函数体分析 / P4 降级与发射
    public enum DiagnosticPhase
    {
        P1,
        P2,
        P3,
        P4
    }

    // 单条诊断。暂不建错误码编号体系（简洁三问），测试按消息子串断言
    // （与 CheckParseError 惯例一致）。Span 可空：编译单元级错误
    // （如无单一源码位置的全局冲突）允许不带位置。
    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity { get; }
        public DiagnosticPhase Phase { get; }
        public CharRange? Span { get; }
        public string Message { get; }

        public Diagnostic(DiagnosticSeverity severity, DiagnosticPhase phase, CharRange? span, string message)
        {
            Severity = severity;
            Phase = phase;
            Span = span;
            Message = message;
        }
    }

    // 诊断收集袋：全编译单元一个实例贯穿 P1–P4，各 pass 只追加。
    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> diagnostics = new List<Diagnostic>();

        public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

        // 阶段推进门槛：任一 pass 结束时存在 Error 即停止推进（ARCHITECTURE §8）
        public bool HasErrors { get; private set; }

        public void Add(Diagnostic diagnostic)
        {
            diagnostics.Add(diagnostic);
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                HasErrors = true;
            }
        }

        public void Error(DiagnosticPhase phase, CharRange? span, string message)
        {
            Add(new Diagnostic(DiagnosticSeverity.Error, phase, span, message));
        }

        public void Warning(DiagnosticPhase phase, CharRange? span, string message)
        {
            Add(new Diagnostic(DiagnosticSeverity.Warning, phase, span, message));
        }
    }
}
