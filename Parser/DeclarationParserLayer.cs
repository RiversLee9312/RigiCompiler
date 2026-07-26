using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{
    public class DeclarationASTNode : ASTNode
    {
        public enum DeclarationType
        {
            Unknown,
            Func,
            Class,
            Interface,
            Struct,
            Enum,
            Wrapper
        }
        public DeclarationType type;
        public ASTNode? Declaration;  // 实际的声明节点（ClassDeclarationASTNode 等）

        public DeclarationASTNode(ASTNode? parent) : base(parent)
        {
            type = DeclarationType.Unknown;
            Declaration = null;
        }

        public override ASTNodeType NodeType { get; } = ASTNodeType.Declaration;
    }

    public class DeclarationParserLayer : IParserLayer, IResultConsumer
    {
        private DeclarationASTNode self;
        private List<string> modifiers = new List<string>();
        private string? typeKeyword = null;  // class/interface/struct/enum/wrapper
        private string? typeName = null;
        private ASTNode? actualDeclarationNode = null;

        public DeclarationParserLayer(DeclarationASTNode self)
        {
            this.self = self;
        }

        private enum State
        {
            Modifiers,         // 解析修饰符（pub, open, abstract, etc.）
            TypeKeyword,       // 解析类型关键字（class/interface/struct/enum/wrapper/func）
            TypeName,          // 解析类型名
            GenericParams,     // 可选：泛型参数 <T>
            Inheritance,       // 可选：继承和接口（: BaseClass, implements Interface）
            BodyStart,         // 等待 {
            BodyContent,       // 等待 } 或成员（暂时只支持空 body）
            Completed          // 解析完成
        }

        private State state = State.Modifiers;
        private Action<ASTNode?>? pendingResultHandler;

        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            var handler = pendingResultHandler;
            pendingResultHandler = null;
            handler?.Invoke(result);
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.Modifiers:
                    return HandleModifiers(currentToken, context);
                case State.TypeKeyword:
                    return HandleTypeKeyword(currentToken, context);
                case State.TypeName:
                    return HandleTypeName(currentToken, context);
                case State.GenericParams:
                    return HandleGenericParams(currentToken, context);
                case State.Inheritance:
                    return HandleInheritance(currentToken, context);
                case State.BodyStart:
                    return HandleBodyStart(currentToken, context);
                case State.BodyContent:
                    return HandleBodyContent(currentToken, context);
                case State.Completed:
                    return new ParserLayerResult.PopLayer(true);
                default:
                    context.RaiseError($"Invalid DeclarationParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        private ParserLayerResult HandleModifiers(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                // 修饰符：pub, priv, open, abstract, etc.
                if (IsModifier(wt.Content))
                {
                    modifiers.Add(wt.Content);
                    return ParserLayerResult.Continue.Instance;
                }

                // 类型关键字：class, interface, struct, enum, wrapper, func
                if (IsTypeKeyword(wt.Content))
                {
                    typeKeyword = wt.Content;
                    state = State.TypeName;
                    return ParserLayerResult.Continue.Instance;
                }

                context.RaiseError($"Expected modifier or type keyword, got: {wt.Content}");
            }

            context.RaiseError($"Unexpected token in declaration: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleTypeKeyword(Token currentToken, ParserLayerContext context)
        {
            // 此状态已废弃，合并到 Modifiers
            state = State.TypeName;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleTypeName(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                typeName = wt.Content;
                CreateDeclarationNode();
                state = State.GenericParams;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected type name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleGenericParams(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 检查是否有泛型参数 <
            if (currentToken is NotationToken nt && nt.Content == "<")
            {
                // 委托给 GenericParametersParserLayer
                // 暂时跳过，先实现最简单的情况
                context.RaiseError("Generic parameters not yet supported in type declarations");
                return new ParserLayerResult.PopLayer(false);
            }

            // 没有泛型，检查继承
            state = State.Inheritance;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleInheritance(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 暂时跳过继承和接口解析，直接进入 body
            state = State.BodyStart;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleBodyStart(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                // 进入 body 内容解析（暂时只支持空 body）
                state = State.BodyContent;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '{{' to start type body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleBodyContent(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                // Body 结束
                state = State.Completed;
                return ParserLayerResult.Continue.Instance;
            }

            // 暂时不支持成员解析，只支持空 body
            context.RaiseError($"Type body members not yet supported. Expected '}}', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private void CreateDeclarationNode()
        {
            switch (typeKeyword)
            {
                case Keywords.CLASS:
                    var classNode = new ClassDeclarationASTNode(self);
                    classNode.Modifiers.AddRange(modifiers);
                    classNode.ClassName = typeName!;
                    self.type = DeclarationASTNode.DeclarationType.Class;
                    self.Declaration = classNode;
                    actualDeclarationNode = classNode;
                    break;

                case Keywords.INTERFACE:
                    var interfaceNode = new InterfaceDeclarationASTNode(self);
                    interfaceNode.Modifiers.AddRange(modifiers);
                    interfaceNode.InterfaceName = typeName!;
                    self.type = DeclarationASTNode.DeclarationType.Interface;
                    self.Declaration = interfaceNode;
                    actualDeclarationNode = interfaceNode;
                    break;

                case Keywords.STRUCT:
                    var structNode = new StructDeclarationASTNode(self);
                    structNode.Modifiers.AddRange(modifiers);
                    structNode.StructName = typeName!;
                    self.type = DeclarationASTNode.DeclarationType.Struct;
                    self.Declaration = structNode;
                    actualDeclarationNode = structNode;
                    break;

                case Keywords.WRAPPER:
                    var wrapperNode = new WrapperDeclarationASTNode(self);
                    wrapperNode.Modifiers.AddRange(modifiers);
                    wrapperNode.WrapperName = typeName!;
                    self.type = DeclarationASTNode.DeclarationType.Wrapper;
                    self.Declaration = wrapperNode;
                    actualDeclarationNode = wrapperNode;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown type keyword: {typeKeyword}");
            }
        }

        private bool IsModifier(string word)
        {
            return word == Keywords.PUB || word == Keywords.PRIV ||
                   word == Keywords.PROTECTED || word == Keywords.INTERNAL ||
                   word == Keywords.OPEN || word == Keywords.ABSTRACT ||
                   word == Keywords.SINGLETON || word == Keywords.SHARED ||
                   word == Keywords.RICH || word == Keywords.STATIC ||
                   word == Keywords.OVERRIDE || word == Keywords.ASYNC;
        }

        private bool IsTypeKeyword(string word)
        {
            return word == Keywords.CLASS || word == Keywords.INTERFACE ||
                   word == Keywords.STRUCT || word == Keywords.WRAPPER ||
                   word == Keywords.ENUM || word == Keywords.FUNC ||
                   word == Keywords.OPERATOR;
        }
    }
}
