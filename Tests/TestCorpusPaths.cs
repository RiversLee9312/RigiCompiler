using System.Runtime.CompilerServices;

namespace RigiCompiler.Tests;

/// <summary>测试资产随输出/发布同行；源码回退仅用于开发，不依赖当前工作目录。</summary>
public static class TestCorpusPaths
{
    public static string Resolve(string relativePath, [CallerFilePath] string sourcePath = "")
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
        if (File.Exists(output) || Directory.Exists(output)) return output;
        if (Environment.GetEnvironmentVariable("RIGI_TEST_CORPUS_ONLY_OUTPUT") != "1")
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "RigiCompiler.csproj")))
                {
                    var source = Path.Combine(directory.FullName, relativePath);
                    if (File.Exists(source) || Directory.Exists(source)) return source;
                    break;
                }
                directory = directory.Parent;
            }
        }
        throw new FileNotFoundException($"测试发布资产缺失: {relativePath}", output);
    }
}
