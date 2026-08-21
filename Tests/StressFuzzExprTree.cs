using System;
using System.Collections.Generic;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 随机表达式树（StressFuzz W9）：递归组合字面量 / 变量 / 括号化运算 /
    /// if / switch / 立即调用 lambda。受深度与 i32/bool 类型约束；
    /// 由调用方的 Random 驱动，用例 i 由 (Seed, i) 唯一确定。
    /// 不产循环与具名递归。
    /// </summary>
    internal static class FuzzExprTree
    {
        public static string I32(Random rng, IReadOnlyList<string> vars, int depth)
        {
            if (depth <= 0 || rng.Next(4) == 0)
                return LeafI32(rng, vars);

            switch (rng.Next(8))
            {
                case 0:
                    return "(" + I32(rng, vars, depth - 1) + " + " + I32(rng, vars, depth - 1) + ")";
                case 1:
                    return "(" + I32(rng, vars, depth - 1) + " - " + I32(rng, vars, depth - 1) + ")";
                case 2:
                {
                    int k = rng.Next(2, 6);
                    return "(" + I32(rng, vars, depth - 1) + " * " + k + ")";
                }
                case 3:
                {
                    int d = rng.Next(2, 9);
                    return "(" + I32(rng, vars, depth - 1) + " / " + d + ")";
                }
                case 4:
                    return "(" + I32(rng, vars, depth - 1) + ")";
                case 5:
                    return "(if (" + Bool(rng, vars, Math.Max(0, depth - 1)) + ") { "
                        + I32(rng, vars, depth - 1) + " } else { "
                        + I32(rng, vars, depth - 1) + " })";
                case 6:
                    return "(switch (" + I32(rng, vars, Math.Max(0, depth - 2)) + ") { (0) -> { "
                        + I32(rng, vars, depth - 1) + " } default -> { "
                        + I32(rng, vars, depth - 1) + " } })";
                default:
                {
                    var inner = new List<string>(vars) { "t" };
                    return "((func{(t: i32): i32 -> " + I32(rng, inner, depth - 1)
                        + "})(" + I32(rng, vars, depth - 1) + "))";
                }
            }
        }

        public static string Bool(Random rng, IReadOnlyList<string> vars, int depth)
        {
            if (depth <= 0 || rng.Next(3) == 0)
            {
                string[] ops = { ">", "<", "==", "!=", ">=", "<=" };
                string op = ops[rng.Next(ops.Length)];
                return "(" + LeafI32(rng, vars) + " " + op + " " + LeafI32(rng, vars) + ")";
            }
            if (rng.Next(2) == 0)
                return "(" + Bool(rng, vars, depth - 1) + " and " + Bool(rng, vars, depth - 1) + ")";
            return "(" + Bool(rng, vars, depth - 1) + " or " + Bool(rng, vars, depth - 1) + ")";
        }

        // 软 miss 用：类型约束偶发打破（i32 当 bool、bool 当 i32），语法仍合法。
        public static string I32Soft(Random rng, IReadOnlyList<string> vars, int depth)
        {
            if (rng.Next(5) == 0)
            {
                return rng.Next(3) switch
                {
                    0 => "(if (" + LeafI32(rng, vars) + ") { 1 } else { 0 })",
                    1 => "(" + LeafI32(rng, vars) + " + true)",
                    _ => "(" + Bool(rng, vars, 0) + ")",
                };
            }
            return I32(rng, vars, depth);
        }

        private static string LeafI32(Random rng, IReadOnlyList<string> vars)
        {
            if (vars.Count > 0 && rng.Next(3) != 0)
                return vars[rng.Next(vars.Count)];
            return rng.Next(0, 50).ToString();
        }
    }
}
