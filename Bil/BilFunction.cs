using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // BIL 函数、参数、局部变量与 block（BIL_STANDARD §9）。

    // 函数定义（§9.1）：fn(METHOD_SYMBOL) { .args/.vars/.block... }
    // 必须对应一个 LocalSymbols 方法声明（生成方责任，verifier 复核）
    public sealed class BilFunction
    {
        public string Symbol { get; }
        // .args（§9.2）：重申可引用的语义参数，保序——.return 在前，
        // 其后 .this / 泛型隐藏参数 / 普通参数 / vargs / kwargs（§7.2 顺序）
        public List<BilArgDeclaration> Args { get; } = new List<BilArgDeclaration>();
        // .vars（§9.3）：局部变量（函数内唯一，与参数共用 $name 引用）
        public List<BilVarDeclaration> Vars { get; } = new List<BilVarDeclaration>();
        // block 列表（§9.4）：必须恰有一个 entrypoint block（生成方责任）
        public List<BilBlock> Blocks { get; } = new List<BilBlock>();

        public BilFunction(string symbol)
        {
            Symbol = symbol;
        }
    }

    // .args 条目：名 = 类型（.return = .i32 / args = .array<.string>）
    public readonly struct BilArgDeclaration
    {
        public string Name { get; }
        public string TypeRef { get; }

        public BilArgDeclaration(string name, string typeRef)
        {
            Name = name;
            TypeRef = typeRef;
        }
    }

    // .vars 条目：类型 名（.i32 counter / com.example::User user）
    public readonly struct BilVarDeclaration
    {
        public string TypeRef { get; }
        public string Name { get; }

        public BilVarDeclaration(string typeRef, string name)
        {
            TypeRef = typeRef;
            Name = name;
        }
    }

    // block（§9.4–§9.6）：结构化代码 region，非 LLVM basic block；
    // 共享函数的参数与局部变量，不能接受独立参数
    public sealed class BilBlock
    {
        public string Id { get; }
        // block 修饰符（§9.6 标准集：entrypoint / volatile）
        public IReadOnlyList<string> Modifiers { get; }
        public List<BilInstruction> Instructions { get; } = new List<BilInstruction>();

        public BilBlock(string id, params string[] modifiers)
        {
            Id = id;
            Modifiers = modifiers;
        }
    }
}
