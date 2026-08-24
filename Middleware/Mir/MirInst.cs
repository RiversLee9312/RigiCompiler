using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler.Middleware
{
    // MIR 指令与终结符（MW1 子集）。与 BIL 指令族不同构：BIL 的结构化控制流
    // 在本层拍平为「顺序指令 + 块终结符」；类型驱动操作的绑定决策不在此层，
    // 由 Binding 层查询、Emit 消费。

    // 操作数：具名局部引用（$x；常量不经操作数流通——字面量只能由
    // MirLoadResource 从 Resources 段物化，BIL §10.1 同纪律）
    public abstract class MirOperand
    {
    }

    public sealed class MirLocalOperand : MirOperand
    {
        public string Name { get; }

        internal MirLocalOperand(string name)
        {
            Name = name;
        }

        public override string ToString() => "$" + Name;
    }

    public abstract class MirInst
    {
    }

    // load res(...)：资源物化进具名局部
    public sealed class MirLoadResource : MirInst
    {
        public BilResource Resource { get; }
        public string Target { get; }

        internal MirLoadResource(BilResource resource, string target)
        {
            Resource = resource;
            Target = target;
        }
    }

    // set.var / get.var：具名局部间的值拷贝
    public sealed class MirCopyLocal : MirInst
    {
        public MirOperand Source { get; }
        public string Target { get; }

        internal MirCopyLocal(MirOperand source, string target)
        {
            Source = source;
            Target = target;
        }
    }

    // §11 二元内建运算：操作数/结果的严格类型由 MirBuilder 从局部表解析
    // 填入（Binding 查询的输入；MIR 不自作实现决策）
    public sealed class MirBinaryIntrinsic : MirInst
    {
        public BilBinaryOp Op { get; }
        public MirOperand Left { get; }
        public MirOperand Right { get; }
        public MirType LeftType { get; }
        public MirType RightType { get; }
        public MirType ResultType { get; }
        public string Target { get; }

        internal MirBinaryIntrinsic(BilBinaryOp op, MirOperand left, MirOperand right,
            MirType leftType, MirType rightType, MirType resultType, string target)
        {
            Op = op;
            Left = left;
            Right = right;
            LeftType = leftType;
            RightType = rightType;
            ResultType = resultType;
            Target = target;
        }
    }

    // §11 一元内建运算
    public sealed class MirUnaryIntrinsic : MirInst
    {
        public BilUnaryOp Op { get; }
        public MirOperand Operand { get; }
        public MirType OperandType { get; }
        public MirType ResultType { get; }
        public string Target { get; }

        internal MirUnaryIntrinsic(BilUnaryOp op, MirOperand operand,
            MirType operandType, MirType resultType, string target)
        {
            Op = op;
            Operand = operand;
            OperandType = operandType;
            ResultType = resultType;
            Target = target;
        }
    }

    // invoke / invoke.noret：目标为驻留成员符号（含 native 声明；
    // 派发形态——native 面/直接调用——由 Binding.BindCall 回答）
    public sealed class MirCall : MirInst
    {
        public MwMemberSymbol Target { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        // null = invoke.noret（无结果槽）
        public string? Result { get; }

        internal MirCall(MwMemberSymbol target, IReadOnlyList<MirOperand> args, string? result)
        {
            Target = target;
            Args = args;
            Result = result;
        }
    }

    public abstract class MirTerminator
    {
    }

    // ret / ret $value
    public sealed class MirRet : MirTerminator
    {
        public MirOperand? Value { get; }

        internal MirRet(MirOperand? value)
        {
            Value = value;
        }
    }

    // 无条件跳转（BIL 结构化 region 的边界/落出边）
    public sealed class MirBranch : MirTerminator
    {
        public string Target { get; }

        internal MirBranch(string target)
        {
            Target = target;
        }
    }

    // 条件跳转（if 双分支、loop 的 judge 出口）
    public sealed class MirCondBranch : MirTerminator
    {
        public MirOperand Condition { get; }
        public string ThenTarget { get; }
        public string ElseTarget { get; }

        internal MirCondBranch(MirOperand condition, string thenTarget, string elseTarget)
        {
            Condition = condition;
            ThenTarget = thenTarget;
            ElseTarget = elseTarget;
        }
    }

    // switch：常量表匹配（§16.6）。表元素序与 ItemTargets 一一对应；
    // 匹配语义按表序首个 cmp.eq 命中（VM 同口径），无穿透
    public sealed class MirSwitch : MirTerminator
    {
        public MirOperand Selector { get; }
        public BilSwitchTableResource Table { get; }
        public IReadOnlyList<string> ItemTargets { get; }
        public string DefaultTarget { get; }

        internal MirSwitch(MirOperand selector, BilSwitchTableResource table,
            IReadOnlyList<string> itemTargets, string defaultTarget)
        {
            Selector = selector;
            Table = table;
            ItemTargets = itemTargets;
            DefaultTarget = defaultTarget;
        }
    }

    // 不可达块收尾（双分支均终结的汇聚块、ret/break 后的死块）
    public sealed class MirUnreachable : MirTerminator
    {
        internal MirUnreachable()
        {
        }
    }
}
