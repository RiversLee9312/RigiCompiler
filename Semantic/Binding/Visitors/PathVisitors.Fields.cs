
namespace RigiCompiler
{
    internal static partial class PathFacility
    {
        // Fields 职责；与主文件共享同一类型、字段及生命周期。

        // 字段引用上色（迁移自旧 BindSession.BindFieldReference，行为不变）：
        // 无标注字段类型推断归后续（诊断）；泛型字段类型最小替换（S7f）：
        // 实例字段以宿主（method.Owner）为 receiver 链取构造实参；全局/
        // static 字段声明类型不含泛型参数，原样直通；实例字段在实例上下文
        // 补 this（静态上下文诊断）。
        // g7：staticHostType 为构造类型容器路径（`Box\<i32>.zero`）的代入
        // 宿主——静态字段声明类型中的宿主泛型参数按容器构造实参替换
        // （zero: T → i32）；null = 旧行为（当前 this/宿主）。
        // S8e（SYNTAX §16.1/§9.4.1）：使用点访问控制——带访问器字段读需
        // getter 存在且可见（写由赋值侧检查 setter，forAssignment 直达时
        // 跳过读侧检查），字段自身可见性不再检查（由访问器承载）；
        // 无访问器字段读写字位均检查字段可见性
        public static BoundExpression? BindFieldReference(ASTNode node, FieldSymbol field,
            BindContext ctx, BindEnvironment env, bool forAssignment = false,
            TypeSymbol? staticHostType = null)
        {
            if (field.Getter != null || field.Setter != null)
            {
                if (!forAssignment && !CheckReadable(field, node.Span, ctx, env))
                {
                    return null;
                }
            }
            else if (!ctx.Frame.CanAccess(field))
            {
                env.Error(node.Span, AccessChecker.InaccessibleMessage(field));
                return null;
            }
            if (field.FieldType == null)
            {
                env.Error(node.Span, $"P3: field '{field.Name}' has no type annotation " +
                    "(field type inference is not supported yet)");
                return null;
            }
            // S9a 放行：替换失败的泛型参数类型原样保留（定义级宿主场景）；
            // 返回值恒非空（FieldType 非 null 上面已查，SymbolLookup 契约）。
            // lambda 语境（§5.2）：有效 this 类型取 LambdaThisType（外层 this），
            // Frame.Method 是隐藏类 $$call（恒实例）——代入宿主与 this 上色
            // 都必须用外层有效 this 类型，且裸字段访问即 this 捕获
            var effectiveThis = EffectiveThisType(ctx, env);
            var fieldType = SymbolLookup.SubstituteFieldType(field, staticHostType ?? effectiveThis
                ?? ctx.Frame.Method.Owner, env.Unit.Symbols);
            if (field.Owner != null && !field.IsStatic)
            {
                // 实例字段（S7c-2）：当前上下文有 this（实例方法/ext 方法
                // 体内，method.Owner 统一承载宿主）→ this.field；静态
                // 上下文（static 方法/全局函数/默认值表达式）→ 诊断
                if (effectiveThis != null)
                {
                    if (ctx.IsLambda && ctx.This != null) ctx.CapturedSymbols.Add(ctx.This);
                    var access = new BoundFieldAccessExpression(node,
                        new BoundThisExpression(node, effectiveThis), field, fieldType);
                    // S8b：this.f 收窄（const 字段 + 非 init 体内，经
                    // ConstFieldRules.IsNarrowable 判定）；赋值 place
                    // （forAssignment）不包——目标被 SmartCast 包装会落赋值
                    // switch 的 default，顶替应有的 const/可写诊断（与变量
                    // 引用路径 !forAssignment 口径对齐）
                    if (forAssignment) return access;
                    return ApplyNarrowing(node, access,
                        NarrowKey.TryFromFieldAccess(access.Receiver, field, ctx.Frame),
                        ctx.Flow);
                }
                env.Error(node.Span, $"P3: instance field '{field.Name}' requires a receiver" +
                    " ('this' is not available in a static context)");
                return null;
            }
            // 全局/静态字段：键 = 字段符号本身（const 全局字段收窄永不失效）；
            // 赋值 place 不包收窄（同上口径）
            var reference = new BoundFieldReferenceExpression(node, field, fieldType);
            if (forAssignment) return reference;
            return ApplyNarrowing(node, reference,
                ConstFieldRules.IsNarrowable(field, ctx.Frame) ? NarrowKey.ForSymbol(field) : null,
                ctx.Flow);
        }

        // 带访问器字段的读侧检查（S8e，SYNTAX §9.4.1；BindFieldReference
        // 与 BindInstanceFieldAccess 共用）：getter 存在且自身可见
        private static bool CheckReadable(FieldSymbol field, CharRange? span, BindContext ctx,
            BindEnvironment env)
        {
            if (field.Getter == null)
            {
                env.Error(span, $"'{field.Name}' has no getter");
                return false;
            }
            if (!ctx.Frame.CanAccess(field.Getter))
            {
                env.Error(span, $"'{field.Name}' getter is inaccessible due to its " +
                    "accessibility level");
                return false;
            }
            UnsafeGates.CheckMethod(field.Getter, span, ctx, env);
            return true;
        }

        // setter 体内 backing 访问改指保留字段 ..value（§13.3：VM 见此
        // 即直读直写 backing，不再绕 wrapper 链）；getter 仍用原逻辑字段
        public static FieldSymbol BackingStorageField(FieldSymbol field, bool forSetter)
        {
            if (!forSetter)
            {
                return field;
            }
            return new FieldSymbol(RigiCompiler.Bil.BilSpellings.BackingValueFieldName,
                owner: field.Owner, ns: field.Namespace, isStatic: field.IsStatic,
                fieldType: field.FieldType);
        }

        // backing 字段直达节点（S8e，SYNTAX §9.4.1）：访问器体内 value
        // 别名与驱动合成（隐含赋值/自动访问器体）共用——实例补 this，
        // 静态/全局直引；不接名称解析、不走访问器/访问控制检查
        // （backing 直达是编译器机制内部路径）
        public static BoundExpression MakeBackingFieldReference(ASTNode node, FieldSymbol field,
            SemanticSymbol fieldType, BindFunctionFrame frame, bool forSetter,
            SymbolGraph symbols)
        {
            var target = BackingStorageField(field, forSetter);
            if (target.Owner != null && !target.IsStatic)
            {
                return new BoundFieldAccessExpression(node,
                    new BoundThisExpression(node,
                        SymbolLookup.AsSelfConstructed(frame.Method.Owner, symbols)!),
                    target, fieldType);
            }
            return new BoundFieldReferenceExpression(node, target, fieldType);
        }

        // this 路径（S7c-2，SYNTAX §9）：值位置 this（Type = 宿主类型，
        // method.Owner 统一承载——普通成员为声明类型，ext 方法为目标类型）
        // 或实例链起点；S8c 起首段后缀折叠（this[i] / this[i] = x 索引
        // 访问；this(...) 值调用未支持，由 FoldSuffixes 归口）。静态上下文
        // （static 方法/全局函数）不可用
        private static BoundExpression? BindThisPath(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env, bool forAssignment)
        {
            // lambda 语境（SYNTAX §5.2）：函数级宿主是隐藏类的 $$call（实例方法，
            // Frame.HasThis 恒真），但源码层 this 指向外层声明位置的实例——
            // 有效 this 类型取 LambdaThisType（外层静态上下文中的 lambda 为 null）
            var thisType = EffectiveThisType(ctx, env);
            if (thisType == null)
            {
                env.Error(node.Span, "P3: 'this' is not available in a static context");
                return null;
            }
            // wrapper 接收者借用宿主的隐藏存储，不能复制成脱离宿主的普通值。
            // 索引/调用后缀和成员链仍按接收者使用；括号内单独的 this 也不例外。
            if (thisType is TypeSymbol { Kind: TypeKind.Wrapper }
                && node.Head.Suffixes.Count == 0 && node.Segments.Count == 0)
            {
                env.Error(node.Span, "P3: Wrapper 'this' cannot be used as a value (only as a member access receiver)");
                return null;
            }
            // M88：模板态下 proxy 声明的 Owner 即 wrapper 类型，this 走普通
            // 实例上色（不再重写 BoundWrapperAccessExpression）
            BoundExpression receiver = new BoundThisExpression(node, thisType);
            if (ctx.IsLambda && ctx.This != null) ctx.CapturedSymbols.Add(ctx.This);
            var folded = FoldSuffixes(node, receiver, node.Head.Suffixes, 0,
                forAssignment && node.Segments.Count == 0, scope, ctx, env);
            if (folded == null) return null;
            if (node.Segments.Count == 0)
            {
                return folded;
            }
            return BindInstanceChain(node, folded, node.Segments, scope, ctx, env, forAssignment);
        }

        // self 路径（M88，SYNTAX §14.2）：proxy 体内 self = BoundSelfExpression
        //（Type = TTarget 泛型参数）；wrapper 零泛型参数时 self 不可用；
        // 非 proxy 语境是编译错误（ARCH §5.2）
        private static BoundExpression? BindSelfPath(PathExpressionASTNode node, Scope scope,
            BindContext ctx, BindEnvironment env, bool forAssignment)
        {
            if (!ctx.Proxy.IsActive)
            {
                env.Error(node.Span,
                    "P3: 'self' is only available in a wrapper proxy body (§14.2)");
                return null;
            }
            if (ctx.Proxy.SelfType == null)
            {
                env.Error(node.Span, "P3: 'self' is not available here: the wrapper declares " +
                    "no TTarget generic parameter (§14.2)");
                return null;
            }
            BoundExpression receiver = new BoundSelfExpression(node, ctx.Proxy.SelfType);
            var folded = FoldSuffixes(node, receiver, node.Head.Suffixes, 0,
                forAssignment && node.Segments.Count == 0, scope, ctx, env);
            if (folded == null) return null;
            if (node.Segments.Count == 0)
            {
                return folded;
            }
            return BindInstanceChain(node, folded, node.Segments, scope, ctx, env, forAssignment);
        }

        // 表达式底座路径（S8c）：(a+b).c / new X().c / 字面量.foo——底座
        // 表达式先绑定（内部路径经 PathVisitor 递归上色），再折叠首段
        // 后缀、交实例链。S11 特判：前导点 enum case 底座 + Call 后缀
        //（`.Failed(404)`，SYNTAX §12.1）——参数化 case 调用，expectedType
        // 提供 enum 上下文（与裸 `.Success` 同一通道）
        private static BoundExpression? BindExpressionBasePath(PathExpressionASTNode node,
            Scope scope, BindContext ctx, BindEnvironment env, bool forAssignment,
            TypeSymbol? expectedType = null)
        {
            if (node.Head.Expression!.Expression is EnumCaseExpressionASTNode enumCaseBase
                && node.Head.Suffixes.Count > 0
                && node.Head.Suffixes[0].Kind == PathSuffixKind.Call)
            {
                var caseValue = EnumCaseFacility.BindParameterizedCall(node, enumCaseBase,
                    node.Head.Suffixes[0].Arguments!, expectedType, scope, ctx, env);
                if (caseValue == null) return null;
                var foldedCase = FoldSuffixes(node, caseValue, node.Head.Suffixes, 1,
                    forAssignment && node.Segments.Count == 0, scope, ctx, env);
                if (foldedCase == null) return null;
                return BindInstanceChain(node, foldedCase, node.Segments, scope, ctx, env,
                    forAssignment);
            }
            var receiver = ExpressionDispatcher.Visit(node.Head.Expression!.Expression, scope,
                ctx, env);
            if (receiver == null) return null;
            var folded = FoldSuffixes(node, receiver, node.Head.Suffixes, 0,
                forAssignment && node.Segments.Count == 0, scope, ctx, env);
            if (folded == null) return null;
            return BindInstanceChain(node, folded, node.Segments, scope, ctx, env, forAssignment);
        }

    }
}
