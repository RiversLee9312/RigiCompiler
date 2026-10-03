using RigiCompiler.Middleware.Toolchain;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

[Category("LinkerThreads")]
public class LinkerThreadTests
{
    [Test]
    public void ElfAndCoffSameSpelling()
    {
        if (LinkerThreads.Argument("4") != "-Wl,--threads=4" || LinkerThreads.Argument(null) != null)
            throw new InvalidOperationException("ELF/COFF必须使用官方 --threads=N");
    }
    [Test]
    public void StrictSettings()
    {
        foreach (var setting in new[] { "", "0", "255", "-1", "+1", " 1", "1 ", "1.0", "abc" })
        {
            try { LinkerThreads.Argument(setting); throw new InvalidOperationException("非法lld线程被接受：" + setting); }
            catch (ArgumentException) { }
        }
    }
}
