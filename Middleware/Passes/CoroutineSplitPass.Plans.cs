using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // Plans 职责；与主文件共享同一类型、字段及生命周期。

        // ===== 挂起点与活性分析 =====

        // 挂起点描述：所在块、块内指令序、state 号（1 起；0 = 原入口）
        private sealed class SuspensionPoint
        {
            internal MirBlock Block = null!;
            internal int InstIndex;
            internal int State;
            internal MirInst Inst = null!;
            // 恢复后仍活跃的槽（活性分析回填，保 fn.Locals 序）
            internal List<string> LiveAfter = new();
            // B-1：tainted→tainted 直调挂起点（Inst 为 MirCall 时非空）
            internal CallSiteInfo? CallSite;
            // B-2：虚/interface/运算符派发挂起点（目标集合动态分流）
            internal VirtualSiteInfo? Virtual;
            // B-2：含挂起点 init 的构造挂起点（Inst 为 MirNewObject）
            internal InitSiteInfo? InitSite;
            // R2-c：new.indirect × tainted class init 的构造挂起点
            internal IndirectInitSiteInfo? IndirectInit;
            // Phase 2.6：PollingAlarm 探测站点（闭包内存在 tainted isReady
            // 实现时非空）——探测经恢复块站点协议臂下钻，支持 isReady
            // 中途挂起；全 untainted 闭包为 null（$mw.poll_probe 廉价路径）
            internal PollProbeSiteInfo? ProbeSite;
        }

        // Phase 2.6：yield-alarm 探测站点协议信息（PreparePollProbeSites
        // 回填；镜像 VirtualSiteInfo 的臂结构，站点固定为 isReady(alarm)
        // 单参虚派发）。ProbeState 是探测挂起子状态：探测 fn（isReady
        // 状态机）中途挂起时本层 frame 写入该 state，重发布恢复走专用
        // 恢复块直落探测调用块下钻——区别于「未就绪退回等待后重排」
        // 的再次首探（state 保持 N 重入 poll gate）
        private sealed class PollProbeSiteInfo
        {
            internal int ProbeState;
            internal string AlarmSlot = "";   // 探测帧接收者（alarm 槽，恒活跃）
            // tainted isReady 实现臂（InheritanceDepth 深→浅；每实现
            // 一套 CallSiteInfo，TypeRefs 为臂 type.is 目标集，Depth 为
            // 排序键——排完序后仅作占位）
            internal List<(CallSiteInfo Impl, List<string> TypeRefs, int Depth)>
                Arms = new();
            // untainted 联合臂 type.is 目标集（全 tainted 闭包为空——
            // 命中走 $mw.poll_probe 同步廉价路径；分流 miss = 闭包外
            // 类型，运行期不可达）
            internal List<string> UntaintedTypeRefs = new();
        }

        // R2-c：new.indirect × tainted class init 的构造点协议信息。
        // native 槽 0 分发器不继承 init（实证：派生类无自声明 init
        // 时 new.indirect 抛 NoSuchMethod，双端一致）——运行期
        // typeid 只有恰好是 tainted 重载宿主类（的某个闭合构造）
        // sheet 时才可能选中该 init，故臂条件 = 精确 sheet 匹配：
        // IsTypeId(臂 sheet) ∧ ¬IsTypeId(各派生物化 sheet)。命中
        // 臂：以臂 sheet 的静态构造形态 MirNewObject 空 init 分配
        //（init.wrapper 原位缝合字段初始值）→ Target 槽落定并回存
        // 本层 frame → init frame（.this = 新建对象）下钻；DONE 直
        // 落原后继（结果即 Target 槽）。全部臂未命中 → 默认臂落原
        // MirNewIndirect（同步分发器路径——运行期目标必非 tainted
        // init 或 NoSuchMethod，语义保持）
        private sealed class IndirectInitSiteInfo
        {
            internal string TypeIdLocal = "";        // typeid 槽（分流链复读，强制活跃）
            internal string TargetLocal = "";        // 原指令结果槽（臂内分配落点 + 回存 frame）
            internal MirBlock? ExcTarget;            // 原指令异常边
            internal MirNewIndirect Original = null!; // 默认臂复用原指令
            internal List<IndirectInitArm> Arms = new();
        }

        // 单条 new.indirect 臂：一个物化 sheet × 一个 tainted init 重载
        private sealed class IndirectInitArm
        {
            internal MwTypeSymbol AllocType = null!;  // 臂 sheet 的构造形态（MirNewObject 分配用）
            internal string SheetCanonical = "";
            internal MwMemberSymbol? InitWrapper;     // 宿主声明级 ..init.wrapper（可空）
            internal List<string> ExclusionSheets = new(); // 派生物化 sheet（精确化排除项）
            internal CallSiteInfo Impl = null!;
        }

        // R2-c：模块内 class init 重载描述（tainted 判定在调用点）；
        // 声明级形参类型按物化 sheet 逐份代入（镜像 DynamicNewEmitter
        // .CollectInits 的匹配语义：argc + ArgToken(canonical) 恒等）
        private sealed class IndirectInitOverload
        {
            internal MirFunction InitFn = null!;
            internal MwTypeSymbol HostTemplate = null!;
            internal List<string> DeclParamTypeRefs = new();
            internal MwMemberSymbol? Wrapper;
            internal List<(TypeLayoutPlan Plan, List<string> ParamTypes)>? Sheets;
        }

        // 含挂起点 init 的构造点协议信息：分配与 init 下钻分离——
        // head 用合成空 init 完成分配（init.wrapper 缝合字段初始值
        // 保持原位），Target 槽先落定；init frame 的 .this = 新建对
        // 象，恢复后 DONE 直落原后继（结果即 Target 槽本身）
        private sealed class InitSiteInfo
        {
            internal CallSiteInfo Site = null!;
            internal string TargetLocal = "";
            internal MirNewObject Original = null!;
        }

        // tainted 直调点的 callee 协议信息（PrepareCallSites 回填；
        // FrameType/FrameInit 延迟到 EmitCallSplit 解析——plain frame
        // 预注册完成后才存在，递归调用链安全）
        private sealed class CallSiteInfo
        {
            internal MirFunction Callee = null!;
            internal string CalleeLocal = "";        // 调用方 resume fn 内的 callee frame 槽
            internal string CalleeFrameCanonical = "";
            internal MwMemberSymbol ResumeSymbol = null!;
            internal string? ResultFieldSymbol;      // callee 非 void 时的 $mw.result 字段符号
            // B-2：实参→callee frame 落参计划（§7.2 隐藏参数感知：
            // 类级 typeid 从调用约定剔除——按宿主构造形态合成常量
            // typeid 或转抄调用方同名 .generic.* 局部）
            internal List<ArgDrop> Drops = new();
        }

        // 单条落参：直落 = 调用点实参槽；TypeIdConst = MirGetTypeId
        // 常量 typeid；CallerTypeId = 调用方 .generic.* 局部转抄；
        // ReceiverTypeIdOwner/Parameter = 运行期读取接收者真实泛型实参
        //（#..generic.）读取（泛型宿主虚派发臂——静态构造形态被
        // 接收者 cast 剥成裸模板时，真实构造实参恒在实例头隐藏槽）
        private sealed class ArgDrop
        {
            internal string FrameFieldSymbol = "";
            internal MirOperand? Operand;
            internal MirType? OperandTargetType;
            internal string? TypeIdTypeRef;
            internal string? CallerTypeIdLocal;
            internal string? ReceiverTypeIdOwner;
            internal string? ReceiverTypeIdParameter;
            internal MirOperand? ReceiverTypeIdOperand;
        }

        // B-2 虚派发挂起点：闭包全类臂（最深派生优先——臂条件
        // type.is 是子类判定，浅类臂不得遮蔽深类）。tainted 实现臂
        // 走协议（建对应 frame 下钻）；非 tainted 实现臂落原调用块
        //（普通虚派发，对齐 VM 可观察行为）；默认臂（闭包外类型/
        // null 接收者）同为原调用（NRE 语义保持）
        private sealed class VirtualSiteInfo
        {
            internal string ReceiverLocal = "";      // 接收者槽（分流链复读，强制活跃）
            internal string? Result;                 // 原调用结果槽
            internal MirBlock? ExcTarget;            // 原调用异常边
            internal MirInst OriginalCall = null!;   // 默认臂/非 tainted 臂复用原指令（MirCall/MirInvokeIndirect）
            internal List<VirtualArm> Arms = new();  // 全闭包类臂（最深派生优先）
        }

        private sealed class VirtualArm
        {
            internal int InheritanceDepth;
            internal CallSiteInfo? Impl;             // 非 tainted 实现为 null（落原调用）
            // R2-a：臂条件 type.is 目标集——首元素恒为类 PlanKey
            //（非泛型 = 唯一元素；泛型类 = 模板空壳 + 模块内全部闭
            // 合构造 sheet：实例头是构造 sheet 且其基链不含模板空
            // 壳，单模板键判定恒 miss；开放占位 new 的实例仍携模
            // 板空壳，故模板键保留在首位）
            internal List<string> TypeRefs = null!;
        }

        // split 模式：Tasked = Task 包装（async fn 与 tainted main——
        // frame 带 $mw.task，终态走 Task complete/fail 序列）；Plain =
        // 裸 frame（tainted 普通 fn——无 Task，结果写 $mw.result 由
        // 调用方 DONE 臂读取，传播垫尾 release + ret FAILED 沿链上传）
        private enum SplitMode { Tasked, Plain }

        // split 计划（PrepareSplit 产出，ExecuteSplit 消费；两相分离
        // 让 plain frame 类型先于一切 resume 合成完成注册）
        private sealed class SplitPlan
        {
            internal MirFunction Fn = null!;
            internal SplitMode Mode;
            internal List<SuspensionPoint> Points = null!;
            internal List<MirLocal> SavedSlots = null!;
            internal HashSet<string> ParamNames = null!;
            internal string FrameCanonical = "";
            internal MwTypeSymbol FrameType = null!;
            internal MirType FrameMirType = null!;
            internal MwMemberSymbol FrameInit = null!;
            internal string StateFieldSymbol = "";
            internal string? TaskFieldSymbol;
            internal string? TaskTypeRef;
            internal string? TaskConstructionRef;
            internal string? ResultFieldSymbol;
            internal MwMemberSymbol ResumeSymbol = null!;
        }

    }
}
