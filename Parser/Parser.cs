using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
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

            public override CharRange GetLocation()
            {
                return currentRange;
            }

            public override void Log(string message)
            {
                Console.WriteLine($"VERBOSE [{currentRange.sourceName}][[Line {currentRange.Start.line} Col {currentRange.Start.column}]->[Line {currentRange.End.line} Col {currentRange.End.column}]] {message}");
            }

            public override void LogWarning(string message)
            {
                Console.WriteLine($"WARNING [{currentRange.sourceName}][[Line {currentRange.Start.line} Col {currentRange.Start.column}]->[Line {currentRange.End.line} Col {currentRange.End.column}]] {message}");
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

        // 主循环：只负责 Layer 栈与 Token 调度；Layer 之间只传递控制权，
        // 不传递任何 AST 数据（施工目标在 Push 前已由父层确定）
        private void ParseCore(List<Token> tokens, IParserLayer baseLayer, IParserLayer? entryLayer)
        {
            var context = new ContextImpl();
            var stack = new Stack<IParserLayer>();
            int offset = 0;
            // 不修改调用者的 token 列表：本地副本末尾追加正式 EOF token
            var input = new List<Token>(tokens) { CreateEndOfFileToken(tokens) };
            stack.Push(baseLayer);
            if (entryLayer != null)
            {
                stack.Push(entryLayer);
            }
            while(offset<input.Count) {
                var token = input[offset];
                context.Log("Current token:"+token);
                context.currentRange = token.CharRange;
                IParserLayer? layer;
                if (stack.TryPeek(out layer)) {
                    // 主循环只负责 Layer 栈与 Token 调度：Layer 之间只传递控制权，
                    // 不传递任何 AST 数据（施工目标在 Push 前已由父层确定）
                    var result = layer.ParseToken(token,context);
                    var keepToken = false;
                    switch (result) {
                        case ParserLayerResult.Continue:
                            keepToken = false;
                            break;
                        case ParserLayerResult.PopLayer r:
                            keepToken = r.Disposition == TokenDisposition.Replay;
                            var popped = stack.Pop();
                            context.Log("Popped parser layer:" + popped);
                            break;
                        case ParserLayerResult.PushLayer r:
                            keepToken = r.Disposition == TokenDisposition.Replay;
                            stack.Push(r.LayerToPush);
                            context.Log("Pushed parser layer:" + r.LayerToPush);
                            break;
                    }
                    if (!keepToken)
                    {
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
