using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// 访问器改写（MW4）：字段访问命中 computed getter/setter 时改写为
    /// MirCall；伪字段 #..value@ 直读写 backing；wrapper 字段受控拒绝。
    /// 读：Mir + Symbols + Layout；写：原地改写 Mir 指令列表。
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
                foreach (var block in fn.Blocks)
                {
                    RewriteBlock(context, fn, block);
                }
            }
        }

        private static void RewriteBlock(MwContext context, MirFunction fn, MirBlock block)
        {
            var insts = block.InstructionList;
            for (var i = 0; i < insts.Count; i++)
            {
                insts[i] = insts[i] switch
                {
                    MirGetField get => LowerGetField(context, fn, get),
                    MirSetField set => LowerSetField(context, fn, set),
                    MirGetStatic getStatic => LowerGetStatic(context, fn, getStatic),
                    MirSetStatic setStatic => LowerSetStatic(context, fn, setStatic),
                    var other => other,
                };
            }
        }

        // 原 FlowBuilder.EmitGetField 同口径：#..value@ → backing 直读；
        // .length 不触碰；其余 wrapper 拒绝后按访问器改写
        private static MirInst LowerGetField(MwContext context, MirFunction fn, MirGetField inst)
        {
            var fieldSymbol = inst.FieldSymbol;
            if (fieldSymbol.Contains("#..value@", System.StringComparison.Ordinal))
            {
                return new MirGetField(inst.Object, CurrentAccessorField(fn.Symbol), inst.Target);
            }
            if (TypeLayout.IsLengthField(fieldSymbol))
            {
                return inst;
            }
            RejectIfWrappedField(context.Symbols, fieldSymbol);
            if (!IsCurrentAccessorOf(fn.Symbol, fieldSymbol)
                && MirBuilder.FindAccessor(context.Symbols, fieldSymbol,
                    BilAccessorKind.Getter, fn.Symbol.Canonical) is { } getter)
            {
                return new MirCall(getter, new List<MirOperand> { inst.Object }, inst.Target);
            }
            return inst;
        }

        // 原 FlowBuilder.EmitSetField 同口径
        private static MirInst LowerSetField(MwContext context, MirFunction fn, MirSetField inst)
        {
            var fieldSymbol = inst.FieldSymbol;
            if (fieldSymbol.Contains("#..value@", System.StringComparison.Ordinal))
            {
                return new MirSetField(inst.Source, inst.Object, CurrentAccessorField(fn.Symbol));
            }
            RejectIfWrappedField(context.Symbols, fieldSymbol);
            if (!IsCurrentAccessorOf(fn.Symbol, fieldSymbol)
                && MirBuilder.FindAccessor(context.Symbols, fieldSymbol,
                    BilAccessorKind.Setter, fn.Symbol.Canonical) is { } setter)
            {
                return new MirCall(setter,
                    new List<MirOperand> { inst.Object, inst.Source }, null);
            }
            return inst;
        }

        // 原 FlowBuilder.EmitGetStatic 同口径（无 #..value@ / IsCurrentAccessorOf）
        private static MirInst LowerGetStatic(MwContext context, MirFunction fn, MirGetStatic inst)
        {
            RejectIfWrappedField(context.Symbols, inst.FieldSymbol);
            if (MirBuilder.FindAccessor(context.Symbols, inst.FieldSymbol,
                BilAccessorKind.Getter, fn.Symbol.Canonical) is { } getter)
            {
                return new MirCall(getter, new List<MirOperand>(), inst.Target);
            }
            return inst;
        }

        // 原 FlowBuilder.EmitSetStatic 同口径
        private static MirInst LowerSetStatic(MwContext context, MirFunction fn, MirSetStatic inst)
        {
            RejectIfWrappedField(context.Symbols, inst.FieldSymbol);
            if (MirBuilder.FindAccessor(context.Symbols, inst.FieldSymbol,
                BilAccessorKind.Setter, fn.Symbol.Canonical) is { } setter)
            {
                return new MirCall(setter, new List<MirOperand> { inst.Source }, null);
            }
            return inst;
        }

        // 当前 fn 即该字段的访问器（体内直访 backing）。FindAccessor 的
        // excludingFn 只跳过自身符号，派生 getter 体内读字段仍会命中基类
        // getter，故需此判定（原 FlowBuilder.IsCurrentAccessorOf 同口径）
        private static bool IsCurrentAccessorOf(MwMemberSymbol current, string fieldSymbol) =>
            CurrentAccessorFieldOrNull(current) == fieldSymbol;

        private static string? CurrentAccessorFieldOrNull(MwMemberSymbol current)
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

        private static string CurrentAccessorField(MwMemberSymbol current) =>
            CurrentAccessorFieldOrNull(current)
            ?? throw new CompilerInternalException(
                $"#..value@ 伪字段出现在非访问器 fn: {current.Canonical}");

        // wrapper 标记字段/宿主（链语义随 MW10；此处受控拒绝）
        private static void RejectIfWrappedField(MwSymbolTable symbols, string fieldSymbol)
        {
            var field = symbols.FindMember(fieldSymbol);
            if (field == null)
            {
                throw new MwNotSupportedException(
                    $"MW4 暂不支持的字段访问（外部/特殊字段）: {fieldSymbol}");
            }
            foreach (var modifier in field.Declaration.Modifiers)
            {
                if (modifier is BilWrappedModifier)
                {
                    throw new MwNotSupportedException($"wrapper 字段访问随 MW10: {fieldSymbol}");
                }
            }
            if (field.Owner != null)
            {
                foreach (var modifier in field.Owner.Declaration.Modifiers)
                {
                    if (modifier is BilWrappedModifier)
                    {
                        throw new MwNotSupportedException(
                            $"wrapper 宿主字段访问随 MW10: {fieldSymbol}");
                    }
                }
            }
        }
    }
}
