using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    /// <summary>
    /// import 语句解析器（SYNTAX.md §15.2，P5 重建）
    ///
    /// 规范的三种形态：
    ///   import core.collections.List             // 单个导入
    ///   import core.collections.{List, Map}      // 多个导入（共享前缀路径）
    ///   import core.collections.*                // 全部导入
    ///
    /// 前缀路径复用 PathParserLayer 解析：它遇 `*` / `{` / 换行会弹出并交还 token；
    /// `.*` / `.{` 前被吞下的 `.` 会在符号末尾留下空名元素，弹出后统一清理
    /// （与 GenericParameters 层对 `...` 残留空名元素的既有处理同款）。
    /// 多导入列表中的每一项展开为独立的完整路径 ImportItem。
    ///
    /// 状态流转：
    /// ImportKeyword → PathStart →（PathParserLayer 弹出）→ AfterPath
    ///   → 换行：单导入完成，弹栈
    ///   → * ：记录 importAll，WaitEnd 等换行弹栈
    ///   → { ：ListItem → AfterListItem（`,` 循环 / `}` → WaitEnd）
    /// </summary>
    public class ImportParserLayer : IParserLayer, ISpanReceiver
    {
        private enum State
        {
            ImportKeyword,   // 等待 import 关键字（RootParserLayer 以 keepToken 传入）
            PathStart,       // 等待路径起点：委托 PathParserLayer
            AfterPath,       // 路径已解析：换行（单导入）/ *（全导入）/ {（多导入列表）
            ListItem,        // {} 列表内：等待标识符（} 空列表报错）
            AfterListItem,   // 列表项已读：, 下一项 / } 结束
            WaitEnd          // 收尾：等待换行弹栈
        }

        private readonly ImportASTNode self;
        private State state = State.ImportKeyword;
        private SymbolASTNode? pathSymbol;      // 前缀路径（单导入时即完整路径）
        private bool pathFinalized;             // 空名尾元素是否已清理

        public ImportParserLayer(ImportASTNode self)
        {
            this.self = self;
        }

        // 层弹出时回填 import 节点的源码范围（M28）
        public void ReceiveSpan(CharRange span) => self.Span ??= span;

        public ParserLayerResult ParseToken(Token t, ParserLayerContext context)
        {
            switch (state)
            {
                case State.ImportKeyword: return OnImportKeyword(t, context);
                case State.PathStart: return OnPathStart(t, context);
                case State.AfterPath: return OnAfterPath(t, context);
                case State.ListItem: return OnListItem(t, context);
                case State.AfterListItem: return OnAfterListItem(t, context);
                case State.WaitEnd: return OnWaitEnd(t, context);
                default:
                    throw context.RaiseError($"Invalid ImportParserLayer state: {state}");
            }
        }

        private ParserLayerResult OnImportKeyword(Token t, ParserLayerContext context)
        {
            if (t is WordToken w && w.Content == Keywords.IMPORT)
            {
                state = State.PathStart;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Expected 'import', got: {t}");
        }

        // 路径起点：委托 PathParserLayer 解析 a.b.c 路径（复用轮子）
        private ParserLayerResult OnPathStart(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken)
                throw context.RaiseError("Import statement requires an import path (SYNTAX §15.2)");

            pathSymbol = new SymbolASTNode(self);
            state = State.AfterPath;
            return new ParserLayerResult.PushLayer(
                new PathParserLayer(pathSymbol, lineBreakSensitive: true), TokenDisposition.Replay);
        }

        // PathParserLayer 弹出后：清末尾空名元素（`.*` / `.{` 前的 . 残留，只清一次）
        private void FinalizePath()
        {
            if (pathFinalized) return;
            pathFinalized = true;
            var elements = pathSymbol!.symbol.elements;
            if (elements.Count > 0 && elements[elements.Count - 1].name.Length == 0)
                elements.RemoveAt(elements.Count - 1);
        }

        private ParserLayerResult OnAfterPath(Token t, ParserLayerContext context)
        {
            FinalizePath();
            var elements = pathSymbol!.symbol.elements;

            // 换行：单个导入完成
            if (t is LineBreakToken)
            {
                if (elements.Count == 0)
                    throw context.RaiseError("Import statement requires an import path (SYNTAX §15.2)");
                self.importedSymbols.Add(new ImportItem { symbolNode = pathSymbol });
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // EOF：单个导入完成（结构完整），EOF 上交 Root
            if (t is EndOfFileToken)
            {
                if (elements.Count == 0)
                    throw context.RaiseError("Import statement requires an import path (SYNTAX §15.2)");
                self.importedSymbols.Add(new ImportItem { symbolNode = pathSymbol });
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            if (t is NotationToken n)
            {
                if (elements.Count == 0)
                    throw context.RaiseError("Import statement requires an import path (SYNTAX §15.2)");

                // 全部导入：import a.b.*
                if (n.Content == "*")
                {
                    self.importedSymbols.Add(
                        new ImportItem { symbolNode = pathSymbol, importAll = true });
                    state = State.WaitEnd;
                    return ParserLayerResult.Continue.Instance;
                }

                // 多个导入：import a.b.{X, Y}（共享前缀路径）
                if (n.Content == "{")
                {
                    state = State.ListItem;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            throw context.RaiseError($"Unexpected token in import statement: {t}");
        }

        // {} 列表内：等待标识符（每项展开为 前缀 + 名称 的完整路径）
        private ParserLayerResult OnListItem(Token t, ParserLayerContext context)
        {
            if (t is WordToken w)
            {
                var itemSymbol = new SymbolASTNode(self);
                foreach (var el in pathSymbol!.symbol.elements)
                    itemSymbol.symbol.elements.Add(el);
                itemSymbol.symbol.elements.Add(new SymbolElement { name = w.Content });
                // 列表项 span：标识符 token 自身的范围（M28）
                var loc = context.GetLocation();
                itemSymbol.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
                self.importedSymbols.Add(new ImportItem { symbolNode = itemSymbol });
                state = State.AfterListItem;
                return ParserLayerResult.Continue.Instance;
            }
            if (t is NotationToken n && n.Content == "}")
                throw context.RaiseError("Import list cannot be empty (SYNTAX §15.2)");

            throw context.RaiseError($"Expected identifier in import list, got: {t}");
        }

        private ParserLayerResult OnAfterListItem(Token t, ParserLayerContext context)
        {
            if (t is NotationToken n)
            {
                if (n.Content == ",")
                {
                    state = State.ListItem;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == "}")
                {
                    state = State.WaitEnd;
                    return ParserLayerResult.Continue.Instance;
                }
            }
            throw context.RaiseError($"Expected ',' or '}}' in import list, got: {t}");
        }

        // 收尾：import 是单行语句，只允许换行结束
        private ParserLayerResult OnWaitEnd(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken)
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);

            // EOF：import 语句已完整，EOF 上交 Root
            if (t is EndOfFileToken)
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);

            throw context.RaiseError($"Unexpected token after import statement: {t}");
        }
    }
}
