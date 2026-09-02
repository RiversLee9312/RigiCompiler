using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler
{
    // MW11d-B2：为每个 @Serializable 宿主合成 ..toParcel / ..fromParcel /
    // ..init.serializable，并填充 core.serialization.fromParcel。
    // 合成物走 SyntheticCellBodies → 正常 P4 BIL 产物流，VM/native 零分叉。
    //
    // 构造通道：..init.serializable(token: ..serializable.token) 是 MethodKind.Init，
    // 空体——分配仍走既有 new（alloc + ..init.wrapper + 本 init）。token 类型
    // 名以 `..` 开头，源码无法写出，故不与用户 init 签名冲突；动态 new T(token)
    // 经 mw.init.dispatch / VM TryFindInit 按实参类型命中本 init。
    // 对照 ..init.wrapper（Regular、由 VM 在 new 内自动调）与 mw.init.dispatch
    // （按 init 签名匹配）：本通道复用后者，不发明第二套对象构建。
    internal static class SerializationSynthesis
    {
        public static void Synthesize(BindEnvironment env)
        {
            var symbols = env.Unit.Symbols;
            var serializable = SerializationFacts.FindWrapper(symbols, "Serializable");
            var serializationBase = SerializationFacts.FindWrapper(symbols, "SerializationBase");
            var temporary = SerializationFacts.FindWrapper(symbols, "Temporary");
            var parcel = SerializationFacts.FindParcel(symbols);
            var ns = SerializationFacts.FindSerializationNamespace(symbols);
            if (serializable == null || parcel == null || ns == null) return;

            var syntax = FallbackSyntax(env);
            if (!EnsureInfrastructure(env, ns, parcel, syntax, out var iface, out var token))
            {
                return;
            }

            var hosts = new List<(TypeSymbol Type, ASTNode Syntax)>();
            CollectSerializableHosts(env, serializable, hosts);
            foreach (var (type, hostSyntax) in hosts)
            {
                EnsureHostMethods(type, iface, token, parcel, hostSyntax);
            }
            foreach (var (type, hostSyntax) in hosts)
            {
                FillHostBodies(type, hostSyntax, env, serializable, serializationBase,
                    temporary, parcel, iface, token);
            }
            FillFromParcelFunction(env, ns, parcel, token, iface, syntax);
            FillDeepCopyFunction(env, ns, parcel, iface, syntax);
        }

        public static MethodSymbol? FindToParcelMethod(SemanticSymbol? type, BindEnvironment env)
        {
            return FindSynthMethod(type, env, BilSpellings.ToParcelMethodName);
        }

        private static MethodSymbol? FindSynthMethod(SemanticSymbol? type, BindEnvironment env,
            string name)
        {
            var ns = SerializationFacts.FindSerializationNamespace(env.Unit.Symbols);
            var iface = ns?.Types.FirstOrDefault(t =>
                t.Name == BilSpellings.SerializableIfaceName);
            if (type is GenericParameterSymbol gp)
            {
                var serializable = SerializationFacts.FindWrapper(env.Unit.Symbols, "Serializable");
                if (serializable != null
                    && SerializationFacts.HasSerializableConstraint(gp, serializable))
                {
                    return iface?.Methods.FirstOrDefault(m => m.Name == name);
                }
                return null;
            }
            if (type is not TypeSymbol ts) return null;
            var found = SymbolLookup.FindInstanceMethods(ts, name, env.Unit.Symbols);
            if (found.Count > 0) return found[0];
            return iface?.Methods.FirstOrDefault(m => m.Name == name);
        }

        private static ASTNode FallbackSyntax(BindEnvironment env)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                foreach (var decl in file.Declarations)
                {
                    return decl;
                }
            }
            throw new CompilerInternalException("合成序列化方法时编译单元无声明节点");
        }

        private static bool EnsureInfrastructure(BindEnvironment env, NamespaceSymbol ns,
            TypeSymbol parcel, ASTNode syntax, out TypeSymbol iface, out TypeSymbol token)
        {
            iface = ns.Types.FirstOrDefault(t => t.Name == BilSpellings.SerializableIfaceName)
                ?? CreateIface(ns, parcel);
            token = ns.Types.FirstOrDefault(t => t.Name == BilSpellings.SerializableTokenName)
                ?? CreateToken(env, ns, syntax);
            return true;
        }

        private static TypeSymbol CreateIface(NamespaceSymbol ns, TypeSymbol parcel)
        {
            var iface = new TypeSymbol(BilSpellings.SerializableIfaceName, TypeKind.Interface,
                ns: ns)
            {
                Accessibility = Accessibility.Public,
                IsAbstract = true,
            };
            var toParcel = new MethodSymbol(BilSpellings.ToParcelMethodName, MethodKind.Regular,
                owner: iface, returnType: parcel)
            {
                Accessibility = Accessibility.Public,
                IsAbstract = true,
                HasBody = false,
                IsSynthetic = true,
            };
            var fromParcel = new MethodSymbol(BilSpellings.FromParcelMethodName, MethodKind.Regular,
                owner: iface)
            {
                Accessibility = Accessibility.Public,
                IsAbstract = true,
                HasBody = false,
                IsSynthetic = true,
            };
            fromParcel.Parameters.Add(new ParameterSymbol("parcel", parcel));
            iface.Methods.Add(toParcel);
            iface.Methods.Add(fromParcel);
            ns.Types.Add(iface);
            return iface;
        }

        private static TypeSymbol CreateToken(BindEnvironment env, NamespaceSymbol ns,
            ASTNode syntax)
        {
            var token = new TypeSymbol(BilSpellings.SerializableTokenName, TypeKind.Class,
                ns: ns, baseType: env.B.Object)
            {
                Accessibility = Accessibility.Public,
            };
            var init = new MethodSymbol("init", MethodKind.Init, owner: token, returnType: null)
            {
                Accessibility = Accessibility.Public,
                HasBody = true,
                IsSynthetic = true,
            };
            token.Methods.Add(init);
            env.SyntheticCellBodies.Add(new BoundFunctionBody(init, Array.Empty<LocalSymbol>(),
                new BoundBlock(syntax, Array.Empty<BoundStatement>())));
            ns.Types.Add(token);
            return token;
        }

        private static void CollectSerializableHosts(BindEnvironment env, TypeSymbol serializable,
            List<(TypeSymbol, ASTNode)> hosts)
        {
            foreach (var file in env.Unit.SourceFiles)
            {
                foreach (var decl in file.Declarations)
                {
                    CollectHosts(decl, env, serializable, hosts);
                }
            }
        }

        private static void CollectHosts(ASTNode node, BindEnvironment env, TypeSymbol serializable,
            List<(TypeSymbol, ASTNode)> hosts)
        {
            switch (node)
            {
                case ClassDeclarationASTNode or StructDeclarationASTNode:
                    var type = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    if (type.Kind is TypeKind.Class or TypeKind.Struct
                        && SerializationFacts.HasWrapper(type, serializable)
                        && !type.IsAbstract)
                    {
                        hosts.Add((type, node));
                    }
                    foreach (var member in MembersOf(node))
                        CollectHosts(member, env, serializable, hosts);
                    return;
                case InterfaceDeclarationASTNode iface:
                    foreach (var member in iface.Members)
                        CollectHosts(member, env, serializable, hosts);
                    return;
                case WrapperDeclarationASTNode wrapper:
                    foreach (var member in wrapper.Members)
                        CollectHosts(member, env, serializable, hosts);
                    return;
                case EnumStructDeclarationASTNode enumStruct:
                    foreach (var member in enumStruct.Members)
                        CollectHosts(member, env, serializable, hosts);
                    return;
            }
        }

        private static List<ASTNode> MembersOf(ASTNode node) => node switch
        {
            ClassDeclarationASTNode d => d.Members,
            StructDeclarationASTNode d => d.Members,
            InterfaceDeclarationASTNode d => d.Members,
            EnumStructDeclarationASTNode d => d.Members,
            WrapperDeclarationASTNode d => d.Members,
            _ => throw new CompilerInternalException("非类型声明: " + node.GetType().Name),
        };

        private static void EnsureHostMethods(TypeSymbol type, TypeSymbol iface, TypeSymbol token,
            TypeSymbol parcel, ASTNode syntax)
        {
            if (!type.Interfaces.Contains(iface))
            {
                type.Interfaces.Add(iface);
            }
            if (!type.Methods.Any(m => m.Name == BilSpellings.InitSerializableMethodName))
            {
                var init = new MethodSymbol(BilSpellings.InitSerializableMethodName,
                    MethodKind.Init, owner: type, returnType: null)
                {
                    Accessibility = Accessibility.Private,
                    HasBody = true,
                    IsSynthetic = true,
                };
                init.Parameters.Add(new ParameterSymbol("token", token));
                type.Methods.Add(init);
            }
            if (!type.Methods.Any(m => m.Name == BilSpellings.ToParcelMethodName))
            {
                var toParcel = new MethodSymbol(BilSpellings.ToParcelMethodName, MethodKind.Regular,
                    owner: type, returnType: parcel)
                {
                    Accessibility = Accessibility.Public,
                    HasBody = true,
                    IsSynthetic = true,
                    IsOverride = true,
                };
                type.Methods.Add(toParcel);
            }
            if (!type.Methods.Any(m => m.Name == BilSpellings.FromParcelMethodName))
            {
                var fromParcel = new MethodSymbol(BilSpellings.FromParcelMethodName,
                    MethodKind.Regular, owner: type)
                {
                    Accessibility = Accessibility.Public,
                    HasBody = true,
                    IsSynthetic = true,
                    IsOverride = true,
                };
                fromParcel.Parameters.Add(new ParameterSymbol("parcel", parcel));
                type.Methods.Add(fromParcel);
            }
        }

        private static void FillHostBodies(TypeSymbol type, ASTNode syntax, BindEnvironment env,
            TypeSymbol serializable, TypeSymbol? serializationBase, TypeSymbol? temporary,
            TypeSymbol parcel, TypeSymbol iface, TypeSymbol token)
        {
            if (env.SyntheticCellBodies.Any(b =>
                ReferenceEquals(b.Method.Owner, type)
                && b.Method.Name == BilSpellings.ToParcelMethodName))
            {
                return;
            }
            var ctx = new SynthContext(env, syntax, type, serializable, serializationBase,
                temporary, parcel, iface, token);
            FillInitSerializable(ctx);
            FillToParcel(ctx);
            FillFromParcel(ctx);
        }

        private static void FillInitSerializable(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m =>
                m.Name == BilSpellings.InitSerializableMethodName);
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method,
                Array.Empty<LocalSymbol>(),
                new BoundBlock(ctx.Syntax, Array.Empty<BoundStatement>())));
        }

        private static void FillToParcel(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m => m.Name == BilSpellings.ToParcelMethodName);
            var locals = new List<LocalSymbol>();
            var statements = new List<BoundStatement>();
            var parcelLocal = new LocalSymbol("p", ctx.Parcel, isConst: false);
            locals.Add(parcelLocal);
            var typeName = CanonicalSymbolPrinter.PrintType(ctx.Host, compact: true);
            var parcelNew = new BoundNewExpression(ctx.Syntax, ctx.Parcel, ctx.ParcelInit,
                new List<BoundExpression> { BindingDriver.MakeStringLiteral(ctx.Env, typeName) });
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, parcelLocal, parcelNew));
            var parcelRef = new BoundValueReferenceExpression(ctx.Syntax, parcelLocal, ctx.Parcel);
            var selfType = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            foreach (var (field, fieldType) in SerializableFields(ctx, forWrite: false))
            {
                var fieldExpr = new BoundFieldAccessExpression(ctx.Syntax,
                    new BoundThisExpression(ctx.Syntax, selfType), field, fieldType!);
                var encoded = EncodeValue(ctx, fieldExpr, fieldType!, locals, statements);
                var stored = StoredType(ctx, fieldType!);
                statements.Add(new BoundCallStatement(ctx.Syntax, ctx.SetElement,
                    new List<BoundExpression>
                    {
                        BindingDriver.MakeStringLiteral(ctx.Env, field.Name),
                        encoded,
                    },
                    parcelRef, new[] { stored }));
            }
            statements.Add(new BoundReturnStatement(ctx.Syntax, parcelRef));
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                new BoundBlock(ctx.Syntax, statements)));
        }

        private static void FillFromParcel(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m => m.Name == BilSpellings.FromParcelMethodName);
            var locals = new List<LocalSymbol>();
            var statements = new List<BoundStatement>();
            var parcelParam = method.Parameters[0];
            var parcelRef = new BoundValueReferenceExpression(ctx.Syntax, parcelParam, ctx.Parcel);
            var selfType = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            foreach (var (field, fieldType) in SerializableFields(ctx, forWrite: true))
            {
                var stored = StoredType(ctx, fieldType!);
                var got = new BoundInstanceCallExpression(ctx.Syntax, parcelRef, ctx.GetElement,
                    new List<BoundExpression>
                    {
                        BindingDriver.MakeStringLiteral(ctx.Env, field.Name),
                    },
                    ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, stored),
                    new[] { stored });
                var decoded = DecodeValue(ctx, got, fieldType!, locals, statements);
                statements.Add(new BoundAssignmentStatement(ctx.Syntax,
                    new BoundFieldAccessExpression(ctx.Syntax,
                        new BoundThisExpression(ctx.Syntax, selfType), field, fieldType!),
                    decoded));
            }
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                new BoundBlock(ctx.Syntax, statements)));
        }

        private static void FillFromParcelFunction(BindEnvironment env, NamespaceSymbol ns,
            TypeSymbol parcel, TypeSymbol token, TypeSymbol iface, ASTNode syntax)
        {
            var method = ns.Methods.FirstOrDefault(m => m.Name == "fromParcel"
                && m.GenericParameters.Count == 1);
            if (method == null) return;
            if (env.SyntheticCellBodies.Any(b => ReferenceEquals(b.Method, method))) return;
            var tParam = method.GenericParameters[0];
            var parcelParam = method.Parameters[0];
            var locals = new List<LocalSymbol>();
            var tokenLocal = new LocalSymbol("tok", token, isConst: false);
            var instLocal = new LocalSymbol("inst", tParam, isConst: false);
            locals.Add(tokenLocal);
            locals.Add(instLocal);
            var tokenInit = token.Methods.First(m => m.Kind == MethodKind.Init);
            var fromParcel = iface.Methods.First(m => m.Name == BilSpellings.FromParcelMethodName);
            var statements = new List<BoundStatement>
            {
                new BoundLocalDeclarationStatement(syntax, tokenLocal,
                    new BoundNewExpression(syntax, token, tokenInit,
                        Array.Empty<BoundExpression>())),
                new BoundLocalDeclarationStatement(syntax, instLocal,
                    new BoundDynamicNewExpression(syntax, null, tParam,
                        new List<BoundExpression>
                        {
                            new BoundValueReferenceExpression(syntax, tokenLocal, token),
                        }, tParam)),
                new BoundCallStatement(syntax, fromParcel,
                    new List<BoundExpression>
                    {
                        new BoundValueReferenceExpression(syntax, parcelParam, parcel),
                    },
                    new BoundValueReferenceExpression(syntax, instLocal, tParam)),
                new BoundReturnStatement(syntax,
                    new BoundValueReferenceExpression(syntax, instLocal, tParam)),
            };
            env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                new BoundBlock(syntax, statements)));
        }

        private static void FillDeepCopyFunction(BindEnvironment env, NamespaceSymbol ns,
            TypeSymbol parcel, TypeSymbol iface, ASTNode syntax)
        {
            var method = ns.Methods.FirstOrDefault(m => m.Name == "deepCopy"
                && m.GenericParameters.Count == 1);
            if (method == null) return;
            if (env.SyntheticCellBodies.Any(b => ReferenceEquals(b.Method, method))) return;
            var tParam = method.GenericParameters[0];
            var valueParam = method.Parameters[0];
            var fromParcel = ns.Methods.First(m => m.Name == "fromParcel"
                && m.GenericParameters.Count == 1);
            var toParcel = iface.Methods.First(m => m.Name == BilSpellings.ToParcelMethodName);
            var locals = new List<LocalSymbol>();
            var pLocal = new LocalSymbol("p", parcel, isConst: false);
            locals.Add(pLocal);
            var statements = new List<BoundStatement>
            {
                new BoundLocalDeclarationStatement(syntax, pLocal,
                    new BoundInstanceCallExpression(syntax,
                        new BoundValueReferenceExpression(syntax, valueParam, tParam),
                        toParcel, Array.Empty<BoundExpression>(), parcel)),
                new BoundReturnStatement(syntax,
                    new BoundCallExpression(syntax, fromParcel,
                        new List<BoundExpression>
                        {
                            new BoundValueReferenceExpression(syntax, pLocal, parcel),
                        }, tParam, new SemanticSymbol[] { tParam })),
            };
            env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                new BoundBlock(syntax, statements)));
        }

        private static IEnumerable<(FieldSymbol Field, SemanticSymbol? FieldType)>
            SerializableFields(SynthContext ctx, bool forWrite)
        {
            foreach (var (field, fieldType) in FieldClosureChecker.ClosureFieldsOf(ctx.Host,
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
                if (forWrite && field.IsConst) continue;
                if (fieldType is null or ErrorTypeSymbol) continue;
                yield return (field, fieldType);
            }
        }

        private enum FieldKind { Scalar, Object, Array, List, Map, Parcel }

        private static FieldKind Classify(SynthContext ctx, SemanticSymbol type)
        {
            var symbols = ctx.Env.Unit.Symbols;
            if (SerializationFacts.IsArray(type, symbols, out _)) return FieldKind.Array;
            if (SerializationFacts.IsList(type, symbols, out _)) return FieldKind.List;
            if (SerializationFacts.IsMap(type, symbols, out _, out _)) return FieldKind.Map;
            if (SerializationFacts.IsParcel(type, symbols)) return FieldKind.Parcel;
            if (type is GenericParameterSymbol gp)
            {
                if (SerializationFacts.HasSerializableConstraint(gp, ctx.Serializable))
                    return FieldKind.Object;
                return FieldKind.Scalar;
            }
            if (SerializationFacts.HasWrapper(type, ctx.Serializable)) return FieldKind.Object;
            return FieldKind.Scalar;
        }

        private static SemanticSymbol StoredType(SynthContext ctx, SemanticSymbol fieldType)
        {
            return Classify(ctx, fieldType) switch
            {
                FieldKind.Object or FieldKind.Map or FieldKind.Parcel => ctx.Parcel,
                FieldKind.Array or FieldKind.List => ctx.ArrayAny,
                _ => fieldType,
            };
        }

        private static BoundExpression EncodeValue(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            return Classify(ctx, type) switch
            {
                FieldKind.Array => EncodeArray(ctx, value, type, locals, statements),
                FieldKind.List => EncodeList(ctx, value, type, locals, statements),
                FieldKind.Map => EncodeMap(ctx, value, type, locals, statements),
                FieldKind.Object => EncodeObject(ctx, value, type),
                FieldKind.Parcel => value,
                _ => value,
            };
        }

        private static BoundExpression EncodeObject(SynthContext ctx, BoundExpression value,
            SemanticSymbol type)
        {
            var method = FindToParcelMethod(type, ctx.Env)
                ?? throw new CompilerInternalException("缺少 ..toParcel");
            return new BoundInstanceCallExpression(ctx.Syntax, value, method,
                Array.Empty<BoundExpression>(), ctx.Parcel);
        }

        private static BoundExpression EncodeArray(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            SerializationFacts.IsArray(type, ctx.Env.Unit.Symbols, out var elem);
            return MaterializeArrayAny(ctx, value, elem!, isList: false, locals, statements);
        }

        private static BoundExpression EncodeList(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            SerializationFacts.IsList(type, ctx.Env.Unit.Symbols, out var elem);
            return MaterializeArrayAny(ctx, value, elem!, isList: true, locals, statements);
        }

        private static BoundExpression MaterializeArrayAny(SynthContext ctx, BoundExpression source,
            SemanticSymbol elemType, bool isList, List<LocalSymbol> locals,
            List<BoundStatement> statements)
        {
            var srcLocal = NewLocal(ctx, locals, isList ? "ls" : "ar", source.Type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, srcLocal, source));
            var srcRef = new BoundValueReferenceExpression(ctx.Syntax, srcLocal, source.Type);
            BoundExpression lengthExpr;
            SemanticSymbol indexType;
            if (isList)
            {
                lengthExpr = new BoundFieldAccessExpression(ctx.Syntax, srcRef, ctx.ListLength,
                    ctx.Env.B.Int64);
                indexType = ctx.Env.B.Int64;
            }
            else
            {
                lengthExpr = new BoundFieldAccessExpression(ctx.Syntax, srcRef, ctx.ArrayLength,
                    ctx.Env.B.Int32);
                indexType = ctx.Env.B.Int32;
            }
            var lenLocal = NewLocal(ctx, locals, "n", indexType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, lenLocal, lengthExpr));
            var sizeForAlloc = isList
                ? Cast(ctx, new BoundValueReferenceExpression(ctx.Syntax, lenLocal, indexType),
                    ctx.Env.B.Int32)
                : new BoundValueReferenceExpression(ctx.Syntax, lenLocal, indexType);
            var destLocal = NewLocal(ctx, locals, "aa", ctx.ArrayAny);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, destLocal,
                new BoundCallExpression(ctx.Syntax, ctx.ArrayOf,
                    new List<BoundExpression> { sizeForAlloc }, ctx.ArrayAny,
                    new[] { ctx.Env.B.Any })));
            var iLocal = NewLocal(ctx, locals, "i", indexType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, iLocal,
                IntLiteral(ctx, 0, isList ? IntType.I64 : IntType.I32, indexType)));
            var loop = new BoundLoop(ctx.Syntax, LoopKind.While, null);
            loop.Condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpLt,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                new BoundValueReferenceExpression(ctx.Syntax, lenLocal, indexType),
                ctx.Env.B.Bool);
            var body = new List<BoundStatement>();
            BoundExpression elemRead;
            if (isList)
            {
                var raw = new BoundInstanceCallExpression(ctx.Syntax, srcRef, ctx.ListGetAt,
                    new List<BoundExpression>
                    {
                        new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                    },
                    ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, elemType));
                elemRead = Cast(ctx, raw, elemType);
            }
            else
            {
                var raw = new BoundIndexExpression(ctx.Syntax, srcRef,
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                    ctx.ArrayGetAt,
                    ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, elemType));
                elemRead = Cast(ctx, raw, elemType);
            }
            var encoded = EncodeValue(ctx, elemRead, elemType, locals, body);
            var boxed = Cast(ctx, encoded, ctx.Env.B.Any);
            var destRef = new BoundValueReferenceExpression(ctx.Syntax, destLocal, ctx.ArrayAny);
            var destIndex = isList
                ? Cast(ctx, new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                    ctx.Env.B.Int32)
                : new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType);
            body.Add(new BoundAssignmentStatement(ctx.Syntax,
                new BoundIndexExpression(ctx.Syntax, destRef, destIndex, ctx.ArraySetAt,
                    ctx.Env.B.Any),
                boxed));
            body.Add(new BoundAssignmentStatement(ctx.Syntax,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                    IntLiteral(ctx, 1, isList ? IntType.I64 : IntType.I32, indexType),
                    indexType)));
            loop.Body = new BoundBlock(ctx.Syntax, body);
            statements.Add(loop);
            return destRef;
        }

        private static BoundExpression EncodeMap(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            SerializationFacts.IsMap(type, ctx.Env.Unit.Symbols, out _, out var valType);
            var mapLocal = NewLocal(ctx, locals, "mp", value.Type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, mapLocal, value));
            var nested = NewLocal(ctx, locals, "np", ctx.Parcel);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, nested,
                new BoundNewExpression(ctx.Syntax, ctx.Parcel, ctx.ParcelInit,
                    new List<BoundExpression>
                    {
                        BindingDriver.MakeStringLiteral(ctx.Env, BilSpellings.MapParcelTypeName),
                    })));
            var nestedRef = new BoundValueReferenceExpression(ctx.Syntax, nested, ctx.Parcel);
            var mapRef = new BoundValueReferenceExpression(ctx.Syntax, mapLocal, value.Type);
            var nLocal = NewLocal(ctx, locals, "mn", ctx.Env.B.Int64);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, nLocal,
                new BoundFieldAccessExpression(ctx.Syntax, mapRef, ctx.MapCount,
                    ctx.Env.B.Int64)));
            var iLocal = NewLocal(ctx, locals, "mi", ctx.Env.B.Int64);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, iLocal,
                IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64)));
            var loop = new BoundLoop(ctx.Syntax, LoopKind.While, null);
            loop.Condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpLt,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int64),
                new BoundValueReferenceExpression(ctx.Syntax, nLocal, ctx.Env.B.Int64),
                ctx.Env.B.Bool);
            var iRef = new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int64);
            var keyOpt = ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition,
                ctx.Env.B.String);
            var valOpt = ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition,
                valType!);
            var rawKey = new BoundInstanceCallExpression(ctx.Syntax, mapRef, ctx.MapKeyAt,
                new List<BoundExpression> { iRef }, keyOpt);
            var rawVal = new BoundInstanceCallExpression(ctx.Syntax, mapRef, ctx.MapValueAt,
                new List<BoundExpression> { iRef }, valOpt);
            var body = new List<BoundStatement>();
            var keyExpr = Cast(ctx, rawKey, ctx.Env.B.String);
            var valExpr = Cast(ctx, rawVal, valType!);
            var encoded = EncodeValue(ctx, valExpr, valType!, locals, body);
            body.Add(new BoundCallStatement(ctx.Syntax, ctx.SetElement,
                new List<BoundExpression> { keyExpr, encoded },
                nestedRef, new[] { StoredType(ctx, valType!) }));
            body.Add(new BoundAssignmentStatement(ctx.Syntax, iRef,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add, iRef,
                    IntLiteral(ctx, 1, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Int64)));
            loop.Body = new BoundBlock(ctx.Syntax, body);
            statements.Add(loop);
            return nestedRef;
        }

        private static BoundExpression DecodeValue(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            return Classify(ctx, type) switch
            {
                FieldKind.Array => DecodeArray(ctx, stored, type, locals, statements),
                FieldKind.List => DecodeList(ctx, stored, type, locals, statements),
                FieldKind.Map => DecodeMap(ctx, stored, type, locals, statements),
                FieldKind.Object => DecodeObject(ctx, stored, type, locals, statements),
                FieldKind.Parcel => Cast(ctx, stored, type),
                _ => Cast(ctx, stored, type),
            };
        }

        private static BoundExpression DecodeObject(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var parcelVal = Cast(ctx, stored, ctx.Parcel);
            var inst = ConstructSerializable(ctx, type);
            var instLocal = NewLocal(ctx, locals, "ob", type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, instLocal, inst));
            var fromParcel = FindSynthMethod(type, ctx.Env, BilSpellings.FromParcelMethodName)
                ?? throw new CompilerInternalException("缺少 ..fromParcel");
            statements.Add(new BoundCallStatement(ctx.Syntax, fromParcel,
                new List<BoundExpression> { parcelVal },
                new BoundValueReferenceExpression(ctx.Syntax, instLocal, type)));
            return new BoundValueReferenceExpression(ctx.Syntax, instLocal, type);
        }

        private static BoundExpression ConstructSerializable(SynthContext ctx, SemanticSymbol type)
        {
            var tokenNew = new BoundNewExpression(ctx.Syntax, ctx.Token, ctx.TokenInit,
                Array.Empty<BoundExpression>());
            if (type is GenericParameterSymbol gp)
            {
                return new BoundDynamicNewExpression(ctx.Syntax, null, gp,
                    new List<BoundExpression> { tokenNew }, gp);
            }
            var ts = (TypeSymbol)type;
            var def = ts.ConstructedFrom ?? ts;
            var init = def.Methods.First(m => m.Name == BilSpellings.InitSerializableMethodName);
            return new BoundNewExpression(ctx.Syntax, ts, init,
                new List<BoundExpression> { tokenNew });
        }

        private static BoundExpression DecodeArray(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            SerializationFacts.IsArray(type, ctx.Env.Unit.Symbols, out var elem);
            var boxed = Cast(ctx, stored, ctx.ArrayAny);
            return RestoreArray(ctx, boxed, elem!, type, locals, statements);
        }

        private static BoundExpression DecodeList(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            SerializationFacts.IsList(type, ctx.Env.Unit.Symbols, out var elem);
            var boxed = Cast(ctx, stored, ctx.ArrayAny);
            var arr = RestoreArray(ctx, boxed, elem!,
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.ArrayDefinition, elem!),
                locals, statements);
            var listType = (TypeSymbol)type;
            var listDef = listType.ConstructedFrom ?? listType;
            var listInit = listDef.Methods.First(m =>
                m.Kind == MethodKind.Init && m.Parameters.Count == 0);
            var listLocal = NewLocal(ctx, locals, "li", listType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, listLocal,
                new BoundNewExpression(ctx.Syntax, listType, listInit,
                    Array.Empty<BoundExpression>())));
            var arrLocal = NewLocal(ctx, locals, "la", arr.Type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, arrLocal, arr));
            var nLocal = NewLocal(ctx, locals, "ln", ctx.Env.B.Int32);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, nLocal,
                new BoundFieldAccessExpression(ctx.Syntax,
                    new BoundValueReferenceExpression(ctx.Syntax, arrLocal, arr.Type),
                    ctx.ArrayLength, ctx.Env.B.Int32)));
            var iLocal = NewLocal(ctx, locals, "li2", ctx.Env.B.Int32);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, iLocal,
                IntLiteral(ctx, 0, IntType.I32, ctx.Env.B.Int32)));
            var loop = new BoundLoop(ctx.Syntax, LoopKind.While, null);
            loop.Condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpLt,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                new BoundValueReferenceExpression(ctx.Syntax, nLocal, ctx.Env.B.Int32),
                ctx.Env.B.Bool);
            var raw = new BoundIndexExpression(ctx.Syntax,
                new BoundValueReferenceExpression(ctx.Syntax, arrLocal, arr.Type),
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                ctx.ArrayGetAt,
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, elem!));
            var elemVal = Cast(ctx, raw, elem!);
            var body = new List<BoundStatement>
            {
                new BoundCallStatement(ctx.Syntax, ctx.ListAdd,
                    new List<BoundExpression> { elemVal },
                    new BoundValueReferenceExpression(ctx.Syntax, listLocal, listType)),
                new BoundAssignmentStatement(ctx.Syntax,
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                    new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                        new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                        IntLiteral(ctx, 1, IntType.I32, ctx.Env.B.Int32),
                        ctx.Env.B.Int32)),
            };
            loop.Body = new BoundBlock(ctx.Syntax, body);
            statements.Add(loop);
            return new BoundValueReferenceExpression(ctx.Syntax, listLocal, listType);
        }

        private static BoundExpression RestoreArray(SynthContext ctx, BoundExpression boxedAnyArr,
            SemanticSymbol elemType, SemanticSymbol resultArrayType, List<LocalSymbol> locals,
            List<BoundStatement> statements)
        {
            var srcLocal = NewLocal(ctx, locals, "ba", ctx.ArrayAny);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, srcLocal, boxedAnyArr));
            var nLocal = NewLocal(ctx, locals, "bn", ctx.Env.B.Int32);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, nLocal,
                new BoundFieldAccessExpression(ctx.Syntax,
                    new BoundValueReferenceExpression(ctx.Syntax, srcLocal, ctx.ArrayAny),
                    ctx.ArrayLength, ctx.Env.B.Int32)));
            var destType = resultArrayType as TypeSymbol
                ?? ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.ArrayDefinition, elemType);
            var destLocal = NewLocal(ctx, locals, "bd", destType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, destLocal,
                new BoundCallExpression(ctx.Syntax, ctx.ArrayOf,
                    new List<BoundExpression>
                    {
                        new BoundValueReferenceExpression(ctx.Syntax, nLocal, ctx.Env.B.Int32),
                    }, destType, new[] { elemType })));
            var iLocal = NewLocal(ctx, locals, "bi", ctx.Env.B.Int32);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, iLocal,
                IntLiteral(ctx, 0, IntType.I32, ctx.Env.B.Int32)));
            var loop = new BoundLoop(ctx.Syntax, LoopKind.While, null);
            loop.Condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpLt,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                new BoundValueReferenceExpression(ctx.Syntax, nLocal, ctx.Env.B.Int32),
                ctx.Env.B.Bool);
            var rawAny = new BoundIndexExpression(ctx.Syntax,
                new BoundValueReferenceExpression(ctx.Syntax, srcLocal, ctx.ArrayAny),
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                ctx.ArrayGetAt,
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
            var anyVal = Cast(ctx, rawAny, ctx.Env.B.Any);
            var body = new List<BoundStatement>();
            var decoded = DecodeBoxedAny(ctx, anyVal, elemType, locals, body);
            body.Add(new BoundAssignmentStatement(ctx.Syntax,
                new BoundIndexExpression(ctx.Syntax,
                    new BoundValueReferenceExpression(ctx.Syntax, destLocal, destType),
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                    ctx.ArraySetAt, elemType),
                decoded));
            body.Add(new BoundAssignmentStatement(ctx.Syntax,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                    IntLiteral(ctx, 1, IntType.I32, ctx.Env.B.Int32),
                    ctx.Env.B.Int32)));
            loop.Body = new BoundBlock(ctx.Syntax, body);
            statements.Add(loop);
            return new BoundValueReferenceExpression(ctx.Syntax, destLocal, destType);
        }

        private static BoundExpression DecodeBoxedAny(SynthContext ctx, BoundExpression anyVal,
            SemanticSymbol elemType, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var kind = Classify(ctx, elemType);
            BoundExpression stored = kind switch
            {
                FieldKind.Object or FieldKind.Map or FieldKind.Parcel =>
                    Cast(ctx, anyVal, ctx.Parcel),
                FieldKind.Array or FieldKind.List => Cast(ctx, anyVal, ctx.ArrayAny),
                _ => Cast(ctx, anyVal, elemType),
            };
            return DecodeValue(ctx, stored, elemType, locals, statements);
        }

        private static BoundExpression DecodeMap(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            SerializationFacts.IsMap(type, ctx.Env.Unit.Symbols, out _, out var valType);
            var nested = Cast(ctx, stored, ctx.Parcel);
            var nestedLocal = NewLocal(ctx, locals, "nm", ctx.Parcel);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, nestedLocal, nested));
            var mapType = (TypeSymbol)type;
            var mapDef = mapType.ConstructedFrom ?? mapType;
            var mapInit = mapDef.Methods.First(m =>
                m.Kind == MethodKind.Init && m.Parameters.Count == 0);
            var mapLocal = NewLocal(ctx, locals, "rm", mapType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, mapLocal,
                new BoundNewExpression(ctx.Syntax, mapType, mapInit,
                    Array.Empty<BoundExpression>())));
            var parcelRef = new BoundValueReferenceExpression(ctx.Syntax, nestedLocal, ctx.Parcel);
            var nLocal = NewLocal(ctx, locals, "pn", ctx.Env.B.Int64);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, nLocal,
                new BoundInstanceCallExpression(ctx.Syntax, parcelRef, ctx.ParcelCount,
                    Array.Empty<BoundExpression>(), ctx.Env.B.Int64)));
            var iLocal = NewLocal(ctx, locals, "pi", ctx.Env.B.Int64);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, iLocal,
                IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64)));
            var loop = new BoundLoop(ctx.Syntax, LoopKind.While, null);
            loop.Condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpLt,
                new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int64),
                new BoundValueReferenceExpression(ctx.Syntax, nLocal, ctx.Env.B.Int64),
                ctx.Env.B.Bool);
            var iRef = new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int64);
            var keyOpt = ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition,
                ctx.Env.B.String);
            var anyOpt = ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition,
                ctx.Env.B.Any);
            var rawKey = new BoundInstanceCallExpression(ctx.Syntax, parcelRef, ctx.ParcelKeyAt,
                new List<BoundExpression> { iRef }, keyOpt);
            var rawAny = new BoundInstanceCallExpression(ctx.Syntax, parcelRef, ctx.ParcelValueAt,
                new List<BoundExpression> { iRef }, anyOpt);
            var body = new List<BoundStatement>();
            var keyExpr = Cast(ctx, rawKey, ctx.Env.B.String);
            var anyVal = Cast(ctx, rawAny, ctx.Env.B.Any);
            var decoded = DecodeBoxedAny(ctx, anyVal, valType!, locals, body);
            body.Add(new BoundCallStatement(ctx.Syntax, ctx.MapSet,
                new List<BoundExpression> { keyExpr, decoded },
                new BoundValueReferenceExpression(ctx.Syntax, mapLocal, mapType)));
            body.Add(new BoundAssignmentStatement(ctx.Syntax, iRef,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add, iRef,
                    IntLiteral(ctx, 1, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Int64)));
            loop.Body = new BoundBlock(ctx.Syntax, body);
            statements.Add(loop);
            return new BoundValueReferenceExpression(ctx.Syntax, mapLocal, mapType);
        }

        private static LocalSymbol NewLocal(SynthContext ctx, List<LocalSymbol> locals,
            string hint, SemanticSymbol type)
        {
            var local = new LocalSymbol(hint + ctx.NextId++, type, isConst: false);
            locals.Add(local);
            return local;
        }

        private static BoundExpression Cast(SynthContext ctx, BoundExpression source,
            SemanticSymbol target)
        {
            return new BoundCastExpression(ctx.Syntax, source, target, isSafe: false, target);
        }

        private static BoundExpression IntLiteral(SynthContext ctx, decimal value, IntType intType,
            SemanticSymbol type)
        {
            var expr = new LiteralExpressionASTNode();
            expr.AttachLiteral(new IntLiteralASTNode(expr) { Value = value, IntType = intType });
            return new BoundLiteralExpression(expr, type);
        }

        private sealed class SynthContext
        {
            public BindEnvironment Env { get; }
            public ASTNode Syntax { get; }
            public TypeSymbol Host { get; }
            public TypeSymbol Serializable { get; }
            public TypeSymbol? SerializationBase { get; }
            public TypeSymbol? Temporary { get; }
            public TypeSymbol Parcel { get; }
            public TypeSymbol Iface { get; }
            public TypeSymbol Token { get; }
            public MethodSymbol ParcelInit { get; }
            public MethodSymbol TokenInit { get; }
            public MethodSymbol SetElement { get; }
            public MethodSymbol GetElement { get; }
            public MethodSymbol ArrayOf { get; }
            public MethodSymbol ArrayGetAt { get; }
            public MethodSymbol ArraySetAt { get; }
            public FieldSymbol ArrayLength { get; }
            public TypeSymbol ArrayAny { get; }
            public MethodSymbol ListAdd { get; }
            public MethodSymbol ListGetAt { get; }
            public FieldSymbol ListLength { get; }
            public MethodSymbol MapSet { get; }
            public FieldSymbol MapCount { get; }
            public MethodSymbol MapKeyAt { get; }
            public MethodSymbol MapValueAt { get; }
            public MethodSymbol ParcelCount { get; }
            public MethodSymbol ParcelKeyAt { get; }
            public MethodSymbol ParcelValueAt { get; }
            public MethodSymbol Iterate { get; }
            public MethodSymbol MoveNext { get; }
            public MethodSymbol Current { get; }
            public TypeSymbol PairDef { get; }
            public FieldSymbol PairKey { get; }
            public FieldSymbol PairValue { get; }
            public int NextId;

            public SynthContext(BindEnvironment env, ASTNode syntax, TypeSymbol host,
                TypeSymbol serializable, TypeSymbol? serializationBase, TypeSymbol? temporary,
                TypeSymbol parcel, TypeSymbol iface, TypeSymbol token)
            {
                Env = env;
                Syntax = syntax;
                Host = host;
                Serializable = serializable;
                SerializationBase = serializationBase;
                Temporary = temporary;
                Parcel = parcel;
                Iface = iface;
                Token = token;
                ParcelInit = parcel.Methods.First(m =>
                    m.Kind == MethodKind.Init && m.Parameters.Count == 1);
                TokenInit = token.Methods.First(m => m.Kind == MethodKind.Init);
                SetElement = parcel.Methods.First(m => m.Name == "setElement");
                GetElement = parcel.Methods.First(m => m.Name == "getElement");
                var collections = SerializationFacts.FindCollectionsNamespace(env.Unit.Symbols)
                    ?? throw new CompilerInternalException("缺少 core.collections");
                ArrayOf = collections.Methods.First(m => m.Name == "arrayOf");
                ArrayGetAt = env.B.ArrayDefinition.Methods.First(m => m.Name == "getAtIndex");
                ArraySetAt = env.B.ArrayDefinition.Methods.First(m => m.Name == "setAtIndex");
                ArrayLength = env.B.ArrayDefinition.Fields.First(f => f.Name == "length");
                ArrayAny = env.Unit.Symbols.GetConstructedType(env.B.ArrayDefinition, env.B.Any);
                var listDef = SerializationFacts.FindCollectionsType(env.Unit.Symbols, "List")
                    ?? throw new CompilerInternalException("缺少 List");
                ListAdd = listDef.Methods.First(m => m.Name == "add");
                ListGetAt = listDef.Methods.First(m => m.Name == "getAtIndex");
                ListLength = listDef.Fields.First(f => f.Name == "length");
                var mapDef = SerializationFacts.FindCollectionsType(env.Unit.Symbols, "Map")
                    ?? throw new CompilerInternalException("缺少 Map");
                MapSet = mapDef.Methods.First(m => m.Name == "set");
                MapCount = mapDef.Fields.First(f => f.Name == "count");
                MapKeyAt = mapDef.Methods.First(m => m.Name == "keyAtIndex");
                MapValueAt = mapDef.Methods.First(m => m.Name == "valueAtIndex");
                ParcelCount = parcel.Methods.First(m => m.Name == "elementCount");
                ParcelKeyAt = parcel.Methods.First(m => m.Name == "keyAtIndex");
                ParcelValueAt = parcel.Methods.First(m => m.Name == "valueAtIndex");
                var enumerable = collections.Types.First(t => t.Name == "IEnumerable");
                var enumerator = collections.Types.First(t => t.Name == "IEnumerator");
                Iterate = enumerable.Methods.First(m => m.Name == "iterate");
                MoveNext = enumerator.Methods.First(m => m.Name == "moveNext");
                Current = enumerator.Methods.First(m => m.Name == "current");
                PairDef = env.B.Core.Types.First(t => t.Name == "Pair" && t.GenericParameters.Count == 2);
                PairKey = PairDef.Fields.First(f => f.Name == "key");
                PairValue = PairDef.Fields.First(f => f.Name == "value");
            }
        }
    }
}
