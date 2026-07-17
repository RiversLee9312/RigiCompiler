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
            Type
        }
        public DeclarationType type;
        public DeclarationASTNode(ASTNode? parent) : base(parent)
        {
            type = DeclarationType.Unknown;

        }
        

        public override ASTNodeType NodeType { get; } = ASTNodeType.Declaration;
    }
    public class DeclarationParserLayer : IParserLayer
    {
        private DeclarationASTNode self;
        public DeclarationParserLayer(DeclarationASTNode self)
        {
            this.self = self;
        }
        private enum ParserStage
        {
            Wrappers,
            Descriptors,
            FuncName,
            FuncParams,
            FuncReturnType,
            FuncBody,
            TypeName,
            TypeBaseTypes,
            TypeBody
        }
        private ParserStage stage = ParserStage.Descriptors;
        private List<string> descriptors = new List<string>();

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (stage)
            {
                case ParserStage.Descriptors:
                    return ParseDescriptors(currentToken, context);
                case ParserStage.FuncName:
                    return ParseFuncName(currentToken, context);
                case ParserStage.TypeName:
                    return ParseTypeName(currentToken, context);
                default:
                    throw context.RaiseError($"Unimplemented parser stage: {stage}");
            }
        }

        private ParserLayerResult ParseDescriptors(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                if (Keywords.IsDescriptor(wt.Content))
                {
                    descriptors.Add(wt.Content);
                    return ParserLayerResult.Continue.Instance;
                }
                else if (wt.Content == Keywords.FUNC)
                {
                    self.type = DeclarationASTNode.DeclarationType.Func;
                    stage = ParserStage.FuncName;
                    return ParserLayerResult.Continue.Instance;
                }
                else if (Keywords.IsTypeKeyword(wt.Content))
                {
                    self.type = DeclarationASTNode.DeclarationType.Type;
                    stage = ParserStage.TypeName;
                    descriptors.Add(wt.Content);
                    return ParserLayerResult.Continue.Instance;
                }
                else
                {
                    throw context.RaiseError($"Unexpected word token in declaration: {wt.Content}");
                }
            }
            else if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }
            else
            {
                throw context.RaiseError($"Unexpected token in declaration descriptors: {currentToken}");
            }
        }

        private ParserLayerResult ParseFuncName(Token currentToken, ParserLayerContext context)
        {
            throw new NotImplementedException("Function parsing not yet implemented");
        }

        private ParserLayerResult ParseTypeName(Token currentToken, ParserLayerContext context)
        {
            throw new NotImplementedException("Type parsing not yet implemented");
        }
    }
}
