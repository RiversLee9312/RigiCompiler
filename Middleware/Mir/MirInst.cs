using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
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

    // invoke.indirect / invoke.indirect.noret（BIL §15.3 callable 协议）：
    // 对 CallTarget 虚调用其 $$call；实参不含 receiver；CallTargetType
    // 为直译时从局部类型表附上的静态类型（参照 MirGetArray.CollectionType）
    public sealed class MirInvokeIndirect : MirInst
    {
        public MirOperand CallTarget { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        // null = invoke.indirect.noret（无结果槽）
        public string? Result { get; }
        public MirType CallTargetType { get; }

        internal MirInvokeIndirect(MirOperand callTarget, IReadOnlyList<MirOperand> args,
            string? result, MirType callTargetType)
        {
            CallTarget = callTarget;
            Args = args;
            Result = result;
            CallTargetType = callTargetType;
        }
    }

    // invoke / invoke.noret：目标为驻留成员符号（含 native 声明；
    // 派发形态——native 面/直接调用/虚调用/interface 调用——由
    // Binding.BindCall 回答）
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

    // invoke fn(..super)（MW4 批 2）：MIR 构建期已解析为直接基类的实现
    // 符号（super init 按实参静态类型精确匹配重载；super 方法按签名键
    // 沿基类链命中）；发射恒为直接调用，不经 BindCall/虚派发
    public sealed class MirSuperCall : MirInst
    {
        public MwMemberSymbol Target { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public string? Result { get; }

        internal MirSuperCall(MwMemberSymbol target, IReadOnlyList<MirOperand> args, string? result)
        {
            Target = target;
            Args = args;
            Result = result;
        }
    }

    // new type(T)（MW4 批 2，class）：rigi_alloc(TypeSheet) → 可选
    // ..init.wrapper（字段初始值缝合）→ init 调用 → 胖引用结果
    public sealed class MirNewObject : MirInst
    {
        public MwTypeSymbol Type { get; }
        public MwMemberSymbol? InitWrapper { get; }
        public MwMemberSymbol Init { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public string Target { get; }

        internal MirNewObject(MwTypeSymbol type, MwMemberSymbol? initWrapper,
            MwMemberSymbol init, IReadOnlyList<MirOperand> args, string target)
        {
            Type = type;
            InitWrapper = initWrapper;
            Init = init;
            Args = args;
            Target = target;
        }
    }

    // new type(V)（MW4 批 3，struct/enum）：目标局部的内联 alloca 槽物化
    //（零初始化对齐 VM ZeroOf）→ 可选 ..init.wrapper → init 直调
    //（.this 传槽地址，原地生效）；不装箱、不上堆
    public sealed class MirNewValue : MirInst
    {
        public MwTypeSymbol Type { get; }
        public MwMemberSymbol? InitWrapper { get; }
        public MwMemberSymbol Init { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public string Target { get; }

        internal MirNewValue(MwTypeSymbol type, MwMemberSymbol? initWrapper,
            MwMemberSymbol init, IReadOnlyList<MirOperand> args, string target)
        {
            Type = type;
            InitWrapper = initWrapper;
            Init = init;
            Args = args;
            Target = target;
        }
    }

    // new.case（MW4 批 3，enum）：槽内偏移 0 写 u32 判别常量 → init 直调
    //（实参 = enum init 参数序；enum 无零值不写零，判别+init 覆盖）
    public sealed class MirNewCase : MirInst
    {
        public MwCaseSymbol Case { get; }
        public MwMemberSymbol Init { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public string Target { get; }

        internal MirNewCase(MwCaseSymbol caseSymbol, MwMemberSymbol init,
            IReadOnlyList<MirOperand> args, string target)
        {
            Case = caseSymbol;
            Init = init;
            Args = args;
            Target = target;
        }
    }

    // type.is / type.supers / type.with（MW5 c3）：经 rigi_type_* helper
    public enum MirTypeCheckKind
    {
        Is,
        Supers,
        With,
    }

    public sealed class MirTypeCheck : MirInst
    {
        public MirTypeCheckKind Kind { get; }
        public MirOperand Value { get; }
        // 静态目标 canonical；indirect / 泛型占位时为 null
        public string? TargetTypeRef { get; }
        // .indirect 或泛型占位：typeid 局部（TypeSheet*）
        public MirOperand? TargetTypeId { get; }
        public string Target { get; }
        public bool IsIndirect => TargetTypeId != null;

        internal MirTypeCheck(MirTypeCheckKind kind, MirOperand value,
            string? targetTypeRef, MirOperand? targetTypeId, string target)
        {
            Kind = kind;
            Value = value;
            TargetTypeRef = targetTypeRef;
            TargetTypeId = targetTypeId;
            Target = target;
        }
    }

    // type.is.case（MW4 批 3）：读隐藏判别 u32（偏移 0）+ icmp eq 判别
    // 常量；非子类型检查、不比较 payload
    public sealed class MirIsCase : MirInst
    {
        public MwCaseSymbol Case { get; }
        public MirOperand Value { get; }
        public string Target { get; }

        internal MirIsCase(MwCaseSymbol caseSymbol, MirOperand value, string target)
        {
            Case = caseSymbol;
            Value = value;
            Target = target;
        }
    }

    // get.field 直译（含 computed 与 accessor 体内 backing；访问器改写
    // 归 AccessorLoweringPass；字段符号自足——宿主段/类型段在符号文本内）
    public sealed class MirGetField : MirInst
    {
        public MirOperand Object { get; }
        public string FieldSymbol { get; }
        public string Target { get; }

        internal MirGetField(MirOperand objectOperand, string fieldSymbol, string target)
        {
            Object = objectOperand;
            FieldSymbol = fieldSymbol;
            Target = target;
        }
    }

    // get.field.static（MW4 批 4）：模块级静态槽读取（canonical 字段符号
    // 键；初值由 ..globals.init 在 main 前经 rigi_entry stub 执行）
    public sealed class MirGetStatic : MirInst
    {
        public string FieldSymbol { get; }
        public string Target { get; }

        internal MirGetStatic(string fieldSymbol, string target)
        {
            FieldSymbol = fieldSymbol;
            Target = target;
        }
    }

    // set.field.static（MW4 批 4）：模块级静态槽写入（ARC 注入属 MW7，
    // 本批不注入）
    public sealed class MirSetStatic : MirInst
    {
        public MirOperand Source { get; }
        public string FieldSymbol { get; }

        internal MirSetStatic(MirOperand source, string fieldSymbol)
        {
            Source = source;
            FieldSymbol = fieldSymbol;
        }
    }

    // set.field 直译（含 computed 与 accessor 体内 backing；访问器改写
    // 归 AccessorLoweringPass；ARC 注入属 MW7）
    public sealed class MirSetField : MirInst
    {
        public MirOperand Source { get; }
        public MirOperand Object { get; }
        public string FieldSymbol { get; }

        internal MirSetField(MirOperand source, MirOperand objectOperand, string fieldSymbol)
        {
            Source = source;
            Object = objectOperand;
            FieldSymbol = fieldSymbol;
        }
    }

    // get.array 直译（内建与用户类型同形态；用户类型由
    // IndexOperatorLoweringPass 降为 $$getAtIndex 调用）
    public sealed class MirGetArray : MirInst
    {
        public MirOperand Collection { get; }
        public MirOperand Index { get; }
        public MirType CollectionType { get; }
        public string Target { get; }

        internal MirGetArray(MirOperand collection, MirOperand index,
            MirType collectionType, string target)
        {
            Collection = collection;
            Index = index;
            CollectionType = collectionType;
            Target = target;
        }
    }

    // set.array 直译（内建与用户类型同形态；用户类型由
    // IndexOperatorLoweringPass 降为 $$setAtIndex 调用）
    public sealed class MirSetArray : MirInst
    {
        public MirOperand Collection { get; }
        public MirOperand Index { get; }
        public MirOperand Element { get; }
        public MirType CollectionType { get; }

        internal MirSetArray(MirOperand collection, MirOperand index, MirOperand element,
            MirType collectionType)
        {
            Collection = collection;
            Index = index;
            Element = element;
            CollectionType = collectionType;
        }
    }

    // new type(.array<T>) [elems...]：alloc + 按序写入元素
    public sealed class MirNewArray : MirInst
    {
        public MirType Type { get; }
        public IReadOnlyList<MirOperand> Elements { get; }
        public string Target { get; }

        internal MirNewArray(MirType type, IReadOnlyList<MirOperand> elements, string target)
        {
            Type = type;
            Elements = elements;
            Target = target;
        }
    }

    // getid.type type(T)：物化 TypeSheet*（.typeid / 泛型 hidden 实参）
    public sealed class MirGetTypeId : MirInst
    {
        public string TypeRef { get; }
        public string Target { get; }

        internal MirGetTypeId(string typeRef, string target)
        {
            TypeRef = typeRef;
            Target = target;
        }
    }

    // Nullable\<T\> 装箱（值类型 T → 胖引用；引用 T 为恒等）
    public sealed class MirWrapNullable : MirInst
    {
        public MirOperand Source { get; }
        public MirType InnerType { get; }
        public string Target { get; }

        internal MirWrapNullable(MirOperand source, MirType innerType, string target)
        {
            Source = source;
            InnerType = innerType;
            Target = target;
        }
    }

    // Nullable\<T\> 拆箱（if? 非空支；引用 T 为恒等）
    public sealed class MirUnwrapNullable : MirInst
    {
        public MirOperand Source { get; }
        public MirType InnerType { get; }
        public string Target { get; }

        internal MirUnwrapNullable(MirOperand source, MirType innerType, string target)
        {
            Source = source;
            InnerType = innerType;
            Target = target;
        }
    }

    // 值类型 → .any/.object 装箱（RUNTIME §2/§4：tag0 内联 / tag1 裸块）
    public sealed class MirBoxAny : MirInst
    {
        public MirOperand Source { get; }
        public string Target { get; }

        internal MirBoxAny(MirOperand source, string target)
        {
            Source = source;
            Target = target;
        }
    }

    // .any/.object → 具体值类型拆箱（目标类型由结果局部携带）
    public sealed class MirUnboxAny : MirInst
    {
        public MirOperand Source { get; }
        public string Target { get; }

        internal MirUnboxAny(MirOperand source, string target)
        {
            Source = source;
            Target = target;
        }
    }

    // RcInjection 产物：就地 acquire（+1）/ release（-1）具名槽；类别由发射期
    // ClassifySlot 决定（决策归 pass，机制归 ArcEmitter）
    public sealed class MirAcquireSlot : MirInst
    {
        public string Local { get; }

        internal MirAcquireSlot(string local)
        {
            Local = local;
        }
    }

    public sealed class MirReleaseSlot : MirInst
    {
        public string Local { get; }

        internal MirReleaseSlot(string local)
        {
            Local = local;
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
