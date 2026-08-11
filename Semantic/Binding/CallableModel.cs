using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // lambda 对象模型类型门面（SYNTAX §5.2）：core 命名空间内源码声明的
    // Func/Action/AsyncFunc/AsyncAction 四家族（0–32 参数元数预生成）与
    // Cell/ReadonlyCell 捕获单元的统一定位/构造入口。
    //
    // Cell 族在首次定位时完成特权认领（幂等）：BilStandardConstructor 使
    // canonical 投影为 BIL §6.3 特权拼写 .cell<T>/.readonly_cell<T>；
    // DerivesSharedSafetyFromTypeArgument 使 shared 安全性按 T 透传
    // （与 Box/Nullable 同例——Cell 是编译器闭包 plumbing 的容器壳）。
    internal static class CallableModel
    {
        // SYNTAX §5.1：lambda 形参个数硬性上限（与 stdlib 预生成元数一致）
        public const int MaxLambdaParameters = 32;

        // core 命名空间定位（缺 stdlib 的测试驱动返回 null——调用侧容错归口）
        private static NamespaceSymbol? CoreNamespace(CompilationUnit unit)
        {
            return unit.Symbols.GlobalNamespace.ChildNamespaces
                .FirstOrDefault(n => n.Name == "core");
        }

        // 按 名+泛型元数 定位 core 下的类型定义（同名不同元数合法共存，§15.3）
        private static TypeSymbol? FindCoreType(CompilationUnit unit, string name, int arity)
        {
            return CoreNamespace(unit)?.Types.FirstOrDefault(
                t => t.Name == name && t.GenericParameters.Count == arity);
        }

        // Cell/ReadonlyCell 定义（首次定位时认领特权拼写与 shared 透传，幂等）
        public static TypeSymbol? FindCellDefinition(CompilationUnit unit)
        {
            var definition = FindCoreType(unit, "Cell", 1);
            if (definition != null)
            {
                definition.BilStandardConstructor = ".cell";
                definition.DerivesSharedSafetyFromTypeArgument = true;
            }
            return definition;
        }

        public static TypeSymbol? FindReadonlyCellDefinition(CompilationUnit unit)
        {
            var definition = FindCoreType(unit, "ReadonlyCell", 1);
            if (definition != null)
            {
                definition.BilStandardConstructor = ".readonly_cell";
                definition.DerivesSharedSafetyFromTypeArgument = true;
            }
            return definition;
        }

        // lambda 隐藏类基类选择：AsyncFunc/AsyncAction（shared，async lambda）或
        // Func/Action；元数 = 形参个数 +（有返回值时 TRet 一个）。缺失返回 null
        public static TypeSymbol? FindCallableBaseDefinition(CompilationUnit unit,
            bool isAsync, bool hasResult, int parameterCount)
        {
            var name = isAsync
                ? (hasResult ? "AsyncFunc" : "AsyncAction")
                : (hasResult ? "Func" : "Action");
            return FindCoreType(unit, name, parameterCount + (hasResult ? 1 : 0));
        }

        // lambda 隐藏类基类构造：实参序 = [TRet?] + 形参类型序
        public static TypeSymbol? ConstructCallableBase(CompilationUnit unit,
            bool isAsync, SemanticSymbol? returnType, IReadOnlyList<SemanticSymbol> parameterTypes)
        {
            var definition = FindCallableBaseDefinition(unit, isAsync,
                returnType != null, parameterTypes.Count);
            if (definition == null) return null;
            var arguments = new List<SemanticSymbol>();
            if (returnType != null) arguments.Add(returnType);
            arguments.AddRange(parameterTypes);
            return arguments.Count == 0
                ? definition
                : unit.Symbols.GetConstructedType(definition, arguments);
        }

        // 构造 Cell/ReadonlyCell 构造类型（定义缺失返回 null，调用侧归口诊断）
        public static TypeSymbol? ConstructCell(CompilationUnit unit, SemanticSymbol elementType,
            bool readOnly)
        {
            var definition = readOnly ? FindReadonlyCellDefinition(unit) : FindCellDefinition(unit);
            return definition == null
                ? null
                : unit.Symbols.GetConstructedType(definition, elementType);
        }

        // Cell/ReadonlyCell 上的访问器方法（定义级符号——BIL 成员引用恒为
        // 定义级 canonical，宿主泛型实参代入由验证器按 §6.4 严格口径处理）：
        // getValue 两家都有；setValue 仅 Cell（ReadonlyCell 无写通道）
        public static MethodSymbol? FindCellGetValue(CompilationUnit unit, bool readOnly)
        {
            var definition = readOnly ? FindReadonlyCellDefinition(unit) : FindCellDefinition(unit);
            return definition?.Methods.FirstOrDefault(m => m.Name == "getValue");
        }

        public static MethodSymbol? FindCellSetValue(CompilationUnit unit)
        {
            return FindCellDefinition(unit)?.Methods.FirstOrDefault(m => m.Name == "setValue");
        }

        // Cell 构造 init：valueInit = init(value)（声明/实参构造点）；
        // defaultInit = init()（未初始化 var 被捕获的空 cell 构造点）
        public static MethodSymbol? FindCellInit(CompilationUnit unit, bool readOnly,
            bool valueInit)
        {
            var definition = readOnly ? FindReadonlyCellDefinition(unit) : FindCellDefinition(unit);
            return definition?.Methods.FirstOrDefault(m => m.Kind == MethodKind.Init
                && m.Parameters.Count == (valueInit ? 1 : 0));
        }
    }
}
