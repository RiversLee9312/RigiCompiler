using System.Globalization;
using System.Text;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 无产物占位：语句/块发射的 TResult——副作用填充 target（BilBlock），
    // 无返回值语义（EmitVisitor 协议需要一个具体 TResult 类型承载）
    internal readonly struct Unit
    {
        public static readonly Unit Value = new Unit();
    }

    // 发射共享设施（P4b）：intrinsic/类型检查映射/字段宿主投影/资源登记/
    // 字面量渲染/转义。自旧 EmitSession 同名方法迁移，行为不变——
    // 静态设施，env/ctx 参数显式传。M57 起映射产物为 Bil/ 强类型枚举
    // （拼写唯一定义在 BilSpellings）。M65 起临时变量物化（NewTemp）
    // 迁 TempVarTable（EmitContext.Temps）。
    internal static class EmittingFacility
    {
        // BIL §11 二元 intrinsic 映射（BilIntrinsicOp → BilBinaryOp）。
        // 注意：内建 bool 的短路 and/or 已在 P4a 展开为 if + 合成局部
        // （§11.3，S7b），LoweredBinaryExpression 不再承载 And/Or——
        // 表项为 and/or 被重载后的不短路场景（S8+）保留
        public static BilBinaryOp MapBinaryOp(BilIntrinsicOp op)
        {
            return op switch
            {
                BilIntrinsicOp.Add => BilBinaryOp.Add,
                BilIntrinsicOp.Sub => BilBinaryOp.Sub,
                BilIntrinsicOp.Mul => BilBinaryOp.Mul,
                BilIntrinsicOp.Div => BilBinaryOp.Div,
                BilIntrinsicOp.And => BilBinaryOp.And,
                BilIntrinsicOp.Or => BilBinaryOp.Or,
                BilIntrinsicOp.BinAnd => BilBinaryOp.BinAnd,
                BilIntrinsicOp.BinOr => BilBinaryOp.BinOr,
                BilIntrinsicOp.BinXor => BilBinaryOp.BinXor,
                BilIntrinsicOp.ShiftLeft => BilBinaryOp.ShiftLeft,
                BilIntrinsicOp.ShiftRight => BilBinaryOp.ShiftRight,
                BilIntrinsicOp.ShiftRightUnsigned => BilBinaryOp.ShiftRightUnsigned,
                BilIntrinsicOp.CmpEq => BilBinaryOp.CmpEq,
                BilIntrinsicOp.CmpNe => BilBinaryOp.CmpNe,
                BilIntrinsicOp.CmpLt => BilBinaryOp.CmpLt,
                BilIntrinsicOp.CmpLe => BilBinaryOp.CmpLe,
                BilIntrinsicOp.CmpGt => BilBinaryOp.CmpGt,
                BilIntrinsicOp.CmpGe => BilBinaryOp.CmpGe,
                _ => throw new CompilerInternalException("非二元 BilIntrinsicOp: " + op),
            };
        }

        // BIL §11 一元 intrinsic 映射（BilIntrinsicOp → BilUnaryOp）
        public static BilUnaryOp MapUnaryOp(BilIntrinsicOp op)
        {
            return op switch
            {
                BilIntrinsicOp.Opposite => BilUnaryOp.Opposite,
                BilIntrinsicOp.Not => BilUnaryOp.Not,
                BilIntrinsicOp.BinNot => BilUnaryOp.BinNot,
                _ => throw new CompilerInternalException("非一元 BilIntrinsicOp: " + op),
            };
        }

        // §12.3 类型检查种类映射（Bound → Bil）
        public static BilTypeCheckKind MapTypeCheckKind(BoundTypeCheckKind kind)
        {
            return kind switch
            {
                BoundTypeCheckKind.Is => BilTypeCheckKind.Is,
                BoundTypeCheckKind.Supers => BilTypeCheckKind.Supers,
                BoundTypeCheckKind.With => BilTypeCheckKind.With,
                _ => throw new CompilerInternalException("未知类型检查种类: " + kind),
            };
        }

        // 值引用的 BIL 变量名映射（S9d，§7.1）：可变参数（值包）引用映射到
        // 隐藏包变量——源码参数名是包变量，BIL 以保留名承载
        // （.vargs.<名>/.kwargs.<名>）；读（值引用操作数）写（set.var
        // 目标）两侧共用同一份映射，否则写入侧引用未声明变量
        public static string ValueVariableName(SemanticSymbol symbol)
        {
            // named 参数 IsVariadic 与 IsNamedVariadic 同时为 true——具名先判
            return symbol switch
            {
                ParameterSymbol { IsNamedVariadic: true } parameter => ".kwargs." + parameter.Name,
                ParameterSymbol { IsVariadic: true } parameter => ".vargs." + parameter.Name,
                _ => symbol.Name,
            };
        }

        // 字段宿主投影（§13.4 type(OWNER_TYPE)）：static 字段 = 宿主类型
        // canonical；命名空间全局字段 = 命名空间全名（§13.4 未规定全局字段的
        // 宿主形态，以命名空间全名投影，verifier（S12）阶段再核）；
        // 根命名空间全局字段 = 空串（字段符号无宿主前缀——`#name@type`，
        // 宿主操作数同形为空投影 `type()`，verifier 归一后两侧全等放行）
        public static string? FieldOwnerRef(FieldSymbol field, EmitEnvironment env)
        {
            if (field.Owner != null)
            {
                return CanonicalSymbolPrinter.PrintType(field.Owner);
            }
            return field.Namespace is { FullName: { } fullName } ? fullName : "";
        }

        // 字面量 → 资源：同（类型, 原文）去重，名按首次出现 R_0/R_1... 编号
        // （null 资源走 RegisterNullResource——与 P4a 合成 null 常量同路径）
        public static BilResource RegisterResource(LoweredLiteralExpression literal,
            EmitEnvironment env)
        {
            // null 字面量直接路由（错误情形由 RegisterNullResource 单点诊断）
            if (((LiteralExpressionASTNode)literal.Origin.Syntax).Literal is NullLiteralASTNode)
            {
                return RegisterNullResource(literal.Type, literal.Origin.Syntax.Span, env);
            }
            var (type, literalText) = RenderLiteral(literal, env);
            return RegisterScalarResource(type!.Value, literalText, env);
        }

        // null 资源登记（§19.1；S7f 起与合成 null 常量共用）：键 =
        // 元素类型投影；类型语义 = .nullable<元素类型>——
        // 可直接与 .nullable<T> 变量做 cmp.eq/cmp.ne（§11.5 严格相同）。
        // S9 放宽为 SemanticSymbol：元素可为泛型参数（T? 内的 T）——
        // 不可静态展开，按 §7.5 canonical（.generic<$.generic.T> 形态）
        // 投影作键与元素类型（CanonicalSymbolPrinter.PrintType 统一投影）；
        // 错误路径返回未登记的占位资源（诊断已落袋，输出按 §8 门槛不写盘）
        public static BilResource RegisterNullResource(SemanticSymbol nullableType, CharRange? span,
            EmitEnvironment env)
        {
            if (nullableType is TypeSymbol type
                && type.ConstructedFrom != null
                && ReferenceEquals(type.ConstructedFrom,
                    env.Unit.Symbols.Bootstrap.NullableDefinition))
            {
                var key = CanonicalSymbolPrinter.PrintType(type.TypeArguments![0]);
                if (!env.NullKeys.TryGetValue(key, out var resource))
                {
                    resource = new BilNullResource("R_" + env.Module.Resources.Count, key);
                    env.Module.Resources.Add(resource);
                    env.NullKeys.Add(key, resource);
                }
                return resource;
            }
            env.Error(span,
                "P4: null literal is not typed as Nullable<T> " +
                $"(got {CanonicalSymbolPrinter.PrintType(nullableType)})");
            return new BilNullResource("<error>", "<error>");
        }

        // 标量资源登记（字面量与 P4a 合成常量共用）：同（类型, 原文）去重
        public static BilResource RegisterScalarResource(BilScalarType type, string literalText,
            EmitEnvironment env)
        {
            if (!env.ScalarKeys.TryGetValue((type, literalText), out var resource))
            {
                resource = new BilScalarResource("R_" + env.Module.Resources.Count,
                    type, literalText);
                env.Module.Resources.Add(resource);
                env.ScalarKeys.Add((type, literalText), resource);
            }
            return resource;
        }

        // switch 常量表资源（§19.4）：selector 类型引用 = SELECTOR 类型
        // 投影（带前导点的类型引用，与 §19.1 标量关键字不同族），元素 =
        // 各 case 常量字面量原文（经 RenderLiteral 复用 §19.1 渲染；类型与
        // selector 严格相同，P3 已查）。单行形态；
        // 同（selector, 元素序列）去重——case 集完全相同的多个 switch 共享一张表
        public static BilResource RegisterSwitchTable(LoweredSwitch sw, EmitEnvironment env)
        {
            var selectorTypeRef = CanonicalSymbolPrinter.PrintType(sw.Selector.Type);
            var elements = new List<string>();
            foreach (var switchCase in sw.Cases)
            {
                // P3 已限定值匹配 case 的 Match 只绑定为字面量表达式
                var (_, literalText) = RenderLiteral((LoweredLiteralExpression)switchCase.Value, env);
                elements.Add(literalText);
            }
            var key = selectorTypeRef + "|" + string.Join(",", elements);
            if (!env.SwitchTableKeys.TryGetValue(key, out var resource))
            {
                resource = new BilSwitchTableResource("R_" + env.Module.Resources.Count,
                    selectorTypeRef, elements);
                env.Module.Resources.Add(resource);
                env.SwitchTableKeys.Add(key, resource);
            }
            return resource;
        }

        // catch 表资源（S7e，§19.5）：元素 = type(EXCEPTION_TYPE) ->
        // blk(CATCH_BLOCK)，保序（表序即匹配序，不能重排）。多行形态；
        // 空 catch 列表出空表。函数级同元素序列去重（元素含 block id，
        // tryN 函数内唯一——同键命中仅在同一函数内，与 switch-table
        // 同机制；去重表挂 EmitContext，跨函数不共享——条目持 fn 局部
        // block 对象引用，跨 fn 共享会把别函数 block 引进本 fn，§21.5
        // 判 block 引用越权）
        public static BilResource RegisterCatchTable(LoweredTryStatement tryStatement,
            IReadOnlyList<BilBlock> catchBlocks, EmitContext ctx, EmitEnvironment env)
        {
            var entries = new List<BilCatchEntry>();
            for (var i = 0; i < tryStatement.Catches.Count; i++)
            {
                entries.Add(new BilCatchEntry(
                    BilOp.Type(CanonicalSymbolPrinter.PrintType(
                        tryStatement.Catches[i].ExceptionType)),
                    catchBlocks[i]));
            }
            var keyParts = new List<string>();
            foreach (var entry in entries)
            {
                keyParts.Add(entry.ExceptionType.TypeRef + "->" + entry.Handler.Id);
            }
            var key = string.Join(",", keyParts);
            if (!ctx.CatchTableKeys.TryGetValue(key, out var resource))
            {
                resource = new BilCatchTableResource("R_" + env.Module.Resources.Count, entries);
                env.Module.Resources.Add(resource);
                ctx.CatchTableKeys.Add(key, resource);
            }
            return resource;
        }

        // 字面量 → (BIL 标量类型, 字面量原文)（§19.1；值取 Syntax 的解码后
        // 内容，重新转义为 BIL 字面量原文）。null 字面量返回 (null, 元素类型
        // canonical)——P3 已把 null 定型为上下文可空类型 Nullable\<T>；
        // Type 为 null 仅此情形（§19.4 表元素取原文时同此约定）。
        // 已诊断的错误路径返回 (String, "<error>") 占位（输出不写盘）
        public static (BilScalarType? Type, string LiteralText) RenderLiteral(
            LoweredLiteralExpression literal, EmitEnvironment env)
        {
            var syntax = (LiteralExpressionASTNode)literal.Origin.Syntax;
            switch (syntax.Literal)
            {
                case StringLiteralASTNode s:
                    return (BilScalarType.String, "\"" + Escape(s.Value) + "\"");
                case IntLiteralASTNode i:
                    // decimal 值统一走不变文化（整数值无小数点，文本与
                    // long 时代一致，如 "42"）
                    return (IntScalarType(i.IntType),
                        i.Value.ToString(CultureInfo.InvariantCulture));
                case BoolLiteralASTNode b:
                    return (BilScalarType.Bool, b.Value ? "true" : "false");
                case CharLiteralASTNode c:
                    return (BilScalarType.Char, "'" + EscapeChar(c.Value) + "'");
                case FloatLiteralASTNode f:
                    // round-trip 格式保精度；f32 先收窄回 float 再打印
                    // （AST 统一以 double 存值，直接打印会带出双精度尾巴）
                    return (f.IsFloat ? BilScalarType.F32 : BilScalarType.F64,
                        f.IsFloat
                            ? ((float)f.Value).ToString("R", CultureInfo.InvariantCulture)
                            : f.Value.ToString("R", CultureInfo.InvariantCulture));
                case NullLiteralASTNode:
                    // S9：literal.Type 的元素可为泛型参数（T? 内层）——
                    // 泛型参数不可静态展开，按 §7.5 canonical 投影
                    // （.generic<$.generic.T> 形态）
                    if (literal.Type is TypeSymbol nullType
                        && nullType.ConstructedFrom != null
                        && ReferenceEquals(nullType.ConstructedFrom,
                            env.Unit.Symbols.Bootstrap.NullableDefinition))
                    {
                        return (null, CanonicalSymbolPrinter.PrintType(
                            nullType.TypeArguments![0]));
                    }
                    env.Error(syntax.Span,
                        "P4: null literal is not typed as Nullable<T> " +
                        $"(got {CanonicalSymbolPrinter.PrintType(literal.Type)})");
                    return (BilScalarType.String, "<error>");
                default:
                    env.Error(syntax.Span,
                        $"P4: literal kind not supported by minimal emission: " +
                        syntax.Literal.GetType().Name);
                    return (BilScalarType.String, "<error>");
            }
        }

        // AST 整数类型 → BIL 标量类型（§19.1）
        public static BilScalarType IntScalarType(IntType intType)
        {
            return intType switch
            {
                IntType.I32 => BilScalarType.I32,
                IntType.I64 => BilScalarType.I64,
                IntType.I16 => BilScalarType.I16,
                IntType.I8 => BilScalarType.I8,
                IntType.U32 => BilScalarType.U32,
                IntType.U64 => BilScalarType.U64,
                IntType.U16 => BilScalarType.U16,
                IntType.U8 => BilScalarType.U8,
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

        // 泛型实参的 typeid 值物化（S9e，BIL §7.2 调用序前置）：静态实参
        // 经 getid.type type(...)（§12.5）产 .typeid 临时变量；嵌套泛型
        // 调用转发自身接收的 .generic.T 隐藏参数（零指令——.args 已声明，
        // §7.5 示例）；ErrorType 已诊断，占位操作数防内部异常
        public static BilVariableOperand MaterializeTypeId(SemanticSymbol typeArg,
            LoweredNode origin, BilBlock target, EmitContext ctx, EmitEnvironment env)
        {
            if (typeArg is GenericParameterSymbol generic)
            {
                return BilOp.Var(".generic." + generic.Name);
            }
            if (typeArg is ErrorTypeSymbol)
            {
                return BilOp.Var("<error>");
            }
            var typeIdTemp = ctx.Temps.NewTypeIdTemp();
            target.Instructions.Add(new GetIdTypeInstruction(
                BilOp.Type(CanonicalSymbolPrinter.PrintType(typeArg)), typeIdTemp)
            { Origin = origin });
            return typeIdTemp;
        }
    }
}
