using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Passes;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Tests
{
    public static partial class MiddlewareTests
    {
        // Binding 职责；与主文件共享同一类型、字段及生命周期。

        // ===== 实现绑定 =====

        private static void TestBinding()
        {
            // string + 是 op 级内建 → 运行时面；别名 core::String 与 .string 同键
            var concat = ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                ".string", "core::String", ".string");
            TestHarness.CheckTrue("string + → rigi_string_concat 运行时面",
                concat is RuntimeFaceBinding { FaceSymbol: RuntimeFaces.StringConcat });

            TestHarness.CheckTrue("i32 + → IntAdd 指令选择",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                    ".i32", ".i32", ".i32")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntAdd });
            TestHarness.CheckTrue("u64 / → IntUDiv",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Div,
                    ".u64", ".u64", ".u64")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntUDiv });
            TestHarness.CheckTrue("i64 / → IntSDiv",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Div,
                    ".i64", ".i64", ".i64")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntSDiv });
            TestHarness.CheckTrue("f64 >= → FloatCmpGe",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpGe,
                    ".f64", ".f64", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.FloatCmpGe });
            TestHarness.CheckTrue("bool and → LogicAnd",
                ImplBinder.BindBinary(Bil.BilBinaryOp.And,
                    ".bool", ".bool", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.LogicAnd });
            TestHarness.CheckTrue("i32 取负 → IntNeg",
                ImplBinder.BindUnary(Bil.BilUnaryOp.Opposite,
                    ".i32", ".i32")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntNeg });
            TestHarness.CheckTrue("bool not → LogicNot",
                ImplBinder.BindUnary(Bil.BilUnaryOp.Not,
                    ".bool", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.LogicNot });

            // string 比较 → 比较面（六种比较同一面，次序判定归 Emit）
            TestHarness.CheckTrue("string cmp.eq → StringCompareBinding",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpEq,
                    ".string", "core::String", ".bool")
                    is StringCompareBinding);
            TestHarness.CheckTrue("string cmp.lt → StringCompareBinding",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".string", ".string", ".bool")
                    is StringCompareBinding);

            // 窄宽度整数：与 i32 同族绑定（LLVM 指令同宽两侧天然满足）
            TestHarness.CheckTrue("i8 + → IntAdd",
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                    ".i8", ".i8", ".i8")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntAdd });
            TestHarness.CheckTrue("u16 >> → ShiftRightUnsigned",
                ImplBinder.BindBinary(Bil.BilBinaryOp.ShiftRight,
                    ".u16", ".u16", ".u16")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.ShiftRightUnsigned });
            TestHarness.CheckTrue("i16 >> → ShiftRightSigned",
                ImplBinder.BindBinary(Bil.BilBinaryOp.ShiftRight,
                    ".i16", ".i16", ".i16")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.ShiftRightSigned });
            TestHarness.CheckTrue("u8 < → IntCmpULt",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".u8", ".u8", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntCmpULt });

            // char 比较按 UTF-16 码元无符号序（VM 同口径）；char 无算术/一元
            TestHarness.CheckTrue("char == → IntCmpEq",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpEq,
                    ".char", ".char", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntCmpEq });
            TestHarness.CheckTrue("char < → IntCmpULt",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".char", ".char", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.IntCmpULt });
            var charArith = false;
            try
            {
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add, ".char", ".char", ".char");
            }
            catch (MwNotSupportedException)
            {
                charArith = true;
            }
            TestHarness.CheckTrue("char 算术受控拒绝（VM 同口径）", charArith);
            var charUnary = false;
            try
            {
                ImplBinder.BindUnary(Bil.BilUnaryOp.BinNot, ".char", ".char");
            }
            catch (MwNotSupportedException)
            {
                charUnary = true;
            }
            TestHarness.CheckTrue("char 一元受控拒绝（VM 同口径）", charUnary);

            // .nullable<T>：eq/ne → 胖引用恒等（null 双段零天然成立）；
            // 排序比较不适用（受控拒绝）
            TestHarness.CheckTrue("nullable cmp.eq → RefCmpEq",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpEq,
                    ".nullable<.string>", ".nullable<.string>", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.RefCmpEq });
            TestHarness.CheckTrue("nullable cmp.ne → RefCmpNe",
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpNe,
                    ".nullable<.i32>", "core::Nullable<core::i32>", ".bool")
                    is PrimitiveOpBinding { Kind: PrimitiveOpKind.RefCmpNe });
            var nullableOrder = false;
            try
            {
                ImplBinder.BindBinary(Bil.BilBinaryOp.CmpLt,
                    ".nullable<.string>", ".nullable<.string>", ".bool");
            }
            catch (MwNotSupportedException)
            {
                nullableOrder = true;
            }
            TestHarness.CheckTrue("nullable 排序比较受控拒绝", nullableOrder);

            // bool 位运算不绑定（§11.4 收紧：内建位运算仅整数族；此类
            // BIL 已过不了 Gate，此处为纵深防御断言）
            var boolBitwise = 0;
            foreach (var bitOp in new[]
            {
                Bil.BilBinaryOp.BinAnd, Bil.BilBinaryOp.BinOr, Bil.BilBinaryOp.BinXor,
            })
            {
                try
                {
                    ImplBinder.BindBinary(bitOp, ".bool", ".bool", ".bool");
                }
                catch (MwNotSupportedException)
                {
                    boolBitwise++;
                }
            }
            TestHarness.CheckTrue("bool bin.and/or/xor 受控拒绝", boolBitwise == 3);

            // 实例方法派发细分（VM 同口径）：class → 虚调用；interface →
            // iMap 派发；init → 直调
            var (_, classTextModule, classText) = BilTestHarness.EmitBilUnit(
                "pub interface Named { func name(): String }\n" +
                "pub open class Base { pub init() { } pub open func who(): i32 { return 1 } }\n" +
                "pub class Derived : Base implements Named {\n" +
                "    pub init() { }\n" +
                "    pub override func who(): i32 { return 2 }\n" +
                "    pub override func name(): String { return \"d\" }\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            classText = BilWriter.Write(classTextModule);
            var classGate = BilGate.Accept(classText, "bindclass.bil");
            TestHarness.CheckTrue("派发用例门禁放行", classGate.IsAccepted,
                string.Join("; ", classGate.Errors));
            var classContext = new MwContext(classGate.Module!);
            TestHarness.CheckTrue("class 实例方法 → VirtualCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Base$who()@.i32")!)
                    is VirtualCallBinding);
            TestHarness.CheckTrue("override 方法 → VirtualCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Derived$who()@.i32")!)
                    is VirtualCallBinding);
            TestHarness.CheckTrue("interface 方法 → InterfaceCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Named$name()@.string")!)
                    is InterfaceCallBinding);
            TestHarness.CheckTrue("init → DirectCallBinding",
                ImplBinder.BindCall(classContext.Symbols.FindMember("Derived$init()@.void")!)
                    is DirectCallBinding);

            // 不支持组合 → MwNotSupportedException（受控失败，非崩溃）
            var unsupported = false;
            try
            {
                ImplBinder.BindBinary(Bil.BilBinaryOp.Add,
                    ".string", ".i32", ".string");
            }
            catch (MwNotSupportedException)
            {
                unsupported = true;
            }
            TestHarness.CheckTrue("string + i32 受控拒绝", unsupported);

            // 调用绑定：native 声明 → NativeDirectBinding；本地 fn → DirectCallBinding
            var gate = BilGate.Accept(HelloConcatBil, "bind.bil");
            var context = new MwContext(gate.Module!);
            var nativePrint = context.Symbols.FindMember(
                "core.io::Console$.static.print(value:.string)@.void");
            TestHarness.CheckTrue("native print → NativeDirectBinding(rigi_rt, print)",
                ImplBinder.BindCall(nativePrint!) is NativeDirectBinding
                { Library: "rigi_rt", Symbol: "print" });
            var main = context.Symbols.FindMember("$main()@.i32");
            TestHarness.CheckTrue("本地 fn → DirectCallBinding",
                ImplBinder.BindCall(main!) is DirectCallBinding);

            // canonical 签名解析（native 声明无 fn 体，签名从符号文本解析）
            var signature = CanonicalSignature.Parse(
                "core.io::Console$.static.print(value:.string)@.void");
            TestHarness.CheckTrue("签名解析：参数 名:类型",
                signature.Parameters.Count == 1 && signature.Parameters[0].Name == "value"
                && signature.Parameters[0].TypeRef == ".string");
            TestHarness.CheckTrue("签名解析：返回类型", signature.ReturnTypeRef == ".void");
            var nested = CanonicalSignature.Parse("f(m:.map<.string, .i64>, x:.i32)@.void");
            TestHarness.CheckTrue("签名解析：嵌套泛型逗号不分割",
                nested.Parameters.Count == 2
                && nested.Parameters[0].TypeRef == ".map<.string, .i64>");
        }

    }
}
