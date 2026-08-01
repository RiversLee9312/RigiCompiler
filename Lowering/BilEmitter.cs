using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
    // S7a 发射补齐：局部声明/赋值（set.var §13.2、set.field.static §13.4）、
    // §11 运算指令（BilIntrinsicOp → opcode 单点映射表）、带返回值 invoke
    // （§15.1）、new（§14.1，init 选择归 Middleware，发射不写 init 符号）、
    // §18.1 标量资源全形态（bool/char/f32/f64/null type(...)）。
    // S7b 发射补齐：LoweredIfStatement → 多 block（§16.2 结构化条件：
    // 条件物化到临时变量 → if $c blk(then) blk(else)，无 else 用 none
    // 操作数；分支 block 落尾自然返回 §9.4，不补 ret——entrypoint 块维持
    // 既有「void 末尾补 ret / 不得落尾」逻辑）；P4a 合成常量（bool）与
    // 字面量同路进 Resources（同键去重）；block id 函数内唯一递增
    // （if0-then/if0-else，字符集限 A-Za-z0-9_- §5.1，无点号）。
    // S7c-1 发射补齐：LoweredLoop → loop/loop.rev（§16.3/§16.4：条件即
    // 合成局部引用，操作数序 cond/body/none/judge/breakid，块 id
    // loop0-body/loop0-judge）+ LoweredLoopControl → break/continue
    // （§16.5）+ .vars 的 .breakid 条目（§9.3：Type null 的合成局部
    // 投影 .breakid 别名）。
    // S7c-2 发射开闸：实例方法 fn 定义（.args 首条 .return 后插 .this =
    // OwnerType 投影，§9.2/§7.3——ext 成员同形态）、实例 invoke（receiver
    // 首实参；接口方法符号引用分派归 Middleware）、get.field/set.field
    // （§13.3）、this → $.this 零指令、init/operator 的 §8.4 声明形态
    // （init 修饰符 / operator(名) 修饰符 / ext 修饰符）。
    // 统一风格：表达式求值结果一律先物化到 .t 临时变量，再经 set.var 写入目标。
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
            // 当前函数（EmitFunction 设置）与 if/loop 分支 block 编号（函数内唯一递增）
            private BilFunction function = null!;
            private int ifCount;
            private int loopCount;

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
                EmitBuiltinExtMembers();
                // Resources 在函数发射中按（bodies 顺序 + 树内先序）登记
                foreach (var body in bodies)
                {
                    var function = EmitFunction(body);
                    if (function != null) module.Functions.Add(function);
                }
                return module;
            }

            // 内建类型的 ext 成员声明（S7c-2）：内建类型自身不声明
            // （EmitTypeTree 跳过 IsBuiltin——基元经 BIL 别名投影而非符号
            // 引用），但 P2 注册到其上的 ext 成员（如 .bootstrap 的
            // EnumerateInRange）必须声明，否则其 fn 定义引用了未声明符号——
            // 以 §8.4.1 裸条目形态输出（canonical 自带宿主前缀，段内位置
            // 任意）。枚举经 BootstrapSymbols 公共 TypeSymbol 属性反射——
            // 新内建类型自动覆盖，不维护手列清单
            private void EmitBuiltinExtMembers()
            {
                foreach (var property in typeof(BootstrapSymbols).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetValue(unit.Symbols.Bootstrap) is not TypeSymbol
                        { IsBuiltin: true } builtinType) continue;
                    foreach (var field in builtinType.Fields)
                    {
                        if (field.ExtTargetPath == null) continue;
                        module.LocalSymbols.Add(new BilSimpleMemberDeclaration(
                            field.IsStatic ? ".static-field" : ".field",
                            CanonicalSymbolPrinter.PrintField(field),
                            new[] { AccessibilityModifier(field.Accessibility) }));
                    }
                    foreach (var method in builtinType.Methods)
                    {
                        if (method.ExtTargetPath == null) continue;
                        var declaration = EmitMethodDeclaration(method);
                        if (declaration != null) module.LocalSymbols.Add(declaration);
                    }
                }
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

            // 方法声明（§8.4）：类型成员与全局函数共形态；null 返回 = 已诊断跳过。
            // S7c-2 开闸 init/operator 与实例方法：init 走普通 canonical
            // （$init...@.void）+ init 修饰符；operator 走 $$名 canonical +
            // operator(名) 修饰符；ext 成员带 ext 修饰符（P2 注册后
            // ExtTargetPath 保留为标记）；getter/setter 归 S8/S11 仍跳过
            private BilSimpleMemberDeclaration? EmitMethodDeclaration(MethodSymbol method)
            {
                if (method.Kind is MethodKind.Getter or MethodKind.Setter)
                {
                    Error(null, $"P4: method kind not supported by minimal emission: " +
                        $"{method.Kind} ({CanonicalSymbolPrinter.PrintMethod(method)})");
                    return null;
                }
                var modifiers = new List<string> { AccessibilityModifier(method.Accessibility) };
                if (method.ExtTargetPath != null) modifiers.Add("ext");
                if (method.Kind == MethodKind.Init) modifiers.Add("init");
                if (method.Kind == MethodKind.Operator) modifiers.Add($"operator({method.Name})");
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

            // null 返回 = 已诊断跳过。S7c-2 开闸实例方法（含 ext 成员）：
            // .args 按 §9.2 顺序——.return 在前，实例方法 .this 次之
            // （§7.3：ext 成员同以 .this 表示被扩展值的 receiver），
            // 普通参数随后（隐藏参数随 S9 落地）
            private BilFunction? EmitFunction(LoweredFunctionBody body)
            {
                var method = body.Method;
                var function = new BilFunction(CanonicalSymbolPrinter.PrintMethod(method));
                // .args（§9.2）：.return →（实例）.this → 普通参数
                function.Args.Add(new BilArgDeclaration(".return",
                    CanonicalSymbolPrinter.PrintTypeReference(method.ReturnType)));
                if (method.Owner != null && !method.IsStatic)
                {
                    function.Args.Add(new BilArgDeclaration(".this",
                        CanonicalSymbolPrinter.PrintType(method.Owner)));
                }
                foreach (var parameter in method.Parameters)
                {
                    function.Args.Add(new BilArgDeclaration(parameter.Name,
                        CanonicalSymbolPrinter.PrintTypeReference(parameter.Type)));
                }
                // 指令生成（临时变量在生成中登记）：entry block 先行入列，
                // if 分支 block 随 LoweredIfStatement 发射追加（§16.2）、
                // loop 的 body/judge block 随 LoweredLoop 发射追加（§16.3/§16.4）
                this.function = function;
                tempVars.Clear();
                tempCount = 0;
                ifCount = 0;
                loopCount = 0;
                var entry = new BilBlock("entry", "entrypoint");
                function.Blocks.Add(entry);
                EmitBlock(body.Body, entry);
                // §9.4：entrypoint block 不得正常落到末尾——void 函数体无显式
                // return 时补 ret（如 stdlib println）；分支 block 落尾自然
                // 返回引用它的结构化指令，不补 ret
                if (method.ReturnType == null
                    && (entry.Instructions.Count == 0
                        || entry.Instructions[entry.Instructions.Count - 1].Opcode != "ret"))
                {
                    entry.Instructions.Add(new BilInstruction("ret"));
                }
                // .vars（§9.3）：Locals 在前、临时变量在后；Type 为 null 的
                // 合成局部是 .breakid capability（§9.3 别名，无 TypeSymbol）
                foreach (var local in body.Locals)
                {
                    function.Vars.Add(new BilVarDeclaration(
                        local.Type == null
                            ? ".breakid"
                            : CanonicalSymbolPrinter.PrintType(local.Type), local.Name));
                }
                function.Vars.AddRange(tempVars);
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
                    case LoweredLocalDeclarationStatement decl:
                        // 无初始化器 → 无指令（.vars 已声明）；有初始化器 →
                        // 求值物化后经 set.var 写入（§13.2）
                        if (decl.Initializer != null)
                        {
                            var initValue = EmitValue(decl.Initializer, target);
                            target.Instructions.Add(new BilInstruction("set.var",
                                BilOp.Var(initValue), BilOp.Var(decl.Local.Name))
                            { Origin = decl });
                        }
                        break;
                    case LoweredAssignmentStatement assignment:
                        var assignedValue = EmitValue(assignment.Value, target);
                        switch (assignment.Target)
                        {
                            case LoweredValueReferenceExpression localTarget:
                                target.Instructions.Add(new BilInstruction("set.var",
                                    BilOp.Var(assignedValue), BilOp.Var(localTarget.Symbol.Name))
                                { Origin = assignment });
                                break;
                            case LoweredFieldReferenceExpression fieldTarget:
                                var ownerRef = FieldOwnerRef(fieldTarget.Field);
                                if (ownerRef == null) break;    // 已诊断
                                target.Instructions.Add(new BilInstruction("set.field.static",
                                    BilOp.Var(assignedValue), BilOp.Type(ownerRef),
                                    BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldTarget.Field)))
                                { Origin = assignment });
                                break;
                            case LoweredFieldAccessExpression accessTarget:
                                // 实例字段写入（§13.3：set.field SOURCE OBJECT
                                // field(F)——SOURCE 已物化，OBJECT 随后求值，
                                // 与操作数序一致）
                                var writeReceiver = EmitValue(accessTarget.Receiver, target);
                                target.Instructions.Add(new BilInstruction("set.field",
                                    BilOp.Var(assignedValue), BilOp.Var(writeReceiver),
                                    BilOp.Field(CanonicalSymbolPrinter.PrintField(accessTarget.Field)))
                                { Origin = assignment });
                                break;
                            default:
                                // P3 已强制赋值目标为 place（值引用/字段引用）
                                throw new CompilerInternalException(
                                    "非法赋值目标: " + assignment.Target.GetType().Name);
                        }
                        break;
                    case LoweredExpressionStatement expressionStatement:
                        // 求值结果物化到临时变量后丢弃（SYNTAX §4 无隐式返回值利用）
                        EmitValue(expressionStatement.Expression, target);
                        break;
                    case LoweredCallStatement call:
                        // 实参从左到右物化（§10.2），再发 invoke.noret（§15.1）；
                        // 实例调用（S7c-2）receiver 求值作首实参
                        var arguments = new List<BilOperand>();
                        if (call.Receiver != null)
                        {
                            arguments.Add(BilOp.Var(EmitValue(call.Receiver, target)));
                        }
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
                    case LoweredIfStatement ifStatement:
                        // 结构化条件（§16.2）：条件物化到临时变量 →
                        // if $c blk(then) blk(else)（无 else 用 none 操作数）；
                        // 分支 block 加入函数并递归发射，落尾自然返回（§9.4）
                        var conditionValue = EmitValue(ifStatement.Condition, target);
                        var id = "if" + ifCount;
                        ifCount++;
                        var thenBlock = new BilBlock(id + "-then");
                        var elseBlock = ifStatement.FalseBlock != null
                            ? new BilBlock(id + "-else") : null;
                        target.Instructions.Add(new BilInstruction("if",
                            BilOp.Var(conditionValue), BilOp.Blk(thenBlock.Id),
                            elseBlock != null ? BilOp.Blk(elseBlock.Id) : BilOp.None)
                        { Origin = ifStatement });
                        function.Blocks.Add(thenBlock);
                        EmitBlock(ifStatement.TrueBlock, thenBlock);
                        if (elseBlock != null)
                        {
                            function.Blocks.Add(elseBlock);
                            EmitBlock(ifStatement.FalseBlock!, elseBlock);
                        }
                        break;
                    case LoweredLoop loop:
                        // 结构化循环（§16.3/§16.4）：条件由 Judge 块写入合成
                        // 局部，loop $c blk(body) ENUM blk(judge) $breakid
                        // （IsRev → loop.rev；Enumerator 本步恒 none，for 的
                        // 枚举器块随 S7c-2）；body/judge block 加入函数并
                        // 递归发射，落尾自然返回（§9.4 同 if 分支块）
                        var loopId = "loop" + loopCount;
                        loopCount++;
                        var loopBodyBlock = new BilBlock(loopId + "-body");
                        var enumBlock = loop.Enumerator != null
                            ? new BilBlock(loopId + "-enum") : null;
                        var judgeBlock = new BilBlock(loopId + "-judge");
                        target.Instructions.Add(new BilInstruction(
                            loop.IsRev ? "loop.rev" : "loop",
                            BilOp.Var(loop.Condition.Name), BilOp.Blk(loopBodyBlock.Id),
                            enumBlock != null ? BilOp.Blk(enumBlock.Id) : BilOp.None,
                            BilOp.Blk(judgeBlock.Id), BilOp.Var(loop.BreakId.Name))
                        { Origin = loop });
                        function.Blocks.Add(loopBodyBlock);
                        EmitBlock(loop.Body, loopBodyBlock);
                        if (enumBlock != null)
                        {
                            function.Blocks.Add(enumBlock);
                            EmitBlock(loop.Enumerator!, enumBlock);
                        }
                        function.Blocks.Add(judgeBlock);
                        EmitBlock(loop.Judge, judgeBlock);
                        break;
                    case LoweredLoopControl loopControl:
                        // break/continue（§16.5）：直接引用目标循环的 breakid
                        target.Instructions.Add(new BilInstruction(
                            loopControl.IsBreak ? "break" : "continue",
                            BilOp.Var(loopControl.BreakId.Name))
                        { Origin = loopControl });
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
                    case LoweredConstantExpression constant:
                        // P4a 合成常量（S7b 仅 bool）：与字面量同路进 Resources
                        // （同键去重——短路展开的 false 与源码 false 字面量共享）
                        var constantResource = constant.Value is bool boolValue
                            ? RegisterScalarResource("bool", boolValue ? "true" : "false")
                            : throw new CompilerInternalException(
                                "P4a 合成常量类型未覆盖: " + constant.Value.GetType().Name);
                        var constantTemp = NewTemp(constant.Type);
                        target.Instructions.Add(new BilInstruction("load",
                            BilOp.Res(constantResource), BilOp.Var(constantTemp))
                        { Origin = constant });
                        return constantTemp;
                    case LoweredValueReferenceExpression valueReference:
                        return valueReference.Symbol.Name;
                    case LoweredFieldReferenceExpression fieldReference:
                        // 全局/static 字段读取（§13.4）
                        var ownerRef = FieldOwnerRef(fieldReference.Field);
                        if (ownerRef == null) return "<error>";    // 已诊断
                        var fieldValue = NewTemp(fieldReference.Type);
                        target.Instructions.Add(new BilInstruction("get.field.static",
                            BilOp.Var(fieldValue), BilOp.Type(ownerRef),
                            BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldReference.Field)))
                        { Origin = fieldReference });
                        return fieldValue;
                    case LoweredBinaryExpression binary:
                        var left = EmitValue(binary.Left, target);
                        var right = EmitValue(binary.Right, target);
                        var binaryResult = NewTemp(binary.Type);
                        target.Instructions.Add(new BilInstruction(IntrinsicOpcode(binary.Op),
                            BilOp.Var(left), BilOp.Var(right), BilOp.Var(binaryResult))
                        { Origin = binary });
                        return binaryResult;
                    case LoweredUnaryExpression unary:
                        var operand = EmitValue(unary.Operand, target);
                        var unaryResult = NewTemp(unary.Type);
                        target.Instructions.Add(new BilInstruction(IntrinsicOpcode(unary.Op),
                            BilOp.Var(operand), BilOp.Var(unaryResult))
                        { Origin = unary });
                        return unaryResult;
                    case LoweredCallExpression callExpression:
                        var callArguments = new List<BilOperand>();
                        foreach (var argument in callExpression.Arguments)
                        {
                            callArguments.Add(BilOp.Var(EmitValue(argument, target)));
                        }
                        var callResult = NewTemp(callExpression.Type);
                        target.Instructions.Add(new BilInstruction("invoke",
                            BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(callExpression.Method)),
                            BilOp.Var(callResult), BilOp.List(callArguments.ToArray()))
                        { Origin = callExpression });
                        return callResult;
                    case LoweredNewExpression newExpression:
                        var newArguments = new List<BilOperand>();
                        foreach (var argument in newExpression.Arguments)
                        {
                            newArguments.Add(BilOp.Var(EmitValue(argument, target)));
                        }
                        // §14.1：init 选择归 Middleware（按精确参数类型），发射不写 init 符号
                        var newResult = NewTemp(newExpression.Type);
                        target.Instructions.Add(new BilInstruction("new",
                            BilOp.Type(CanonicalSymbolPrinter.PrintType(newExpression.Type)),
                            BilOp.Var(newResult), BilOp.List(newArguments.ToArray()))
                        { Origin = newExpression });
                        return newResult;
                    case LoweredThisExpression:
                        // this → $.this 变量操作数（§7.3，零指令——.this 在
                        // .args 已声明，与参数同 $ 引用形式 §9.3）
                        return ".this";
                    case LoweredInstanceCallExpression instCall:
                        // 实例调用（§7.3/§15.1）：receiver 求值作首实参；
                        // 接口方法符号引用时分派归 Middleware（注释约定）
                        var instReceiver = EmitValue(instCall.Receiver, target);
                        var instArguments = new List<BilOperand> { BilOp.Var(instReceiver) };
                        foreach (var argument in instCall.Arguments)
                        {
                            instArguments.Add(BilOp.Var(EmitValue(argument, target)));
                        }
                        var instResult = NewTemp(instCall.Type);
                        target.Instructions.Add(new BilInstruction("invoke",
                            BilOp.Fn(CanonicalSymbolPrinter.PrintMethod(instCall.Method)),
                            BilOp.Var(instResult), BilOp.List(instArguments.ToArray()))
                        { Origin = instCall });
                        return instResult;
                    case LoweredFieldAccessExpression fieldAccess:
                        // 实例字段读取（§13.3：get.field OBJECT TARGET field(F)）
                        var accessReceiver = EmitValue(fieldAccess.Receiver, target);
                        var accessResult = NewTemp(fieldAccess.Type);
                        target.Instructions.Add(new BilInstruction("get.field",
                            BilOp.Var(accessReceiver), BilOp.Var(accessResult),
                            BilOp.Field(CanonicalSymbolPrinter.PrintField(fieldAccess.Field)))
                        { Origin = fieldAccess });
                        return accessResult;
                    default:
                        Error(expression.Origin.Syntax.Span,
                            $"P4: lowered expression kind not supported by minimal emission: " +
                            expression.GetType().Name);
                        return "<error>";
                }
            }

            // BIL §11 opcode 单点映射表（BilIntrinsicOp → 指令 opcode）。
            // 注意：内建 bool 的短路 and/or 已在 P4a 展开为 if + 合成局部
            // （§11.3，S7b），LoweredBinaryExpression 不再承载 And/Or——
            // 表项为 and/or 被重载后的不短路场景（S8+）保留
            private static string IntrinsicOpcode(BilIntrinsicOp op)
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
            private string? FieldOwnerRef(FieldSymbol field)
            {
                if (field.Owner != null)
                {
                    return CanonicalSymbolPrinter.PrintType(field.Owner);
                }
                if (field.Namespace is { FullName: { Length: > 0 } fullName })
                {
                    return fullName;
                }
                Error(null, $"P4: global field '{field.Name}' in the root namespace has no " +
                    "owner to project for get/set.field.static (BIL §13.4)");
                return null;
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
            // （null 资源的键 = ("null", 元素类型投影)，走 BilNullResource 形态）
            private string RegisterResource(LoweredLiteralExpression literal)
            {
                var (typeKeyword, literalText) = RenderLiteral(literal);
                if (typeKeyword != "null") return RegisterScalarResource(typeKeyword, literalText);
                if (!resourceKeys.TryGetValue((typeKeyword, literalText), out var name))
                {
                    name = "R_" + module.Resources.Count;
                    module.Resources.Add(new BilNullResource(name, literalText));
                    resourceKeys.Add((typeKeyword, literalText), name);
                }
                return name;
            }

            // 标量资源登记（字面量与 P4a 合成常量共用）：同（类型, 原文）去重
            private string RegisterScalarResource(string typeKeyword, string literalText)
            {
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
            // 重新转义为 BIL 字面量原文）。null 字面量返回 ("null", 元素类型
            // canonical)——P3 已把 null 定型为上下文可空类型 Nullable\<T>
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
                        if (literal.Type.ConstructedFrom == unit.Symbols.Bootstrap.NullableDefinition
                            && literal.Type.TypeArguments![0] is TypeSymbol element)
                        {
                            return ("null", CanonicalSymbolPrinter.PrintType(element));
                        }
                        Error(syntax.Span,
                            "P4: null literal is not typed as Nullable<T> " +
                            $"(got {CanonicalSymbolPrinter.PrintType(literal.Type)})");
                        return ("<error>", "<error>");
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

            // 解码字符 → BIL 字符字面量原文（转义表与字符串同集，外加单引号）
            private static string EscapeChar(char c)
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
}
