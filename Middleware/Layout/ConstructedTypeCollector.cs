using System;
using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 构造类型收集（MW5 c2-a）：从已过门禁的 BilModule 收集需物化的
    /// 构造类型 canonical（MwTypeKey.Normalize 归一），传递闭包 + 环保护。
    /// 来源：new/new.case/new.array 的 Type；fn .vars/.args；getid.type 与
    /// cast 目标；get.array/set.array 元素类型；构造类型代入后的
    /// extends/implements；构造实参内层递归。
    /// </summary>
    public static class ConstructedTypeCollector
    {
        public static IReadOnlyList<string> Collect(MwContext context)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            var queue = new Queue<string>();

            foreach (var function in context.Module.Functions)
            {
                var locals = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var arg in function.Args)
                {
                    locals[arg.Name] = arg.TypeRef;
                    Enqueue(arg.TypeRef, seen, order, queue);
                }
                foreach (var varDecl in function.Vars)
                {
                    locals[varDecl.Name] = varDecl.TypeRef;
                    Enqueue(varDecl.TypeRef, seen, order, queue);
                }
                foreach (var block in function.Blocks)
                {
                    WalkBlock(block, locals, seen, order, queue);
                }
            }

            // 非泛型类 implements I<i32>：接口构造不经 fn 局部也会出现
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                if (type.Declaration.ExtendsType is { } extends)
                {
                    Enqueue(extends, seen, order, queue);
                }
                foreach (var iface in type.Declaration.ImplementsTypes)
                {
                    Enqueue(iface, seen, order, queue);
                }
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var argument in TypeArgumentsOf(current))
                {
                    Enqueue(argument, seen, order, queue);
                }
                var template = context.Symbols.FindTypeByRef(current);
                if (template == null)
                {
                    continue;
                }
                var substitution = BuildSubstitution(current, template.Declaration);
                if (template.Declaration.ExtendsType is { } extends)
                {
                    Enqueue(Substitute(extends, substitution), seen, order, queue);
                }
                foreach (var iface in template.Declaration.ImplementsTypes)
                {
                    Enqueue(Substitute(iface, substitution), seen, order, queue);
                }
            }

            Logger.Verbose("Middleware", $"构造类型收集 {order.Count} 项");
            return order;
        }

        // 是否为需入队的构造类型（.generic 占位不是构造类型）
        public static bool IsConstructed(string typeRef)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            var angle = normalized.IndexOf('<');
            if (angle < 0 || !normalized.EndsWith(">", StringComparison.Ordinal))
            {
                return false;
            }
            var head = normalized.Substring(0, angle);
            return !head.Equals(".generic", StringComparison.Ordinal);
        }

        public static IReadOnlyList<string> TypeArgumentsOf(string typeRef)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            var angle = normalized.IndexOf('<');
            if (angle < 0 || !normalized.EndsWith(">", StringComparison.Ordinal))
            {
                return Array.Empty<string>();
            }
            return BilVerificationContext.SplitTopLevel(
                normalized.Substring(angle + 1, normalized.Length - angle - 2));
        }

        // 构造实参代入模板签名（VM BuildSubstitution 同语义）
        public static Dictionary<string, string>? BuildSubstitution(string typeRef,
            BilTypeDeclaration declaration)
        {
            if (declaration.GenericParameters.Count == 0)
            {
                return null;
            }
            var arguments = TypeArgumentsOf(typeRef);
            if (arguments.Count != declaration.GenericParameters.Count)
            {
                return null;
            }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < arguments.Count; i++)
            {
                map[declaration.GenericParameters[i]] = arguments[i];
            }
            return map;
        }

        public static string Substitute(string typeRef, Dictionary<string, string>? substitution)
        {
            if (substitution == null
                || !typeRef.Contains(".generic<", StringComparison.Ordinal))
            {
                return typeRef;
            }
            var result = typeRef;
            foreach (var pair in substitution)
            {
                result = result.Replace(".generic<$.generic." + pair.Key + ">",
                    pair.Value, StringComparison.Ordinal);
                result = result.Replace(".generic<" + pair.Key + ">",
                    pair.Value, StringComparison.Ordinal);
            }
            return result;
        }

        private static void Enqueue(string? typeRef, HashSet<string> seen, List<string> order,
            Queue<string> queue)
        {
            if (string.IsNullOrEmpty(typeRef) || !IsConstructed(typeRef))
            {
                return;
            }
            var key = MwTypeKey.Normalize(typeRef);
            if (!seen.Add(key))
            {
                return;
            }
            order.Add(key);
            queue.Enqueue(key);
        }

        private static void WalkBlock(BilBlock block, Dictionary<string, string> locals,
            HashSet<string> seen, List<string> order, Queue<string> queue)
        {
            foreach (var inst in block.Instructions)
            {
                switch (inst)
                {
                    case NewInstruction n:
                        Enqueue(n.Type.TypeRef, seen, order, queue);
                        break;
                    case NewCaseInstruction n:
                        Enqueue(n.Type.TypeRef, seen, order, queue);
                        break;
                    case NewWrappedInstruction n:
                        Enqueue(n.Type.TypeRef, seen, order, queue);
                        break;
                    case GetIdTypeInstruction g:
                        Enqueue(g.TargetType.TypeRef, seen, order, queue);
                        break;
                    case CastInstruction c:
                        Enqueue(c.TargetType.TypeRef, seen, order, queue);
                        break;
                    case GetArrayInstruction g:
                        Enqueue(ElementTypeOf(locals, g.Array.Name), seen, order, queue);
                        Enqueue(Lookup(locals, g.Target.Name), seen, order, queue);
                        break;
                    case SetArrayInstruction s:
                        Enqueue(ElementTypeOf(locals, s.Collection.Name), seen, order, queue);
                        Enqueue(Lookup(locals, s.Element.Name), seen, order, queue);
                        break;
                }
                foreach (var child in BilVerifier.ReferencedBlocks(inst))
                {
                    WalkBlock(child, locals, seen, order, queue);
                }
            }
        }

        private static string? Lookup(Dictionary<string, string> locals, string name) =>
            locals.TryGetValue(name, out var typeRef) ? typeRef : null;

        private static string? ElementTypeOf(Dictionary<string, string> locals, string arrayName)
        {
            if (!locals.TryGetValue(arrayName, out var arrayType))
            {
                return null;
            }
            var args = TypeArgumentsOf(arrayType);
            return args.Count > 0 ? args[0] : null;
        }
    }
}
