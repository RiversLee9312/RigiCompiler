using System.Runtime.CompilerServices;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // VM 与 native 使用同一份有明确行为断言的语料，避免复制两份测试源码。
        private static string SerializationGraphCorpus(string name, [CallerFilePath] string path = "") =>
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "e2e", "rigi", name + ".rg"));

        // 依赖原生台账的语料不属于 BIL VM 语言语料目录。
        private static string NativeResourceCorpus(string name, [CallerFilePath] string path = "") =>
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "native", "rigi", name + ".rg"));

        // 日常回归保留竞争形态；百万级压力由工具显式运行。
        private static string MqConcurrentReleaseCorpus([CallerFilePath] string path = "")
        {
            var source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!,
                "..", "tools", "stress", "mq.rg"));
            // 四生产者、四读者、八个以上日志段仍完整覆盖竞争形态；
            // VM 全指令计费下控制日常预算，大吞吐量留给原生压力工具。
            source = source.Replace("1000000", "2048").Replace("250000", "512")
                .Replace("62500", "128");
            // 压力观测为 native 专属且顺序不确定，不进入 VM/native 输出对拍。
            source = System.Text.RegularExpressions.Regex.Replace(source,
                @"(?m)^.*Console\.println.*runtimeStatus\(\)[^\r\n]*[\r\n]*", "");
            return System.Text.RegularExpressions.Regex.Replace(source,
                "(?m)^.*Console\\.println\\(\"live-[^\\r\\n]*[\\r\\n]*", "");
        }
    }
}
