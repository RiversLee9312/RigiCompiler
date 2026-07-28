using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    // Token 处置方式（具名枚举，替代原 bool shouldKeepToken）
    public enum TokenDisposition
    {
        Consume,    // 当前 token 已被本层消费，Parser 前进到下一个 token
        Replay      // 当前 token 原样交给 Push/Pop 后的新栈顶 Layer 重新处理
    }

    public abstract record ParserLayerResult
    {
        // 使用 sealed record 来防止进一步继承
        public sealed record PopLayer(TokenDisposition Disposition) : ParserLayerResult;

        public sealed record PushLayer(IParserLayer LayerToPush, TokenDisposition Disposition) : ParserLayerResult;

        // 使用单例模式的 Continue 记录
        public sealed record Continue : ParserLayerResult
        {
            private Continue() { }
            public static readonly ParserLayerResult Instance = new Continue();
        }
    }
    public interface IParserLayer
    {
        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context);
    }

    // Span 接收者（M28）：Parser 主循环在层弹出时，把该层消费的 token 范围
    // 计算为 CharRange 回调给层；层用它回填施工目标的 Span
    // （约定 target.Span ??= span——层内显式设置的 span 优先，层 span 只填空）。
    public interface ISpanReceiver
    {
        public void ReceiveSpan(CharRange span);
    }

    public struct CharRange
    {
        public CharPosition Start = new CharPosition();
        public CharPosition End = new CharPosition();

        public CharRange()
        {
        }
        public string sourceName = "";
    }
    public abstract class ParserLayerContext
    {
        public abstract CharRange GetLocation();
        // 最近被消费的 token 范围（注释跳过不算消费；Replay 未前进不算消费）。
        // 当前 token 不属于本结构时（Replay 弹出、语句终于换行），用它取前一 token 位置封 End。
        public abstract CharRange GetPreviousLocation();
        [DoesNotReturn]
        public abstract Exception RaiseError(string message);
        public abstract void LogWarning(string message);
        public abstract void Log(string message);
    }



    public class Parser
    {
        private class ContextImpl : ParserLayerContext
        {
            public CharRange currentRange = new CharRange();
            // 最近被消费的 token 范围（由主循环在前进时维护）
            public CharRange lastConsumedRange = new CharRange();

            public override CharRange GetLocation()
            {
                return currentRange;
            }

            public override CharRange GetPreviousLocation()
            {
                return lastConsumedRange;
            }

            // 日志统一走 Logger（禁止直接 Console.WriteLine）；
            // source 标识子系统便于 grep，位置信息保留在 message 前缀里
            public override void Log(string message)
            {
                Logger.Verbose("Parser", $"[{currentRange.sourceName}][[Line {currentRange.Start.line} Col {currentRange.Start.column}]->[Line {currentRange.End.line} Col {currentRange.End.column}]] {message}");
            }

            public override void LogWarning(string message)
            {
                Logger.Warning("Parser", $"[{currentRange.sourceName}][[Line {currentRange.Start.line} Col {currentRange.Start.column}]->[Line {currentRange.End.line} Col {currentRange.End.column}]] {message}");
            }

            [DoesNotReturn]
            public override Exception RaiseError(string message)
            {
                throw new ParserException($"ERROR [{currentRange.sourceName}][[Line {currentRange.Start.line} Col {currentRange.Start.column}]->[Line {currentRange.End.line} Col {currentRange.End.column}]] {message}");
            }
        }
        public ASTNode Parse(List<Token> tokens)
        {
            return Parse(tokens, null);
        }

        // entryLayer 不为 null 时：在 Root 之上推入指定起始层。
        // 用于独立测试某个 ParserLayer（Root 垫底，吞掉该层完成后的剩余 token）。
        public ASTNode Parse(List<Token> tokens, IParserLayer? entryLayer)
        {
            // Parser 直接持有 Root 节点：Layer 无法经由 Context 触碰全局根，
            // 只能施工传入的目标
            var root = new RootASTNode();
            ParseCore(tokens, new RootParserLayer(root), entryLayer);
            // Root span：整文件范围（首 token Start → 末 token/EOF End）；
            // RootParserLayer 永不弹栈，不走 ISpanReceiver
            if (tokens.Count > 0)
            {
                root.Span = new CharRange
                {
                    Start = tokens[0].CharRange.Start,
                    End = tokens[tokens.Count - 1].CharRange.End,
                    sourceName = tokens[0].CharRange.sourceName
                };
            }
            else
            {
                // 空 token 流（绕过 Lexer 的手工调用方）：零宽 span，
                // 保证 Validator 的 span 校验通过（M31）
                root.Span = new CharRange { sourceName = "<empty>" };
            }
            // Parser 成功后、进入后续阶段前：AST 完整性验证（失败即内部编译器错误）
            ASTIntegrityValidator.Validate(root);
            return root;
        }

        // 测试专用：以 baseLayer 代替 RootParserLayer 垫底驱动被测 Layer
        // （TestRootParserLayer 只接受 EOF：被测 Layer 提前结束或漏消费 token
        // 会立即暴露）。被测目标的校验由调用方自行处理。
        public void Parse(List<Token> tokens, IParserLayer baseLayer, IParserLayer entryLayer)
        {
            ParseCore(tokens, baseLayer, entryLayer);
        }

        // 层栈帧（M28）：层 + 首个分发给该层的 token 范围；
        // 层弹出时据以计算该层消费的 token span，回填给 ISpanReceiver
        private sealed class LayerFrame
        {
            public required IParserLayer Layer { get; init; }
            public CharRange? FirstRange { get; set; }
        }

        // 主循环：只负责 Layer 栈与 Token 调度；Layer 之间只传递控制权，
        // 不传递任何 AST 数据（施工目标在 Push 前已由父层确定）
        private void ParseCore(List<Token> tokens, IParserLayer baseLayer, IParserLayer? entryLayer)
        {
            var context = new ContextImpl();
            var stack = new Stack<LayerFrame>();
            int offset = 0;
            // Lexer 已在输出末尾追加正式 EOF token（M25）；
            // 对绕过 Lexer 手工构造 token 流的调用方（如协议测试）保持末尾追加的兼容
            var input = tokens.Count > 0 && tokens[tokens.Count - 1] is EndOfFileToken
                ? tokens
                : new List<Token>(tokens) { CreateEndOfFileToken(tokens) };
            stack.Push(new LayerFrame { Layer = baseLayer });
            if (entryLayer != null)
            {
                stack.Push(new LayerFrame { Layer = entryLayer });
            }
            while(offset<input.Count) {
                var token = input[offset];
                // 注释 token 不参与语法：Parser 分发时统一跳过，
                // 各 ParserLayer 不再自行处理（M25）
                if (token is CommentToken)
                {
                    offset++;
                    continue;
                }
                context.Log("Current token:"+token);
                context.currentRange = token.CharRange;
                LayerFrame? frame;
                if (stack.TryPeek(out frame)) {
                    // 记录首个分发给该层的 token 范围（层 span 的起点）
                    frame.FirstRange ??= token.CharRange;
                    // 主循环只负责 Layer 栈与 Token 调度：Layer 之间只传递控制权，
                    // 不传递任何 AST 数据（施工目标在 Push 前已由父层确定）
                    var result = frame.Layer.ParseToken(token,context);
                    var keepToken = false;
                    switch (result) {
                        case ParserLayerResult.Continue:
                            keepToken = false;
                            break;
                        case ParserLayerResult.PopLayer r:
                            keepToken = r.Disposition == TokenDisposition.Replay;
                            var popped = stack.Pop();
                            context.Log("Popped parser layer:" + popped.Layer);
                            // 层弹出：把该层消费的 token 范围回填给施工目标（M28）
                            if (popped.Layer is ISpanReceiver receiver && popped.FirstRange is { } firstRange)
                            {
                                receiver.ReceiveSpan(ComputeLayerSpan(firstRange, token, r.Disposition, context));
                            }
                            break;
                        case ParserLayerResult.PushLayer r:
                            keepToken = r.Disposition == TokenDisposition.Replay;
                            stack.Push(new LayerFrame { Layer = r.LayerToPush });
                            context.Log("Pushed parser layer:" + r.LayerToPush);
                            break;
                    }
                    if (!keepToken)
                    {
                        context.lastConsumedRange = token.CharRange;
                        offset++;
                    }
                }
                else
                {
                    throw context.RaiseError("Empty parser stack");
                }
            }
            // EOF 自身必须已完成全部栈收敛：唯一剩余项是垫底 Layer
            if (stack.Count != 1)
            {
                throw context.RaiseError("Unexpected End");
            }
        }

        // 层 span（M28）：首个分发 token 的 Start → 最后一个属于该层的 token 的 End。
        // 左闭右开 [Start, End)（M31 起）：继承 token 流的开区间语义，无需换算。
        // Consume 弹出且当前 token 非换行：当前 token 属于该层；
        // 其余（Replay 弹出、换行处 Consume 弹出）：span 终于最近被消费的 token——
        // 声明/语句的 span 不拖尾换行符到下一行。
        private static CharRange ComputeLayerSpan(
            CharRange firstRange, Token currentToken, TokenDisposition disposition, ParserLayerContext context)
        {
            var start = firstRange.Start;
            CharPosition end;
            if (disposition == TokenDisposition.Consume && currentToken is not LineBreakToken)
            {
                end = currentToken.CharRange.End;
            }
            else
            {
                end = context.GetPreviousLocation().End;
            }
            // 防御：层未消费任何 token（Replay 进 Replay 出）或位置倒置 → 起点处零宽 span
            if (end.offset < start.offset)
            {
                end = start;
            }
            return new CharRange { Start = start, End = end, sourceName = firstRange.sourceName };
        }

        // EOF 的 CharRange 是零长度范围，位置位于源文件最后一个 Token 的结束位置
        private static EndOfFileToken CreateEndOfFileToken(List<Token> tokens)
        {
            var eof = new EndOfFileToken();
            if (tokens.Count > 0)
            {
                var last = tokens[tokens.Count - 1].CharRange;
                eof.CharRange = new CharRange
                {
                    Start = last.End,
                    End = last.End,
                    sourceName = last.sourceName
                };
            }
            return eof;
        }
    }
}
