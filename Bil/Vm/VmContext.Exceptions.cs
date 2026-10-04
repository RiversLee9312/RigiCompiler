using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Exceptions 职责；与主文件共享同一类型、字段及生命周期。

        // MW9b：cast 失败消息模板源码化（stdlib init(fromType,toType)）
        public VmException CastFailed(VmCoroutine coroutine, string fromType, string toType)
        {
            return LanguageException(coroutine, "core::CastException",
                new VmValue[] { new VmString(fromType), new VmString(toType) },
                new[] { ".string", ".string" },
                "无法将 " + fromType + " 转换为 " + toType);
        }

        // 自由文本 NoSuchMethodException（$$call 无匹配 / wrapper 降级未路由）
        public VmException NoSuchMethod(VmCoroutine coroutine, string message)
        {
            return LanguageException(coroutine, "core::NoSuchMethodException",
                new VmValue[] { new VmString(message) }, new[] { ".string" }, message);
        }

        // 无协程上下文（native hook 等）沿用直写字段路径
        public VmException NoSuchMethod(string message)
        {
            return LanguageException("core::NoSuchMethodException", message);
        }

        // MW9b：new.indirect 动态构造失败（stdlib init(typeName)）。
        // init(typeName: String) 与 init(text: String) 同为 (String) 单参
        // 签名——按首形参名精确命中 typeName 重载
        public VmException NoSuchMethodForType(VmCoroutine coroutine, string typeRef)
        {
            return LanguageException(coroutine, "core::NoSuchMethodException",
                new VmValue[] { new VmString(typeRef) }, new[] { ".string" },
                "new.indirect 目标不可构造：不匹配任何 init：" + typeRef,
                initFirstParamName: "typeName");
        }

        // MW9b：整数除零（stdlib 零参 init，模板烘进 Rigi 源码）
        public VmException DividedByZero(VmCoroutine coroutine)
        {
            return LanguageException(coroutine, "core::DividedByZeroException",
                Array.Empty<VmValue>(), Array.Empty<string>(), "整数除以零");
        }

        // MW9b：数组/Span 写越界（stdlib init(index,length)，可捕获）
        public VmException OutOfBounds(VmCoroutine coroutine, long index, long length)
        {
            return LanguageException(coroutine, "core::OutOfBoundException",
                new VmValue[] { new VmI64(index), new VmI64(length) },
                new[] { ".i64", ".i64" },
                "数组下标越界：" + index + "（长度 " + length + "）");
        }

        // MW9b：内置异常 message 源码化——分配对象后按新便捷 init 签名经
        // VM 正常派发调 init（参照用户 new 的 init 调用路径：receiver 打头
        // InvokeValues + 同步 Step 回落到原栈深），消息模板烘在
        // stdlib/core/exceptions.rg，VM/native 两侧天然一致。
        // stdlib init 体是纯赋值 + 插值拼接不会失败；防御性失败（模板漂移、
        // init 匹配落空、派发中 abrupt）回落旧直写字段路径。
        public VmException LanguageException(VmCoroutine coroutine, string typeRef,
            IReadOnlyList<VmValue> initArguments, IReadOnlyList<string> initStaticTypes,
            string fallbackMessage, string? initFirstParamName = null)
        {
            var instance = AllocateObject(typeRef);
            var found = initFirstParamName != null
                ? TryFindInitByFirstParamName(typeRef, initFirstParamName, initArguments,
                    initStaticTypes, out var initSymbol)
                : TryFindInit(typeRef, initArguments, initStaticTypes, out initSymbol);
            if (coroutine.State == VmCoroutineState.Running
                && !coroutine.HasAbruptCompletion
                && found
                && initSymbol.Length > 0)
            {
                try
                {
                    var callArgs = new List<VmValue>(initArguments.Count + 1) { instance };
                    callArgs.AddRange(initArguments);
                    var depth = coroutine.CallStack.Count;
                    BilInvokeExecution.InvokeValues(this, coroutine, initSymbol, callArgs,
                        resultSlot: null);
                    while (coroutine.CallStack.Count > depth
                        && coroutine.State == VmCoroutineState.Running
                        && !coroutine.HasAbruptCompletion)
                    {
                        coroutine.Step(this);
                    }
                    if (coroutine.CallStack.Count == depth
                        && coroutine.State == VmCoroutineState.Running
                        && !coroutine.HasAbruptCompletion
                        && instance.TryReadField(RuntimeField("core::Exception#message@.string"),
                            out var messageValue)
                        && messageValue is VmString messageText)
                    {
                        return new VmException(messageText.Value, instance);
                    }
                }
                catch (VmException)
                {
                    // 回落旧直写字段路径（见上注释：stdlib init 不会失败）
                }
            }
            instance.WriteField(RuntimeField("core::Exception#message@.string"),
                new VmString(fallbackMessage));
            return new VmException(fallbackMessage, instance);
        }

        public VmException LanguageException(string typeRef, string message)
        {
            var instance = AllocateObject(typeRef);
            instance.WriteField(RuntimeField("core::Exception#message@.string"), new VmString(message));
            return new VmException(message, instance);
        }

    }
}
