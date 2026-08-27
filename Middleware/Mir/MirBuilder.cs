using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// MirBuilder（MW3 层）：BIL 结构化块 → MIR CFG 的确定性直译
    /// （MIDDLEWARE_ARCHITECTURE §3：输入已结构化，无需 Relooper/Stackifier，
    /// 输出天然 reducible CFG）。输入已过 BilVerifier 门禁，故形状不变量
    /// （恰一 entrypoint block、break/continue token 作用域、块恰好被一个
    /// 父 region 引用等）直接依赖，不复查。构建顺序由 MirReachability 给出
    /// （调用图可达闭包，模块级 DCE）。
    /// </summary>
    public static class MirBuilder
    {
        public static MirModule Build(MwContext context)
        {
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var order = new List<MirFunction>();
            var (buildOrder, tentativeInitFamily) = MirReachability.ResolveBuildOrder(context);
            foreach (var symbol in buildOrder)
            {
                try
                {
                    order.Add(BuildFunction(context, bySymbol[symbol]));
                }
                catch (MwNotSupportedException ex) when (tentativeInitFamily.Contains(symbol))
                {
                    Logger.Verbose("Middleware",
                        "试探性跳过 init 族 fn（new.indirect 保守边）: "
                        + symbol + ": " + ex.Message);
                }
            }
            var module = new MirModule(order);
            context.Mir = module;
            return module;
        }

        private static MirFunction BuildFunction(MwContext context, BilFunction bilFn)
        {
            var symbol = context.Symbols.FindMember(bilFn.Symbol)
                ?? throw new MwNotSupportedException(
                    $"MW1 不支持无符号段声明的 fn（预定义合成体）: {bilFn.Symbol}");

            // .args：.return 在前，其后按 §7.2 序登记——.this / 固定泛型
            // .generic.T（.typeid）/ 泛型包 .generic.<Pack>（.array/.map）/
            // 普通参数 / .vargs. / .kwargs.。包与固定泛型同走声明 TypeRef
            // 落槽（包 = 胖引用）；其余未知隐藏参数形态拒绝
            MirType? returnType = null;
            var parameters = new List<MirLocal>();
            var locals = new List<MirLocal>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var arg in bilFn.Args)
            {
                if (arg.Name == ".return")
                {
                    returnType = MirType.Of(arg.TypeRef);
                    continue;
                }
                if (arg.Name == ".this")
                {
                    // .this 必须在普通参数之前（BIL .args 序保证：.return 后首条）
                    if (parameters.Count != 0)
                    {
                        throw new CompilerInternalException(
                            $".this 不在参数表首位（fn {bilFn.Symbol}）");
                    }
                    AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
                    continue;
                }
                if (IsAdmittedHiddenArg(arg.Name))
                {
                    AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
                    continue;
                }
                if (arg.Name.StartsWith('.'))
                {
                    throw new MwNotSupportedException(
                        $"MW5 不支持隐藏参数 {arg.Name}（fn {bilFn.Symbol}）");
                }
                AddLocal(parameters, locals, seen, arg.Name, arg.TypeRef, bilFn.Symbol);
            }
            if (returnType == null)
            {
                throw new CompilerInternalException($"fn 缺 .return 条目: {bilFn.Symbol}");
            }

            foreach (var varDecl in bilFn.Vars)
            {
                AddLocal(null, locals, seen, varDecl.Name, varDecl.TypeRef, bilFn.Symbol);
            }

            // §9.4：恰一个 entrypoint block（verifier 保证）；防御
            BilBlock? entryBlock = null;
            foreach (var block in bilFn.Blocks)
            {
                if (block.Modifiers.Contains(BilBlockModifier.Entrypoint))
                {
                    entryBlock = block;
                    break;
                }
            }
            if (entryBlock == null)
            {
                throw new CompilerInternalException($"fn 缺 entrypoint block: {bilFn.Symbol}");
            }

            var localMap = new Dictionary<string, MirLocal>(System.StringComparer.Ordinal);
            foreach (var local in locals)
            {
                localMap.Add(local.Name, local);
            }
            var flowBuilder = new FlowBuilder(context, bilFn.Symbol, localMap, returnType);
            var blocks = flowBuilder.Build(entryBlock);
            // try 展开期登记的合成局部（$mw.exc.N 等）并入 fn 局部表
            locals.AddRange(flowBuilder.SyntheticLocals);

            var isEntrypoint = symbol.HasKeyword(BilKeyword.Entrypoint);
            return new MirFunction(symbol, returnType, parameters, locals, blocks, isEntrypoint);
        }

        // cast 子集判定的内建标量/String 键
        internal static bool IsScalarOrString(MirType type) =>
            type.Key is "bool" or "char"
                or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double" or "String";

        // VM TryNumericCast 目标集（含 char；不含 bool）
        internal static bool IsNumericScalar(MirType type) =>
            type.Key is "char"
                or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64"
                or "float" or "double";

        // ===== super/init/访问器解析（MirReachability 可达性共用） =====

        // fn(..super) 解析（VM ResolveSuper 同口径）：super init → 直接
        // 基类 init 按实参静态类型精确匹配；super 方法 → 基类链同名签名键
        internal static MwMemberSymbol ResolveSuperCall(MwContext context, string currentFnSymbol,
            IReadOnlyList<string> argTypeRefs)
        {
            var current = context.Symbols.FindMember(currentFnSymbol)
                ?? throw new CompilerInternalException($"super 所在 fn 无符号: {currentFnSymbol}");
            var baseRef = current.Owner?.Declaration.ExtendsType
                ?? throw new MwNotSupportedException($"super 所在 fn 无直接基类: {currentFnSymbol}");
            var baseType = context.Symbols.FindTypeByRef(baseRef)
                ?? throw new MwNotSupportedException($"super 基类声明缺失: {baseRef}");
            if (current.HasKeyword(BilKeyword.Init))
            {
                // argTypeRefs 首位是 .this 接收者，重载匹配跳过
                return ResolveInit(context.Symbols, baseType, argTypeRefs, skipReceiver: 1,
                    constructedTypeRef: baseRef);
            }
            var key = current.SignatureKey;
            for (var type = baseType; type != null; type = BaseOf(context.Symbols, type))
            {
                foreach (var member in type.Members)
                {
                    if (member.IsVirtualMember && member.SignatureKey == key)
                    {
                        return member;
                    }
                }
            }
            throw new MwNotSupportedException($"super 未在基类链命中同名方法: {currentFnSymbol}");
        }

        // init 重载匹配（BIL §14.1/§9.2.2：实参静态类型精确一致、唯一
        // 命中；skipReceiver=1 时 argTypeRefs 首位是 .this 接收者）
        internal static MwMemberSymbol ResolveInit(MwSymbolTable symbols, MwTypeSymbol type,
            IReadOnlyList<string> argTypeRefs, int skipReceiver, string? constructedTypeRef = null)
        {
            var substitution = constructedTypeRef != null
                ? Layout.ConstructedTypeCollector.BuildSubstitution(constructedTypeRef, type.Declaration)
                : null;
            MwMemberSymbol? match = null;
            foreach (var member in type.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count != argTypeRefs.Count - skipReceiver)
                {
                    continue;
                }
                var all = true;
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    var expected = Layout.ConstructedTypeCollector.Substitute(
                        signature.Parameters[i].TypeRef, substitution);
                    if (MwTypeKey.Normalize(expected)
                        != MwTypeKey.Normalize(argTypeRefs[i + skipReceiver]))
                    {
                        all = false;
                        break;
                    }
                }
                if (!all)
                {
                    continue;
                }
                if (match != null)
                {
                    throw new MwNotSupportedException($"init 匹配不唯一: {type.Canonical}");
                }
                match = member;
            }
            return match ?? throw new MwNotSupportedException($"new/super 无匹配 init: {type.Canonical}");
        }

        // 字段访问器查找（沿宿主基类链；excludingFn = 当前 fn，访问器
        // 体内不递归自调——VM TryFindAccessor 同口径）
        internal static MwMemberSymbol? FindAccessor(MwSymbolTable symbols, string fieldSymbol,
            BilAccessorKind kind, string excludingFn)
        {
            for (var type = symbols.FindType(FieldOwnerOf(fieldSymbol));
                type != null; type = BaseOf(symbols, type))
            {
                foreach (var member in type.Members)
                {
                    if (member.Canonical == excludingFn)
                    {
                        continue;
                    }
                    foreach (var modifier in member.Declaration.Modifiers)
                    {
                        if (modifier is BilAccessorModifier accessor
                            && accessor.Kind == kind && accessor.FieldSymbol == fieldSymbol)
                        {
                            return member;
                        }
                    }
                }
            }
            return null;
        }

        // 用户索引运算符（VM FindIndexOperator 同口径）：$$getAtIndex /
        // $$setAtIndex，宿主按剥泛型后的类型名匹配
        internal static MwMemberSymbol? FindIndexOperator(MwSymbolTable symbols,
            MirType collectionType, bool isGet)
        {
            var needle = isGet ? "$$getAtIndex(" : "$$setAtIndex(";
            var hosts = new HashSet<string>(System.StringComparer.Ordinal)
            {
                collectionType.Canonical,
                BilVerificationContext.StripTypeArguments(collectionType.Canonical),
            };
            for (var type = symbols.FindType(collectionType.Canonical)
                    ?? symbols.FindType(
                        BilVerificationContext.StripTypeArguments(collectionType.Canonical));
                type != null; type = BaseOf(symbols, type))
            {
                hosts.Add(type.Canonical);
                if (type.Declaration.ExtendsType is { } baseRef)
                {
                    hosts.Add(BilVerificationContext.StripTypeArguments(baseRef));
                }
            }
            foreach (var member in symbols.Members)
            {
                if (!member.Canonical.Contains(needle, System.StringComparison.Ordinal))
                {
                    continue;
                }
                var owner = member.Owner?.Canonical;
                if (owner != null && (hosts.Contains(owner)
                    || hosts.Contains(BilVerificationContext.StripTypeArguments(owner))))
                {
                    return member;
                }
            }
            return null;
        }

        // 字段符号宿主段：Counter#count@.i32 → Counter
        internal static string FieldOwnerOf(string fieldSymbol)
        {
            var hash = fieldSymbol.IndexOf('#');
            return hash < 0
                ? throw new CompilerInternalException($"字段符号缺宿主段: {fieldSymbol}")
                : fieldSymbol.Substring(0, hash);
        }

        private static MwTypeSymbol? BaseOf(MwSymbolTable symbols, MwTypeSymbol type) =>
            type.Declaration.ExtendsType is { } baseRef ? symbols.FindTypeByRef(baseRef) : null;

        // §7.1 放行：固定泛型 .generic.T、泛型包 .generic.<Pack>、
        // 位置值包 .vargs.<名>、具名值包 .kwargs.<名>。固定/包靠 TypeRef
        // 区分（.typeid vs .array/.map），此处只认前缀
        private static bool IsAdmittedHiddenArg(string name) =>
            name.StartsWith(".generic.", System.StringComparison.Ordinal)
            || name.StartsWith(".vargs.", System.StringComparison.Ordinal)
            || name.StartsWith(".kwargs.", System.StringComparison.Ordinal);

        private static void AddLocal(List<MirLocal>? parameters, List<MirLocal> locals,
            HashSet<string> seen, string name, string typeRef, string fnSymbol)
        {
            // .vars 与参数共名属生成方违约（verifier 已查）；此处防御
            if (!seen.Add(name))
            {
                throw new CompilerInternalException($"fn {fnSymbol} 局部重名: {name}");
            }
            var local = new MirLocal(name, MirType.Of(typeRef));
            locals.Add(local);
            parameters?.Add(local);
        }

        // region/try 作用域统一栈（MW9a）：break/continue/return/throw 的
        // 穿越解析按词法嵌套序逐层走；TryScope 归 TryExpander
        internal abstract class Scope
        {
        }

        // break/continue 的宿 region：breakid 变量名 → 边界块 id
        // （BreakTarget = region 后汇聚/出口块；ContinueTarget = loop 的
        // enum 块（缺省 judge 块），非 loop region 为 null）
        internal sealed class RegionScope : Scope
        {
            internal string BreakIdVar { get; }
            internal string BreakTarget { get; }
            internal string? ContinueTarget { get; }

            internal RegionScope(string breakIdVar, string breakTarget, string? continueTarget)
            {
                BreakIdVar = breakIdVar;
                BreakTarget = breakTarget;
                ContinueTarget = continueTarget;
            }
        }

        // BIL 结构化块 → CFG 直译器：结构化 region 指令（if/loop/switch/
        // call blk/try）递归展开为基本块图，break/continue 目标在展开期静态
        // 解析（breakid 变量 → 宿 region 的合成块 id；§21.5/§21.6 保证
        // 绑定唯一、作用域正确、continue 只命中 loop）。
        internal sealed class FlowBuilder
        {
            private readonly MwContext _context;
            private readonly string _fnSymbol;
            private readonly Dictionary<string, MirLocal> _localMap;
            private readonly MirType _returnType;
            private readonly TryExpander _tryExpander;
            private readonly List<MirBlock> _blocks = new();
            private readonly List<Scope> _scopes = new();   // 作用域栈，顶在末尾
            private string _currentId = "";
            private List<MirInst> _currentInsts = new();
            private MirTerminator? _terminator;
            private int _syntheticCounter;

            internal FlowBuilder(MwContext context, string fnSymbol,
                Dictionary<string, MirLocal> localMap, MirType returnType)
            {
                _context = context;
                _fnSymbol = fnSymbol;
                _localMap = localMap;
                _returnType = returnType;
                _tryExpander = new TryExpander(this);
            }

            // ===== TryExpander 钩子 =====

            internal MwContext Context => _context;
            internal string FnSymbol => _fnSymbol;
            internal MirType ReturnType => _returnType;
            internal List<MirInst> CurrentInsts => _currentInsts;
            internal IReadOnlyList<Scope> Scopes => _scopes;
            internal List<MirLocal> SyntheticLocals { get; } = new();

            internal int NextSynthetic() => _syntheticCounter++;

            // 合成局部登记：并入 fn 局部表（发射期开 alloca，RcInjection
            // 按槽分类处理）
            internal MirLocal RegisterSyntheticLocal(string name, MirType type)
            {
                var local = new MirLocal(name, type);
                _localMap.Add(name, local);
                SyntheticLocals.Add(local);
                return local;
            }

            internal MirType TypeOf(string name) => _localMap[name].Type;

            internal void PushScope(Scope scope) => _scopes.Add(scope);

            internal void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

            // 合成块直接追加（派发垫等；对象身份需在内容填充前建立——
            // MirThrow/调用的 ExcTarget 持块引用）
            internal void AppendBlock(MirBlock block) => _blocks.Add(block);

            internal IReadOnlyList<MirBlock> Build(BilBlock entryBlock)
            {
                StartNewBlock(entryBlock.Id);
                EmitBlock(entryBlock);
                // 函数尾落出：仅当双分支均终结的汇聚块不可达时发生（entry
                // 落尾已被 verifier 拒绝）；补 unreachable 使块形态合法
                if (_terminator == null)
                {
                    Terminate(new MirUnreachable());
                }
                SealCurrentBlock();
                return _blocks;
            }

            // 译入一个 BIL 块的指令流；返回 true = 指令流结束后控制流自然
            // 落出（当前块未终结，调用者接 region 边界边）
            private bool EmitBlock(BilBlock block)
            {
                foreach (var inst in block.Instructions)
                {
                    EmitInstruction(inst);
                }
                return _terminator == null;
            }

            private void EmitInstruction(BilInstruction inst)
            {
                switch (inst)
                {
                    case IfInstruction ifInst:
                        EmitIf(ifInst);
                        break;
                    case LoopInstruction loop:
                        EmitLoop(loop);
                        break;
                    case SwitchInstruction sw:
                        EmitSwitch(sw);
                        break;
                    case CallBlockInstruction callBlock:
                        EmitCallBlock(callBlock);
                        break;
                    case BreakInstruction brk:
                        EnsureOpen();
                        Terminate(new MirBranch(_tryExpander.ResolveBreak(brk.BreakId.Name)));
                        break;
                    case ContinueInstruction cont:
                        EnsureOpen();
                        Terminate(new MirBranch(_tryExpander.ResolveContinue(cont.BreakId.Name)));
                        break;
                    case RetInstruction ret:
                        EnsureOpen();
                        _tryExpander.EmitReturn(ret.Value);
                        break;
                    case HintInstruction:
                        // §18 route dispatcher 标注，codegen 无语义
                        break;
                    case TryInstruction tryInst:
                        _tryExpander.ExpandTry(tryInst);
                        break;
                    case ThrowInstruction throwInst:
                        _tryExpander.EmitThrow(throwInst);
                        break;
                    default:
                        EnsureOpen();
                        EmitSimple(inst);
                        break;
                }
            }

            // §16.2：cond → then / (else|merge)，分支落出汇于 merge
            private void EmitIf(IfInstruction inst)
            {
                EnsureOpen();
                var mergeId = SyntheticId("if.end");
                Terminate(new MirCondBranch(Local(inst.Condition), inst.ThenBlock.Id,
                    inst.ElseBlock?.Id ?? mergeId));
                PushScope(new RegionScope(inst.BreakId.Name, mergeId, null));
                EmitChildBlock(inst.ThenBlock, mergeId);
                if (inst.ElseBlock != null)
                {
                    EmitChildBlock(inst.ElseBlock, mergeId);
                }
                PopScope();
                SealAndStart(mergeId);
            }

            // §16.3 正向：judge → 读 cond →（false 出）/（true body → enum →
            // judge）；§16.4 反向：body → enum → judge → 读 cond →（true 回
            // body）。continue 恒跳 enum（缺省 judge）再走 judge（§16.5）
            private void EmitLoop(LoopInstruction inst)
            {
                EnsureOpen();
                var exitId = SyntheticId("loop.end");
                var continueId = inst.EnumBlock?.Id ?? inst.Judge.Id;
                PushScope(new RegionScope(inst.BreakId.Name, exitId, continueId));
                if (inst.IsRev)
                {
                    Terminate(new MirBranch(inst.Body.Id));
                    EmitChildBlock(inst.Body, continueId);
                    if (inst.EnumBlock != null)
                    {
                        EmitChildBlock(inst.EnumBlock, inst.Judge.Id);
                    }
                    SealAndStart(inst.Judge.Id);
                    if (EmitBlock(inst.Judge))
                    {
                        Terminate(new MirCondBranch(Local(inst.Condition), inst.Body.Id, exitId));
                    }
                }
                else
                {
                    Terminate(new MirBranch(inst.Judge.Id));
                    SealAndStart(inst.Judge.Id);
                    if (EmitBlock(inst.Judge))
                    {
                        Terminate(new MirCondBranch(Local(inst.Condition), inst.Body.Id, exitId));
                    }
                    EmitChildBlock(inst.Body, continueId);
                    if (inst.EnumBlock != null)
                    {
                        EmitChildBlock(inst.EnumBlock, inst.Judge.Id);
                    }
                }
                PopScope();
                SealAndStart(exitId);
            }

            // §16.6：常量表匹配（表序首个 cmp.eq 命中），item/default 落出
            // 汇于 merge，无穿透
            private void EmitSwitch(SwitchInstruction inst)
            {
                EnsureOpen();
                if (inst.Table is not BilSwitchTableResource table)
                {
                    throw new CompilerInternalException($"switch 的表不是 switch-table（fn {_fnSymbol}）");
                }
                var mergeId = SyntheticId("switch.end");
                var itemTargets = new List<string>(inst.ItemBlocks.Count);
                foreach (var item in inst.ItemBlocks)
                {
                    itemTargets.Add(item.Id);
                }
                Terminate(new MirSwitch(Local(inst.Selector), table, itemTargets, inst.DefaultBlock.Id));
                PushScope(new RegionScope(inst.BreakId.Name, mergeId, null));
                foreach (var item in inst.ItemBlocks)
                {
                    EmitChildBlock(item, mergeId);
                }
                EmitChildBlock(inst.DefaultBlock, mergeId);
                PopScope();
                SealAndStart(mergeId);
            }

            // §16.1：进入目标 block，正常落出返回 call 之后；不建调用帧
            private void EmitCallBlock(CallBlockInstruction inst)
            {
                EnsureOpen();
                var mergeId = SyntheticId("call.end");
                Terminate(new MirBranch(inst.Block.Id));
                PushScope(new RegionScope(inst.BreakId.Name, mergeId, null));
                EmitChildBlock(inst.Block, mergeId);
                PopScope();
                SealAndStart(mergeId);
            }

            // ===== 顺序指令（MW1 面） =====

            private void EmitSimple(BilInstruction inst)
            {
                switch (inst)
                {
                    case LoadInstruction load:
                        _currentInsts.Add(new MirLoadResource(load.Resource, load.Target.Name));
                        break;
                    case SetVarInstruction setVar:
                        _currentInsts.Add(new MirCopyLocal(Local(setVar.Source), setVar.Target.Name));
                        break;
                    case GetVarInstruction getVar:
                        _currentInsts.Add(new MirCopyLocal(Local(getVar.Source), getVar.Target.Name));
                        break;
                    case BinaryIntrinsicInstruction binary:
                        _currentInsts.Add(new MirBinaryIntrinsic(binary.Op,
                            Local(binary.Left), Local(binary.Right),
                            TypeOf(binary.Left.Name), TypeOf(binary.Right.Name),
                            TypeOf(binary.Target.Name), binary.Target.Name,
                            _tryExpander.CurrentExcTarget()));
                        break;
                    case UnaryIntrinsicInstruction unary:
                        _currentInsts.Add(new MirUnaryIntrinsic(unary.Op, Local(unary.Operand),
                            TypeOf(unary.Operand.Name), TypeOf(unary.Target.Name), unary.Target.Name));
                        break;
                    case InvokeInstruction invoke:
                        _currentInsts.Add(EmitCallOrSuper(invoke.Method.Symbol,
                            Locals(invoke.Arguments), invoke.Target.Name));
                        break;
                    case InvokeNoResultInstruction invokeNoResult:
                        _currentInsts.Add(EmitCallOrSuper(invokeNoResult.Method.Symbol,
                            Locals(invokeNoResult.Arguments), null));
                        break;
                    case InvokeIndirectInstruction invokeIndirect:
                        _currentInsts.Add(new MirInvokeIndirect(
                            Local(invokeIndirect.CallTarget), Locals(invokeIndirect.Arguments),
                            invokeIndirect.Target.Name, TypeOf(invokeIndirect.CallTarget.Name),
                            _tryExpander.CurrentExcTarget()));
                        break;
                    case InvokeIndirectNoResultInstruction invokeIndirectNoResult:
                        _currentInsts.Add(new MirInvokeIndirect(
                            Local(invokeIndirectNoResult.CallTarget),
                            Locals(invokeIndirectNoResult.Arguments),
                            null, TypeOf(invokeIndirectNoResult.CallTarget.Name),
                            _tryExpander.CurrentExcTarget()));
                        break;
                    case GetFieldInstruction getField:
                        EmitGetField(getField);
                        break;
                    case SetFieldInstruction setField:
                        EmitSetField(setField);
                        break;
                    case GetFieldStaticInstruction getStatic:
                        EmitGetStatic(getStatic);
                        break;
                    case SetFieldStaticInstruction setStatic:
                        EmitSetStatic(setStatic);
                        break;
                    case GetArrayInstruction getArray:
                        EmitGetArray(getArray);
                        break;
                    case SetArrayInstruction setArray:
                        EmitSetArray(setArray);
                        break;
                    case GetIdTypeInstruction getIdType:
                        _currentInsts.Add(new MirGetTypeId(getIdType.TargetType.TypeRef,
                            getIdType.Target.Name));
                        break;
                    case GetIdVarInstruction getIdVar:
                        _currentInsts.Add(new MirGetTypeIdVar(Local(getIdVar.Value),
                            getIdVar.Target.Name));
                        break;
                    case DirectTypeCheckInstruction directCheck:
                        EmitTypeCheck(directCheck.Kind, Local(directCheck.Value),
                            directCheck.TargetType.TypeRef, null, directCheck.Target.Name);
                        break;
                    case IndirectTypeCheckInstruction indirectCheck:
                        EmitTypeCheck(indirectCheck.Kind, Local(indirectCheck.Value),
                            null, Local(indirectCheck.TypeId), indirectCheck.Target.Name);
                        break;
                    case NewWrapperEntityInstruction:
                        // wrapper 实例安装随 MW10；type.with 只查 TypeInfo.wrappers
                        break;
                    case NewIndirectInstruction newIndirect:
                        EmitNewIndirect(newIndirect);
                        break;
                    case NewInstruction newInst:
                        EmitNew(newInst);
                        break;
                    case NewCaseInstruction newCase:
                        EmitNewCase(newCase);
                        break;
                    case IsCaseInstruction isCase:
                        EmitIsCase(isCase);
                        break;
                    case CastInstruction cast:
                        EmitCast(cast);
                        break;
                    default:
                        throw new MwNotSupportedException($"MW3 不支持指令 {inst.Opcode}（fn {_fnSymbol}）");
                }
            }

            // ===== 块状态机 =====

            // 当前块已被前一指令终结时，同块后续指令不可达（abrupt
            // completion 语义）：开死块继续直译（保持忠实，裁减交 LLVM）
            internal void EnsureOpen()
            {
                if (_terminator != null)
                {
                    SealCurrentBlock();
                    StartNewBlock(SyntheticId("dead"));
                }
            }

            // 子 region 块：译入后落出接 exitId 边
            internal void EmitChildBlock(BilBlock child, string exitId)
            {
                SealAndStart(child.Id);
                if (EmitBlock(child))
                {
                    Terminate(new MirBranch(exitId));
                }
            }

            private void SealAndStart(string id)
            {
                SealCurrentBlock();
                StartNewBlock(id);
            }

            internal void StartNewBlock(string id)
            {
                _currentId = id;
                _currentInsts = new List<MirInst>();
                _terminator = null;
            }

            internal void Terminate(MirTerminator terminator)
            {
                _terminator = terminator;
            }

            internal void SealCurrentBlock()
            {
                // 封存即基本块定型，必须有终结符；缺失即直译器自身 bug
                if (_terminator == null)
                {
                    throw new CompilerInternalException($"block {_currentId} 未终结即封存（fn {_fnSymbol}）");
                }
                _blocks.Add(new MirBlock(_currentId, _currentInsts, _terminator));
            }

            private string SyntheticId(string kind)
            {
                return "mw." + kind + "." + _syntheticCounter++;
            }

            // ===== 调用与对象路径（MW4 批 2） =====

            // invoke / invoke.noret：fn(..super) 解析为基类实现符号的
            // MirSuperCall（直调）；其余为普通 MirCall。异常边目标取当前
            // 词法上下文（try 内非空；try 外 null，留待 C 棒解析）
            private MirInst EmitCallOrSuper(string symbol, List<MirOperand> args, string? result)
            {
                var excTarget = _tryExpander.CurrentExcTarget();
                symbol = RedirectBuiltinToString(symbol);
                if (symbol == BilSpellings.SuperReservedFunction)
                {
                    return new MirSuperCall(
                        MirBuilder.ResolveSuperCall(_context, _fnSymbol, ArgTypes(args)),
                        args, result, excTarget);
                }
                return new MirCall(ResolveTarget(symbol), args, result, excTarget);
            }

            // MW9b-G：bootstrap 合成体 toString（Any/Object 默认实现，fn 体
            // 无符号段声明、宿主不进 vtable）直降为全局 native any_to_string
            // 直调——合成体本身即「.this（必要时先装箱 .any）直传
            // any_to_string」，降口径与合成体逐字等价（VM 侧的用户 override
            // 防御扫描不复现；插值场景接收者恒为装箱基元/String，无 override）
            private static string RedirectBuiltinToString(string symbol) => symbol switch
            {
                "core::Any$toString()@.string" => "core::$any_to_string(value:.any)@.string",
                "core::Object$toString()@.string" => "core::$any_to_string(value:.any)@.string",
                _ => symbol,
            };

            // get.array：恒直译 MirGetArray（内建与用户类型同形态；用户
            // 类型降级归 IndexOperatorLoweringPass）
            private void EmitGetArray(GetArrayInstruction inst)
            {
                _currentInsts.Add(new MirGetArray(Local(inst.Array), Local(inst.Index),
                    TypeOf(inst.Array.Name), inst.Target.Name));
            }

            // set.array：恒直译 MirSetArray（内建与用户类型同形态；用户
            // 类型降级归 IndexOperatorLoweringPass）。MW9b-G：写越界可抛，
            // 异常边目标取当前词法上下文
            private void EmitSetArray(SetArrayInstruction inst)
            {
                _currentInsts.Add(new MirSetArray(Local(inst.Collection), Local(inst.Index),
                    Local(inst.Element), TypeOf(inst.Collection.Name),
                    _tryExpander.CurrentExcTarget()));
            }

            // new.indirect：TYPEID 局部 + 实参列表直译。运行期目标不可
            // 静态知；方法级泛型 init 不可能由前端进入（语言无 init<T>），
            // 分发器合成处若见到则 MwNotSupportedException。MW9b-G：无匹配
            // init 可抛，异常边目标取当前词法上下文
            private void EmitNewIndirect(NewIndirectInstruction inst)
            {
                _currentInsts.Add(new MirNewIndirect(Local(inst.TypeId),
                    Locals(inst.Arguments), inst.Target.Name,
                    _tryExpander.CurrentExcTarget()));
            }

            // new type(T)：init 按实参静态类型精确匹配（BIL §14.1）；
            // class → 堆对象（批 2）；struct/enum → 内联槽物化（批 3）
            private void EmitNew(NewInstruction inst)
            {
                var newType = MirType.Of(inst.Type.TypeRef);
                if (TypeLayout.IsArray(newType))
                {
                    _currentInsts.Add(new MirNewArray(newType, Locals(inst.Arguments),
                        inst.Target.Name));
                    return;
                }
                var typeRef = inst.Type.TypeRef;
                var template = _context.Symbols.FindTypeByRef(typeRef)
                    ?? throw new MwNotSupportedException($"MW4 new 的类型不可解析: {typeRef}");
                if (ConstructedTypeCollector.IsConstructed(typeRef)
                    && template.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
                {
                    throw new MwNotSupportedException($"MW5 暂不支持泛型值类型构造: {typeRef}");
                }
                if (ConstructedTypeCollector.IsConstructed(typeRef)
                    && template.Declaration.Kind != BilTypeKind.Class)
                {
                    throw new MwNotSupportedException($"MW5 暂不支持的构造类型形态: {typeRef}");
                }
                var init = MirBuilder.ResolveInit(_context.Symbols, template,
                    ArgTypes(inst.Arguments), skipReceiver: 0, constructedTypeRef: typeRef);
                // 字段初始值缝合方法（VM「new 先装 ..init.wrapper 再调 init」）；
                // 查找维持模板 canonical
                var initWrapper = _context.Symbols.FindMember(
                    template.Canonical + "$..init.wrapper()@.void");
                var sheetCanonical = GenericAbi.IsClosedConstructed(typeRef)
                    ? MwTypeKey.Normalize(typeRef)
                    : template.Canonical;
                var type = sheetCanonical == template.Canonical
                    ? template
                    : new MwTypeSymbol(sheetCanonical, template);
                if (template.Declaration.Kind == BilTypeKind.Class)
                {
                    _currentInsts.Add(new MirNewObject(type, initWrapper, init,
                        Locals(inst.Arguments), inst.Target.Name));
                    return;
                }
                if (template.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
                {
                    _currentInsts.Add(new MirNewValue(type, initWrapper, init,
                        Locals(inst.Arguments), inst.Target.Name));
                    return;
                }
                throw new MwNotSupportedException($"MW4 new 暂不支持类型形态: {typeRef}");
            }

            // new.case：enum 构造（判别常量 + init；init 按实参静态类型
            // 精确匹配，实参 = enum init 参数序）
            private void EmitNewCase(NewCaseInstruction inst)
            {
                var (type, caseSymbol) = ResolveCase(inst.Type.TypeRef, inst.Case.QualifiedName);
                var init = MirBuilder.ResolveInit(_context.Symbols, type,
                    ArgTypes(inst.Arguments), skipReceiver: 0);
                _currentInsts.Add(new MirNewCase(caseSymbol, init,
                    Locals(inst.Arguments), inst.Target.Name));
            }

            // type.is / type.supers / type.with（含 .indirect 与泛型占位）
            private void EmitTypeCheck(BilTypeCheckKind kind, MirOperand value,
                string? targetTypeRef, MirOperand? targetTypeId, string target)
            {
                if (targetTypeRef != null
                    && GenericAbi.TryPlaceholderName(targetTypeRef, out var name))
                {
                    targetTypeId = new MirLocalOperand(".generic." + name);
                    targetTypeRef = null;
                }
                _currentInsts.Add(new MirTypeCheck(MapTypeCheckKind(kind), value,
                    targetTypeRef, targetTypeId, target));
            }

            private static MirTypeCheckKind MapTypeCheckKind(BilTypeCheckKind kind) =>
                kind switch
                {
                    BilTypeCheckKind.Is => MirTypeCheckKind.Is,
                    BilTypeCheckKind.Supers => MirTypeCheckKind.Supers,
                    BilTypeCheckKind.With => MirTypeCheckKind.With,
                    _ => throw new CompilerInternalException("未知 BilTypeCheckKind: " + kind),
                };

            // type.is.case：判别整数比较（enum struct 限定）
            private void EmitIsCase(IsCaseInstruction inst)
            {
                var (_, caseSymbol) = ResolveCase(
                    EnumOwnerOf(inst.Case.QualifiedName), inst.Case.QualifiedName);
                _currentInsts.Add(new MirIsCase(caseSymbol, Local(inst.Value), inst.Target.Name));
            }

            // case 操作数解析：宿主必须是本地 enum struct 且 case 已登记
            private (MwTypeSymbol Type, MwCaseSymbol Case) ResolveCase(string typeRef,
                string qualifiedName)
            {
                var type = _context.Symbols.FindTypeByRef(typeRef)
                    ?? throw new MwNotSupportedException($"MW4 enum 类型不可解析: {typeRef}");
                if (Layout.ConstructedTypeCollector.IsConstructed(typeRef))
                {
                    throw new MwNotSupportedException($"MW5 暂不支持泛型值类型构造: {typeRef}");
                }
                if (type.Declaration.Kind != BilTypeKind.EnumStruct)
                {
                    throw new MwNotSupportedException($"MW4 new.case/type.is.case 仅限 enum struct: {typeRef}");
                }
                foreach (var caseSymbol in type.Cases)
                {
                    if (caseSymbol.Declaration.QualifiedName == qualifiedName)
                    {
                        return (type, caseSymbol);
                    }
                }
                throw new CompilerInternalException($"enum case 未登记: {qualifiedName}");
            }

            // case 限定名的宿主段：Direction.East → Direction
            private static string EnumOwnerOf(string qualifiedName)
            {
                var dot = qualifiedName.LastIndexOf('.');
                return dot < 0
                    ? throw new CompilerInternalException($"case 限定名形状非法: {qualifiedName}")
                    : qualifiedName.Substring(0, dot);
            }

            // get.field：恒直译 MirGetField（含 computed 与 accessor 体内
            // backing 伪字段；访问器改写 / wrapper 拒绝归 AccessorLoweringPass）
            private void EmitGetField(GetFieldInstruction inst)
            {
                _currentInsts.Add(new MirGetField(Local(inst.Object), inst.Field.Symbol,
                    inst.Target.Name, _tryExpander.CurrentExcTarget()));
            }

            // set.field：恒直译 MirSetField（含 computed 与 accessor 体内
            // backing 伪字段；访问器改写 / wrapper 拒绝归 AccessorLoweringPass）
            private void EmitSetField(SetFieldInstruction inst)
            {
                _currentInsts.Add(new MirSetField(Local(inst.Source), Local(inst.Object),
                    inst.Field.Symbol));
            }

            private List<string> ArgTypes(IReadOnlyList<MirOperand> args)
            {
                var types = new List<string>(args.Count);
                foreach (var arg in args)
                {
                    types.Add(TypeOf(((MirLocalOperand)arg).Name).Canonical);
                }
                return types;
            }

            private List<string> ArgTypes(IReadOnlyList<BilVariableOperand> args)
            {
                var types = new List<string>(args.Count);
                foreach (var arg in args)
                {
                    types.Add(TypeOf(arg.Name).Canonical);
                }
                return types;
            }

            // get.field.static：恒直译 MirGetStatic（computed / wrapper
            // 拒绝归 AccessorLoweringPass）
            private void EmitGetStatic(GetFieldStaticInstruction inst)
            {
                RejectConstructedStatic(inst.Field.Symbol);
                _currentInsts.Add(new MirGetStatic(inst.Field.Symbol, inst.Target.Name));
            }

            // set.field.static：恒直译 MirSetStatic（computed / wrapper
            // 拒绝归 AccessorLoweringPass）
            private void EmitSetStatic(SetFieldStaticInstruction inst)
            {
                RejectConstructedStatic(inst.Field.Symbol);
                _currentInsts.Add(new MirSetStatic(Local(inst.Source), inst.Field.Symbol));
            }

            private static void RejectConstructedStatic(string symbol)
            {
                var cut = symbol.IndexOfAny(new[] { '$', '#' });
                var host = cut < 0 ? symbol : symbol.Substring(0, cut);
                if (host.Contains('<'))
                {
                    throw new MwNotSupportedException(
                        $"MW5 暂不支持经构造类型访问静态成员: {symbol}");
                }
            }

            // cast（§12）：nullable 装拆 → Box/Unbox → 占位目标 → 数值转换
            // → 恒等拷贝 / as? 包装 → 标量·struct 不相容（MirCast 失败）
            // → 引用恒等拷贝
            private void EmitCast(CastInstruction inst)
            {
                var sourceType = TypeOf(inst.Source.Name);
                var targetType = MirType.Of(inst.TargetType.TypeRef);
                var resultType = TypeOf(inst.Target.Name);
                // MW9b-G：强制转换失败可抛（CastException），异常边目标取
                // 当前词法上下文（try 内指向派发垫；try 外 null 留待解析）
                var excTarget = _tryExpander.CurrentExcTarget();
                if (TypeLayout.TryGetNullableInner(sourceType, out var unwrapInner)
                    && unwrapInner.Canonical == targetType.Canonical)
                {
                    _currentInsts.Add(new MirUnwrapNullable(Local(inst.Source), unwrapInner,
                        inst.Target.Name));
                    return;
                }
                if (TypeLayout.TryGetNullableInner(targetType, out var wrapInner)
                    && wrapInner.Canonical == sourceType.Canonical)
                {
                    _currentInsts.Add(new MirWrapNullable(Local(inst.Source), wrapInner,
                        inst.Target.Name));
                    return;
                }
                if (IsBoxableValueType(sourceType) && targetType.IsAnyOrObject)
                {
                    _currentInsts.Add(new MirBoxAny(Local(inst.Source), inst.Target.Name));
                    return;
                }
                if (sourceType.IsAnyOrObject && IsBoxableValueType(targetType))
                {
                    _currentInsts.Add(new MirUnboxAny(Local(inst.Source), inst.Target.Name,
                        excTarget));
                    return;
                }
                // 泛型占位目标：降为 typeid 局部，运行期 try_cast
                if (GenericAbi.TryPlaceholderName(targetType.Canonical, out var phName))
                {
                    _currentInsts.Add(new MirCast(Local(inst.Source), inst.Target.Name,
                        inst.IsSafe, null, new MirLocalOperand(".generic." + phName),
                        excTarget));
                    return;
                }
                // 占位源 → 静态目标：同样走运行期 try_cast
                if (TypeLayout.IsGenericPlaceholder(sourceType))
                {
                    _currentInsts.Add(new MirCast(Local(inst.Source), inst.Target.Name,
                        inst.IsSafe, targetType.Canonical, null, excTarget));
                    return;
                }
                // 数值互转（含 char）；as? 结果槽为 Nullable 时仍走 MirCast 包装
                if (MirBuilder.IsNumericScalar(sourceType)
                    && MirBuilder.IsNumericScalar(targetType))
                {
                    if (sourceType.Key == targetType.Key
                        && !TypeLayout.IsNullable(resultType))
                    {
                        _currentInsts.Add(new MirCopyLocal(Local(inst.Source),
                            inst.Target.Name));
                        return;
                    }
                    if (sourceType.Key == targetType.Key
                        && TypeLayout.TryGetNullableInner(resultType, out var numInner)
                        && numInner.Canonical == targetType.Canonical)
                    {
                        _currentInsts.Add(new MirWrapNullable(Local(inst.Source), numInner,
                            inst.Target.Name));
                        return;
                    }
                    _currentInsts.Add(new MirCast(Local(inst.Source), inst.Target.Name,
                        inst.IsSafe, targetType.Canonical, null, excTarget));
                    return;
                }
                // 恒等（String / 同 struct / 同类）；as? 则包 Nullable
                if (sourceType.Canonical == targetType.Canonical)
                {
                    if (TypeLayout.TryGetNullableInner(resultType, out var idInner)
                        && idInner.Canonical == targetType.Canonical)
                    {
                        _currentInsts.Add(new MirWrapNullable(Local(inst.Source), idInner,
                            inst.Target.Name));
                        return;
                    }
                    _currentInsts.Add(new MirCopyLocal(Local(inst.Source), inst.Target.Name));
                    return;
                }
                // 标量/String/typeid/struct 不相容：抛 CastException / as? 产 null
                if (MirBuilder.IsScalarOrString(sourceType)
                    || MirBuilder.IsScalarOrString(targetType)
                    || TypeLayout.IsTypeId(sourceType) || TypeLayout.IsTypeId(targetType)
                    || IsUserValueType(sourceType) || IsUserValueType(targetType))
                {
                    _currentInsts.Add(new MirCast(Local(inst.Source), inst.Target.Name,
                        inst.IsSafe, targetType.Canonical, null, excTarget));
                    return;
                }
                _currentInsts.Add(new MirCopyLocal(Local(inst.Source), inst.Target.Name));
            }

            private bool IsUserValueType(MirType type) =>
                _context.Symbols.FindType(type.Canonical) is { Declaration.Kind:
                    BilTypeKind.Struct or BilTypeKind.EnumStruct };

            private bool IsBoxableValueType(MirType type)
            {
                if (MirBuilder.IsScalarOrString(type) || TypeLayout.IsTypeId(type))
                {
                    return true;
                }
                return _context.Symbols.FindType(type.Canonical) is { Declaration.Kind:
                    BilTypeKind.Struct or BilTypeKind.EnumStruct };
            }

            // ===== 操作数与目标解析 =====

            private MirLocalOperand Local(BilVariableOperand operand) => new(operand.Name);

            private MwMemberSymbol ResolveTarget(string symbol)
            {
                return _context.Symbols.FindMember(symbol)
                    ?? throw new MwNotSupportedException(
                        $"MW1 不支持调用无符号段声明的预定义符号: {symbol}（fn {_fnSymbol}）");
            }

            private static List<MirOperand> Locals(IReadOnlyList<BilVariableOperand> operands)
            {
                var list = new List<MirOperand>(operands.Count);
                foreach (var operand in operands)
                {
                    list.Add(new MirLocalOperand(operand.Name));
                }
                return list;
            }
        }
    }
}
