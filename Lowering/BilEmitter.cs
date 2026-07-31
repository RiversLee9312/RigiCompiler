using System.Collections.Generic;
using System.Text;
using LatteCompiler.Bil;

namespace LatteCompiler
{
    // P4b 发射（SEMANTIC_ARCHITECTURE §6.2）：LoweredTree → BilModule 的机械
    // 线性化，不再有任何语言级决策。S6 最小闭环落地范围：
    // - LocalSymbols：符号图全量声明平铺（类型 + 成员 + 全局裸条目 §8.4.1；
    //   内建 bootstrap 符号与 ErrorType 不声明——基元经 BIL 别名投影，
    //   不是符号引用；ExternalSymbols 本阶段恒为空段）
    // - Resources：字面量提取（BIL §4.2：指令不得内联字面量），
    //   键 = (类型投影, 字面量原文) 去重，名 = R_0/R_1... 按首次出现编号
    // - Functions：每 LoweredFunctionBody 一个 fn 定义（.args/.vars/
    //   单 entry block；表达式物化为临时变量，BIL §10.1 操作数只能是变量）
    // 符号引用一律经 CanonicalSymbolPrinter 投影；BilInstruction.Origin 塞
    // LoweredNode（语句级；load 的 Origin 是字面量 LoweredNode），BIL 模型
    // 对中端零依赖（Origin 保持 object?，§6.3）。
    // 有 P4 Error 时 Emit 仍返回模块（调用方按 §8 门槛不推进写盘）。
    public static class BilEmitter
    {
        public static BilModule Emit(CompilationUnit unit,
            IReadOnlyList<LoweredFunctionBody> bodies, string moduleName)
        {
            return new EmitSession(unit, bodies, moduleName).Run();
        }

        private sealed class EmitSession
        {
            private readonly CompilationUnit unit;
            private readonly IReadOnlyList<LoweredFunctionBody> bodies;
            private readonly string moduleName;
            private BilModule module = null!;

            // 资源去重表：键 = (BIL 资源类型关键字, 字面量原文)
            private readonly Dictionary<(string TypeKeyword, string LiteralText), string> resourceKeys =
                new Dictionary<(string, string), string>();

            // 当前函数的临时变量（编译器保留名 .t0/.t1...，§5.1：用户标识符
            // 不得以 . 开头，与用户变量零冲突）；指令生成中登记，.vars 收尾输出
            private readonly List<BilVarDeclaration> tempVars = new List<BilVarDeclaration>();
            private int tempCount;

            public EmitSession(CompilationUnit unit, IReadOnlyList<LoweredFunctionBody> bodies,
                string moduleName)
            {
                this.unit = unit;
                this.bodies = bodies;
                this.moduleName = moduleName;
            }

            private void Error(CharRange? span, string message)
            {
                unit.Diagnostics.Error(DiagnosticPhase.P4, span, message);
            }

            public BilModule Run()
            {
                module = new BilModule();
                // §4.1：源模块名（LiteralText 含引号；moduleName 由编译器内部给定）
                module.Metadata.Add(new BilMetadataEntry("module", "string", $"\"{moduleName}\""));
                EmitNamespace(unit.Symbols.GlobalNamespace);
                // Resources 在函数发射中按（bodies 顺序 + 树内先序）登记
                foreach (var body in bodies)
                {
                    var function = EmitFunction(body);
                    if (function != null) module.Functions.Add(function);
                }
                return module;
            }

            // ===== LocalSymbols（§8）=====

            // 命名空间平铺：本空间类型（含 NestedTypes 递归）→ 子命名空间递归 →
            // 本空间全局字段/函数裸条目（§8.4.1：不包裹在 .type 中）
            private void EmitNamespace(NamespaceSymbol ns)
            {
                foreach (var type in ns.Types)
                {
                    EmitTypeTree(type);
                }
                foreach (var child in ns.ChildNamespaces)
                {
                    EmitNamespace(child);
                }
                foreach (var field in ns.Fields)
                {
                    module.LocalSymbols.Add(new BilSimpleMemberDeclaration(
                        field.IsStatic ? ".static-field" : ".field",
                        CanonicalSymbolPrinter.PrintField(field),
                        new[] { AccessibilityModifier(field.Accessibility) }));
                }
                foreach (var method in ns.Methods)
                {
                    var declaration = EmitMethodDeclaration(method);
                    if (declaration != null) module.LocalSymbols.Add(declaration);
                }
            }

            private void EmitTypeTree(TypeSymbol type)
            {
                // 内建 bootstrap 符号（基元/层级根）不声明：经 BIL 别名投影引用；
                // ErrorType 是毒化单例，同样不进符号段
                if (type.IsBuiltin || type is ErrorTypeSymbol) return;
                module.LocalSymbols.Add(EmitTypeDeclaration(type));
                foreach (var nested in type.NestedTypes)
                {
                    EmitTypeTree(nested);
                }
            }

            private BilTypeDeclaration EmitTypeDeclaration(TypeSymbol type)
            {
                var kind = type.Kind switch
                {
                    TypeKind.Class => "class",
                    TypeKind.Struct => "struct",
                    TypeKind.EnumStruct => "enum-struct",
                    TypeKind.Interface => "interface",
                    TypeKind.Wrapper => "wrapper",
                    _ => throw new CompilerInternalException("未知 TypeKind: " + type.Kind),
                };
                var declaration = new BilTypeDeclaration(CanonicalSymbolPrinter.PrintType(type), kind);
                // 修饰符（§8.2）：访问（全显式）→ open/abstract/singleton → rich/shared
                // （wrapper 恒 rich 也显式输出——BIL 是显式 IR，不做源码的隐含）
                declaration.Modifiers.Add(AccessibilityModifier(type.Accessibility));
                if (type.IsOpen) declaration.Modifiers.Add("open");
                if (type.IsAbstract) declaration.Modifiers.Add("abstract");
                if (type.IsSingleton) declaration.Modifiers.Add("singleton");
                if (type.IsRich) declaration.Modifiers.Add("rich");
                if (type.IsShared) declaration.Modifiers.Add("shared");
                // extends：与种类默认基类相同则省略（P1 建壳即填默认基类——
                // class→Object / struct→ValueType / enum struct→Enum /
                // wrapper→Wrapper；P2 仅在源码显式继承时覆盖），不同才输出
                if (!ReferenceEquals(type.BaseType, DefaultBaseOf(type)))
                {
                    declaration.ExtendsType = CanonicalSymbolPrinter.PrintType(type.BaseType!);
                }
                foreach (var iface in type.Interfaces)
                {
                    declaration.ImplementsTypes.Add(CanonicalSymbolPrinter.PrintType(iface));
                }
                foreach (var field in type.Fields)
                {
                    declaration.Members.Add(new BilSimpleMemberDeclaration(
                        field.IsStatic ? ".static-field" : ".field",
                        CanonicalSymbolPrinter.PrintField(field),
                        new[] { AccessibilityModifier(field.Accessibility) }));
                }
                foreach (var method in type.Methods)
                {
                    var member = EmitMethodDeclaration(method);
                    if (member != null) declaration.Members.Add(member);
                }
                return declaration;
            }

            // 方法声明（§8.4）：类型成员与全局函数共形态；null 返回 = 已诊断跳过
            private BilSimpleMemberDeclaration? EmitMethodDeclaration(MethodSymbol method)
            {
                // init/operator/getter/setter 的声明形态随 S8/S11 落地
                if (method.Kind != MethodKind.Regular)
                {
                    Error(null, $"P4: method kind not supported by minimal emission: " +
                        $"{method.Kind} ({CanonicalSymbolPrinter.PrintMethod(method)})");
                    return null;
                }
                var modifiers = new List<string> { AccessibilityModifier(method.Accessibility) };
                // native 三件套（§8.4：symbol/lib 必须与 native 同时出现且各恰好一次）
                if (method.IsNative)
                {
                    modifiers.Add("native");
                    modifiers.Add($"symbol(\"{method.NativeSymbol}\")");
                    modifiers.Add($"lib(\"{method.NativeLibrary}\")");
                }
                // entrypoint：全局命名空间的裸 main（SYNTAX 程序入口）
                if (method.Owner == null && method.Namespace is { FullName: "" }
                    && method.Name == "main")
                {
                    modifiers.Add("entrypoint");
                }
                return new BilSimpleMemberDeclaration(
                    method.IsStatic ? ".static-method" : ".method",
                    CanonicalSymbolPrinter.PrintMethod(method),
                    modifiers);
            }

            // 种类的默认基类（P1 建壳填充规则；interface 无基类）
            private TypeSymbol? DefaultBaseOf(TypeSymbol type)
            {
                var bootstrap = unit.Symbols.Bootstrap;
                return type.Kind switch
                {
                    TypeKind.Class => bootstrap.Object,
                    TypeKind.Struct => bootstrap.ValueType,
                    TypeKind.EnumStruct => bootstrap.Enum,
                    TypeKind.Wrapper => bootstrap.Wrapper,
                    TypeKind.Interface => null,
                    _ => throw new CompilerInternalException("未知 TypeKind: " + type.Kind),
                };
            }

            private static string AccessibilityModifier(Accessibility accessibility)
            {
                return accessibility switch
                {
                    Accessibility.Public => "pub",
                    Accessibility.Protected => "protected",
                    Accessibility.Internal => "internal",
                    Accessibility.Private => "priv",
                    _ => throw new CompilerInternalException("未知 Accessibility: " + accessibility),
                };
            }

            // ===== Functions（§9）=====

            // null 返回 = 已诊断跳过（实例方法第一版不支持 .this receiver，§7.3）
            private BilFunction? EmitFunction(LoweredFunctionBody body)
            {
                var method = body.Method;
                if (method.Owner != null && !method.IsStatic)
                {
                    Error(body.Body.Origin.Syntax.Span,
                        $"P4: instance method '{method.Name}' requires .this receiver (S8)");
                    return null;
                }
                var function = new BilFunction(CanonicalSymbolPrinter.PrintMethod(method));
                // .args（§9.2）：.return 在前，其后普通参数（隐藏参数随 S9 落地）
                function.Args.Add(new BilArgDeclaration(".return",
                    CanonicalSymbolPrinter.PrintTypeReference(method.ReturnType)));
                foreach (var parameter in method.Parameters)
                {
                    function.Args.Add(new BilArgDeclaration(parameter.Name,
                        CanonicalSymbolPrinter.PrintTypeReference(parameter.Type)));
                }
                // 单 entry block：指令生成（临时变量在生成中登记）
                tempVars.Clear();
                tempCount = 0;
                var entry = new BilBlock("entry", "entrypoint");
                EmitBlock(body.Body, entry);
                // §9.4：entrypoint block 不得正常落到末尾——void 函数体无显式
                // return 时补 ret（如 stdlib println）
                if (method.ReturnType == null
                    && (entry.Instructions.Count == 0
                        || entry.Instructions[entry.Instructions.Count - 1].Opcode != "ret"))
                {
                    entry.Instructions.Add(new BilInstruction("ret"));
                }
                // .vars（§9.3）：Locals 在前、临时变量在后
                foreach (var local in body.Locals)
                {
                    function.Vars.Add(new BilVarDeclaration(
                        CanonicalSymbolPrinter.PrintType(local.Type), local.Name));
                }
                function.Vars.AddRange(tempVars);
                function.Blocks.Add(entry);
                return function;
            }

            // 块语句平铺（嵌套块无独立 BIL 结构，共享函数的参数与局部变量）
            private void EmitBlock(LoweredBlock block, BilBlock target)
            {
                foreach (var statement in block.Statements)
                {
                    EmitStatement(statement, target);
                }
            }

            private void EmitStatement(LoweredStatement statement, BilBlock target)
            {
                switch (statement)
                {
                    case LoweredBlock nested:
                        EmitBlock(nested, target);
                        break;
                    case LoweredCallStatement call:
                        // 实参从左到右物化（§10.2），再发 invoke.noret（§15.1）
                        var arguments = new List<BilOperand>();
                        foreach (var argument in call.Arguments)
                        {
                            arguments.Add(BilOp.Var(EmitValue(argument, target)));
                        }
                        target.Instructions.Add(new BilInstruction("invoke.noret",
                            BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(call.Method)),
                            BilOp.List(arguments.ToArray()))
                        { Origin = call });
                        break;
                    case LoweredReturnStatement ret:
                        if (ret.Value == null)
                        {
                            target.Instructions.Add(new BilInstruction("ret") { Origin = ret });
                        }
                        else
                        {
                            var value = EmitValue(ret.Value, target);
                            target.Instructions.Add(new BilInstruction("ret", BilOp.Var(value))
                            { Origin = ret });
                        }
                        break;
                    default:
                        Error(statement.Origin.Syntax.Span,
                            $"P4: lowered statement kind not supported by minimal emission: " +
                            statement.GetType().Name);
                        break;
                }
            }

            // 表达式物化为变量操作数（§10.1），返回变量名
            private string EmitValue(LoweredExpression expression, BilBlock target)
            {
                switch (expression)
                {
                    case LoweredLiteralExpression literal:
                        // 字面量提取进 Resources（§4.2），经 load res(...) 引用（§13.1）
                        var resource = RegisterResource(literal);
                        var temp = NewTemp(literal.Type);
                        target.Instructions.Add(new BilInstruction("load",
                            BilOp.Res(resource), BilOp.Var(temp))
                        { Origin = literal });
                        return temp;
                    case LoweredValueReferenceExpression valueReference:
                        return valueReference.Symbol.Name;
                    default:
                        Error(expression.Origin.Syntax.Span,
                            $"P4: lowered expression kind not supported by minimal emission: " +
                            expression.GetType().Name);
                        return "<error>";
                }
            }

            // ===== Resources（§4.2/§18）=====

            private string NewTemp(TypeSymbol type)
            {
                var name = ".t" + tempCount;
                tempCount++;
                tempVars.Add(new BilVarDeclaration(CanonicalSymbolPrinter.PrintType(type), name));
                return name;
            }

            // 字面量 → 资源：同（类型, 原文）去重，名按首次出现 R_0/R_1... 编号
            private string RegisterResource(LoweredLiteralExpression literal)
            {
                var (typeKeyword, literalText) = RenderLiteral(literal);
                if (!resourceKeys.TryGetValue((typeKeyword, literalText), out var name))
                {
                    name = "R_" + module.Resources.Count;
                    module.Resources.Add(new BilScalarResource(name, typeKeyword, literalText));
                    resourceKeys.Add((typeKeyword, literalText), name);
                }
                return name;
            }

            // 字面量 → (BIL 资源类型关键字, 字面量原文)（§18.1：类型关键字无
            // 前导点——R_X = string "..." / i32 0；值取 Syntax 的解码后内容，
            // 重新转义为 BIL 字面量原文）
            private (string TypeKeyword, string LiteralText) RenderLiteral(
                LoweredLiteralExpression literal)
            {
                var syntax = (LiteralExpressionASTNode)literal.Origin.Syntax;
                switch (syntax.Literal)
                {
                    case StringLiteralASTNode s:
                        return ("string", "\"" + Escape(s.Value) + "\"");
                    case IntLiteralASTNode i:
                        return (IntResourceKeyword(i.IntType), i.Value.ToString());
                    default:
                        Error(syntax.Span,
                            $"P4: literal kind not supported by minimal emission: " +
                            syntax.Literal.GetType().Name);
                        return ("<error>", "<error>");
                }
            }

            private static string IntResourceKeyword(IntType intType)
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
            private static string Escape(string value)
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
        }
    }
}
