using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // cell 隐藏子类合成工厂（统一 cell 存储，SYNTAX §5.2 捕获 / §14.3
    // wrapper 值）：每个被 cell 盛装的符号（lambda 捕获的局部/参数、被
    // wrapper 修饰的局部/静态字段）逐变量合成一个 ..cell..UUID 隐藏子类
    // ——同 lambda 隐藏类先例：与声明位置同命名空间、不进符号图容器表、
    // 泛型上下文同符号对象共享。子类自行声明 pub value 字段（wrapper
    // 应用经该字段的 wrapped(W) 标记承载——同 WrapperApplication 实例，
    // WrapperPlaceLowering 按引用匹配直接命中）并 override 基类抽象
    // getValue/setValue；对 Middleware 而言就是「普通类 + 带标记字段」，
    // 一看即知如何烘焙，不特殊处理 Cell 也可正确工作。
    // 合成方法体直接构造 Bound 节点（lambda SynthesizeInit 先例——合成
    // 代码无 DA/return 问题），汇入 env.SyntheticCellBodies 由
    // BindingDriver 收尾进函数体列表，走统一 P4 管线。
    internal static class CellClassFactory
    {
        // 局部 cell 化（幂等——多重捕获/既有 wrapper 存储直接复用）
        public static CellStorageInfo? EnsureCellStorage(LocalSymbol local, ASTNode syntax,
            BindContext ctx, BindEnvironment env)
        {
            if (local.CellStorage != null) return local.CellStorage;
            return local.CellStorage = Create(ctx.Frame.FileCtx.Namespace,
                InScopeGenericParameters(ctx).ToList(), local.Type!, local.IsConst,
                local.AppliedWrappers, syntax, env);
        }

        // 参数 cell 化（幂等；参数无 const 概念，恒 Cell 风味）
        public static CellStorageInfo? EnsureCellStorage(ParameterSymbol parameter,
            ASTNode syntax, BindContext ctx, BindEnvironment env)
        {
            if (parameter.CellStorage != null) return parameter.CellStorage;
            return parameter.CellStorage = Create(ctx.Frame.FileCtx.Namespace,
                InScopeGenericParameters(ctx).ToList(), parameter.Type!, readOnly: false,
                Array.Empty<WrapperApplication>(), syntax, env);
        }

        // 静态/全局字段 cell 化（幂等；P3 BindingDriver 阶段 1.6 调用）：
        // 命名空间取声明文件；在册泛型参数 = 宿主类型链（无方法上下文）
        public static CellStorageInfo? EnsureCellStorage(FieldSymbol field, ASTNode syntax,
            NamespaceSymbol ns, BindEnvironment env)
        {
            if (field.CellStorage != null) return field.CellStorage;
            var genericParameters = new List<GenericParameterSymbol>();
            for (var t = field.Owner; t != null; t = t.DeclaringType)
            {
                genericParameters.AddRange(t.GenericParameters);
            }
            return field.CellStorage = Create(ns, genericParameters, field.FieldType!,
                field.IsConst, field.AppliedWrappers, syntax, env);
        }

        // 隐藏子类合成：基类 = Cell<T>/ReadonlyCell<T> 构造类型；stdlib
        // 缺席时毒化返回 null（诊断落袋，符号保持未 cell 化——P4 消费点
        // 按缺失各自归口，不二次报）
        private static CellStorageInfo? Create(NamespaceSymbol ns,
            IReadOnlyList<GenericParameterSymbol> genericParameters, SemanticSymbol elementType,
            bool readOnly, IReadOnlyList<WrapperApplication> wrappers, ASTNode syntax,
            BindEnvironment env)
        {
            var unit = env.Unit;
            // 毒化静默（类型解析失败的诊断已在前序落袋，不合成残缺子类）
            if (elementType is ErrorTypeSymbol) return null;
            // 泛型参数值类型的 cell 化要求该参数在合成点的在册集合内
            //（lambda 体内包装外层方法泛型参数等缺席场景无法转发 typeid——
            // 显式归口，不合成残缺子类）
            if (elementType is GenericParameterSymbol genericElement
                && !genericParameters.Contains(genericElement))
            {
                env.Error(syntax.Span,
                    "P3: cell storage for generic-parameter-typed values in this context " +
                    "is not supported yet");
                return null;
            }
            var baseType = CallableModel.ConstructCell(unit, elementType, readOnly);
            if (baseType == null)
            {
                env.Error(syntax.Span,
                    "P3: cell storage requires the core::Cell/ReadonlyCell family " +
                    "(stdlib not loaded)");
                return null;
            }
            var cellClass = new TypeSymbol(
                "..cell.." + Guid.NewGuid().ToString("N"),
                TypeKind.Class, ns: ns, baseType: baseType)
            {
                Accessibility = Accessibility.Public,
            };
            // 泛型上下文共享：在册泛型参数以同符号对象挂进隐藏子类
            //（序即构造点 type 实参序——声明类型链先行、方法随后）
            foreach (var genericParameter in genericParameters)
            {
                cellClass.GenericParameters.Add(genericParameter);
            }
            var cellType = genericParameters.Count == 0
                ? cellClass
                : unit.Symbols.GetConstructedType(cellClass, genericParameters.ToArray());

            // pub value 字段（盛装值 + wrapper 应用载体；ReadonlyCell 风味
            // 为 const——无写通道，构造后只读）
            var valueField = new FieldSymbol("value", owner: cellClass,
                fieldType: elementType, isConst: readOnly)
            {
                Accessibility = Accessibility.Public,
            };
            valueField.AppliedWrappers.AddRange(wrappers);
            cellClass.Fields.Add(valueField);

            // 成员与体序：init → init(value) → getValue → setValue（声明与
            // fn 发射同序）。体一律直接构造 Bound 节点（Syntax 回指合成来源）
            var methods = new List<(MethodSymbol Method, BoundFunctionBody Body)>();
            if (!readOnly)
            {
                // 空构造 init()（体为空块）：未初始化 var 的空 cell 构造点——
                // 源级 DA 保证空值不可观测（读必先经赋值）
                var method = NewMethod("init", MethodKind.Init, cellClass, null,
                    isOverride: false);
                methods.Add((method, new BoundFunctionBody(method,
                    Array.Empty<LocalSymbol>(), new BoundBlock(syntax,
                        Array.Empty<BoundStatement>()))));
            }
            // init(value)：体 = this.value = value（const 字段写由 init
            // 豁免，BIL §21.8）
            var valueInit = NewMethod("init", MethodKind.Init, cellClass, null,
                isOverride: false);
            var initParameter = new ParameterSymbol("value", elementType);
            valueInit.Parameters.Add(initParameter);
            methods.Add((valueInit, new BoundFunctionBody(valueInit,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, new BoundStatement[]
                {
                    AssignValue(syntax, cellClass, valueField, elementType,
                        new BoundValueReferenceExpression(syntax, initParameter, elementType)),
                }))));
            // override getValue：return this.value
            var getValue = NewMethod("getValue", MethodKind.Regular, cellClass, elementType,
                isOverride: true);
            methods.Add((getValue, new BoundFunctionBody(getValue,
                Array.Empty<LocalSymbol>(), new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundReturnStatement(syntax, new BoundFieldAccessExpression(syntax,
                        new BoundThisExpression(syntax, cellClass), valueField, elementType)),
                }))));
            // override setValue：this.value = value（仅 Cell 风味）
            if (!readOnly)
            {
                var setValue = NewMethod("setValue", MethodKind.Regular, cellClass, null,
                    isOverride: true);
                var parameter = new ParameterSymbol("value", elementType);
                setValue.Parameters.Add(parameter);
                methods.Add((setValue, new BoundFunctionBody(setValue,
                    Array.Empty<LocalSymbol>(), new BoundBlock(syntax, new BoundStatement[]
                    {
                        AssignValue(syntax, cellClass, valueField, elementType,
                            new BoundValueReferenceExpression(syntax, parameter, elementType)),
                    }))));
            }
            foreach (var (method, _) in methods) cellClass.Methods.Add(method);
            var info = new CellStorageInfo(cellClass, cellType, readOnly, valueField,
                valueInit, readOnly ? null : methods[0].Method);
            cellClass.CellStorage = info;
            foreach (var (_, body) in methods) env.SyntheticCellBodies.Add(body);
            return info;
        }

        private static MethodSymbol NewMethod(string name, MethodKind kind, TypeSymbol owner,
            SemanticSymbol? returnType, bool isOverride)
        {
            return new MethodSymbol(name, kind, owner: owner, returnType: returnType)
            {
                Accessibility = Accessibility.Public,
                IsOverride = isOverride,
                HasBody = true,
            };
        }

        // this.value = <value>
        private static BoundAssignmentStatement AssignValue(ASTNode syntax, TypeSymbol cellClass,
            FieldSymbol valueField, SemanticSymbol elementType, BoundExpression value)
        {
            return new BoundAssignmentStatement(syntax,
                new BoundFieldAccessExpression(syntax,
                    new BoundThisExpression(syntax, cellClass), valueField, elementType),
                value);
        }

        // 外层方法 + 外层声明类型链的泛型参数（声明类型链先行、方法随后——
        // 与 NameResolver.FindGenericParameter 的查找范围一致；同符号对象共享）
        internal static IEnumerable<GenericParameterSymbol> InScopeGenericParameters(
            BindContext ctx)
        {
            for (var t = ctx.Frame.DeclaringType; t != null; t = t.DeclaringType)
            {
                foreach (var parameter in t.GenericParameters) yield return parameter;
            }
            foreach (var parameter in ctx.Frame.Method.GenericParameters) yield return parameter;
        }
    }
}
