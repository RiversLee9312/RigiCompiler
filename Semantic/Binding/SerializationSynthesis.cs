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
    internal static partial class SerializationSynthesis
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
                // 无载荷 enum 原有隐式零参 case 构造必须保留；加入 token
                // 构造后不能使它消失，也不能让验证器忽略不匹配的构造实参。
                if (type.Kind == TypeKind.EnumStruct && !type.Fields.Any(f => !f.IsStatic)
                    && !type.Methods.Any(m => m.Kind == MethodKind.Init))
                {
                    var defaultInit = new MethodSymbol("init", MethodKind.Init, owner: type)
                    { Accessibility = Accessibility.Public, HasBody = true, IsSynthetic = true };
                    type.Methods.Add(defaultInit);
                    env.SyntheticCellBodies.Add(new BoundFunctionBody(defaultInit,
                        Array.Empty<LocalSymbol>(), new BoundBlock(hostSyntax, Array.Empty<BoundStatement>())));
                }
                EnsureHostMethods(type, iface, token, parcel, hostSyntax);
            }
            foreach (var (type, hostSyntax) in hosts)
            {
                FillHostBodies(type, hostSyntax, env, serializable, serializationBase,
                    temporary, parcel, iface, token, hosts.Select(h => h.Type).ToList());
            }
            FillFromParcelFunction(env, ns, parcel, token, iface, syntax);
            FillDeepCopyFunction(env, ns, parcel, iface, syntax);
            FillAnyDecoder(env, ns, parcel, iface, token, syntax);
            // 块 4-2 B 面：格式层最小动态面（擦除 SB 视图）首版填充；
            // 绑定收尾 FinalizeDynamicDecoder 按终态快照重填。
            FillSbViewFunctions(env, ns, parcel, iface, token, syntax);
            // 块 5-1a：通用字段反射（typeNameOf/isSerializable/fieldsOf/
            // casesOf）首版填充；FinalizeDynamicDecoder 按终态快照重填。
            FillReflectionFunctions(env, ns, parcel, iface, token, syntax);
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
                ?? CreateIface(env, ns, parcel);
            token = ns.Types.FirstOrDefault(t => t.Name == BilSpellings.SerializableTokenName)
                ?? CreateToken(env, ns, syntax);
            return true;
        }

        private static TypeSymbol CreateIface(BindEnvironment env, NamespaceSymbol ns, TypeSymbol parcel)
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
            var flag = ns.Methods.First(m => m.Name == "deepCopy").Parameters[1];
            toParcel.Parameters.Add(flag);
            fromParcel.Parameters.Add(flag);
            iface.Methods.Add(toParcel);
            iface.Methods.Add(fromParcel);
            var runtime = ns.Types.First(t => t.Name == "SerializationGraphContext");
            foreach (var name in new[] { BilSpellings.EncodeGraphMethodName, BilSpellings.DecodeGraphMethodName })
            {
                var method = new MethodSymbol(name, MethodKind.Regular, owner: iface,
                    returnType: name == BilSpellings.EncodeGraphMethodName ? parcel : env.B.Any)
                { Accessibility = Accessibility.Public, IsAbstract = true, IsSynthetic = true };
                if (name == BilSpellings.DecodeGraphMethodName)
                    method.Parameters.Add(new ParameterSymbol("parcel", parcel));
                method.Parameters.Add(new ParameterSymbol("context", runtime));
                if (name == BilSpellings.DecodeGraphMethodName)
                    method.Parameters.Add(new ParameterSymbol("nodeId", env.B.Int64));
                iface.Methods.Add(method);
            }
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
                case ClassDeclarationASTNode or StructDeclarationASTNode or EnumStructDeclarationASTNode:
                    var type = env.Declarations.SymbolOf(node) as TypeSymbol
                        ?? throw new CompilerInternalException("P1 未登记类型符号");
                    if (type.Kind is TypeKind.Class or TypeKind.Struct or TypeKind.EnumStruct
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
            // 所有公开与私有递归槽从统一接口签名复制，避免泛型动态派发形状漂移。
            foreach (var slot in iface.Methods)
            {
                if (type.Methods.Any(m => m.Name == slot.Name)) continue;
                var implementation = new MethodSymbol(slot.Name, MethodKind.Regular,
                    owner: type, returnType: slot.ReturnType)
                { Accessibility = Accessibility.Public, HasBody = true, IsSynthetic = true, IsOverride = true };
                implementation.Parameters.AddRange(slot.Parameters);
                type.Methods.Add(implementation);
            }
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
            TypeSymbol parcel, TypeSymbol iface, TypeSymbol token,
            IReadOnlyList<TypeSymbol> serializableHosts)
        {
            if (env.SyntheticCellBodies.Any(b =>
                ReferenceEquals(b.Method.Owner, type)
                && b.Method.Name == BilSpellings.ToParcelMethodName))
            {
                return;
            }
            var ctx = new SynthContext(env, syntax, type, serializable, serializationBase,
                temporary, parcel, iface, token, serializableHosts);
            FillInitSerializable(ctx);
            if (serializationBase != null && SerializationFacts.HasWrapper(type, serializationBase))
                FillBaseCodec(ctx);
            else
            {
                FillToParcel(ctx);
                FillFromParcel(ctx);
            }
            FillPublicToParcel(ctx);
        }

        // Serializable 的 SB 分支只使用既有的基元/容器编码器，不扫描实现字段。
        private static void FillBaseCodec(SynthContext ctx)
        {
            var self = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            var encode = ctx.Host.Methods.First(m => m.Name == BilSpellings.EncodeGraphMethodName);
            ctx.Runtime = new BoundValueReferenceExpression(ctx.Syntax, encode.Parameters[0], ctx.RuntimeType);
            var locals = new List<LocalSymbol>();
            var work = new List<BoundStatement>();
            var receiver = new BoundThisExpression(ctx.Syntax, self);
            var payload = EncodeBody(ctx, receiver, self, locals, work);
            var record = NewRecord(ctx, self, locals, work);
            work.Add(SetMeta(ctx, record, "..value", payload, ctx.Env.B.Any));
            work.Add(new BoundReturnStatement(ctx.Syntax, record));
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(encode, locals, GuardSerializationFrame(ctx, work)));

            var decode = ctx.Host.Methods.First(m => m.Name == BilSpellings.DecodeGraphMethodName);
            ctx.Runtime = new BoundValueReferenceExpression(ctx.Syntax, decode.Parameters[1], ctx.RuntimeType);
            locals = new List<LocalSymbol>();
            work = new List<BoundStatement>();
            var parcel = new BoundValueReferenceExpression(ctx.Syntax, decode.Parameters[0], ctx.Parcel);
            var id = new BoundValueReferenceExpression(ctx.Syntax, decode.Parameters[2], ctx.Env.B.Int64);
            // 原始槽值（Nullable<Any>）进 DecodeBody：对象为 Parcel 形状
            // 核验（StrictWireCheck）先于 cast，null 同样先抛
            // SerializationException（native cast 对 null 先抛
            // CastException，双宿主分裂）。
            var value = new BoundInstanceCallExpression(ctx.Syntax, parcel, ctx.GetMetaElement,
                new List<BoundExpression>
                {
                    BindingDriver.MakeStringLiteral(ctx.Env, "..value"),
                },
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any),
                new[] { ctx.Env.B.Any });
            var graph = new List<BoundStatement>();
            ctx.PendingId = id;
            var decoded = DecodeBody(ctx, value, self, locals, graph);
            graph.Add(new BoundReturnStatement(ctx.Syntax, Cast(ctx, decoded, ctx.Env.B.Any)));
            ctx.PendingId = null;
            var tree = new List<BoundStatement>();
            decoded = DecodeBody(ctx, value, self, locals, tree);
            tree.Add(new BoundReturnStatement(ctx.Syntax, Cast(ctx, decoded, ctx.Env.B.Any)));
            work.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpGt, id,
                    IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Bool),
                new BoundBlock(ctx.Syntax, graph), new BoundBlock(ctx.Syntax, tree)));
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(decode, locals, GuardSerializationFrame(ctx, work)));
        }

        private static void FillInitSerializable(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m =>
                m.Name == BilSpellings.InitSerializableMethodName);
            var statements = new List<BoundStatement>();
            var self = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            // enum 没有零值：壳也必须先安装真实声明的 case，不豁免 DA 校验。
            foreach (var (field, type) in FieldClosureChecker.ClosureFieldsOf(ctx.Host, ctx.Env.Unit.Symbols))
                if (!field.IsStatic && type is TypeSymbol { Kind: TypeKind.EnumStruct } enumType
                    && (enumType.ConstructedFrom ?? enumType).Methods.Any(m => m.Name == BilSpellings.InitSerializableMethodName))
                    statements.Add(new BoundAssignmentStatement(ctx.Syntax,
                        new BoundFieldAccessExpression(ctx.Syntax, new BoundThisExpression(ctx.Syntax, self), field, type),
                        ConstructSerializable(ctx, enumType)));
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method,
                Array.Empty<LocalSymbol>(),
                new BoundBlock(ctx.Syntax, statements)));
        }

        private static void FillToParcel(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m => m.Name == BilSpellings.EncodeGraphMethodName);
            ctx.Runtime = new BoundValueReferenceExpression(ctx.Syntax, method.Parameters[0], ctx.RuntimeType);
            var locals = new List<LocalSymbol>();
            var statements = new List<BoundStatement>();
            var parcelLocal = new LocalSymbol("p", ctx.Parcel, isConst: false);
            locals.Add(parcelLocal);
            var typeName = WireTypeName(ctx, SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!);
            var parcelNew = new BoundNewExpression(ctx.Syntax, ctx.Parcel, ctx.ParcelInit,
                new List<BoundExpression> { typeName });
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, parcelLocal, parcelNew));
            var parcelRef = new BoundValueReferenceExpression(ctx.Syntax, parcelLocal, ctx.Parcel);
            var selfType = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            if (ctx.Host.Kind == TypeKind.EnumStruct)
            {
                // wire 只保存已声明 case 的名称，不把携带对象引用的 enum 值塞进 Parcel。
                BoundBlock branch = UnknownWireType(ctx, BindingDriver.MakeStringLiteral(ctx.Env, "无效 enum case"));
                foreach (var enumCase in ctx.Host.Cases.AsEnumerable().Reverse())
                {
                    var condition = new BoundTypeCheckExpression(ctx.Syntax, BoundTypeCheckKind.IsCase,
                        new BoundThisExpression(ctx.Syntax, selfType), null, null, ctx.Env.B.Bool, enumCase);
                    var write = SetMeta(ctx, parcelRef, "..case",
                        BindingDriver.MakeStringLiteral(ctx.Env, enumCase.Name), ctx.Env.B.String);
                    branch = new BoundBlock(ctx.Syntax, new BoundStatement[] {
                        new BoundIfStatement(ctx.Syntax, condition, new BoundBlock(ctx.Syntax, new[] { write }), branch) });
                }
                statements.AddRange(branch.Statements);
            }
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
                GuardSerializationFrame(ctx, statements)));
        }

        private static void FillFromParcel(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m => m.Name == BilSpellings.DecodeGraphMethodName);
            ctx.Runtime = new BoundValueReferenceExpression(ctx.Syntax, method.Parameters[1], ctx.RuntimeType);
            var locals = new List<LocalSymbol>();
            var statements = new List<BoundStatement>();
            var parcelParam = method.Parameters[0];
            var parcelRef = new BoundValueReferenceExpression(ctx.Syntax, parcelParam, ctx.Parcel);
            var selfType = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            BoundExpression receiver = new BoundThisExpression(ctx.Syntax, selfType);
            if (ctx.Host.Kind == TypeKind.Class && SerializableFields(ctx, forWrite: false).Any(f => f.Field.IsConst))
            {
                FillClassConstructorDecoder(ctx, method, selfType);
                return;
            }
            if (ctx.Host.Kind == TypeKind.Class)
            {
                var nodeId = new BoundValueReferenceExpression(ctx.Syntax, method.Parameters[2], ctx.Env.B.Int64);
                statements.Add(new BoundIfStatement(ctx.Syntax,
                    new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpGt, nodeId,
                        IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Bool),
                    new BoundBlock(ctx.Syntax, new[] { RuntimeStatement(ctx, "remember", nodeId,
                        Cast(ctx, receiver, ctx.Env.B.Object)) }), null));
            }
            if (ctx.Host.Kind == TypeKind.EnumStruct)
            {
                FillEnumFromParcel(ctx, method, parcelRef, selfType, locals, statements);
                return;
            }
            // 严格恢复（§4.6.3 / D3）：字段集合完全匹配先于逐字段解码。
            StrictFieldSetCheck(ctx, parcelRef, locals, statements);
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
                        receiver, field, fieldType!),
                    decoded));
            }
            // 值类型 this 是独立副本，返回填充后的值；类返回同一个已登记壳。
            statements.Add(new BoundReturnStatement(ctx.Syntax,
                Cast(ctx, receiver, ctx.Env.B.Any)));
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals,
                GuardSerializationFrame(ctx, statements)));
        }

        private static void FillEnumFromParcel(SynthContext ctx, MethodSymbol method,
            BoundExpression parcel, TypeSymbol self, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            // 严格恢复（§4.6.3 / D3）：枚举载荷字段集合完全匹配（..case
            // 在 meta 槽，不计入业务字段集合）。
            StrictFieldSetCheck(ctx, parcel, locals, statements);
            // const 载荷也必须恢复，但不能把普通解码方法变成写 readonly 字段的后门。
            // 全部载荷先解码为构造实参，字段只在私有合成 init 内赋值一次。
            var init = new MethodSymbol("..init.serializable.fields", MethodKind.Init, owner: ctx.Host)
            { Accessibility = Accessibility.Private, HasBody = true, IsSynthetic = true };
            init.Parameters.Add(new ParameterSymbol("token", ctx.Token));
            var assignments = new List<BoundStatement>();
            var arguments = new List<BoundExpression> {
                new BoundNewExpression(ctx.Syntax, ctx.Token, ctx.TokenInit, Array.Empty<BoundExpression>()) };
            foreach (var (field, type) in SerializableFields(ctx, forWrite: false))
            {
                var parameter = new ParameterSymbol("field" + init.Parameters.Count, type!);
                init.Parameters.Add(parameter);
                assignments.Add(new BoundAssignmentStatement(ctx.Syntax,
                    new BoundFieldAccessExpression(ctx.Syntax, new BoundThisExpression(ctx.Syntax, self), field, type!),
                    new BoundValueReferenceExpression(ctx.Syntax, parameter, type!)));
                // 原始槽值（Nullable<Any>）进 DecodeValue：null 由
                // StripSlotToAny 先于 cast 核验（native cast 对 null 先抛
                // CastException，双宿主分裂）。
                var got = new BoundInstanceCallExpression(ctx.Syntax, parcel, ctx.GetElement,
                    new List<BoundExpression>
                    {
                        BindingDriver.MakeStringLiteral(ctx.Env, field.Name),
                    },
                    ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any),
                    new[] { ctx.Env.B.Any });
                arguments.Add(Save(ctx, DecodeValue(ctx, got, type!, locals, statements), locals, statements, "enumField"));
            }
            // 无载荷 enum 直接复用 token 构造；不能再声明一个相同签名的 init。
            if (assignments.Count > 0)
            {
                ctx.Host.Methods.Add(init);
                ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(init,
                    Array.Empty<LocalSymbol>(), new BoundBlock(ctx.Syntax, assignments)));
            }
            var caseName = Save(ctx, GetMeta(ctx, parcel, "..case", ctx.Env.B.String), locals, statements, "enumCase");
            BoundBlock branch = UnknownWireType(ctx, caseName);
            foreach (var enumCase in ctx.Host.Cases.AsEnumerable().Reverse())
            {
                var created = new BoundEnumCaseExpression(ctx.Syntax, enumCase, arguments,
                    argumentsAreInitArguments: true, constructedType: self);
                var condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq,
                    caseName, BindingDriver.MakeStringLiteral(ctx.Env, enumCase.Name), ctx.Env.B.Bool);
                branch = new BoundBlock(ctx.Syntax, new BoundStatement[] {
                    new BoundIfStatement(ctx.Syntax, condition, new BoundBlock(ctx.Syntax, new BoundStatement[] {
                        new BoundReturnStatement(ctx.Syntax, Cast(ctx, created, ctx.Env.B.Any)) }), branch) });
            }
            statements.AddRange(branch.Statements);
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals, GuardSerializationFrame(ctx, statements)));
        }

        private static BoundBlock GuardSerializationFrame(SynthContext ctx,
            IReadOnlyList<BoundStatement> work)
        {
            return new BoundBlock(ctx.Syntax, new BoundStatement[]
            {
                RuntimeStatement(ctx, "enterFrame"),
                new BoundTryStatement(ctx.Syntax, new BoundBlock(ctx.Syntax, work),
                    System.Array.Empty<BoundCatchClause>(),
                    new BoundBlock(ctx.Syntax, new[] { RuntimeStatement(ctx, "leaveFrame") }),
                    null),
            });
        }

        private static void FillFromParcelFunction(BindEnvironment env, NamespaceSymbol ns,
            TypeSymbol parcel, TypeSymbol token, TypeSymbol iface, ASTNode syntax)
        {
            var method = ns.Methods.FirstOrDefault(m => m.Name == "fromParcel"
                && m.GenericParameters.Count == 1);
            if (method == null) return;
            if (env.SyntheticCellBodies.Any(b => ReferenceEquals(b.Method, method))) return;
            var ctx = PublicContext(env, syntax, iface, parcel, token);
            var graphLocals = new List<LocalSymbol>();
            var graphStatements = new List<BoundStatement>();
            StartRuntime(ctx, method.Parameters[1], graphLocals, graphStatements);
            var work = new List<BoundStatement>();
            var result = DecodeValue(ctx,
                new BoundValueReferenceExpression(syntax, method.Parameters[0], parcel),
                method.GenericParameters[0], graphLocals, work);
            work.Add(new BoundReturnStatement(syntax, result));
            FinishRuntime(ctx, graphStatements, work);
            env.SyntheticCellBodies.Add(new BoundFunctionBody(method, graphLocals, new BoundBlock(syntax, graphStatements)));
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
                        toParcel, new BoundExpression[] { new BoundValueReferenceExpression(syntax, method.Parameters[1], env.B.Bool) }, parcel)),
                new BoundReturnStatement(syntax,
                    new BoundCallExpression(syntax, fromParcel,
                        new List<BoundExpression>
                        {
                            new BoundValueReferenceExpression(syntax, pLocal, parcel),
                            new BoundValueReferenceExpression(syntax, method.Parameters[1], env.B.Bool),
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

        private static SynthContext PublicContext(BindEnvironment env, ASTNode syntax,
            TypeSymbol host, TypeSymbol parcel, TypeSymbol token)
        {
            var serializable = SerializationFacts.FindWrapper(env.Unit.Symbols, "Serializable")!;
            var hosts = new List<(TypeSymbol Type, ASTNode Syntax)>();
            CollectSerializableHosts(env, serializable, hosts);
            return new(env, syntax, host, serializable,
                SerializationFacts.FindWrapper(env.Unit.Symbols, "SerializationBase"),
                SerializationFacts.FindWrapper(env.Unit.Symbols, "Temporary"), parcel, host, token,
                hosts.Select(h => h.Type).ToArray());
        }

        private static BoundExpression RuntimeCall(SynthContext ctx, string name,
            params BoundExpression[] args)
        {
            var method = ctx.RuntimeType.Methods.First(m => m.Name == name);
            return new BoundInstanceCallExpression(ctx.Syntax, ctx.Runtime, method, args, method.ReturnType!);
        }

        private static BoundStatement RuntimeStatement(SynthContext ctx, string name,
            params BoundExpression[] args) => new BoundCallStatement(ctx.Syntax,
                ctx.RuntimeType.Methods.First(m => m.Name == name), args, ctx.Runtime);

        private static BoundExpression Enabled(SynthContext ctx) => new BoundFieldAccessExpression(
            ctx.Syntax, ctx.Runtime, ctx.RuntimeType.Fields.First(f => f.Name == "enabled"), ctx.Env.B.Bool);

        private static void StartRuntime(SynthContext ctx, ParameterSymbol flag,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var local = NewLocal(ctx, locals, "graph", ctx.RuntimeType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, local,
                new BoundNewExpression(ctx.Syntax, ctx.RuntimeType,
                    ctx.RuntimeType.Methods.First(m => m.Kind == MethodKind.Init),
                    new BoundExpression[] { new BoundValueReferenceExpression(ctx.Syntax, flag, ctx.Env.B.Bool) })));
            ctx.Runtime = new BoundValueReferenceExpression(ctx.Syntax, local, ctx.RuntimeType);
        }

        private static void FinishRuntime(SynthContext ctx, List<BoundStatement> statements,
            List<BoundStatement> work) => statements.Add(new BoundTryStatement(ctx.Syntax,
                new BoundBlock(ctx.Syntax, work), Array.Empty<BoundCatchClause>(),
                new BoundBlock(ctx.Syntax, new[] { RuntimeStatement(ctx, "dispose") }), null));

        private static void FillPublicToParcel(SynthContext ctx)
        {
            var method = ctx.Host.Methods.First(m => m.Name == BilSpellings.ToParcelMethodName);
            var locals = new List<LocalSymbol>();
            var statements = new List<BoundStatement>();
            StartRuntime(ctx, method.Parameters[0], locals, statements);
            var work = new List<BoundStatement>();
            var self = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            var result = EncodeValue(ctx, new BoundThisExpression(ctx.Syntax, self), self, locals, work,
                objectCodec: ctx.SerializationBase != null && SerializationFacts.HasWrapper(self, ctx.SerializationBase));
            work.Add(new BoundReturnStatement(ctx.Syntax, Cast(ctx, result, ctx.Parcel)));
            FinishRuntime(ctx, statements, work);
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals, new BoundBlock(ctx.Syntax, statements)));

            // 宿主的反序列化入口仍保留；公开泛型函数负责返回新壳。
            method = ctx.Host.Methods.First(m => m.Name == BilSpellings.FromParcelMethodName);
            locals = new List<LocalSymbol>();
            statements = new List<BoundStatement>();
            StartRuntime(ctx, method.Parameters[1], locals, statements);
            work = new List<BoundStatement>();
            var parcel = new BoundValueReferenceExpression(ctx.Syntax, method.Parameters[0], ctx.Parcel);
            var body = ctx.Host.Methods.First(m => m.Name == BilSpellings.DecodeGraphMethodName);
            var payload = NewLocal(ctx, locals, "payload", ctx.Parcel);
            work.Add(new BoundLocalDeclarationStatement(ctx.Syntax, payload, parcel));
            if (ctx.Host.Kind == TypeKind.Class)
            {
                var graph = new List<BoundStatement> {
                    RuntimeStatement(ctx, "remember", GetMeta(ctx, parcel, BilSpellings.GraphIdKey, ctx.Env.B.Int64),
                        Cast(ctx, new BoundThisExpression(ctx.Syntax, self), ctx.Env.B.Object)),
                    new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, payload),
                        Cast(ctx, GetMeta(ctx, parcel, BilSpellings.GraphPayloadKey, ctx.Env.B.Any), ctx.Parcel)) };
                work.Add(new BoundIfStatement(ctx.Syntax, Enabled(ctx), new BoundBlock(ctx.Syntax, graph), null));
            }
            Save(ctx, new BoundInstanceCallExpression(ctx.Syntax, new BoundThisExpression(ctx.Syntax, self), body,
                new BoundExpression[] { Ref(ctx, payload), ctx.Runtime,
                    IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64) }, ctx.Env.B.Any), locals, work, "filled");
            FinishRuntime(ctx, statements, work);
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals, new BoundBlock(ctx.Syntax, statements)));
        }

        private static FieldKind Classify(SynthContext ctx, SemanticSymbol type)
        {
            var symbols = ctx.Env.Unit.Symbols;
            if (ReferenceEquals(type, ctx.Env.B.Any)) return FieldKind.Object;
            if (SerializationFacts.IsArray(type, symbols, out _)) return FieldKind.Array;
            if (SerializationFacts.IsList(type, symbols, out _)) return FieldKind.List;
            if (SerializationFacts.IsMap(type, symbols, out _, out _)) return FieldKind.Map;
            if (SerializationFacts.IsParcel(type, symbols)) return FieldKind.Parcel;
            if (ctx.SerializationBase != null && SerializationFacts.HasWrapper(type, ctx.SerializationBase))
                return FieldKind.Scalar;
            if (type is GenericParameterSymbol gp)
            {
                // SB 容器元素即使在定义处未约束，也不能直接复制对象引用。
                // 实际值通过检查转换进入真实 Serializable 实现；不具备能力则拒绝。
                return FieldKind.Object;
            }
            if (SerializationFacts.HasWrapper(type, ctx.Serializable)) return FieldKind.Object;
            return FieldKind.Scalar;
        }

        private static SemanticSymbol StoredType(SynthContext ctx, SemanticSymbol fieldType)
        {
            // 编译器已在字段闭包验证可序列性；槽中的实际值仍保持旧树编码。
            // Any 仅用于容纳运行时 flag 选择的旧值或图 envelope。
            return ctx.Env.B.Any;
        }

        private static BoundExpression EncodeValue(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements, bool objectCodec = false)
        {
            if (NullableElement(ctx, type) is { } inner)
                return NullableValue(ctx, value, type, inner, locals, statements, encode: true);
            if (!objectCodec && Classify(ctx, type) is FieldKind.Scalar) return value;
            if (Classify(ctx, type) is FieldKind.Parcel) objectCodec = true;
            if (type is TypeSymbol { Kind: TypeKind.Struct or TypeKind.EnumStruct })
                return objectCodec ? EncodeObject(ctx, value, type) : EncodeBody(ctx, value, type, locals, statements);
            var source = Save(ctx, value, locals, statements, "source");
            var id = Save(ctx, RuntimeCall(ctx, "enter", Cast(ctx, source, ctx.Env.B.Any)), locals, statements, "id");
            var result = NewLocal(ctx, locals, "encoded", ctx.Env.B.Any);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, result, null));
            var resultRef = Ref(ctx, result);
            var full = new List<BoundStatement>();
            var payload = objectCodec ? EncodeObject(ctx, source, type) : EncodeBody(ctx, source, type, locals, full);
            var payloadRef = Save(ctx, Cast(ctx, payload, ctx.Env.B.Any), locals, full, "payload");
            full.Add(new BoundAssignmentStatement(ctx.Syntax, resultRef, payloadRef));
            var graph = new List<BoundStatement>();
            var node = NewRecord(ctx, type, locals, graph,
                Classify(ctx, type) == FieldKind.Object
                    ? new BoundFieldAccessExpression(ctx.Syntax, Cast(ctx, payloadRef, ctx.Parcel),
                        ctx.Parcel.Fields.First(field => field.Name == "typeName"), ctx.Env.B.String)
                    : null);
            graph.Add(SetMeta(ctx, node, BilSpellings.GraphIdKey,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Sub, IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), id, ctx.Env.B.Int64), ctx.Env.B.Int64));
            graph.Add(SetMeta(ctx, node, BilSpellings.GraphReferenceKey, IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Int64));
            graph.Add(SetMeta(ctx, node, BilSpellings.GraphPayloadKey, payloadRef, ctx.Env.B.Any));
            graph.Add(new BoundAssignmentStatement(ctx.Syntax, resultRef, Cast(ctx, node, ctx.Env.B.Any)));
            full.Add(new BoundIfStatement(ctx.Syntax, RuntimeCall(ctx, "isNewNode", id), new BoundBlock(ctx.Syntax, graph), null));
            full.Add(RuntimeStatement(ctx, "leave", id));
            var repeated = new List<BoundStatement>();
            var reference = NewRecord(ctx, type, locals, repeated);
            repeated.Add(SetMeta(ctx, reference, BilSpellings.GraphReferenceKey, id, ctx.Env.B.Int64));
            repeated.Add(new BoundAssignmentStatement(ctx.Syntax, resultRef, Cast(ctx, reference, ctx.Env.B.Any)));
            statements.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpGt, id, IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Bool),
                new BoundBlock(ctx.Syntax, repeated), new BoundBlock(ctx.Syntax, full)));
            return resultRef;
        }

        private static BoundExpression EncodeBody(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            return Classify(ctx, type) switch
            {
                FieldKind.Array => EncodeArray(ctx, value, type, locals, statements),
                FieldKind.List => EncodeList(ctx, value, type, locals, statements),
                FieldKind.Map => EncodeMap(ctx, value, type, locals, statements),
                FieldKind.Object => EncodeObject(ctx, value, type),
                FieldKind.Parcel => EncodeParcel(ctx, value, locals, statements),
                _ => value,
            };
        }

        private static BoundExpression EncodeObject(SynthContext ctx, BoundExpression value,
            SemanticSymbol type)
        {
            if (ReferenceEquals(type, ctx.Env.B.Any)
                || (type is GenericParameterSymbol && !SerializationFacts.HasSerializableConstraint(type, ctx.Serializable)))
            {
                // SB 容器的开放元素必须通过运行期检查，不能因容器有能力便假定元素有能力。
                var slot = ctx.Iface.Methods.First(m => m.Name == BilSpellings.EncodeGraphMethodName);
                return new BoundInstanceCallExpression(ctx.Syntax, Cast(ctx, value, ctx.Iface), slot,
                    new[] { ctx.Runtime }, ctx.Parcel);
            }
            var method = FindSynthMethod(type, ctx.Env, BilSpellings.EncodeGraphMethodName)
                ?? throw new CompilerInternalException("缺少 ..toParcel");
            return new BoundInstanceCallExpression(ctx.Syntax, value, method,
                new[] { ctx.Runtime }, ctx.Parcel);
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
                elemRead = raw;
            }
            else
            {
                var raw = new BoundIndexExpression(ctx.Syntax, srcRef,
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, indexType),
                    ctx.ArrayGetAt,
                    ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, elemType));
                elemRead = raw;
            }
            var encoded = EncodeOptionalElement(ctx, elemRead, elemType, locals, body);
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

        // 所有 Map（含 String 键，§4.6.3/D3）统一为有序键值条目序列
        // （MapEntries 交错数组），保留键值真实类型，不借业务键作字段名、
        // 不调用键的 toString。树模式逐边独立复制，图模式经 envelope 保留别名。
        private static BoundExpression EncodeMap(SynthContext ctx, BoundExpression value,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            return MapEntries(ctx, value, type, locals, statements, encode: true);
        }

        private static BoundExpression DecodeValue(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements, bool checkNullMarker = true)
        {
            if (checkNullMarker && (type is GenericParameterSymbol || NullableElement(ctx, type) != null))
                return DecodeOptionalElement(ctx, stored, type, locals, statements);
            if (NullableElement(ctx, type) is { } inner)
                return NullableValue(ctx, stored, type, inner, locals, statements, encode: false);
            // getElement 通道的槽值静态类型是 Nullable<Any>；先核验 null
            // 再剥离到 Any（StripSlotToAny），严禁在此 eager cast——native
            // 的 cast 对 null 立即抛 CastException，会先于严格核验逃逸
            // （VM cast 宽松不暴露该分歧，对拍实证：strict_fromparcel
            // 负例 7 text=null 仅 native 崩）。
            stored = StripSlotToAny(ctx, stored, type, locals, statements);
            if (Classify(ctx, type) is FieldKind.Scalar)
            {
                // 严格恢复（§4.6.3 / D3）：核验先于 cast，禁止整数宽度
                // 转换、浮点截断与 String/bool 数值互转。
                StrictWireCheck(ctx, stored, type, locals, statements);
                return Cast(ctx, stored, type);
            }
            if (type is TypeSymbol { Kind: TypeKind.Struct or TypeKind.EnumStruct })
                return DecodeBody(ctx, stored, type, locals, statements);
            var source = Save(ctx, stored, locals, statements, "wire");
            if (type is TypeSymbol && Classify(ctx, type) == FieldKind.Object)
            {
                statements.Add(RuntimeStatement(ctx, "validateParcelMode",
                    Cast(ctx, source, ctx.Parcel),
                    BindingDriver.MakeStringLiteral(ctx.Env,
                        BilSpellings.GraphReferenceKey)));
            }
            var result = NewLocal(ctx, locals, "decoded", type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, result, null));
            var resultRef = Ref(ctx, result);
            var graph = new List<BoundStatement>();
            var record = Save(ctx, Cast(ctx, source, ctx.Parcel), locals, graph, "record");
            var reference = Save(ctx, RuntimeCall(ctx, "referenceId",
                GetMeta(ctx, record, BilSpellings.GraphReferenceKey, ctx.Env.B.Int64)), locals, graph, "refid");
            var reused = new BoundBlock(ctx.Syntax, new BoundStatement[] {
                new BoundAssignmentStatement(ctx.Syntax, resultRef, Cast(ctx, RuntimeCall(ctx, "resolve", reference), type)) });
            var full = new List<BoundStatement>();
            var id = Save(ctx, GetMeta(ctx, record, BilSpellings.GraphIdKey, ctx.Env.B.Int64), locals, full, "nodeid");
            var oldId = ctx.PendingId;
            ctx.PendingId = id;
            var payload = GetMeta(ctx, record, BilSpellings.GraphPayloadKey, ctx.Env.B.Any);
            var decoded = Classify(ctx, type) == FieldKind.Parcel
                ? DecodeObject(ctx, payload, type, locals, full) : DecodeBody(ctx, payload, type, locals, full);
            ctx.PendingId = oldId;
            full.Add(new BoundAssignmentStatement(ctx.Syntax, resultRef, decoded));
            graph.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpGt, reference, IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Bool),
                reused, new BoundBlock(ctx.Syntax, full)));
            var tree = new List<BoundStatement>();
            ctx.PendingId = null;
            decoded = Classify(ctx, type) == FieldKind.Parcel
                ? DecodeObject(ctx, source, type, locals, tree) : DecodeBody(ctx, source, type, locals, tree);
            ctx.PendingId = oldId;
            tree.Add(new BoundAssignmentStatement(ctx.Syntax, resultRef, decoded));
            var graphMode = type is GenericParameterSymbol
                ? RuntimeCall(ctx, "isGraphParcel", Cast(ctx, source, ctx.Parcel),
                    BindingDriver.MakeStringLiteral(ctx.Env,
                        BilSpellings.GraphReferenceKey))
                : Enabled(ctx);
            statements.Add(new BoundIfStatement(ctx.Syntax, graphMode,
                new BoundBlock(ctx.Syntax, graph), new BoundBlock(ctx.Syntax, tree)));
            return resultRef;
        }

        private static BoundExpression DecodeBody(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            // 严格恢复（§4.6.3 / D3）：集合/对象载荷的形状核验先于解码。
            // 集合（Array/List/Map）wire 载荷统一为 Array<Any>（Map 为有序
            // 键值条目序列）；List 不能当 Array、活 Map 不是合法 wire 载荷。
            // 对象载荷必为 Parcel 记录（SB 值为 typeName+..value 记录）。
            switch (Classify(ctx, type))
            {
                case FieldKind.Array:
                    StrictWireCheck(ctx, stored, ctx.ArrayAny, locals, statements);
                    return DecodeArray(ctx, stored, type, locals, statements);
                case FieldKind.List:
                    StrictWireCheck(ctx, stored, ctx.ArrayAny, locals, statements);
                    return DecodeList(ctx, stored, type, locals, statements);
                case FieldKind.Map:
                    StrictWireCheck(ctx, stored, ctx.ArrayAny, locals, statements);
                    return DecodeMap(ctx, stored, type, locals, statements);
                case FieldKind.Object:
                    return DecodeObject(ctx, stored, type, locals, statements);
                case FieldKind.Parcel:
                    return DecodeParcelRecord(ctx, stored, locals, statements);
                default:
                    // 标量（含 SB 基元宿主 ..value 载荷）：核验先于 cast。
                    StrictWireCheck(ctx, stored, type, locals, statements);
                    return Cast(ctx, stored, type);
            }
        }

        private static BoundExpression DecodeObject(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            // 严格恢复（§4.6.3 / D3）：对象槽载荷必为 Parcel 记录（用户
            // 对象为业务字段记录；SB 值为 typeName+..value 记录）。
            StrictWireCheck(ctx, stored, ctx.Parcel, locals, statements);
            if (ReferenceEquals(type, ctx.Env.B.Any))
                return new BoundCallExpression(ctx.Syntax,
                    SerializationFacts.FindSerializationNamespace(ctx.Env.Unit.Symbols)!.Methods.First(m => m.Name == "decodeAnyValue"),
                    new BoundExpression[] { Cast(ctx, stored, ctx.Parcel), ctx.Runtime,
                        ctx.PendingId ?? IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64) }, ctx.Env.B.Any);
            var parcelLocal = NewLocal(ctx, locals, "objectParcel", ctx.Parcel);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, parcelLocal,
                Cast(ctx, stored, ctx.Parcel)));
            var parcelVal = Ref(ctx, parcelLocal);

            // 开放 T 按 wire 的真实闭合类型重建，再检查转换到 T；不能构造 Nullable<T> 占位壳。
            if (type is GenericParameterSymbol)
                return Cast(ctx, DecodeObject(ctx, parcelVal, ctx.Env.B.Any, locals, statements), type);

            var result = NewLocal(ctx, locals, "decodedObject", type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, result, null));
            var typeName = Save(ctx, new BoundFieldAccessExpression(ctx.Syntax, parcelVal,
                ctx.Parcel.Fields.First(field => field.Name == "typeName"), ctx.Env.B.String),
                locals, statements, "wireType");
            BoundBlock otherwise = UnknownWireType(ctx, typeName);
            var candidates = ctx.SerializableHosts
                .Where(candidate => !candidate.IsAbstract
                    && SymbolLookup.IsAssignable(candidate, type, ctx.Env))
                .OrderByDescending(candidate => InheritanceDepth(candidate))
                .ToList();
            foreach (var candidate in candidates.AsEnumerable().Reverse())
            {
                var branch = new List<BoundStatement>();
                var decoded = DecodeKnownObject(ctx, parcelVal, candidate, type, locals, branch);
                branch.Add(new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, result), decoded));
                var expectedName = WireTypeName(ctx, candidate);
                var condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq,
                    typeName, expectedName,
                    ctx.Env.B.Bool);
                otherwise = new BoundBlock(ctx.Syntax, new BoundStatement[]
                {
                    new BoundIfStatement(ctx.Syntax, condition,
                        new BoundBlock(ctx.Syntax, branch), otherwise),
                });
            }
            statements.AddRange(otherwise.Statements);
            return Ref(ctx, result);
        }

        private static BoundExpression DecodeKnownObject(SynthContext ctx,
            BoundExpression parcelVal, SemanticSymbol concreteType, SemanticSymbol resultType,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var instLocal = NewLocal(ctx, locals, "ob", concreteType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, instLocal,
                ConstructSerializable(ctx, concreteType)));
            var fromParcel = FindSynthMethod(concreteType, ctx.Env,
                BilSpellings.DecodeGraphMethodName)
                ?? ctx.Iface.Methods.First(m => m.Name == BilSpellings.DecodeGraphMethodName);
            var receiver = ReferenceEquals(fromParcel.Owner, ctx.Iface)
                ? Cast(ctx, Ref(ctx, instLocal), ctx.Iface) : Ref(ctx, instLocal);
            var filled = new BoundInstanceCallExpression(ctx.Syntax, receiver,
                fromParcel, new List<BoundExpression> { parcelVal, ctx.Runtime,
                    ctx.PendingId ?? IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64) },
                ctx.Env.B.Any);
            return Cast(ctx, filled, resultType);
        }

        private static BoundBlock UnknownWireType(SynthContext ctx, BoundExpression typeName)
        {
            var exception = ctx.Env.B.Core.Types.First(t => t.Name == "IllegalStateException");
            var init = exception.Methods.First(m => m.Kind == MethodKind.Init
                && m.Parameters.Count == 1
                && ReferenceEquals(m.Parameters[0].Type, ctx.Env.B.String));
            var prefix = BindingDriver.MakeStringLiteral(ctx.Env,
                "序列化 wire 类型未登记：");
            var message = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add, prefix,
                typeName, ctx.Env.B.String);
            return new BoundBlock(ctx.Syntax, new BoundStatement[]
            {
                new BoundThrowStatement(ctx.Syntax, new BoundNewExpression(ctx.Syntax,
                    exception, init, new BoundExpression[] { message })),
            });
        }

        // ---- 严格恢复契约（§4.6.3 / D3，块 4-3）----

        // 类型/字段集合不匹配的统一异常：core.serialization.
        // SerializationException（stdlib 源码声明）；符号缺失的编译环境
        // （如无 stdlib 的单元合成）退回 IllegalStateException。
        private static TypeSymbol StrictExceptionType(SynthContext ctx) =>
            SerializationFacts.FindSerializationNamespace(ctx.Env.Unit.Symbols)?.Types
                .FirstOrDefault(t => t.Name == "SerializationException")
            ?? ctx.Env.B.Core.Types.First(t => t.Name == "IllegalStateException");

        private static BoundBlock ThrowStrict(SynthContext ctx, BoundExpression message)
        {
            var exception = StrictExceptionType(ctx);
            var init = exception.Methods.First(m => m.Kind == MethodKind.Init
                && m.Parameters.Count == 1
                && ReferenceEquals(m.Parameters[0].Type, ctx.Env.B.String));
            return new BoundBlock(ctx.Syntax, new BoundStatement[]
            {
                new BoundThrowStatement(ctx.Syntax, new BoundNewExpression(ctx.Syntax,
                    exception, init, new BoundExpression[] { message })),
            });
        }

        // 消息：前缀 + 声明类型名（typeOf<T>().toString() 运行时求值）。
        private static BoundExpression StrictMessage(SynthContext ctx, string prefix,
            SemanticSymbol expected) => new BoundBinaryExpression(ctx.Syntax,
                BilIntrinsicOp.Add, BindingDriver.MakeStringLiteral(ctx.Env, prefix),
                WireTypeName(ctx, expected), ctx.Env.B.String);

        // cast 不是类型检查（rigi_try_cast 带数值宽展/浮点截断，块 4-2
        // 实证）——一切标量/容器/对象解码的 cast 之前必须先做名义核验。
        // 两宿主 `is` 对数值均为精确判定（VM TypesAssignable 名义链、
        // native rigi_type_is sheet 链），宽展只存在于 cast 路径；可空槽
        // 直接 `is` 底层运行时类型（DecodeOptionalElement 同先例）。
        // null 单独报错：null 只可用于可空声明。核验进入可空槽域后进行，
        // 两个分支都抛 SerializationException，双宿主错误形态一致。
        private static void StrictWireCheck(SynthContext ctx, BoundExpression value,
            SemanticSymbol expected, List<LocalSymbol> locals,
            List<BoundStatement> statements)
        {
            var nullableAny = ctx.Env.Unit.Symbols.GetConstructedType(
                ctx.Env.B.NullableDefinition, ctx.Env.B.Any);
            var slot = Save(ctx, Cast(ctx, value, nullableAny), locals, statements,
                "strictSlot");
            statements.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq, slot,
                    Null(ctx, nullableAny), ctx.Env.B.Bool),
                ThrowStrict(ctx, StrictMessage(ctx,
                    "严格恢复：null 不能恢复为非可空声明 ", expected)), null));
            var isCheck = new BoundTypeCheckExpression(ctx.Syntax,
                BoundTypeCheckKind.Is, slot, expected, null, ctx.Env.B.Bool);
            statements.Add(new BoundIfStatement(ctx.Syntax, isCheck,
                new BoundBlock(ctx.Syntax, Array.Empty<BoundStatement>()),
                ThrowStrict(ctx, StrictMessage(ctx,
                    "严格恢复：wire 值类型与声明不符 ", expected))));
        }

        // 槽值（Nullable<Any> 静态类型）剥离到 Any 的严格前置：null 核验
        // 必须先于一切 cast。native 的 cast 路径对 null 立即抛
        // CastException（rigi_try_cast 无 null 宽限），VM cast 宽松会
        // 把 null 原样放过——若先剥再查，native 的异常先于
        // SerializationException 逃逸，双宿主对拍即裂（块 4-3
        // strict_fromparcel 负例 7 实证）。已 Any 化的输入说明上游
        // 已剥离（本合成内所有解码入口都先经此核验），原样透传。
        private static BoundExpression StripSlotToAny(SynthContext ctx, BoundExpression stored,
            SemanticSymbol declared, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            if (ReferenceEquals(stored.Type, ctx.Env.B.Any))
            {
                return stored;
            }
            var nullableAny = ctx.Env.Unit.Symbols.GetConstructedType(
                ctx.Env.B.NullableDefinition, ctx.Env.B.Any);
            var slot = ReferenceEquals(stored.Type, nullableAny)
                ? stored
                : Save(ctx, Cast(ctx, stored, nullableAny), locals, statements, "slot");
            statements.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq, slot,
                    Null(ctx, nullableAny), ctx.Env.B.Bool),
                ThrowStrict(ctx, StrictMessage(ctx,
                    "严格恢复：null 不能恢复为非可空声明 ", declared)), null));
            return Cast(ctx, slot, ctx.Env.B.Any);
        }

        // 字段集合完全匹配（§4.6.3 / D3）：业务字段集合（meta 槽不计入，
        // elementCount/contains 只看业务表）必须与应序列化字段集合完全
        // 一致——计数相等 + 逐字段存在；缺失、多余、可空字段缺项一律抛
        // SerializationException，不能用字段初始值填补缺失项。expected
        // 取编码侧写字段集（forWrite:false，含 const 载荷；解码侧或经
        // ..init.serializable.fields 全量读取，或按非 const 读取但计数
        // 核验覆盖 const 的存在性）。
        private static void StrictFieldSetCheck(SynthContext ctx, BoundExpression parcel,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var expected = SerializableFields(ctx, forWrite: false).ToList();
            var self = SymbolLookup.AsSelfConstructed(ctx.Host, ctx.Env.Unit.Symbols)!;
            var count = Save(ctx, new BoundInstanceCallExpression(ctx.Syntax, parcel,
                ctx.ElementCount, new List<BoundExpression>(), ctx.Env.B.Int64),
                locals, statements, "wireFieldCount");
            statements.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpNe, count,
                    IntLiteral(ctx, expected.Count, IntType.I64, ctx.Env.B.Int64),
                    ctx.Env.B.Bool),
                ThrowStrict(ctx, StrictMessage(ctx,
                    "严格恢复：业务字段集合与应序列化字段集合不一致 ", self)), null));
            foreach (var (field, _) in expected)
            {
                var head = BindingDriver.MakeStringLiteral(ctx.Env,
                    "严格恢复：缺失业务字段 '" + field.Name + "'（类型 ");
                var message = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                    new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add, head,
                        WireTypeName(ctx, self), ctx.Env.B.String),
                    BindingDriver.MakeStringLiteral(ctx.Env, "）"), ctx.Env.B.String);
                var containsCall = new BoundInstanceCallExpression(ctx.Syntax, parcel,
                    ctx.ContainsElement,
                    new List<BoundExpression> {
                        BindingDriver.MakeStringLiteral(ctx.Env, field.Name) },
                    ctx.Env.B.Bool);
                statements.Add(new BoundIfStatement(ctx.Syntax, containsCall,
                    new BoundBlock(ctx.Syntax, Array.Empty<BoundStatement>()),
                    ThrowStrict(ctx, message)));
            }
        }

        private static int InheritanceDepth(TypeSymbol type)
        {
            var depth = 0;
            var seen = new HashSet<TypeSymbol>();
            for (var current = type; current != null && seen.Add(current);
                current = current.BaseType)
            {
                depth++;
            }
            return depth;
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
            if (def.Kind == TypeKind.EnumStruct && def.Cases.Count > 0)
                return new BoundEnumCaseExpression(ctx.Syntax, def.Cases[0],
                    new BoundExpression[] { tokenNew }, argumentsAreInitArguments: true, constructedType: ts);
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
            // List 必须在解码任何元素之前登记，不能先恢复临时 Array。
            var arr = Cast(ctx, stored, ctx.ArrayAny);
            var listType = (TypeSymbol)type;
            var listDef = listType.ConstructedFrom ?? listType;
            var listInit = listDef.Methods.First(m =>
                m.Kind == MethodKind.Init && m.Parameters.Count == 0);
            var listLocal = NewLocal(ctx, locals, "li", listType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, listLocal,
                new BoundNewExpression(ctx.Syntax, listType, listInit,
                    Array.Empty<BoundExpression>())));
            Remember(ctx, Ref(ctx, listLocal), statements);
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
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
            var body = new List<BoundStatement>();
            var elemVal = DecodeValue(ctx, raw, elem!, locals, body);
            body.Add(new BoundCallStatement(ctx.Syntax, ctx.ListAdd,
                    new List<BoundExpression> { elemVal },
                    new BoundValueReferenceExpression(ctx.Syntax, listLocal, listType)));
            body.Add(new BoundAssignmentStatement(ctx.Syntax,
                    new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                    new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add,
                        new BoundValueReferenceExpression(ctx.Syntax, iLocal, ctx.Env.B.Int32),
                        IntLiteral(ctx, 1, IntType.I32, ctx.Env.B.Int32),
                        ctx.Env.B.Int32)));
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
            Remember(ctx, Ref(ctx, destLocal), statements);
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
            var body = new List<BoundStatement>();
            // 原始槽值（Nullable<Any>）直接进 DecodeValue：null 由
            // StripSlotToAny 先于 cast 核验，严禁此处 eager cast
            // （native 对 null 先抛 CastException，双宿主分裂）。
            var decoded = DecodeBoxedAny(ctx, rawAny, elemType, locals, body);
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
            return DecodeValue(ctx, anyVal, elemType, locals, statements);
        }

        // 所有 Map 统一按有序键值条目序列恢复（与 EncodeMap/MapEntries 对称，
        // §4.6.3/D3）；不再识别 String 键直摊平为 Parcel 字段的旧特例。
        private static BoundExpression DecodeMap(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            return MapEntries(ctx, stored, type, locals, statements, encode: false);
        }

        private static LocalSymbol NewLocal(SynthContext ctx, List<LocalSymbol> locals,
            string hint, SemanticSymbol type)
        {
            var local = new LocalSymbol(hint + ctx.NextId++, type, isConst: false);
            locals.Add(local);
            return local;
        }

        // 所有 Map 的有序键值条目序列（§4.6.3/D3，树/图模式同布局）：
        // 交错键/值数组，保留键值真实类型，不将用户键借作字段名、不调
        // toString。树模式逐边独立复制；图模式键/值作为节点参与引用表，
        // 同一对象作键、值及普通字段时仍能恢复别名。
        private static BoundExpression MapEntries(SynthContext ctx, BoundExpression input,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements, bool encode)
        {
            SerializationFacts.IsMap(type, ctx.Env.Unit.Symbols, out var keyType, out var valueType);
            var source = Save(ctx, encode ? input : Cast(ctx, input, ctx.ArrayAny), locals, statements, "entries");
            BoundExpression length = encode
                ? Cast(ctx, new BoundFieldAccessExpression(ctx.Syntax, source, ctx.MapCount, ctx.Env.B.Int64), ctx.Env.B.Int32)
                : new BoundFieldAccessExpression(ctx.Syntax, source, ctx.ArrayLength, ctx.Env.B.Int32);
            if (encode) length = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Mul, length,
                IntLiteral(ctx, 2, IntType.I32, ctx.Env.B.Int32), ctx.Env.B.Int32);
            var count = Save(ctx, length, locals, statements, "entrycount");
            BoundExpression target;
            if (encode)
                target = new BoundCallExpression(ctx.Syntax, ctx.ArrayOf, new[] { count }, ctx.ArrayAny, new[] { ctx.Env.B.Any });
            else
            {
                var map = (TypeSymbol)type;
                target = new BoundNewExpression(ctx.Syntax, map,
                    (map.ConstructedFrom ?? map).Methods.First(m => m.Kind == MethodKind.Init && m.Parameters.Count == 0),
                    Array.Empty<BoundExpression>());
            }
            target = Save(ctx, target, locals, statements, "entrytarget");
            if (!encode) Remember(ctx, target, statements);
            var index = NewLocal(ctx, locals, "entryindex", ctx.Env.B.Int32);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, index, IntLiteral(ctx, 0, IntType.I32, ctx.Env.B.Int32)));
            var at = Ref(ctx, index);
            var next = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add, at, IntLiteral(ctx, 1, IntType.I32, ctx.Env.B.Int32), ctx.Env.B.Int32);
            var body = new List<BoundStatement>();
            var values = new List<BoundExpression>();
            foreach (var (itemType, slot, accessor) in new[] {
                (keyType!, (BoundExpression)at, ctx.MapKeyAt), (valueType!, (BoundExpression)next, ctx.MapValueAt) })
            {
                BoundExpression raw;
                if (encode)
                {
                    var mapIndex = Cast(ctx, new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Div, at,
                        IntLiteral(ctx, 2, IntType.I32, ctx.Env.B.Int32), ctx.Env.B.Int32), ctx.Env.B.Int64);
                    raw = new BoundInstanceCallExpression(ctx.Syntax, source, accessor, new[] { mapIndex },
                        ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, itemType));
                    var encoded = EncodeOptionalElement(ctx, raw, itemType, locals, body);
                    body.Add(new BoundAssignmentStatement(ctx.Syntax,
                        new BoundIndexExpression(ctx.Syntax, target, slot, ctx.ArraySetAt, ctx.Env.B.Any), Cast(ctx, encoded, ctx.Env.B.Any)));
                }
                else
                {
                    raw = new BoundIndexExpression(ctx.Syntax, source, slot, ctx.ArrayGetAt,
                        ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
                    values.Add(Save(ctx, DecodeValue(ctx, raw, itemType, locals, body), locals, body, "entryvalue"));
                }
            }
            if (!encode)
            {
                if (ctx.PendingId != null)
                {
                    // 图中的键可能指向尚未填完字段的祖先壳；此时调用 set
                    // 会按临时 toString 合并不同键。按原序填标准库存储，
                    // 完整图恢复后集合仍使用原来的键比较规则。
                    var map = (TypeSymbol)type;
                    var definition = map.ConstructedFrom ?? map;
                    foreach (var (name, value) in new[] { ("ks", values[0]), ("vs", values[1]) })
                    {
                        var field = definition.Fields.First(f => f.Name == name);
                        var fieldType = SymbolLookup.SubstituteFieldType(field, map, ctx.Env.Unit.Symbols);
                        body.Add(new BoundCallStatement(ctx.Syntax, ctx.ListAdd, new[] { value },
                            new BoundFieldAccessExpression(ctx.Syntax, target, field, fieldType)));
                    }
                }
                else body.Add(new BoundCallStatement(ctx.Syntax, ctx.MapSet, values, target));
            }
            body.Add(new BoundAssignmentStatement(ctx.Syntax, at,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.Add, at, IntLiteral(ctx, 2, IntType.I32, ctx.Env.B.Int32), ctx.Env.B.Int32)));
            statements.Add(new BoundLoop(ctx.Syntax, LoopKind.While, null) {
                Condition = new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpLt, at, count, ctx.Env.B.Bool),
                Body = new BoundBlock(ctx.Syntax, body) });
            return target;
        }

        private static BoundExpression Ref(SynthContext ctx, LocalSymbol local) =>
            new BoundValueReferenceExpression(ctx.Syntax, local, local.Type!);

        private static BoundExpression Save(SynthContext ctx, BoundExpression value,
            List<LocalSymbol> locals, List<BoundStatement> statements, string name)
        {
            var local = NewLocal(ctx, locals, name, value.Type);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, local, value));
            return Ref(ctx, local);
        }

        private static BoundExpression NewRecord(SynthContext ctx, SemanticSymbol type,
            List<LocalSymbol> locals, List<BoundStatement> statements, BoundExpression? typeName = null) => Save(ctx,
                new BoundNewExpression(ctx.Syntax, ctx.Parcel, ctx.ParcelInit,
                    new[] { typeName ?? WireTypeName(ctx, type) }),
                locals, statements, "record");

        private static BoundStatement Set(SynthContext ctx, BoundExpression parcel,
            string key, BoundExpression value, SemanticSymbol type) => new BoundCallStatement(ctx.Syntax,
                ctx.SetElement, new[] { BindingDriver.MakeStringLiteral(ctx.Env, key), value }, parcel, new[] { type });

        private static BoundExpression Get(SynthContext ctx, BoundExpression parcel,
            string key, SemanticSymbol type) => Cast(ctx, new BoundInstanceCallExpression(ctx.Syntax,
                parcel, ctx.GetElement, new[] { BindingDriver.MakeStringLiteral(ctx.Env, key) },
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, type), new[] { type }), type);

        // 元数据通道写入/读取：保留键（..value/..case/..id/..ref/..data 及
        // BilSpellings 图键）只作用于 Parcel 受控元数据槽（§4.6.3）。
        private static BoundStatement SetMeta(SynthContext ctx, BoundExpression parcel,
            string key, BoundExpression value, SemanticSymbol type) => new BoundCallStatement(ctx.Syntax,
                ctx.SetMetaElement, new[] { BindingDriver.MakeStringLiteral(ctx.Env, key), value }, parcel, new[] { type });

        private static BoundExpression GetMeta(SynthContext ctx, BoundExpression parcel,
            string key, SemanticSymbol type) => Cast(ctx, new BoundInstanceCallExpression(ctx.Syntax,
                parcel, ctx.GetMetaElement, new[] { BindingDriver.MakeStringLiteral(ctx.Env, key) },
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, type), new[] { type }), type);

        private static void Remember(SynthContext ctx, BoundExpression shell, List<BoundStatement> statements)
        {
            if (ctx.PendingId is { } id)
                statements.Add(RuntimeStatement(ctx, "remember", id, Cast(ctx, shell, ctx.Env.B.Object)));
        }

        private static SemanticSymbol? NullableElement(SynthContext ctx, SemanticSymbol type) =>
            type is TypeSymbol { ConstructedFrom: { } definition, TypeArguments: { Count: 1 } args }
                && ReferenceEquals(definition, ctx.Env.B.NullableDefinition) ? args[0] : null;

        private static BoundExpression Null(SynthContext ctx, SemanticSymbol type)
        {
            var expr = new LiteralExpressionASTNode();
            expr.AttachLiteral(new NullLiteralASTNode(expr));
            var nullable = NullableElement(ctx, type) != null ? type
                : ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, type);
            return new BoundLiteralExpression(expr, nullable);
        }

        private static BoundExpression NullableValue(SynthContext ctx, BoundExpression source,
            SemanticSymbol type, SemanticSymbol inner, List<LocalSymbol> locals,
            List<BoundStatement> statements, bool encode)
        {
            if (NullableElement(ctx, source.Type) == null)
                source = Cast(ctx, source, ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any));
            var saved = Save(ctx, source, locals, statements, "nullable");
            var resultType = encode
                ? ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any) : type;
            var result = NewLocal(ctx, locals, "optional", resultType);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, result, Null(ctx, resultType)));
            var body = new List<BoundStatement>();
            var value = encode ? EncodeValue(ctx, Cast(ctx, saved, inner), inner, locals, body)
                : DecodeValue(ctx, saved, inner, locals, body);
            body.Add(new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, result), Cast(ctx, value, resultType)));
            statements.Add(new BoundIfStatement(ctx.Syntax,
                new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpNe, saved, Null(ctx, saved.Type), ctx.Env.B.Bool),
                new BoundBlock(ctx.Syntax, body), null));
            return Ref(ctx, result);
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
            public IReadOnlyList<TypeSymbol> SerializableHosts { get; }
            public MethodSymbol ParcelInit { get; }
            public MethodSymbol TokenInit { get; }
            public MethodSymbol SetElement { get; }
            public MethodSymbol GetElement { get; }
            // 业务字段存在性/计数查询（§4.6.1 / D3）：严格恢复字段集合
            // 完全匹配的判定面；只反映业务字段表，meta 槽不计入。
            public MethodSymbol ContainsElement { get; }
            public MethodSymbol ElementCount { get; }
            // 受控元数据通道（§4.6.3 元数据隔离）：..value/..case/..id/
            // ..ref/..data 只进 Parcel 的 meta 槽，不进业务字段表。
            public MethodSymbol SetMetaElement { get; }
            public MethodSymbol GetMetaElement { get; }
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
            public MethodSymbol Iterate { get; }
            public MethodSymbol MoveNext { get; }
            public MethodSymbol Current { get; }
            public TypeSymbol PairDef { get; }
            public FieldSymbol PairKey { get; }
            public FieldSymbol PairValue { get; }
            public int NextId;
            public TypeSymbol RuntimeType { get; }
            public BoundExpression Runtime { get; set; } = null!;
            public BoundExpression? PendingId { get; set; }

            public SynthContext(BindEnvironment env, ASTNode syntax, TypeSymbol host,
                TypeSymbol serializable, TypeSymbol? serializationBase, TypeSymbol? temporary,
                TypeSymbol parcel, TypeSymbol iface, TypeSymbol token,
                IReadOnlyList<TypeSymbol> serializableHosts)
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
                SerializableHosts = serializableHosts;
                RuntimeType = SerializationFacts.FindSerializationNamespace(env.Unit.Symbols)!.Types
                    .First(t => t.Name == "SerializationGraphContext");
                ParcelInit = parcel.Methods.First(m =>
                    m.Kind == MethodKind.Init && m.Parameters.Count == 1);
                TokenInit = token.Methods.First(m => m.Kind == MethodKind.Init);
                SetElement = parcel.Methods.First(m => m.Name == "setElement");
                GetElement = parcel.Methods.First(m => m.Name == "getElement");
                ContainsElement = parcel.Methods.First(m => m.Name == "contains");
                ElementCount = parcel.Methods.First(m => m.Name == "elementCount");
                SetMetaElement = parcel.Methods.First(m => m.Name == "setMetaElement");
                GetMetaElement = parcel.Methods.First(m => m.Name == "getMetaElement");
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
