namespace LatteCompiler.Tests
{
    /// <summary>
    /// S1 符号图内核测试（M37）：构造泛型驻留（同一引用）、bootstrap 层级、
    /// rich/shared 标记、共享安全推导、泛型约束、基元 intrinsic 键空间。
    /// 符号比较一律引用相等（ReferenceEquals），禁止按名字字符串比较身份。
    /// </summary>
    public static class SymbolGraphTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("SymbolGraph");

            var graph = new SymbolGraph();
            var b = graph.Bootstrap;

            // ===== 构造泛型驻留（§4.2：同一 (定义, 实参列表) 必得同一实例）=====
            var nullableI32a = graph.GetNullable(b.Int32);
            var nullableI32b = graph.GetConstructedType(b.NullableDefinition, b.Int32);
            TestHarness.CheckTrue("T? 与 Nullable\\<T> 同一实例",
                ReferenceEquals(nullableI32a, nullableI32b));
            TestHarness.CheckTrue("重复构造同一引用",
                ReferenceEquals(nullableI32a, graph.GetNullable(b.Int32)));
            TestHarness.CheckTrue("不同实参不同实例",
                !ReferenceEquals(nullableI32a, graph.GetNullable(b.Int64)));

            // 构造类型属性传播
            TestHarness.CheckTrue("构造类型 ConstructedFrom",
                ReferenceEquals(nullableI32a.ConstructedFrom, b.NullableDefinition));
            TestHarness.CheckTrue("构造类型实参",
                nullableI32a.TypeArguments!.Count == 1
                && ReferenceEquals(nullableI32a.TypeArguments[0], b.Int32));
            TestHarness.CheckTrue("构造类型基类随定义（Object 分支）",
                ReferenceEquals(nullableI32a.BaseType, b.Object));
            TestHarness.CheckTrue("构造类型非 ValueType 分支", !nullableI32a.IsValueTypeBranch);

            // ===== bootstrap 层级（SYNTAX §3.1）=====
            TestHarness.CheckTrue("Any 无基类", b.Any.BaseType == null);
            TestHarness.CheckTrue("Object <: Any", ReferenceEquals(b.Object.BaseType, b.Any));
            TestHarness.CheckTrue("ValueType <: Any", ReferenceEquals(b.ValueType.BaseType, b.Any));
            TestHarness.CheckTrue("i32 <: ValueType", ReferenceEquals(b.Int32.BaseType, b.ValueType));
            TestHarness.CheckTrue("Enum <: ValueType", ReferenceEquals(b.Enum.BaseType, b.ValueType));
            TestHarness.CheckTrue("Wrapper <: ValueType", ReferenceEquals(b.Wrapper.BaseType, b.ValueType));
            TestHarness.CheckTrue("String <: ValueType", ReferenceEquals(b.String.BaseType, b.ValueType));
            TestHarness.CheckTrue("Nullable\\<T> <: Object", ReferenceEquals(b.NullableDefinition.BaseType, b.Object));
            TestHarness.CheckTrue("Box\\<T> <: Object（内建事实）", ReferenceEquals(b.BoxDefinition.BaseType, b.Object));
            TestHarness.CheckTrue("Type\\<T> <: ValueType", ReferenceEquals(b.TypeDefinition.BaseType, b.ValueType));
            TestHarness.CheckTrue("Span\\<T> <: ValueType", ReferenceEquals(b.SpanDefinition.BaseType, b.ValueType));

            // ===== 分支与 rich/shared 标记 =====
            TestHarness.CheckTrue("i32 在 ValueType 分支", b.Int32.IsValueTypeBranch);
            TestHarness.CheckTrue("String 在 ValueType 分支", b.String.IsValueTypeBranch);
            TestHarness.CheckTrue("Object 不在 ValueType 分支", !b.Object.IsValueTypeBranch);
            TestHarness.CheckTrue("Any 不在 ValueType 分支", !b.Any.IsValueTypeBranch);
            TestHarness.CheckTrue("Wrapper 恒 rich", b.Wrapper.IsRich);
            TestHarness.CheckTrue("String 非 rich", !b.String.IsRich);
            TestHarness.CheckTrue("i32 非 rich", !b.Int32.IsRich);

            // ===== 共享安全推导（SYNTAX §3.1.1 白名单）=====
            var localClass = new TypeSymbol("LocalUser", TypeKind.Class, b.Core, baseType: b.Object);
            var sharedClass = new TypeSymbol("SharedUser", TypeKind.Class, b.Core, baseType: b.Object, isShared: true);
            var sharedRichStruct = new TypeSymbol("SharedEntry", TypeKind.Struct, b.Core,
                baseType: b.ValueType, isRich: true, isShared: true);
            TestHarness.CheckTrue("非 rich ValueType 共享安全（i32）", b.Int32.IsSharedSafe());
            TestHarness.CheckTrue("String 共享安全", b.String.IsSharedSafe());
            TestHarness.CheckTrue("local class 不共享安全", !localClass.IsSharedSafe());
            TestHarness.CheckTrue("shared class 共享安全", sharedClass.IsSharedSafe());
            TestHarness.CheckTrue("shared rich struct 共享安全", sharedRichStruct.IsSharedSafe());
            TestHarness.CheckTrue("Nullable\\<i32> 按 T 推导共享安全", nullableI32a.IsSharedSafe());
            TestHarness.CheckTrue("Nullable\\<local class> 不共享安全",
                !graph.GetNullable(localClass).IsSharedSafe());

            // ===== 泛型约束（extends ValueType）=====
            TestHarness.CheckTrue("Span\\<T extends ValueType\\>",
                b.SpanDefinition.GenericParameters.Count == 1
                && ReferenceEquals(b.SpanDefinition.GenericParameters[0].Constraint, b.ValueType));
            TestHarness.CheckTrue("Box\\<T extends ValueType\\>",
                b.BoxDefinition.GenericParameters.Count == 1
                && ReferenceEquals(b.BoxDefinition.GenericParameters[0].Constraint, b.ValueType));
            TestHarness.CheckTrue("Nullable\\<T\\> 无约束",
                b.NullableDefinition.GenericParameters.Count == 1
                && b.NullableDefinition.GenericParameters[0].Constraint == null);

            // ===== 基元 intrinsic 键空间（BIL §11）=====
            TestHarness.CheckTrue("i32 含 Add/CmpLt/ShiftLeft/BinAnd",
                b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.Add)
                && b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.CmpLt)
                && b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.ShiftLeft)
                && b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.BinAnd));
            TestHarness.CheckTrue("i32 含 Opposite", b.Int32.IntrinsicOps.Contains(BilIntrinsicOp.Opposite));
            TestHarness.CheckTrue("u32 不含 Opposite", !b.UInt32.IntrinsicOps.Contains(BilIntrinsicOp.Opposite));
            TestHarness.CheckTrue("double 含 Add 不含位运算",
                b.Double.IntrinsicOps.Contains(BilIntrinsicOp.Add)
                && !b.Double.IntrinsicOps.Contains(BilIntrinsicOp.BinAnd)
                && !b.Double.IntrinsicOps.Contains(BilIntrinsicOp.ShiftLeft));
            TestHarness.CheckTrue("bool 含 And/Not/CmpEq 不含 Add",
                b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.And)
                && b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.Not)
                && b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.CmpEq)
                && !b.Bool.IntrinsicOps.Contains(BilIntrinsicOp.Add));
            TestHarness.CheckTrue("String 仅含相等比较",
                b.String.IntrinsicOps.Contains(BilIntrinsicOp.CmpEq)
                && b.String.IntrinsicOps.Contains(BilIntrinsicOp.CmpNe)
                && !b.String.IntrinsicOps.Contains(BilIntrinsicOp.CmpLt));
            TestHarness.CheckTrue("用户类型无 intrinsic", localClass.IntrinsicOps.Count == 0);

            // ===== Freeze 机制 =====
            TestHarness.CheckTrue("冻结前 IsFrozen == false", !graph.IsFrozen);
            graph.Freeze();
            TestHarness.CheckTrue("Freeze 后 IsFrozen == true", graph.IsFrozen);
            TestHarness.CheckTrue("冻结后构造驻留仍幂等（透明派生物）",
                ReferenceEquals(nullableI32a, graph.GetNullable(b.Int32)));

            return TestHarness.Summary("SymbolGraph");
        }
    }
}
