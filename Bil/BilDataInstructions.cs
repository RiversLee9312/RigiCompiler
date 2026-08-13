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
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Getter,
                    coroutine.CurrentFrame.Function.Symbol, out var getter))
            {
                BilInvokeExecution.Invoke(context, coroutine, getter,
                    new[] { objectVar }, target.Name);
                return;
            }
            var instance = coroutine.ReadVar(objectVar.Name);
            if (instance is VmArray array && fieldSymbol == VmArray.LengthFieldSymbol)
            {
                coroutine.WriteVar(target.Name, new VmI32(array.Length));
                return;
            }
            coroutine.WriteVar(target.Name, ReadInstanceField(instance, fieldSymbol).Copy());
        }

        internal static void SetField(VmContext context, VmCoroutine coroutine,
            BilVariableOperand source, BilVariableOperand objectVar, string fieldSymbol)
        {
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Setter,
                    coroutine.CurrentFrame.Function.Symbol, out var setter))
            {
                BilInvokeExecution.Invoke(context, coroutine, setter,
                    new[] { objectVar, source }, resultSlot: null);
                return;
            }
            WriteInstanceField(coroutine.ReadVar(objectVar.Name), fieldSymbol,
                coroutine.ReadVar(source.Name).Copy());
        }

        internal static void GetFieldStatic(VmContext context, VmCoroutine coroutine,
            BilVariableOperand target, string fieldSymbol)
        {
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Getter,
                    coroutine.CurrentFrame.Function.Symbol, out var getter))
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
            if (context.TryFindAccessor(fieldSymbol, BilAccessorKind.Setter,
                    coroutine.CurrentFrame.Function.Symbol, out var setter))
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
            if (collection is VmArray array)
            {
                coroutine.WriteVar(target.Name,
                    array.GetAt(VmContext.RequireIndex(coroutine.ReadVar(indexVar.Name))).Copy());
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
            if (collection is VmArray array)
            {
                array.SetAt(VmContext.RequireIndex(coroutine.ReadVar(indexVar.Name)),
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
            var symbol = context.FindCallOperator(receiver.TypeRef, values);
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
            var instance = context.AllocateObject(typeRef);
            coroutine.WriteVar(target.Name, instance);
            var initArgs = ReadArgs(coroutine, initArguments);
            if (!context.TryFindInit(typeRef, initArgs, out var initSymbol)
                && initArguments.Count > 0)
            {
                throw new VmException("new 实参不匹配任何 init：" + typeRef);
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
            if (!context.TryFindInit(typeRef, payload, out initSymbol)
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
            if (!context.TryFindInit(wrapperType, initArgs, out var initSymbol))
            {
                if (arguments.Count > 0)
                {
                    throw new VmException("wrapper init 实参不匹配：" + wrapperType);
                }
                return;
            }
            var values = new List<VmValue> { wrapper };
            values.AddRange(initArgs);
            BilInvokeExecution.InvokeValues(context, coroutine, initSymbol, values,
                resultSlot: null);
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
            InvokeValues(context, coroutine, methodSymbol, args, resultSlot);
        }

        internal static void InvokeValues(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (context.TryResolveNative(methodSymbol, out var library, out var nativeSymbol))
            {
                var result = context.Hooks.Invoke(context, library, nativeSymbol, args);
                if (resultSlot != null)
                {
                    coroutine.WriteVar(resultSlot, result);
                }
                return;
            }
            var function = context.FindVirtualFunction(methodSymbol, args);
            if (function == null)
            {
                throw new VmException("找不到 fn 定义：" + methodSymbol);
            }
            if (context.IsAsyncMethod(function.Symbol) || context.IsAsyncMethod(methodSymbol))
            {
                EagerSpawn(coroutine, function, args, resultSlot);
                return;
            }
            coroutine.PushFrame(function, args, resultSlot);
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
