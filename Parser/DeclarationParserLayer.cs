using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    /// <summary>
    /// 通用声明层：解析**任何位置**的任何声明，把结果挂到 target（父节点的声明容器）。
    ///
    /// 依据 SYNTAX.md §14.8：canonical symbol 的类名段可为空、`.static.` 只是标记位，
    /// 因此以下形态在结构上同构，全部由本层一套状态机处理，不各自造轮子：
    ///   - 全局字段 / 类字段        → 复用 VariableDeclarationParserLayer
    ///   - 全局函数 / 方法 / static → 同一套 Callable 状态
    ///   - operator / init          → 同上，只改 Kind
    ///   - 嵌套类型 / 顶层类型      → 类型体内递归 push 本层自身
    ///   - enum struct 的 [case 列表] → 本层内联子状态（SYNTAX §12）
    ///
    /// 复用的既有 Layer：VariableDeclaration / ParameterList / GenericParameters / TypeReference / CodeBlock / ArgumentList。
    /// </summary>
    public class DeclarationParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly ASTNode parent;
        private readonly List<ASTNode> target;   // 声明挂接目标（root.Declarations / block.Statements / 类型节点.Members）

        public DeclarationParserLayer(ASTNode parent, List<ASTNode> target)
        {
            this.parent = parent;
            this.target = target;
        }

        // 施工中的声明节点（var/callable/各类类型声明）：层弹出时回填 span（M28）。
        // 直接赋值而非 ??=：本层 span 覆盖修饰符与注解，比子层
        // （如 VariableDeclarationParserLayer 以 var 关键字为起点）更完整，故覆盖
        private ASTNode? currentDeclaration;

        // 层弹出时回填声明节点的源码范围（M28）
        public void ReceiveSpan(CharRange span)
        {
            if (currentDeclaration != null)
            {
                currentDeclaration.Span = span;
            }
        }

        private enum State
        {
            Modifiers,        // 收集修饰符与注解，遇关键字分派
            AnnotationName,   // @ 已读、注解名已由 PathParserLayer 解析：判断有无 ( 实参
            CallableName,     // func/operator 已读：等待名称（init 跳过）
            CallableNameDot,  // ext 限定名的段间点已读：等待下一段名称
            ParamsExpected,   // 等待 (
            AfterParams,      // ) 已读：: 转返回类型，{ 转体，否则无体收尾
            ReturnType,       // : 已读：委托 TypeReference
            AfterReturnType,  // 返回类型已读：{ 转体，否则无体收尾
            TypeName,         // class/struct/... 已读：等待类型名
            AfterTypeName,    // 类型名已读：: 基类，implements 接口，like 委托，{ 体
            BaseExpected,     // : 已读：委托 TypeReference 解析基类
            InterfaceExpected,// implements/, 已读：委托 TypeReference 解析接口
            AfterBase,        // 基类/接口已读：, 继续，implements 转接口，like 委托，{ 体
            LikeExpected,     // like 已读：等待委托目标字段名（§9.6，仅 class）
            AfterLike,        // like 目标已读：等待 {
            BodyContent,      // 类型体内：} 结束，否则递归 push 本层解析成员
            EnumCaseListOpen, // enum 体 } 已读：[ 转 case 列表，否则无 case 收尾
            EnumCaseStart,    // case 列表内：等待 case 名或 ]
            EnumAfterName,    // case 名已读：( 实参 / -> 判别值 / , / ]
            EnumAfterArgs,    // 实参列表已读：-> 判别值 / , / ]
            EnumDiscriminant, // -> 已读：等待非负整数字面量
            EnumAfterCase,    // 判别值已读：, / ]
            Finish            // 收尾：交还当前 token 并弹栈
        }

        private State state = State.Modifiers;
        private readonly List<string> modifiers = new List<string>();
        // @ 注解暂存（SYNTAX §14.5）：注解先于声明本体解析，构造时暂无父节点，
        // 声明节点创建时由 AttachAnnotations 一次性 AttachTo 挂接
        private readonly List<AnnotationASTNode> pendingAnnotations = new List<AnnotationASTNode>();
        // 最后一个注解的 span 是否已封口（M28）：新注解创建时重置，封口时置位
        private bool lastAnnotationSealed = true;
        private CallableDeclarationASTNode? callable;
        private ASTNode? typeNode;                 // 正在解析的类型声明节点
        private List<TypeReferenceASTNode>? interfaceList;
        private bool enumSeen;
        private bool extSeen;                    // 修饰符含 ext：允许限定名（Type.member，§4.4）
        private bool proxyNameSeen;              // operator 名以 . 开头（wrapper proxy 成员，§14.2）
        private bool wildcardSeen;               // proxy 名已读 * 段（wildcard 必须收尾）
        private EnumCaseASTNode? currentCase;      // 正在解析的 enum case

        public ParserLayerResult ParseToken(Token t, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Modifiers: return OnModifiers(t, context);
                case State.AnnotationName: return OnAnnotationName(t, context);
                case State.CallableName: return OnCallableName(t, context);
                case State.CallableNameDot: return OnCallableNameDot(t, context);
                case State.ParamsExpected: return OnParamsExpected(t, context);
                case State.AfterParams: return OnAfterParams(t, context);
                case State.ReturnType: return OnReturnType(t, context);
                case State.AfterReturnType: return OnAfterReturnType(t, context);
                case State.TypeName: return OnTypeName(t, context);
                case State.AfterTypeName: return OnAfterTypeName(t, context);
                case State.BaseExpected: return OnBaseExpected(t, context);
                case State.InterfaceExpected: return OnInterfaceExpected(t, context);
                case State.AfterBase: return OnAfterBase(t, context);
                case State.LikeExpected: return OnLikeExpected(t, context);
                case State.AfterLike: return OnAfterLike(t, context);
                case State.BodyContent: return OnBodyContent(t, context);
                case State.EnumCaseListOpen: return OnEnumCaseListOpen(t, context);
                case State.EnumCaseStart: return OnEnumCaseStart(t, context);
                case State.EnumAfterName: return OnEnumAfterName(t, context);
                case State.EnumAfterArgs: return OnEnumAfterArgs(t, context);
                case State.EnumDiscriminant: return OnEnumDiscriminant(t, context);
                case State.EnumAfterCase: return OnEnumAfterCase(t, context);
                case State.Finish: return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                default:
                    throw context.RaiseError($"Invalid DeclarationParserLayer state: {state}");
            }
        }

        // 修饰符收集 + 关键字分派。这里是"全局/成员/嵌套同构"的关键：
        // 三种位置进入的都是本状态，后续路径完全一致。
        private ParserLayerResult OnModifiers(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            // 注解 / wrapper 应用（SYNTAX §14.5）：@Name[(args)]，可叠加多个
            if (t is NotationToken at && at.Content == "@")
            {
                // 前一个注解到此结束（@ 不属于它）：封口
                SealLastAnnotation(context);
                var ann = new AnnotationASTNode(null);  // 暂无父节点：声明创建时一次性 AttachTo
                // 注解 span 起点 = @ 处；名/实参完成后封口（M28）
                var loc = context.GetLocation();
                ann.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
                lastAnnotationSealed = false;
                pendingAnnotations.Add(ann);
                state = State.AnnotationName;
                // 注解名（可为 a.b 路径）复用 PathParserLayer
                return new ParserLayerResult.PushLayer(
                    new PathParserLayer(ann.Name, lineBreakSensitive: true), TokenDisposition.Consume);
            }

            if (t is WordToken w)
            {
                if (Keywords.IsDescriptor(w.Content))
                {
                    // 修饰符一律照收（M40 起）：重复、互斥与种类组合校验
                    // 全部归 P2 DeclarationResolver（可恢复诊断，见 CreateTypeNode 注释）
                    modifiers.Add(w.Content);
                    return ParserLayerResult.Continue.Instance;
                }

                // enum 后必须紧跟 struct（SYNTAX §12：enum struct 是唯一形态；
                // M31 修复：enum class/func/var 此前被静默吞掉 enum）
                if (enumSeen && w.Content != Keywords.STRUCT)
                {
                    throw context.RaiseError($"Expected 'struct' after 'enum', got: {w.Content}");
                }

                // 字段 / 全局变量：直接复用 VariableDeclarationParserLayer
                if (w.Content == Keywords.VAR || w.Content == Keywords.CONST)
                {
                    var v = new VariableDeclarationASTNode(parent);
                    v.Modifiers.AddRange(modifiers);
                    AttachAnnotations(v, context);
                    target.Add(v);
                    currentDeclaration = v;
                    state = State.Finish;
                    // ext 允许限定名（pub ext var String.isEmpty: bool，§4.4）
                    return new ParserLayerResult.PushLayer(
                        new VariableDeclarationParserLayer(
                            v, allowExtension: modifiers.Contains(Keywords.EXT)), TokenDisposition.Replay);
                }

                if (w.Content == Keywords.FUNC) return StartCallable(CallableKind.Func, State.CallableName, context);
                if (w.Content == Keywords.OPERATOR) return StartCallable(CallableKind.Operator, State.CallableName, context);
                if (w.Content == Keywords.INIT)
                {
                    var r = StartCallable(CallableKind.Init, State.ParamsExpected, context);
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

        // 注解挂接（大扫除 Validator 重写）：注解先于声明本体解析（构造时 parent 为 null），
        // 声明节点创建后对每个暂存注解一次性 AttachTo，再挂到其 Annotations 列表。
        // 同时给最后一个注解的 span 封口：声明关键字的前一 token 即注解末尾（M28）
        private void AttachAnnotations(ASTNode node, ParserLayerContext context)
        {
            SealLastAnnotation(context);
            foreach (var ann in pendingAnnotations)
            {
                ann.AttachTo(node);
            }
            // node 必为实现 IWrapperAttachable 的声明节点（var/callable/类型声明）
            ((IWrapperAttachable)node).Annotations.AddRange(pendingAnnotations);
        }

        // 最后一个注解的 span 封口（M28）：当前 token 已不属于注解，
        // End 取最近被消费的 token（注解名末尾或实参列表的 )）
        private void SealLastAnnotation(ParserLayerContext context)
        {
            if (lastAnnotationSealed || pendingAnnotations.Count == 0) return;
            var last = pendingAnnotations[pendingAnnotations.Count - 1];
            if (last.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                last.Span = s;
            }
            lastAnnotationSealed = true;
        }

        private ParserLayerResult StartCallable(CallableKind kind, State next, ParserLayerContext context)
        {
            callable = new CallableDeclarationASTNode(parent) { Kind = kind };
            callable.Modifiers.AddRange(modifiers);
            AttachAnnotations(callable, context);
            target.Add(callable);
            currentDeclaration = callable;
            extSeen = modifiers.Contains(Keywords.EXT);
            state = next;
            return ParserLayerResult.Continue.Instance;
        }

        // ===== 注解（@Name[(args)]，SYNTAX §14.5）=====

        // 注解名已解析：( 转实参列表（复用 ArgumentListParserLayer，开括号由本层消费——
        // 与调用点既有约定一致）；否则注解结束，token 交给修饰符收集重新处理
        private ParserLayerResult OnAnnotationName(Token t, ParserLayerContext context)
        {
            var ann = pendingAnnotations[pendingAnnotations.Count - 1];
            // 注解名不得为空（M31：@(1) 或 @ 后换行此前被静默接受）
            if (ann.Name.symbol.elements.Count == 0)
            {
                throw context.RaiseError($"Expected annotation name after '@', got: {t}");
            }

            if (t is NotationToken n && n.Content == "(")
            {
                ann.HasArguments = true;
                state = State.Modifiers;
                return new ParserLayerResult.PushLayer(
                    new ArgumentListParserLayer(
                        ann.Arguments, ArgumentListParserLayer.BracketKind.Round, ann), TokenDisposition.Consume);
            }

            state = State.Modifiers;
            return OnModifiers(t, context);
        }

        // ===== Callable：func / operator / init 共用，全局与成员共用 =====

        private ParserLayerResult OnCallableName(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            // 声明名必须是合法标识符（M31：数字词/保留字此前被静默接受）
            if (t is WordToken w && Keywords.IsIdentifier(w.Content))
            {
                callable!.Name = w.Content;
                state = State.ParamsExpected;
                return ParserLayerResult.Continue.Instance;
            }
            // wrapper proxy 成员（SYNTAX §14.2）：operator .proxy.<category?>.<name|*>，
            // 名以 . 开头，仅 operator 且仅 wrapper 体内合法
            if (t is NotationToken dot && dot.Content == "."
                && callable!.Kind == CallableKind.Operator
                && parent is WrapperDeclarationASTNode)
            {
                // 首段拼接时统一走 Name += "." + 段名，此处保持 Name 为空
                proxyNameSeen = true;
                state = State.CallableNameDot;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Expected declaration name, got: {t}");
        }

        // ext/proxy 限定名的段间点已读：拼接下一段
        // （ext：Type.member，可多段路径，§4.4；proxy：.proxy.<category?>.<name|*>，§14.2）
        private ParserLayerResult OnCallableNameDot(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            // wildcard 段：.proxy.* / .proxy.get.* / .proxy.set.* / .proxy.opr.*，必须收尾
            if (t is NotationToken star && star.Content == "*" && proxyNameSeen)
            {
                callable!.Name += ".*";
                wildcardSeen = true;
                state = State.ParamsExpected;
                return ParserLayerResult.Continue.Instance;
            }
            // 段名必须是合法标识符（非数字词、非保留字，与 OnCallableName 首段一致）
            if (t is WordToken w && Keywords.IsIdentifier(w.Content))
            {
                // proxy 名首段固定为 proxy（§14 规定的代理入口前缀）
                if (proxyNameSeen && callable!.Name.Length == 0 && w.Content != Keywords.PROXY)
                {
                    throw context.RaiseError(
                        $"Wrapper proxy member name must start with '.proxy.', got: .{w.Content}");
                }
                callable!.Name += "." + w.Content;
                state = State.ParamsExpected;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Expected member name after '.' in extension declaration, got: {t}");
        }

        private ParserLayerResult OnParamsExpected(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            if (t is NotationToken n)
            {
                if (n.Content == "(")
                {
                    state = State.AfterParams;
                    // 复用 ParameterListParserLayer（它自己吃掉 '(' 到 ')'）；
                    // 仅 init 允许 _ -> field 参数映射（§9.3）
                    return new ParserLayerResult.PushLayer(
                        new ParameterListParserLayer(
                            callable!.Parameters, allowMapping: callable.Kind == CallableKind.Init), TokenDisposition.Replay);
                }
                if (n.Content == "\\")
                {
                    callable!.GenericParameters = new GenericParameterListASTNode(callable);
                    // 复用 GenericParametersParserLayer（它自己吃掉 \< 到 >）；
                    // 状态保持 ParamsExpected：泛型列表弹出后仍等待 (
                    return new ParserLayerResult.PushLayer(
                        new GenericParametersParserLayer(callable.GenericParameters), TokenDisposition.Replay);
                }
                // 限定名的段间点：ext（String.reversed，§4.4）或 wrapper proxy
                // （.proxy.get.name，§14.2）；wildcard .* 后不允许再有点（* 必须收尾）
                if (n.Content == "." && (extSeen || proxyNameSeen) && !wildcardSeen)
                {
                    state = State.CallableNameDot;
                    return ParserLayerResult.Continue.Instance;
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
            return new ParserLayerResult.PopLayer(t is not LineBreakToken ? TokenDisposition.Replay : TokenDisposition.Consume);
        }

        private ParserLayerResult OnReturnType(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;
            callable!.ReturnType = new TypeReferenceASTNode(callable);
            state = State.AfterReturnType;
            // 复用 TypeReferenceParserLayer
            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(callable.ReturnType), TokenDisposition.Replay);
        }

        private ParserLayerResult OnAfterReturnType(Token t, ParserLayerContext context)
        {
            if (t is NotationToken n && n.Content == "{") return PushBody();
            return new ParserLayerResult.PopLayer(t is not LineBreakToken ? TokenDisposition.Replay : TokenDisposition.Consume);
        }

        private ParserLayerResult PushBody()
        {
            callable!.Body = new CodeBlockASTNode(callable);
            state = State.Finish;
            // 复用 CodeBlockParserLayer（它自己吃掉 '{' 到 '}'）
            return new ParserLayerResult.PushLayer(
                new CodeBlockParserLayer(callable.Body), TokenDisposition.Replay);
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

            // 修饰符与类型种类的组合校验不归前端（M40 起）：SYNTAX §3.1.1/§9.2/
            // §10/§14.9 的全部修饰符合法性由 P2 DeclarationResolver 以可恢复
            // 诊断检查（ARCHITECTURE §2 分工表），Parser 只收下修饰符原文。
            // （此前 M31 的三条即死拦截已删除：它们把合法的 shared wrapper
            // 一并误杀，且单发即死违背中端可累积诊断模型。）

            GetModifiers(node).AddRange(modifiers);
            AttachAnnotations(node, context);
            target.Add(node);
            currentDeclaration = node;
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

        // 各类型节点的成员容器访问集中在此（与 GetModifiers 同款集中 switch）；
        // 只在类型体递归时调用，default 分支永远不该走到
        private static List<ASTNode> GetMembers(ASTNode n) => n switch
        {
            ClassDeclarationASTNode c => c.Members,
            InterfaceDeclarationASTNode i => i.Members,
            StructDeclarationASTNode s => s.Members,
            EnumStructDeclarationASTNode e => e.Members,
            WrapperDeclarationASTNode w => w.Members,
            _ => throw new CompilerInternalException($"Unexpected type node: {n.GetType().Name}")
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
            // 类型名必须是合法标识符（M31：数字词/保留字此前被静默接受）
            if (t is WordToken w && Keywords.IsIdentifier(w.Content))
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
                    return new ParserLayerResult.PushLayer(new GenericParametersParserLayer(gp), TokenDisposition.Replay);
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
            if (t is WordToken lw && lw.Content == Keywords.LIKE)
            {
                state = State.LikeExpected;
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
            return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(tr), TokenDisposition.Replay);
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
            return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(tr), TokenDisposition.Replay);
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
            if (t is WordToken lw && lw.Content == Keywords.LIKE)
            {
                state = State.LikeExpected;
                return ParserLayerResult.Continue.Instance;
            }
            throw context.RaiseError($"Unexpected token in inheritance clause: {t}");
        }

        // like 已读：等待委托目标字段名（§9.6，仅 class 可委托）
        private ParserLayerResult OnLikeExpected(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is WordToken w)
            {
                switch (typeNode)
                {
                    case ClassDeclarationASTNode c:
                        c.LikeTarget = w.Content;
                        state = State.AfterLike;
                        return ParserLayerResult.Continue.Instance;
                    default:
                        throw context.RaiseError("Only class declarations can use 'like' delegation (SYNTAX §9.6)");
                }
            }

            throw context.RaiseError($"Expected field name after 'like', got: {t}");
        }

        // like 目标已读：只允许 { 进入类型体
        private ParserLayerResult OnAfterLike(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n && n.Content == "{")
            {
                state = State.BodyContent;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected '{{' after 'like' delegation, got: {t}");
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

            // EOF：类型体必须由 } 闭合，收到 EOF 是不完整结构
            if (t is EndOfFileToken)
                throw context.RaiseError("Unexpected end of file");

            if (t is NotationToken n && n.Content == "}")
            {
                // enum struct 体后可能紧跟 [case 列表]（SYNTAX §12）
                if (typeNode is EnumStructDeclarationASTNode)
                {
                    state = State.EnumCaseListOpen;
                    return ParserLayerResult.Continue.Instance;
                }
                // wrapper 体结束：校验同类 wildcard proxy 唯一（SYNTAX §14.6）
                if (typeNode is WrapperDeclarationASTNode) ValidateWildcardUniqueness(context);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            return new ParserLayerResult.PushLayer(
                new DeclarationParserLayer(typeNode!, GetMembers(typeNode!)), TokenDisposition.Replay);
        }

        // §14.6：同一 wrapper 中四类 wildcard proxy（.proxy.* / .proxy.get.* /
        // .proxy.set.* / .proxy.opr.*）各自最多一个；specific proxy 不受限
        private void ValidateWildcardUniqueness(ParserLayerContext context)
        {
            var wildcards = ((WrapperDeclarationASTNode)typeNode!).Members
                .OfType<CallableDeclarationASTNode>()
                .Where(c => c.Kind == CallableKind.Operator && c.Name.EndsWith(".*"))
                .Select(c => c.Name)
                .ToList();
            if (wildcards.Distinct().Count() != wildcards.Count)
            {
                throw context.RaiseError(
                    "Duplicate wildcard proxy of the same category in one wrapper (SYNTAX §14.6)");
            }
        }

        // ===== enum struct 的 [case 列表]（SYNTAX §12）=====

        // enum 体已闭合：[ 转 case 列表；否则无 case 收尾。
        // [ 必须与 } 同行（与 callable 体 { 的既有约定一致）：换行即声明结束
        private ParserLayerResult OnEnumCaseListOpen(Token t, ParserLayerContext context)
        {
            if (t is NotationToken n && n.Content == "[")
            {
                state = State.EnumCaseStart;
                return ParserLayerResult.Continue.Instance;
            }

            return new ParserLayerResult.PopLayer(t is not LineBreakToken ? TokenDisposition.Replay : TokenDisposition.Consume);
        }

        // 等待 case 名或 ]（[] 内换行忽略）
        private ParserLayerResult OnEnumCaseStart(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n && n.Content == "]")
                return FinishEnumCases(context);

            if (t is WordToken w && Keywords.IsIdentifier(w.Content))
            {
                currentCase = new EnumCaseASTNode(typeNode) { CaseName = w.Content };
                // case span 起点 = case 名（M28）；`,`/`]` 处封口
                var loc = context.GetLocation();
                currentCase.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
                ((EnumStructDeclarationASTNode)typeNode!).Cases.Add(currentCase);
                state = State.EnumAfterName;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected enum case name or ']', got: {t}");
        }

        // case 名已读：( 实参（复用 ArgumentListParserLayer）/ -> 判别值 / , / ]
        private ParserLayerResult OnEnumAfterName(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n)
            {
                if (n.Content == "(")
                {
                    state = State.EnumAfterArgs;
                    // 复用 ArgumentListParserLayer：开括号由本层消费，
                    // 它从实参开始、自己吃掉闭合 )（与调用点的既有约定一致）
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            currentCase!.Arguments, ArgumentListParserLayer.BracketKind.Round, currentCase), TokenDisposition.Consume);
                }
                if (n.Content == Notations.ARROW)
                {
                    state = State.EnumDiscriminant;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == ",")
                {
                    SealCurrentCase(context);
                    state = State.EnumCaseStart;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == "]") return FinishEnumCases(context);
            }

            throw context.RaiseError($"Expected '(', '->', ',' or ']' after enum case name, got: {t}");
        }

        // 实参列表已读：-> 判别值 / , / ]
        private ParserLayerResult OnEnumAfterArgs(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n)
            {
                if (n.Content == Notations.ARROW)
                {
                    state = State.EnumDiscriminant;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == ",")
                {
                    SealCurrentCase(context);
                    state = State.EnumCaseStart;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == "]") return FinishEnumCases(context);
            }

            throw context.RaiseError($"Expected '->', ',' or ']' after enum case arguments, got: {t}");
        }

        // -> 已读：判别值只接受非负整数字面量（§12.4 编译期整数常量；
        // M31：支持 0x 等进制与更大范围，统一走 NumericLiteral）
        private ParserLayerResult OnEnumDiscriminant(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is WordToken w &&
                NumericLiteral.TryParseInt(w.Content, out var value, out _, out _, out _) &&
                value >= 0)
            {
                currentCase!.DiscriminantValue = value;
                state = State.EnumAfterCase;
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError($"Expected non-negative integer as enum discriminant, got: {t}");
        }

        // 判别值已读：, 下一个 / ] 收尾
        private ParserLayerResult OnEnumAfterCase(Token t, ParserLayerContext context)
        {
            if (t is LineBreakToken) return ParserLayerResult.Continue.Instance;

            if (t is NotationToken n)
            {
                if (n.Content == ",")
                {
                    SealCurrentCase(context);
                    state = State.EnumCaseStart;
                    return ParserLayerResult.Continue.Instance;
                }
                if (n.Content == "]") return FinishEnumCases(context);
            }

            throw context.RaiseError($"Expected ',' or ']' after enum discriminant, got: {t}");
        }

        // 当前 enum case 的 span 封口（M28）：`,`/`]` 不属于 case，End 取最近被消费的 token
        private void SealCurrentCase(ParserLayerContext context)
        {
            if (currentCase?.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                currentCase.Span = s;
            }
        }

        // case 列表收尾（] 已读）：case 名唯一；判别值唯一且「全显式或全分配」（§12/§12.4）
        private ParserLayerResult FinishEnumCases(ParserLayerContext context)
        {
            SealCurrentCase(context);
            var cases = ((EnumStructDeclarationASTNode)typeNode!).Cases;

            if (cases.Select(c => c.CaseName).Distinct().Count() != cases.Count)
                throw context.RaiseError("Duplicate enum case name (SYNTAX §12)");

            var explicitCases = cases.Where(c => c.DiscriminantValue != null).ToList();
            if (explicitCases.Count > 0 && explicitCases.Count != cases.Count)
                throw context.RaiseError(
                    "Enum cases must either all have explicit discriminants or all be compiler-assigned (SYNTAX §12.4)");
            if (explicitCases.Select(c => c.DiscriminantValue).Distinct().Count() != explicitCases.Count)
                throw context.RaiseError("Duplicate enum discriminant value (SYNTAX §12.4)");

            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
