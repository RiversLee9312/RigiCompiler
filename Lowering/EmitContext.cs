using System.Collections.Generic;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 函数级发射上下文（P4b）：一个 fn 定义发射期间存活的可变状态。
    // 每个函数新建一个实例（同 BindContext/LowerContext 原则）。
    //
    // 组件化结构（M65 Lowering 侧组件化拆分，同 BindContext 先例）：
    // 本类是组合根——Function 留根部（.args/.vars/Blocks 填充目标），
    // Temps（临时变量表 .tN）与 BlockIds（分支 block 编号分配器）
    // 两组件随组合根同生同灭。
    internal sealed class EmitContext
    {
        public EmitContext(BilFunction function)
        {
            Function = function;
        }

        // 当前函数的 BIL 定义（.args/.vars/Blocks 填充目标）
        public BilFunction Function { get; }

        // 临时变量表（.tN 工厂 + .vars 收尾输出源）
        public TempVarTable Temps { get; } = new TempVarTable();

        // 分支 block 编号分配器（if/loop/switch/seq/try）
        public BlockIdAllocator BlockIds { get; } = new BlockIdAllocator();

        // catch-table 去重表（函数级，§19.5）：键 = 元素文本序列（含
        // block id）。条目持 fn 局部 block 对象引用且 block id 按函数
        // 独立编号（tryN 跨函数撞名）——去重只能在函数内进行（同函数
        // 空 catch 表等形态合法共享）；跨 fn 同键共享会把别函数的
        // block 对象引进本 fn，§21.5 判 block 引用越权
        public Dictionary<string, BilCatchTableResource> CatchTableKeys { get; } =
            new Dictionary<string, BilCatchTableResource>();

        // 同名局部唯一化改名表（BIL §9.3：.vars 函数级平铺、函数内
        // 唯一；源码允许兄弟作用域同名局部——SYNTAX §6 seq 即作用域、
        // 多 catch 同名异常变量亦合法）。撞名局部 → 「名_N」改名；
        // 声明（.vars）与全部引用（set.var/值引用/try 异常槽/cell
        // 引用）统一经 VariableNameOf 解析，两侧一致
        private Dictionary<LocalSymbol, string>? localRenames;

        // 唯一化扫描（指令发射前调用一次）：以 .args 已占名为起点按
        // Locals 顺序扫描，撞名的局部登记「名_N」改名（N 自 1 递增、
        // 跳过已占名）；不撞名不占表（黄金文本零扰动）
        public void BuildLocalRenames(IReadOnlyList<LocalSymbol> locals)
        {
            var taken = new HashSet<string>();
            foreach (var arg in Function.Args)
            {
                taken.Add(arg.Name);
            }
            foreach (var local in locals)
            {
                var name = local.Name;
                if (taken.Add(name))
                {
                    continue;
                }
                var suffix = 1;
                var renamed = name + "_" + suffix;
                while (!taken.Add(renamed))
                {
                    suffix++;
                    renamed = name + "_" + suffix;
                }
                (localRenames ??= new Dictionary<LocalSymbol, string>(
                    ReferenceEqualityComparer.Instance)).Add(local, renamed);
            }
        }

        // 变量名解析（读写共用）：撞名改名表命中取改名，否则走
        // EmittingFacility.ValueVariableName 同名规则（含可变参数
        // 隐藏包映射）
        public string VariableNameOf(SemanticSymbol symbol)
        {
            if (symbol is LocalSymbol local
                && localRenames != null
                && localRenames.TryGetValue(local, out var renamed))
            {
                return renamed;
            }
            return EmittingFacility.ValueVariableName(symbol);
        }
    }
}
