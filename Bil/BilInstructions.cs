using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 指令与操作数模型（BIL_STANDARD §10–§16；协程指令 §17 暂缓——
    // 见 SEMANTIC_ARCHITECTURE §7 待修订清单与 ROADMAP S13）。
    //
    // 模型按「opcode + 操作数列表」的通用形态承载全部标准指令：
    // 结构化理解（类型检查、capability 校验）是 BilVerifier（S12）的职责，
    // 模型层只保证自足与无损打印。

    public sealed class BilInstruction
    {
        // 标准 opcode（§5.6：不带前导点；标准生成器不输出 legacy spelling）
        public string Opcode { get; }
        public IReadOnlyList<BilOperand> Operands { get; }

        // Origin 调试链占位（ARCHITECTURE §6.3：Bil指令.Origin → LoweredNode，
        // S6 接通后收窄类型；从 BIL 文本反序列化得到的模型 Origin 恒为 null）
        public object? Origin { get; set; }

        public BilInstruction(string opcode, params BilOperand[] operands)
        {
            Opcode = opcode;
            Operands = operands;
        }
    }

    // 指令操作数（§10.1：普通指令操作数只能是变量与符号表达式；
    // 用户字面量不得直接出现）
    public abstract class BilOperand
    {
        internal abstract string Render();
    }

    // $variable（§5.1 本地标识符）
    public sealed class BilVariableOperand : BilOperand
    {
        public string Name { get; }
        public BilVariableOperand(string name) { Name = name; }
        internal override string Render() => "$" + Name;
    }

    // fn(METHOD_SYMBOL)（§8.1：内嵌完整 canonical symbol）
    public sealed class BilFnOperand : BilOperand
    {
        public string Symbol { get; }
        public BilFnOperand(string symbol) { Symbol = symbol; }
        internal override string Render() => $"fn({Symbol})";
    }

    // field(FIELD_SYMBOL)
    public sealed class BilFieldOperand : BilOperand
    {
        public string Symbol { get; }
        public BilFieldOperand(string symbol) { Symbol = symbol; }
        internal override string Render() => $"field({Symbol})";
    }

    // type(TYPE_SYMBOL_OR_REF)
    public sealed class BilTypeOperand : BilOperand
    {
        public string TypeRef { get; }
        public BilTypeOperand(string typeRef) { TypeRef = typeRef; }
        internal override string Render() => $"type({TypeRef})";
    }

    // case(ENUM_TYPE_SYMBOL.CaseName)（§8.1/§8.5：必须含完整 enum 类型符号）
    public sealed class BilCaseOperand : BilOperand
    {
        public string QualifiedName { get; }
        public BilCaseOperand(string qualifiedName) { QualifiedName = qualifiedName; }
        internal override string Render() => $"case({QualifiedName})";
    }

    // blk(BLOCK_ID)（§9.5：必须引用当前函数内存在的 block）
    public sealed class BilBlockOperand : BilOperand
    {
        public string BlockId { get; }
        public BilBlockOperand(string blockId) { BlockId = blockId; }
        internal override string Render() => $"blk({BlockId})";
    }

    // res(RESOURCE_ID)
    public sealed class BilResourceOperand : BilOperand
    {
        public string ResourceId { get; }
        public BilResourceOperand(string resourceId) { ResourceId = resourceId; }
        internal override string Render() => $"res({ResourceId})";
    }

    // none（结构位置：if 的 false 分支、loop 的 enumerator、try 的 finally、
    // switch 的 default；单例）
    public sealed class BilNoneOperand : BilOperand
    {
        public static readonly BilNoneOperand Instance = new BilNoneOperand();
        private BilNoneOperand() { }
        internal override string Render() => "none";
    }

    // 操作数列表：[ARG_0, ARG_1, ...]（invoke 实参 §15、new 参数 §14、
    // switch block 表 §16.6）
    public sealed class BilOperandList : BilOperand
    {
        public IReadOnlyList<BilOperand> Items { get; }
        public BilOperandList(params BilOperand[] items) { Items = items; }

        internal override string Render()
        {
            var parts = new List<string>();
            foreach (var item in Items)
            {
                parts.Add(item.Render());
            }
            return $"[{string.Join(", ", parts)}]";
        }
    }

    // 操作数便捷构造（BilEmitter 与测试共用）
    public static class BilOp
    {
        public static BilVariableOperand Var(string name) => new BilVariableOperand(name);
        public static BilFnOperand Fn(string symbol) => new BilFnOperand(symbol);
        public static BilFieldOperand Field(string symbol) => new BilFieldOperand(symbol);
        public static BilTypeOperand Type(string typeRef) => new BilTypeOperand(typeRef);
        public static BilCaseOperand Case(string qualifiedName) => new BilCaseOperand(qualifiedName);
        public static BilBlockOperand Blk(string blockId) => new BilBlockOperand(blockId);
        public static BilResourceOperand Res(string resourceId) => new BilResourceOperand(resourceId);
        public static BilNoneOperand None => BilNoneOperand.Instance;
        public static BilOperandList List(params BilOperand[] items) => new BilOperandList(items);
    }
}
