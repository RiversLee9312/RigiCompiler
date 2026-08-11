using System.Collections.Generic;

namespace LatteCompiler.Bil
{
    // §13 值/变量/字段/索引指令与 §14 构造、§15 调用指令（M57 强类型化；
    // S8c 增补 §13.6 set.array——get.array 模型自 M57 已存在）。
    // 规范的 asymmetric 操作数序由构造签名固定（如 §13.3 读取是
    // OBJECT TARGET 序、写入是 SOURCE OBJECT 序）。

    // §13.1 资源加载：load res(RESOURCE) TARGET
    public sealed class LoadInstruction : BilInstruction
    {
        public BilResource Resource { get; }
        public BilVariableOperand Target { get; }

        public LoadInstruction(BilResource resource, BilVariableOperand target)
        {
            Resource = resource;
            Target = target;
        }

        internal override string Opcode => "load";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { new BilResourceOperand(Resource), Target };
    }

    // §13.2 局部变量读取：get.var SOURCE TARGET
    public sealed class GetVarInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Target { get; }

        public GetVarInstruction(BilVariableOperand source, BilVariableOperand target)
        {
            Source = source;
            Target = target;
        }

        internal override string Opcode => "get.var";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Target };
    }

    // §13.2 局部变量写入：set.var SOURCE TARGET
    public sealed class SetVarInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Target { get; }

        public SetVarInstruction(BilVariableOperand source, BilVariableOperand target)
        {
            Source = source;
            Target = target;
        }

        internal override string Opcode => "set.var";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Target };
    }

    // §13.3 实例字段读取：get.field OBJECT TARGET field(FIELD)
    public sealed class GetFieldInstruction : BilInstruction
    {
        public BilVariableOperand Object { get; }
        public BilVariableOperand Target { get; }
        public BilFieldOperand Field { get; }

        public GetFieldInstruction(BilVariableOperand objectValue, BilVariableOperand target,
            BilFieldOperand field)
        {
            Object = objectValue;
            Target = target;
            Field = field;
        }

        internal override string Opcode => "get.field";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Object, Target, Field };
    }

    // §13.3 实例字段写入：set.field SOURCE OBJECT field(FIELD)
    public sealed class SetFieldInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Object { get; }
        public BilFieldOperand Field { get; }

        public SetFieldInstruction(BilVariableOperand source, BilVariableOperand objectValue,
            BilFieldOperand field)
        {
            Source = source;
            Object = objectValue;
            Field = field;
        }

        internal override string Opcode => "set.field";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Object, Field };
    }

    // §13.3 wrapper 隐藏存储写入后门：set.wrapper.field SOURCE OBJECT
    // CHAIN_ELEM... field(INNER_FIELD)。CHAIN 表达 field(F)+wrapper(W) 或
    // wrapper(W) 的 wrapper 存储寻址（非普通 set.field；读侧走
    // get.wrapper / get.wrapper.field 值拷贝 + 普通 get.field；普通值类型
    // 中间层反向写回由 lowering 发 set.field）
    public sealed class SetWrapperFieldInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Object { get; }
        public IReadOnlyList<BilOperand> Chain { get; }
        public BilFieldOperand InnerField { get; }

        public SetWrapperFieldInstruction(BilVariableOperand source,
            BilVariableOperand objectValue, IReadOnlyList<BilOperand> chain,
            BilFieldOperand innerField)
        {
            Source = source;
            Object = objectValue;
            Chain = chain;
            InnerField = innerField;
        }

        public SetWrapperFieldInstruction(BilVariableOperand source,
            BilVariableOperand objectValue, BilOperand chainElement, BilFieldOperand innerField)
            : this(source, objectValue, new[] { chainElement }, innerField)
        {
        }

        internal override string Opcode => "set.wrapper.field";
        internal override IReadOnlyList<BilOperand> Operands
        {
            get
            {
                var list = new List<BilOperand>(2 + Chain.Count + 1) { Source, Object };
                list.AddRange(Chain);
                list.Add(InnerField);
                return list;
            }
        }
    }

    // §13.4 静态字段读取：get.field.static TARGET type(OWNER_TYPE) field(FIELD)
    public sealed class GetFieldStaticInstruction : BilInstruction
    {
        public BilVariableOperand Target { get; }
        public BilTypeOperand OwnerType { get; }
        public BilFieldOperand Field { get; }

        public GetFieldStaticInstruction(BilVariableOperand target, BilTypeOperand ownerType,
            BilFieldOperand field)
        {
            Target = target;
            OwnerType = ownerType;
            Field = field;
        }

        internal override string Opcode => "get.field.static";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Target, OwnerType, Field };
    }

    // §13.4 静态字段写入：set.field.static SOURCE type(OWNER_TYPE) field(FIELD)
    public sealed class SetFieldStaticInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilTypeOperand OwnerType { get; }
        public BilFieldOperand Field { get; }

        public SetFieldStaticInstruction(BilVariableOperand source, BilTypeOperand ownerType,
            BilFieldOperand field)
        {
            Source = source;
            OwnerType = ownerType;
            Field = field;
        }

        internal override string Opcode => "set.field.static";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, OwnerType, Field };
    }

    // §13.6 索引读取：get.array ARRAY INDEX TARGET
    public sealed class GetArrayInstruction : BilInstruction
    {
        public BilVariableOperand Array { get; }
        public BilVariableOperand Index { get; }
        public BilVariableOperand Target { get; }

        public GetArrayInstruction(BilVariableOperand array, BilVariableOperand index,
            BilVariableOperand target)
        {
            Array = array;
            Index = index;
            Target = target;
        }

        internal override string Opcode => "get.array";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Array, Index, Target };
    }

    // §13.6 索引写入：set.array COLLECTION INDEX ELEMENT
    public sealed class SetArrayInstruction : BilInstruction
    {
        public BilVariableOperand Collection { get; }
        public BilVariableOperand Index { get; }
        public BilVariableOperand Element { get; }

        public SetArrayInstruction(BilVariableOperand collection, BilVariableOperand index,
            BilVariableOperand element)
        {
            Collection = collection;
            Index = index;
            Element = element;
        }

        internal override string Opcode => "set.array";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Collection, Index, Element };
    }

    // §14.1 静态普通构造：new type(TYPE) TARGET [ARGS]
    // （init 选择归 Middleware——按精确参数类型，发射不写 init 符号）
    public sealed class NewInstruction : BilInstruction
    {
        public BilTypeOperand Type { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public NewInstruction(BilTypeOperand type, BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            Type = type;
            Target = target;
            Arguments = arguments;
        }

        internal override string Opcode => "new";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Type, Target, new BilOperandList(Arguments) };
    }

    // §14.3 enum case 构造：new.case type(TYPE) case(CASE) TARGET [ARGS]
    public sealed class NewCaseInstruction : BilInstruction
    {
        public BilTypeOperand Type { get; }
        public BilCaseOperand Case { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public NewCaseInstruction(BilTypeOperand type, BilCaseOperand caseOperand,
            BilVariableOperand target, IReadOnlyList<BilVariableOperand> arguments)
        {
            Type = type;
            Case = caseOperand;
            Target = target;
            Arguments = arguments;
        }

        internal override string Opcode => "new.case";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Type, Case, Target, new BilOperandList(Arguments) };
    }

    // §15.1 带返回值直接调用：invoke fn(METHOD) TARGET [ARGS]
    public sealed class InvokeInstruction : BilInstruction
    {
        public BilFnOperand Method { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public InvokeInstruction(BilFnOperand method, BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            Method = method;
            Target = target;
            Arguments = arguments;
        }

        internal override string Opcode => "invoke";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Method, Target, new BilOperandList(Arguments) };
    }

    // §15.1 无返回值直接调用：invoke.noret fn(METHOD) [ARGS]
    public sealed class InvokeNoResultInstruction : BilInstruction
    {
        public BilFnOperand Method { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public InvokeNoResultInstruction(BilFnOperand method,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            Method = method;
            Arguments = arguments;
        }

        internal override string Opcode => "invoke.noret";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Method, new BilOperandList(Arguments) };
    }

    // §15.3：间接调用（callable 协议）——CALL_TARGET 是声明了 operator call
    // 的对象引用，调用 = 对该对象虚调用其 $$call 实现；实参列表不含 receiver
    public sealed class InvokeIndirectInstruction : BilInstruction
    {
        public BilVariableOperand CallTarget { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public InvokeIndirectInstruction(BilVariableOperand callTarget, BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            CallTarget = callTarget;
            Target = target;
            Arguments = arguments;
        }

        internal override string Opcode => "invoke.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { CallTarget, Target, new BilOperandList(Arguments) };
    }

    public sealed class InvokeIndirectNoResultInstruction : BilInstruction
    {
        public BilVariableOperand CallTarget { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public InvokeIndirectNoResultInstruction(BilVariableOperand callTarget,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            CallTarget = callTarget;
            Arguments = arguments;
        }

        internal override string Opcode => "invoke.indirect.noret";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { CallTarget, new BilOperandList(Arguments) };
    }
}
