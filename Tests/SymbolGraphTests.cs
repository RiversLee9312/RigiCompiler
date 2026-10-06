namespace RigiCompiler.Tests
{
    /// <summary>
    /// S1 符号图内核测试（M37）：构造泛型驻留（同一引用）、bootstrap 层级、
    /// rich/shared 标记、共享安全推导、泛型约束、基元 intrinsic 键空间。
    /// 符号比较一律引用相等（ReferenceEquals），禁止按名字字符串比较身份。
    /// </summary>
    public static class SymbolGraphTests
    {


        internal static TestSuiteData Spec { get; } = new("SymbolGraph",
        [
            (nameof(TestConstructedTypesAndInterfaces), TestConstructedTypesAndInterfaces),
            (nameof(TestBootstrapHierarchy), TestBootstrapHierarchy),
            (nameof(TestBranches), TestBranches),
            (nameof(TestSharedSafety), TestSharedSafety),
            (nameof(TestGenericConstraints), TestGenericConstraints),
            (nameof(TestIntrinsicOps), TestIntrinsicOps),
            (nameof(TestFreeze), TestFreeze),
        ], sectionTitle: "SymbolGraph");

        private static void TestConstructedTypesAndInterfaces()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            // ===== 构造泛型驻留（§4.2：同一 (定义, 实参列表) 必得同一实例）=====
            var nullableI32a = graph.GetNullable(b.Int32);
            var nullableI32b = graph.GetConstructedType(b.NullableDefinition, b.Int32);
            CaseAssertions.CheckTrue("T? 与 Nullable\\<T> 同一实例",
                ReferenceEquals(nullableI32a, nullableI32b));
            CaseAssertions.CheckTrue("重复构造同一引用",
                ReferenceEquals(nullableI32a, graph.GetNullable(b.Int32)));
            CaseAssertions.CheckTrue("不同实参不同实例",
                !ReferenceEquals(nullableI32a, graph.GetNullable(b.Int64)));

            CaseAssertions.CheckTrue("可空实参不重复包装",
                ReferenceEquals(nullableI32a, graph.GetNullable(nullableI32a)));
            CaseAssertions.CheckTrue("泛型索引返回代入保持单层可空",
                ReferenceEquals(nullableI32a, graph.Substitute(
                    b.ArrayDefinition.Methods.Single(m => m.Name == "getAtIndex").ReturnType,
                    b.ArrayDefinition, graph.GetConstructedType(b.ArrayDefinition, nullableI32a))));
            // 构造类型属性传播
            CaseAssertions.CheckTrue("构造类型 ConstructedFrom",
                ReferenceEquals(nullableI32a.ConstructedFrom, b.NullableDefinition));
            CaseAssertions.CheckTrue("构造类型实参",
                nullableI32a.TypeArguments!.Count == 1
                && ReferenceEquals(nullableI32a.TypeArguments[0], b.Int32));
            CaseAssertions.CheckTrue("构造类型基类随定义（Object 分支）",
                ReferenceEquals(nullableI32a.BaseType, b.Object));
            CaseAssertions.CheckTrue("构造类型非 ValueType 分支", !nullableI32a.IsValueTypeBranch);

            // 接口赋值必须走传递闭包，且不放宽无关接口或反向赋值。
            var ia = new TypeSymbol("IA", TypeKind.Interface, b.Core);
            var ib = new TypeSymbol("IB", TypeKind.Interface, b.Core);
            ib.Interfaces.Add(ia);
            var impl = new TypeSymbol("Impl", TypeKind.Class, b.Core, baseType: b.Object);
            impl.Interfaces.Add(ib);
            CaseAssertions.CheckTrue("传递接口可赋值", SymbolLookup.IsAssignable(impl, ia, graph));
            CaseAssertions.CheckTrue("无关接口仍不可赋值", !SymbolLookup.IsAssignable(b.Object, ia, graph));
            CaseAssertions.CheckTrue("接口不得反向赋实现类", !SymbolLookup.IsAssignable(ia, impl, graph));
        }

        private static void TestBootstrapHierarchy()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            var nullableI32a = graph.GetNullable(b.Int32);

            // ===== bootstrap 层级（SYNTAX §3.1）=====
            CaseAssertions.CheckTrue("Any 无基类", b.Any.BaseType == null);
            CaseAssertions.CheckTrue("Object <: Any", ReferenceEquals(b.Object.BaseType, b.Any));
            CaseAssertions.CheckTrue("ValueType <: Any", ReferenceEquals(b.ValueType.BaseType, b.Any));
            CaseAssertions.CheckTrue("i32 <: ValueType", ReferenceEquals(b.Int32.BaseType, b.ValueType));
            CaseAssertions.CheckTrue("Enum <: ValueType", ReferenceEquals(b.Enum.BaseType, b.ValueType));
            CaseAssertions.CheckTrue("Wrapper <: ValueType", ReferenceEquals(b.Wrapper.BaseType, b.ValueType));
            CaseAssertions.CheckTrue("String <: ValueType", ReferenceEquals(b.String.BaseType, b.ValueType));
            CaseAssertions.CheckTrue("Nullable\\<T> <: Object", ReferenceEquals(b.NullableDefinition.BaseType, b.Object));
            CaseAssertions.CheckTrue("Box\\<T> <: Object（内建事实）", ReferenceEquals(b.BoxDefinition.BaseType, b.Object));
            CaseAssertions.CheckTrue("Type\\<T> <: ValueType", ReferenceEquals(b.TypeDefinition.BaseType, b.ValueType));
            CaseAssertions.CheckTrue("Span\\<T> <: Object", ReferenceEquals(b.SpanDefinition.BaseType, b.Object));
            CaseAssertions.CheckTrue("SharedSpan\\<T> <: Object", ReferenceEquals(b.SharedSpanDefinition.BaseType, b.Object));
            CaseAssertions.CheckTrue("Span 是 class", b.SpanDefinition.Kind == TypeKind.Class);
            CaseAssertions.CheckTrue("SharedSpan 是 shared class",
                b.SharedSpanDefinition.Kind == TypeKind.Class && b.SharedSpanDefinition.IsShared);
        }

        private static void TestBranches()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            var nullableI32a = graph.GetNullable(b.Int32);

            // ===== 分支与 rich/shared 标记 =====
            CaseAssertions.CheckTrue("i32 在 ValueType 分支", b.Int32.IsValueTypeBranch);
            CaseAssertions.CheckTrue("String 在 ValueType 分支", b.String.IsValueTypeBranch);
            CaseAssertions.CheckTrue("Object 不在 ValueType 分支", !b.Object.IsValueTypeBranch);
            CaseAssertions.CheckTrue("Any 不在 ValueType 分支", !b.Any.IsValueTypeBranch);
            CaseAssertions.CheckTrue("Wrapper 默认非 rich", !b.Wrapper.IsRich);
            CaseAssertions.CheckTrue("String 非 rich", !b.String.IsRich);
            CaseAssertions.CheckTrue("i32 非 rich", !b.Int32.IsRich);
        }

        private static void TestSharedSafety()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            var nullableI32a = graph.GetNullable(b.Int32);

            // ===== 共享安全推导（SYNTAX §3.1.1 白名单）=====
            var localClass = new TypeSymbol("LocalUser", TypeKind.Class, b.Core, baseType: b.Object);
            var sharedClass = new TypeSymbol("SharedUser", TypeKind.Class, b.Core, baseType: b.Object, isShared: true);
            var sharedRichStruct = new TypeSymbol("SharedEntry", TypeKind.Struct, b.Core,
                baseType: b.ValueType, isRich: true, isShared: true);
            CaseAssertions.CheckTrue("非 rich ValueType 共享安全（i32）", b.Int32.IsSharedSafe());
            CaseAssertions.CheckTrue("String 共享安全", b.String.IsSharedSafe());
            CaseAssertions.CheckTrue("local class 不共享安全", !localClass.IsSharedSafe());
            CaseAssertions.CheckTrue("shared class 共享安全", sharedClass.IsSharedSafe());
            CaseAssertions.CheckTrue("shared rich struct 共享安全", sharedRichStruct.IsSharedSafe());
            CaseAssertions.CheckTrue("Nullable\\<i32> 按 T 推导共享安全", nullableI32a.IsSharedSafe());
            CaseAssertions.CheckTrue("Nullable\\<local class> 不共享安全",
                !graph.GetNullable(localClass).IsSharedSafe());
        }

        private static void TestGenericConstraints()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            var nullableI32a = graph.GetNullable(b.Int32);

            // ===== 泛型约束（extends ValueType）=====
            CaseAssertions.CheckTrue("Span\\<T extends ValueType\\>",
                b.SpanDefinition.GenericParameters.Count == 1
                && b.SpanDefinition.GenericParameters[0].Constraints.Count == 1
                && b.SpanDefinition.GenericParameters[0].Constraints[0].Kind == GenericConstraintKind.Extends
                && ReferenceEquals(b.SpanDefinition.GenericParameters[0].Constraints[0].Bound, b.ValueType));
            CaseAssertions.CheckTrue("SharedSpan\\<T extends ValueType\\>",
                b.SharedSpanDefinition.GenericParameters.Count == 1
                && b.SharedSpanDefinition.GenericParameters[0].Constraints.Count == 1
                && b.SharedSpanDefinition.GenericParameters[0].Constraints[0].Kind == GenericConstraintKind.Extends
                && ReferenceEquals(b.SharedSpanDefinition.GenericParameters[0].Constraints[0].Bound, b.ValueType));
            CaseAssertions.CheckTrue("Box\\<T extends ValueType\\>",
                b.BoxDefinition.GenericParameters.Count == 1
                && b.BoxDefinition.GenericParameters[0].Constraints.Count == 1
                && b.BoxDefinition.GenericParameters[0].Constraints[0].Kind == GenericConstraintKind.Extends
                && ReferenceEquals(b.BoxDefinition.GenericParameters[0].Constraints[0].Bound, b.ValueType));
            CaseAssertions.CheckTrue("Nullable\\<T\\> 无约束",
                b.NullableDefinition.GenericParameters.Count == 1
                && b.NullableDefinition.GenericParameters[0].Constraints.Count == 0);
        }

        private static void TestIntrinsicOps()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            var nullableI32a = graph.GetNullable(b.Int32);
            var localClass = new TypeSymbol("LocalUser", TypeKind.Class, b.Core, baseType: b.Object);

            // ===== 基元 intrinsic 键空间（BIL §11）=====
            CaseAssertions.CheckTrue("i32 含 Add/CmpLt/ShiftLeft/BinAnd",
                b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.Add)
                && b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.CmpLt)
                && b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.ShiftLeft)
                && b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.BinAnd));
            CaseAssertions.CheckTrue("i32 含 Opposite", b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.Opposite));
            CaseAssertions.CheckTrue("u32 不含 Opposite", !b.UInt32.IntrinsicOps.Contains(BilIntrinsicOp.Opposite));
            CaseAssertions.CheckTrue("double 含 Add 不含位运算",
                b.Double.IntrinsicOps.Contains(BilIntrinsicOp.Add)
                && !b.Double.IntrinsicOps.Contains(BilIntrinsicOp.BinAnd)
                && !b.Double.IntrinsicOps.Contains(BilIntrinsicOp.ShiftLeft));
            CaseAssertions.CheckTrue("bool 含 And/Not/CmpEq 不含 Add",
                b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.And)
                && b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.Not)
                && b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.CmpEq)
                && !b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.Add));
            CaseAssertions.CheckTrue("String 仅含相等比较",
                b.String.IntrinsicOps.Contains(BilIntrinsicOp.CmpEq)
                && b.String.IntrinsicOps.Contains(BilIntrinsicOp.CmpNe)
                && !b.String.IntrinsicOps.Contains(BilIntrinsicOp.CmpLt));
            CaseAssertions.CheckTrue("用户类型无 intrinsic", localClass.IntrinsicOps.Count == 0);
        }

        private static void TestFreeze()
        {
            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            var nullableI32a = graph.GetNullable(b.Int32);

            // ===== Freeze 机制 =====
            CaseAssertions.CheckTrue("冻结前 IsFrozen == false", !graph.IsFrozen);
            graph.Freeze();
            CaseAssertions.CheckTrue("Freeze 后 IsFrozen == true", graph.IsFrozen);
            CaseAssertions.CheckTrue("冻结后构造驻留仍幂等（透明派生物）",
                ReferenceEquals(nullableI32a, graph.GetNullable(b.Int32)));

        }
    }
}
