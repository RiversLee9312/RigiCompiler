using System;
using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler
{
    // cell 隐藏子类合成工厂（统一 cell 存储，SYNTAX §5.2 捕获 / §14.3
    // wrapper 值 / §9.4 局部访问器路线 C）：每个被 cell 盛装的符号（lambda
    // 捕获的局部/参数、被 wrapper 修饰的局部/静态字段、带 getter/setter
    // 的局部）逐变量合成一个 ..cell..UUID 隐藏子类——同 lambda 隐藏类先例：
    // 与声明位置同命名空间、不进符号图容器表、泛型上下文同符号对象共享。
    // 子类自行声明 pub value 字段（wrapper 应用经该字段的 wrapped(W) 标记
    // 承载——同 WrapperApplication 实例，WrapperPlaceLowering 按引用匹配
    // 直接命中）并 override 基类抽象 getValue/setValue；局部访问器的
    // override 体 = 用户访问器体（backing 形态 value 别名 → value 字段；
    // 自动访问器 = 默认透传体）。对 Middleware 而言就是「普通类 + 带标记
    // 字段」，一看即知如何烘焙，不特殊处理 Cell 也可正确工作。
    // 合成方法体直接构造 Bound 节点或经声明点词法作用域绑定用户体，
    // 汇入 env.SyntheticCellBodies 由 BindingDriver 收尾进函数体列表，
    // 走统一 P4 管线。
    internal static class CellClassFactory
    {
        // 局部 cell 化（幂等——多重捕获/既有 wrapper/访问器存储直接复用）
        public static CellStorageInfo? EnsureCellStorage(LocalSymbol local, ASTNode syntax,
            BindContext ctx, BindEnvironment env,
            Scope? scope = null,
            PropertyAccessorASTNode? getterNode = null,
            PropertyAccessorASTNode? setterNode = null)
        {
            if (local.CellStorage != null) return local.CellStorage;
            if (getterNode != null || setterNode != null)
            {
                return local.CellStorage = CreateWithAccessors(local, syntax, ctx, env,
                    scope ?? new Scope(null), getterNode, setterNode);
            }
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
        // 命名空间取声明文件；在册泛型参数 = 宿主类型链（无方法上下文）。
        // 全局字段（Owner == null，裁定 1）：cell 子类即 singleton——
        // 初值表达式在 cell 单例的 init 里求值，main 前就绪
        public static CellStorageInfo? EnsureCellStorage(FieldSymbol field,
            VariableDeclarationASTNode variable, FileContext fileCtx, BindEnvironment env)
        {
            if (field.CellStorage != null) return field.CellStorage;
            var genericParameters = new List<GenericParameterSymbol>();
            for (var t = field.Owner; t != null; t = t.DeclaringType)
            {
                genericParameters.AddRange(t.GenericParameters);
            }
            var isGlobal = field.Owner == null;
            BoundExpression? initializer = null;
            if (isGlobal)
            {
                initializer = WrapperInitSynthesis.BindFieldInitializer(field, variable,
                    fileCtx, env);
            }
            // 带用户访问器：getValue/setValue override 接管用户体（不再绑
            // 独立访问器 fn）；init 元数仍走 Create 既有静态/全局规则
            if (variable.Getter != null || variable.Setter != null)
            {
                return field.CellStorage = CreateForFieldWithAccessors(field, variable,
                    fileCtx, env, genericParameters, initializer, isSingleton: isGlobal);
            }
            return field.CellStorage = Create(fileCtx.Namespace, genericParameters,
                field.FieldType!, field.IsConst, field.AppliedWrappers, variable, env,
                isSingleton: isGlobal, initializer: initializer);
        }

        // 静态/全局 wrapped 字段带用户访问器：复用 Create 的 cell 壳与 init
        // 元数，绑定语境为合成静态宿主（无 this / 无外层局部，不捕获）
        private static CellStorageInfo? CreateForFieldWithAccessors(FieldSymbol field,
            VariableDeclarationASTNode variable, FileContext fileCtx, BindEnvironment env,
            IReadOnlyList<GenericParameterSymbol> genericParameters,
            BoundExpression? initializer, bool isSingleton)
        {
            var host = new MethodSymbol(".field.accessor.bind", MethodKind.Regular,
                owner: field.Owner, ns: field.Namespace, isStatic: true)
            {
                HasBody = false,
                IsSynthetic = true,
            };
            var accessorOuterCtx = new BindContext(host, fileCtx, field.Owner);
            return Create(fileCtx.Namespace, genericParameters,
                field.FieldType!, field.IsConst, field.AppliedWrappers, variable, env,
                isSingleton: isSingleton, initializer: initializer,
                getterNode: variable.Getter, setterNode: variable.Setter,
                hasBacking: field.HasBackingStorage,
                accessorOuterCtx: accessorOuterCtx);
        }

        // 局部访问器 cell 化（M107 路线 C，SYNTAX §9.4）：getValue/setValue
        // override 体 = 用户访问器体；backing 形态 value 别名 → cell.value；
        // 自由变量按 lambda 同规则捕获进 cell（init 追加捕获实参）
        private static CellStorageInfo? CreateWithAccessors(LocalSymbol local, ASTNode syntax,
            BindContext outerCtx, BindEnvironment env, Scope scope,
            PropertyAccessorASTNode? getterNode, PropertyAccessorASTNode? setterNode)
        {
            var elementType = local.Type!;
            var readOnly = local.IsConst;
            var unit = env.Unit;
            if (elementType is ErrorTypeSymbol) return null;
            // M112：在册集合含合成 Owner 共享的外层方法泛型（见
            // InScopeGenericParameters）；仍不在册则显式归口
            var genericParameters = InScopeGenericParameters(outerCtx).ToList();
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
                TypeKind.Class, ns: outerCtx.Frame.FileCtx.Namespace, baseType: baseType,
                isShared: IsSharedElement(elementType))
            {
                Accessibility = Accessibility.Public,
            };
            foreach (var genericParameter in genericParameters)
            {
                cellClass.GenericParameters.Add(genericParameter);
            }
            var cellType = genericParameters.Count == 0
                ? cellClass
                : unit.Symbols.GetConstructedType(cellClass, genericParameters.ToArray());

            var valueField = new FieldSymbol("value", owner: cellClass,
                fieldType: elementType, isConst: readOnly)
            {
                Accessibility = Accessibility.Public,
            };
            valueField.AppliedWrappers.AddRange(local.AppliedWrappers);
            cellClass.Fields.Add(valueField);

            // 先建 getValue/setValue 方法壳（体稍后填），供访问器体绑定 Frame
            var getValue = NewMethod("getValue", MethodKind.Regular, cellClass, elementType,
                isOverride: true);
            cellClass.Methods.Add(getValue);
            MethodSymbol? setValue = null;
            if (!readOnly)
            {
                setValue = NewMethod("setValue", MethodKind.Regular, cellClass, null,
                    isOverride: true);
                setValue.Parameters.Add(new ParameterSymbol("value", elementType));
                cellClass.Methods.Add(setValue);
            }

            // 绑定用户访问器体（词法作用域 = 声明点；isLambda 复用自由变量捕获）
            var hasBacking = local.HasBackingStorage;
            BoundFunctionBody getBody;
            HashSet<SemanticSymbol> captured = new HashSet<SemanticSymbol>();
            if (getterNode != null)
            {
                var (block, bodyLocals, bodyCaptures) = BindAccessorBody(getterNode, getValue,
                    valueField, hasBacking, isSetter: false, elementType, scope, outerCtx, env);
                getBody = new BoundFunctionBody(getValue, bodyLocals, block);
                foreach (var c in bodyCaptures) captured.Add(c);
                local.Getter = getValue;
            }
            else
            {
                // 仅 setter：保留默认 getValue（P3 读侧已拒，体不可达）
                getBody = new BoundFunctionBody(getValue, Array.Empty<LocalSymbol>(),
                    new BoundBlock(syntax, new BoundStatement[]
                    {
                        new BoundReturnStatement(syntax, new BoundFieldAccessExpression(syntax,
                            new BoundThisExpression(syntax, cellClass), valueField, elementType)),
                    }));
            }

            BoundFunctionBody? setBody = null;
            if (setValue != null)
            {
                if (setterNode != null && !local.IsConst)
                {
                    var (block, bodyLocals, bodyCaptures) = BindAccessorBody(setterNode, setValue,
                        valueField, hasBacking, isSetter: true, elementType, scope, outerCtx, env);
                    setBody = new BoundFunctionBody(setValue, bodyLocals, block);
                    foreach (var c in bodyCaptures) captured.Add(c);
                    local.Setter = setValue;
                }
                else
                {
                    // 仅 getter 的 var：默认 setValue 透传（P3 写侧已拒）
                    var parameter = setValue.Parameters[0];
                    setBody = new BoundFunctionBody(setValue, Array.Empty<LocalSymbol>(),
                        new BoundBlock(syntax, new BoundStatement[]
                        {
                            AssignValue(syntax, cellClass, valueField, elementType,
                                new BoundValueReferenceExpression(syntax, parameter, elementType),
                                forSetter: true),
                        }));
                }
            }

            // 自由变量捕获（排除本局部自身——声明点尚未入 scope，防御性剔除）
            captured.Remove(local);
            var captures = BuildAccessorCaptures(syntax, captured, outerCtx, cellClass, env);

            // init 壳：值参 + 捕获实参；空构造仅 Cell 风味
            MethodSymbol? defaultInit = null;
            if (!readOnly)
            {
                defaultInit = NewMethod("init", MethodKind.Init, cellClass, null,
                    isOverride: false);
                AddCaptureParameters(defaultInit, captures);
                cellClass.Methods.Insert(0, defaultInit);
            }
            var valueInit = NewMethod("init", MethodKind.Init, cellClass, null,
                isOverride: false);
            valueInit.Parameters.Add(new ParameterSymbol("value", elementType));
            AddCaptureParameters(valueInit, captures);
            cellClass.Methods.Insert(defaultInit == null ? 0 : 1, valueInit);

            // init 体
            var methods = new List<BoundFunctionBody>();
            if (defaultInit != null)
            {
                methods.Add(new BoundFunctionBody(defaultInit, Array.Empty<LocalSymbol>(),
                    new BoundBlock(syntax, CaptureAssignStatements(syntax, cellClass, defaultInit,
                        captures, valueParamOffset: 0))));
            }
            var valueInitStmts = new List<BoundStatement>
            {
                AssignValue(syntax, cellClass, valueField, elementType,
                    new BoundValueReferenceExpression(syntax, valueInit.Parameters[0],
                        elementType), forSetter: false),
            };
            valueInitStmts.AddRange(CaptureAssignStatements(syntax, cellClass, valueInit,
                captures, valueParamOffset: 1));
            methods.Add(new BoundFunctionBody(valueInit, Array.Empty<LocalSymbol>(),
                new BoundBlock(syntax, valueInitStmts)));
            methods.Add(getBody);
            if (setBody != null) methods.Add(setBody);

            var info = new CellStorageInfo(cellClass, cellType, readOnly, valueField,
                valueInit, defaultInit, captures);
            cellClass.CellStorage = info;
            foreach (var body in methods) env.SyntheticCellBodies.Add(body);
            // M109b-1：value 字段 wrapped(W) → cell 子类 ..init.wrapper
            WrapperInitSynthesis.SynthesizeForCell(info, syntax, env);
            return info;
        }

        // 字段访问器体或默认透传：有用户体则走 cell 版 BindAccessorBody
        //（捕获丢弃——静态/全局无 this/外层局部）；否则 get 读真实 value、
        // set 写 ..value
        private static BoundFunctionBody BindFieldAccessorOrDefault(
            PropertyAccessorASTNode? accessorNode, MethodSymbol method, FieldSymbol valueField,
            bool hasBacking, bool isSetter, SemanticSymbol elementType, ASTNode syntax,
            TypeSymbol cellClass, BindEnvironment env, BindContext? accessorOuterCtx)
        {
            if (accessorNode != null && accessorOuterCtx != null)
            {
                var (block, bodyLocals, _) = BindAccessorBody(accessorNode, method, valueField,
                    hasBacking, isSetter, elementType, new Scope(null), accessorOuterCtx, env);
                return new BoundFunctionBody(method, bodyLocals, block);
            }
            if (isSetter)
            {
                var parameter = method.Parameters[0];
                return new BoundFunctionBody(method, Array.Empty<LocalSymbol>(),
                    new BoundBlock(syntax, new BoundStatement[]
                    {
                        AssignValue(syntax, cellClass, valueField, elementType,
                            new BoundValueReferenceExpression(syntax, parameter, elementType),
                            forSetter: true),
                    }));
            }
            return new BoundFunctionBody(method, Array.Empty<LocalSymbol>(),
                new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundReturnStatement(syntax, new BoundFieldAccessExpression(syntax,
                        new BoundThisExpression(syntax, cellClass), valueField, elementType)),
                }));
        }

        // 单访问器体绑定：独立 BindContext（Frame.Method = getValue/setValue，
        // LookupHost/DeclaringType/this 沿外层——同 lambda）；backing 形态
        // Accessor.Set(valueField)；自动体合成同字段访问器
        private static (BoundBlock Body, IReadOnlyList<LocalSymbol> Locals,
            HashSet<SemanticSymbol> Captures) BindAccessorBody(
            PropertyAccessorASTNode accessorNode, MethodSymbol method, FieldSymbol valueField,
            bool hasBacking, bool isSetter, SemanticSymbol elementType, Scope outerScope,
            BindContext outerCtx, BindEnvironment env)
        {
            var accessorCtx = new BindContext(method, outerCtx.Frame.FileCtx,
                outerCtx.Frame.DeclaringType, isLambda: true, thisSymbol: outerCtx.This,
                lambdaThisType: outerCtx.IsLambda
                    ? outerCtx.LambdaThisType
                    : PathFacility.EffectiveThisType(outerCtx, env),
                lookupHost: outerCtx.Frame.LookupHost);
            accessorCtx.Flow.InheritAssignedFrom(outerCtx.Flow);
            if (hasBacking) accessorCtx.Accessor.Set(valueField, isSetter);

            var accessorScope = new Scope(outerScope);
            foreach (var outerParameter in outerCtx.Frame.Method.Parameters)
                accessorScope.Declare(outerParameter);
            // setter 的 value 参数经 Frame.Method.Parameters 解析，无需入 scope；
            // 标记为「非捕获」（同 lambda 自身参数）——value 在 Method.Parameters 内，
            // PathVisitors 走参数路径不进 CapturedSymbols

            BoundBlock body;
            if (accessorNode.Body != null)
            {
                body = BlockDispatcher.Visit(accessorNode.Body, accessorScope, accessorCtx, env);
            }
            else if (isSetter)
            {
                body = new BoundBlock(accessorNode, new List<BoundStatement>());
            }
            else
            {
                // 自动 getter：return value（backing 读）
                var statements = new List<BoundStatement>();
                if (elementType is not ErrorTypeSymbol)
                {
                    statements.Add(new BoundReturnStatement(accessorNode,
                        PathFacility.MakeBackingFieldReference(accessorNode, valueField,
                            elementType, accessorCtx.Frame, forSetter: false, env.Unit.Symbols)));
                }
                body = new BoundBlock(accessorNode, statements);
            }
            // backing setter：体首隐含 value 字段 = value 参数
            if (isSetter && hasBacking && elementType is not ErrorTypeSymbol)
            {
                var implicitAssign = new BoundAssignmentStatement(accessorNode,
                    PathFacility.MakeBackingFieldReference(accessorNode, valueField, elementType,
                        accessorCtx.Frame, forSetter: true, env.Unit.Symbols),
                    new BoundValueReferenceExpression(accessorNode, method.Parameters[0],
                        elementType));
                body = new BoundBlock(body.Syntax,
                    new List<BoundStatement> { implicitAssign }.Concat(body.Statements).ToList());
            }
            if (method.ReturnType != null && !BoundAnalysis.GuaranteesReturn(body))
            {
                env.Error(accessorNode.Span,
                    $"Function '{method.Name}' must return a value on all code paths");
            }
            AsyncGates.CheckFunctionBody(body, env);
            // 嵌套 lambda 捕获向外传递（同 LambdaVisitor）
            foreach (var nested in accessorCtx.CapturedSymbols)
            {
                // 已在 accessorCtx.CapturedSymbols
            }
            return (body, accessorCtx.Locals.ToList(), accessorCtx.CapturedSymbols);
        }

        private static IReadOnlyList<LambdaCaptureEntry> BuildAccessorCaptures(
            ASTNode syntax, HashSet<SemanticSymbol> captured, BindContext outerCtx,
            TypeSymbol cellClass, BindEnvironment env)
        {
            var ordered = captured
                .OrderBy(symbol => symbol is ThisSymbol ? 0 : 1)
                .ThenBy(symbol => symbol.Name, StringComparer.Ordinal);
            var captures = new List<LambdaCaptureEntry>();
            foreach (var symbol in ordered)
            {
                switch (symbol)
                {
                    case ThisSymbol:
                        {
                            var field = new FieldSymbol(".capture.this", owner: cellClass,
                                fieldType: (outerCtx.IsLambda
                                    ? outerCtx.LambdaThisType
                                    : PathFacility.EffectiveThisType(outerCtx, env))
                                    ?? env.Unit.Symbols.ErrorType);
                            cellClass.Fields.Add(field);
                            captures.Add(new LambdaCaptureEntry(symbol, field,
                                isThis: true, isReadOnly: true));
                            break;
                        }
                    case LocalSymbol outerLocal:
                        {
                            var storage = EnsureCellStorage(outerLocal, syntax, outerCtx, env);
                            var field = new FieldSymbol(".capture." + outerLocal.Name,
                                owner: cellClass,
                                fieldType: storage?.CellType ?? env.Unit.Symbols.ErrorType);
                            cellClass.Fields.Add(field);
                            captures.Add(new LambdaCaptureEntry(symbol, field,
                                isThis: false,
                                isReadOnly: storage?.IsReadOnly ?? outerLocal.IsConst));
                            outerCtx.Flow.ClearRoot(outerLocal);
                            break;
                        }
                    case ParameterSymbol parameter:
                        {
                            var storage = EnsureCellStorage(parameter, syntax, outerCtx, env);
                            var field = new FieldSymbol(".capture." + parameter.Name,
                                owner: cellClass,
                                fieldType: storage?.CellType ?? env.Unit.Symbols.ErrorType);
                            cellClass.Fields.Add(field);
                            captures.Add(new LambdaCaptureEntry(symbol, field,
                                isThis: false, isReadOnly: false));
                            outerCtx.Flow.ClearRoot(parameter);
                            break;
                        }
                }
            }
            return captures;
        }

        private static void AddCaptureParameters(MethodSymbol init,
            IReadOnlyList<LambdaCaptureEntry> captures)
        {
            for (var i = 0; i < captures.Count; i++)
            {
                init.Parameters.Add(new ParameterSymbol("c" + i, captures[i].Field.FieldType));
            }
        }

        private static List<BoundStatement> CaptureAssignStatements(ASTNode syntax,
            TypeSymbol cellClass, MethodSymbol init, IReadOnlyList<LambdaCaptureEntry> captures,
            int valueParamOffset)
        {
            var statements = new List<BoundStatement>();
            for (var i = 0; i < captures.Count; i++)
            {
                var entry = captures[i];
                var parameter = init.Parameters[valueParamOffset + i];
                statements.Add(new BoundAssignmentStatement(syntax,
                    new BoundFieldAccessExpression(syntax,
                        new BoundThisExpression(syntax, cellClass), entry.Field,
                        entry.Field.FieldType!),
                    new BoundValueReferenceExpression(syntax, parameter,
                        entry.Field.FieldType!)));
            }
            return statements;
        }

        // 隐藏子类合成：基类 = Cell<T>/ReadonlyCell<T> 构造类型；stdlib
        // 缺席时毒化返回 null（诊断落袋，符号保持未 cell 化——P4 消费点
        // 按缺失各自归口，不二次报）
        private static CellStorageInfo? Create(NamespaceSymbol ns,
            IReadOnlyList<GenericParameterSymbol> genericParameters, SemanticSymbol elementType,
            bool readOnly, IReadOnlyList<WrapperApplication> wrappers, ASTNode syntax,
            BindEnvironment env, bool isSingleton = false, BoundExpression? initializer = null,
            PropertyAccessorASTNode? getterNode = null, PropertyAccessorASTNode? setterNode = null,
            bool hasBacking = false, BindContext? accessorOuterCtx = null)
        {
            var unit = env.Unit;
            // 毒化静默（类型解析失败的诊断已在前序落袋，不合成残缺子类）
            if (elementType is ErrorTypeSymbol) return null;
            // 泛型参数值类型的 cell 化要求该参数在合成点的在册集合内
            //（M112：外层方法泛型经 Owner 共享已覆盖 lambda/cell 合成主路径；
            // 仍不在册的残缺场景显式归口，不合成残缺子类）
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
                TypeKind.Class, ns: ns, baseType: baseType,
                isShared: isSingleton || IsSharedElement(elementType))
            {
                Accessibility = Accessibility.Public,
                IsSingleton = isSingleton,
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
            MethodSymbol? defaultInit = null;
            // 空构造 init()：未初始化 var 的空 cell 构造点（体为空块）；源级
            // DA 保证空值不可观测（读必先经赋值）。singleton cell（全局字段）
            // 的无参 init 只承担初值求值、写 value 字段（wrapper 安装走
            // ..init.wrapper，§14.5）——ReadonlyCell 风味也补一个无参 init
            // 供 VM 构造单例
            if (!readOnly || initializer != null)
            {
                var method = NewMethod("init", MethodKind.Init, cellClass, null,
                    isOverride: false);
                var initStatements = new List<BoundStatement>();
                if (initializer != null)
                {
                    initStatements.Add(AssignValue(syntax, cellClass, valueField, elementType,
                        initializer, forSetter: false));
                }
                defaultInit = method;
                methods.Add((method, new BoundFunctionBody(method,
                    Array.Empty<LocalSymbol>(), new BoundBlock(syntax, initStatements))));
            }
            // init(value)：体 = this.value = value（const 字段写由 init
            // 豁免，BIL §21.8）。singleton cell（全局字段）初值在无参 init
            // 内求值，不另设 1 元 init(value)——用户裁定的 init 元数规则：
            // 静态/全局 → 0 元、函数内 const → 1 元（Cell 子类同理）
            MethodSymbol valueInit;
            if (isSingleton)
            {
                valueInit = defaultInit
                    ?? throw new CompilerInternalException("singleton cell 缺无参 init: " +
                        cellClass.Name);
            }
            else
            {
                valueInit = NewMethod("init", MethodKind.Init, cellClass, null,
                    isOverride: false);
                var initParameter = new ParameterSymbol("value", elementType);
                valueInit.Parameters.Add(initParameter);
                methods.Add((valueInit, new BoundFunctionBody(valueInit,
                    Array.Empty<LocalSymbol>(), new BoundBlock(syntax, new BoundStatement[]
                    {
                        AssignValue(syntax, cellClass, valueField, elementType,
                            new BoundValueReferenceExpression(syntax, initParameter,
                                elementType), forSetter: false),
                    }))));
            }
            // override getValue：用户 getter 体入壳；缺侧默认透传读真实 value
            // 字段（VM 识别直读）。field.Getter 符号保持原样，不改指本方法
            var getValue = NewMethod("getValue", MethodKind.Regular, cellClass, elementType,
                isOverride: true);
            methods.Add((getValue, BindFieldAccessorOrDefault(getterNode, getValue, valueField,
                hasBacking, isSetter: false, elementType, syntax, cellClass, env,
                accessorOuterCtx)));
            // override setValue：用户 setter 体入壳；缺侧默认透传写 ..value
            // （仅 Cell 风味；const/ReadonlyCell 无 setValue）
            if (!readOnly)
            {
                var setValue = NewMethod("setValue", MethodKind.Regular, cellClass, null,
                    isOverride: true);
                setValue.Parameters.Add(new ParameterSymbol("value", elementType));
                methods.Add((setValue, BindFieldAccessorOrDefault(setterNode, setValue,
                    valueField, hasBacking, isSetter: true, elementType, syntax, cellClass,
                    env, accessorOuterCtx)));
            }
            foreach (var (method, _) in methods) cellClass.Methods.Add(method);
            var info = new CellStorageInfo(cellClass, cellType, readOnly, valueField,
                valueInit, defaultInit);
            cellClass.CellStorage = info;
            foreach (var (_, body) in methods) env.SyntheticCellBodies.Add(body);
            // M109b-1：value 字段 wrapped(W) → cell 子类 ..init.wrapper。
            // singleton cell（全局字段）走无参 ..init.wrapper（wrapper 实参按
            // 全局作用域在体内求值）；非 singleton 走有参 new.wrapped 形态
            if (isSingleton)
            {
                WrapperInitSynthesis.SynthesizeForSingletonCell(info, syntax, env);
            }
            else
            {
                WrapperInitSynthesis.SynthesizeForCell(info, syntax, env);
            }
            return info;
        }

        // cell 隐藏子类 shared 判定（用户裁定，堵死「经 lambda 捕获 / Value
        // wrapper 把普通值偷带进并发上下文」）：仅当元素类型**本身被显式声明
        // 为 shared**（声明修饰符 IsShared）时子类才 shared——
        //   · 元素 = 显式 shared 类型（含其构造类型 Foo<...>——IsShared 随定义
        //     传播）→ 子类 shared；
        //   · 元素 = i32/bool/char 等非 rich 内建值类型 → 不 shared（即便它们
        //     通常 shared-safe——值经 cell 装箱成为可变容器，跨协程共享须靠
        //     显式 shared 声明放行，不自动豁免）；
        //   · 元素 = 未标 shared 的普通 struct/class/wrapper → 不 shared；
        //   · 元素 = 泛型参数 T → 不 shared（无法静态证明显式 shared）。
        // 非 shared 的 cell 会被 shared 闸门（async 边界 / 全局静态字段）拦下；
        // 显式 shared 元素合成的 shared cell 则合法放行。
        private static bool IsSharedElement(SemanticSymbol elementType) =>
            elementType is TypeSymbol { IsShared: true };

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

        // this.value = <value>；setValue 体改指 ..value，init 体保持真实 value 字段
        private static BoundAssignmentStatement AssignValue(ASTNode syntax, TypeSymbol cellClass,
            FieldSymbol valueField, SemanticSymbol elementType, BoundExpression value,
            bool forSetter)
        {
            var target = PathFacility.BackingStorageField(valueField, forSetter);
            return new BoundAssignmentStatement(syntax,
                new BoundFieldAccessExpression(syntax,
                    new BoundThisExpression(syntax, cellClass), target, elementType),
                value);
        }

        // 外层方法 + 外层声明类型链 + 合成方法 Owner 的泛型参数（声明类型链
        // 先行、Owner 补齐、方法随后——与 NameResolver.FindGenericParameter
        // 的查找范围一致；同符号对象共享，HashSet 去重保序）。
        // M112：lambda $$call / cell getValue·setValue·init 等合成方法自身
        // 无 GenericParameters，外层**方法**泛型经 M103 挂在 Owner（隐藏类/
        // cell 子类）GenericParameters——须从 Owner 纳入在册集合，否则嵌套
        // lambda 捕获 T 型局部时守卫误拒、体内 `var y: T` 亦无法解析。
        internal static IEnumerable<GenericParameterSymbol> InScopeGenericParameters(
            BindContext ctx)
        {
            var seen = new HashSet<GenericParameterSymbol>();
            for (var t = ctx.Frame.DeclaringType; t != null; t = t.DeclaringType)
            {
                foreach (var parameter in t.GenericParameters)
                {
                    if (seen.Add(parameter)) yield return parameter;
                }
            }
            if (ctx.Frame.Method.Owner != null)
            {
                foreach (var parameter in ctx.Frame.Method.Owner.GenericParameters)
                {
                    if (seen.Add(parameter)) yield return parameter;
                }
            }
            foreach (var parameter in ctx.Frame.Method.GenericParameters)
            {
                if (seen.Add(parameter)) yield return parameter;
            }
        }
    }
}
