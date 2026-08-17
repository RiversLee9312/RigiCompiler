using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 语句发射（S6–S8c；BIL §13/§15/§16）。自旧 EmitSession.EmitStatement
    // 各分支迁移，行为不变——无产物（Unit），副作用填充 target 指令流；
    // 分支/循环/switch/seq/try 的子块追加进 ctx.Function.Blocks。
    // M57 起指令为强类型构造（操作数序由构造签名固定）；值发射产物为
    // BilVariableOperand（§10.1 物化契约）。

    // 局部声明：无初始化器 → 无指令（.vars 已声明）；有初始化器 →
    // 求值物化后经 set.var 写入（§13.2）
    internal sealed class LocalDeclarationEmitter : EmitVisitor<LocalDeclarationEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var decl = (LoweredLocalDeclarationStatement)node;
            if (decl.Initializer != null)
            {
                var initValue = EmitValueDispatcher.Visit(decl.Initializer, target, ctx, env);
                target.Instructions.Add(new SetVarInstruction(
                    initValue, BilOp.Var(decl.Local.Name))
                { Origin = decl });
            }
            return Unit.Value;
        }
    }

    // 赋值：按目标形态发射——局部 set.var（§13.2）/全局·static
    // 字段 set.field.static（§13.4）/实例字段 set.field（§13.3）/
    // 索引 set.array（§13.6，S8c）。求值序（与复合赋值 P4a 脱糖同
    // 规则）：接收者（含容器/索引）先求值、右值后求值——set.field/
    // set.array 的 receiver/index 物化先于 Value 物化；指令操作数序
    // 不变（BIL 文本形态不变，变的只是求值先后）
    internal sealed class AssignmentEmitter : EmitVisitor<AssignmentEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var assignment = (LoweredAssignmentStatement)node;
            // 复合赋值的目标在 P3 保留 SmartCast 包装（收窄类型参与运算
            // 定型），P4a 物化产物为 LoweredCastExpression 包 place——
            // place 判定时剥壳（Operand 本是 place：值引用/字段引用，
            // NarrowKey 稳定链保证其无副作用，剥壳写入即写原 place）
            var place = assignment.Target;
            while (place is LoweredCastExpression castShell) place = castShell.Source;
            // 接收者先行物化（set.field/set.array 两形态；set.var/
            // set.field.static 无 receiver 不动；S11c set.wrapper.field 写
            // place 的宿主同序先行物化）
            BilVariableOperand? writeReceiver = null;
            BilVariableOperand? indexReceiver = null;
            BilVariableOperand? indexOperand = null;
            switch (place)
            {
                case LoweredFieldAccessExpression accessTarget:
                    writeReceiver = EmitValueDispatcher.Visit(accessTarget.Receiver, target,
                        ctx, env);
                    break;
                case LoweredIndexExpression indexTarget:
                    indexReceiver = EmitValueDispatcher.Visit(indexTarget.Receiver, target,
                        ctx, env);
                    indexOperand = EmitValueDispatcher.Visit(indexTarget.Index, target,
                        ctx, env);
                    break;
                case LoweredWrapperFieldExpression wrapperFieldTarget:
                    writeReceiver = EmitValueDispatcher.Visit(wrapperFieldTarget.Receiver, target,
                        ctx, env);
                    break;
            }
            var assignedValue = EmitValueDispatcher.Visit(assignment.Value, target, ctx, env);
            switch (place)
            {
                case LoweredValueReferenceExpression localTarget:
                    // S9d：可变参数写入同样映射隐藏包变量（与读侧共用
                    // EmittingFacility.ValueVariableName——.args 只声明
                    // .vargs./.kwargs. 保留名，写原名即引用未声明变量）
                    target.Instructions.Add(new SetVarInstruction(
                        assignedValue, BilOp.Var(
                            EmittingFacility.ValueVariableName(localTarget.Symbol)))
                    { Origin = assignment });
                    break;
                case LoweredFieldReferenceExpression fieldTarget:
                    var ownerRef = EmittingFacility.FieldOwnerRef(fieldTarget.Field, env);
                    if (ownerRef == null) break;    // 已诊断
                    target.Instructions.Add(new SetFieldStaticInstruction(
                        assignedValue, BilOp.Type(ownerRef),
                        BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldTarget.Field)))
                    { Origin = assignment });
                    break;
                case LoweredFieldAccessExpression accessTarget:
                    // 实例字段写入（§13.3：set.field SOURCE OBJECT
                    // field(F)——OBJECT 先于 SOURCE 求值，操作数序不变）
                    target.Instructions.Add(new SetFieldInstruction(
                        assignedValue, writeReceiver!,
                        BilOp.Field(CanonicalSymbolPrinter.PrintField(accessTarget.Field)))
                    { Origin = assignment });
                    break;
                case LoweredIndexExpression:
                    // 索引写入（S8c，§13.6：set.array COLLECTION INDEX
                    // ELEMENT——COLLECTION/INDEX 先于 ELEMENT 求值，
                    // 操作数序不变）
                    target.Instructions.Add(new SetArrayInstruction(
                        indexReceiver!, indexOperand!, assignedValue)
                    { Origin = assignment });
                    break;
                case LoweredWrapperFieldExpression wrapperFieldTarget:
                    // wrapper place 字段写入（§13.3：set.wrapper.field 链——
                    // 宿主先行物化（上方），SOURCE 后求值；非普通 set.field）
                    WrapperFieldEmission.EmitWrite(wrapperFieldTarget, assignedValue,
                        writeReceiver!, target, ctx, env);
                    break;
                default:
                    // P3 已强制赋值目标为 place（值引用/字段引用/索引访问）
                    throw new CompilerInternalException(
                        "非法赋值目标: " + place.GetType().Name);
            }
            return Unit.Value;
        }
    }

    // 表达式语句：求值结果物化到临时变量后丢弃（SYNTAX §4 无隐式返回值利用）
    internal sealed class ExpressionStatementEmitter
        : EmitVisitor<ExpressionStatementEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var expressionStatement = (LoweredExpressionStatement)node;
            EmitValueDispatcher.Visit(expressionStatement.Expression, target, ctx, env);
            return Unit.Value;
        }
    }

    // void 调用语句：实参从左到右物化（§10.2），再发 invoke.noret（§15.1）；
    // 实例调用（S7c-2）receiver 求值作首实参；S9e 泛型实参在 receiver
    // 之后、普通实参之前（§7.2 调用序）；S9d-2 泛型可变包在固定泛型之后
    internal sealed class CallStatementEmitter : EmitVisitor<CallStatementEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var call = (LoweredCallStatement)node;
            var arguments = new List<BilVariableOperand>();
            if (call.Receiver != null)
            {
                arguments.Add(EmitValueDispatcher.Visit(call.Receiver, target, ctx, env));
            }
            foreach (var typeArgument in call.TypeArguments)
            {
                arguments.Add(EmittingFacility.MaterializeTypeId(typeArgument, call,
                    target, ctx, env));
            }
            if (call.GenericPack != null)
            {
                arguments.Add(GenericVarArgsEmitter.Visit(call.GenericPack, target, ctx, env));
            }
            foreach (var argument in call.Arguments)
            {
                arguments.Add(EmitValueDispatcher.Visit(argument, target, ctx, env));
            }
            if (call.IsIndirect)
            {
                // §15.3 间接调用（无结果语句）：物化目标对象后虚调用其 $$call。
                // async call（AsyncAction 族）也有 Task 结果（§15.2
                // fire-and-forget）——发 invoke 产 Task 丢弃，而非 noret
                //（stdlib 缺席时与直接调用同口径降级 noret，仅测试可达）
                var indirectTarget = EmitValueDispatcher.Visit(call.IndirectTarget!,
                    target, ctx, env);
                if (call.Method.IsAsync)
                {
                    var coroutine = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                        .FirstOrDefault(n => n.Name == "core")?.ChildNamespaces
                        .FirstOrDefault(n => n.Name == "coroutine");
                    var task = coroutine?.Types.FirstOrDefault(t => t.Name == "Task"
                        && t.GenericParameters.Count == 0);
                    if (task != null)
                    {
                        var discarded = ctx.Temps.NewTemp(task);
                        target.Instructions.Add(new InvokeIndirectInstruction(indirectTarget,
                            discarded, arguments) { Origin = call });
                        return Unit.Value;
                    }
                }
                target.Instructions.Add(new InvokeIndirectNoResultInstruction(
                    indirectTarget, arguments) { Origin = call });
                return Unit.Value;
            }
            // S10（BIL §15.2）：async 无结果调用语句位置也有 Task 结果
            // （fire-and-forget——丢弃句柄即不与其同步）；发 invoke 产 Task
            // 到临时变量丢弃，而非 invoke.noret。stdlib 缺席的测试驱动
            // 找不到 Task 定义时降级 invoke.noret（既有行为，仅测试可达）
            if (call.Method.IsAsync)
            {
                var core = env.Unit.Symbols.GlobalNamespace.ChildNamespaces
                    .FirstOrDefault(n => n.Name == "core");
                var coroutine = core?.ChildNamespaces
                    .FirstOrDefault(n => n.Name == "coroutine");
                var task = coroutine?.Types.FirstOrDefault(t => t.Name == "Task"
                    && t.GenericParameters.Count == 0);
                if (task != null)
                {
                    var discarded = ctx.Temps.NewTemp(task);
                    target.Instructions.Add(new InvokeInstruction(
                        BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(call.Method)),
                        discarded, arguments)
                    { Origin = call });
                    return Unit.Value;
                }
            }
            target.Instructions.Add(new InvokeNoResultInstruction(
                BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(call.Method)), arguments)
            { Origin = call });
            return Unit.Value;
        }
    }

    // return：无值裸 ret；有值物化后单操作数 ret
    internal sealed class ReturnEmitter : EmitVisitor<ReturnEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var ret = (LoweredReturnStatement)node;
            if (ret.Value == null)
            {
                target.Instructions.Add(new RetInstruction() { Origin = ret });
            }
            else
            {
                var value = EmitValueDispatcher.Visit(ret.Value, target, ctx, env);
                target.Instructions.Add(new RetInstruction(value) { Origin = ret });
            }
            return Unit.Value;
        }
    }

    // 结构化条件（§16.2）：条件物化到临时变量 →
    // if $c blk(then) blk(else)|none $breakid（无 else 用 none 操作数——
    // 模型为可空 ElseBlock；breakid 为 §16.5 推广的末尾 region-exit
    // capability 操作数，与 loop/switch 同位）；
    // 分支 block 加入函数并递归发射，落尾自然返回（§9.4）
    internal sealed class IfEmitter : EmitVisitor<IfEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var ifStatement = (LoweredIfStatement)node;
            var conditionValue = EmitValueDispatcher.Visit(ifStatement.Condition, target, ctx, env);
            var id = "if" + ctx.BlockIds.NextIf();
            var thenBlock = new BilBlock(id + "-then");
            var elseBlock = ifStatement.FalseBlock != null
                ? new BilBlock(id + "-else") : null;
            target.Instructions.Add(new IfInstruction(conditionValue, thenBlock, elseBlock,
                BilOp.Var(ifStatement.BreakId.Name))
            { Origin = ifStatement });
            ctx.Function.Blocks.Add(thenBlock);
            EmitBlockVisitor.Visit(ifStatement.TrueBlock, thenBlock, ctx, env);
            if (elseBlock != null)
            {
                ctx.Function.Blocks.Add(elseBlock);
                EmitBlockVisitor.Visit(ifStatement.FalseBlock!, elseBlock, ctx, env);
            }
            return Unit.Value;
        }
    }

    // 结构化循环（§16.3/§16.4）：条件由 Judge 块写入合成局部，
    // loop $c blk(body) none blk(judge) $breakid（IsRev → loop.rev；
    // 枚举器块恒 none——for 的枚举器在 S7c-2 已脱糖为前置 iterate +
    // Judge/Body 协议调用，不走枚举器块形态）；body/judge
    // block 加入函数并递归发射，落尾自然返回（§9.4 同 if 分支块）
    internal sealed class LoopEmitter : EmitVisitor<LoopEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var loop = (LoweredLoop)node;
            var loopId = "loop" + ctx.BlockIds.NextLoop();
            var loopBodyBlock = new BilBlock(loopId + "-body");
            var judgeBlock = new BilBlock(loopId + "-judge");
            target.Instructions.Add(new LoopInstruction(
                BilOp.Var(loop.Condition.Name), loopBodyBlock, enumBlock: null, judgeBlock,
                BilOp.Var(loop.BreakId.Name), loop.IsRev)
            { Origin = loop });
            ctx.Function.Blocks.Add(loopBodyBlock);
            EmitBlockVisitor.Visit(loop.Body, loopBodyBlock, ctx, env);
            ctx.Function.Blocks.Add(judgeBlock);
            EmitBlockVisitor.Visit(loop.Judge, judgeBlock, ctx, env);
            return Unit.Value;
        }
    }

    // break（§16.5）：直接引用目标 region 的 breakid（Stage B 拆分；
    // StructuredExitRouting 的 region 收尾/dispatcher relay 同走本发射）
    internal sealed class BreakEmitter : EmitVisitor<BreakEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var breakStatement = (LoweredBreakStatement)node;
            target.Instructions.Add(new BreakInstruction(BilOp.Var(breakStatement.BreakId.Name))
            { Origin = breakStatement });
            return Unit.Value;
        }
    }

    // continue（§16.5）：直接引用目标循环的 breakid（Stage B 拆分）
    internal sealed class ContinueEmitter : EmitVisitor<ContinueEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var continueStatement = (LoweredContinueStatement)node;
            target.Instructions.Add(
                new ContinueInstruction(BilOp.Var(continueStatement.BreakId.Name))
                { Origin = continueStatement });
            return Unit.Value;
        }
    }

    // 常量 switch（§16.6）：selector 物化 + §19.4 常量表资源，
    // switch $s res(T) [blk(item)...] blk(default) $breakid
    // （操作数序即规范排版序）；item/default block 加入函数
    // 并递归发射，落尾自然返回（§9.4 同 if 分支块）
    internal sealed class SwitchEmitter : EmitVisitor<SwitchEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var sw = (LoweredSwitch)node;
            var selectorValue = EmitValueDispatcher.Visit(sw.Selector, target, ctx, env);
            var tableResource = EmittingFacility.RegisterSwitchTable(sw, env);
            var switchId = "switch" + ctx.BlockIds.NextSwitch();
            var itemBlocks = new List<BilBlock>();
            for (var i = 0; i < sw.Cases.Count; i++)
            {
                itemBlocks.Add(new BilBlock(switchId + "-item" + i));
            }
            var defaultBlock = new BilBlock(switchId + "-default");
            target.Instructions.Add(new SwitchInstruction(
                selectorValue, tableResource, itemBlocks, defaultBlock,
                BilOp.Var(sw.BreakId.Name))
            { Origin = sw });
            for (var i = 0; i < sw.Cases.Count; i++)
            {
                ctx.Function.Blocks.Add(itemBlocks[i]);
                EmitBlockVisitor.Visit(sw.Cases[i].Body, itemBlocks[i], ctx, env);
            }
            ctx.Function.Blocks.Add(defaultBlock);
            EmitBlockVisitor.Visit(sw.DefaultBody, defaultBlock, ctx, env);
            return Unit.Value;
        }
    }

    // throw（§16.9）：异常值物化后单操作数抛出
    internal sealed class ThrowEmitter : EmitVisitor<ThrowEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var throwStatement = (LoweredThrowStatement)node;
            var exceptionValue = EmitValueDispatcher.Visit(throwStatement.Exception, target,
                ctx, env);
            target.Instructions.Add(new ThrowInstruction(exceptionValue)
            { Origin = throwStatement });
            return Unit.Value;
        }
    }

    // seq 块（S7e，§3.4/§16.1）：独立 block + call blk(seqN) $breakid
    // （call 不建栈帧，block 落尾自然返回续call 的下一条；breakid 为
    // §16.5 推广的末尾 region-exit capability 操作数）；
    // volatile → §9.6 block 修饰符
    internal sealed class SeqBlockEmitter : EmitVisitor<SeqBlockEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var seqBlock = (LoweredSeqBlock)node;
            var seqId = "seq" + ctx.BlockIds.NextSeq();
            var seqBilBlock = seqBlock.IsVolatile
                ? new BilBlock(seqId, BilBlockModifier.Volatile)
                : new BilBlock(seqId);
            target.Instructions.Add(new CallBlockInstruction(seqBilBlock,
                BilOp.Var(seqBlock.BreakId.Name))
            { Origin = seqBlock });
            ctx.Function.Blocks.Add(seqBilBlock);
            EmitBlockVisitor.Visit(seqBlock.Body, seqBilBlock, ctx, env);
            return Unit.Value;
        }
    }

    // try（S7e，§16.7 五操作数）：blk(tryN-body) $slot
    // res(catch-table) blk(tryN-finally)|none $breakid（breakid 为
    // §16.5 推广的末尾 region-exit capability 操作数）；catch 表 =
    // §19.5 多行资源（元素 type(T) -> blk(tryN-catchI)，
    // 保序——表序即匹配序）；body/catch/finally block 加入
    // 函数并递归发射，落尾自然返回（§9.4 同 if 分支块）
    internal sealed class TryEmitter : EmitVisitor<TryEmitter, Unit>
    {
        protected override Unit VisitCore(LoweredNode node, BilBlock target, EmitContext ctx,
            EmitEnvironment env)
        {
            var tryStatement = (LoweredTryStatement)node;
            var tryId = "try" + ctx.BlockIds.NextTry();
            var tryBodyBlock = new BilBlock(tryId + "-body");
            var catchBlocks = new List<BilBlock>();
            for (var i = 0; i < tryStatement.Catches.Count; i++)
            {
                catchBlocks.Add(new BilBlock(tryId + "-catch" + i));
            }
            var catchTable = EmittingFacility.RegisterCatchTable(tryStatement, catchBlocks, env);
            var finallyBilBlock = tryStatement.FinallyBlock != null
                ? new BilBlock(tryId + "-finally") : null;
            target.Instructions.Add(new TryInstruction(
                tryBodyBlock, BilOp.Var(tryStatement.ExceptionSlot.Name),
                catchTable, finallyBilBlock, BilOp.Var(tryStatement.BreakId.Name))
            { Origin = tryStatement });
            ctx.Function.Blocks.Add(tryBodyBlock);
            EmitBlockVisitor.Visit(tryStatement.TryBlock, tryBodyBlock, ctx, env);
            for (var i = 0; i < tryStatement.Catches.Count; i++)
            {
                ctx.Function.Blocks.Add(catchBlocks[i]);
                EmitBlockVisitor.Visit(tryStatement.Catches[i].Body, catchBlocks[i], ctx, env);
            }
            if (finallyBilBlock != null)
            {
                ctx.Function.Blocks.Add(finallyBilBlock);
                EmitBlockVisitor.Visit(tryStatement.FinallyBlock!, finallyBilBlock, ctx, env);
            }
            return Unit.Value;
        }
    }
}
