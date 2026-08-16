using System;

namespace RigiCompiler
{
    // 字面量节点的公共基类：LiteralParserLayer 的施工目标类型
    // （LiteralExpressionASTNode.AttachLiteral 只接受 LiteralASTNode）
    public abstract class LiteralASTNode : ASTNode
    {
        protected LiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 整数类型枚举
    public enum IntType
    {
        I32,    // 默认
        I64,    // L 后缀
        I16,    // S 后缀
        I8,     // B 后缀
        U32,    // U 后缀
        U64,    // UL 后缀
        U16,    // US 后缀
        U8      // UB 后缀
    }

    // 整数字面量进制（M31：替代原 IsHex 布尔；SYNTAX §3.3 的 0x/0b/0o 前缀）
    public enum LiteralIntBase
    {
        Decimal,
        Hex,
        Binary,
        Octal
    }

    // 整数字面量 AST 节点
    public class IntLiteralASTNode : LiteralASTNode
    {
        // 值一律以 decimal 装载（128 位十进制，可精确覆盖 u64 全范围）
        public decimal Value;
        public IntType IntType;
        public LiteralIntBase Base = LiteralIntBase.Decimal;  // 进制（0x/0b/0o 前缀）

        public IntLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 浮点数字面量 AST 节点
    public class FloatLiteralASTNode : LiteralASTNode
    {
        public double Value;
        public bool IsFloat;  // true = float, false = double

        public FloatLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 字符串插值段（S7f，[AstCarrier]；SYNTAX §3.8）：两字段互斥——
    // Text 非 null = 字面量段（LiteralExpression 包装 StringLiteral 的段级
    // 子结构，Value 为解码后文本、span 为段范围——与普通字符串字面量同构，
    // P3/P4 全程复用字面量机器）；Expression 非 null = 插值表达式段
    // （子解析产物，经 ExpressionRootASTNode 稳定挂载点一次性 Attach，
    // 段内表达式的 span 精确映射回源文件）
    [AstCarrier]
    public class StringInterpolationPart
    {
        public LiteralExpressionASTNode? Text;
        public ExpressionRootASTNode? Expression;
    }

    // 字符串字面量 AST 节点
    public class StringLiteralASTNode : LiteralASTNode
    {
        public string Value = "";
        public bool HasInterpolation;  // 是否包含字符串插值

        // 插值段序列（S7f；null = 无插值或未拆分，Value/HasInterpolation
        // 维持原义）：段按源码顺序；有插值时 Parser 拆分 RawContent 填充
        [ChildAstNode] public List<StringInterpolationPart>? InterpolationParts;

        public StringLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 字符字面量 AST 节点（SYNTAX §3.3：单引号内恰好一个字符或一个转义序列，类型 char）
    public class CharLiteralASTNode : LiteralASTNode
    {
        public char Value;

        public CharLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // 布尔字面量 AST 节点
    public class BoolLiteralASTNode : LiteralASTNode
    {
        public bool Value;

        public BoolLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }

    // null 字面量 AST 节点
    public class NullLiteralASTNode : LiteralASTNode
    {
        public NullLiteralASTNode(ASTNode? parent) : base(parent)
        {
        }
    }
}
