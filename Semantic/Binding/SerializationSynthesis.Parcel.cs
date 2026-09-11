namespace RigiCompiler
{
    internal static partial class SerializationSynthesis
    {
        internal static void FinalizeDynamicDecoder(BindEnvironment env)
        {
            var ns = SerializationFacts.FindSerializationNamespace(env.Unit.Symbols);
            var parcel = SerializationFacts.FindParcel(env.Unit.Symbols);
            var iface = ns?.Types.FirstOrDefault(t => t.Name == Bil.BilSpellings.SerializableIfaceName);
            var token = ns?.Types.FirstOrDefault(t => t.Name == Bil.BilSpellings.SerializableTokenName);
            if (ns == null || parcel == null || iface == null || token == null) return;
            env.Unit.Symbols.MaterializeGenericUseTypes(message => env.Error(null, message));
            var method = ns.Methods.First(m => m.Name == "decodeAnyValue");
            env.SyntheticCellBodies.RemoveAll(b => ReferenceEquals(b.Method, method));
            FillAnyDecoder(env, ns, parcel, iface, token, FallbackSyntax(env));
        }

        // Parcel 作为待编码对象时，与普通字段共用同一图上下文。
        // 新建的记录只是输出容器，不递归编码输出记录本身。
        private static BoundExpression EncodeParcel(SynthContext ctx, BoundExpression value,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var source = Save(ctx, value, locals, statements, "parcelSource");
            var name = ctx.Parcel.Fields.First(f => f.Name == "typeName");
            var data = ctx.Parcel.Fields.First(f => f.Name == "data");
            var record = NewRecord(ctx, ctx.Parcel, locals, statements);
            statements.Add(Set(ctx, record, "typeName", new BoundFieldAccessExpression(ctx.Syntax,
                source, name, ctx.Env.B.String), ctx.Env.B.String));
            var contents = EncodeValue(ctx, new BoundFieldAccessExpression(ctx.Syntax,
                source, data, data.FieldType!), data.FieldType!, locals, statements);
            statements.Add(Set(ctx, record, "data", contents, ctx.Env.B.Any));
            return record;
        }

        private static BoundExpression DecodeParcelRecord(SynthContext ctx, BoundExpression stored,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var record = Save(ctx, Cast(ctx, stored, ctx.Parcel), locals, statements, "parcelRecord");
            var result = Save(ctx, new BoundNewExpression(ctx.Syntax, ctx.Parcel, ctx.ParcelInit,
                new[] { Get(ctx, record, "typeName", ctx.Env.B.String) }), locals, statements, "parcelResult");
            Remember(ctx, result, statements);
            var data = ctx.Parcel.Fields.First(f => f.Name == "data");
            var contents = DecodeValue(ctx, Get(ctx, record, "data", ctx.Env.B.Any), data.FieldType!, locals, statements);
            statements.Add(new BoundAssignmentStatement(ctx.Syntax,
                new BoundFieldAccessExpression(ctx.Syntax, result, data, data.FieldType!), contents));
            return result;
        }

        private static void FillAnyDecoder(BindEnvironment env, NamespaceSymbol ns, TypeSymbol parcel,
            TypeSymbol iface, TypeSymbol token, ASTNode syntax)
        {
            var method = ns.Methods.First(m => m.Name == "decodeAnyValue");
            var ctx = PublicContext(env, syntax, iface, parcel, token);
            ctx.Runtime = new BoundValueReferenceExpression(syntax, method.Parameters[1], ctx.RuntimeType);
            ctx.PendingId = new BoundValueReferenceExpression(syntax, method.Parameters[2], env.B.Int64);
            var record = new BoundValueReferenceExpression(syntax, method.Parameters[0], parcel);
            var locals = new List<LocalSymbol>();
            var statements = new List<BoundStatement>();
            var name = Save(ctx, new BoundFieldAccessExpression(syntax, record,
                parcel.Fields.First(f => f.Name == "typeName"), env.B.String), locals, statements, "dynamicType");
            BoundBlock choices = UnknownWireType(ctx, name);
            var candidates = ctx.SerializableHosts.Concat(env.Unit.Symbols.ConstructedTypeSnapshot())
                .Where(t => !t.IsAbstract && IsClosedWireType(t) && SerializationFacts.HasWrapper(t, ctx.Serializable))
                .Distinct().ToArray();
            foreach (var type in candidates.Reverse())
            {
                var body = new List<BoundStatement>();
                var value = ctx.SerializationBase != null && SerializationFacts.HasWrapper(type, ctx.SerializationBase)
                    ? DecodeRegisteredBase(ctx, record, type, locals, body)
                    : DecodeKnownObject(ctx, record, type, env.B.Any, locals, body);
                body.Add(new BoundReturnStatement(syntax, Cast(ctx, value, env.B.Any)));
                choices = new BoundBlock(syntax, new BoundStatement[] { new BoundIfStatement(syntax,
                    new BoundBinaryExpression(syntax, BilIntrinsicOp.CmpEq, name,
                        WireTypeName(ctx, type), env.B.Bool), new BoundBlock(syntax, body), choices) });
            }
            statements.AddRange(choices.Statements);
            env.SyntheticCellBodies.Add(new BoundFunctionBody(method, locals, new BoundBlock(syntax, statements)));
        }

        private static bool IsClosedWireType(SemanticSymbol type) => type is TypeSymbol t
            && (t.ConstructedFrom == null ? t.GenericParameters.Count == 0
                : t.TypeArguments!.All(IsClosedWireType));

        private static BoundExpression DecodeRegisteredBase(SynthContext ctx, BoundExpression record, TypeSymbol type,
            List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            if (Classify(ctx, type) == FieldKind.Scalar) return Cast(ctx, Get(ctx, record, "..value", ctx.Env.B.Any), type);
            if (!SerializationFacts.IsArray(type, ctx.Env.Unit.Symbols, out var element))
                return DecodeKnownObject(ctx, record, type, ctx.Env.B.Any, locals, statements);
            // Array 的分配参数是长度，不能把序列化 token 当作数组长度。
            // 通过普通 arrayOf 建立真实具化接收者，由同一数组 codec 按载荷长度重建。
            var receiver = new BoundCallExpression(ctx.Syntax, ctx.ArrayOf,
                new[] { IntLiteral(ctx, 0, IntType.I32, ctx.Env.B.Int32) }, type, new[] { element! });
            return new BoundInstanceCallExpression(ctx.Syntax, receiver,
                FindSynthMethod(type, ctx.Env, Bil.BilSpellings.DecodeGraphMethodName)!,
                new[] { record, ctx.Runtime, ctx.PendingId! }, ctx.Env.B.Any);
        }

        private static void FillClassConstructorDecoder(SynthContext ctx, MethodSymbol method, TypeSymbol self)
        {
            var init = new MethodSymbol(Bil.BilSpellings.InitDeserializeMethodName, MethodKind.Init, owner: ctx.Host)
                { Accessibility = Accessibility.Private, HasBody = true, IsSynthetic = true };
            init.Parameters.Add(new ParameterSymbol("parcel", ctx.Parcel));
            init.Parameters.Add(new ParameterSymbol("context", ctx.RuntimeType));
            init.Parameters.Add(new ParameterSymbol("nodeId", ctx.Env.B.Int64));
            ctx.Host.Methods.Add(init);
            var outerRuntime = ctx.Runtime;
            ctx.Runtime = new BoundValueReferenceExpression(ctx.Syntax, init.Parameters[1], ctx.RuntimeType);
            var parcel = new BoundValueReferenceExpression(ctx.Syntax, init.Parameters[0], ctx.Parcel);
            var id = new BoundValueReferenceExpression(ctx.Syntax, init.Parameters[2], ctx.Env.B.Int64);
            var receiver = new BoundThisExpression(ctx.Syntax, self);
            var locals = new List<LocalSymbol>();
            var work = new List<BoundStatement> {
                new BoundIfStatement(ctx.Syntax, new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpGt,
                    id, IntLiteral(ctx, 0, IntType.I64, ctx.Env.B.Int64), ctx.Env.B.Bool),
                    new BoundBlock(ctx.Syntax, new[] { RuntimeStatement(ctx, "remember", id,
                        Cast(ctx, receiver, ctx.Env.B.Object)) }), null) };
            // 只读字段只在真正的 init 中初始化，不给普通解码方法增加写权限。
            foreach (var (field, type) in SerializableFields(ctx, forWrite: false))
            {
                var raw = new BoundInstanceCallExpression(ctx.Syntax, parcel, ctx.GetElement,
                    new[] { BindingDriver.MakeStringLiteral(ctx.Env, field.Name) },
                    ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.NullableDefinition, ctx.Env.B.Any), new[] { ctx.Env.B.Any });
                var value = DecodeValue(ctx, raw, type!, locals, work);
                work.Add(new BoundAssignmentStatement(ctx.Syntax,
                    new BoundFieldAccessExpression(ctx.Syntax, receiver, field, type!), value));
            }
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(init, locals, new BoundBlock(ctx.Syntax, work)));
            ctx.Runtime = outerRuntime;
            var args = method.Parameters.Select(p => (BoundExpression)new BoundValueReferenceExpression(ctx.Syntax, p, p.Type!)).ToArray();
            ctx.Env.SyntheticCellBodies.Add(new BoundFunctionBody(method, Array.Empty<LocalSymbol>(),
                GuardSerializationFrame(ctx, new[] { new BoundReturnStatement(ctx.Syntax,
                    Cast(ctx, new BoundNewExpression(ctx.Syntax, self, init, args), ctx.Env.B.Any)) })));
        }

        private static BoundExpression WireTypeName(SynthContext ctx, SemanticSymbol type)
        {
            var typeValue = new BoundTypeOfExpression(ctx.Syntax, null, type,
                ctx.Env.Unit.Symbols.GetConstructedType(ctx.Env.B.TypeDefinition, type));
            return new BoundInstanceCallExpression(ctx.Syntax, Cast(ctx, typeValue, ctx.Env.B.Any),
                ctx.Env.B.Any.Methods.First(m => m.Name == "toString"), Array.Empty<BoundExpression>(), ctx.Env.B.String);
        }

        private static TypeSymbol NullMarkerType(SynthContext ctx) => ctx.Parcel.NestedTypes.First(t => t.Name == "NullSentinel");

        private static BoundExpression EncodeOptionalElement(SynthContext ctx, BoundExpression raw,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var source = Save(ctx, raw, locals, statements, "optionalElement");
            var result = NewLocal(ctx, locals, "encodedElement", ctx.Env.B.Any);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, result, null));
            var empty = new List<BoundStatement>();
            var marker = NewRecord(ctx, NullMarkerType(ctx), locals, empty);
            empty.Add(new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, result), Cast(ctx, marker, ctx.Env.B.Any)));
            var present = new List<BoundStatement>();
            var elementType = NullableElement(ctx, type) ?? type;
            var encoded = EncodeValue(ctx, Cast(ctx, source, elementType), elementType, locals, present);
            present.Add(new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, result), Cast(ctx, encoded, ctx.Env.B.Any)));
            statements.Add(new BoundIfStatement(ctx.Syntax, new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq,
                source, Null(ctx, source.Type), ctx.Env.B.Bool), new BoundBlock(ctx.Syntax, empty), new BoundBlock(ctx.Syntax, present)));
            return Ref(ctx, result);
        }

        private static BoundExpression DecodeOptionalElement(SynthContext ctx, BoundExpression stored,
            SemanticSymbol type, List<LocalSymbol> locals, List<BoundStatement> statements)
        {
            var source = Save(ctx, stored, locals, statements, "optionalWire");
            var result = NewLocal(ctx, locals, "decodedElement", type);
            var marker = NewLocal(ctx, locals, "isNullMarker", ctx.Env.B.Bool);
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, result, null));
            var falseSyntax = new LiteralExpressionASTNode();
            falseSyntax.AttachLiteral(new BoolLiteralASTNode(falseSyntax) { Value = false });
            statements.Add(new BoundLocalDeclarationStatement(ctx.Syntax, marker, new BoundLiteralExpression(falseSyntax, ctx.Env.B.Bool)));
            var record = Cast(ctx, source, ctx.Parcel);
            statements.Add(new BoundIfStatement(ctx.Syntax, new BoundTypeCheckExpression(ctx.Syntax,
                BoundTypeCheckKind.Is, source, ctx.Parcel, null, ctx.Env.B.Bool),
                new BoundBlock(ctx.Syntax, new[] { new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, marker),
                    new BoundBinaryExpression(ctx.Syntax, BilIntrinsicOp.CmpEq,
                        new BoundFieldAccessExpression(ctx.Syntax, record, ctx.Parcel.Fields.First(f => f.Name == "typeName"), ctx.Env.B.String),
                        WireTypeName(ctx, NullMarkerType(ctx)), ctx.Env.B.Bool)) }), null));
            var present = new List<BoundStatement>();
            var value = DecodeValue(ctx, source, type, locals, present, checkNullMarker: false);
            present.Add(new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, result), value));
            statements.Add(new BoundIfStatement(ctx.Syntax, Ref(ctx, marker), new BoundBlock(ctx.Syntax,
                new[] { new BoundAssignmentStatement(ctx.Syntax, Ref(ctx, result), Cast(ctx, Null(ctx, type), type)) }),
                new BoundBlock(ctx.Syntax, present)));
            return Ref(ctx, result);
        }
    }
}
