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
        // MW9b-G：整数除零守卫的异常边目标（同 MirInvokeIndirect.ExcTarget
        // 口径）；非除法运算不抛，恒等占位随 RcInjection 一并解析
        public MirBlock? ExcTarget { get; }

        internal MirBinaryIntrinsic(BilBinaryOp op, MirOperand left, MirOperand right,
            MirType leftType, MirType rightType, MirType resultType, string target,
            MirBlock? excTarget = null)
        {
            Op = op;
            Left = left;
            Right = right;
            LeftType = leftType;
            RightType = rightType;
            ResultType = resultType;
            Target = target;
            ExcTarget = excTarget;
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

    // G4：泛型占位操作数的二元运算（T extends Bound 内 a + b 族）——
    // 操作数静态类型含 .generic< 占位，编译期不知 operator 目标；发射
    // 运行期按实际 typeid 派发（VM ExecuteBinary 同口径：内建标量
    // 求值优先，否则按左操作数实际类型沿派生链找最具体 operator
    // 实现，全部落空抛 core.NoSuchMethodException）。!= 调 equals
    // 取反、</<=/>/>= 调 compareTo 按 case 判别（SYNTAX §13.2）
    public sealed class MirGenericBinaryOp : MirInst
    {
        public BilBinaryOp Op { get; }
        public MirOperand Left { get; }
        public MirOperand Right { get; }
        public MirType LeftType { get; }
        public MirType RightType { get; }
        public MirType ResultType { get; }
        public string Target { get; }
        // 异常边（派发落空/除零等可抛点；同 MirBinaryIntrinsic 口径）
        public MirBlock? ExcTarget { get; }

        internal MirGenericBinaryOp(BilBinaryOp op, MirOperand left, MirOperand right,
            MirType leftType, MirType rightType, MirType resultType, string target,
            MirBlock? excTarget = null)
        {
            Op = op;
            Left = left;
            Right = right;
            LeftType = leftType;
            RightType = rightType;
            ResultType = resultType;
            Target = target;
            ExcTarget = excTarget;
        }
    }

    // G4：泛型占位操作数的一元运算（-a / not a / !a 族，同上运行期派发）
    public sealed class MirGenericUnaryOp : MirInst
    {
        public BilUnaryOp Op { get; }
        public MirOperand Operand { get; }
        public MirType OperandType { get; }
        public MirType ResultType { get; }
        public string Target { get; }
        public MirBlock? ExcTarget { get; }

        internal MirGenericUnaryOp(BilUnaryOp op, MirOperand operand,
            MirType operandType, MirType resultType, string target,
            MirBlock? excTarget = null)
        {
            Op = op;
            Operand = operand;
            OperandType = operandType;
            ResultType = resultType;
            Target = target;
            ExcTarget = excTarget;
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
        // MW9a：异常边目标（try 派发垫/逃逸垫，入口恒为 MirTakePending）；
        // null = 未解析（留待 C 棒填 propagate 垫）
        public MirBlock? ExcTarget { get; }

        internal MirInvokeIndirect(MirOperand callTarget, IReadOnlyList<MirOperand> args,
            string? result, MirType callTargetType, MirBlock? excTarget = null)
        {
            CallTarget = callTarget;
            Args = args;
            Result = result;
            CallTargetType = callTargetType;
            ExcTarget = excTarget;
        }
    }

    // invoke / invoke.noret：目标为驻留成员符号（含 native 声明；
    // 派发形态——native 面/直接调用/虚调用/interface 调用——由
    // Binding.BindCall 回答）。OperatorDispatch = 遗1 intrinsic 运算
    // 符直译形态（Binding.BindOperatorCall 回答派发：class 虚/interface
    // iMap/值类型直调；对齐 VM 按左操作数实际类型查 operator 的语义）
    public sealed class MirCall : MirInst
    {
        public MwMemberSymbol Target { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        // null = invoke.noret（无结果槽）
        public string? Result { get; }
        // MW9a：异常边目标（同 MirInvokeIndirect.ExcTarget 口径）
        public MirBlock? ExcTarget { get; }
        public bool OperatorDispatch { get; }
        // G1：泛型值类型宿主方法的接收者构造形态 canonical（类级 typeid
        // 直传代入用；null = 非值类型泛型宿主/静态不可知，发射侧再按
        // 接收者静态类型与 .generic.* 局部兜底）
        public string? HostConstructedRef { get; }

        internal MirCall(MwMemberSymbol target, IReadOnlyList<MirOperand> args, string? result,
            MirBlock? excTarget = null, bool operatorDispatch = false,
            string? hostConstructedRef = null)
        {
            Target = target;
            Args = args;
            Result = result;
            ExcTarget = excTarget;
            OperatorDispatch = operatorDispatch;
            HostConstructedRef = hostConstructedRef;
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
        // MW9a：异常边目标（同 MirInvokeIndirect.ExcTarget 口径）
        public MirBlock? ExcTarget { get; }

        internal MirSuperCall(MwMemberSymbol target, IReadOnlyList<MirOperand> args, string? result,
            MirBlock? excTarget = null)
        {
            Target = target;
            Args = args;
            Result = result;
            ExcTarget = excTarget;
        }
    }

    // new type(T)（MW4 批 2，class）：rigi_alloc(TypeSheet) → 可选
    // ..init.wrapper（字段初始值缝合）→ init 调用 → 胖引用结果。
    // MW10 刀5：new.wrapped（§14.4.1 有参 ..init.wrapper 构造）经
    // WrapperArgs 携带 wrapper 实参（空 = 无参 ..init.wrapper 或缺省）。
    // L7：全链无 init 声明（含基类仅有参 init 的隐式默认构造形态）+
    // 零实参 new —— Init = null，仅 alloc + ..init.wrapper 缝合（VM
    // TryFindInit「无 init 声明 + 零实参仍构造」同口径）
    public sealed class MirNewObject : MirInst
    {
        public MwTypeSymbol Type { get; }
        public MwMemberSymbol? InitWrapper { get; }
        public MwMemberSymbol? Init { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public IReadOnlyList<MirOperand> WrapperArgs { get; }
        public string Target { get; }
        // MW10 刀5：构造异常边（init/..init.wrapper 内抛出 → 释放新建
        // 实例后沿边传播，VM New 同步抛出同语义）。null = 历史形态
        //（不带边直调，pending 推迟到下一检查点）；仅 singleton get fn
        // 的合成构造点带边（RcInjection 不改写本字段——带边即已定）
        public MirBlock? ExcTarget { get; }

        internal MirNewObject(MwTypeSymbol type, MwMemberSymbol? initWrapper,
            MwMemberSymbol? init, IReadOnlyList<MirOperand> args, string target,
            IReadOnlyList<MirOperand>? wrapperArgs = null, MirBlock? excTarget = null)
        {
            Type = type;
            InitWrapper = initWrapper;
            Init = init;
            Args = args;
            WrapperArgs = wrapperArgs ?? (IReadOnlyList<MirOperand>)System.Array.Empty<MirOperand>();
            Target = target;
            ExcTarget = excTarget;
        }
    }

    // new.indirect（MW8b）：运行期 TYPEID 选类型，vtable 槽 0 分发器
    // 匹配 init。方法级泛型 init 语言层不存在；分发器合成处若见到则
    // 受控拒绝。struct / 标量零值路径与 VM 有语义差，暂不实现。
    public sealed class MirNewIndirect : MirInst
    {
        public MirOperand TypeId { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public string Target { get; }
        // MW9b-G：无匹配 init 抛 NoSuchMethodException 的异常边目标
        //（同 MirInvokeIndirect.ExcTarget 口径）
        public MirBlock? ExcTarget { get; }

        internal MirNewIndirect(MirOperand typeId, IReadOnlyList<MirOperand> args,
            string target, MirBlock? excTarget = null)
        {
            TypeId = typeId;
            Args = args;
            Target = target;
            ExcTarget = excTarget;
        }
    }

    // new type(V)（MW4 批 3，struct/enum）：目标局部的内联 alloca 槽物化
    //（零初始化对齐 VM ZeroOf）→ 可选 ..init.wrapper → init 直调
    //（.this 传槽地址，原地生效）；不装箱、不上堆
    public sealed class MirNewValue : MirInst
    {
        public MwTypeSymbol Type { get; }
        public MwMemberSymbol? InitWrapper { get; }
        // L7：无 init 声明 + 零实参时可为 null（同 MirNewObject）
        public MwMemberSymbol? Init { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        // MW10 刀5：new.wrapped 的 wrapper 实参（同 MirNewObject）
        public IReadOnlyList<MirOperand> WrapperArgs { get; }
        public string Target { get; }
        // R3：构造异常边（同 R2-d MirNewObject 口径）：init/..init.wrapper
        // 内同步抛出沿本 fn 异常边走（对齐 VM 帧展开——此前 null 时
        // pending 推迟到下一检查点，同 fn try 捕获形态 VM/native 错位）。
        // 值类型构造原地落目标槽，无新建堆对象需释放
        public MirBlock? ExcTarget { get; }

        internal MirNewValue(MwTypeSymbol type, MwMemberSymbol? initWrapper,
            MwMemberSymbol? init, IReadOnlyList<MirOperand> args, string target,
            IReadOnlyList<MirOperand>? wrapperArgs = null, MirBlock? excTarget = null)
        {
            Type = type;
            InitWrapper = initWrapper;
            Init = init;
            Args = args;
            WrapperArgs = wrapperArgs ?? (IReadOnlyList<MirOperand>)System.Array.Empty<MirOperand>();
            Target = target;
            ExcTarget = excTarget;
        }
    }

    // new.case（MW4 批 3，enum）：槽内偏移 0 写 u32 判别常量 → init 直调
    //（实参 = enum init 参数序；enum 无零值不写零，判别+init 覆盖）。
    // 遗1：无 init 声明 + 零实参的 enum（stdlib ComparisonResult 形态）
    // Init = null——仅写判别（VM NewCase 同口径：TryFindInit 未命中且
    // 零实参时不调 init；无 init 有实参 VM 运行期抛错，native 编译期
    // 拒绝）。
    // L1：new.wrapped.case（§14.4.2 有参 ..init.wrapper 的 enum case
    // 构造）经 InitWrapper/WrapperArgs 携带 wrapper 实参（null/空 =
    // 直译 new.case 形态）；发射序 = 判别 → ..init.wrapper → init
    //（VM PushConstructorTail LIFO：wrapper 帧后压先执行，同口径）
    public sealed class MirNewCase : MirInst
    {
        public MwCaseSymbol Case { get; }
        public MwMemberSymbol? Init { get; }
        public MwMemberSymbol? InitWrapper { get; }
        public IReadOnlyList<MirOperand> WrapperArgs { get; }
        public IReadOnlyList<MirOperand> Args { get; }
        public string Target { get; }

        internal MirNewCase(MwCaseSymbol caseSymbol, MwMemberSymbol? init,
            IReadOnlyList<MirOperand> args, string target,
            MwMemberSymbol? initWrapper = null, IReadOnlyList<MirOperand>? wrapperArgs = null)
        {
            Case = caseSymbol;
            Init = init;
            InitWrapper = initWrapper;
            WrapperArgs = wrapperArgs ?? (IReadOnlyList<MirOperand>)System.Array.Empty<MirOperand>();
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
        // R2-c：操作数是 .typeid 原值（TypeSheet*），判定其描述的类
        // 型 is-a 目标（不包 Type<X> 元类型视图；仅 CoroutineSplit
        // 的 new.indirect tainted init 臂使用，源级无对应形态）
        IsTypeId,
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
        // MW9b-G：泛型占位字段读取的拆箱不符守卫抛 CastException 的异常
        // 边目标（同 MirInvokeIndirect.ExcTarget 口径）
        public MirBlock? ExcTarget { get; }

        internal MirGetField(MirOperand objectOperand, string fieldSymbol, string target,
            MirBlock? excTarget = null)
        {
            Object = objectOperand;
            FieldSymbol = fieldSymbol;
            Target = target;
            ExcTarget = excTarget;
        }
    }

    // get.wrapper：从宿主隐藏槽拷贝 wrapper 值（Entity 应用）
    public sealed class MirGetWrapper : MirInst
    {
        public MirOperand Host { get; }
        public string WrapperType { get; }
        public string Target { get; }

        internal MirGetWrapper(MirOperand host, string wrapperType, string target)
        {
            Host = host;
            WrapperType = wrapperType;
            Target = target;
        }
    }

    // get.wrapper.field：从字段-Value 隐藏槽拷贝 wrapper 值
    public sealed class MirGetWrapperField : MirInst
    {
        public MirOperand Host { get; }
        public string FieldSymbol { get; }
        public string WrapperType { get; }
        public string Target { get; }

        internal MirGetWrapperField(MirOperand host, string fieldSymbol, string wrapperType,
            string target)
        {
            Host = host;
            FieldSymbol = fieldSymbol;
            WrapperType = wrapperType;
            Target = target;
        }
    }

    // get.wrapper.addr：proxy 环 receiver 取址（MW10 刀3c §14.5 原地访问
    // 修订）——目标局部的槽重定向为宿主 Entity 隐藏槽地址，不产生值拷贝：
    // 环 fn 的值类型 .this 本就是槽地址约定，调用点直传隐藏槽 GEP 地址，
    // 环内对 wrapper 自身字段的写入原地生效、跨调用持久。仅 proxy 烘焙环
    // receiver 使用；obj:W 方法调用的值拷贝 receiver 语义不动
    public sealed class MirGetWrapperAddr : MirInst
    {
        public MirOperand Host { get; }
        public string WrapperType { get; }
        public string Target { get; }

        internal MirGetWrapperAddr(MirOperand host, string wrapperType, string target)
        {
            Host = host;
            WrapperType = wrapperType;
            Target = target;
        }
    }

    // get.wrapper.field.addr：字段-Value 隐藏槽同口径取址（proxy 环
    // receiver 专用，语义同 MirGetWrapperAddr）
    public sealed class MirGetWrapperFieldAddr : MirInst
    {
        public MirOperand Host { get; }
        public string FieldSymbol { get; }
        public string WrapperType { get; }
        public string Target { get; }

        internal MirGetWrapperFieldAddr(MirOperand host, string fieldSymbol, string wrapperType,
            string target)
        {
            Host = host;
            FieldSymbol = fieldSymbol;
            WrapperType = wrapperType;
            Target = target;
        }
    }

    // get.wrapper.method.addr：Method wrapper 隐藏槽同口径取址（MW10 刀6
    // method 链环 receiver / trampoline 专用，语义同 MirGetWrapperAddr；
    // 槽键 = 实现槽方法符号 + wrapper 类型）
    public sealed class MirGetWrapperMethodAddr : MirInst
    {
        public MirOperand Host { get; }
        public string MethodSymbol { get; }
        public string WrapperType { get; }
        public string Target { get; }

        internal MirGetWrapperMethodAddr(MirOperand host, string methodSymbol, string wrapperType,
            string target)
        {
            Host = host;
            MethodSymbol = methodSymbol;
            WrapperType = wrapperType;
            Target = target;
        }
    }

    // set.wrapper.field 链元素：field(F) 或 wrapper(W)
    public abstract class MirWrapperChainElem
    {
    }

    public sealed class MirWrapperChainField : MirWrapperChainElem
    {
        public string FieldSymbol { get; }

        internal MirWrapperChainField(string fieldSymbol)
        {
            FieldSymbol = fieldSymbol;
        }
    }

    public sealed class MirWrapperChainWrapper : MirWrapperChainElem
    {
        public string WrapperType { get; }

        internal MirWrapperChainWrapper(string wrapperType)
        {
            WrapperType = wrapperType;
        }
    }

    // set.wrapper.field：沿链定位隐藏槽后写 wrapper 内字段
    public sealed class MirSetWrapperField : MirInst
    {
        public MirOperand Source { get; }
        public MirOperand Host { get; }
        public IReadOnlyList<MirWrapperChainElem> Chain { get; }
        public string InnerField { get; }

        internal MirSetWrapperField(MirOperand source, MirOperand host,
            IReadOnlyList<MirWrapperChainElem> chain, string innerField)
        {
            Source = source;
            Host = host;
            Chain = chain;
            InnerField = innerField;
        }
    }

    public enum MirWrapperInstallKind
    {
        Entity,
        Field,
        Method,
    }

    // new.wrapper.*：在宿主 .this 的隐藏槽上构造 wrapper 值
    public sealed class MirNewWrapper : MirInst
    {
        public MirOperand Host { get; }
        public MirWrapperInstallKind Kind { get; }
        public string WrapperType { get; }
        public string? FieldSymbol { get; }
        public string? MethodSymbol { get; }
        public MwMemberSymbol? Init { get; }
        public MwMemberSymbol? InitWrapper { get; }
        public IReadOnlyList<MirOperand> Args { get; }

        internal MirNewWrapper(MirOperand host, MirWrapperInstallKind kind, string wrapperType,
            string? fieldSymbol, string? methodSymbol, MwMemberSymbol? init,
            MwMemberSymbol? initWrapper, IReadOnlyList<MirOperand> args)
        {
            Host = host;
            Kind = kind;
            WrapperType = wrapperType;
            FieldSymbol = fieldSymbol;
            MethodSymbol = methodSymbol;
            Init = init;
            InitWrapper = initWrapper;
            Args = args;
        }
    }

    // get.self：从 wrapper .this 的宿主回指槽取出 TTarget（proxy 模板）
    public sealed class MirGetSelf : MirInst
    {
        public string Target { get; }

        internal MirGetSelf(string target)
        {
            Target = target;
        }
    }

    // invoke fn(..inner)：烘焙前占位，ProxyBaking 改写为 MirCall
    public sealed class MirInnerCall : MirInst
    {
        public IReadOnlyList<MirOperand> Args { get; }
        public string? Result { get; }
        public MirBlock? ExcTarget { get; }

        internal MirInnerCall(IReadOnlyList<MirOperand> args, string? result,
            MirBlock? excTarget = null)
        {
            Args = args;
            Result = result;
            ExcTarget = excTarget;
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
        // MW9b-G：写越界抛 OutOfBoundException 的异常边目标
        //（同 MirInvokeIndirect.ExcTarget 口径；读越界不抛，MirGetArray 无此边）
        public MirBlock? ExcTarget { get; }

        internal MirSetArray(MirOperand collection, MirOperand index, MirOperand element,
            MirType collectionType, MirBlock? excTarget = null)
        {
            Collection = collection;
            Index = index;
            Element = element;
            CollectionType = collectionType;
            ExcTarget = excTarget;
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

    // getid.var VALUE：取操作数实际类型 TypeSheet*（.typeid）
    public sealed class MirGetTypeIdVar : MirInst
    {
        public MirOperand Value { get; }
        public string Target { get; }

        internal MirGetTypeIdVar(MirOperand value, string target)
        {
            Value = value;
            Target = target;
        }
    }

    // getid.field field(F)（L1）：fieldid 槽占位物化（.fieldid → ptr）。
    // indirect 族的字段符号在 lowering 期经 FlowBuilder 静态追踪解析，
    // 运行时值无消费面——发射仅向槽写 null（对齐 VM VmFieldId 的
    // 「符号携带体」角色，native 侧符号由 MIR 直译节点携带）
    public sealed class MirGetFieldId : MirInst
    {
        public string FieldSymbol { get; }
        public string Target { get; }

        internal MirGetFieldId(string fieldSymbol, string target)
        {
            FieldSymbol = fieldSymbol;
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
        // MW9b-G：拆箱类型不符抛 CastException 的异常边目标
        //（同 MirInvokeIndirect.ExcTarget 口径）
        public MirBlock? ExcTarget { get; }

        internal MirUnboxAny(MirOperand source, string target, MirBlock? excTarget = null)
        {
            Source = source;
            Target = target;
            ExcTarget = excTarget;
        }
    }

    // cast / cast.safe（MW8c-2）：数值转换、占位目标、不相容失败。
    // 静态目标 = TargetTypeRef；泛型占位 = TargetTypeId（.generic.X 局部）。
    public sealed class MirCast : MirInst
    {
        public MirOperand Source { get; }
        public string Target { get; }
        public bool IsSafe { get; }
        public string? TargetTypeRef { get; }
        public MirOperand? TargetTypeId { get; }
        public bool IsIndirect => TargetTypeId != null;
        // MW9b-G：强制转换失败抛 CastException 的异常边目标（同
        // MirInvokeIndirect.ExcTarget 口径；cast.safe 不抛，恒等占位随
        // RcInjection 一并解析）
        public MirBlock? ExcTarget { get; }

        internal MirCast(MirOperand source, string target, bool isSafe,
            string? targetTypeRef, MirOperand? targetTypeId, MirBlock? excTarget = null)
        {
            Source = source;
            Target = target;
            IsSafe = isSafe;
            TargetTypeRef = targetTypeRef;
            TargetTypeId = targetTypeId;
            ExcTarget = excTarget;
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

    // throw 直译（MW9a，§16.9）：发射 = rigi_exc_raise(操作数)（内部
    // acquire，TLS pending 槽持 +1；被抛槽位自身 +1 不动，照常由
    // RcInjection 配平），随后沿异常边传播。ExcTarget = 本词法上下文的
    // 异常入口块（try 派发垫/逃逸垫，入口恒为 MirTakePending）；
    // null = 传播出函数（留待 C 棒解析）
    public sealed class MirThrow : MirInst
    {
        public MirOperand Exception { get; }
        public MirBlock? ExcTarget { get; }

        internal MirThrow(MirOperand exception, MirBlock? excTarget)
        {
            Exception = exception;
            ExcTarget = excTarget;
        }
    }

    // try 派发垫/逃逸垫入口（MW9a）：把 TLS pending 移入目标槽
    //（rigi_exc_take 移动语义：TLS 的 +1 转给接收槽，不另 acquire；
    // 产出类指令，旧值由 RcInjection 前置 release）
    public sealed class MirTakePending : MirInst
    {
        public string TargetLocal { get; }

        internal MirTakePending(string targetLocal)
        {
            TargetLocal = targetLocal;
        }
    }

    // ===== MW11a 协程（棒2：状态机改造）=====
    // lowering 层（BIL→MIR 直译，CoroutineSplitPass 之前）与 split 产出层
    //（split 保证消除全部 MirAwait/MirYieldBare）两族。
    // MW11c 棒5a 转向（RUNTIME §17.4）：split 产出的运行时交互点从
    // rigi_rt C 面族改为「Rigi 世界方法调（Task/Dispatcher，普通
    // MirCall/MirGetField 等）+ 最小原语指令」。保留的专用指令只剩
    // 三条：MirCoroutineCreate（fn 指针物化——Rigi 无此通道）、
    // MirCoroutineDone（DONE 出口标记，RcInjection frame 最终 release
    // 依据）、probe fn 已退为普通 MIR 调用（C ABI 特判随
    // rigi_yield_alarm 面一并退场）。

    // await 直译（split 前）：TaskSlot = Task/Task<T> 胖引用槽；ResultSlot
    // 非空 = await Task<T> 的结果槽（严格 T）。await 可重抛 Task 失败异常：
    // ExcTarget 语义与 MirCall 相同（本词法上下文的异常落点；null 由
    // RcInjection 兜底进函数级传播垫——split 不为 resume fn 预解析 null，
    // 留 RcInjection 统一进垫，垫尾分叉为失败终态序列）
    public sealed class MirAwait : MirInst
    {
        public string TaskSlot { get; }
        public string? ResultSlot { get; }
        public MirBlock? ExcTarget { get; }

        internal MirAwait(string taskSlot, string? resultSlot, MirBlock? excTarget)
        {
            TaskSlot = taskSlot;
            ResultSlot = resultSlot;
            ExcTarget = excTarget;
        }
    }

    // 裸 yield 直译（split 前，BIL §17.2 无 Alarm 形态）
    public sealed class MirYieldBare : MirInst
    {
        internal MirYieldBare()
        {
        }
    }

    // 带 Alarm 的 yield 直译（split 前，MW11b 棒3；BIL §17.2
    // yield ALARM 形态）：AlarmSlot = Alarm 胖引用槽（PollingAlarm/
    // EventAlarm 运行时分类——split 产物经 MirTypeCheck 分流，MIR
    // 不区分）。无 ExcTarget——探测异常在恢复块异步发生（对齐
    // VM：isReady 抛出 = yield 点失败走 Task FAILED，await 点重抛），
    // 非本指令同步抛出；split 保证消除本指令
    public sealed class MirYieldAlarm : MirInst
    {
        public string AlarmSlot { get; }

        internal MirYieldAlarm(string alarmSlot)
        {
            AlarmSlot = alarmSlot;
        }
    }

    // rigi_coroutine_create（split 产出；spawn stub 与冷 Task 工厂专用）：
    // FrameSlot = 合成 frame 胖引用槽（+1 所有权随本指令 move 进协程
    // 续体，RcInjection 免配平——resume fn DONE 出口最终 release；
    // 冷 Task 永不启动的 frame/cohandle 泄漏是已知边缘，见
    // MIDDLEWARE_ARCHITECTURE §6）；ResumeFn = 合成状态机 fn 符号
    //（Emit 取 LLVM fn 地址 ptrtoint i64——fn 指针物化的唯一通道）；
    // HandleSlot = i64 协程句柄产出槽
    public sealed class MirCoroutineCreate : MirInst
    {
        public string FrameSlot { get; }
        public MwMemberSymbol ResumeFn { get; }
        public string HandleSlot { get; }

        internal MirCoroutineCreate(string frameSlot, MwMemberSymbol resumeFn,
            string handleSlot)
        {
            FrameSlot = frameSlot;
            ResumeFn = resumeFn;
            HandleSlot = handleSlot;
        }
    }

    // DONE 出口标记（split 产出 + RcInjection resume 垫尾）：纯标记
    // 指令——RcInjection 据此在出口追加 frame 最终 release
    //（IsCoroutineDoneExit）；Emit 无操作。语义由同块的
    // complete/fail + publishAll + noteTerminal 普通 MIR 序列承载
    public sealed class MirCoroutineDone : MirInst
    {
        internal MirCoroutineDone()
        {
        }
    }

    // tainted→tainted 调用协议的 resume 直调（B-1 split 产出）：
    // 原生栈下钻调 callee 的 resume fn（i32(ptr) C ABI， Emit 从
    // frame 胖引用取 payload 半直传）。FrameSlot = callee frame 胖
    // 引用槽（借用——callee resume 的 frame 参数同为借用约定，
    // +1 由调用方 frame 的 callee 槽持有）；CodeSlot = i32
    // RigiResumeCode 产出槽（0=SUSPENDED/1=YIELDED/2=DONE/3=FAILED，
    // 调用方按码分流；FAILED 时 pending 已置位由调用方重抛）。
    // 无 ExcTarget——callee resume 内部兜住全部异常（FAILED 码上传），
    // 本指令自身不抛
    public sealed class MirResumeCall : MirInst
    {
        public MwMemberSymbol ResumeFn { get; }
        public string FrameSlot { get; }
        public string CodeSlot { get; }

        internal MirResumeCall(MwMemberSymbol resumeFn, string frameSlot, string codeSlot)
        {
            ResumeFn = resumeFn;
            FrameSlot = frameSlot;
            CodeSlot = codeSlot;
        }
    }

    // rigi_failure_get（split 产出；await 失败快路径）：NodeIdSlot =
    // i64 失败注册表节点 id（task.failureNodeId 字段读出）；
    // OutFatSlot = 异常拷入的 .any 胖槽（+1 随拷贝移交，产出类指令；
    // 节点在 record 后恒存活至进程退出，读取必然命中）
    public sealed class MirFailureLoad : MirInst
    {
        public string NodeIdSlot { get; }
        public string OutFatSlot { get; }

        internal MirFailureLoad(string nodeIdSlot, string outFatSlot)
        {
            NodeIdSlot = nodeIdSlot;
            OutFatSlot = outFatSlot;
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

    // 异常返回终结符（MW9a）：pending 已在 TLS，直接返回调用方
    //（本棒仅由 MirThrow 的 ExcTarget==null 落点产生；release 填充与
    // ExcTarget==null 的解析改写归 C 棒）
    public sealed class MirRetThrow : MirTerminator
    {
        internal MirRetThrow()
        {
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
