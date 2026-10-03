using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit;

public static partial class ModuleBuilder
{
    internal sealed partial class Session
    {
        private void EmitLibrary(LLVMBuilderRef builder)
        {
            // C runtime 的 uv_once 只调用本入口；不运行 main、drain 或每次 shutdown。
            var init = Module.AddFunction("rigi_library_init", LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [], false));
            builder.PositionAtEnd(init.AppendBasicBlock("entry")); SetCurrentFunction(init);
            foreach (var singleton in Singletons)
                if (TryGetFunction(singleton.GetFnCanonical, out var get))
                {
                    var value = builder.BuildCall2(get.Type, get.Value, System.Array.Empty<LLVMValueRef>(), "singleton");
                    ArcEmitter.EmitReleaseFatValue(this, builder, value);
                    EmitLibraryPendingCheck(builder, init);
                }
            foreach (var canonical in Env.GlobalInitializers)
                if (TryGetFunction(canonical, out var global))
                {
                    builder.BuildCall2(global.Type, global.Value, System.Array.Empty<LLVMValueRef>(), "");
                    EmitLibraryPendingCheck(builder, init);
                }
            builder.BuildRetVoid();
            foreach (var export in NativeBuild.Exports)
            {
                if (!TryGetFunction(export.Canonical, out var implementation))
                    throw new CompilerInternalException("C 导出根未发射实现：" + export.Canonical);
                var fn = implementation.Mir;
                if (fn.IsAsync || fn.IsCoroutineResume || IsInlineValueType(fn.ReturnType, out _))
                    throw new MwNotSupportedException("C 导出最终 MIR ABI 不再是同步标量：" + export.Canonical);
                var args = fn.Parameters.Select(p => CScalarType(p.Type)).ToArray();
                var wrapperType = LLVMTypeRef.CreateFunction(CScalarType(fn.ReturnType), args, false);
                var wrapper = Module.AddFunction(export.Name, wrapperType);
                AddScalarAttribute(wrapper, 0, fn.ReturnType);
                for (var i = 0; i < args.Length; i++) AddScalarAttribute(wrapper, (uint)i + 1, fn.Parameters[i].Type);
                builder.PositionAtEnd(wrapper.AppendBasicBlock("entry")); SetCurrentFunction(wrapper);
                var (ensure, ensureType) = CallEmitter.DeclareVoidFace(this, "rigi_library_ensure");
                builder.BuildCall2(ensureType, ensure, System.Array.Empty<LLVMValueRef>(), "");
                var values = new LLVMValueRef[args.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = wrapper.GetParam((uint)i);
                    if (fn.Parameters[i].Type.Key == "bool")
                        values[i] = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, values[i],
                            LLVMValueRef.CreateConstNull(LLVMTypeRef.Int8), "bool.input");
                }
                var result = builder.BuildCall2(implementation.Type, implementation.Value, values, fn.ReturnType.IsVoid ? "" : "result");
                EmitLibraryPendingCheck(builder, wrapper);
                if (fn.ReturnType.IsVoid) builder.BuildRetVoid();
                else builder.BuildRet(fn.ReturnType.Key == "bool" ? builder.BuildZExt(result, LLVMTypeRef.Int8, "bool.output") : result);
            }
        }
        private LLVMTypeRef CScalarType(MirType type) => type.Key == "bool" ? LLVMTypeRef.Int8 : TypeLayout.MapType(Context, type);
        private static void AddScalarAttribute(LLVMValueRef wrapper, uint index, MirType type)
        {
            // Linux x64 clang 的 i8/i16 参数和结果有扩展属性；MSVC x64 ABI 无此属性。
            if (OperatingSystem.IsWindows()) return;
            var attribute = type.Key switch { "i8" or "i16" => "signext", "bool" or "u8" or "u16" => "zeroext", _ => null };
            if (attribute != null) LlvmBitcode.AddEnumAttribute(wrapper, index, attribute);
        }
        private void EmitLibraryPendingCheck(LLVMBuilderRef builder, LLVMValueRef function)
        {
            var (pending, pendingType) = CallEmitter.DeclareHelperFace(this, RuntimeFaces.ExcPending, BytePointer(), []);
            var value = builder.BuildCall2(pendingType, pending, System.Array.Empty<LLVMValueRef>(), "pending");
            var failed = function.AppendBasicBlock("uncaught"); var continuation = function.AppendBasicBlock("continue");
            builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, value,
                LLVMValueRef.CreateConstNull(BytePointer()), "failed"), failed, continuation);
            builder.PositionAtEnd(failed);
            var target = ResolveUncaughtReporterTarget(out var sheet);
            if (target != null)
            {
                var (take, takeType) = CallEmitter.DeclareHelperFace(this, RuntimeFaces.ExcTake, BytePointer(), []);
                var slot = builder.BuildAlloca(BytePointer(), "exception");
                builder.BuildStore(builder.BuildCall2(takeType, take, System.Array.Empty<LLVMValueRef>(), "exception.value"), slot);
                EmitUncaughtReporterBody(builder, target, sheet, slot);
            }
            else
            {
                var (halt, haltType) = CallEmitter.DeclareVoidFace(this, "rigi_library_halt");
                builder.BuildCall2(haltType, halt, System.Array.Empty<LLVMValueRef>(), ""); builder.BuildUnreachable();
            }
            builder.PositionAtEnd(continuation);
        }
    }
}
