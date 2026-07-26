using System;
using System.Collections.Generic;

namespace LatteCompiler
{
    /// <summary>
    /// 通用声明层：解析**任何位置**的任何声明，把结果挂到 parent.Children。
    ///
    /// 依据 SYNTAX.md §14.8：canonical symbol 的类名段可为空、`.static.` 只是标记位，
    /// 因此以下形态在结构上同构，全部由本层一套状态机处理，不各自造轮子：
    ///   - 全局字段 / 类字段        → 复用 VariableDeclarationParserLayer
    ///   - 全局函数 / 方法 / static → 同一套 Callable 状态
    ///   - operator / init          → 同上，只改 Kind
    ///   - 嵌套类型 / 顶层类型      → 类型体内递归 push 本层自身
    ///
    /// 复用的既有 Layer：VariableDeclaration / ParameterList / GenericParameters / TypeReference / CodeBlock。
    /// </summary>
    public class DeclarationParserLayer : IParserLayer
    {
        private readonly ASTNode parent;

        public DeclarationParserLayer(ASTNode parent)
        {
            this.parent = parent;
        }

        private enum State
        {
            Modifiers,        // 收集修饰符，遇关键字分派
            CallableName,     // func/operator 已读：等待名称（init 跳过）
            ParamsExpected,   // 等待 (
            AfterParams,      // ) 已读：: 转返回类型，{ 转体，否则无体收尾
            ReturnType,       // : 已读：委托 TypeReference
            AfterReturnType,  // 返回类型已读：{ 转体，否则无体收尾
            TypeName,         // class/struct/... 已读：等待类型名
            AfterTypeName,    // 类型名已读：: 基类，implements 接口，{ 体
            BaseExpected,     // : 已读：委托 TypeReference 解析基类
            InterfaceExpected,// implements/, 已读：委托 TypeReference 解析接口
            AfterBase,        // 基类/接口已读：, 继续，implements 转接口，{ 体
            BodyContent,      // 类型体内：} 结束，否则递归 push 本层解析成员
            Finish            // 收尾：交还当前 token 并弹栈
        }

        private State state = State.Modifiers;
        private readonly List<string> modifiers = new List<string>();
        private CallableDeclarationASTNode? callable;
        private ASTNode? typeNode;                 // 正在解析的类型声明节点
        private List<TypeReferenceASTNode>? interfaceList;
        private bool enumSeen;

        public ParserLayerResult ParseToken(Token t, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Modifiers: return OnModifiers(t, context);
                case State.CallableName: return OnCallableName(t, context);
                case State.ParamsExpected: return OnParamsExpected(t, context);
                case State.AfterParams: return OnAfterParams(t, context);
                case State.ReturnType: return OnReturnType(t, context);
                case State.AfterReturnType: return OnAfterReturnType(t, context);
                case State.TypeName: return OnTypeName(t, context);
                case State.AfterTypeName: return OnAfterTypeName(t, context);
                case State.BaseExpected: return OnBaseExpected(t, context);
                case State.InterfaceExpected: return OnInterfaceExpected(t, context);
                case State.AfterBase: return OnAfterBase(t, context);
                case State.BodyContent: return OnBodyContent(t, context);
                case State.Finish: return new ParserLayerResult.PopLayer(true);
                default:
                    throw context.RaiseError($"Invalid DeclarationParserLayer state: {state}");
            }
        }

        // 修饰符收集 + 关键字分派。这里是"全局/成员/嵌套同构"的关键：
        // 三种位置进入的都是本状态，后续路径完全一致。
        private ParserLayerResult OnModifiers(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is WordToken w)
            {
                if (Keywords.IsDescriptor(w.Content))
                {
                    modifiers.Add(w.Content);
                    return ParserLayerResult.Continue.Instance;
                }

                // 字段 / 全局变量：直接复用 VariableDeclarationParserLayer
                if (w.Content == Keywords.VAR || w.Content == Keywords.CONST)
                {
                    var v = new VariableDeclarationASTNode(parent);
                    v.Modifiers.AddRange(modifiers);
                    parent.Children.Add(v);
                    state = State.Finish;
                    return new ParserLayerResult.PushLayer(
                        new VariableDeclarationParserLayer(v), true);
                }

                if (w.Content == Keywords.FUNC) return StartCallable(CallableKind.Func, State.CallableName);
                if (w.Content == Keywords.OPERATOR) return StartCallable(CallableKind.Operator, State.CallableName);
                if (w.Content == Keywords.INIT)
                {
                    var r = StartCallable(CallableKind.Init, State.ParamsExpected);
                    callable!.Name = Keywords.INIT;
                    return r;
                }

                // enum struct：吃掉 enum，struct 由下一轮处理
                if (w.Content == Keywords.ENUM)
                {
                    enumSeen = true;
                    return ParserLayerResult.Continue.Instance;
                }

                if (Keywords.IsTypeKeyword(w.Content))
                {
                    typeNode = CreateTypeNode(w.Content, context);
                    state = State.TypeName;
                    return ParserLayerResult.Continue.Instance;
                }

                throw context.RaiseError($"Expected modifier or declaration keyword, got: {w.Content}");
            }

            throw context.RaiseError($"Unexpected token in declaration: {t}");
        }

        private ParserLayerResult StartCallable(CallableKind kind, State next)
        {
            callable = new CallableDeclarationASTNode(parent) { Kind = kind };
            callable.Modifiers.AddRange(modifiers);
            parent.Children.Add(callable);
            state = next;
            return ParserLayerResult.Continue.Instance;
        }

        // ===== Callable：func / operator / init 共用，全局与成员共用 =====

        private ParserLayerResult OnCallableName(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            if (t is WordToken w)
            {
                callable!.Name = w.Content;
                state = State.ParamsExpected;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Expected declaration name, got: {t}");
        }

        private ParserLayerResult OnParamsExpected(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            if (t is NotationToken n)
            {
                if (n.Content == "(")
                {
                    state = State.AfterParams;
                    // 复用 ParameterListParserLayer（它自己吃掉 '(' 到 ')'）
                    return new ParserLayerResult.PushLayer(
                        new ParameterListParserLayer(callable!.Parameters), true);
                }
                if (n.Content == "\\")
                {
                    callable!.GenericParameters = new GenericParameterListASTNode(callable);
                    // 复用 GenericParametersParserLayer（它自己吃掉 \< 到 >）；
                    // 状态保持 ParamsExpected：泛型列表弹出后仍等待 (
                    return new ParserLayerResult.PushLayer(
                        new GenericParametersParserLayer(callable.GenericParameters), true);
                }
            }
            throw context.RaiseError($"Expected '(' in declaration, got: {t}");
        }

        private ParserLayerResult OnAfterParams(Token t, ParserLayerContext context)
        {
            if (t is NotationToken n)
            {
                if (n.Content == ":")
                {
                    state = State.ReturnType;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == "{") return PushBody();
            }
            // 换行/其他：无体声明（接口方法、抽象方法、init 映射形态）
            return new ParserLayerResult.PopLayer(t is not LineBreakToken);
        }

        private ParserLayerResult OnReturnType(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            callable!.ReturnType = new TypeReferenceASTNode(callable);
            state = State.AfterReturnType;
            // 复用 TypeReferenceParserLayer
            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(callable.ReturnType), true);
        }

        private ParserLayerResult OnAfterReturnType(Token t, ParserLayerContext context)
        {
            if (t is NotationToken n && n.Content == "{") return PushBody();
            return new ParserLayerResult.PopLayer(t is not LineBreakToken);
        }

        private ParserLayerResult PushBody()
        {
            callable!.Body = new CodeBlockASTNode(callable);
            state = State.Finish;
            // 复用 CodeBlockParserLayer（它自己吃掉 '{' 到 '}'）
            return new ParserLayerResult.PushLayer(
                new CodeBlockParserLayer(callable.Body), true);
        }

        // ===== 类型声明：顶层与嵌套共用同一路径 =====

        private ASTNode CreateTypeNode(string keyword, ParserLayerContext context)
        {
            ASTNode node = keyword switch
            {
                Keywords.CLASS => new ClassDeclarationASTNode(parent),
                Keywords.INTERFACE => new InterfaceDeclarationASTNode(parent),
                Keywords.STRUCT => enumSeen
                    ? new EnumStructDeclarationASTNode(parent)
                    : new StructDeclarationASTNode(parent),
                Keywords.WRAPPER => new WrapperDeclarationASTNode(parent),
                _ => throw context.RaiseError($"Unsupported type keyword: {keyword}")
            };
            GetModifiers(node).AddRange(modifiers);
            parent.Children.Add(node);
            return node;
        }

        // 各类型节点的同名成员访问集中在此，避免调用点到处 switch
        private static List<string> GetModifiers(ASTNode n) => n switch
        {
            ClassDeclarationASTNode c => c.Modifiers,
            InterfaceDeclarationASTNode i => i.Modifiers,
            StructDeclarationASTNode s => s.Modifiers,
            EnumStructDeclarationASTNode e => e.Modifiers,
            WrapperDeclarationASTNode w => w.Modifiers,
            _ => new List<string>()
        };

        private void SetTypeName(string name)
        {
            switch (typeNode)
            {
                case ClassDeclarationASTNode c: c.ClassName = name; break;
                case InterfaceDeclarationASTNode i: i.InterfaceName = name; break;
                case StructDeclarationASTNode s: s.StructName = name; break;
                case EnumStructDeclarationASTNode e: e.EnumName = name; break;
                case WrapperDeclarationASTNode w: w.WrapperName = name; break;
            }
        }

        // 与 SetTypeName 同理：5 个类型节点的同名 GenericParameters 字段集中分派
        private static void SetGenericParameters(ASTNode node, GenericParameterListASTNode gp)
        {
            switch (node)
            {
                case ClassDeclarationASTNode c: c.GenericParameters = gp; break;
                case InterfaceDeclarationASTNode i: i.GenericParameters = gp; break;
                case StructDeclarationASTNode s: s.GenericParameters = gp; break;
                case EnumStructDeclarationASTNode e: e.GenericParameters = gp; break;
                case WrapperDeclarationASTNode w: w.GenericParameters = gp; break;
            }
        }

        private ParserLayerResult OnTypeName(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            if (t is WordToken w)
            {
                SetTypeName(w.Content);
                state = State.AfterTypeName;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Expected type name, got: {t}");
        }

        private ParserLayerResult OnAfterTypeName(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n)
            {
                if (n.Content == "\\")
                {
                    // 类型名后的泛型形参列表：复用 GenericParametersParserLayer；
                    // 状态保持 AfterTypeName：泛型列表弹出后仍等待 : / implements / {
                    var gp = new GenericParameterListASTNode(typeNode);
                    SetGenericParameters(typeNode!, gp);
                    return new ParserLayerResult.PushLayer(new GenericParametersParserLayer(gp), true);
                }
                if (n.Content == ":")
                {
                    state = State.BaseExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == "{")
                {
                    state = State.BodyContent;
                    return ParserLayerResult.Continue.Instance;
                }
            }
            if (t is WordToken iw && iw.Content == Keywords.IMPLEMENTS)
            {
                state = State.InterfaceExpected;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Unexpected token after type name: {t}");
        }

        // 基类：interface 的 `:` 列表本身就是父接口，故按节点种类落位
        private ParserLayerResult OnBaseExpected(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            var tr = new TypeReferenceASTNode(typeNode);
            switch (typeNode)
            {
                case ClassDeclarationASTNode c: c.BaseClass = tr; break;
                case StructDeclarationASTNode s: s.BaseStruct = tr; break;
                case InterfaceDeclarationASTNode i: i.BaseInterfaces.Add(tr); break;
                default:
                    throw context.RaiseError("This declaration cannot have a base type");
            }
            state = State.AfterBase;
            return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(tr), true);
        }

        private ParserLayerResult OnInterfaceExpected(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            interfaceList ??= typeNode switch
            {
                ClassDeclarationASTNode c => c.Interfaces,
                StructDeclarationASTNode s => s.Interfaces,
                InterfaceDeclarationASTNode i => i.BaseInterfaces,
                _ => throw context.RaiseError("This declaration cannot implement interfaces")
            };

            var tr = new TypeReferenceASTNode(typeNode);
            interfaceList.Add(tr);
            state = State.AfterBase;
            return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(tr), true);
        }

        private ParserLayerResult OnAfterBase(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n)
            {
                if (n.Content == ",") return OnInterfaceExpectedNext();
                if (n.Content == "{")
                {
                    state = State.BodyContent;
                    return ParserLayerResult.Continue.Instance;
                }
            }
            if (t is WordToken w && w.Content == Keywords.IMPLEMENTS)
            {
                interfaceList = null;   // 切换到 implements 列表
                state = State.InterfaceExpected;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Unexpected token in inheritance clause: {t}");
        }

        private ParserLayerResult OnInterfaceExpectedNext()
        {
            state = State.InterfaceExpected;
            return ParserLayerResult.Continue.Instance;
        }

        // 类型体：成员与嵌套类型都递归 push 本层自身 —— 成员/嵌套/全局共用一套逻辑
        private ParserLayerResult OnBodyContent(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n && n.Content == "}")
                return new ParserLayerResult.PopLayer(false);

            return new ParserLayerResult.PushLayer(
                new DeclarationParserLayer(typeNode!), true);
        }
    }
}
