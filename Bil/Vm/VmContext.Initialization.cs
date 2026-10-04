using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Initialization 职责；与主文件共享同一类型、字段及生命周期。

        public VmValue ZeroOf(string typeRef)
        {
            if (IsArrayType(typeRef, out _))
            {
                return VmNull.Instance;
            }
            switch (typeRef)
            {
                case ".i8": case "core::i8": return new VmI8(0);
                case ".i16": case "core::i16": return new VmI16(0);
                case ".i32": case "core::i32": return new VmI32(0);
                case ".i64": case "core::i64": return new VmI64(0);
                case ".u8": case "core::u8": return new VmU8(0);
                case ".u16": case "core::u16": return new VmU16(0);
                case ".u32": case "core::u32": return new VmU32(0);
                case ".u64": case "core::u64": return new VmU64(0);
                case ".f32": case "core::float": return new VmF32(0);
                case ".f64": case "core::double": return new VmF64(0);
                case ".bool": case "core::bool": return new VmBool(false);
                case ".char": case "core::char": return new VmChar(0u);
                case ".string": case "core::String": return new VmString("");
                case ".void": return VmVoid.Instance;
                case ".null": return VmNull.Instance;
            }
            if (typeRef.StartsWith(".nullable<", StringComparison.Ordinal)
                || typeRef.StartsWith("core::Nullable<", StringComparison.Ordinal)
                || typeRef == ".any" || typeRef == "core::Any"
                || typeRef == ".object" || typeRef == "core::Object")
            {
                return VmNull.Instance;
            }
            var declaration = FindType(typeRef);
            if (declaration != null
                && declaration.Kind is BilTypeKind.Struct or BilTypeKind.Wrapper)
            {
                return AllocateObject(typeRef);
            }
            return VmNull.Instance;
        }

        public VmObject AllocateObject(string typeRef)
        {
            var valueType = IsValueType(typeRef);
            // MW12b §25.2：class 引用对象且实现闭包含 core::IDisposable
            // 才挂销毁检查（判定结果按类型缓存，热路径仅一次字典读）
            var instance = new VmObject(typeRef, valueType,
                !valueType && DisposeSlotTargetOf(typeRef) != null
                    ? UndisposedTracker : null);
            for (var resourceType = FindType(typeRef); resourceType != null;
                resourceType = resourceType.ExtendsType == null ? null : FindType(resourceType.ExtendsType))
            {
                var logicalOwner = new[] { "core.coroutine::Mutex", "core.coroutine::Dispatcher",
                    "core.coroutine::Task", "core.coroutine::Task<TReturn>" }
                    .SingleOrDefault(name => RuntimeSymbol(name) == resourceType.Symbol);
                if (logicalOwner != null)
                {
                    instance.NativeResourceRelease = Dispatch.ReleaseOwnedResources;
                    var gate = RuntimeField(logicalOwner + "#gate@.i64");
                    instance.NativeGateFieldSuffix = gate[gate.LastIndexOf('#')..];
                    var handle = RuntimeField(logicalOwner + "#handle@.i64");
                    instance.NativeCoroutineFieldSuffix = handle[handle.LastIndexOf('#')..];
                    break;
                }
            }
            foreach (var field in CollectInstanceFields(typeRef))
            {
                if (!BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out _, out _, out var fieldType))
                {
                    continue;
                }
                instance.WriteField(field.Symbol, ZeroOf(fieldType));
            }
            return instance;
        }

        // ===== singleton（BIL §8.2 / §8.7）=====

        // 类型是否 singleton（含 companion）。VM 语义：singleton 全模块
        // 唯一实例，main 前由 InitializeSingletons 急切初始化；运行期
        // new type(singleton) 返回同一份已初始化实例（不再重跑 init）
        public bool IsSingletonType(string typeRef)
        {
            var declaration = FindType(typeRef);
            if (declaration == null) return false;
            foreach (var modifier in declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keyword
                    && keyword.Keyword == BilKeyword.Singleton)
                {
                    return true;
                }
            }
            return false;
        }

        // 取已缓存 singleton（null = 尚未构造）
        public VmValue? GetSingleton(string typeRef)
        {
            lock (_singletonLock)
            {
                return _singletons.TryGetValue(typeRef, out var value) ? value : null;
            }
        }

        // 登记 singleton 实例（幂等：仅首次写入——构造期间登记即标记
        // 在途，递归/环引用命中同一份半成品实例）
        public void RegisterSingleton(string typeRef, VmValue instance)
        {
            lock (_singletonLock)
            {
                if (!_singletons.ContainsKey(typeRef))
                {
                    _singletons[typeRef] = instance;
                }
            }
        }

        // 是否正在初始化（init 在途，尚未登记为已就绪）
        public bool IsInitializing(string typeRef)
        {
            lock (_initializingLock)
            {
                return _initializing.Contains(typeRef);
            }
        }

        // 进入构造：压入在途栈（幂等语义由 New 的环检测前置保证——同一
        // 类型不会重复入栈）
        public void BeginInitializing(string typeRef)
        {
            lock (_initializingLock)
            {
                _initializing.Add(typeRef);
            }
        }

        // 离开构造：弹出最近一次入栈的本类型条目
        public void EndInitializing(string typeRef)
        {
            lock (_initializingLock)
            {
                var index = _initializing.LastIndexOf(typeRef);
                if (index >= 0)
                {
                    _initializing.RemoveAt(index);
                }
            }
        }

        // 循环依赖异常（裁定 2）：链 = 在途栈中首次出现 typeRef 起的子链 +
        // typeRef 自身，报清晰循环链而非栈溢出
        public VmException SingletonCycleException(string typeRef)
        {
            var chain = new List<string>();
            lock (_initializingLock)
            {
                var index = _initializing.IndexOf(typeRef);
                for (var i = index < 0 ? 0 : index; i < _initializing.Count; i++)
                {
                    chain.Add(_initializing[i]);
                }
            }
            chain.Add(typeRef);
            return new VmException("singleton 初始化循环依赖：" + string.Join(" → ", chain));
        }

        // main 前急切初始化全部 singleton（§8.7）：构造 → init 跑完
        // （companion 的 init 即完成 cell 构造与 wrapper 安装）。初始化
        // 依赖（companion init 引用别的 singleton）由 New 的递归构造触发；
        // 本循环按声明序逐一补全尚未构造者
        public void InitializeSingletons()
        {
            foreach (var declaration in _types.Values)
            {
                var singleton = false;
                foreach (var modifier in declaration.Modifiers)
                {
                    if (modifier is BilKeywordModifier keyword
                        && keyword.Keyword == BilKeyword.Singleton)
                    {
                        singleton = true;
                        break;
                    }
                }
                if (!singleton) continue;
                var typeRef = declaration.Symbol;
                if (GetSingleton(typeRef) != null) continue;
                ConstructSingleton(typeRef);
            }
        }

        // 同步构造单个 singleton：借一次性协程跑 new（分配 + init +
        // ..init.wrapper），驱动至构造帧完全退回引导帧
        private void ConstructSingleton(string typeRef)
        {
            var coroutine = new VmCoroutine(Dispatch);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(SingletonBootstrapFunction, Array.Empty<VmValue>(), null, this);
            BilDataExecution.New(this, coroutine, typeRef, SingletonBootstrapTarget,
                Array.Empty<BilVariableOperand>(), null);
            while (coroutine.CallStack.Count > 1 && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(this);
            }
            if (coroutine.State != VmCoroutineState.Running)
            {
                throw coroutine.Failure ?? new VmException("singleton 初始化失败：" + typeRef);
            }
        }

        // N1（§8.4.1/§9.3，新 init 原则）：全局/静态字段的声明初始值——
        // 编译器为含初始值的模块合成 ..globals.init 全局 fn（体内按声明序
        // set.field.static），本方法在 singleton 初始化之后、main 之前
        // 同步驱动它跑完（参照 §8.7 companion 统一设计：静态初值的执行
        // 时机归 VM 启动序列；多模块合并时逐 fn 各跑一次）
        public void InvokeGlobalInitializers()
        {
            foreach (var symbol in BilModuleInitialization.Order(Module))
            {
                var function = _functions[symbol];
                var coroutine = new VmCoroutine(Dispatch);
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
                coroutine.PushFrame(function, Array.Empty<VmValue>(), null, this);
                while (coroutine.CallStack.Count > 0
                    && coroutine.State == VmCoroutineState.Running)
                {
                    coroutine.Step(this);
                }
                if (coroutine.State != VmCoroutineState.Running
                    && coroutine.State != VmCoroutineState.Completed)
                {
                    throw coroutine.Failure
                        ?? new VmException("全局字段初始化失败：" + function.Symbol);
                }
            }
        }

        private static BilFunction CreateSingletonBootstrap()
        {
            var function = new BilFunction("..singleton.init");
            function.Args.Add(new BilArgDeclaration(".return", ".void"));
            function.Vars.Add(new BilVarDeclaration(".any", ".singleton"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new RetInstruction());
            function.Blocks.Add(entry);
            return function;
        }

        public VmValue ReadStaticField(string fieldSymbol)
        {
            lock (_staticLock)
            {
                if (_statics.TryGetValue(fieldSymbol, out var value))
                {
                    return value;
                }
                var fieldType = ".any";
                if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                        out _, out _, out var parsed))
                {
                    fieldType = parsed;
                }
                var zero = ZeroOf(fieldType);
                _statics[fieldSymbol] = zero;
                return zero;
            }
        }

        public void WriteStaticField(string fieldSymbol, VmValue value)
        {
            lock (_staticLock)
            {
                _statics[fieldSymbol] = value;
            }
        }

    }
}
