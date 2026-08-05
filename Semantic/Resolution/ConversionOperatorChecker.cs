namespace LatteCompiler
{
    // ===== castTo/castFrom 声明形状检查（SYNTAX §3.5，S8f）=====
    //
    // 前置：参数/返回类型已由 TypeReferenceResolver 解析（形状判定依赖
    // ReturnType 非 void 与参数个数）。转换运算符的形态规范（§3.5 示例）：
    //   operator castTo\<TTarget>(): TTarget            // 源类型上，零参数
    //   operator castFrom\<TSource>(obj: TSource): Celsius   // 目标类型上，恰一参数
    // 形状违反 = 该运算符在任意 cast 使用点都不可能被名字分析选中（死声明），
    // 使用点不可判（编译单元外还有消费方），在声明处拒绝。
    //
    // 只检查 Kind == Operator 且名为 castTo/castFrom 的方法；普通函数同名
    // 不是转换运算符（名字分析只搜 operator），不检查。
    internal sealed class ConversionOperatorChecker : ResolverVisitor<ConversionOperatorChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                if (entry.Symbol is not MethodSymbol
                    { Kind: MethodKind.Operator } method) continue;
                switch (method.Name)
                {
                    case "castTo":
                        // 源类型上取 this 作转换源，目标由 TTarget 决定——必须零参数
                        if (method.Parameters.Count != 0)
                        {
                            env.Error(entry.Node.Span,
                                $"Operator 'castTo' must have no parameters");
                        }
                        // 无返回类型（void）时转换没有产物，名字分析恒不可用
                        if (method.ReturnType == null)
                        {
                            env.Error(entry.Node.Span,
                                $"Operator 'castTo' must declare a return type");
                        }
                        break;
                    case "castFrom":
                        // 目标类型上接受源值作唯一参数——必须恰一参数
                        if (method.Parameters.Count != 1)
                        {
                            env.Error(entry.Node.Span,
                                $"Operator 'castFrom' must have exactly one parameter");
                        }
                        if (method.ReturnType == null)
                        {
                            env.Error(entry.Node.Span,
                                $"Operator 'castFrom' must declare a return type");
                        }
                        break;
                }
            }
        }
    }
}
