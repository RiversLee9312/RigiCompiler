namespace RigiCompiler
{
    // ===== 子任务 1：类型引用解析（字段 → 方法，init 映射依赖字段类型）=====
    //
    // 自旧 DeclarationResolver.ResolveSession.ResolveTypeReferences/
    // ResolveParameterType 迁移。执行序在 InheritanceResolver 之后（原序
    // 在先的倒置根因：init 映射省略类型沿基类链查字段，链须先就绪——
    // 含继承字段与内建类型的程序化字段，如 bootstrap Exception.message）。
    internal sealed class TypeReferenceResolver : ResolverVisitor<TypeReferenceResolver>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            // 填入点检查（g4 框架）推迟到两循环完成之后统一执行：字段循环
            // 先于方法签名解析，被引用构造类型的 async 成员签名此时尚未
            // 就绪——（构造类型, 标注位置）先登记，全部签名就绪后统一收口
            var pendingFillIns = new List<(TypeSymbol Constructed, CharRange? Span)>();
            // 先字段（init `_ -> field` 省略类型时沿用字段类型，字段须先就绪）
            foreach (var entry in env.Entries)
            {
                if (entry.Node is VariableDeclarationASTNode { TypeAnnotation: not null } v)
                {
                    ((FieldSymbol)entry.Symbol).FieldType = env.ResolveTypeReference(v.TypeAnnotation, entry);
                    CheckAccessible(((FieldSymbol)entry.Symbol).FieldType!,
                        v.TypeAnnotation.Span ?? entry.Node.Span, entry, env);
                    RegisterFillIn(((FieldSymbol)entry.Symbol).FieldType!,
                        v.TypeAnnotation.Span ?? entry.Node.Span, pendingFillIns);
                }
            }
            foreach (var entry in env.Entries)
            {
                if (entry.Node is not CallableDeclarationASTNode fn) continue;
                var method = (MethodSymbol)entry.Symbol;
                if (fn.ReturnType != null)
                {
                    method.ReturnType = env.ResolveTypeReference(fn.ReturnType, entry);
                    CheckAccessible(method.ReturnType!, fn.ReturnType.Span ?? entry.Node.Span,
                        entry, env);
                    RegisterFillIn(method.ReturnType!, fn.ReturnType.Span ?? entry.Node.Span,
                        pendingFillIns);
                }
                for (int i = 0; i < fn.Parameters.Parameters.Count; i++)
                {
                    method.Parameters[i].Type = ResolveParameterType(fn.Parameters.Parameters[i],
                        method.Parameters[i], entry, env);
                    CheckAccessible(method.Parameters[i].Type!,
                        fn.Parameters.Parameters[i].Span ?? entry.Node.Span, entry, env);
                    // init 映射省略类型的形参沿用字段类型——字段标注处已查，
                    // 不是独立填入点，不重复检查
                    if (fn.Parameters.Parameters[i].Type.TypeSymbol.symbol.elements.Count > 0)
                    {
                        RegisterFillIn(method.Parameters[i].Type!,
                            fn.Parameters.Parameters[i].Span ?? entry.Node.Span, pendingFillIns);
                    }
                }
                // 默认参数顺序（SYNTAX §4.2）：首个默认值之后的形参必须全部携带默认值
                var seenDefault = false;
                for (int i = 0; i < method.Parameters.Count; i++)
                {
                    if (method.Parameters[i].DefaultValue != null)
                    {
                        seenDefault = true;
                        continue;
                    }
                    if (seenDefault)
                    {
                        env.Error(fn.Parameters.Parameters[i].Span ?? fn.Span,
                            $"Parameter '{method.Parameters[i].Name}' must declare a default value " +
                            "(a preceding parameter has one)");
                    }
                }
            }
            // 统一收口：此时全部字段/方法签名均已解析
            foreach (var (constructed, span) in pendingFillIns)
            {
                GenericConstraints.CheckConstructedType(constructed, span,
                    env.Unit.Symbols, env.Error);
            }
        }

        // 登记填入点（构造类型标注；非构造类型/泛型参数/毒化不登记）
        private static void RegisterFillIn(SemanticSymbol resolved, CharRange? span,
            List<(TypeSymbol Constructed, CharRange? Span)> pendingFillIns)
        {
            if (resolved is TypeSymbol { ConstructedFrom: not null } constructed)
            {
                pendingFillIns.Add((constructed, span));
            }
        }

        private static SemanticSymbol ResolveParameterType(ParameterASTNode p,
            ParameterSymbol symbol, DeclEntry entry, ResolveEnvironment env)
        {
            if (p.Type.TypeSymbol.symbol.elements.Count > 0)
            {
                // 显式写类型的映射参数（§9.3 允许 name: type -> field）：映射
                // 字段同样做存在性检查与 MappedField 回写（参数类型以标注为准）
                if (p.MappedFieldName != null)
                {
                    CheckMappedField(p, symbol, entry, env, out _);
                }
                return env.ResolveTypeReference(p.Type, entry);
            }
            // 空类型节点仅出现于 init 映射省略类型（§9.3：沿用字段类型）
            if (p.MappedFieldName == null)
            {
                env.Error(p.Span ?? entry.Node.Span, $"Parameter '{p.Name}' is missing a type annotation");
                return env.Unit.Symbols.ErrorType;
            }
            if (!CheckMappedField(p, symbol, entry, env, out var fieldType))
            {
                return env.Unit.Symbols.ErrorType;
            }
            if (fieldType == null)
            {
                env.Error(p.Span ?? entry.Node.Span,
                    $"Init parameter mapping requires field '{p.MappedFieldName}' to have a type annotation");
                return env.Unit.Symbols.ErrorType;
            }
            return fieldType;
        }

        // init 映射字段检查（§9.3）：字段存在性沿基类链判定（FindField），
        // 命中回写 ParameterSymbol.MappedField——P3 映射赋值合成的唯一凭据。
        // 返回 false = 字段不存在（诊断已落袋）；fieldType 为字段类型
        // （构造代入后；字段无类型标注时为 null，由调用方按分支处理）
        private static bool CheckMappedField(ParameterASTNode p, ParameterSymbol symbol,
            DeclEntry entry, ResolveEnvironment env, out SemanticSymbol? fieldType)
        {
            var field = env.FindField(entry.DeclaringType, p.MappedFieldName!, out fieldType);
            if (field == null)
            {
                env.Error(p.Span ?? entry.Node.Span,
                    $"Init parameter mapping targets unknown field: '{p.MappedFieldName}'");
                return false;
            }
            symbol.MappedField = field;
            return true;
        }

        // 声明侧访问控制（SYNTAX §16，S8e；F1/V-A 起递归口径）：类型引用
        // 命中处即使用点——与 P3 函数体内检查共用 AccessChecker；毒化/泛型
        // 参数由设施内跳过。构造类型递归实参（写出 Box\<Hidden\> 报最深
        // 不可见者 Hidden，而非可见的顶层容器）
        private static void CheckAccessible(SemanticSymbol resolved, CharRange? span,
            DeclEntry entry, ResolveEnvironment env)
        {
            var inaccessible = AccessChecker.FindInaccessibleType(resolved, entry.Context.File,
                entry.Context.Namespace, entry.DeclaringType);
            if (inaccessible != null)
            {
                env.Error(span, AccessChecker.InaccessibleMessage(inaccessible));
            }
        }

        // 声明侧填入点统一检查（g4「最悲观假设」框架）：构造类型标注即
        // 填入点——显式 extends 界检查（P2 此前不查，一并补）+ 构造类型
        // 自身的隐式限制闭包（非 rich struct 字段闭包 / shared 持有者闭包 /
        // 静态字段闸门 / async 闸门 2/3，GenericConstraints 同通道设施）。
        // 诊断可恢复：落袋后保留已解析类型（与访问控制同口径，不毒化）。
        // 调用点见 VisitCore 收尾的统一收口（RegisterFillIn 登记）
    }
}
