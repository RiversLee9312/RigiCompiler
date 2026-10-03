using System.Globalization;

namespace RigiCompiler.Middleware.Toolchain;

/// <summary>LLVM 20 的 ELF/COFF 共用 --threads=N；只影响 relink，不改变 O2 对象身份。</summary>
public static class LinkerThreads
{
    public static string? Argument(string? setting)
    {
        if (setting == null) return null;
        if (!int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count is < 1 or > 254)
            throw new ArgumentException("RIGI_LLD_THREADS 必须是 1..254 的十进制整数");
        return "-Wl,--threads=" + count.ToString(CultureInfo.InvariantCulture);
    }
}
