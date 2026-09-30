using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 块 5-1a：通用字段反射（§4.6.3「反射与实现边界」）——core.serialization
    // 的 typeNameOf / isSerializable / fieldsOf / casesOf 一组 pub 函数占位体
    // （serialization.rg，仿 decodeAnyValue/sbKind 先例）在此填充。
    // b5-2c 追加按名（String 形参）重载：与 Type\<T\> 值形态共用同一
    // 候选集/分发/匹配路径（ReflectionTargetName 统一目标名来源）。
    //
    // 数据源与筛选口径：
    //   字段清单 = FieldClosureChecker.ClosureFieldsOf（含继承字段、泛型
    //   实参代入）- static - @Temporary - 无支撑存储的计算属性，与
    //   Serializable 合成规则（SerializableFields forWrite:false）完全一致；
    //   宿主类型不限于 @Serializable——「参与序列化的字段」闭包对任何
    //   class/struct/enum struct 可计算，能力查询经 isSerializable 暴露。
    //   枚举 case 清单 = TypeSymbol.Cases + HoleParameters（参数洞名/类型）。
    //
    // 运行时分发：Type\<T\> 值即 typeid，其装箱 toString 文本携带规范类型名
    // （VM 为 BIL 别名形、native 为 canonical 形）。分支按双拼写字面量等值
    // 匹配（SbViewName 两种形态，同 sbBuild 先例），命中后返回的合成器静态
    // 构造元信息（typeName 一律 VM BIL 规范拼写，不依赖宿主 toString）。
    // 全部使用 VM/native 共有的运行时运算，不新增 BIL 指令/验证器面。
    //
    // 帧膨胀警示（jsonfix 实证）：本文件合成的按名分派体（fieldsOf(typeName)
    // 等）为**全部登记类型**生成静态分支——IR 与 native 帧尺寸随登记类型数
    // 线性增长（b5-2c→b7-2：+78 typeinfo 即 +16 alloca ≈ +204B/帧）。该派发
    // 位于序列化递归环每层一次（json.rg buildNestedParcel），递归栈预算
    // （shim.c rigi_stack_has_room）按「层数 × 本派发帧 + 环上其余帧」计费，
    // 新增登记类型会持续推高每层栈耗——扩表前先复核递归语料余量
    // （Tests/e2e/rigi/json_read_nested.rg 深度安全段 / array_boxed_scalar.rg）。
    // 结构性出路（独立立项）：分支改 sheet→元信息 表驱动查找，帧尺寸与登记
    // 数解耦。
    internal static partial class SerializationSynthesis
    {
        private static void FillReflectionFunctions(BindEnvironment env, NamespaceSymbol ns,
            TypeSymbol parcel, TypeSymbol iface, TypeSymbol token, ASTNode syntax)
        {
            var methods = ns.Methods
                .Where(m => m.Name is "typeNameOf" or "isSerializable" or "fieldsOf" or "casesOf")
                .ToList();
            if (methods.Count == 0) return;
            var fieldInfo = ns.Types.FirstOrDefault(t =>
                t.Name == "FieldInfo" && t.Kind == TypeKind.Struct);
            var caseInfo = ns.Types.FirstOrDefault(t =>
                t.Name == "EnumCaseInfo" && t.Kind == TypeKind.Class);
            if (fieldInfo == null || caseInfo == null) return;
            // Synthesize（1.8b）先填一版；FinalizeDynamicDecoder 按终态快照重填
            //（与 sbKind 同纪律：闭合构造候选集以 ConstructedTypeSnapshot 为准）。
            env.SyntheticCellBodies.RemoveAll(b => methods.Contains(b.Method));
            var candidates = ReflectionCandidates(env);
            var enumCandidates = ReflectionEnumCandidates(env);
            var ctx = PublicContext(env, syntax, iface, parcel, token);
            foreach (var method in methods)
            {
                var locals = new List<LocalSymbol>();
                var statements = new List<BoundStatement>();
                switch (method.Name)
                {
                    case "typeNameOf":
                        if (method.Parameters.Count == 0)
                        {
                            FillReflectionGenericForwarder(ctx, method, ns, "typeNameOf",
                                locals, statements);
                        }
                        else
                        {
                            FillTypeNameOf(ctx, method, candidates, locals, statements);
                        }
                        break;
                    case "isSerializable":
                        FillIsSerializable(ctx, method, candidates, locals, statements);
                        break;
                    case "fieldsOf":
                        if (method.Parameters.Count == 0)
                        {
                            FillReflectionGenericForwarder(ctx, method, ns, "fieldsOf",
                                locals, statements);
                        }
                        else
                        {
                            FillFieldsOf(ctx, method, fieldInfo, candidates, locals, statements);
                        }
                        break;
                    case "casesOf":
                        if (method.Parameters.Count == 0)
                        {
                            FillReflectionGenericForwarder(ctx, method, ns, "casesOf",
                                locals, statements);
                        }
                        else
                        {
                            FillCasesOf(ctx, method, fieldInfo, caseInfo, enumCandidates,
                                locals, statements);
                        }
                        break;
                }
                env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                    new BoundBlock(syntax, statements)));
            }
        }

        // ---- 候选集 ----

        // 反射宿主候选：编译单元内用户源声明的全部 class/struct/enum
        // struct（非 abstract；嵌套类型随成员递归）。抽象类型与接口不进
        // 候选（字段闭包对抽象基类经由具体派生类型的闭包体现）。
        // b5-1b 修正：跳过 IsCompilerLibrary 根（stdlib 内嵌源）——早前
        // 把 stdlib 声明的数百个类型一并收录，分发函数 IR 达百万行级，
        // native default<O2> 单核不收敛（36min+ 未完）；stdlib 类型经
        // 下方 @SerializationBase 宿主快照入口（容器/Parcel 等）覆盖。
        private static void CollectHostDefinitions(BindEnvironment env, List<TypeSymbol> defs)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                if (file.IsCompilerLibrary) continue;
                foreach (var decl in file.Declarations)
                {
                    CollectHostDefinition(decl, env, defs);
                }
            }
        }

        private static void CollectHostDefinition(ASTNode node, BindEnvironment env,
            List<TypeSymbol> defs)
        {
            switch (node)
            {
                case ClassDeclarationASTNode or StructDeclarationASTNode or EnumStructDeclarationASTNode:
                    if (env.Declarations.SymbolOf(node) is TypeSymbol { IsAbstract: false } type
                        && type.Kind is TypeKind.Class or TypeKind.Struct or TypeKind.EnumStruct)
                    {
                        defs.Add(type);
                    }
                    foreach (var member in MembersOf(node))
                    {
                        CollectHostDefinition(member, env, defs);
                    }
                    return;
                case InterfaceDeclarationASTNode iface:
                    foreach (var member in iface.Members)
                    {
                        CollectHostDefinition(member, env, defs);
                    }
                    return;
                case WrapperDeclarationASTNode wrapper:
                    foreach (var member in wrapper.Members)
                    {
                        CollectHostDefinition(member, env, defs);
                    }
                    return;
            }
        }

        // fieldsOf/typeNameOf 候选：宿主定义（非泛型定义本体；泛型定义取
        // ConstructedTypeSnapshot 中的闭合构造——快照外无运行时分发名）+
        // 标量内建（字段闭包恒空，返回空数组而非抛未登记）+ 快照内
        // @SerializationBase 宿主的闭合构造（标量/String/容器/Parcel——
        // 经基元/容器编解码器，字段闭包为空数组，但要可查询/可分发）。
        // 按 VM BIL 别名形去重。
        //
        // b5-1b 修正：早前「快照内其余闭合 class/struct 构造」全量收录
        // （639 个）使分发函数 IR 达百万行级（每分支内联字符串比较 +
        // 数组构造 ≈ 千条指令），native default<O2> 单核不收敛（36min+
        // 未完）。无单元内使用点的 stdlib 内部类按「未登记」口径抛
        // IllegalArgumentException（serialization.rg 头注与 SYNTAX §3.7
        // 「未登记（编译单元内无闭合使用点）」一致），不纳入候选。
        private static List<TypeSymbol> ReflectionCandidates(BindEnvironment env)
        {
            var defs = new List<TypeSymbol>();
            CollectHostDefinitions(env, defs);
            var snapshot = env.Unit.Symbols.ConstructedTypeSnapshot();
            var serializationBase = SerializationFacts.FindWrapper(
                env.Unit.Symbols, "SerializationBase");
            var result = new List<TypeSymbol>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            void Add(TypeSymbol? type)
            {
                if (type == null || !IsClosedWireType(type)) return;
                if (seen.Add(SbViewName(type, canonicalForm: false)))
                {
                    result.Add(type);
                }
            }
            foreach (var def in defs)
            {
                if (def.GenericParameters.Count == 0)
                {
                    Add(def);
                    continue;
                }
                foreach (var constructed in snapshot)
                {
                    if (ReferenceEquals(constructed.ConstructedFrom ?? constructed, def))
                    {
                        Add(constructed);
                    }
                }
            }
            Add(env.B.Bool);
            Add(env.B.Char);
            Add(env.B.Int8);
            Add(env.B.UInt8);
            Add(env.B.Int16);
            Add(env.B.UInt16);
            Add(env.B.Int32);
            Add(env.B.UInt32);
            Add(env.B.Int64);
            Add(env.B.UInt64);
            Add(env.B.Float);
            Add(env.B.Double);
            Add(env.B.String);
            foreach (var constructed in snapshot)
            {
                if (constructed.Kind is TypeKind.Class or TypeKind.Struct
                    && serializationBase != null
                    && SerializationFacts.HasWrapper(constructed, serializationBase))
                {
                    Add(constructed);
                }
            }
            return result;
        }

        // casesOf 候选：enum struct 定义（非泛型定义本体；泛型 enum 取闭合
        // 构造）。载荷类型含未闭合泛型参数的候选整型跳过（SbViewName 不支持
        // 开放类型），由未登记异常兜底。
        private static List<TypeSymbol> ReflectionEnumCandidates(BindEnvironment env)
        {
            var defs = new List<TypeSymbol>();
            CollectHostDefinitions(env, defs);
            var snapshot = env.Unit.Symbols.ConstructedTypeSnapshot();
            var result = new List<TypeSymbol>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var def in defs)
            {
                if (def.Kind != TypeKind.EnumStruct) continue;
                if (def.GenericParameters.Count == 0)
                {
                    if (seen.Add(SbViewName(def, canonicalForm: false))) result.Add(def);
                    continue;
                }
                foreach (var constructed in snapshot)
                {
                    if (ReferenceEquals(constructed.ConstructedFrom ?? constructed, def)
                        && IsClosedWireType(constructed)
                        && seen.Add(SbViewName(constructed, canonicalForm: false)))
                    {
                        result.Add(constructed);
                    }
                }
            }
            return result;
        }

        // ---- 公共构件 ----

        // Type\<T\> 形参的运行时类型名：typeid 装箱 toString（VM BIL 别名形 /
        // native canonical 形，宿主决定——分发条件两种拼写并收，命中后返回
        // 的合成器静态字面量恒为 VM 别名形，不依赖宿主 toString 差异）。
        private static BoundExpression ReflectionTypeValueName(SynthContext ctx,
            BoundExpression value)
        {
            var toString = ctx.Env.B.Any.Methods.First(m => m.Name == "toString");
            return new BoundInstanceCallExpression(ctx.Syntax, Cast(ctx, value, ctx.Env.B.Any),
                toString, new List<BoundExpression>(), ctx.Env.B.String);
        }

        // name == VM 别名形 || name == native canonical 形。
        private static BoundExpression ReflectionNameMatches(SynthContext ctx,
            BoundExpression nameRef, TypeSymbol candidate)
        {
            var alias = BindingDriver.MakeStringLiteral(ctx.Env,
                SbViewName(candidate, canonicalForm: false));
            var canonical = BindingDriver.MakeStringLiteral(ctx.Env,
                SbViewName(candidate, canonicalForm: true));
            return new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Or,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq, nameRef, alias,
                    ctx.Env.B.Bool),
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq, nameRef, canonical,
                    ctx.Env.B.Bool),
                ctx.Env.B.Bool);
        }

        // b5-2c 按名重载的目标名来源：Type\<T\> 值形态取 typeid 装箱
        // toString（双拼写由分发并收承担）；String 形态直接用实参（实参
        // 即规范类型名文本，同一分发/匹配代码路径，避免双份漂移）。
        private static BoundExpression ReflectionTargetName(SynthContext ctx,
            MethodSymbol method, List<LocalSymbol> locals,
            List<BoundStatement> statements)
        {
            var param = Param(ctx.Syntax, method, 0);
            if (ReferenceEquals(method.Parameters[0].Type, ctx.Env.B.String))
            {
                return Save(ctx, param, locals, statements, "rfName");
            }
            return Save(ctx, ReflectionTypeValueName(ctx, param), locals, statements,
                "rfName");
        }

        private static BoundBlock ReflectionThrowUnknown(SynthContext ctx, BoundExpression nameRef)
        {
            var exception = ctx.Env.B.Core.Types.First(t => t.Name == "IllegalArgumentException");
            var init = exception.Methods.First(m => m.Kind == MethodKind.Init
                && m.Parameters.Count == 1
                && ReferenceEquals(m.Parameters[0].Type, ctx.Env.B.String));
            var message = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                BindingDriver.MakeStringLiteral(ctx.Env, "反射查询未登记的类型："),
                nameRef, ctx.Env.B.String);
            return new BoundBlock(ctx.Syntax, new BoundStatement[]
            {
                new BoundThrowStatement(ctx.Syntax, new BoundNewExpression(ctx.Syntax,
                    exception, init, new BoundExpression[] { message })),
            });
        }

        private static BoundExpression ReflectionBoolLiteral(SynthContext ctx, bool value)
        {
            var expr = new LiteralExpressionASTNode();
            expr.AttachLiteral(new BoolLiteralASTNode(expr) { Value = value });
            return new BoundLiteralExpression(expr, ctx.Env.B.Bool);
        }

        // 字段闭包（与 SerializableFields forWrite:false 同口径：含继承、
        // 含 const 载荷；static / @Temporary / 无支撑存储计算属性排除；
        // 类型解析失败的毒化字段排除）。
        private static IEnumerable<(FieldSymbol Field, SemanticSymbol? FieldType)>
            ReflectionFields(SynthContext ctx, TypeSymbol host)
        {
            foreach (var (field, fieldType) in FieldClosureChecker.ClosureFieldsOf(host,
                ctx.Env.Unit.Symbols))
            {
                if (field.IsStatic) continue;
                if (ctx.Temporary != null && field.AppliedWrappers.Any(w =>
                    ReferenceEquals(w.WrapperDefinition, ctx.Temporary)))
                {
                    continue;
                }
                if ((field.Getter != null || field.Setter != null) && !field.HasBackingStorage)
                {
                    continue;
                }
                if (fieldType is null or ErrorTypeSymbol) continue;
                yield return (field, fieldType);
            }
        }

        // FieldInfo 三元组（name/typeName/nullable）：可空包装剥离——
        // 结构化载体是 nullable 标志，内层类型名进 typeName 文本。
        private static (string Name, string TypeName, bool Nullable)? ReflectionFieldOf(
            SynthContext ctx, FieldSymbol field, SemanticSymbol fieldType)
        {
            var nullable = NullableElement(ctx, fieldType) != null;
            var inner = NullableElement(ctx, fieldType) ?? fieldType;
            if (inner is not TypeSymbol innerSymbol) return null;
            return (field.Name, SbViewName(innerSymbol, canonicalForm: false), nullable);
        }

        // 静态构造 FieldInfo 数组：arrayOf(n) + 逐槽 new FieldInfo + setAtIndex。
        private static BoundExpression BuildFieldInfoArray(SynthContext ctx,
            TypeSymbol fieldInfo, List<(string Name, string TypeName, bool Nullable)> fields,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var arrayType = ctx.Env.Unit.Symbols.GetConstructedType(
                ctx.Env.B.ArrayDefinition, fieldInfo);
            var arr = NewLocal(ctx, locals, "rfArr", arrayType);
            statements.Add(new BoundLocalDeclarationStatement(syntax, arr,
                new BoundCallExpression(syntax, ctx.ArrayOf,
                    new List<BoundExpression>
                    {
                        IntLiteral(ctx, fields.Count, IntType.I32, ctx.Env.B.Int32),
                    }, arrayType, new SemanticSymbol[] { fieldInfo })));
            var init = fieldInfo.Methods.First(m =>
                m.Kind == MethodKind.Init && m.Parameters.Count == 3);
            for (var i = 0; i < fields.Count; i++)
            {
                var info = new BoundNewExpression(syntax, fieldInfo, init,
                    new List<BoundExpression>
                    {
                        BindingDriver.MakeStringLiteral(ctx.Env, fields[i].Name),
                        BindingDriver.MakeStringLiteral(ctx.Env, fields[i].TypeName),
                        ReflectionBoolLiteral(ctx, fields[i].Nullable),
                    });
                statements.Add(new BoundAssignmentStatement(syntax,
                    new BoundIndexExpression(syntax, Ref(ctx, arr),
                        IntLiteral(ctx, i, IntType.I32, ctx.Env.B.Int32),
                        ctx.ArraySetAt, fieldInfo),
                    info));
            }
            return Ref(ctx, arr);
        }

        // 无参泛型形态转发：getid.type .generic\<T\> 构造 Type\<T\> 值后调
        // 对应 Type\<T\> 形参重载（§4.7.1：调用点泛型实参与既有 Type\<T\>
        // 值两个入口同语义）。
        // b5-2c 消歧：追加按名（String 形参）重载后，单参重载不再唯一——
        // 排除 String 形参锁定 Type\<T\> 值形态（否则会向前者转发 typeid
        // 实参，类型错误）。
        private static void FillReflectionGenericForwarder(SynthContext ctx,
            MethodSymbol method, NamespaceSymbol ns, string overloadName,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var gp = method.GenericParameters[0];
            var valueOverload = ns.Methods.First(m =>
                m.Name == overloadName && m.Parameters.Count == 1
                && !ReferenceEquals(m.Parameters[0].Type, ctx.Env.B.String));
            var typeValue = new BoundTypeOfExpression(syntax, null, gp,
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.TypeDefinition, gp));
            statements.Add(new BoundReturnStatement(syntax,
                new BoundCallExpression(syntax, valueOverload,
                    new List<BoundExpression> { typeValue },
                    valueOverload.ReturnType!, new SemanticSymbol[] { gp })));
        }

        // ---- 各函数填充 ----

        private static void FillTypeNameOf(SynthContext ctx, MethodSymbol method,
            List<TypeSymbol> candidates, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var name = ReflectionTargetName(ctx, method, locals, statements);
            BoundBlock choices = ReflectionThrowUnknown(ctx, name);
            foreach (var candidate in candidates.AsEnumerable().Reverse())
            {
                var body = new List<BoundStatement>
                {
                    new BoundReturnStatement(syntax, BindingDriver.MakeStringLiteral(
                        ctx.Env, SbViewName(candidate, canonicalForm: false))),
                };
                choices = new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundIfStatement(syntax, ReflectionNameMatches(ctx, name,
                        candidate), new BoundBlock(syntax, body), choices),
                });
            }
            statements.AddRange(choices.Statements);
        }

        private static void FillIsSerializable(SynthContext ctx, MethodSymbol method,
            List<TypeSymbol> candidates, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var name = ReflectionTargetName(ctx, method, locals, statements);
            // 未知/未登记类型不是能力证据：默认 false，不抛异常。
            BoundBlock choices = new BoundBlock(syntax, new BoundStatement[]
            {
                new BoundReturnStatement(syntax, ReflectionBoolLiteral(ctx, false)),
            });
            var serializable = candidates
                .Where(candidate => SerializationFacts.HasWrapper(candidate, ctx.Serializable))
                .ToList();
            foreach (var candidate in serializable.AsEnumerable().Reverse())
            {
                choices = new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundIfStatement(syntax, ReflectionNameMatches(ctx, name,
                        candidate),
                        new BoundBlock(syntax, new BoundStatement[]
                        {
                            new BoundReturnStatement(syntax, ReflectionBoolLiteral(ctx, true)),
                        }), choices),
                });
            }
            statements.AddRange(choices.Statements);
        }

        private static void FillFieldsOf(SynthContext ctx, MethodSymbol method,
            TypeSymbol fieldInfo, List<TypeSymbol> candidates, List<LocalSymbol> locals,
            List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var name = ReflectionTargetName(ctx, method, locals, statements);
            BoundBlock choices = ReflectionThrowUnknown(ctx, name);
            foreach (var candidate in candidates.AsEnumerable().Reverse())
            {
                var body = new List<BoundStatement>();
                // 与 Serializable 合成规则一致：SB 宿主（标量/String/容器/
                // Parcel）经基元/容器编解码器，不扫描实现字段（含 ext 内建
                // 字段）——字段闭包对它们恒为空数组。
                var fields = ctx.SerializationBase != null
                    && SerializationFacts.HasWrapper(candidate, ctx.SerializationBase)
                        ? new List<(string Name, string TypeName, bool Nullable)>()
                        : ReflectionFields(ctx, candidate)
                            .Select(f => ReflectionFieldOf(ctx, f.Field, f.FieldType!))
                            .Where(f => f != null)
                            .Select(f => f!.Value)
                            .ToList();
                var arr = BuildFieldInfoArray(ctx, fieldInfo, fields, locals, body);
                body.Add(new BoundReturnStatement(syntax, arr));
                choices = new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundIfStatement(syntax, ReflectionNameMatches(ctx, name,
                        candidate), new BoundBlock(syntax, body), choices),
                });
            }
            statements.AddRange(choices.Statements);
        }

        private static void FillCasesOf(SynthContext ctx, MethodSymbol method,
            TypeSymbol fieldInfo, TypeSymbol caseInfo, List<TypeSymbol> enumCandidates,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var name = ReflectionTargetName(ctx, method, locals, statements);
            BoundBlock choices = ReflectionThrowUnknown(ctx, name);
            foreach (var candidate in enumCandidates.AsEnumerable().Reverse())
            {
                // 载荷签名：case 名 + 参数洞（名/声明类型）。任一洞类型不是
                // 闭合类型（开放泛型残留）时整型跳过，由未登记异常兜底。
                var infos = new List<(string Name, List<(string Name, string TypeName, bool Nullable)> Fields)>();
                var viable = true;
                foreach (var enumCase in candidate.Cases)
                {
                    var payload = new List<(string Name, string TypeName, bool Nullable)>();
                    foreach (var hole in enumCase.HoleParameters
                        ?? new List<EnumCaseHoleParameter>())
                    {
                        var described = ReflectionFieldOf(ctx,
                            new FieldSymbol(hole.Name, fieldType: hole.Type), hole.Type);
                        if (described == null) { viable = false; break; }
                        payload.Add(described.Value);
                    }
                    if (!viable) break;
                    infos.Add((enumCase.Name, payload));
                }
                if (!viable) continue;
                var body = new List<BoundStatement>();
                var caseArrayType = ctx.Env.Unit.Symbols.GetConstructedType(
                    ctx.Env.B.ArrayDefinition, caseInfo);
                var arr = NewLocal(ctx, locals, "rfCases", caseArrayType);
                body.Add(new BoundLocalDeclarationStatement(syntax, arr,
                    new BoundCallExpression(syntax, ctx.ArrayOf,
                        new List<BoundExpression>
                        {
                            IntLiteral(ctx, infos.Count, IntType.I32, ctx.Env.B.Int32),
                        }, caseArrayType, new SemanticSymbol[] { caseInfo })));
                var caseInit = caseInfo.Methods.First(m =>
                    m.Kind == MethodKind.Init && m.Parameters.Count == 2);
                for (var i = 0; i < infos.Count; i++)
                {
                    var payloadArr = BuildFieldInfoArray(ctx, fieldInfo, infos[i].Fields,
                        locals, body);
                    var info = new BoundNewExpression(syntax, caseInfo, caseInit,
                        new List<BoundExpression>
                        {
                            BindingDriver.MakeStringLiteral(ctx.Env, infos[i].Name),
                            payloadArr,
                        });
                    body.Add(new BoundAssignmentStatement(syntax,
                        new BoundIndexExpression(syntax, Ref(ctx, arr),
                            IntLiteral(ctx, i, IntType.I32, ctx.Env.B.Int32),
                            ctx.ArraySetAt, caseInfo),
                        info));
                }
                body.Add(new BoundReturnStatement(syntax, Ref(ctx, arr)));
                choices = new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundIfStatement(syntax, ReflectionNameMatches(ctx, name,
                        candidate), new BoundBlock(syntax, body), choices),
                });
            }
            statements.AddRange(choices.Statements);
        }
    }
}
