using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 发射环境（P4b，模块级）：整个发射期存活的共享状态。
    internal sealed class EmitEnvironment
    {
        public EmitEnvironment(CompilationUnit unit, string moduleName)
        {
            Unit = unit;
            ModuleName = moduleName;
            Module = new BilModule();
        }

        public CompilationUnit Unit { get; }

        public string ModuleName { get; }

        // 构建中的模块（LocalSymbols/Resources/Functions 逐段填充）。
        // 注意：符号段/函数/资源的添加一律经 AddLocalSymbol/AddFunction/
        // AddResource——merged 模块与命名空间切片同步记录（§17 切分）
        public BilModule Module { get; }

        // ===== 命名空间切片（§17：一次编译按命名空间切分多个 BIL 文件）=====
        // merged（Module）内容与原单模块完全一致（声明序不变，供验证器与
        // 测试消费）；切片是 merge 兼容的子集——资源全局统一命名（R_N
        // 不跨切片重号）、每个符号/fn 恰好归属一个切片，全部切片合并
        // （vm --file 多文件）即还原 merged。切片不追求各自自足：fn 可
        // 引用落在别的切片里的资源/符号
        private readonly Dictionary<string, BilModule> _sliceByNs =
            new Dictionary<string, BilModule>();
        private readonly List<KeyValuePair<string, BilModule>> _sliceOrder =
            new List<KeyValuePair<string, BilModule>>();

        // 当前发射单元归属的命名空间（"" = 全局）；由 EmittingDriver/
        // LocalSymbolEmitters 在发射每个声明/fn 前设置
        public string CurrentSliceNs { get; set; } = "";

        // 创建序稳定的切片列表（ns → 模块）
        public IReadOnlyList<KeyValuePair<string, BilModule>> Slices => _sliceOrder;

        public BilModule SliceFor(string ns)
        {
            if (!_sliceByNs.TryGetValue(ns, out var slice))
            {
                slice = new BilModule();
                // §4.1：切片模块名 = 基名（全局切片）或 基名.命名空间
                var name = ns.Length == 0 ? ModuleName : ModuleName + "." + ns;
                slice.Metadata.Add(new BilMetadataEntry("module", BilScalarType.String,
                    $"\"{name}\""));
                _sliceByNs.Add(ns, slice);
                _sliceOrder.Add(new KeyValuePair<string, BilModule>(ns, slice));
            }
            return slice;
        }

        public void AddLocalSymbol(BilSymbolSectionEntry entry)
        {
            Module.LocalSymbols.Add(entry);
            SliceFor(CurrentSliceNs).LocalSymbols.Add(entry);
        }

        public void AddExternalSymbol(BilSymbolSectionEntry entry)
        {
            Module.ExternalSymbols.Add(entry);
            SliceFor(CurrentSliceNs).ExternalSymbols.Add(entry);
        }

        public void AddFunction(BilFunction function)
        {
            Module.Functions.Add(function);
            SliceFor(CurrentSliceNs).Functions.Add(function);
        }

        public void AddResource(BilResource resource)
        {
            Module.Resources.Add(resource);
            SliceFor(CurrentSliceNs).Resources.Add(resource);
        }

        // 切片收尾（EmittingDriver.Run 末尾）：把各切片实际引用到的资源
        // 回填进切片 Resources——切片由此单文件自足（BilReader 单文件解析
        // 要求 res(...) 在本文档内可解析）；跨切片共享的资源同名同内容
        //（同一资源对象），合并去重归 vm MergeModule。保 merged 资源序
        public void FinalizeSlices()
        {
            foreach (var (_, slice) in _sliceOrder)
            {
                var referenced = new HashSet<string>();
                foreach (var function in slice.Functions)
                {
                    foreach (var block in function.Blocks)
                    {
                        foreach (var instruction in block.Instructions)
                        {
                            foreach (var operand in instruction.Operands)
                            {
                                if (operand is BilResourceOperand resourceOperand)
                                {
                                    referenced.Add(resourceOperand.Resource.Name);
                                }
                            }
                        }
                    }
                }
                foreach (var entry in slice.LocalSymbols)
                {
                    if (entry is not BilTypeDeclaration type) continue;
                    foreach (var member in type.Members)
                    {
                        if (member is BilCaseDeclaration { DiscriminantResource: { } name })
                        {
                            referenced.Add(name);
                        }
                    }
                }
                foreach (var resource in Module.Resources)
                {
                    if (referenced.Contains(resource.Name) && !slice.Resources.Contains(resource))
                    {
                        slice.Resources.Add(resource);
                    }
                }
            }
        }

        // 资源去重表（模块级，跨 fn 共享——§19.4 switch-table 等同元素
        // 序列资源跨 fn 去重；M57 起按资源种类分表，值为资源对象）：
        // 标量键 = (类型, 字面量原文)；null 键 = 元素类型 canonical；
        // switch-table 键 = selector 类型引用 + 元素序列。
        // catch-table 不在此（条目持 fn 局部 block 对象引用，去重表
        // 为函数级、挂 EmitContext——跨 fn 共享会引入别函数 block，
        // §21.5 越权）
        public Dictionary<(BilScalarType Type, string LiteralText), BilScalarResource> ScalarKeys
            { get; } = new Dictionary<(BilScalarType, string), BilScalarResource>();
        public Dictionary<string, BilNullResource> NullKeys { get; } =
            new Dictionary<string, BilNullResource>();
        public Dictionary<string, BilSwitchTableResource> SwitchTableKeys { get; } =
            new Dictionary<string, BilSwitchTableResource>();

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P4, span, message);
        }
    }
}
