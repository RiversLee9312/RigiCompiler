namespace LatteCompiler
{
    // 只读绑定环境（M55 visitor 化协议）：全编译期不变，作为固定参数
    // 传入所有 visitor。诊断统一经 Error 落袋（P3 phase；可恢复诊断模型）。
    internal sealed class BindEnvironment
    {
        public BindEnvironment(CompilationUnit unit, DeclarationCollection declarations)
        {
            Unit = unit;
            Declarations = declarations;
            Names = new NameResolver(unit, DiagnosticPhase.P3);
        }

        public CompilationUnit Unit { get; }

        public DeclarationCollection Declarations { get; }

        // 类型引用/符号路径解析（P2 同设施，本环境以 DiagnosticPhase.P3 实例化）
        public NameResolver Names { get; }

        public BootstrapSymbols B => Unit.Symbols.Bootstrap;

        // 参数默认值绑定产物（S8d）：BindingDriver 驱动绑定（声明点作用域），
        // 调用点缺省时查表填充。记忆化按需绑定：前向依赖（f(a = h()) 声明
        // 先于 h）由调用点查表递归触发被依赖参数的绑定，结果入表不重绑
        // （诊断不重复）；in-flight 集合拦截默认值依赖环（保守 null——
        // 环上各声明点自然产生诊断）。绑定失败的默认值缓存 null——
        // 调用点按无默认处理（声明点诊断已报）
        private readonly Dictionary<ParameterSymbol, BoundExpression?> parameterDefaults =
            new Dictionary<ParameterSymbol, BoundExpression?>();
        private readonly HashSet<ParameterSymbol> parameterDefaultsInFlight =
            new HashSet<ParameterSymbol>();
        private Func<ParameterSymbol, BoundExpression?>? defaultValueBinder;

        internal void SetDefaultValueBinder(Func<ParameterSymbol, BoundExpression?> binder)
        {
            defaultValueBinder = binder;
        }

        public BoundExpression? GetParameterDefault(ParameterSymbol parameter)
        {
            if (parameter.DefaultValue == null) return null;
            if (parameterDefaults.TryGetValue(parameter, out var cached)) return cached;
            if (defaultValueBinder == null || !parameterDefaultsInFlight.Add(parameter))
            {
                return null;
            }
            var value = defaultValueBinder(parameter);
            parameterDefaultsInFlight.Remove(parameter);
            parameterDefaults[parameter] = value;
            return value;
        }

        // enum case 模板固定实参绑定产物（S11，SYNTAX §12.1）：BindingDriver
        // 声明点模板绑定时写入（驱动单趟遍历、每 case 至多一次——模板表达式
        // 不能引用 case，无依赖环，无需 in-flight 拦截）；键 = case 符号，
        // 值 = 按 init 参数序的固定实参（洞位置 null 占位——无显式 init 的
        // 零参 case 为空列表）。备 P4 case 构造入口 lowering 消费，本阶段
        // 只承担声明点类型检查
        private readonly Dictionary<EnumCaseSymbol, IReadOnlyList<BoundExpression?>>
            enumCaseFixedArguments = new Dictionary<EnumCaseSymbol, IReadOnlyList<BoundExpression?>>();

        internal void SetEnumCaseFixedArguments(EnumCaseSymbol caseSymbol,
            IReadOnlyList<BoundExpression?> fixedArguments)
        {
            enumCaseFixedArguments[caseSymbol] = fixedArguments;
        }

        public IReadOnlyList<BoundExpression?>? GetEnumCaseFixedArguments(EnumCaseSymbol caseSymbol)
        {
            return enumCaseFixedArguments.TryGetValue(caseSymbol, out var fixedArguments)
                ? fixedArguments
                : null;
        }

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P3, span, message);
        }
    }
}
