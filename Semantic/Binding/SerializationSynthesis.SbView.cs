using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 块 4-2 B 面：格式层最小动态面（§4.6.1 / D3）——擦除 SB 视图的
    // 编译器合成。core.serialization 的 sbKind/sbLength/sbElementAt/
    // sbKeyAt/sbValueAt/sbTypeName/sbBuild* 一组 internal 函数占位体
    // （serialization.rg，仿 decodeAnyValue 先例）在此填充。
    // 容器分支按 ConstructedTypeSnapshot 的闭合容器实参生成，分支内
    // 静态 typed 访问后把元素以 Any? 上抛（元素级上抛是合法泛型上抛，
    // 不违反容器名义身份——List<i32> 仍不可强转为 List<Any?>）。
    // 全部使用 VM/native 共有的运行时运算（is 链 / 字段读 / 方法调用 /
    // 数组下标 / cast 核验），双宿主一致性由构造保证；元素/键值原样
    // 读写活值，不做 wire 编解码。sbBuild* 名称分发同时接受两种宿主
    // 拼写（VM BIL 别名形与 native canonical 形，见 SbViewName）。
    internal static partial class SerializationSynthesis
    {
        // sbKind 类别码（与 serialization.rg 注释中的契约固定表一致）。
        private const int SbKindBool = 1;
        private const int SbKindChar = 2;
        private const int SbKindI8 = 3;
        private const int SbKindU8 = 4;
        private const int SbKindI16 = 5;
        private const int SbKindU16 = 6;
        private const int SbKindI32 = 7;
        private const int SbKindU32 = 8;
        private const int SbKindI64 = 9;
        private const int SbKindU64 = 10;
        private const int SbKindF32 = 11;
        private const int SbKindF64 = 12;
        private const int SbKindString = 13;
        private const int SbKindParcel = 14;
        private const int SbKindArray = 15;
        private const int SbKindList = 16;
        private const int SbKindMap = 17;

        // Synthesize（阶段 1.8b）先填一版；FinalizeDynamicDecoder（绑定
        // 收尾、闭合泛型使用点已驻留）重填终版——与 decodeAnyValue 同纪律。
        private static void FillSbViewFunctions(BindEnvironment env, NamespaceSymbol ns,
            TypeSymbol parcel, TypeSymbol iface, TypeSymbol token, ASTNode syntax)
        {
            var names = new[]
            {
                "sbKind", "sbLength", "sbElementAt", "sbKeyAt", "sbValueAt",
                "sbTypeName", "sbBuildArray", "sbBuildList", "sbBuildMap",
            };
            var methods = names
                .Select(name => ns.Methods.FirstOrDefault(m => m.Name == name))
                .Where(m => m != null)
                .Cast<MethodSymbol>()
                .ToArray();
            if (methods.Length == 0) return;
            env.SyntheticCellBodies.RemoveAll(b => methods.Contains(b.Method));
            var containers = SbViewContainers(env);
            foreach (var method in methods)
            {
                var ctx = PublicContext(env, syntax, iface, parcel, token);
                var locals = new List<LocalSymbol>();
                var statements = new List<BoundStatement>();
                switch (method.Name)
                {
                    case "sbKind":
                        FillSbKind(env, ctx, method, parcel, containers, locals, statements);
                        break;
                    case "sbLength":
                        FillSbLength(env, ctx, method, parcel, containers, locals, statements);
                        break;
                    case "sbElementAt":
                        FillSbElementAt(env, ctx, method, containers, locals, statements);
                        break;
                    case "sbKeyAt":
                    case "sbValueAt":
                        FillSbMapEntryAt(env, ctx, method, containers,
                            key: method.Name == "sbKeyAt", locals, statements);
                        break;
                    case "sbTypeName":
                        FillSbTypeName(env, ctx, method, locals, statements);
                        break;
                    case "sbBuildArray":
                        FillSbBuild(env, ctx, method, containers, FieldKind.Array, locals, statements);
                        break;
                    case "sbBuildList":
                        FillSbBuild(env, ctx, method, containers, FieldKind.List, locals, statements);
                        break;
                    case "sbBuildMap":
                        FillSbBuild(env, ctx, method, containers, FieldKind.Map, locals, statements);
                        break;
                }
                env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                    new BoundBlock(syntax, statements)));
            }
        }

        // 闭合容器实参清单（去重、快照序）：格式层视图的分支候选集。
        private static List<(FieldKind Kind, TypeSymbol Type)> SbViewContainers(BindEnvironment env)
        {
            var result = new List<(FieldKind, TypeSymbol)>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var candidate in env.Unit.Symbols.ConstructedTypeSnapshot())
            {
                if (candidate is not TypeSymbol type
                    || type.TypeArguments is not { Count: > 0 }
                    || !IsClosedWireType(type))
                {
                    continue;
                }
                FieldKind kind;
                if (SerializationFacts.IsArray(type, env.Unit.Symbols, out _))
                {
                    kind = FieldKind.Array;
                }
                else if (SerializationFacts.IsList(type, env.Unit.Symbols, out _))
                {
                    kind = FieldKind.List;
                }
                else if (SerializationFacts.IsMap(type, env.Unit.Symbols, out _, out _))
                {
                    kind = FieldKind.Map;
                }
                else
                {
                    continue;
                }
                if (seen.Add(CanonicalSymbolPrinter.PrintType(type)))
                {
                    result.Add((kind, type));
                }
            }
            return result;
        }

        private static BoundExpression Param(ASTNode syntax, MethodSymbol method, int index) =>
            new BoundValueReferenceExpression(syntax, method.Parameters[index],
                method.Parameters[index].Type!);

        // if (value is Target) return <buildReturn(body)> 链（自底向上包，
        // 首个候选优先级最高）；buildReturn 可把中间语句追加进 body。
        private static void SbIsBranchChain(SynthContext ctx, BoundExpression value,
            List<BoundStatement> statements,
            IEnumerable<(SemanticSymbol Target, System.Func<List<BoundStatement>, BoundStatement> Return)> branches,
            BoundBlock otherwise)
        {
            BoundBlock choices = otherwise;
            foreach (var (target, buildReturn) in branches.Reverse().ToArray())
            {
                var condition = new BoundTypeCheckExpression(ctx.Syntax, BoundTypeCheckKind.Is,
                    value, target, null, ctx.Env.B.Bool);
                var body = new List<BoundStatement>();
                body.Add(buildReturn(body));
                choices = new BoundBlock(ctx.Syntax, new BoundStatement[]
                {
                    new BoundIfStatement(ctx.Syntax, condition,
                        new BoundBlock(ctx.Syntax, body), choices),
                });
            }
            statements.AddRange(choices.Statements);
        }

        private static BoundBlock SbThrow(SynthContext ctx, string exceptionName,
            string prefix, BoundExpression detail)
        {
            var exception = ctx.Env.B.Core.Types.First(t => t.Name == exceptionName);
            var init = exception.Methods.First(m => m.Kind == MethodKind.Init
                && m.Parameters.Count == 1
                && ReferenceEquals(m.Parameters[0].Type, ctx.Env.B.String));
            var detailText = new BoundInstanceCallExpression(ctx.Syntax,
                Cast(ctx, detail, ctx.Env.B.Any),
                ctx.Env.B.Any.Methods.First(m => m.Name == "toString"),
                new List<BoundExpression>(), ctx.Env.B.String);
            var message = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                BindingDriver.MakeStringLiteral(ctx.Env, prefix), detailText, ctx.Env.B.String);
            return new BoundBlock(ctx.Syntax, new BoundStatement[]
            {
                new BoundThrowStatement(ctx.Syntax, new BoundNewExpression(ctx.Syntax,
                    exception, init, new BoundExpression[] { message })),
            });
        }

        private static void FillSbKind(BindEnvironment env, SynthContext ctx, MethodSymbol method,
            TypeSymbol parcel, List<(FieldKind Kind, TypeSymbol Type)> containers,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var value = Param(syntax, method, 0);
            var scalars = new (TypeSymbol Type, int Code)[]
            {
                (env.B.Bool, SbKindBool), (env.B.Char, SbKindChar),
                (env.B.Int8, SbKindI8), (env.B.UInt8, SbKindU8),
                (env.B.Int16, SbKindI16), (env.B.UInt16, SbKindU16),
                (env.B.Int32, SbKindI32), (env.B.UInt32, SbKindU32),
                (env.B.Int64, SbKindI64), (env.B.UInt64, SbKindU64),
                (env.B.Float, SbKindF32), (env.B.Double, SbKindF64),
                (env.B.String, SbKindString),
            };
            var branches = new List<(SemanticSymbol, System.Func<List<BoundStatement>, BoundStatement>)>();
            foreach (var (type, code) in scalars)
            {
                branches.Add((type, _ =>
                    new BoundReturnStatement(syntax, IntLiteral(ctx, code, IntType.I32, env.B.Int32))));
            }
            branches.Add((parcel, _ =>
                new BoundReturnStatement(syntax, IntLiteral(ctx, SbKindParcel, IntType.I32, env.B.Int32))));
            foreach (var (kind, type) in containers)
            {
                var code = kind == FieldKind.Array ? SbKindArray
                    : kind == FieldKind.List ? SbKindList : SbKindMap;
                branches.Add((type, _ =>
                    new BoundReturnStatement(syntax, IntLiteral(ctx, code, IntType.I32, env.B.Int32))));
            }
            SbIsBranchChain(ctx, value, statements, branches,
                new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundReturnStatement(syntax, IntLiteral(ctx, 0, IntType.I32, env.B.Int32)),
                }));
        }

        private static void FillSbLength(BindEnvironment env, SynthContext ctx, MethodSymbol method,
            TypeSymbol parcel, List<(FieldKind Kind, TypeSymbol Type)> containers,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var value = Param(syntax, method, 0);
            var branches = new List<(SemanticSymbol, System.Func<List<BoundStatement>, BoundStatement>)>();
            foreach (var (kind, type) in containers)
            {
                branches.Add((type, _ =>
                {
                    var typed = Cast(ctx, value, type);
                    BoundExpression length = kind == FieldKind.Array
                        ? Cast(ctx, new BoundFieldAccessExpression(syntax, typed,
                            ctx.ArrayLength, env.B.Int32), env.B.Int64)
                        : kind == FieldKind.List
                            ? new BoundFieldAccessExpression(syntax, typed,
                                ctx.ListLength, env.B.Int64)
                            : new BoundFieldAccessExpression(syntax, typed,
                                ctx.MapCount, env.B.Int64);
                    return new BoundReturnStatement(syntax, length);
                }));
            }
            var elementCount = parcel.Methods.First(m => m.Name == "elementCount");
            branches.Add((parcel, _ => new BoundReturnStatement(syntax,
                new BoundInstanceCallExpression(syntax, Cast(ctx, value, parcel),
                    elementCount, new List<BoundExpression>(), env.B.Int64))));
            SbIsBranchChain(ctx, value, statements, branches,
                SbThrow(ctx, "IllegalStateException", "sbLength：不是 SB 容器 ", value));
        }

        // 活容器视图的元素上抛：raw 恒为可空槽（Array/List 访问器的 T?
        // 返回）；null 原样上抛为真实 Any? null，非 null 静态 typed 值
        // 直接装箱——不做 wire 编解码（视图语义是「原样读取活值」）。
        private static BoundExpression ViewBoxElement(SynthContext ctx, BoundExpression raw,
            SemanticSymbol elementType, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var saved = Save(ctx, raw, locals, statements, "viewElem");
            var resultType = ctx.Env.Unit.Symbols.GetConstructedType(
                ctx.Env.B.NullableDefinition, ctx.Env.B.Any);
            var result = NewLocal(ctx, locals, "viewBoxed", resultType);
            statements.Add(new BoundLocalDeclarationStatement(syntax, result,
                Null(ctx, resultType)));
            var inner = NullableElement(ctx, elementType) ?? elementType;
            var present = new List<BoundStatement>();
            present.Add(new BoundAssignmentStatement(syntax, Ref(ctx, result),
                Cast(ctx, Cast(ctx, saved, inner), ctx.Env.B.Any)));
            statements.Add(new BoundIfStatement(syntax,
                new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpNe, saved,
                    Null(ctx, saved.Type), ctx.Env.B.Bool),
                new BoundBlock(syntax, present), null));
            return Ref(ctx, result);
        }

        // 动态构造侧的元素解包：严格核验后转型。cast 不是类型检查——
        // rigi_try_cast 带数值宽展路径（i32 盒可「转换」为 i64，§4.6.3
        // 明令验证先于 cast），元素身份必须先经名义 is 判定；可空目标
        // 放行 null，非空目标遇 null 以 CastException 失败。
        // raw 是活 null（Array<Any?> 槽），非 wire 哨兵。
        private static BoundExpression ViewUnboxElement(SynthContext ctx, BoundExpression raw,
            SemanticSymbol elementType, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var saved = Save(ctx, raw, locals, statements, "buildElem");
            var inner = NullableElement(ctx, elementType) ?? elementType;
            var checkedLocal = NewLocal(ctx, locals, "buildChecked", inner);
            var ok = new List<BoundStatement>
            {
                new BoundLocalDeclarationStatement(syntax, checkedLocal, Cast(ctx, saved, inner)),
            };
            BoundExpression finish;
            if (NullableElement(ctx, elementType) == null)
            {
                finish = Ref(ctx, checkedLocal);
            }
            else
            {
                var result = NewLocal(ctx, locals, "buildUnboxed", elementType);
                ok.Add(new BoundLocalDeclarationStatement(syntax, result, Null(ctx, elementType)));
                var present = new List<BoundStatement>();
                present.Add(new BoundAssignmentStatement(syntax, Ref(ctx, result),
                    Cast(ctx, Ref(ctx, checkedLocal), elementType)));
                ok.Add(new BoundIfStatement(syntax,
                    new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpNe, saved,
                        Null(ctx, saved.Type), ctx.Env.B.Bool),
                    new BoundBlock(syntax, present), null));
                finish = Ref(ctx, result);
            }
            var isCheck = new BoundTypeCheckExpression(syntax, BoundTypeCheckKind.Is,
                saved, inner, null, ctx.Env.B.Bool);
            BoundExpression condition = isCheck;
            if (NullableElement(ctx, elementType) != null)
            {
                // 可空目标：null 合法（条件 = is inner 或 值为 null）
                condition = new BoundBinaryExpression(syntax, BilIntrinsicOp.Or, isCheck,
                    new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpEq, saved,
                        Null(ctx, saved.Type), ctx.Env.B.Bool), ctx.Env.B.Bool);
            }
            statements.Add(new BoundIfStatement(syntax, condition,
                new BoundBlock(syntax, ok),
                SbThrow(ctx, "CastException", "sbBuild：元素类型不符 ", saved)));
            return finish;
        }

        private static void FillSbElementAt(BindEnvironment env, SynthContext ctx,
            MethodSymbol method, List<(FieldKind Kind, TypeSymbol Type)> containers,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var value = Param(syntax, method, 0);
            var index = Param(syntax, method, 1);
            var branches = new List<(SemanticSymbol, System.Func<List<BoundStatement>, BoundStatement>)>();
            foreach (var (kind, type) in containers)
            {
                if (kind == FieldKind.Map)
                {
                    continue;
                }
                SerializationFacts.IsArray(type, env.Unit.Symbols, out var arrayElem);
                SerializationFacts.IsList(type, env.Unit.Symbols, out var listElem);
                var elem = arrayElem ?? listElem!;
                branches.Add((type, body =>
                {
                    var typed = Cast(ctx, value, type);
                    BoundExpression raw = kind == FieldKind.Array
                        ? new BoundIndexExpression(syntax, typed,
                            Cast(ctx, index, env.B.Int32), ctx.ArrayGetAt,
                            ctx.Env.Unit.Symbols.GetConstructedType(
                                ctx.Env.B.NullableDefinition, elem))
                        : new BoundInstanceCallExpression(syntax, typed, ctx.ListGetAt,
                            new List<BoundExpression> { index },
                            ctx.Env.Unit.Symbols.GetConstructedType(
                                ctx.Env.B.NullableDefinition, elem));
                    return new BoundReturnStatement(syntax,
                        ViewBoxElement(ctx, raw, elem, locals, body));
                }));
            }
            SbIsBranchChain(ctx, value, statements, branches,
                SbThrow(ctx, "IllegalStateException", "sbElementAt：不是 Array/List ", value));
        }

        private static void FillSbMapEntryAt(BindEnvironment env, SynthContext ctx,
            MethodSymbol method, List<(FieldKind Kind, TypeSymbol Type)> containers,
            bool key, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var value = Param(syntax, method, 0);
            var index = Param(syntax, method, 1);
            var branches = new List<(SemanticSymbol, System.Func<List<BoundStatement>, BoundStatement>)>();
            foreach (var (kind, type) in containers)
            {
                if (kind != FieldKind.Map)
                {
                    continue;
                }
                SerializationFacts.IsMap(type, env.Unit.Symbols, out var keyType, out var valueType);
                var accessor = key ? ctx.MapKeyAt : ctx.MapValueAt;
                var entryType = key ? keyType! : valueType!;
                branches.Add((type, body =>
                {
                    var typed = Cast(ctx, value, type);
                    var raw = new BoundInstanceCallExpression(syntax, typed, accessor,
                        new List<BoundExpression> { index },
                        ctx.Env.Unit.Symbols.GetConstructedType(
                            ctx.Env.B.NullableDefinition, entryType));
                    return new BoundReturnStatement(syntax,
                        ViewBoxElement(ctx, raw, entryType, locals, body));
                }));
            }
            SbIsBranchChain(ctx, value, statements, branches,
                SbThrow(ctx, "IllegalStateException",
                    key ? "sbKeyAt：不是 Map " : "sbValueAt：不是 Map ", value));
        }

        private static void FillSbTypeName(BindEnvironment env, SynthContext ctx,
            MethodSymbol method, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var value = Param(syntax, method, 0);
            var resultType = env.Unit.Symbols.GetConstructedType(env.B.TypeDefinition, env.B.Any);
            var typeValue = new BoundTypeOfExpression(syntax, value, null, resultType);
            var toString = env.B.Any.Methods.First(m => m.Name == "toString");
            statements.Add(new BoundReturnStatement(syntax,
                new BoundInstanceCallExpression(syntax, Cast(ctx, typeValue, env.B.Any),
                    toString, new List<BoundExpression>(), env.B.String)));
        }

        // sbBuild* 共体：名称双拼写分发 + 逐元素严格核验填入后返回活容器。
        // 参数序：sbBuildArray/List(elementTypeName, elements:Array<Any?>)；
        // sbBuildMap(keyTypeName, valueTypeName, keys, values)。
        private static void FillSbBuild(BindEnvironment env, SynthContext ctx,
            MethodSymbol method, List<(FieldKind Kind, TypeSymbol Type)> containers,
            FieldKind wanted, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var syntax = ctx.Syntax;
            var name = Param(syntax, method, 0);
            BoundBlock choices = SbThrow(ctx, "IllegalArgumentException",
                method.Name + "：未登记的容器实参类型 ", name);
            foreach (var (kind, type) in containers.Where(c => c.Kind == wanted).Reverse().ToArray())
            {
                SerializationFacts.IsArray(type, env.Unit.Symbols, out var arrayElem);
                SerializationFacts.IsList(type, env.Unit.Symbols, out var listElem);
                SerializationFacts.IsMap(type, env.Unit.Symbols, out var keyType, out var valueType);
                var elem = arrayElem ?? listElem;
                // 名称参数序：sbBuildArray/List 的 elementTypeName 是参数 0；
                // sbBuildMap 的 keyTypeName/valueTypeName 是参数 0/1。
                var nameParams = kind == FieldKind.Map
                    ? new[] { (Index: 0, Type: keyType!), (Index: 1, Type: valueType!) }
                    : new[] { (Index: 0, Type: elem!) };
                BoundExpression? condition = null;
                foreach (var (paramIndex, target) in nameParams)
                {
                    // 双拼写并收：VM typeOf().toString() 为 BIL 别名形，
                    // native 为 canonical 形；两种字面量都参与等值比较。
                    var alias = BindingDriver.MakeStringLiteral(env,
                        SbViewName(target, canonicalForm: false));
                    var canonical = BindingDriver.MakeStringLiteral(env,
                        SbViewName(target, canonicalForm: true));
                    var nameRef = Param(syntax, method, paramIndex);
                    var matches = new BoundBinaryExpression(syntax, BilIntrinsicOp.Or,
                        new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpEq,
                            nameRef, alias, env.B.Bool),
                        new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpEq,
                            nameRef, canonical, env.B.Bool), env.B.Bool);
                    condition = condition == null
                        ? matches
                        : new BoundBinaryExpression(syntax, BilIntrinsicOp.And,
                            condition, matches, env.B.Bool);
                }
                var body = new List<BoundStatement>();
                FillSbBuildBranch(env, ctx, method, kind, type, elem,
                    keyType, valueType, locals, body);
                choices = new BoundBlock(syntax, new BoundStatement[]
                {
                    new BoundIfStatement(syntax, condition!,
                        new BoundBlock(syntax, body), choices),
                });
            }
            statements.AddRange(choices.Statements);
        }

        // 单个 sbBuild 分支体：构造目标容器 + 逐元素/键值填入 + 返回。
        private static void FillSbBuildBranch(BindEnvironment env, SynthContext ctx,
            MethodSymbol method, FieldKind kind, TypeSymbol containerType,
            SemanticSymbol? elem, SemanticSymbol? keyType, SemanticSymbol? valueType,
            List<LocalSymbol> locals, List<BoundStatement> body)
        {
            var syntax = ctx.Syntax;
            // 数据源槽：Array/List 取 elements（参数 1）；Map 取 keys/values
            //（参数 2/3），两数组同长（调用方契约）。
            var sourceRef = Param(syntax, method, kind == FieldKind.Map ? 2 : 1);
            var nLocal = NewLocal(ctx, locals, "n", env.B.Int32);
            body.Add(new BoundLocalDeclarationStatement(syntax, nLocal,
                new BoundFieldAccessExpression(syntax, sourceRef, ctx.ArrayLength, env.B.Int32)));
            var iLocal = NewLocal(ctx, locals, "i", env.B.Int32);
            body.Add(new BoundLocalDeclarationStatement(syntax, iLocal,
                IntLiteral(ctx, 0, IntType.I32, env.B.Int32)));
            var target = NewLocal(ctx, locals, "target", containerType);
            if (kind == FieldKind.Array)
            {
                body.Add(new BoundLocalDeclarationStatement(syntax, target,
                    new BoundCallExpression(syntax, ctx.ArrayOf,
                        new List<BoundExpression> { Ref(ctx, nLocal) }, containerType,
                        new SemanticSymbol[] { elem! })));
            }
            else
            {
                var definition = (containerType.ConstructedFrom ?? containerType);
                var init = definition.Methods.First(m =>
                    m.Kind == MethodKind.Init && m.Parameters.Count == 0);
                body.Add(new BoundLocalDeclarationStatement(syntax, target,
                    new BoundNewExpression(syntax, containerType, init,
                        new List<BoundExpression>())));
            }
            var loopBody = new List<BoundStatement>();
            if (kind == FieldKind.Map)
            {
                var keysRef = Param(syntax, method, 2);
                var valuesRef = Param(syntax, method, 3);
                var rawKey = new BoundIndexExpression(syntax, keysRef, Ref(ctx, iLocal),
                    ctx.ArrayGetAt, ctx.Env.Unit.Symbols.GetConstructedType(
                        ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
                var rawValue = new BoundIndexExpression(syntax, valuesRef, Ref(ctx, iLocal),
                    ctx.ArrayGetAt, ctx.Env.Unit.Symbols.GetConstructedType(
                        ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
                var key = ViewUnboxElement(ctx, rawKey, keyType!, locals, loopBody);
                var value = ViewUnboxElement(ctx, rawValue, valueType!, locals, loopBody);
                loopBody.Add(new BoundCallStatement(syntax, ctx.MapSet,
                    new List<BoundExpression> { key, value }, Ref(ctx, target)));
            }
            else
            {
                var raw = new BoundIndexExpression(syntax, sourceRef, Ref(ctx, iLocal),
                    ctx.ArrayGetAt, ctx.Env.Unit.Symbols.GetConstructedType(
                        ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
                var item = ViewUnboxElement(ctx, raw, elem!, locals, loopBody);
                if (kind == FieldKind.Array)
                {
                    loopBody.Add(new BoundAssignmentStatement(syntax,
                        new BoundIndexExpression(syntax, Ref(ctx, target), Ref(ctx, iLocal),
                            ctx.ArraySetAt, elem!), item));
                }
                else
                {
                    loopBody.Add(new BoundCallStatement(syntax, ctx.ListAdd,
                        new List<BoundExpression> { item }, Ref(ctx, target)));
                }
            }
            loopBody.Add(new BoundAssignmentStatement(syntax, Ref(ctx, iLocal),
                new BoundBinaryExpression(syntax, BilIntrinsicOp.Add,
                    Ref(ctx, iLocal), IntLiteral(ctx, 1, IntType.I32, env.B.Int32),
                    env.B.Int32)));
            body.Add(new BoundLoop(syntax, LoopKind.While, null)
            {
                Condition = new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpLt,
                    Ref(ctx, iLocal), Ref(ctx, nLocal), env.B.Bool),
                Body = new BoundBlock(syntax, loopBody),
            });
            body.Add(new BoundReturnStatement(syntax, Cast(ctx, Ref(ctx, target), env.B.Any)));
        }

        // SB 视图名的双拼写渲染：aliasForm=false 得 VM typeOf().toString()
        // 形态（BIL 别名 + 标准构造名，与 CanonicalSymbolPrinter 同规则）；
        // canonicalForm=true 得 native TypeInfo.name 形态（core:: 前缀 +
        // 定义本名，标准构造用 core::Array/core::Nullable 等）。
        private static string SbViewName(SemanticSymbol type, bool canonicalForm)
        {
            if (type is not TypeSymbol symbol)
            {
                throw new CompilerInternalException("SB 视图名仅支持闭合类型: " + type);
            }
            if (symbol.ConstructedFrom is { } definition
                && symbol.TypeArguments is { Count: > 0 } args)
            {
                var head = SbViewHead(definition, canonicalForm);
                var inner = string.Join(", ", args.Select(a => SbViewName(a, canonicalForm)));
                return head + "<" + inner + ">";
            }
            return SbViewHead(symbol, canonicalForm);
        }

        private static string SbViewHead(TypeSymbol symbol, bool canonicalForm)
        {
            if (!canonicalForm)
            {
                if (symbol.BilAlias != null)
                {
                    return symbol.BilAlias;
                }
                if (symbol.BilStandardConstructor != null)
                {
                    return symbol.BilStandardConstructor;
                }
            }
            var segments = new List<string>();
            var root = symbol;
            for (var t = symbol; t != null; t = t.DeclaringType)
            {
                segments.Insert(0, t.Name);
                root = t;
            }
            var ns = root.Namespace?.FullName;
            return (ns is { Length: > 0 } ? ns + "::" : "") + string.Join(".", segments);
        }
    }
}
