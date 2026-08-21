namespace RigiCompiler
{
    // 函数级绑定上下文（M55 visitor 化协议；M65 组件化拆分）：一个
    // 函数体绑定期间存活的可变状态。每个函数体新建一个实例（旧
    // BindSession 的「防御性清空」正是状态污染证据——对象化后新建
    // 即清空，无需 Clear）。
    //
    // 组件化结构（M65）：本类是组合根，职责按隔离需求拆为组件——
    // Frame（只读函数帧：当前函数是谁/在哪/能否访问）、Accessor
    // （访问器体状态）、Labels（控制流标签栈集）、Flow（流分析，
    // M55 先例）、Locals（函数内局部符号表）。设计原则承自
    // IFlowContext（M55 起，M65 撤销——方言 = 同一状态对象的
    // 接口视图；按真实隔离需求从 BindContext 拉组件；禁止切多个
    // 独立状态对象——组件随组合根同生同灭，非按 visitor 各切状态）。
    internal sealed class BindContext
    {
        public BindContext(MethodSymbol method, FileContext fileCtx, TypeSymbol? declaringType,
            bool isDefaultValueContext = false, bool isLambda = false,
            ThisSymbol? thisSymbol = null, TypeSymbol? lambdaThisType = null,
            TypeSymbol? lookupHost = null)
        {
            Frame = new BindFunctionFrame(method, fileCtx, declaringType, isDefaultValueContext,
                lookupHost);
            IsLambda = isLambda;
            This = thisSymbol ?? (Frame.HasThis && declaringType != null
                ? new ThisSymbol(declaringType) : null);
            LambdaThisType = lambdaThisType;
        }

        // 只读函数帧（当前函数/文件上下文/宿主类型/默认值上下文标记
        // + HasThis/CanAccess 计算）
        public BindFunctionFrame Frame { get; }

        // 访问器体绑定状态（S8e；非访问器体上下文 Field 为 null）
        public AccessorBodyState Accessor { get; } = new AccessorBodyState();

        // proxy 体绑定状态（S11b；非 proxy 体上下文 Specialization 为 null）
        public ProxyBodyState Proxy { get; } = new ProxyBodyState();

        // 控制流标签栈集（S7b/S7c-1/S7d/M61）
        public BindLabelState Labels { get; } = new BindLabelState();

        // 函数内全部局部符号（含值块/合成之外的源级声明），BoundFunctionBody.Locals 来源
        public List<LocalSymbol> Locals { get; } = new List<LocalSymbol>();

        // 流分析状态（DA；S8b 收窄表将长在这里）
        public FlowState Flow { get; } = new FlowState();

        public bool IsLambda { get; }
        public HashSet<SemanticSymbol> CapturedSymbols { get; } = new HashSet<SemanticSymbol>();
        public HashSet<ParameterSymbol> LambdaParameters { get; } = new HashSet<ParameterSymbol>();
        public ThisSymbol? This { get; }
        // lambda 体内的有效 this 类型（SYNTAX §5.2：lambda 的函数级宿主是隐藏类的
        // $$call，但源码层 this 指向外层声明位置的实例——此槽承载外层有效 this 类型，
        // 捕获后 P4 经 .capture.this 字段物化）；非 lambda 上下文恒 null
        public TypeSymbol? LambdaThisType { get; }

        // 语句位路径绑定：允许链末 void 实例调用（§14.5 wrapper place 上调
        // void 方法）。仅 BindNonAssignment 短暂置位，值位置恒 false。
        public bool AllowVoidCall { get; set; }

        // F1 使用点类型可见性去重（V-B 统一收口 / c6 级联控制）：本函数体
        // 内已做过可见性检查的语法点（同一 AST 节点重绑不重复查）与已报过
        // 的不可见类型（引用相等驻留）——同一泄漏链（显式标注 → 推断声明
        // → 直链/?. 链使用）只报一次，下游不级联。返回 false = 已登记
        //（调用方据此跳过重复诊断）
        private readonly HashSet<ASTNode> accessibilityCheckedNodes = new HashSet<ASTNode>();
        private readonly HashSet<TypeSymbol> reportedInaccessibleTypes =
            new HashSet<TypeSymbol>();

        public bool NoteAccessibilityChecked(ASTNode node) => accessibilityCheckedNodes.Add(node);

        public bool NoteInaccessibleTypeReported(TypeSymbol type) =>
            reportedInaccessibleTypes.Add(type);
    }
}
