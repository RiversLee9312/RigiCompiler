using System;

namespace LatteCompiler
{
    /// <summary>
    /// Seq 块解析器（roadmap #11，P2）
    ///
    /// 语法（SYNTAX.md §6）：
    /// [volatile] seq [using(...)]* [named label] { ... }
    ///
    /// - volatile 修饰符：标记块中操作为 volatile
    /// - using 资源绑定：using(const/var name = initializer)，可有多个
    /// - named 标签：配合 return@label 使用
    /// - 代码块：标准代码块，委托 CodeBlockParserLayer
    ///
    /// seq 可作为语句或表达式使用：
    /// - 作为表达式时，必须通过 return@seq 或 return@label 返回值
    /// - 作为语句时，可以不返回值
    ///
    /// 施工协议（大扫除后）：构造函数接收父层创建好的 SeqBlockExpressionASTNode
    /// （表达式位置由 ExpressionParserLayer 持有为当前表达式，语句位置已挂入代码块），
    /// 本层只原地填充它，不产生任何返回值。
    ///
    /// 状态流转：
    /// Initial → [Volatile] → SeqKeyword → UsingOrNamed
    ///   → [UsingOpenParen → UsingVarConst → UsingName → UsingColon → UsingType → UsingEquals
    ///      → UsingInitializer → UsingCloseParen] → UsingOrNamed（继续或进入 Named/Body）
    ///   → [Named → NamedLabel] → Body（委托 CodeBlockParserLayer）→ Completed
    /// </summary>
    public class SeqBlockParserLayer : IParserLayer
    {
        private readonly SeqBlockExpressionASTNode seqNode;

        private enum State
        {
            Initial,           // 初始状态：可选 volatile 或直接 seq
            Volatile,          // volatile 已读，等待 seq
            SeqKeyword,        // seq 关键字已读，检查 using/named/body
            UsingOrNamed,      // 检查是否有 using 或 named 或直接进入 body
            UsingOpenParen,    // 等待 using 的 (
            UsingVarConst,     // 等待 const/var
            UsingName,         // 等待变量名
            UsingColon,        // 等待可选的 :（类型标注）或 =
            UsingType,         // 解析类型（委托中）
            UsingEquals,       // 等待 =
            UsingInitializer,  // 解析初始化表达式（委托中）
            UsingCloseParen,   // 等待 )
            Named,             // named 关键字已读，等待标签名
            NamedLabel,        // 标签名已读，等待 body
            Body,              // 解析代码块（委托中）
            Completed
        }

        private State state = State.Initial;
        private UsingBindingASTNode? currentUsing;

        public SeqBlockParserLayer(SeqBlockExpressionASTNode target)
        {
            seqNode = target;
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：seq 块必须由 } 闭合，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            switch (state)
            {
                case State.Initial:
                    return HandleInitial(currentToken, context);
                case State.Volatile:
                    return HandleVolatile(currentToken, context);
                case State.SeqKeyword:
                    return HandleSeqKeyword(currentToken, context);
                case State.UsingOrNamed:
                    return HandleUsingOrNamed(currentToken, context);
                case State.UsingOpenParen:
                    return HandleUsingOpenParen(currentToken, context);
                case State.UsingVarConst:
                    return HandleUsingVarConst(currentToken, context);
                case State.UsingName:
                    return HandleUsingName(currentToken, context);
                case State.UsingColon:
                    return HandleUsingColon(currentToken, context);
                case State.UsingType:
                    return HandleUsingType(currentToken, context);
                case State.UsingEquals:
                    return HandleUsingEquals(currentToken, context);
                case State.UsingInitializer:
                    return HandleUsingInitializer(currentToken, context);
                case State.UsingCloseParen:
                    return HandleUsingCloseParen(currentToken, context);
                case State.Named:
                    return HandleNamed(currentToken, context);
                case State.NamedLabel:
                    return HandleNamedLabel(currentToken, context);
                case State.Body:
                    return HandleBody(currentToken, context);
                case State.Completed:
                    return HandleCompleted(currentToken, context);
                default:
                    context.RaiseError($"Invalid SeqBlockParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                // volatile 修饰符
                if (wt.Content == Keywords.VOLATILE)
                {
                    seqNode.IsVolatile = true;
                    state = State.Volatile;
                    return ParserLayerResult.Continue.Instance;
                }

                // 直接是 seq
                if (wt.Content == Keywords.SEQ)
                {
                    state = State.SeqKeyword;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected 'volatile' or 'seq', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleVolatile(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt && wt.Content == Keywords.SEQ)
            {
                state = State.SeqKeyword;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'seq' after 'volatile', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleSeqKeyword(Token currentToken, ParserLayerContext context)
        {
            // seq 已读，现在检查 using/named/body
            state = State.UsingOrNamed;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleUsingOrNamed(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                // using 子句
                if (wt.Content == Keywords.USING)
                {
                    currentUsing = new UsingBindingASTNode(seqNode);
                    state = State.UsingOpenParen;
                    return ParserLayerResult.Continue.Instance;
                }

                // named 标签
                if (wt.Content == Keywords.NAMED)
                {
                    state = State.Named;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            // 直接进入 body
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.Body;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(seqNode.Body), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected 'using', 'named', or '{{' after 'seq', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleUsingOpenParen(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                state = State.UsingVarConst;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '(' after 'using', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleUsingVarConst(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                if (wt.Content == Keywords.CONST)
                {
                    currentUsing!.IsConst = true;
                    state = State.UsingName;
                    return ParserLayerResult.Continue.Instance;
                }

                if (wt.Content == Keywords.VAR)
                {
                    currentUsing!.IsConst = false;
                    state = State.UsingName;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected 'const' or 'var' in using clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleUsingName(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                currentUsing!.VariableName = wt.Content;
                state = State.UsingColon;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected variable name in using clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleUsingColon(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 可选类型标注
            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.UsingType;
                currentUsing!.Type = new TypeReferenceASTNode(currentUsing);
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(currentUsing!.Type), TokenDisposition.Consume);
            }

            // 直接到 =
            if (currentToken is NotationToken nt2 && nt2.Content == "=")
            {
                state = State.UsingInitializer;
                return new ParserLayerResult.PushLayer(
                    new ExpressionParserLayer(currentUsing!.Initializer), TokenDisposition.Consume);
            }

            context.RaiseError($"Expected ':' or '=' in using clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleUsingType(Token currentToken, ParserLayerContext context)
        {
            // 类型已由 TypeReferenceParserLayer 解析，现在等待 =
            state = State.UsingEquals;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleUsingEquals(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                state = State.UsingInitializer;
                return new ParserLayerResult.PushLayer(
                    new ExpressionParserLayer(currentUsing!.Initializer), TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '=' in using clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleUsingInitializer(Token currentToken, ParserLayerContext context)
        {
            // 初始化表达式已解析，现在等待 )
            state = State.UsingCloseParen;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleUsingCloseParen(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                // 保存当前 using 绑定
                seqNode.UsingBindings.Add(currentUsing!);
                currentUsing = null;

                // 继续检查是否有更多 using 或进入 named/body
                state = State.UsingOrNamed;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ')' after using initializer, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleNamed(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                // 验证标签名首字符不是数字
                if (char.IsDigit(wt.Content[0]))
                {
                    context.RaiseError($"Label name cannot start with a digit: {wt.Content}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                }

                seqNode.Label = wt.Content;
                state = State.NamedLabel;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label name after 'named', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleNamedLabel(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 进入 body
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.Body;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(seqNode.Body), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' after named label, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleBody(Token currentToken, ParserLayerContext context)
        {
            // 代码块已解析完成
            state = State.Completed;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleCompleted(Token currentToken, ParserLayerContext context)
        {
            // seq 节点的归属在父层创建时已确定（表达式位置由 ExpressionParserLayer 持有，
            // 语句位置已挂入代码块），本层只原地填充，弹出即完成
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
        }
    }
}
