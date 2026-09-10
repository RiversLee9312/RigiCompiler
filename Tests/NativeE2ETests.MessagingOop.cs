namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // 同源分别作 VM 字面量断言与 native 对拍，且 native 默认启用泄漏检测。
        internal static string MessagingLifecycleSource =>
            SerializationGraphCorpus("mq_oop_lifecycle");
    }
}
