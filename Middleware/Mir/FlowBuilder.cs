using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
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

    /// <summary>
    /// BIL→MIR 组合根（对照 BindContext）：当前块施工目标 + RegionScope 栈
    /// + 合成局部。指令翻译经 MirLowerDispatchers 唯一 switch 分到 CRTP 类；
    /// 本类不直接 switch opcode。
    /// </summary>
    internal sealed class FlowBuilder
    {
        private readonly MwContext _context;
        private readonly string _fnSymbol;
        private readonly Dictionary<string, MirLocal> _localMap;
        private readonly MirType _returnType;
        private readonly TryExpander _tryExpander;
        private readonly List<MirBlock> _blocks = new();
        private readonly List<Scope> _scopes = new();
        // null 常量资源物化的局部名（load null → 局部）：BinaryIntrinsicLowering
        // 以此识别「x ==/!= null」nullness-only 形态（同型 Nullable 判等展开
        // 不适用于该形态——位比即语义；且展开会在 wrapper bake 特化体内留下
        // 占位指令 + 具化槽的形态分裂，见 NullableEqualityLowering 注释）
        private readonly HashSet<string> _nullConstantLocals = new(System.StringComparer.Ordinal);
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

        internal MwContext Context => _context;
        internal string FnSymbol => _fnSymbol;
        internal MirType ReturnType => _returnType;
        internal List<MirInst> CurrentInsts => _currentInsts;
        internal IReadOnlyList<Scope> Scopes => _scopes;
        internal List<MirLocal> SyntheticLocals { get; } = new();
        internal TryExpander Tries => _tryExpander;

        internal int NextSynthetic() => _syntheticCounter++;

        internal MirLocal RegisterSyntheticLocal(string name, MirType type)
        {
            var local = new MirLocal(name, type);
            _localMap.Add(name, local);
            SyntheticLocals.Add(local);
            return local;
        }

        internal MirType TypeOf(string name) => _localMap[name].Type;

        // null 常量资源物化登记（LoadLowering 调用）与查询
        internal void MarkNullConstant(string localName) => _nullConstantLocals.Add(localName);
        internal bool IsNullConstant(string localName) => _nullConstantLocals.Contains(localName);

        internal void PushScope(Scope scope) => _scopes.Add(scope);

        internal void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

        internal void AppendBlock(MirBlock block) => _blocks.Add(block);

        internal void Add(MirInst inst) => _currentInsts.Add(inst);

        internal IReadOnlyList<MirBlock> Build(BilBlock entryBlock)
        {
            StartNewBlock(entryBlock.Id);
            EmitBlock(entryBlock);
            if (_terminator == null)
            {
                Terminate(new MirUnreachable());
            }
            SealCurrentBlock();
            return _blocks;
        }

        // 译入一个 BIL 块的指令流；返回 true = 指令流结束后控制流自然
        // 落出（当前块未终结，调用者接 region 边界边）
        internal bool EmitBlock(BilBlock block)
        {
            foreach (var inst in block.Instructions)
            {
                MirLowerDispatchers.Visit(inst, this);
            }
            return _terminator == null;
        }

        internal void EnsureOpen()
        {
            if (_terminator != null)
            {
                SealCurrentBlock();
                StartNewBlock(SyntheticId("dead"));
            }
        }

        internal void EmitChildBlock(BilBlock child, string exitId)
        {
            SealAndStart(child.Id);
            if (EmitBlock(child))
            {
                Terminate(new MirBranch(exitId));
            }
        }

        internal void SealAndStart(string id)
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
            if (_terminator == null)
            {
                throw new CompilerInternalException($"block {_currentId} 未终结即封存（fn {_fnSymbol}）");
            }
            _blocks.Add(new MirBlock(_currentId, _currentInsts, _terminator));
        }

        internal string SyntheticId(string kind)
        {
            return "mw." + kind + "." + _syntheticCounter++;
        }

        internal MirInst EmitCallOrSuper(string symbol, List<MirOperand> args, string? result)
        {
            var excTarget = _tryExpander.CurrentExcTarget();
            symbol = RedirectBuiltinToString(symbol);
            if (symbol == BilSpellings.SuperReservedFunction)
            {
                return new MirSuperCall(
                    MirBuilder.ResolveSuperCall(_context, _fnSymbol, ArgTypes(args)),
                    args, result, excTarget);
            }
            var target = ResolveTarget(symbol);
            return new MirCall(target, args, result, excTarget,
                hostConstructedRef: ResolveValueHostTypeRef(target, args));
        }

        // G1：泛型值类型宿主的实例方法调用——类级 typeid 随实参直传
        //（值类型无对象头隐藏槽，与 class「被调方自取」对偶），构造形态
        // 由接收者静态类型给出；frontend 的擦除 cast（构造 → 裸模板）经
        // _erasedValueHosts 回溯。静态方法无类级 typeid（SYNTAX §9.2.3），
        // 发射侧按 fn 实参表定是否需要，此处仅尽力附上构造 ref
        private string? ResolveValueHostTypeRef(MwMemberSymbol target, List<MirOperand> args)
        {
            var owner = target.Owner;
            if (!GenericAbi.IsValueTypeOwner(owner)
                || owner!.Declaration.GenericParameters.Count == 0
                // 静态成员无 .this 接收者、无类级 typeid 实参（§9.2.3）
                || target.Canonical.Contains("$.static.", System.StringComparison.Ordinal)
                || args.Count == 0 || args[0] is not MirLocalOperand receiver)
            {
                return null;
            }
            var receiverType = TypeOf(receiver.Name).Canonical;
            if (ConstructedTypeCollector.IsConstructed(receiverType)
                && BilVerificationContext.StripTypeArguments(
                    MwTypeKey.Normalize(receiverType)) == owner.Canonical)
            {
                return MwTypeKey.Normalize(receiverType);
            }
            if (receiverType == owner.Canonical
                && TryResolveErasedValueHost(receiver.Name, out var erased))
            {
                return erased;
            }
            return null;
        }

        private string RedirectBuiltinToString(string symbol) => symbol switch
        {
            "core::Any$toString()@.string" or "core::Object$toString()@.string" =>
                BilCompilerHelpers.Resolve(_context.Module, "any_to_string") ?? symbol,
            // hash（Map 键判等）：与 toString 同构的 helper 重定向
            "core::Any$hash()@.i64" or "core::Object$hash()@.i64" =>
                BilCompilerHelpers.Resolve(_context.Module, "any_hash") ?? symbol,
            _ => symbol,
        };

        internal (MwTypeSymbol Type, MwCaseSymbol Case) ResolveCase(string typeRef,
            string qualifiedName)
        {
            // G1：构造 enum（Choice<i32>.Some(...)）具化支持——VM NewCase
            // 经 ResolveTypeRef 具体化同语义；宿主解析到模板，构造身份
            // 由目标局部静态类型携带（Emit 侧代入类级 typeid）
            var type = _context.Symbols.FindTypeByRef(typeRef)
                ?? throw new MwNotSupportedException($"MW4 enum 类型不可解析: {typeRef}");
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

        internal static string EnumOwnerOf(string qualifiedName)
        {
            var dot = qualifiedName.LastIndexOf('.');
            return dot < 0
                ? throw new CompilerInternalException($"case 限定名形状非法: {qualifiedName}")
                : qualifiedName.Substring(0, dot);
        }

        internal static void RejectConstructedStatic(string symbol)
        {
            var cut = symbol.IndexOfAny(new[] { '$', '#' });
            var host = cut < 0 ? symbol : symbol.Substring(0, cut);
            if (host.Contains('<'))
            {
                throw new MwNotSupportedException(
                    $"MW5 暂不支持经构造类型访问静态成员: {symbol}");
            }
        }

        internal bool IsUserValueType(MirType type) =>
            _context.Symbols.FindTypeByRef(type.Canonical) is { Declaration.Kind:
                BilTypeKind.Struct or BilTypeKind.EnumStruct };

        internal bool IsBoxableValueType(MirType type)
        {
            if (MirBuilder.IsScalarOrString(type) || TypeLayout.IsTypeId(type))
            {
                return true;
            }
            return _context.Symbols.FindTypeByRef(type.Canonical) is { Declaration.Kind:
                BilTypeKind.Struct or BilTypeKind.EnumStruct };
        }

        internal List<string> ArgTypes(IReadOnlyList<MirOperand> args)
        {
            var types = new List<string>(args.Count);
            foreach (var arg in args)
            {
                types.Add(TypeOf(((MirLocalOperand)arg).Name).Canonical);
            }
            return types;
        }

        internal List<string> ArgTypes(IReadOnlyList<BilVariableOperand> args)
        {
            var types = new List<string>(args.Count);
            foreach (var arg in args)
            {
                types.Add(TypeOf(arg.Name).Canonical);
            }
            return types;
        }

        internal MirLocalOperand Local(BilVariableOperand operand) => new(operand.Name);

        // ===== 静态 id 追踪（L1：get.wrapper.indirect / field.indirect 族）=====
        // getid.type / getid.field 的产物局部 → 静态 typeref / 字段符号；
        // 经 set.var/get.var 拷贝传播（未追踪源覆盖 = 撤登记，保守对齐
        // verifier FieldIdOf 的「不可静态解则不算」口径）。不做跨块流敏感
        // 合并——与 verifier 直线路径先例同覆盖；静态不可解析的 indirect
        // 使用点在 lowering 受控拒绝（native 无运行期 wrapper 槽/字段偏移
        // 查找面，编译期拒绝是 VM 运行期解析的保守超集）
        private readonly Dictionary<string, string> _typeIdRefs = new(System.StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fieldIdSymbols = new(System.StringComparer.Ordinal);

        // G1：泛型值类型方法接收者的构造形态追踪——frontend 对泛型值类型
        // 方法调用接收者先发「构造 → 裸模板」的擦除 cast（BIL invoke 接收者
        // 静态类型须为模板），类级 typeid 合成需回溯构造实参；随
        // set.var/get.var 拷贝传播（同上行 id 追踪口径），未追踪即 null，
        // 发射侧退 .generic.* 局部兜底/受控拒绝
        private readonly Dictionary<string, string> _erasedValueHosts = new(System.StringComparer.Ordinal);

        internal void NoteErasedValueHost(string name, string constructedRef) =>
            _erasedValueHosts[name] = constructedRef;

        internal bool TryResolveErasedValueHost(string name, out string constructedRef) =>
            _erasedValueHosts.TryGetValue(name, out constructedRef!);

        internal void NoteTypeId(string name, string typeRef) => _typeIdRefs[name] = typeRef;

        internal void NoteFieldId(string name, string symbol) => _fieldIdSymbols[name] = symbol;

        internal bool TryResolveTypeIdRef(string name, out string typeRef) =>
            _typeIdRefs.TryGetValue(name, out typeRef!);

        internal bool TryResolveFieldIdSymbol(string name, out string symbol) =>
            _fieldIdSymbols.TryGetValue(name, out symbol!);

        // set.var/get.var 拷贝传播：源已追踪 → 目标继承；源未追踪 → 目标
        // 撤登记（覆盖写语义）。.typeid/.fieldid 槽的唯一生产者是
        // getid.* 与拷贝，其余写形态不出现在 indirect 族合法模块内
        internal void PropagateIdCopy(string sourceName, string targetName)
        {
            if (_typeIdRefs.TryGetValue(sourceName, out var typeRef))
            {
                _typeIdRefs[targetName] = typeRef;
            }
            else
            {
                _typeIdRefs.Remove(targetName);
            }
            if (_fieldIdSymbols.TryGetValue(sourceName, out var symbol))
            {
                _fieldIdSymbols[targetName] = symbol;
            }
            else
            {
                _fieldIdSymbols.Remove(targetName);
            }
            if (_erasedValueHosts.TryGetValue(sourceName, out var constructedRef))
            {
                _erasedValueHosts[targetName] = constructedRef;
            }
            else
            {
                _erasedValueHosts.Remove(targetName);
            }
        }

        internal MwMemberSymbol ResolveTarget(string symbol)
        {
            var member = _context.Symbols.FindMember(symbol);
            if (member != null)
            {
                return member;
            }
            // 刀4：call??? 是 bootstrap 预定义符号（§15.6，无符号段声明）——
            // 先成通用 MirCall 占位，由 CallWildcardLoweringPass 改写为
            // $mw.call???.dispatch；漏改即发射缺符号（同刀前口径）
            if (BilSpellings.IsCallWildcardMethod(symbol))
            {
                return new MwMemberSymbol(
                    new BilSimpleMemberDeclaration(BilMemberKind.Method, symbol),
                    owner: null, isExternal: false);
            }
            throw new MwNotSupportedException(
                $"MW1 不支持调用无符号段声明的预定义符号: {symbol}（fn {_fnSymbol}）");
        }

        internal static List<MirOperand> Locals(IReadOnlyList<BilVariableOperand> operands)
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
