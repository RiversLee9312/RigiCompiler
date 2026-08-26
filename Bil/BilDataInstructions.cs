using System.Collections.Generic;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Bil
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name, context.LoadResource(Resource));
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name, coroutine.ReadVar(Source.Name).Copy());
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            coroutine.WriteVar(Target.Name, coroutine.ReadVar(Source.Name).Copy());
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.GetField(context, coroutine, Object, Target, Field.Symbol);
        }
    }

    // §13.5 间接实例字段读取：get.field.indirect OBJECT TARGET FIELDID_VAR
    public sealed class GetFieldIndirectInstruction : BilInstruction
    {
        public BilVariableOperand Object { get; }
        public BilVariableOperand Target { get; }
        public BilVariableOperand FieldId { get; }

        public GetFieldIndirectInstruction(BilVariableOperand objectValue,
            BilVariableOperand target, BilVariableOperand fieldId)
        {
            Object = objectValue;
            Target = target;
            FieldId = fieldId;
        }

        internal override string Opcode => "get.field.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Object, Target, FieldId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.GetField(context, coroutine, Object, Target,
                VmTypeOps.RequireFieldId(coroutine.ReadVar(FieldId.Name)));
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.SetField(context, coroutine, Source, Object, Field.Symbol);
        }
    }

    // §13.5 间接实例字段写入：set.field.indirect SOURCE OBJECT FIELDID_VAR
    public sealed class SetFieldIndirectInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand Object { get; }
        public BilVariableOperand FieldId { get; }

        public SetFieldIndirectInstruction(BilVariableOperand source,
            BilVariableOperand objectValue, BilVariableOperand fieldId)
        {
            Source = source;
            Object = objectValue;
            FieldId = fieldId;
        }

        internal override string Opcode => "set.field.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, Object, FieldId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.SetField(context, coroutine, Source, Object,
                VmTypeOps.RequireFieldId(coroutine.ReadVar(FieldId.Name)));
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.SetWrapperField(context, coroutine, Source, Object, Chain, InnerField);
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.GetFieldStatic(context, coroutine, Target, Field.Symbol);
        }
    }

    // §13.5 间接静态字段读取：get.field.static.indirect TARGET TYPEID_VAR FIELDID_VAR
    public sealed class GetFieldStaticIndirectInstruction : BilInstruction
    {
        public BilVariableOperand Target { get; }
        public BilVariableOperand TypeId { get; }
        public BilVariableOperand FieldId { get; }

        public GetFieldStaticIndirectInstruction(BilVariableOperand target,
            BilVariableOperand typeId, BilVariableOperand fieldId)
        {
            Target = target;
            TypeId = typeId;
            FieldId = fieldId;
        }

        internal override string Opcode => "get.field.static.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Target, TypeId, FieldId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            _ = VmTypeOps.RequireTypeId(coroutine.ReadVar(TypeId.Name));
            BilDataExecution.GetFieldStatic(context, coroutine, Target,
                VmTypeOps.RequireFieldId(coroutine.ReadVar(FieldId.Name)));
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.SetFieldStatic(context, coroutine, Source, Field.Symbol);
        }
    }

    // §13.5 间接静态字段写入：set.field.static.indirect SOURCE TYPEID_VAR FIELDID_VAR
    public sealed class SetFieldStaticIndirectInstruction : BilInstruction
    {
        public BilVariableOperand Source { get; }
        public BilVariableOperand TypeId { get; }
        public BilVariableOperand FieldId { get; }

        public SetFieldStaticIndirectInstruction(BilVariableOperand source,
            BilVariableOperand typeId, BilVariableOperand fieldId)
        {
            Source = source;
            TypeId = typeId;
            FieldId = fieldId;
        }

        internal override string Opcode => "set.field.static.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Source, TypeId, FieldId };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            _ = VmTypeOps.RequireTypeId(coroutine.ReadVar(TypeId.Name));
            BilDataExecution.SetFieldStatic(context, coroutine, Source,
                VmTypeOps.RequireFieldId(coroutine.ReadVar(FieldId.Name)));
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.GetArray(context, coroutine, Array, Index, Target);
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.SetArray(context, coroutine, Collection, Index, Element);
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.New(context, coroutine, Type.TypeRef, Target, Arguments,
                wrapperArguments: null);
        }
    }

    // §14.2 动态普通构造：new.indirect TYPEID_VAR RESULT [ARGS]
    public sealed class NewIndirectInstruction : BilInstruction
    {
        public BilVariableOperand TypeId { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public NewIndirectInstruction(BilVariableOperand typeId, BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            TypeId = typeId;
            Target = target;
            Arguments = arguments;
        }

        internal override string Opcode => "new.indirect";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { TypeId, Target, new BilOperandList(Arguments) };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            var typeRef = VmTypeOps.RequireTypeId(coroutine.ReadVar(TypeId.Name));
            var declaration = context.FindType(typeRef);
            if (declaration != null
                && (declaration.Kind == BilTypeKind.EnumStruct
                    || HasAbstract(declaration)))
            {
                throw context.NoSuchMethod("new.indirect 目标不可构造：" + typeRef);
            }
            try
            {
                BilDataExecution.New(context, coroutine, typeRef, Target, Arguments,
                    wrapperArguments: null);
            }
            catch (VmException exception) when (exception.ExceptionObject == null)
            {
                throw context.NoSuchMethod(exception.Message);
            }
        }

        private static bool HasAbstract(BilTypeDeclaration declaration)
        {
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keyword
                    && keyword.Keyword == BilKeyword.Abstract)
                {
                    return true;
                }
            }
            return false;
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.NewCase(context, coroutine, Type.TypeRef, Case.QualifiedName,
                Target, Arguments, wrapperArguments: null);
        }
    }

    // §14.4.1 有参 ..init.wrapper 的普通构造：
    // new.wrapped type(TYPE) TARGET [WRAPPER_ARGS] [INIT_ARGS]
    public sealed class NewWrappedInstruction : BilInstruction
    {
        public BilTypeOperand Type { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> WrapperArguments { get; }
        public IReadOnlyList<BilVariableOperand> InitArguments { get; }

        public NewWrappedInstruction(BilTypeOperand type, BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> wrapperArguments,
            IReadOnlyList<BilVariableOperand> initArguments)
        {
            Type = type;
            Target = target;
            WrapperArguments = wrapperArguments;
            InitArguments = initArguments;
        }

        internal override string Opcode => "new.wrapped";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[]
            {
                Type, Target,
                new BilOperandList(WrapperArguments),
                new BilOperandList(InitArguments),
            };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.New(context, coroutine, Type.TypeRef, Target, InitArguments,
                WrapperArguments);
        }
    }

    // §14.4.2 有参 ..init.wrapper 的 enum case 构造：
    // new.wrapped.case type(ENUM) case(CASE) TARGET [WRAPPER_ARGS] [CASE_ARGS]
    public sealed class NewWrappedCaseInstruction : BilInstruction
    {
        public BilTypeOperand Type { get; }
        public BilCaseOperand Case { get; }
        public BilVariableOperand Target { get; }
        public IReadOnlyList<BilVariableOperand> WrapperArguments { get; }
        public IReadOnlyList<BilVariableOperand> CaseArguments { get; }

        public NewWrappedCaseInstruction(BilTypeOperand type, BilCaseOperand caseOperand,
            BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> wrapperArguments,
            IReadOnlyList<BilVariableOperand> caseArguments)
        {
            Type = type;
            Case = caseOperand;
            Target = target;
            WrapperArguments = wrapperArguments;
            CaseArguments = caseArguments;
        }

        internal override string Opcode => "new.wrapped.case";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[]
            {
                Type, Case, Target,
                new BilOperandList(WrapperArguments),
                new BilOperandList(CaseArguments),
            };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.NewCase(context, coroutine, Type.TypeRef, Case.QualifiedName,
                Target, CaseArguments, WrapperArguments);
        }
    }

    // §14.5 字段-Value wrapper 初始化（仅 ..init.wrapper 体内）：
    // new.wrapper.field field(FIELD) type(WRAPPER) [ARGS]
    public sealed class NewWrapperFieldInstruction : BilInstruction
    {
        public BilFieldOperand Field { get; }
        public BilTypeOperand WrapperType { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public NewWrapperFieldInstruction(BilFieldOperand field, BilTypeOperand wrapperType,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            Field = field;
            WrapperType = wrapperType;
            Arguments = arguments;
        }

        internal override string Opcode => "new.wrapper.field";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Field, WrapperType, new BilOperandList(Arguments) };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.InstallWrapper(context, coroutine, WrapperType.TypeRef, Arguments,
                VmContext.HiddenFieldKey(Field.Symbol, WrapperType.TypeRef));
        }
    }

    // §14.5 方法 Method wrapper 初始化（仅 ..init.wrapper 体内）：
    // new.wrapper.method fn(METHOD) type(WRAPPER) [ARGS]
    public sealed class NewWrapperMethodInstruction : BilInstruction
    {
        public BilFnOperand Method { get; }
        public BilTypeOperand WrapperType { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public NewWrapperMethodInstruction(BilFnOperand method, BilTypeOperand wrapperType,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            Method = method;
            WrapperType = wrapperType;
            Arguments = arguments;
        }

        internal override string Opcode => "new.wrapper.method";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { Method, WrapperType, new BilOperandList(Arguments) };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.InstallWrapper(context, coroutine, WrapperType.TypeRef, Arguments,
                VmContext.HiddenMethodKey(Method.Symbol, WrapperType.TypeRef));
        }
    }

    // §14.5 实体 Entity wrapper 初始化（仅 ..init.wrapper 体内）：
    // new.wrapper.entity type(WRAPPER) [ARGS]
    public sealed class NewWrapperEntityInstruction : BilInstruction
    {
        public BilTypeOperand WrapperType { get; }
        public IReadOnlyList<BilVariableOperand> Arguments { get; }

        public NewWrapperEntityInstruction(BilTypeOperand wrapperType,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            WrapperType = wrapperType;
            Arguments = arguments;
        }

        internal override string Opcode => "new.wrapper.entity";
        internal override IReadOnlyList<BilOperand> Operands =>
            new BilOperand[] { WrapperType, new BilOperandList(Arguments) };

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.InstallWrapper(context, coroutine, WrapperType.TypeRef, Arguments,
                VmContext.HiddenEntityKey(WrapperType.TypeRef));
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilInvokeExecution.Invoke(context, coroutine, Method.Symbol, Arguments, Target.Name);
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilInvokeExecution.Invoke(context, coroutine, Method.Symbol, Arguments, resultSlot: null);
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.InvokeIndirect(context, coroutine, CallTarget, Arguments,
                Target.Name);
        }
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

        internal override void Execute(VmContext context, VmCoroutine coroutine)
        {
            BilDataExecution.InvokeIndirect(context, coroutine, CallTarget, Arguments,
                resultSlot: null);
        }
    }

    // §13/§14 数据与构造基建（BIL_VM_DESIGN §5 / §9 切片 2 / BIL_STANDARD §13 / §14 / §22.4）：
    // 字段按符号元数据执行 getter/setter 或直访；静态字段加锁；
    // new 先装 ..init.wrapper 再调 init；wrapper 隐藏存储按 §5.3 约定安装。
    internal static class BilDataExecution
    {
        internal static void GetField(VmContext context, VmCoroutine coroutine,
            BilVariableOperand objectVar, BilVariableOperand target, string fieldSymbol)
        {
            var currentFn = coroutine.CurrentFrame.Function.Symbol;
            // 0) setter 体内 ..value：直读 backing
            if (context.TryResolveBackingValue(fieldSymbol, currentFn, out var backing))
            {
                var host = coroutine.ReadVar(objectVar.Name);
                coroutine.WriteVar(target.Name, ReadInstanceField(host, backing).Copy());
                return;
            }
            // 1) getter 体内读自身字段：直读 backing（不再绕 wrapper）
            if (context.IsGetterOf(currentFn, fieldSymbol))
            {
                var host = coroutine.ReadVar(objectVar.Name);
                coroutine.WriteVar(target.Name, ReadInstanceField(host, fieldSymbol).Copy());
                return;
            }
            // 2) 使用点 getter：结果在返回后过 wrapper 链
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Getter,
                    currentFn, out var getter))
            {
                var wrappers = context.CollectWrappedWrappers(fieldSymbol);
                var host = coroutine.ReadVar(objectVar.Name);
                if (wrappers.Count > 0)
                {
                    var temp = coroutine.RegisterPendingGetChain(target.Name, host, fieldSymbol,
                        FieldType(fieldSymbol), wrappers, Array.Empty<string>());
                    BilInvokeExecution.Invoke(context, coroutine, getter,
                        new[] { objectVar }, temp);
                    return;
                }
                // 与方法链同口径：子类 Entity wrapper 拦截继承字段
                var entityWrappers = context.CollectEntityWrappers(VmTypeOps.ActualType(host));
                if (entityWrappers.Count > 0)
                {
                    var temp = coroutine.RegisterPendingGetChain(target.Name, host, fieldSymbol,
                        FieldType(fieldSymbol), Array.Empty<string>(), entityWrappers);
                    BilInvokeExecution.Invoke(context, coroutine, getter,
                        new[] { objectVar }, temp);
                    return;
                }
                BilInvokeExecution.Invoke(context, coroutine, getter,
                    new[] { objectVar }, target.Name);
                return;
            }
            var instance = coroutine.ReadVar(objectVar.Name);
            if (instance is IVmIndexBuffer buffer
                && (fieldSymbol == VmArray.LengthFieldSymbol
                    || VmSpan.IsLengthField(fieldSymbol)))
            {
                coroutine.WriteVar(target.Name, new VmI32(buffer.Length));
                return;
            }
            // String.length（.bootstrap.rg 的 pub ext const 声明，无 backing
            // 存储）：与 Array.length 同一 VM 直读通道——按宿主字符串实际
            // 长度求值（裁定 i64）
            if (instance is VmString text && fieldSymbol == VmString.LengthFieldSymbol)
            {
                coroutine.WriteVar(target.Name, new VmI64(text.Value.Length));
                return;
            }
            var raw = ReadInstanceField(instance, fieldSymbol).Copy();
            var fieldWrappers = context.CollectWrappedWrappers(fieldSymbol);
            if (fieldWrappers.Count == 0)
            {
                // 字段自身无 wrapped 标记、但宿主类型带 wrapped 标记 → Entity
                // getter 类别（.proxy.get.<名> / .proxy.get.*）；按实例实际类型
                // 收集，使子类 wrapper 拦截继承字段
                var entityWrappers = context.CollectEntityWrappers(VmTypeOps.ActualType(instance));
                if (entityWrappers.Count > 0)
                {
                    if (VmWrapperDispatch.ApplyEntityGetChain(context, coroutine, instance,
                            fieldSymbol, FieldType(fieldSymbol), entityWrappers, raw,
                            out var entityFinal))
                    {
                        coroutine.WriteVar(target.Name, entityFinal);
                    }
                    return;
                }
                coroutine.WriteVar(target.Name, raw);
                return;
            }
            if (VmWrapperDispatch.ApplyGetChain(context, coroutine, instance, fieldSymbol,
                    FieldType(fieldSymbol), fieldWrappers, raw, out var final))
            {
                coroutine.WriteVar(target.Name, final);
            }
        }

        internal static void SetField(VmContext context, VmCoroutine coroutine,
            BilVariableOperand source, BilVariableOperand objectVar, string fieldSymbol)
        {
            var currentFn = coroutine.CurrentFrame.Function.Symbol;
            // 0) setter 体内 ..value：直写 backing
            if (context.TryResolveBackingValue(fieldSymbol, currentFn, out var backing))
            {
                var host = coroutine.ReadVar(objectVar.Name);
                WriteInstanceField(host, backing, coroutine.ReadVar(source.Name).Copy());
                return;
            }
            var isInit = context.IsInitFunctionOf(currentFn, FieldOwner(fieldSymbol));
            // 1) 构造期：不绕 wrapper 链；有 setter 则调，否则直写
            if (isInit)
            {
                if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Setter,
                        currentFn, out var initSetter))
                {
                    BilInvokeExecution.Invoke(context, coroutine, initSetter,
                        new[] { objectVar, source }, resultSlot: null);
                    return;
                }
                var host = coroutine.ReadVar(objectVar.Name);
                WriteInstanceField(host, fieldSymbol, coroutine.ReadVar(source.Name).Copy());
                return;
            }
            // 2) Value wrapper 链（链末改调 setter）
            var wrappers = context.CollectWrappedWrappers(fieldSymbol);
            if (wrappers.Count > 0)
            {
                var host = coroutine.ReadVar(objectVar.Name);
                var value = coroutine.ReadVar(source.Name).Copy();
                VmWrapperDispatch.ApplySetChain(context, coroutine, host, fieldSymbol,
                    FieldType(fieldSymbol), wrappers, value);
                return;
            }
            // 3) Entity wrapper（按实例实际类型，子类 wrapper 拦截继承字段）
            var entityHost = coroutine.ReadVar(objectVar.Name);
            var entityWrappers = context.CollectEntityWrappers(VmTypeOps.ActualType(entityHost));
            if (entityWrappers.Count > 0)
            {
                var value = coroutine.ReadVar(source.Name).Copy();
                if (VmWrapperDispatch.TryStartEntitySetChain(context, coroutine, entityHost,
                        fieldSymbol, FieldType(fieldSymbol), entityWrappers, value,
                        resultSlot: null))
                {
                    return;
                }
            }
            // 4) setter
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Setter,
                    currentFn, out var setter))
            {
                BilInvokeExecution.Invoke(context, coroutine, setter,
                    new[] { objectVar, source }, resultSlot: null);
                return;
            }
            // 5) 直写
            WriteInstanceField(coroutine.ReadVar(objectVar.Name), fieldSymbol,
                coroutine.ReadVar(source.Name).Copy());
        }

        internal static void GetFieldStatic(VmContext context, VmCoroutine coroutine,
            BilVariableOperand target, string fieldSymbol)
        {
            var currentFn = coroutine.CurrentFrame.Function.Symbol;
            if (context.TryResolveBackingValue(fieldSymbol, currentFn, out var backing))
            {
                coroutine.WriteVar(target.Name, context.ReadStaticField(backing).Copy());
                return;
            }
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Getter,
                    currentFn, out var getter))
            {
                BilInvokeExecution.Invoke(context, coroutine, getter,
                    Array.Empty<BilVariableOperand>(), target.Name);
                return;
            }
            coroutine.WriteVar(target.Name, context.ReadStaticField(fieldSymbol).Copy());
        }

        internal static void SetFieldStatic(VmContext context, VmCoroutine coroutine,
            BilVariableOperand source, string fieldSymbol)
        {
            var currentFn = coroutine.CurrentFrame.Function.Symbol;
            if (context.TryResolveBackingValue(fieldSymbol, currentFn, out var backing))
            {
                context.WriteStaticField(backing, coroutine.ReadVar(source.Name).Copy());
                return;
            }
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Setter,
                    currentFn, out var setter))
            {
                BilInvokeExecution.Invoke(context, coroutine, setter,
                    new[] { source }, resultSlot: null);
                return;
            }
            context.WriteStaticField(fieldSymbol, coroutine.ReadVar(source.Name).Copy());
        }

        internal static void SetWrapperField(VmContext context, VmCoroutine coroutine,
            BilVariableOperand source, BilVariableOperand objectVar,
            IReadOnlyList<BilOperand> chain, BilFieldOperand innerField)
        {
            var current = coroutine.ReadVar(objectVar.Name);
            var index = 0;
            while (index < chain.Count)
            {
                if (chain[index] is BilFieldOperand field
                    && index + 1 < chain.Count
                    && chain[index + 1] is BilWrapperOperand wrapper
                    && FieldHasWrapped(context, field.Symbol, wrapper.TypeRef))
                {
                    current = ReadHidden(current,
                        VmContext.HiddenFieldKey(field.Symbol, wrapper.TypeRef));
                    index += 2;
                    continue;
                }
                if (chain[index] is BilFieldOperand nested)
                {
                    current = ReadInstanceField(current, nested.Symbol);
                    index++;
                    continue;
                }
                if (chain[index] is BilWrapperOperand entity)
                {
                    current = ReadHidden(current, VmContext.HiddenEntityKey(entity.TypeRef));
                    index++;
                    continue;
                }
                throw new VmException("set.wrapper.field 链元素非法");
            }
            WriteInstanceField(current, innerField.Symbol,
                coroutine.ReadVar(source.Name).Copy());
        }

        internal static void GetArray(VmContext context, VmCoroutine coroutine,
            BilVariableOperand collectionVar, BilVariableOperand indexVar,
            BilVariableOperand target)
        {
            var collection = coroutine.ReadVar(collectionVar.Name);
            if (collection is IVmIndexBuffer buffer)
            {
                // Q6（SYNTAX §13.2 / BIL §13.6）：内建 Array/Span 的索引读取语义上
                // 走 getAtIndex（返回 T?）——界内元素包成 Nullable\<T\>
                // （值类型 VmNullable 存在位包装，引用类型沿用 VmNull 表示）；
                // 越界读取不再 trap，得 null
                var index = VmContext.RequireIndex(coroutine.ReadVar(indexVar.Name));
                if (index < 0 || index >= buffer.Length)
                {
                    coroutine.WriteVar(target.Name, VmNull.Instance);
                    return;
                }
                coroutine.WriteVar(target.Name,
                    VmTypeOps.WrapNullable(buffer.GetAt(index).Copy(), buffer.ElementType));
                return;
            }
            var method = context.FindIndexOperator(collection.TypeRef, isGet: true);
            if (method == null)
            {
                throw new VmException("没有 getAtIndex：" + collection.TypeRef);
            }
            BilInvokeExecution.Invoke(context, coroutine, method,
                new[] { collectionVar, indexVar }, target.Name);
        }

        internal static void SetArray(VmContext context, VmCoroutine coroutine,
            BilVariableOperand collectionVar, BilVariableOperand indexVar,
            BilVariableOperand elementVar)
        {
            var collection = coroutine.ReadVar(collectionVar.Name);
            if (collection is IVmIndexBuffer buffer)
            {
                buffer.SetAt(VmContext.RequireIndex(coroutine.ReadVar(indexVar.Name)),
                    coroutine.ReadVar(elementVar.Name).Copy());
                return;
            }
            var method = context.FindIndexOperator(collection.TypeRef, isGet: false);
            if (method == null)
            {
                throw new VmException("没有 setAtIndex：" + collection.TypeRef);
            }
            BilInvokeExecution.Invoke(context, coroutine, method,
                new[] { collectionVar, indexVar, elementVar }, resultSlot: null);
        }

        internal static void InvokeIndirect(VmContext context, VmCoroutine coroutine,
            BilVariableOperand callTarget, IReadOnlyList<BilVariableOperand> arguments,
            string? resultSlot)
        {
            var receiver = coroutine.ReadVar(callTarget.Name);
            var values = new VmValue[arguments.Count];
            for (var i = 0; i < arguments.Count; i++)
            {
                values[i] = coroutine.ReadVar(arguments[i].Name);
            }
            // callable 协议（§15.3）：对 receiver 实际类型虚派发其 $$call 实现
            //（逻辑 TypeSheet 槽匹配，lambda 隐藏类经继承槽 + 自有 override 命中）
            var symbol = context.FindCallTarget(VmTypeOps.ActualType(receiver), values);
            if (symbol == null)
            {
                throw context.NoSuchMethod("没有匹配的 $$call：" + receiver.TypeRef);
            }
            var callArgs = new List<VmValue>(1 + values.Length) { receiver };
            callArgs.AddRange(values);
            BilInvokeExecution.InvokeValues(context, coroutine, symbol, callArgs, resultSlot);
        }

        internal static void New(VmContext context, VmCoroutine coroutine, string typeRef,
            BilVariableOperand target, IReadOnlyList<BilVariableOperand> initArguments,
            IReadOnlyList<BilVariableOperand>? wrapperArguments)
        {
            // 泛型函数体内的直达构造（new type(T<.generic<$.generic.X>,...>)）：
            // 先按当前帧 hidden typeid 绑定把构造实参解析成具体类型
            //（VmTypeOps.ResolveTypeRef，cast/is 同一通道），init 匹配与实例
            // TypeRef 一律以具体形态落地
            typeRef = VmTypeOps.ResolveTypeRef(context, coroutine, typeRef);
            // 具化构造 T() / new.indirect：内建标量与 String 无用户 init，
            // 产出该类型零值（SYNTAX §3.6/§3.7，i32 → 0）。不得 AllocateObject
            // 出 VmObject（打印成 ".i32" 且没有 plus）。
            if (IsPrimitiveZeroConstructible(typeRef))
            {
                if (initArguments.Count > 0)
                {
                    throw new VmException("new 实参不匹配任何 init：" + typeRef);
                }
                coroutine.WriteVar(target.Name, context.ZeroOf(typeRef));
                return;
            }
            // 编译器打包形态（vargs/kwargs）：实参即元素。已拆除 V2
            // 「单 i32 = 长度」特权——用户构造只经 alloc_array / arrayOf。
            if (VmContext.IsArrayType(typeRef, out var elementType))
            {
                var items = ReadArgs(coroutine, initArguments);
                var array = new VmArray(elementType, items.Length, context.ZeroOf(elementType));
                for (var i = 0; i < items.Length; i++)
                {
                    array.SetAt(i, items[i].Copy());
                }
                coroutine.WriteVar(target.Name, array);
                return;
            }
            // singleton（§8.7，裁定 2）：全模块唯一实例。已初始化时返回
            // 缓存实例（不再重跑 init）；在途（init 尚未跑完）时是循环依赖
            // → 抛带循环链的 VmException；首次构造走同步路径——借当前协程
            // 的调用帧把 init 跑完再登记，嵌套 new 在此递归（Step 重入，同
            // ProbePolling），保证任一 singleton 的 init 里访问另一 singleton
            // 时按需递归触发、副作用恰好一次
            if (context.IsSingletonType(typeRef))
            {
                var existing = context.GetSingleton(typeRef);
                if (existing != null)
                {
                    coroutine.WriteVar(target.Name, existing);
                    return;
                }
                if (context.IsInitializing(typeRef))
                {
                    throw context.SingletonCycleException(typeRef);
                }
            }
            var instance = context.AllocateObject(typeRef);
            coroutine.WriteVar(target.Name, instance);
            var initArgs = ReadArgs(coroutine, initArguments);
            var initStaticTypes = VmContext.ArgumentStaticTypes(
                coroutine.CurrentFrame.Function, initArguments, initArgs);
            if (!context.TryFindInit(typeRef, initArgs, initStaticTypes, out var initSymbol)
                && initArguments.Count > 0)
            {
                throw new VmException("new 实参不匹配任何 init：" + typeRef);
            }
            if (context.IsSingletonType(typeRef))
            {
                context.BeginInitializing(typeRef);
                var depth = coroutine.CallStack.Count;
                try
                {
                    PushConstructorTail(context, coroutine, typeRef, initSymbol,
                        initArguments, wrapperArguments, target);
                    while (coroutine.CallStack.Count > depth
                        && coroutine.State == VmCoroutineState.Running)
                    {
                        coroutine.Step(context);
                    }
                }
                finally
                {
                    context.EndInitializing(typeRef);
                }
                if (coroutine.State != VmCoroutineState.Running)
                {
                    throw coroutine.Failure ?? new VmException("singleton 初始化失败：" + typeRef);
                }
                context.RegisterSingleton(typeRef, instance);
                return;
            }
            PushConstructorTail(context, coroutine, typeRef, initSymbol,
                initArguments, wrapperArguments, target);
        }

        internal static void NewCase(VmContext context, VmCoroutine coroutine, string typeRef,
            string caseSymbol, BilVariableOperand target,
            IReadOnlyList<BilVariableOperand> caseArguments,
            IReadOnlyList<BilVariableOperand>? wrapperArguments)
        {
            var payload = ReadArgs(coroutine, caseArguments);
            var instance = new VmEnum(typeRef, caseSymbol, payload);
            foreach (var field in context.CollectInstanceFields(typeRef))
            {
                if (!BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out _, out _, out var fieldType))
                {
                    continue;
                }
                instance.WriteField(field.Symbol, context.ZeroOf(fieldType));
            }
            coroutine.WriteVar(target.Name, instance);
            var initSymbol = "";
            var caseStaticTypes = VmContext.ArgumentStaticTypes(
                coroutine.CurrentFrame.Function, caseArguments, payload);
            if (!context.TryFindInit(typeRef, payload, caseStaticTypes, out initSymbol)
                && caseArguments.Count > 0)
            {
                throw new VmException("new.case 实参不匹配任何 init：" + typeRef);
            }
            PushConstructorTail(context, coroutine, typeRef, initSymbol,
                caseArguments, wrapperArguments, target);
        }

        internal static void InstallWrapper(VmContext context, VmCoroutine coroutine,
            string wrapperType, IReadOnlyList<BilVariableOperand> arguments, string hiddenKey)
        {
            var hostValue = coroutine.ReadVar(".this");
            if (!VmObject.TryAsHost(hostValue, out var host))
            {
                throw new VmException("new.wrapper.* 要求 .this 为对象实例");
            }
            var wrapper = context.AllocateObject(wrapperType);
            wrapper.Host = hostValue;
            host.WriteHidden(hiddenKey, wrapper);
            var initArgs = ReadArgs(coroutine, arguments);
            var wrapperInitStaticTypes = VmContext.ArgumentStaticTypes(
                coroutine.CurrentFrame.Function, arguments, initArgs);
            // TryFindInit 在「无 init 声明 + 零实参」时返回 true 且 initSymbol 为空：
            // 该形态表示安装即完成，不能拿空符号去 invoke
            var hasInit = context.TryFindInit(wrapperType, initArgs, wrapperInitStaticTypes,
                    out var initSymbol)
                && initSymbol.Length > 0;
            if (!hasInit && arguments.Count > 0)
            {
                throw new VmException("wrapper init 实参不匹配：" + wrapperType);
            }
            if (hasInit)
            {
                var values = new List<VmValue> { wrapper };
                values.AddRange(initArgs);
                BilInvokeExecution.InvokeValues(context, coroutine, initSymbol, values,
                    resultSlot: null);
            }
            // wrapper 类型自身的 ..init.wrapper（新 init 原则 §9.7：wrapper
            // 类型成员的 wrapper 安装与字段初值）——类型级 ..init.wrapper 恒
            // 无参。调用帧 LIFO：后压先执行——压在 init 帧之上 ⇒ 先于
            // wrapper 的 init 体运行
            if (context.TryFindInitWrapper(wrapperType, out var wrapperInitSymbol,
                    out var wrapperInitArity) && wrapperInitArity == 0)
            {
                BilInvokeExecution.InvokeValues(context, coroutine, wrapperInitSymbol,
                    new List<VmValue> { wrapper }, resultSlot: null);
            }
        }

        private static void PushConstructorTail(VmContext context, VmCoroutine coroutine,
            string typeRef, string initSymbol,
            IReadOnlyList<BilVariableOperand> initArguments,
            IReadOnlyList<BilVariableOperand>? wrapperArguments,
            BilVariableOperand thisVar)
        {
            var receiver = coroutine.ReadVar(thisVar.Name);
            List<VmValue>? initCall = null;
            if (initSymbol.Length > 0)
            {
                initCall = new List<VmValue> { receiver };
                initCall.AddRange(ReadArgs(coroutine, initArguments));
            }
            List<VmValue>? wrapperCall = null;
            var wrapperSymbol = "";
            var hasWrapperArgs = wrapperArguments != null;
            if (context.TryFindInitWrapper(typeRef, out wrapperSymbol, out var wrapperArity)
                && (hasWrapperArgs || wrapperArity == 0))
            {
                if (hasWrapperArgs && wrapperArguments!.Count != wrapperArity)
                {
                    throw new VmException("..init.wrapper 实参个数不匹配：" + typeRef);
                }
                wrapperCall = new List<VmValue> { receiver };
                if (wrapperArguments != null)
                {
                    wrapperCall.AddRange(ReadArgs(coroutine, wrapperArguments));
                }
            }
            else if (hasWrapperArgs)
            {
                throw new VmException("类型没有 ..init.wrapper：" + typeRef);
            }
            // §9.7/§14.4：..init.wrapper 在实体 init 之前自动调用（wrapper 先
            // 安装、字段初值先落、再跑 init 链）。调用帧是 LIFO 栈——先压
            // init 帧、后压 ..init.wrapper 帧，执行序才是 wrapper → init
            if (initCall != null)
            {
                BilInvokeExecution.InvokeValues(context, coroutine, initSymbol, initCall,
                    resultSlot: null);
            }
            if (wrapperCall != null)
            {
                BilInvokeExecution.InvokeValues(context, coroutine, wrapperSymbol, wrapperCall,
                    resultSlot: null);
            }
        }

        private static bool IsPrimitiveZeroConstructible(string typeRef)
        {
            return typeRef is ".i8" or ".i16" or ".i32" or ".i64"
                or ".u8" or ".u16" or ".u32" or ".u64"
                or ".f32" or ".f64" or ".bool" or ".char"
                or ".string" or "core::String"
                or "core::i8" or "core::i16" or "core::i32" or "core::i64"
                or "core::u8" or "core::u16" or "core::u32" or "core::u64"
                or "core::float" or "core::double" or "core::bool" or "core::char";
        }

        private static VmValue[] ReadArgs(VmCoroutine coroutine,
            IReadOnlyList<BilVariableOperand> arguments)
        {
            var values = new VmValue[arguments.Count];
            for (var i = 0; i < arguments.Count; i++)
            {
                values[i] = coroutine.ReadVar(arguments[i].Name);
            }
            return values;
        }

        private static string FieldType(string fieldSymbol)
        {
            return BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                out _, out _, out var fieldType) ? fieldType : ".any";
        }

        private static string FieldOwner(string fieldSymbol)
        {
            return BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                out var owner, out _, out _) ? owner : "";
        }

        private static bool FieldHasWrapped(VmContext context, string fieldSymbol,
            string wrapperType)
        {
            var field = context.FindField(fieldSymbol);
            if (field == null)
            {
                return false;
            }
            foreach (var modifier in field.Modifiers)
            {
                if (modifier is BilWrappedModifier wrapped
                    && VmContext.TypesEqual(wrapped.WrapperTypeRef, wrapperType))
                {
                    return true;
                }
            }
            return false;
        }

        private static VmValue ReadInstanceField(VmValue instance, string fieldSymbol)
        {
            RequireHost(instance, out var host);
            if (!host.TryReadField(fieldSymbol, out var value))
            {
                throw new VmException("字段未初始化：" + fieldSymbol);
            }
            return value;
        }

        private static void WriteInstanceField(VmValue instance, string fieldSymbol, VmValue value)
        {
            RequireHost(instance, out var host);
            host.WriteField(fieldSymbol, value);
        }

        private static VmValue ReadHidden(VmValue instance, string key)
        {
            RequireHost(instance, out var host);
            if (!host.TryReadHidden(key, out var value))
            {
                throw new VmException("wrapper 隐藏存储不存在：" + key);
            }
            return value;
        }

        private static void RequireHost(VmValue instance, out IVmFieldHost host)
        {
            if (instance is VmNull)
            {
                throw new VmException("对 null 做字段访问");
            }
            if (!VmObject.TryAsHost(instance, out host))
            {
                throw new VmException("字段访问目标不是对象：" + instance.TypeRef);
            }
        }
    }

    // §15.1 / §15.2 直接调用基建（BIL_VM_DESIGN §5 / §9 切片 5 / BIL_STANDARD §7 / §22.5）：
    // native 走 hook 表；async 按 RUNTIME §18.1 五步 eager spawn；其余同步压帧。
    internal static class BilInvokeExecution
    {
        internal static void Invoke(VmContext context, VmCoroutine coroutine, string methodSymbol,
            IReadOnlyList<BilVariableOperand> arguments, string? resultSlot)
        {
            var args = new VmValue[arguments.Count];
            for (var i = 0; i < arguments.Count; i++)
            {
                args[i] = coroutine.ReadVar(arguments[i].Name);
            }
            var staticTypes = VmContext.ArgumentStaticTypes(
                coroutine.CurrentFrame.Function, arguments, args);
            InvokeValues(context, coroutine, methodSymbol, args, resultSlot, staticTypes);
        }

        internal static void InvokeValues(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            InvokeValues(context, coroutine, methodSymbol, args, resultSlot, null);
        }

        internal static void InvokeValues(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot,
            IReadOnlyList<string>? argumentStaticTypes)
        {
            // §15.4：invoke fn(..inner) 是 proxy 模板内的保留目标，由派发上下文
            // 在执行期解析「下一环」；上下文外出现抛清晰 VmException。
            if (methodSymbol == BilSpellings.InnerReservedFunction)
            {
                VmWrapperDispatch.ResolveInner(context, coroutine, args, resultSlot);
                return;
            }
            if (methodSymbol == BilSpellings.SuperReservedFunction)
            {
                ResolveSuper(context, coroutine, args, resultSlot, argumentStaticTypes);
                return;
            }
            // §14.2/§14.3 call??? 降级路由：在方法 hook 默认抛之前拦截，按
            // receiver 类型 wrapper 链找能路由的 proxy；无路由时落回 hook 默认
            if (IsCallWildcardSymbol(methodSymbol)
                && VmWrapperDispatch.TryStartCallChain(context, coroutine, args, resultSlot))
            {
                return;
            }
            // native hook（§22.5）：hook 天然挂在实现上——可被 override 的
            // toString 默认实现已是合成 fn 体（调 any_to_string），native
            // 符号不再带 receiver 派发语义；NativeDeclarationChecker 强制
            // native 类型成员必须 static，非 static native 实例方法不可能
            // 存在，无需「hook 给 override 让位」特判
            if (context.TryResolveNative(methodSymbol, out var library, out var nativeSymbol))
            {
                var result = context.Hooks.Invoke(context, library, nativeSymbol, args,
                    methodSymbol);
                if (resultSlot != null)
                {
                    coroutine.WriteVar(resultSlot, result);
                }
                return;
            }
            // §22.5 方法 hook（core::Any$call???）：无 BIL fn 定义，按方法符号命中
            if (context.Hooks.TryInvokeMethod(context, methodSymbol, args, out var methodResult))
            {
                if (resultSlot != null)
                {
                    coroutine.WriteVar(resultSlot, methodResult);
                }
                return;
            }
            // §14.2 Entity wrapper 成员方法派发：Host 类型声明带 wrapped 标记时
            // 按 outer→inner 建链；无 wrapper 或无 proxy 可路由走普通 invoke
            if (VmWrapperDispatch.TryStartMethodChain(context, coroutine, methodSymbol,
                    args, resultSlot))
            {
                return;
            }
            // §14.4 Method wrapper 派发（.proxy.call）：wrapper 安装是运行时事实
            // （frontend 在宿主 ..init.wrapper 内发 new.wrapper.method，静态方法经
            // companion 实例）；查 receiver 实例的 method 隐藏存储，命中则建链。
            if (VmWrapperDispatch.TryStartMethodWrapperChain(context, coroutine, methodSymbol,
                    args, resultSlot))
            {
                return;
            }
            // wrapped cell 的 getValue/setValue 派发：wrapper 链在访问器外侧
            if (VmWrapperDispatch.TryStartCellAccessorChain(context, coroutine, methodSymbol,
                    args, resultSlot))
            {
                return;
            }
            InvokeResolved(context, coroutine, methodSymbol, args, resultSlot);
        }

        // 跳过 wrapper 派发/原生/hook 的普通 fn 解析与压帧（方法派发链末的
        // 原始 fn 落点复用；统一走逻辑 TypeSheet 虚派发 ResolveDispatch）
        internal static void InvokeResolved(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            var function = context.ResolveDispatch(methodSymbol,
                args.Count > 0 ? args[0] : null);
            if (function == null)
            {
                throw new VmException("找不到 fn 定义：" + methodSymbol);
            }
            // .any 胖值 receiver 的实例方法派发：方法体在负载上执行
            //（.this = payload；与 VmWrapperDispatch 的 receiver 解包同口径）。
            // 仅限实例方法符号——全局 fn 的首个实参可能是合法的 .any 值
            if (args.Count > 0 && args[0] is VmAny boxed
                && BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out var receiverStatic, out _, out _)
                && !receiverStatic)
            {
                var unboxed = new VmValue[args.Count];
                unboxed[0] = boxed.Payload;
                for (var i = 1; i < args.Count; i++)
                {
                    unboxed[i] = args[i];
                }
                args = unboxed;
            }
            if (context.IsAsyncMethod(function.Symbol) || context.IsAsyncMethod(methodSymbol))
            {
                EagerSpawn(coroutine, function, args, resultSlot);
                return;
            }
            coroutine.PushFrame(function, args, resultSlot);
        }

        // §15.5 fn(..super)：解析为直接基类的原始实现并压帧——super 非虚、
        // 绕过 wrapper 派发链、不再二次派发；实参（$.this + 隐藏泛型 + 普通
        // 实参）原样压入。仅 override / init fn 体内合法（frontend 保证）。
        private static void ResolveSuper(VmContext context, VmCoroutine coroutine,
            IReadOnlyList<VmValue> args, string? resultSlot,
            IReadOnlyList<string>? argumentStaticTypes)
        {
            var current = coroutine.CurrentFrame.Function;
            if (!BilVerificationContext.TryParseMethodSymbol(current.Symbol,
                    out var owner, out _, out _, out _)
                || context.FindType(owner) is not { } ownerDeclaration
                || ownerDeclaration.ExtendsType is not { } baseRef)
            {
                throw new VmException("fn(..super) 所在 fn 无直接基类：" + current.Symbol);
            }
            if (context.IsInitMethod(current.Symbol))
            {
                ResolveSuperInit(context, coroutine, baseRef, args, argumentStaticTypes);
                return;
            }
            var baseSheet = context.SheetOf(baseRef)
                ?? throw new VmException("基类声明缺失，无法解析 super：" + baseRef);
            var signatureKey = VmTypeSheetBuilder.SignatureKeyOf(current.Symbol);
            var offset = -1;
            for (var i = 0; i < baseSheet.Slots.Count; i++)
            {
                if (baseSheet.Slots[i].SignatureKey == signatureKey)
                {
                    offset = i;
                    break;
                }
            }
            if (offset < 0)
            {
                var fallback = FindUniqueBaseFunction(context, baseRef, current, args)
                    ?? throw new VmException("super 未在直接基类命中同名方法："
                        + current.Symbol);
                PushSuperFrame(context, coroutine, fallback, args, resultSlot);
                return;
            }
            // 基类该槽无实现（抽象声明）也是错误：super 必须落到具体实现
            var impl = baseSheet.Slots[offset].ImplSymbol
                ?? VmTypeSheetBuilder.FindCompatibleImpl(baseSheet.Slots, current.Symbol)
                ?? throw new VmException("直接基类该签名无实现，无法 super：" + current.Symbol);
            // super 目标天然不会是 native 无体符号：sheet 槽 ImplSymbol 仅
            // 在 fn 体存在时落地（VmTypeSheetBuilder.Build），内建类型的
            // 默认实现（Any/Object$toString 合成体）不进任何 sheet——
            // 此处 FindFunction 的 null 分支只防御异常模块形态，无需 hook 路由
            var target = context.FindFunction(impl)
                ?? throw new VmException("super 目标实现缺少 fn 定义：" + impl);
            PushSuperFrame(context, coroutine, target, args, resultSlot);
        }

        private static void PushSuperFrame(VmContext context, VmCoroutine coroutine,
            BilFunction target, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (context.IsAsyncMethod(target.Symbol))
            {
                EagerSpawn(coroutine, target, args, resultSlot);
                return;
            }
            coroutine.PushFrame(target, args, resultSlot);
        }

        // super init（§9.2.2）：候选仅为直接基类 init 重载；实参 ABI 已含
        // $.this + 隐藏泛型前缀（§15.5），剥除后按声明参数比对；init 必为
        // noret（resultSlot 丢弃），$.this 透传（语义同 invoke.noret）
        private static void ResolveSuperInit(VmContext context, VmCoroutine coroutine,
            string baseRef, IReadOnlyList<VmValue> args,
            IReadOnlyList<string>? argumentStaticTypes)
        {
            var baseDeclaration = context.FindType(baseRef)
                ?? throw new VmException("基类声明缺失，无法解析 super init：" + baseRef);
            // 构造泛型基类（Entry : Pair<String, i32> 的 super(k, v)）：定义级
            // init 签名的 .generic 占位按 extends 实参代入后再比对——与
            // VmContext.TryFindInit 同一代入机制
            var substitution = VmTypeSheetBuilder.BuildSubstitution(baseRef, baseDeclaration);
            foreach (var member in baseDeclaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || !context.IsInitMethod(simple.Symbol))
                {
                    continue;
                }
                var function = context.FindFunction(simple.Symbol);
                if (function == null
                    || !BilVerificationContext.TryParseMethodSymbol(simple.Symbol,
                        out _, out _, out var parameters, out _))
                {
                    continue;
                }
                if (substitution != null)
                {
                    parameters = parameters.ConvertAll(p =>
                        (p.Name, VmTypeSheetBuilder.SubstituteGenericArguments(
                            p.TypeRef, substitution)));
                }
                if (!SuperInitArgsMatch(function, parameters, args, argumentStaticTypes))
                {
                    continue;
                }
                coroutine.PushFrame(function, args, resultSlot: null);
                return;
            }
            throw new VmException("super(...) 实参不匹配直接基类任何 init：" + baseRef);
        }

        // super init 实参匹配：args = [.this, 隐藏泛型..., 声明参数...]，
        // 隐藏泛型个数取自 fn .args 的 .generic.* 条目。比对 cast 后静态
        // 类型与形参 TypesEqual（§9.2.2 / §15.5：编译期已选定唯一目标，
        // 此处是验证不是 ranking；引用类型 upcast 不改对象头 typeid，故
        // 不得用运行期 TypeRef）。缺静态类型时回落值 TypeRef（同口径）。
        private static bool SuperInitArgsMatch(BilFunction function,
            List<(string Name, string TypeRef)> parameters, IReadOnlyList<VmValue> args,
            IReadOnlyList<string>? argumentStaticTypes)
        {
            var genericHidden = 0;
            foreach (var arg in function.Args)
            {
                if (arg.Name.StartsWith(".generic.", StringComparison.Ordinal))
                {
                    genericHidden++;
                }
            }
            // 类级 .generic.* 可由 PushFrame 从 .this 注入，super(...) 允许省略
            var minCount = 1 + parameters.Count;
            var maxCount = 1 + genericHidden + parameters.Count;
            if (args.Count < minCount || args.Count > maxCount)
            {
                return false;
            }
            var passedHidden = args.Count - minCount;
            for (var i = 0; i < parameters.Count; i++)
            {
                var index = 1 + passedHidden + i;
                var actual = argumentStaticTypes != null && index < argumentStaticTypes.Count
                    ? argumentStaticTypes[index]
                    : args[index].TypeRef;
                if (!VmContext.TypesEqual(actual, parameters[i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        // 基类存在同名不同签名重载时的精确选择需要调用点静态类型，BIL 不
        // 泄露（§15.5 "BIL 不泄露 base canonical 名"）——VM 以同签名优先、
        // 唯一按实参个数匹配兜底
        private static BilFunction? FindUniqueBaseFunction(VmContext context, string baseRef,
            BilFunction current, IReadOnlyList<VmValue> args)
        {
            var baseDeclaration = context.FindType(baseRef);
            if (baseDeclaration == null)
            {
                return null;
            }
            var name = VmContext.MethodNameOf(current.Symbol);
            BilFunction? unique = null;
            foreach (var member in baseDeclaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple
                    || simple.Kind != BilMemberKind.Method
                    || context.IsInitMethod(simple.Symbol)
                    || VmContext.MethodNameOf(simple.Symbol) != name)
                {
                    continue;
                }
                var function = context.FindFunction(simple.Symbol);
                if (function == null || CountFrameParams(function) != args.Count)
                {
                    continue;
                }
                if (unique != null)
                {
                    return null;
                }
                unique = function;
            }
            return unique;
        }

        private static int CountFrameParams(BilFunction function)
        {
            var count = 0;
            foreach (var arg in function.Args)
            {
                if (arg.Name != ".return")
                {
                    count++;
                }
            }
            return count;
        }

        // call??? 符号判定（core::Any$call??? 前缀，签名段之前）
        private static bool IsCallWildcardSymbol(string methodSymbol)
        {
            var paren = methodSymbol.IndexOf('(');
            var head = paren < 0 ? methodSymbol : methodSymbol.Substring(0, paren);
            return head == "core::Any$call???";
        }

        // RUNTIME §18.1：求实参（已完成）→ 建 Coroutine/Task → 绑定 Executor
        // → 转 Runnable 发布 → 返回 Task。不以全局锁串行化。
        private static void EagerSpawn(VmCoroutine caller,
            BilFunction function, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            var child = caller.BoundExecutor.Spawn(function, args);
            caller.BoundExecutor.Publish(child);
            if (resultSlot != null)
            {
                caller.WriteVar(resultSlot, child.Task);
            }
        }
    }
}
