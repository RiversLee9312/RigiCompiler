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
            return new MirCall(ResolveTarget(symbol), args, result, excTarget);
        }

        private static string RedirectBuiltinToString(string symbol) => symbol switch
        {
            "core::Any$toString()@.string" => "core::$any_to_string(value:.any)@.string",
            "core::Object$toString()@.string" => "core::$any_to_string(value:.any)@.string",
            _ => symbol,
        };

        internal (MwTypeSymbol Type, MwCaseSymbol Case) ResolveCase(string typeRef,
            string qualifiedName)
        {
            var type = _context.Symbols.FindTypeByRef(typeRef)
                ?? throw new MwNotSupportedException($"MW4 enum 类型不可解析: {typeRef}");
            if (ConstructedTypeCollector.IsConstructed(typeRef))
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
            _context.Symbols.FindType(type.Canonical) is { Declaration.Kind:
                BilTypeKind.Struct or BilTypeKind.EnumStruct };

        internal bool IsBoxableValueType(MirType type)
        {
            if (MirBuilder.IsScalarOrString(type) || TypeLayout.IsTypeId(type))
            {
                return true;
            }
            return _context.Symbols.FindType(type.Canonical) is { Declaration.Kind:
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
