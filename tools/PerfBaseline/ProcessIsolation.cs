using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RigiCompiler.PerfBaseline;

/// <summary>共用受管启动；Windows 子线程在归入 Job 前不执行一条用户指令。</summary>
internal sealed class ProcessIsolation : IDisposable
{
    private readonly SafeFileHandle? job;
    private int groupId;
    internal string Kind => OperatingSystem.IsLinux() ? "linux-session-process-group" : OperatingSystem.IsWindows() ? "windows-atomic-job-or-suspended" : "process-tree";
    internal ProcessIsolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
        { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    internal ContainedProcess Start(ProcessStartInfo info)
    {
        if (OperatingSystem.IsWindows()) return StartWindows(info);
        if (OperatingSystem.IsLinux())
        {
            var executable = info.FileName; var arguments = info.ArgumentList.ToArray();
            info.FileName = "setsid"; info.ArgumentList.Clear();
            info.ArgumentList.Add("--wait"); info.ArgumentList.Add(executable);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
        var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            if (OperatingSystem.IsLinux()) groupId = process.Id;
            return new ContainedProcess(process);
        }
        catch { process.Dispose(); throw; }
    }
    internal void Kill(ContainedProcess process)
    {
        if (groupId != 0) kill(-groupId, 9);
        else if (job != null) TerminateJobObject(job, 124);
        // 建组尚未完成时仍要灭本次创建的根；不触碰其他进程。
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (groupId != 0) kill(-groupId, 9);
        job?.Dispose();
    }

    private ContainedProcess StartWindows(ProcessStartInfo info)
    {
        if (!info.RedirectStandardInput || !info.RedirectStandardOutput || !info.RedirectStandardError || info.UseShellExecute)
            throw new ArgumentException("受管启动要求三条重定向管道且禁止 shell");
        using var input = Pipe(parentWrites: true);
        using var output = Pipe(parentWrites: false);
        using var error = Pipe(parentWrites: false);
        var inherited = new[] { input.Child.DangerousGetHandle(), output.Child.DangerousGetHandle(), error.Child.DangerousGetHandle() };
        string executable = ResolveExecutable(info.FileName, info.WorkingDirectory);
        var command = new StringBuilder(BuildWindowsCommand(executable, info));
        var environment = string.Join('\0', info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        using var env = new NativeMemory(Marshal.StringToHGlobalUni(environment));
        ProcessInfo created = default;
        bool atomic = true;
        try
        {
            using var attributes = new Attributes(inherited, job!, includeJob: true);
            created = Create(attributes, atomic);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 50 or 87 or 120)
        {
            // 旧 Windows 无 JOB_LIST 时仍用 HANDLE_LIST，CREATE_SUSPENDED 后先 Assign 再 Resume。
            atomic = false;
            using var attributes = new Attributes(inherited, job!, includeJob: false);
            created = Create(attributes, atomic);
        }
        using var processHandle = new SafeFileHandle(created.Process, ownsHandle: true);
        using var threadHandle = new SafeFileHandle(created.Thread, ownsHandle: true);
        Process? process = null;
        ContainedProcess? contained = null;
        try
        {
            if (!atomic && !AssignProcessToJobObject(job!, processHandle.DangerousGetHandle()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = Process.GetProcessById((int)created.ProcessId);
            _ = process.SafeHandle; // 挂起期间固定真实进程身份，避免快退根导致 PID 复用。
            contained = new ContainedProcess(process, input.TakeParent(), output.TakeParent(), error.TakeParent(), info);
            // 取得 process/父管道所有权后才执行子线程；任何失败先杀 Job/挂起 root 再关闭句柄。
            if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            input.Child.Dispose(); output.Child.Dispose(); error.Child.Dispose();
            return contained;
        }
        catch
        {
            TerminateJobObject(job!, 127); TerminateProcess(processHandle, 127);
            WaitForSingleObject(processHandle, 10_000);
            if (contained != null) contained.Dispose(); else process?.Dispose();
            throw;
        }

        ProcessInfo Create(Attributes attributes, bool useAtomic)
        {
            var startup = new StartupInfoEx { Startup = new StartupInfo {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = inherited[0], Output = inherited[1], Error = inherited[2] }, Attributes = attributes.Pointer };
            // 原子 JOB_LIST 也先挂起，以便父端取得全部句柄；回退绝无 Start→Attach race。
            if (!CreateProcess(executable, new StringBuilder(command.ToString()), IntPtr.Zero, IntPtr.Zero, true,
                0x80000 | 0x400 | 4, env.Pointer, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory,
                ref startup, out var result)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
    }
    private static string ResolveExecutable(string name, string workingDirectory)
    {
        if (Path.IsPathRooted(name)) return Path.GetFullPath(name);
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(name, string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var suffix in Path.HasExtension(name) ? new[] { "" } : new[] { ".exe", "" })
            {
                var path = Path.Combine(directory, name + suffix);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        throw new FileNotFoundException("未找到子进程可执行文件", name);
    }
    internal static string BuildWindowsCommand(string executable, ProcessStartInfo info) =>
        info.Arguments.Length > 0 && info.ArgumentList.Count == 0
            ? QuoteArgument(executable) + " " + info.Arguments
            : string.Join(" ", new[] { executable }.Concat(info.ArgumentList).Select(QuoteArgument));

    internal static string QuoteArgument(string value)
    {
        // Windows CRT 规则：引号前反斜杠双写，末尾反斜杠在闭引号前双写。
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private sealed class NativeMemory(IntPtr pointer) : IDisposable
    { internal IntPtr Pointer { get; } = pointer; public void Dispose() => Marshal.FreeHGlobal(Pointer); }
    private sealed class PipeEnds(SafeFileHandle parent, SafeFileHandle child) : IDisposable
    {
        private SafeFileHandle? parent = parent;
        internal SafeFileHandle Child { get; } = child;
        internal SafeFileHandle TakeParent() => Interlocked.Exchange(ref parent, null)!;
        public void Dispose() { parent?.Dispose(); Child.Dispose(); }
    }
    private static PipeEnds Pipe(bool parentWrites)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        if (!CreatePipe(out var read, out var write, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var parent = parentWrites ? write : read; var child = parentWrites ? read : write;
        if (!SetHandleInformation(parent, 1, 0))
        { read.Dispose(); write.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return new(parent, child);
    }
    private sealed class Attributes : IDisposable
    {
        internal IntPtr Pointer { get; }
        private readonly List<IntPtr> values = [];
        private bool initialized;
        internal Attributes(IntPtr[] handles, SafeFileHandle job, bool includeJob)
        {
            nuint length = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, includeJob ? 2 : 1, 0, ref length);
            Pointer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!InitializeProcThreadAttributeList(Pointer, includeJob ? 2 : 1, 0, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                initialized = true;
                Add(0x00020002, handles);
                if (includeJob) Add(0x0002000D, [job.DangerousGetHandle()]);
            }
            catch { Dispose(); throw; }
        }
        private void Add(nuint attribute, IntPtr[] handles)
        {
            var value = Marshal.AllocHGlobal(handles.Length * IntPtr.Size); values.Add(value);
            Marshal.Copy(handles, 0, value, handles.Length);
            if (!UpdateProcThreadAttribute(Pointer, 0, attribute, value, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        {
            // 属性值必须活到 DeleteAttributeList 之后。
            if (initialized) DeleteProcThreadAttributeList(Pointer);
            foreach (var value in values) Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle handle, int informationClass, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle handle, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle handle, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        internal int Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, CountX, CountY, Fill, Flags;
        internal ushort Show, ReservedSize; internal IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        internal long PerProcessUserTime, PerJobUserTime;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    { internal BasicLimits Basic; internal IoCounters Io; internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
}

/// <summary>托管等待/采样与原子启动管道的统一面。</summary>
internal sealed class ContainedProcess : IDisposable
{
    private readonly Process process;
    internal StreamWriter StandardInput { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }
    internal ContainedProcess(Process process)
    { this.process = process; StandardInput = process.StandardInput; StandardOutput = process.StandardOutput; StandardError = process.StandardError; }
    internal ContainedProcess(Process process, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error, ProcessStartInfo info)
    {
        this.process = process;
        try
        {
            StandardInput = new(new FileStream(input, FileAccess.Write), info.StandardInputEncoding ?? new UTF8Encoding(false));
            StandardOutput = new(new FileStream(output, FileAccess.Read), info.StandardOutputEncoding ?? Encoding.UTF8);
            StandardError = new(new FileStream(error, FileAccess.Read), info.StandardErrorEncoding ?? Encoding.UTF8);
        }
        catch { input.Dispose(); output.Dispose(); error.Dispose(); throw; }
    }
    internal int Id => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal long WorkingSet64 => process.WorkingSet64;
    internal long PeakWorkingSet64 => process.PeakWorkingSet64;
    internal TimeSpan TotalProcessorTime => process.TotalProcessorTime;
    internal void Refresh() => process.Refresh();
    internal Task WaitForExitAsync(CancellationToken cancellationToken = default) => process.WaitForExitAsync(cancellationToken);
    internal void Kill() => process.Kill(entireProcessTree: true);
    public void Dispose() { StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); process.Dispose(); }
}

/// <summary>Linux 同 process-group 活体采样；峰值为采样下界，CPU 为各 PID 最后可读累计值。</summary>
internal sealed class ProcessTreeSampler(int group)
{
    private readonly Dictionary<int, long> observedTicks = [];
    internal long PeakRssBytes { get; private set; }
    internal double CpuMilliseconds => observedTicks.Values.Sum() * 1000.0 / ticksPerSecond;
    private readonly long ticksPerSecond = OperatingSystem.IsLinux() ? Math.Max(1, sysconf(2)) : 100;
    internal void Capture()
    {
        if (!OperatingSystem.IsLinux()) return;
        long rss = 0;
        foreach (var path in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(path), out int pid)) continue;
            try
            {
                var stat = File.ReadAllText(Path.Combine(path, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) != group) continue;
                observedTicks[pid] = long.Parse(fields[11], System.Globalization.CultureInfo.InvariantCulture)
                    + long.Parse(fields[12], System.Globalization.CultureInfo.InvariantCulture);
                rss += Math.Max(0, long.Parse(fields[21], System.Globalization.CultureInfo.InvariantCulture)) * Environment.SystemPageSize;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
        }
        PeakRssBytes = Math.Max(PeakRssBytes, rss);
    }
    [DllImport("libc")] private static extern long sysconf(int name);
}
