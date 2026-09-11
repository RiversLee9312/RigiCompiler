using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// 访问器改写（MW4）：字段访问命中 computed getter/setter 时改写为
    /// MirCall；伪字段 #..value@ 直读写 backing（含 ..cell.. 隐藏子类
    /// getValue/setValue 的 VM 同口径回退）；字段自身 wrapped 放行不降级
    ///（交下游 FieldProxyBakingPass 成链）；宿主 wrapped 自刀3b 起同样
    /// 放行不降级（Entity 字段链与无 proxy 层的访问器兜底都归
    /// FieldProxyBakingPass）。静态字段访问自刀5 起放行：前端 BIL 里
    /// wrapped 静态字段已搬入 companion cell（符号段无独立条目），幸存
    /// 的静态符号（含 Entity wrapped 宿主上的普通静态字段）按 VM 静态
    /// 槽直读写语义处理——Entity wrapper 只作用于实例。
    /// 读：Mir + Symbols + Layout；写：原地改写 Mir 指令列表。本 pass
    /// 为小改写：唯一 switch 分派到内部类，无共享可变状态，不上 CRTP。
    /// </summary>
    public sealed class AccessorLoweringPass : IMwStage
    {
        public string Name => "AccessorLowering";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("AccessorLowering 要求 Mir 已挂载");
            foreach (var fn in mir.Functions)
            {
                RewriteFunction(context, fn);
            }
        }

        internal static void RewriteFunction(MwContext context, MirFunction fn)
        {
            foreach (var block in fn.Blocks)
            {
                RewriteBlock(context, fn, block);
            }
        }

        private static void RewriteBlock(MwContext context, MirFunction fn, MirBlock block)
        {
            var insts = block.InstructionList;
            for (var i = 0; i < insts.Count; i++)
            {
                insts[i] = insts[i] switch
                {
                    MirGetField get => GetFieldLowering.Rewrite(context, fn, get),
                    MirSetField set => SetFieldLowering.Rewrite(context, fn, set),
                    MirGetStatic getStatic => GetStaticLowering.Rewrite(context, fn, getStatic),
                    MirSetStatic setStatic => SetStaticLowering.Rewrite(context, fn, setStatic),
                    var other => other,
                };
            }
        }

        // 原 FlowBuilder.EmitGetField 同口径：#..value@ → backing 直读；
        // .length 不触碰；字段自身 wrapped 放行（交 FieldProxyBakingPass
        // 成链，不降级访问器）；宿主 wrapped 放行（同交 FieldProxyBakingPass，
        // 刀3b）；其余按访问器改写
        private static class GetFieldLowering
        {
            internal static MirInst Rewrite(MwContext context, MirFunction fn, MirGetField inst)
            {
                var fieldSymbol = inst.FieldSymbol;
                if (fieldSymbol.Contains("#..value@", System.StringComparison.Ordinal))
                {
                    return new MirGetField(inst.Object,
                        AccessorRules.CurrentField(fn.Symbol, fieldSymbol), inst.Target, inst.ExcTarget);
                }
                if (TypeLayout.IsLengthField(fieldSymbol)
                    || TypeLayout.IsStringLengthField(fieldSymbol)
                    || TypeLayout.IsStringCharacterCountField(fieldSymbol))
                {
                    // 内建 length 通道（Array/Span i32 直读 + String i64
                    // extractvalue）：ext const 无访问器，直通发射层特判
                    return inst;
                }
                // 类级隐藏 typeid 字段（#..generic.，遗6：泛型宿主成员
                // 烘焙的终态实参拼装就地读）不进成员表，直通发射层
                //（FieldEmitter.Resolve 按布局计划偏移解析）
                if (fieldSymbol.Contains(GenericAbi.HiddenFieldInfix,
                        System.StringComparison.Ordinal))
                {
                    return inst;
                }
                var field = AccessorRules.RequireField(context.Symbols, fieldSymbol);
                if (AccessorRules.IsFieldWrapped(field)
                    || AccessorRules.IsHostWrapped(field))
                {
                    return inst;
                }
                if (!AccessorRules.IsCurrentOf(fn.Symbol, fieldSymbol)
                    && ImplBinder.FindAccessor(context.Symbols, fieldSymbol,
                        BilAccessorKind.Getter, fn.Symbol.Canonical) is { } getter)
                {
                    // computed getter 的抛出仍归原读取点的词法 catch/finally。
                    return new MirCall(getter, new List<MirOperand> { inst.Object }, inst.Target, inst.ExcTarget);
                }
                return inst;
            }
        }

        // 原 FlowBuilder.EmitSetField 同口径
        private static class SetFieldLowering
        {
            internal static MirInst Rewrite(MwContext context, MirFunction fn, MirSetField inst)
            {
                var fieldSymbol = inst.FieldSymbol;
                if (fieldSymbol.Contains("#..value@", System.StringComparison.Ordinal))
                {
                    return new MirSetField(inst.Source, inst.Object,
                        AccessorRules.CurrentField(fn.Symbol, fieldSymbol));
                }
                var field = AccessorRules.RequireField(context.Symbols, fieldSymbol);
                if (AccessorRules.IsFieldWrapped(field)
                    || AccessorRules.IsHostWrapped(field))
                {
                    return inst;
                }
                if (!AccessorRules.IsCurrentOf(fn.Symbol, fieldSymbol)
                    && ImplBinder.FindAccessor(context.Symbols, fieldSymbol,
                        BilAccessorKind.Setter, fn.Symbol.Canonical) is { } setter)
                {
                    return new MirCall(setter,
                        new List<MirOperand> { inst.Object, inst.Source }, null);
                }
                return inst;
            }
        }

        // 原 FlowBuilder.EmitGetStatic 同口径（无 IsCurrentOf——excludingFn
        // 已自身排除）。#..value@ 伪字段（setter 体内 backing 直访）归一到
        // 真实字段：VM TryResolveBackingValue 同口径，实例路径同形。
        // 刀5 解除 wrapped 静态受控拒绝：wrapped 静态字段经 companion
        // cell 间接、自身符号不进符号段（RequireField 的存在性守卫仍在）；
        // Entity wrapped 宿主上的普通静态字段按 VM 静态槽直读写放行
        private static class GetStaticLowering
        {
            internal static MirInst Rewrite(MwContext context, MirFunction fn, MirGetStatic inst)
            {
                if (AccessorRules.IsBackingPseudoField(inst.FieldSymbol))
                {
                    return new MirGetStatic(
                        AccessorRules.CurrentField(fn.Symbol, inst.FieldSymbol), inst.Target);
                }
                AccessorRules.RequireField(context.Symbols, inst.FieldSymbol);
                if (ImplBinder.FindAccessor(context.Symbols, inst.FieldSymbol,
                    BilAccessorKind.Getter, fn.Symbol.Canonical) is { } getter)
                {
                    return new MirCall(getter, new List<MirOperand>(), inst.Target);
                }
                return inst;
            }
        }

        // 原 FlowBuilder.EmitSetStatic 同口径（#..value@ 处置同 GetStatic）
        private static class SetStaticLowering
        {
            internal static MirInst Rewrite(MwContext context, MirFunction fn, MirSetStatic inst)
            {
                if (AccessorRules.IsBackingPseudoField(inst.FieldSymbol))
                {
                    return new MirSetStatic(inst.Source,
                        AccessorRules.CurrentField(fn.Symbol, inst.FieldSymbol));
                }
                AccessorRules.RequireField(context.Symbols, inst.FieldSymbol);
                if (ImplBinder.FindAccessor(context.Symbols, inst.FieldSymbol,
                        BilAccessorKind.Setter, fn.Symbol.Canonical) is { } setter)
                {
                    return new MirCall(setter, new List<MirOperand> { inst.Source }, null);
                }
                return inst;
            }
        }

        // 访问器体判定与 wrapper 处置：跨四个 lowering 共享的纯查询，无可变状态
        internal static class AccessorRules
        {
            // 当前 fn 即该字段的访问器（体内直访 backing）。FindAccessor 的
            // excludingFn 只跳过自身符号，派生 getter 体内读字段仍会命中基类
            // getter，故需此判定（原 FlowBuilder.IsCurrentAccessorOf 同口径）
            internal static bool IsCurrentOf(MwMemberSymbol current, string fieldSymbol) =>
                CurrentFieldOrNull(current) == fieldSymbol;

            // backing 直访伪字段判定（VM TryResolveBackingValue 的
            // FieldSimpleName == ..value 同口径）：实例形态 Host#..value@T，
            // 静态形态 Host#.static...value@T（全局字段宿主段为空）
            internal static bool IsBackingPseudoField(string fieldSymbol)
            {
                var hash = fieldSymbol.IndexOf('#');
                var at = fieldSymbol.LastIndexOf('@');
                if (hash < 0 || at <= hash)
                {
                    return false;
                }
                var name = fieldSymbol.Substring(hash + 1, at - hash - 1);
                if (name.StartsWith(".static.", System.StringComparison.Ordinal))
                {
                    name = name.Substring(".static.".Length);
                }
                return name == BilSpellings.BackingValueFieldName;
            }

            internal static string CurrentField(MwMemberSymbol current, string pseudoFieldSymbol) =>
                CurrentFieldOrNull(current)
                ?? CellBackingField(current, pseudoFieldSymbol)
                ?? throw new CompilerInternalException(
                    $"#..value@ 伪字段出现在非访问器 fn: {current.Canonical}");

            private static string? CurrentFieldOrNull(MwMemberSymbol current)
            {
                foreach (var modifier in current.Declaration.Modifiers)
                {
                    if (modifier is BilAccessorModifier accessor)
                    {
                        return accessor.FieldSymbol;
                    }
                }
                return null;
            }

            // cell getValue/setValue 回退（VM VmContext.TryResolveBackingValue
            // 同口径）：..cell.. 隐藏子类的 getValue/setValue 体无 accessor
            // 修饰符，其 #..value@ 伪字段直映宿主 #value@ 同型字段
            private static string? CellBackingField(MwMemberSymbol current, string pseudoFieldSymbol)
            {
                var canonical = current.Canonical;
                var dollar = canonical.IndexOf('$');
                if (dollar < 0)
                {
                    return null;
                }
                var owner = canonical.Substring(0, dollar);
                if (!IsCellTypeRef(owner))
                {
                    return null;
                }
                var rest = canonical.Substring(dollar + 1);
                var open = rest.IndexOf('(');
                var name = open < 0 ? rest : rest.Substring(0, open);
                if (name != "getValue" && name != "setValue")
                {
                    return null;
                }
                if (!BilVerificationContext.TryParseFieldSymbol(pseudoFieldSymbol,
                        out var pseudoOwner, out _, out var fieldType)
                    || pseudoOwner != owner)
                {
                    return null;
                }
                return owner + "#value@" + fieldType;
            }

            // cell 隐藏子类判定（VM VmContext.IsCellTypeRef 同口径）：
            // 剥泛型实参与命名空间前缀后名以 ..cell.. 起头
            private static bool IsCellTypeRef(string typeRef)
            {
                var name = typeRef;
                var generic = name.IndexOf('<');
                if (generic >= 0)
                {
                    name = name.Substring(0, generic);
                }
                var sep = name.LastIndexOf("::", System.StringComparison.Ordinal);
                if (sep >= 0)
                {
                    name = name.Substring(sep + 2);
                }
                return name.StartsWith("..cell..", System.StringComparison.Ordinal);
            }

            // 字段符号解析（外部/特殊字段受控拒绝）；宿主 wrapped 的处置
            // 由调用方按方向定（实例路径放行交 FieldProxyBakingPass，静态
            // 路径刀5 起放行——见 GetStaticLowering/SetStaticLowering）；
            // 字段自身 wrapped 由调用方按方向处置
            internal static MwMemberSymbol RequireField(MwSymbolTable symbols, string fieldSymbol)
            {
                var field = symbols.FindMember(fieldSymbol);
                if (field == null)
                {
                    throw new MwNotSupportedException(
                        $"MW4 暂不支持的字段访问（外部/特殊字段）: {fieldSymbol}");
                }
                return field;
            }

            // 宿主类型 wrapped 判定（Entity 面，刀3b）。外部宿主除外：
            // WrapperApplicationIndex.Build 跳过 IsExternal，Entity 环链永不
            // 为外部宿主烘焙，按 wrapped 放行即成死路（#10：String 等固定
            // ABI 宿主带序列化 wrapped 标记却无本地环链）——访问器改写就地
            // 完成，与 RewriteEntityGet 的无环兜底同口径（VM 全局访问器表
            // 本就扁平登记，无环时直调 getter）
            internal static bool IsHostWrapped(MwMemberSymbol field)
            {
                if (field.Owner == null || field.Owner.IsExternal)
                {
                    return false;
                }
                foreach (var modifier in field.Owner.Declaration.Modifiers)
                {
                    if (modifier is BilWrappedModifier)
                    {
                        return true;
                    }
                }
                return false;
            }

            internal static bool IsFieldWrapped(MwMemberSymbol field)
            {
                foreach (var modifier in field.Declaration.Modifiers)
                {
                    if (modifier is BilWrappedModifier)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
