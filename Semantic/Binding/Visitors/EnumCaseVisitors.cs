namespace LatteCompiler
{
    // enum case 引用（S11，SYNTAX §12）：裸前导点 `.Success`（值位置，
    // expectedType 提供 enum 上下文——§12「编译器不会单凭 case 名反向
    // 猜测 enum 类型」）。参数化调用 `.Failed(404)` 是表达式底座 + Call
    // 后缀的路径形态，经 PathFacility 底座特判转入
    // EnumCaseFacility.BindParameterizedCall；is .Case 谓词（§12.3）在
    // TypeCheckVisitor（TargetCase 槽不经表达式分派）。
    // 模板绑定失败的 case（HoleParameters 未落定）使用侧一律静默
    // （声明点诊断已报，ErrorType 惯例不二次报）。

    // 裸前导点 case 引用（`.Success`）：固定 case 产 BoundEnumCaseExpression；
    // 参数化 case 裸引用诊断（洞实参缺失）
    internal sealed class EnumCaseVisitor : ExpressionVisitor<EnumCaseVisitor, BindContext>
    {
        protected override BoundExpression? VisitCore(ASTNode node, Scope scope, BindContext ctx,
            BindEnvironment env, TypeSymbol? expectedType)
        {
            var caseNode = (EnumCaseExpressionASTNode)node;
            var caseSymbol = EnumCaseFacility.ResolveCase(caseNode.CaseName,
                caseNode.Span ?? node.Span, expectedType, env);
            if (caseSymbol == null) return null;
            // 参数化 case 裸引用（HoleParameters 已由 ResolveCase 保证落定）
            if (caseSymbol.HoleParameters!.Count > 0)
            {
                env.Error(caseNode.Span ?? node.Span, $"Case '{caseSymbol.Name}' requires " +
                    $"{caseSymbol.HoleParameters.Count} argument(s)");
                return null;
            }
            return new BoundEnumCaseExpression(node, caseSymbol, Array.Empty<BoundExpression>());
        }
    }

    internal static class EnumCaseFacility
    {
        // case 解析（裸引用/参数化调用/is .Case 共用前半）：expectedType
        // 的定义级必须是 enum struct（§12 期望类型上下文；缺失或非
        // enum → 无法推断）；泛型构造 enum 归口（S11 范围决策——声明侧
        // 模板绑定同步跳过）。命中后模板绑定未落定（失败）的静默返回
        // null；落定保证（HoleParameters 非 null）由返回非 null 承载
        public static EnumCaseSymbol? ResolveCase(string caseName, CharRange? span,
            TypeSymbol? expectedType, BindEnvironment env)
        {
            var definition = expectedType?.ConstructedFrom ?? expectedType;
            if (definition != null && definition.Kind == TypeKind.EnumStruct
                && expectedType!.ConstructedFrom != null)
            {
                env.Error(span, "P3: generic enum cases are not supported yet (S11)");
                return null;
            }
            if (definition == null || definition.Kind != TypeKind.EnumStruct)
            {
                env.Error(span, $"Cannot infer the enum type of '.{caseName}' from context");
                return null;
            }
            var caseSymbol = FindCase(definition, caseName, span, env);
            // 模板绑定失败（HoleParameters 未落定）——静默（声明点已诊断）
            if (caseSymbol is { HoleParameters: null }) return null;
            return caseSymbol;
        }

        // 定义级 enum 上按名查 case（undefined 诊断统一落点）
        public static EnumCaseSymbol? FindCase(TypeSymbol enumDefinition, string caseName,
            CharRange? span, BindEnvironment env)
        {
            var caseSymbol = enumDefinition.Cases.FirstOrDefault(c => c.Name == caseName);
            if (caseSymbol == null)
            {
                env.Error(span, $"Undefined case '{caseName}' on " +
                    $"'{BoundAnalysis.TypeDisplay(enumDefinition)}'");
                return null;
            }
            return caseSymbol;
        }

        // 参数化调用（`.Failed(404)`，PathFacility 底座特判转入）：
        // expectedType 解析 case 后绑定洞实参——位置实参按序、具名按
        // 洞名归位；洞无默认值，个数必须恰好。产物规范序 = 洞签名序
        // （= init 参数序，HoleParameters 收集序即实参位置序）。
        // 轻量自实现（不复用 CallFacility.BindArguments）：洞实参只是
        // case 模板的子集参数（固定位置已有声明点值），BindArguments
        // 服务「全形参表 + 默认值填充 + 可变包」场景，输入不可直接改造
        public static BoundExpression? BindParameterizedCall(ASTNode node,
            EnumCaseExpressionASTNode caseNode, List<ArgumentASTNode> arguments,
            TypeSymbol? expectedType, Scope scope, BindContext ctx, BindEnvironment env)
        {
            var caseSymbol = ResolveCase(caseNode.CaseName, caseNode.Span ?? node.Span,
                expectedType, env);
            if (caseSymbol == null) return null;
            var holes = caseSymbol.HoleParameters!;
            if (holes.Count == 0)
            {
                env.Error(node.Span, $"Case '{caseSymbol.Name}' takes no arguments");
                return null;
            }
            var bound = new BoundExpression?[holes.Count];
            var failed = false;
            var nextPositional = 0;
            foreach (var argument in arguments)
            {
                int index;
                if (argument.Name == null)
                {
                    if (nextPositional >= holes.Count)
                    {
                        env.Error(argument.Span,
                            $"Too many arguments for case '{caseSymbol.Name}'");
                        failed = true;
                        continue;
                    }
                    index = nextPositional++;
                }
                else
                {
                    index = -1;
                    for (int i = 0; i < holes.Count; i++)
                    {
                        if (holes[i].Name == argument.Name) { index = i; break; }
                    }
                    if (index < 0)
                    {
                        env.Error(argument.Span,
                            $"Case '{caseSymbol.Name}' has no hole named '{argument.Name}'");
                        failed = true;
                        continue;
                    }
                }
                if (bound[index] != null)
                {
                    env.Error(argument.Span,
                        $"Duplicate argument for hole '{holes[index].Name}'");
                    failed = true;
                    continue;
                }
                var expected = holes[index].Type as TypeSymbol;
                var value = ExpressionDispatcher.Visit(argument.Value.Expression, scope, ctx, env,
                    expected);
                if (value == null)
                {
                    failed = true;
                    continue;
                }
                if (expected != null && !SymbolLookup.IsAssignable(value.Type, expected, env))
                {
                    env.Error(argument.Value.Span ?? argument.Span,
                        $"Cannot pass '{BoundAnalysis.TypeDisplay(value.Type)}' as " +
                        $"'{BoundAnalysis.TypeDisplay(expected)}'");
                    failed = true;
                    continue;
                }
                bound[index] = value;
            }
            // 缺失检查（洞无默认值；与 BindArguments 同口径——绑定失败
            // 的洞同报 Missing，级联先例已接受）
            for (int i = 0; i < holes.Count; i++)
            {
                if (bound[i] != null) continue;
                env.Error(node.Span, $"Missing argument for hole '{holes[i].Name}'");
                failed = true;
            }
            return failed ? null
                : new BoundEnumCaseExpression(node, caseSymbol, bound.Select(b => b!).ToList());
        }
    }
}
