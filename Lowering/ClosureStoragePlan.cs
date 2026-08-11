using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // 闭包存储计划（SYNTAX §5.2，P4a）：一个函数体内「符号 → 存储形态」的
    // 判定表。三种形态：
    // - 默认（无条目）：普通局部/参数，BIL 变量直存；
    // - CellLocal：本函数的被捕获局部/参数——存储是函数级 cell 变量
    //   （局部 = 原名 var，.vars 类型为 .cell<T>/.readonly_cell<T>；参数 =
    //   .c.<名> 合成局部，prologue 在函数入口用实参构造）；读 = getValue
    //   调用，写 = setValue 调用；
    // - ClosureField：lambda $$call 体内的外层捕获符号——cell 在隐藏类
    //   this 的 .capture.* 字段里；读 = get.field + getValue，写 =
    //   get.field + setValue，cell 对象引用 = get.field（this 捕获例外：
    //   普通字段，无 cell 包装）。
    // 读写改写全部复用现有 Lowered 节点（实例调用/字段访问/new），
    // BIL 无闭包特例指令。
    internal sealed class ClosureStoragePlan
    {
        private sealed class Entry
        {
            // CellLocal 的源符号（局部/参数；ClosureField 条目为 null）
            public SemanticSymbol? Symbol;
            public bool IsReadOnly;
            public TypeSymbol CellType = null!;
            // CellLocal：参数的 cell synth 局部（局部为 null——cell 变量即原名）
            public LocalSymbol? ParamCellLocal;
            // ClosureField：隐藏类捕获条目
            public LambdaCaptureEntry? Capture;
        }

        private readonly Dictionary<SemanticSymbol, Entry> entries =
            new Dictionary<SemanticSymbol, Entry>();
        private readonly List<LoweredStatement> prologue = new List<LoweredStatement>();

        private readonly MethodSymbol? cellGetValue;
        private readonly MethodSymbol? cellSetValue;
        private readonly MethodSymbol? readonlyCellGetValue;
        // stdlib Cell 族可用性（缺 stdlib 的测试驱动降级：P3 已就
        // 「lambda 需要 core::Func/Action 族」落诊断，P4 全部查询走默认
        // 路径——不抛出、不产生二次噪音）
        private readonly bool available;

        // 函数入口 prologue（被捕获参数的 cell 构造语句，前插到体首）
        public IReadOnlyList<LoweredStatement> Prologue => prologue;

        private ClosureStoragePlan(CompilationUnit unit)
        {
            cellGetValue = CallableModel.FindCellGetValue(unit, readOnly: false);
            cellSetValue = CallableModel.FindCellSetValue(unit);
            readonlyCellGetValue = CallableModel.FindCellGetValue(unit, readOnly: true);
            available = cellGetValue != null && cellSetValue != null
                && readonlyCellGetValue != null
                && CallableModel.FindCellInit(unit, readOnly: false, valueInit: true) != null
                && CallableModel.FindCellInit(unit, readOnly: false, valueInit: false) != null
                && CallableModel.FindCellInit(unit, readOnly: true, valueInit: true) != null;
        }

        // 构建判定表：lambda $$call 体先登 closure 条目；再登本函数被捕获的
        // 局部与参数（参数产生 .c.<名> synth 局部与 prologue 构造语句）
        public static ClosureStoragePlan Build(BoundFunctionBody body, LowerContext ctx,
            LowerEnvironment env)
        {
            var plan = new ClosureStoragePlan(env.Unit);
            if (!plan.available) return plan;
            // closure 条目仅对 $$call 体生效——init 体的 this 就是隐藏类本身
            // （逐捕获字段赋值），不得参与捕获改写
            if (body.Method.Owner?.LambdaClosure is { } closure
                && ReferenceEquals(body.Method, closure.Call))
            {
                foreach (var capture in closure.Captures)
                {
                    plan.entries[capture.Symbol] = new Entry
                    {
                        IsReadOnly = capture.IsReadOnly,
                        CellType = capture.Field.FieldType as TypeSymbol
                            ?? env.Unit.Symbols.ErrorType,
                        Capture = capture,
                    };
                }
            }
            foreach (var local in body.Locals)
            {
                if (local.CaptureCell == CaptureCellKind.None) continue;
                var readOnly = local.CaptureCell == CaptureCellKind.ReadonlyCell;
                plan.entries[local] = new Entry
                {
                    Symbol = local,
                    IsReadOnly = readOnly,
                    CellType = CallableModel.ConstructCell(env.Unit, local.Type!, readOnly)
                        ?? env.Unit.Symbols.ErrorType,
                };
            }
            foreach (var parameter in body.Method.Parameters)
            {
                if (parameter.CaptureCell == CaptureCellKind.None) continue;
                var readOnly = parameter.CaptureCell == CaptureCellKind.ReadonlyCell;
                var cellType = CallableModel.ConstructCell(env.Unit, parameter.Type!, readOnly)
                    ?? env.Unit.Symbols.ErrorType;
                var cellLocal = ctx.Synth.NewCaptureCellLocal(parameter.Name, cellType);
                plan.entries[parameter] = new Entry
                {
                    Symbol = parameter,
                    IsReadOnly = readOnly,
                    CellType = cellType,
                    ParamCellLocal = cellLocal,
                };
                // prologue：.c.<名> = new Cell<T>(<实参>)（实参引用直造——
                // 不经值引用降级，避免被本计划的 CellLocal 条目递归拦截）
                var init = CallableModel.FindCellInit(env.Unit, readOnly, valueInit: true);
                plan.prologue.Add(new LoweredLocalDeclarationStatement(body.Body, cellLocal,
                    new LoweredNewExpression(body.Body, init,
                        new List<LoweredExpression>
                        {
                            new LoweredValueReferenceExpression(body.Body, parameter)
                        },
                        cellType)));
            }
            return plan;
        }

        // 目标是否为本计划收管的被捕获值引用（赋值/复合赋值路径的
        // 前置判定——命中时改写必经 TryRewriteCellWrite，不回落默认路径）
        public bool IsCapturedReference(BoundExpression expression)
        {
            return expression is BoundValueReferenceExpression reference
                && entries.ContainsKey(reference.Symbol);
        }

        // 值读取改写（值引用降级路径）：命中条目 → getValue 调用 / this 字段
        // 访问；未命中 → null（调用方走默认 LoweredValueReferenceExpression）
        public LoweredExpression? TryRewriteValueRead(BoundValueReferenceExpression reference)
        {
            if (!entries.TryGetValue(reference.Symbol, out var entry)) return null;
            if (entry.Capture is { IsThis: true } thisCapture)
            {
                // this 捕获：普通字段直读（无 cell 包装）
                return new LoweredFieldAccessExpression(reference,
                    new LoweredThisExpression(reference), thisCapture.Field,
                    thisCapture.Field.FieldType);
            }
            return CellGetValueCall(reference, CellObjectExpression(reference, entry),
                entry.IsReadOnly, reference.Type);
        }

        // this 引用改写（lambda 体内的 BoundThisExpression → .capture.this
        // 字段访问；this 捕获每闭包至多一条）；未命中 → null（默认 $.this）
        public LoweredExpression? TryRewriteThis(BoundThisExpression node)
        {
            foreach (var entry in entries.Values)
            {
                if (entry.Capture is { IsThis: true } capture)
                {
                    return new LoweredFieldAccessExpression(node,
                        new LoweredThisExpression(node), capture.Field,
                        capture.Field.FieldType);
                }
            }
            return null;
        }

        // 赋值改写：目标是被捕获值引用 → setValue 调用语句（const 捕获无写
        // 通道——P3 已拦截，到达此处属内部错误）。value 由调用方先行降级
        public LoweredStatement? TryRewriteAssignment(BoundAssignmentStatement assignment,
            LoweredExpression value)
        {
            return TryRewriteCellWrite(assignment, assignment.Target, value);
        }

        // cell 写入语句（普通赋值与复合赋值写回共用）：目标是被捕获值引用
        // → setValue 调用；未命中 → null
        public LoweredStatement? TryRewriteCellWrite(BoundNode origin, BoundExpression target,
            LoweredExpression value)
        {
            if (target is not BoundValueReferenceExpression reference
                || !entries.TryGetValue(reference.Symbol, out var entry))
            {
                return null;
            }
            if (entry.Capture is { IsThis: true })
            {
                throw new CompilerInternalException("this 不能作为赋值目标: " +
                    reference.Symbol.Name);
            }
            if (entry.IsReadOnly || cellSetValue == null)
            {
                throw new CompilerInternalException(
                    "P3 已拦截的 const 捕获写入到达 P4: " + reference.Symbol.Name);
            }
            return new LoweredCallStatement(origin, cellSetValue,
                new List<LoweredExpression> { value },
                CellObjectExpression(target, entry));
        }

        // 符号的 cell 对象引用（lambda 隐藏类构造的 init 实参位）：当前函数
        // 上下文中该符号的 cell 对象——CellLocal 取 cell 变量、ClosureField
        // 取 this 字段（不调 getValue，cell 对象沿引用传递共享语义）。
        // stdlib 缺席的降级计划中所有符号缺席——HasCellObjectFor 前置判定
        public bool HasCellObjectFor(SemanticSymbol symbol) => entries.ContainsKey(symbol);

        public LoweredExpression CellObjectFor(BoundNode origin, SemanticSymbol symbol)
        {
            if (!entries.TryGetValue(symbol, out var entry))
            {
                throw new CompilerInternalException(
                    "闭包存储计划缺失捕获符号: " + symbol.Name);
            }
            return CellObjectExpression(origin, entry);
        }

        // 当前函数的 this 值表达式（lambda 隐藏类构造的 this 捕获实参位）：
        // lambda 体内 = .capture.this 字段；普通方法体 = $.this（显式带
        // 外层 this 类型——Origin 是 lambda 节点，透传类型是隐藏类）
        public LoweredExpression ThisValueFor(BoundNode origin, TypeSymbol fallbackThisType)
        {
            foreach (var entry in entries.Values)
            {
                if (entry.Capture is { IsThis: true } capture)
                {
                    return new LoweredFieldAccessExpression(origin,
                        new LoweredThisExpression(origin), capture.Field,
                        capture.Field.FieldType);
                }
            }
            return new LoweredThisExpression(origin, fallbackThisType);
        }

        // cell 对象引用：ClosureField = this 字段访问；CellLocal = cell 变量
        //（参数 = .c.<名> synth 局部；局部 = 局部符号本身——.vars 条目类型
        //  在发射侧按 CaptureCell 标记投影为 .cell<T>/.readonly_cell<T>）
        private LoweredExpression CellObjectExpression(BoundNode origin, Entry entry)
        {
            if (entry.Capture is { } capture)
            {
                return new LoweredFieldAccessExpression(origin,
                    new LoweredThisExpression(origin), capture.Field, entry.CellType);
            }
            return new LoweredCellReferenceExpression(origin,
                (SemanticSymbol?)entry.ParamCellLocal ?? entry.Symbol!, entry.CellType);
        }

        private LoweredExpression CellGetValueCall(BoundNode origin,
            LoweredExpression cellObject, bool readOnly, SemanticSymbol elementType)
        {
            var getValue = readOnly ? readonlyCellGetValue : cellGetValue;
            if (getValue == null)
            {
                throw new CompilerInternalException("stdlib core::Cell 族缺失（getValue）");
            }
            return new LoweredInstanceCallExpression(origin, cellObject, getValue,
                new List<LoweredExpression>(), elementType);
        }
    }
}
