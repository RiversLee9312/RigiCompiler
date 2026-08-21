namespace RigiCompiler
{
    // 局部变量 DA 的循环出口（W7，对齐 W1 字段 DA / §21.8 loop.rev）：
    // 循环出口 = 全部出环路径交集。do-while 无 break 仍取体尾；
    // break/break@label 出环点与其它出口取交；while/for 含循环前态
    //（体可能零次）；finally 赋值叠到出环点。绑体完成后覆盖 FlowState。
    internal static class LocalLoopDa
    {
        public static HashSet<LocalSymbol> ExitAfter(BoundLoop loop, HashSet<LocalSymbol> before)
        {
            var path = new PathState(CopyOf(before));
            AnalyzeBlock(loop.Body, path);
            var exits = new List<HashSet<LocalSymbol>>();
            if (loop.Kind == LoopKind.DoWhile)
            {
                if (path.FallsThrough) exits.Add(CopyOf(path.Assigned));
                exits.AddRange(TakeJumps(path.Continues, loop));
            }
            else
            {
                exits.Add(CopyOf(before));
                TakeJumps(path.Continues, loop);
            }
            exits.AddRange(TakeJumps(path.Breaks, loop));
            if (exits.Count == 0) return CopyOf(before);
            return IntersectAll(exits);
        }

        private sealed class PathState
        {
            public HashSet<LocalSymbol> Assigned { get; }
            public bool FallsThrough { get; set; } = true;
            public Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> Breaks { get; } =
                new Dictionary<BoundLoop, List<HashSet<LocalSymbol>>>();
            public Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> Continues { get; } =
                new Dictionary<BoundLoop, List<HashSet<LocalSymbol>>>();

            public PathState(HashSet<LocalSymbol> assigned)
            {
                Assigned = assigned;
            }

            public PathState Fork()
            {
                return new PathState(CopyOf(Assigned));
            }
        }

        private static HashSet<LocalSymbol> CopyOf(HashSet<LocalSymbol> set)
        {
            return new HashSet<LocalSymbol>(set);
        }

        private static HashSet<LocalSymbol> IntersectAll(List<HashSet<LocalSymbol>> sets)
        {
            var merged = CopyOf(sets[0]);
            for (var i = 1; i < sets.Count; i++) merged.IntersectWith(sets[i]);
            return merged;
        }

        private static void AnalyzeBlock(BoundBlock block, PathState path)
        {
            foreach (var statement in block.Statements)
            {
                if (!path.FallsThrough) return;
                AnalyzeStatement(statement, path);
            }
        }

        private static void AbsorbJumps(PathState dest, PathState src)
        {
            AbsorbJumpMap(dest.Breaks, src.Breaks);
            AbsorbJumpMap(dest.Continues, src.Continues);
        }

        private static void AbsorbJumpMap(
            Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> dest,
            Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> src)
        {
            foreach (var pair in src)
            {
                if (!dest.TryGetValue(pair.Key, out var list))
                {
                    dest[pair.Key] = new List<HashSet<LocalSymbol>>(pair.Value);
                }
                else
                {
                    list.AddRange(pair.Value);
                }
            }
        }

        private static List<HashSet<LocalSymbol>> TakeJumps(
            Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> map, BoundLoop loop)
        {
            return map.Remove(loop, out var list) ? list : new List<HashSet<LocalSymbol>>();
        }

        private static void RecordJump(
            Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> map, BoundLoop loop,
            HashSet<LocalSymbol> assigned)
        {
            if (!map.TryGetValue(loop, out var list))
            {
                list = new List<HashSet<LocalSymbol>>();
                map[loop] = list;
            }
            list.Add(CopyOf(assigned));
        }

        private static void DelayJumpsThroughFinally(PathState jumps, BoundBlock finallyBlock)
        {
            var extra = new PathState(new HashSet<LocalSymbol>());
            DelayJumpMap(jumps.Breaks, finallyBlock, extra);
            DelayJumpMap(jumps.Continues, finallyBlock, extra);
            AbsorbJumps(jumps, extra);
        }

        private static void DelayJumpMap(
            Dictionary<BoundLoop, List<HashSet<LocalSymbol>>> map, BoundBlock finallyBlock,
            PathState extra)
        {
            foreach (var loop in map.Keys.ToList())
            {
                var delayed = new List<HashSet<LocalSymbol>>();
                foreach (var snapshot in map[loop])
                {
                    var finallyPath = new PathState(CopyOf(snapshot));
                    AnalyzeBlock(finallyBlock, finallyPath);
                    if (finallyPath.FallsThrough) delayed.Add(CopyOf(finallyPath.Assigned));
                    AbsorbJumps(extra, finallyPath);
                }
                map[loop] = delayed;
            }
        }

        private static void MergeFallThrough(PathState path, List<HashSet<LocalSymbol>> falls)
        {
            if (falls.Count == 0)
            {
                path.FallsThrough = false;
                return;
            }
            var merged = IntersectAll(falls);
            path.Assigned.Clear();
            path.Assigned.UnionWith(merged);
        }

        private static void AnalyzeStatement(BoundStatement statement, PathState path)
        {
            switch (statement)
            {
                case BoundAssignmentStatement assignment:
                    MarkTarget(assignment.Target, path.Assigned);
                    ScanExpression(assignment.Value, path.Assigned);
                    break;
                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer != null)
                    {
                        ScanExpression(declaration.Initializer, path.Assigned);
                        path.Assigned.Add(declaration.Local);
                    }
                    break;
                case BoundDestructuringDeclarationStatement destructuring:
                    ScanExpression(destructuring.Initializer, path.Assigned);
                    foreach (var (local, _) in destructuring.Entries)
                    {
                        path.Assigned.Add(local);
                    }
                    break;
                case BoundExpressionStatement expressionStatement:
                    ScanExpression(expressionStatement.Expression, path.Assigned);
                    break;
                case BoundIfStatement ifStatement:
                    ScanExpression(ifStatement.Condition, path.Assigned);
                    var truePath = path.Fork();
                    AnalyzeBlock(ifStatement.TrueBlock, truePath);
                    var falsePath = path.Fork();
                    if (ifStatement.FalseBlock != null)
                    {
                        AnalyzeBlock(ifStatement.FalseBlock, falsePath);
                    }
                    AbsorbJumps(path, truePath);
                    AbsorbJumps(path, falsePath);
                    var ifFalls = new List<HashSet<LocalSymbol>>();
                    if (truePath.FallsThrough) ifFalls.Add(truePath.Assigned);
                    if (falsePath.FallsThrough) ifFalls.Add(falsePath.Assigned);
                    MergeFallThrough(path, ifFalls);
                    break;
                case BoundSwitchStatement switchStatement:
                    ScanExpression(switchStatement.Selector, path.Assigned);
                    var switchFalls = new List<HashSet<LocalSymbol>>();
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        var casePath = path.Fork();
                        AnalyzeBlock(switchCase.Body, casePath);
                        AbsorbJumps(path, casePath);
                        if (casePath.FallsThrough) switchFalls.Add(casePath.Assigned);
                    }
                    var defaultPath = path.Fork();
                    AnalyzeBlock(switchStatement.DefaultBody, defaultPath);
                    AbsorbJumps(path, defaultPath);
                    if (defaultPath.FallsThrough) switchFalls.Add(defaultPath.Assigned);
                    MergeFallThrough(path, switchFalls);
                    break;
                case BoundLoop inner:
                    var innerBefore = CopyOf(path.Assigned);
                    var bodyPath = path.Fork();
                    AnalyzeBlock(inner.Body, bodyPath);
                    AbsorbJumps(path, bodyPath);
                    var innerExits = new List<HashSet<LocalSymbol>>();
                    if (inner.Kind == LoopKind.DoWhile)
                    {
                        if (bodyPath.FallsThrough) innerExits.Add(CopyOf(bodyPath.Assigned));
                        innerExits.AddRange(TakeJumps(path.Continues, inner));
                    }
                    else
                    {
                        innerExits.Add(innerBefore);
                        TakeJumps(path.Continues, inner);
                    }
                    innerExits.AddRange(TakeJumps(path.Breaks, inner));
                    MergeFallThrough(path, innerExits);
                    break;
                case BoundTryStatement tryStatement:
                    AnalyzeTry(tryStatement, path);
                    break;
                case BoundSeqStatement seqStatement:
                    var seqPath = path.Fork();
                    AnalyzeBlock(seqStatement.Body, seqPath);
                    AbsorbJumps(path, seqPath);
                    if (!seqPath.FallsThrough
                        && (seqPath.Breaks.Count > 0 || seqPath.Continues.Count > 0))
                    {
                        path.FallsThrough = false;
                    }
                    break;
                case BoundBlock nested:
                    AnalyzeBlock(nested, path);
                    break;
                case BoundReturnStatement:
                    path.FallsThrough = false;
                    break;
                case BoundThrowStatement throwStatement:
                    ScanExpression(throwStatement.Exception, path.Assigned);
                    path.FallsThrough = false;
                    break;
                case BoundLoopControl control:
                    if (control.IsBreak) RecordJump(path.Breaks, control.Target, path.Assigned);
                    else RecordJump(path.Continues, control.Target, path.Assigned);
                    path.FallsThrough = false;
                    break;
                case BoundSeqExitStatement:
                    path.FallsThrough = false;
                    break;
                case BoundReturnValueStatement returnValue:
                    ScanExpression(returnValue.Value, path.Assigned);
                    path.FallsThrough = false;
                    break;
            }
        }

        private static void AnalyzeTry(BoundTryStatement tryStatement, PathState path)
        {
            var tryPath = path.Fork();
            AnalyzeBlock(tryStatement.TryBlock, tryPath);
            var tryFalls = new List<HashSet<LocalSymbol>>();
            if (tryPath.FallsThrough) tryFalls.Add(CopyOf(tryPath.Assigned));
            var tryJumps = new PathState(new HashSet<LocalSymbol>());
            AbsorbJumps(tryJumps, tryPath);
            foreach (var catchClause in tryStatement.Catches)
            {
                var catchPath = path.Fork();
                if (catchClause.Variable != null) catchPath.Assigned.Add(catchClause.Variable);
                AnalyzeBlock(catchClause.Body, catchPath);
                if (catchPath.FallsThrough) tryFalls.Add(CopyOf(catchPath.Assigned));
                AbsorbJumps(tryJumps, catchPath);
            }
            if (tryStatement.FinallyBlock != null)
            {
                var finallyEntry = tryFalls.Count > 0
                    ? IntersectAll(tryFalls)
                    : CopyOf(path.Assigned);
                var finallyPath = new PathState(CopyOf(finallyEntry));
                if (tryStatement.FinallyVariable != null)
                {
                    finallyPath.Assigned.Add(tryStatement.FinallyVariable);
                }
                AnalyzeBlock(tryStatement.FinallyBlock, finallyPath);
                AbsorbJumps(tryJumps, finallyPath);
                if (tryFalls.Count > 0 && finallyPath.FallsThrough)
                {
                    path.Assigned.Clear();
                    path.Assigned.UnionWith(finallyPath.Assigned);
                    if (tryStatement.FinallyVariable != null)
                    {
                        path.Assigned.Remove(tryStatement.FinallyVariable);
                    }
                }
                else
                {
                    path.FallsThrough = false;
                }
                DelayJumpsThroughFinally(tryJumps, tryStatement.FinallyBlock);
            }
            else
            {
                MergeFallThrough(path, tryFalls);
            }
            AbsorbJumps(path, tryJumps);
        }

        private static void MarkTarget(BoundExpression target, HashSet<LocalSymbol> assigned)
        {
            if (target is BoundValueReferenceExpression { Symbol: LocalSymbol local })
            {
                assigned.Add(local);
            }
        }

        private static void ScanExpression(BoundExpression expression, HashSet<LocalSymbol> assigned)
        {
            if (expression is BoundLambdaExpression) return;
            if (expression is BoundCompoundAssignmentExpression compound)
            {
                MarkTarget(compound.Target, assigned);
            }
            foreach (var child in BoundAnalysis.ChildExpressions(expression))
            {
                ScanExpression(child, assigned);
            }
        }
    }
}
