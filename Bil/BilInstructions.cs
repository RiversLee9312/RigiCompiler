using System.Collections.Generic;
using System.Text;

namespace RigiCompiler.Bil
{
    // BIL 指令与操作数模型（BIL_STANDARD §10–§16 + §18 提示指令；协程指令
    // §17 暂缓——见 SEMANTIC_ARCHITECTURE §7 待修订清单与 ROADMAP S13）。
    //
    // M57 起指令为强类型子类族（BilComputeInstructions/BilDataInstructions/
    // BilControlFlowInstructions/BilHintInstruction，按规范章节分文件）：每种指令的 opcode
    // 拼写、操作数个数/类型/顺序由子类构造签名与属性固定——生成方不再
    // 接触 opcode 字面量与位置式操作数列表。结构化理解（类型检查、
    // capability 校验）是 BilVerifier 的职责（M58 提前自 S12 落地：
    // BilVerifier*.cs 五文件，§21 九类检查的静态可判子集），模型层保证
    // 自足与无损打印。

    // 指令基类：Origin 调试链 + 自渲染协议（WriteTo 统一单行/多行排版，
    // 与 BilOperand.Render 同模式——BIL 文本结构由模型持有，BilWriter
    // 只提供缩进与段落框架）
    public abstract class BilInstruction
    {
        // Origin 调试链占位（ARCHITECTURE §6.3：Bil指令.Origin → LoweredNode，
        // S6 接通后收窄类型；从 BIL 文本反序列化得到的模型 Origin 恒为 null）
        public object? Origin { get; set; }

        // 标准 opcode（§5.6：不带前导点；标准生成器不输出 legacy spelling）。
        // 拼写唯一定义在各子类（经 BilSpellings 映射表）
        internal abstract string Opcode { get; }

        // 规范操作数序列（保序）；由子类的强类型属性合成
        internal abstract IReadOnlyList<BilOperand> Operands { get; }

        // §16.6 switch / §16.7 try 规范排版：首行只放前 N 个操作数，
        // 其余各占一行（默认 int.MaxValue = 全部同行，即单行形态）
        internal virtual int FirstLineOperandCount => int.MaxValue;

        // 指令行渲染：indent 为指令行基础缩进（函数 block 内两级）；
        // 多行形态的续行再进一级
        internal void WriteTo(StringBuilder sb, string indent)
        {
            var operands = Operands;
            sb.Append(indent).Append(Opcode);
            var firstLine = operands.Count < FirstLineOperandCount
                ? operands.Count : FirstLineOperandCount;
            for (var i = 0; i < firstLine; i++)
            {
                sb.Append(' ').Append(operands[i].Render());
            }
            sb.Append('\n');
            for (var i = firstLine; i < operands.Count; i++)
            {
                sb.Append(indent).Append("    ").Append(operands[i].Render()).Append('\n');
            }
        }
    }

    // ===== 指令操作数（§10.1：普通指令操作数只能是变量与符号表达式；
    // 用户字面量不得直接出现）=====

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

    // wrapper(WRAPPER_TYPE_REF)（§13.3 set.wrapper.field 链元素，M88）
    public sealed class BilWrapperOperand : BilOperand
    {
        public string TypeRef { get; }
        public BilWrapperOperand(string typeRef) { TypeRef = typeRef; }
        internal override string Render() => $"wrapper({TypeRef})";
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

    // blk(BLOCK_ID)（§9.5：必须引用当前函数内存在的 block——M57 起持有
    // BilBlock 对象引用，悬空引用在构造期即不可能）
    public sealed class BilBlockOperand : BilOperand
    {
        public BilBlock Block { get; }
        public BilBlockOperand(BilBlock block) { Block = block; }
        internal override string Render() => $"blk({Block.Id})";
    }

    // res(RESOURCE_ID)（M57 起持有 BilResource 对象引用）
    public sealed class BilResourceOperand : BilOperand
    {
        public BilResource Resource { get; }
        public BilResourceOperand(BilResource resource) { Resource = resource; }
        internal override string Render() => $"res({Resource.Name})";
    }

    // none（结构位置：if 的 false 分支、loop 的 enumerator、try 的 finally、
    // switch 的 default；单例——指令子类的可空 block 属性为 null 时由
    // Operands 合成代入，生成方不直接构造）
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
        public BilOperandList(IReadOnlyList<BilOperand> items) { Items = items; }

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
        public static BilWrapperOperand Wrapper(string typeRef) => new BilWrapperOperand(typeRef);
        public static BilTypeOperand Type(string typeRef) => new BilTypeOperand(typeRef);
        public static BilCaseOperand Case(string qualifiedName) => new BilCaseOperand(qualifiedName);
    }
}
