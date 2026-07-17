using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    public abstract record ParserLayerResult
    {
        // 使用 sealed record 来防止进一步继承
        public sealed record PopLayer(bool shouldKeepToken) : ParserLayerResult;

        public sealed record PushLayer(IParserLayer layerToPush, bool shouldKeepToken) : ParserLayerResult;

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

    // 结果传递机制：产生解析结果的 Layer 实现此接口
    // 当该 Layer 被弹出栈时，Parser 主循环会调用 GetResult() 取出结果
    public interface IResultProducer
    {
        public ASTNode? GetResult();
    }

    // 结果传递机制：接收子 Layer 结果的父 Layer 实现此接口
    // 子 Layer 弹出时，Parser 主循环调用 OnChildResult 把结果交给父层
    public interface IResultConsumer
    {
        public void OnChildResult(ASTNode? result, IParserLayer child);
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
        public abstract RootASTNode GetRootNode();
        public abstract void Log(string message);
    }



    public class Parser
    {
        private class ContextImpl : ParserLayerContext
        {
            public CharRange currentRange = new CharRange();
            public RootASTNode root;
            public ASTNode current;
            public ContextImpl()
            {
                root = new RootASTNode();
                current = root;
            }
            public override RootASTNode GetRootNode()
            {
                return root;
            }

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
            var context = new ContextImpl();
            var stack = new Stack<IParserLayer>();
            int offset = 0;
            tokens.Add(new LineBreakToken()); // Sentinel token
            stack.Push(new RootParserLayer(context.GetRootNode()));
            if (entryLayer != null)
            {
                stack.Push(entryLayer);
            }
            while(offset<tokens.Count) {
                var token = tokens[offset];
                context.Log("Current token:"+token);
                context.currentRange = token.CharRange;
                IParserLayer? layer;
                if (stack.TryPeek(out layer)) {
                    var result = layer.ParseToken(token,context);
                    var keepToken = false;
                    switch (result) {
                        case ParserLayerResult.Continue:
                            keepToken = false;
                            break;
                        case ParserLayerResult.PopLayer r:
                            keepToken = r.shouldKeepToken;
                            var popped = stack.Pop();
                            context.Log("Popped parser layer:" + popped);
                            // 结果传递：子层弹出时，把结果交给新的栈顶父层
                            if (popped is IResultProducer producer &&
                                stack.TryPeek(out var parentLayer) &&
                                parentLayer is IResultConsumer consumer)
                            {
                                consumer.OnChildResult(producer.GetResult(), popped);
                            }
                            break;
                        case ParserLayerResult.PushLayer r:
                            keepToken = r.shouldKeepToken;
                            stack.Push(r.layerToPush);
                            context.Log("Pushed parser layer:" + r.layerToPush);
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
            if (stack.Count > 1)
            {
                throw context.RaiseError("Unexpected End");
            }
            return context.GetRootNode();
        }
    }
}
