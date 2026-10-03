namespace RigiCompiler
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

        internal bool IsWorker { get; private set; }
        internal BindEnvironment CreateWorker()
        {
            return new BindEnvironment(Unit, Declarations)
            {
                IsWorker = true,
                parameterDefaults = parameterDefaults,
                enumCaseFixedArguments = enumCaseFixedArguments,
                PrebuiltMethods = PrebuiltMethods,
                DeclarationApplications = DeclarationApplications,
            };
        }

        internal HashSet<MethodSymbol> PrebuiltMethods { get; private set; } = new();
        internal HashSet<WrapperApplication> DeclarationApplications { get; private set; } = new();
        private readonly Dictionary<WrapperApplication, IReadOnlyList<BoundExpression>> wrapperArguments = new();
        internal IReadOnlyList<BoundExpression>? WrapperArguments(WrapperApplication application) =>
            wrapperArguments.TryGetValue(application, out var local) ? local : application.BoundInitArguments;
        internal void StoreWrapperArguments(WrapperApplication application, IReadOnlyList<BoundExpression> arguments)
        {
            // 声明级应用在 preseed 后冻结；失败的再次尝试只写当前 body overlay。
            // 局部/lambda 应用恰由当前 outer job 创建，允许直接挂产物。
            if (IsWorker && DeclarationApplications.Contains(application)) wrapperArguments[application] = arguments;
            else application.BoundInitArguments = arguments;
        }

        internal void MergeDelta(BindEnvironment worker)
        {
            SyntheticLambdas.AddRange(worker.SyntheticLambdas);
            SyntheticCellBodies.AddRange(worker.SyntheticCellBodies);
            foreach (var (application, span) in worker.DefaultWrapperConstructions)
                DefaultWrapperConstructions.TryAdd(application, span);
            DefaultConstructions.AddRange(worker.DefaultConstructions);
            CheckedInitBodies.UnionWith(worker.CheckedInitBodies);
        }

        private readonly Dictionary<string, int> synthesisOrdinals = new(StringComparer.Ordinal);
        internal string HiddenName(string role, ASTNode syntax, string? owner = null)
        {
            var key = Unit.SyntaxIdentity(syntax) + "/" + role + "/" + owner;
            synthesisOrdinals.TryGetValue(key, out var ordinal);
            synthesisOrdinals[key] = ordinal + 1;
            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key + "/" + ordinal));
            return ".." + role + ".." + Convert.ToHexStringLower(hash.AsSpan(0, 16));
        }

        internal List<BoundLambdaExpression> SyntheticLambdas { get; } =
            new List<BoundLambdaExpression>();

        // cell 隐藏子类合成方法体（统一 cell 存储，SYNTAX §5.2/§14.3）：
        // CellClassFactory 逐方法产物（init/getValue/setValue），
        // BindingDriver 收尾汇入函数体列表（走统一 P4 管线）
        internal List<BoundFunctionBody> SyntheticCellBodies { get; } =
            new List<BoundFunctionBody>();

        // 无 init 的 wrapper 应用在全部字段初始化器/默认构造就位后检查 DA，
        // 避免应用声明先于 wrapper 定义时，把尚未合成的初值方法误判为缺失。
        internal Dictionary<WrapperApplication, CharRange?> DefaultWrapperConstructions { get; } =
            new Dictionary<WrapperApplication, CharRange?>();

        // 早期默认构造不能依赖尚未合成的初值方法；全部真实方法就位后检查。
        internal List<(TypeSymbol Type, CharRange? Span, DefaultConstructionMessage Message)>
            DefaultConstructions { get; } = new();
        // 仅实际绑定/合成且已做 DA 的 init 体可担保默认构造；占位签名不算。
        internal HashSet<MethodSymbol> CheckedInitBodies { get; } = new();

        // 参数默认值绑定产物（S8d）：BindingDriver 驱动绑定（声明点作用域），
        // 调用点缺省时查表填充。记忆化按需绑定：前向依赖（f(a = h()) 声明
        // 先于 h）由调用点查表递归触发被依赖参数的绑定，结果入表不重绑
        // （诊断不重复）；in-flight 集合拦截默认值依赖环（保守 null——
        // 环上各声明点自然产生诊断）。绑定失败的默认值缓存 null——
        // 调用点按无默认处理（声明点诊断已报）
        private Dictionary<ParameterSymbol, BoundExpression?> parameterDefaults =
            new Dictionary<ParameterSymbol, BoundExpression?>();
        private readonly HashSet<ParameterSymbol> parameterDefaultsInFlight =
            new HashSet<ParameterSymbol>();
        private Func<ParameterSymbol, BoundExpression?>? defaultValueBinder;

        internal void SetDefaultValueBinder(Func<ParameterSymbol, BoundExpression?> binder)
        {
            defaultValueBinder = binder;
        }

        public BoundExpression? GetParameterDefault(ParameterSymbol parameter, ASTNode? callSyntax = null,
            IReadOnlyDictionary<GenericParameterSymbol, SemanticSymbol>? substitutions = null, SemanticSymbol? expectedType = null)
        {
            if (callSyntax != null && parameter.DefaultFactory is { } factory)
                return new BoundCallExpression(callSyntax, factory, Array.Empty<BoundExpression>(), expectedType ?? factory.ReturnType!,
                    factory.GenericParameters.Select(g => substitutions?.GetValueOrDefault(g) ?? g).ToArray());
            if (parameter.DefaultValue == null) return null;
            if (parameterDefaults.TryGetValue(parameter, out var cached)) return cached;
            if (IsWorker) throw new CompilerInternalException("body worker 不能懒写冻结的参数默认值表");
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
        private Dictionary<EnumCaseSymbol, IReadOnlyList<BoundExpression?>>
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

        // proxy 体诊断去重（S11b，ROADMAP S11b）：同一 proxy 声明体按
        // (proxy × 目标成员) 逐组合绑定——同一声明被绑定多次，体内同一
        // 错误（同 AST 位置同消息）每组合重复落袋。去重键 = (proxy 声明
        // 符号, span 引用, 消息)——span 取 AST 节点的同一 CharRange 实例
        //（引用相等命中）；CurrentProxy 由 BindingDriver 在逐组合绑定
        // 期间设置（单趟顺序执行，无需栈）
        private readonly HashSet<(MethodSymbol Proxy, CharRange? Span, string Message)>
            proxyDiagnostics = new HashSet<(MethodSymbol, CharRange?, string)>();
        internal MethodSymbol? CurrentProxy { get; set; }

        public void Error(CharRange? span, string message)
        {
            if (CurrentProxy is { } proxy
                && !proxyDiagnostics.Add((proxy, span, message)))
            {
                return;
            }
            Unit.Diagnostics.Error(DiagnosticPhase.P3, span, message);
        }
    }
}
