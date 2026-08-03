using System.Globalization;
using System.Text;
using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 无产物占位：语句/块发射的 TResult——副作用填充 target（BilBlock），
    // 无返回值语义（EmitVisitor 协议需要一个具体 TResult 类型承载）
    internal readonly struct Unit
    {
        public static readonly Unit Value = new Unit();
    }

    // 发射共享设施（P4b）：opcode 映射/字段宿主投影/临时变量/资源登记/
    // 字面量渲染/转义。自旧 EmitSession 同名方法迁移，行为不变——
    // 静态设施，env/ctx 参数显式传。
    internal static class EmittingFacility
    {
        // BIL §11 opcode 单点映射表（BilIntrinsicOp → 指令 opcode）。
        // 注意：内建 bool 的短路 and/or 已在 P4a 展开为 if + 合成局部
        // （§11.3，S7b），LoweredBinaryExpression 不再承载 And/Or——
        // 表项为 and/or 被重载后的不短路场景（S8+）保留
        public static string IntrinsicOpcode(BilIntrinsicOp op)
        {
            return op switch
            {
                BilIntrinsicOp.Add => "add",
                BilIntrinsicOp.Sub => "sub",
                BilIntrinsicOp.Mul => "mul",
                BilIntrinsicOp.Div => "div",
                BilIntrinsicOp.Opposite => "opposite",
                BilIntrinsicOp.And => "and",
                BilIntrinsicOp.Or => "or",
                BilIntrinsicOp.Not => "not",
                BilIntrinsicOp.BinAnd => "bin.and",
                BilIntrinsicOp.BinOr => "bin.or",
                BilIntrinsicOp.BinXor => "bin.xor",
                BilIntrinsicOp.BinNot => "bin.not",
                BilIntrinsicOp.ShiftLeft => "shift.left",
                BilIntrinsicOp.ShiftRight => "shift.right",
                BilIntrinsicOp.ShiftRightUnsigned => "shift.right.unsigned",
                BilIntrinsicOp.CmpEq => "cmp.eq",
                BilIntrinsicOp.CmpNe => "cmp.ne",
                BilIntrinsicOp.CmpLt => "cmp.lt",
                BilIntrinsicOp.CmpLe => "cmp.le",
                BilIntrinsicOp.CmpGt => "cmp.gt",
                BilIntrinsicOp.CmpGe => "cmp.ge",
                _ => throw new CompilerInternalException("未知 BilIntrinsicOp: " + op),
            };
        }

        // 字段宿主投影（§13.4 type(OWNER_TYPE)）：static 字段 = 宿主类型
        // canonical；命名空间全局字段 = 命名空间全名（§13.4 未规定全局字段的
        // 宿主形态，以命名空间全名投影，verifier（S12）阶段再核）；
        // 根全局命名空间的字段无宿主可投影——规范空白，报 P4 Error 而不发明语法
        public static string? FieldOwnerRef(FieldSymbol field, EmitEnvironment env)
        {
            if (field.Owner != null)
            {
                return CanonicalSymbolPrinter.PrintType(field.Owner);
            }
            if (field.Namespace is { FullName: { Length: > 0 } fullName })
            {
                return fullName;
            }
            env.Error(null, $"P4: global field '{field.Name}' in the root namespace has no " +
                "owner to project for get/set.field.static (BIL §13.4)");
            return null;
        }

        public static string NewTemp(TypeSymbol type, EmitContext ctx)
        {
            var name = ".t" + ctx.TempCount;
            ctx.TempCount++;
            ctx.TempVars.Add(new BilVarDeclaration(CanonicalSymbolPrinter.PrintType(type), name));
            return name;
        }

        // 字面量 → 资源：同（类型, 原文）去重，名按首次出现 R_0/R_1... 编号
        // （null 资源走 RegisterNullResource——与 P4a 合成 null 常量同路径）
        public static string RegisterResource(LoweredLiteralExpression literal, EmitEnvironment env)
        {
            var (typeKeyword, literalText) = RenderLiteral(literal, env);
            if (typeKeyword != "null") return RegisterScalarResource(typeKeyword, literalText, env);
            return RegisterNullResource(literal.Type, literal.Origin.Syntax.Span, env);
        }

        // null 资源登记（§18.1；S7f 起与合成 null 常量共用）：键 =
        // ("null", 元素类型投影)；类型语义 = .nullable<元素类型>——
        // 可直接与 .nullable<T> 变量做 cmp.eq/cmp.ne（§11.5 严格相同）
        public static string RegisterNullResource(TypeSymbol nullableType, CharRange? span,
            EmitEnvironment env)
        {
            if (nullableType.ConstructedFrom == env.Unit.Symbols.Bootstrap.NullableDefinition
                && nullableType.TypeArguments![0] is TypeSymbol element)
            {
                var key = ("null", CanonicalSymbolPrinter.PrintType(element));
                if (!env.ResourceKeys.TryGetValue(key, out var name))
                {
                    name = "R_" + env.Module.Resources.Count;
                    env.Module.Resources.Add(new BilNullResource(name, key.Item2));
                    env.ResourceKeys.Add(key, name);
                }
                return name;
            }
            env.Error(span,
                "P4: null literal is not typed as Nullable<T> " +
                $"(got {CanonicalSymbolPrinter.PrintType(nullableType)})");
            return "<error>";
        }

        // 标量资源登记（字面量与 P4a 合成常量共用）：同（类型, 原文）去重
        public static string RegisterScalarResource(string typeKeyword, string literalText,
            EmitEnvironment env)
        {
            if (!env.ResourceKeys.TryGetValue((typeKeyword, literalText), out var name))
            {
                name = "R_" + env.Module.Resources.Count;
                env.Module.Resources.Add(new BilScalarResource(name, typeKeyword, literalText));
                env.ResourceKeys.Add((typeKeyword, literalText), name);
            }
            return name;
        }

        // switch 常量表资源（§18.4）：header = switch-table<SELECTOR 类型
        // 投影>（带前导点的类型引用，与 §18.1 标量关键字不同族），元素 =
        // 各 case 常量字面量原文（经 RenderLiteral 复用 §18.1 渲染；类型与
        // selector 严格相同，P3 已查）。单行形态（Multiline: false）；
        // 同（header, 元素序列）去重——case 集完全相同的多个 switch 共享一张表
        public static string RegisterSwitchTable(LoweredSwitch sw, EmitEnvironment env)
        {
            var header = "switch-table<" +
                CanonicalSymbolPrinter.PrintType(sw.Selector.Type) + ">";
            var elements = new List<string>();
            foreach (var switchCase in sw.Cases)
            {
                // P3 已限定值匹配 case 的 Match 只绑定为字面量表达式
                var (_, literalText) = RenderLiteral((LoweredLiteralExpression)switchCase.Value, env);
                elements.Add(literalText);
            }
            var key = ("switch-table", header + "|" + string.Join(",", elements));
            if (!env.ResourceKeys.TryGetValue(key, out var name))
            {
                name = "R_" + env.Module.Resources.Count;
                env.Module.Resources.Add(new BilCollectionResource(name, header, elements));
                env.ResourceKeys.Add(key, name);
            }
            return name;
        }

        // catch 表资源（S7e，§18.5）：header = catch-table（无类型参数），
        // 元素 = type(EXCEPTION_TYPE) -> blk(CATCH_BLOCK_ID)，保序（表序
        // 即匹配序，不能重排）。多行形态（Multiline: true，§18.5 规范
        // 排版）；空 catch 列表出空表。同元素序列去重（元素含 block id，
        // 实际去重仅在同序列重复登记时命中——与 switch-table 同机制）
        public static string RegisterCatchTable(LoweredTryStatement tryStatement,
            IReadOnlyList<string> catchBlockIds, EmitEnvironment env)
        {
            var elements = new List<string>();
            for (var i = 0; i < tryStatement.Catches.Count; i++)
            {
                elements.Add("type(" +
                    CanonicalSymbolPrinter.PrintType(tryStatement.Catches[i].ExceptionType) +
                    ") -> blk(" + catchBlockIds[i] + ")");
            }
            var key = ("catch-table", string.Join(",", elements));
            if (!env.ResourceKeys.TryGetValue(key, out var name))
            {
                name = "R_" + env.Module.Resources.Count;
                env.Module.Resources.Add(new BilCollectionResource(name, "catch-table",
                    elements, multiline: true));
                env.ResourceKeys.Add(key, name);
            }
            return name;
        }

        // 字面量 → (BIL 资源类型关键字, 字面量原文)（§18.1：类型关键字无
        // 前导点——R_X = string "..." / i32 0；值取 Syntax 的解码后内容，
        // 重新转义为 BIL 字面量原文）。null 字面量返回 ("null", 元素类型
        // canonical)——P3 已把 null 定型为上下文可空类型 Nullable\<T>
        public static (string TypeKeyword, string LiteralText) RenderLiteral(
            LoweredLiteralExpression literal, EmitEnvironment env)
        {
            var syntax = (LiteralExpressionASTNode)literal.Origin.Syntax;
            switch (syntax.Literal)
            {
                case StringLiteralASTNode s:
                    return ("string", "\"" + Escape(s.Value) + "\"");
                case IntLiteralASTNode i:
                    return (IntResourceKeyword(i.IntType), i.Value.ToString());
                case BoolLiteralASTNode b:
                    return ("bool", b.Value ? "true" : "false");
                case CharLiteralASTNode c:
                    return ("char", "'" + EscapeChar(c.Value) + "'");
                case FloatLiteralASTNode f:
                    // round-trip 格式保精度；f32 先收窄回 float 再打印
                    // （AST 统一以 double 存值，直接打印会带出双精度尾巴）
                    return (f.IsFloat ? "f32" : "f64",
                        f.IsFloat
                            ? ((float)f.Value).ToString("R", CultureInfo.InvariantCulture)
                            : f.Value.ToString("R", CultureInfo.InvariantCulture));
                case NullLiteralASTNode:
                    if (literal.Type.ConstructedFrom == env.Unit.Symbols.Bootstrap.NullableDefinition
                        && literal.Type.TypeArguments![0] is TypeSymbol element)
                    {
                        return ("null", CanonicalSymbolPrinter.PrintType(element));
                    }
                    env.Error(syntax.Span,
                        "P4: null literal is not typed as Nullable<T> " +
                        $"(got {CanonicalSymbolPrinter.PrintType(literal.Type)})");
                    return ("<error>", "<error>");
                default:
                    env.Error(syntax.Span,
                        $"P4: literal kind not supported by minimal emission: " +
                        syntax.Literal.GetType().Name);
                    return ("<error>", "<error>");
            }
        }

        public static string IntResourceKeyword(IntType intType)
        {
            return intType switch
            {
                IntType.I32 => "i32",
                IntType.I64 => "i64",
                IntType.I16 => "i16",
                IntType.I8 => "i8",
                IntType.U32 => "u32",
                IntType.U64 => "u64",
                IntType.U16 => "u16",
                IntType.U8 => "u8",
                _ => throw new CompilerInternalException("未知 IntType: " + intType),
            };
        }

        // 解码值 → BIL 字符串字面量原文（转义表与 Lexer StringEscape 同集，
        // 逆向映射；$ 在 BIL 字符串中无特殊含义，不转义）
        public static string Escape(string value)
        {
            var sb = new StringBuilder();
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\v': sb.Append("\\v"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // 解码字符 → BIL 字符字面量原文（转义表与字符串同集，外加单引号）
        public static string EscapeChar(char c)
        {
            switch (c)
            {
                case '\\': return "\\\\";
                case '\'': return "\\'";
                case '\a': return "\\a";
                case '\b': return "\\b";
                case '\t': return "\\t";
                case '\n': return "\\n";
                case '\v': return "\\v";
                case '\f': return "\\f";
                case '\r': return "\\r";
                default: return c.ToString();
            }
        }
    }
}
