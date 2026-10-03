using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware;

internal enum NativeBuildKind { Executable, StaticLibrary, DynamicLibrary }
internal sealed record NativeExport(string Name, string Canonical);

/// <summary>一次后端请求的固定 ABI。导出不是 native import，也不写回 BIL。</summary>
internal sealed record NativeBuildOptions(NativeBuildKind Kind, IReadOnlyList<NativeExport> Exports)
{
    internal bool IsLibrary => Kind != NativeBuildKind.Executable;
    internal string Identity => Kind + "|c-scalar-v1|hidden-runtime|" + string.Join("\n",
        Exports.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => e.Name + "=" + e.Canonical));
    internal static readonly NativeBuildOptions Executable = new(NativeBuildKind.Executable, []);
    internal bool IsExport(string canonical) => Exports.Any(e => e.Canonical == canonical);
    internal static string CType(string type) => MwTypeKey.Of(MwTypeKey.Normalize(type)) switch
    {
        "void" => "void", "bool" or "u8" => "uint8_t", "i8" => "int8_t", "u16" => "uint16_t", "i16" => "int16_t",
        "u32" or "char" => "uint32_t", "i32" => "int32_t", "u64" => "uint64_t", "i64" => "int64_t",
        "float" => "float", "double" => "double", _ => throw new MwNotSupportedException("C 导出不支持类型：" + type)
    };
    internal void Validate(MwContext context)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in Exports)
        {
            if (!names.Add(export.Name) || !System.Text.RegularExpressions.Regex.IsMatch(export.Name, @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
                throw new MwNotSupportedException("C 导出名重复或非法：" + export.Name);
            var member = context.Symbols.FindMember(export.Canonical)
                ?? throw new MwNotSupportedException("C 导出不存在：" + export.Canonical);
            var fn = context.Module.Functions.SingleOrDefault(f => f.Symbol == export.Canonical);
            if (fn == null || member.IsExternal || member.HasKeyword(BilKeyword.Native) || member.HasKeyword(BilKeyword.Async)
                || !member.Declaration.Modifiers.OfType<BilAccessibilityModifier>().Any(m => m.Accessibility == BilAccessibility.Public)
                || member.Owner != null && (member.Declaration.Kind != BilMemberKind.StaticMethod
                    || member.Owner.Declaration.GenericParameters.Count != 0 || member.Owner.Canonical.Contains('<')))
                throw new MwNotSupportedException("C 导出必须为 pub、有本地实现、无开放宿主的同步静态/全局函数：" + export.Canonical);
            var signature = CanonicalSignature.Parse(export.Canonical);
            CType(signature.ReturnTypeRef);
            var args = fn.Args.Where(a => a.Name != ".return").ToArray();
            if (args.Length != signature.Parameters.Count || args.Any(a => a.Name.StartsWith('.')))
                throw new MwNotSupportedException("C 导出不允许泛型隐藏参数、实例接收者或可变参数：" + export.Canonical);
            foreach (var arg in args)
                if (CType(arg.TypeRef) == "void") throw new MwNotSupportedException("C 导出参数不能是 void");
        }
    }
    internal void ValidateSynchronousClosure(MirModule mir, IReadOnlySet<string> tainted, MwContext? context = null)
    {
        if (Exports.Count == 0 && !IsLibrary) return;
        var functions = mir.Functions.ToDictionary(f => f.Symbol.Canonical, StringComparer.Ordinal);
        var visited = new HashSet<(string, bool)>();
        var queue = new Queue<(string Canonical, bool Initialization)>(Exports.Select(e => (e.Canonical, false)));
        // 库没有 Dispatcher drain；初始化也不得发布任务。全部 eager singleton 与 DAG globals 同口径审查。
        if (IsLibrary && context != null)
        {
            foreach (var singleton in context.Singletons) queue.Enqueue((singleton.GetFnCanonical, true));
            foreach (var fn in mir.Functions.Where(f => BilLogicalName.IsGlobalInitializer(f.Symbol.Canonical)))
                queue.Enqueue((fn.Symbol.Canonical, true));
        }
        while (queue.TryDequeue(out var item))
        {
            var (canonical, initialization) = item;
            if (!visited.Add(item) || !functions.TryGetValue(canonical, out var fn)) continue;
            if (fn.IsAsync || fn.IsCoroutineResume || tainted.Contains(canonical))
                throw new MwNotSupportedException("C 导出闭包不允许协程、挂起或发布异步任务：" + canonical);
            foreach (var instruction in fn.Blocks.SelectMany(b => b.Instructions))
            {
                if (instruction is MirAwait or MirCoroutineCreate or MirInvokeIndirect or MirNewIndirect or MirInnerCall)
                    throw new MwNotSupportedException("C 导出闭包不允许挂起、调度或不确定的间接调用：" + canonical);
                if (instruction is MirCall call)
                {
                    var binding = call.OperatorDispatch ? Binding.ImplBinder.BindOperatorCall(call.Target) : Binding.ImplBinder.BindCall(call.Target);
                    if (binding is Binding.VirtualCallBinding or Binding.InterfaceCallBinding)
                        throw new MwNotSupportedException("C 导出闭包不允许不确定的虚派发：" + call.Target.Canonical);
                }
                // 构造、枚举与 wrapper 安装都是调用边，不能只审显式 invoke。
                MwMemberSymbol?[] targets = instruction switch
                {
                    MirCall c => [c.Target], MirSuperCall c => [c.Target],
                    MirNewObject c => [c.Init, c.InitWrapper], MirNewValue c => [c.Init, c.InitWrapper],
                    MirNewCase c => [c.Init, c.InitWrapper], MirNewWrapper c => [c.Init, c.InitWrapper], _ => []
                };
                foreach (var target in targets.OfType<MwMemberSymbol>())
                {
                    // Dispatcher 的同步构造可分配锁与队列；只放行准确声明和已授权的 mutex-create native ABI。
                    // 构造体仍进入队列，故其中 sleep/spawn/间接调用不会因宿主名称而绕过限制。
                    var synchronousInitialization = initialization && (instruction is MirNewObject or MirNewValue or MirNewCase or MirNewWrapper
                        || target.HasKeyword(BilKeyword.Init)
                        || BilLogicalName.Method(target.Canonical) == BilSpellings.InitWrapperMethodName
                        || context!.Singletons.Any(s => s.GetFnCanonical == target.Canonical)
                        || target.Canonical == BilCompilerSymbols.ResolvePrefix(context.Module, "core.coroutine::$rigi_sync_mutex_create(")
                            && target.HasKeyword(BilKeyword.Native)
                            && target.Declaration.Modifiers.OfType<BilNativeLibraryModifier>().SingleOrDefault()?.Library == "rigi_rt"
                            && target.Declaration.Modifiers.OfType<BilNativeSymbolModifier>().SingleOrDefault()?.Symbol == "sync_mutex_create");
                    if (target.HasKeyword(BilKeyword.Async) || target.Canonical.StartsWith("core.coroutine::", StringComparison.Ordinal) && !synchronousInitialization)
                        throw new MwNotSupportedException("C 导出闭包不允许调度运行面：" + target.Canonical);
                    queue.Enqueue((target.Canonical, initialization));
                }
            }
        }
    }
}
