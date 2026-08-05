using System.Collections.Generic;

namespace LatteCompiler
{
    // ===== enum case 结构级检查与判别值落定（SYNTAX §12，S11）=====
    //
    // 纯 AST 结构级阶段，不依赖 init 参数类型解析（init 选择与洞签名归 P3）。
    // enum case 不走 DeclEntry 条目驱动（EntryCollector 只为类型/成员产条目），
    // 本阶段直接按类型条目遍历宿主 enum 的 Cases AST，经 Declarations.SymbolOf
    // 取 P1 建好的 EnumCaseSymbol。
    //
    // 本阶段内容：
    //   1. 洞独占性（§12.1）：`_` 必须独占一个实参位置（case 参数表的直接
    //      实参，可具名 name = _）；嵌套在表达式内部的 `_`（someExpression(_)
    //      形态）落诊断。`_` 的单段路径识别与 switch 占位同一形态
    //      （S7d SwitchVisitors 先例）；switch pattern 子树排除——pattern 的
    //      `_` 是占位语义（P3 有独立 selector 上下文），不是模板洞；
    //   2. case 名唯一性复核（§12；Parser FinishEnumCases 已拦，防御）；
    //   3. 判别值落定：显式值写入 EnumCaseSymbol.Discriminant（唯一/非负
    //      复核——Parser 已拦，防御）；auto 保持 null（编号按声明序从 0，
    //      §12.4，归发射侧按宿主 Cases 表序推导）。
    //
    // 明确不做（归下阶段，P3 声明点绑定，仿 S8d 参数默认值声明点绑定先例）：
    //   init 模板绑定（init 选择 + 固定实参表达式的绑定与类型检查）、
    //   洞 pub 规则（§12.2：绑定到非 pub init 的 case 必须是固定模板）。
    internal sealed class EnumCaseResolver : ResolverVisitor<EnumCaseResolver>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                // 重复类型声明（InGraph=false）跳过——P1 已诊断，不重复报错
                if (!entry.InGraph) continue;
                if (entry.Symbol is not TypeSymbol { Kind: TypeKind.EnumStruct }) continue;
                if (entry.Node is not EnumStructDeclarationASTNode enumNode) continue;
                CheckCaseNames(env, enumNode);
                ResolveDiscriminants(env, enumNode);
                foreach (var caseNode in enumNode.Cases)
                {
                    CheckHoleExclusivity(env, caseNode);
                }
            }
        }

        // ===== case 名唯一性复核（§12；Parser 已拦，防御，正常编译不可到达）=====

        private static void CheckCaseNames(ResolveEnvironment env, EnumStructDeclarationASTNode enumNode)
        {
            var seen = new HashSet<string>();
            foreach (var caseNode in enumNode.Cases)
            {
                if (!seen.Add(caseNode.CaseName))
                {
                    env.Error(caseNode.Span, $"Duplicate enum case name: '{caseNode.CaseName}'");
                }
            }
        }

        // ===== 判别值落定（显式值写符号；auto 保持 null）=====

        private static void ResolveDiscriminants(ResolveEnvironment env, EnumStructDeclarationASTNode enumNode)
        {
            var seen = new HashSet<long>();
            foreach (var caseNode in enumNode.Cases)
            {
                // auto（无 -> N）：符号保持 null，编号归发射侧按声明序推导（§12.4）
                if (caseNode.DiscriminantValue is not { } value) continue;
                // 防御复核（Parser 已拦：非负、同 enum 内唯一）
                if (value < 0)
                {
                    env.Error(caseNode.Span, "Enum discriminant value must be non-negative");
                }
                else if (!seen.Add(value))
                {
                    env.Error(caseNode.Span, $"Duplicate enum discriminant value: {value}");
                }
                if (env.Declarations.SymbolOf(caseNode) is EnumCaseSymbol symbol)
                {
                    symbol.Discriminant = value;
                }
            }
        }

        // ===== 洞独占性（§12.1：`_` 必须独占一个实参位置）=====

        private static void CheckHoleExclusivity(ResolveEnvironment env, EnumCaseASTNode caseNode)
        {
            foreach (var arg in caseNode.Arguments)
            {
                // 实参根就是单段 `_` 路径 = 参数洞（可具名 name = _），合法
                if (IsHoleArgument(arg.Value)) continue;
                // `_` 出现在表达式内部（someExpression(_) 形态）→ 诊断
                if (ContainsNestedHole(arg.Value))
                {
                    env.Error(arg.Span ?? caseNode.Span,
                        "Enum case hole '_' must occupy an entire argument position");
                }
            }
        }

        // 单段 `_` 路径识别：无表达式底座、无泛型实参、无后缀、无路径段
        // （与 switch 占位同一形态，S7d SwitchVisitors.ContainsPlaceholder 先例）
        private static bool IsHolePath(ASTNode node)
        {
            return node is PathExpressionASTNode path
                && path.Head.Expression == null && path.Head.Name == "_"
                && path.Head.GenericArguments.Count == 0
                && path.Head.Suffixes.Count == 0 && path.Segments.Count == 0;
        }

        private static bool IsHoleArgument(ExpressionRootASTNode root)
        {
            return root.IsAttached && IsHolePath(root.Expression);
        }

        // 递归查找表达式内部的 `_`；switch pattern 子树排除（pattern 的 `_`
        // 是占位语义，P3 绑定有独立 selector 上下文，与模板洞无关）
        private static bool ContainsNestedHole(ASTNode node)
        {
            if (IsHolePath(node)) return true;
            foreach (var (child, _) in AstStructureReflection.EnumerateChildren(node))
            {
                if (node is SwitchCaseASTNode switchCase && ReferenceEquals(child, switchCase.Pattern))
                {
                    continue;
                }
                if (ContainsNestedHole(child)) return true;
            }
            return false;
        }
    }
}
