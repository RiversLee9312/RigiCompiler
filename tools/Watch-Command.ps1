#Requires -Version 5.1
<#
.SYNOPSIS
    Watch-Command —— shell 层看门狗：带超时运行命令，超时/中止时经 Job Object 灭整棵进程树。

.DESCRIPTION
    落实 docs/agent_guide/development.md「子进程进程树管理」四条铁律：
      1. stdin 必须断开：本脚本以重定向 stdin 并立即关闭（EOF）的方式启动子进程。
      2. 不用 $p.Kill($true)（PS 5.1 无此 API）；超时杀伤走 TerminateJobObject + taskkill /T /F。
      3. Job Object 根治：子进程出生即入 job（KILL_ON_JOB_CLOSE），看门狗退出（正常、超时、
         被外层杀掉）时句柄关闭，内核自动灭整树，不留孤儿。
      4. 全程不需要管理员权限。

    用途一（看门狗运行）：
      tools/Watch-Command.ps1 -Command dotnet -ArgumentList "run -- test --run 58" -TimeoutSeconds 900
      - 子进程 stdout/stderr 原样穿透（不缓冲、不接管）；stdin 断开。
      - 正常结束：退出码 = 子进程退出码；job 关闭时顺带清扫残留孙进程。
      - 超时：灭整树，stderr 打印诊断，退出码 124。

    用途二（孤儿清扫，独立调用）：
      tools/Watch-Command.ps1 -CleanupOrphans
      杀掉遗留的 rigic.exe / dotnet rigic.dll 测试子进程（文件锁会干扰重跑）；
      全部清净返回 0，存在杀不掉者（铁律 4 提权边界）返回 1。

.PARAMETER Command
    要运行的可执行文件（看门狗模式必填）。

.PARAMETER ArgumentList
    原始参数字符串，原样传给子进程（含空格的参数自行加双引号）。
    注意：经 powershell.exe 命令行传参时逗号不会拆分数组，故本参数是单字符串而非数组。

.PARAMETER TimeoutSeconds
    超时秒数（默认 600）。到期灭整树并退出 124。

.PARAMETER WorkingDirectory
    子进程工作目录（默认当前目录）。

.PARAMETER CleanupOrphans
    独立模式：清扫遗留 rigic/dotnet 测试子进程后退出。
#>
[CmdletBinding()]
param(
    [string]$Command,
    [string]$ArgumentList = '',
    [int]$TimeoutSeconds = 600,
    [string]$WorkingDirectory = (Get-Location).Path,
    [switch]$CleanupOrphans
)

$ErrorActionPreference = 'Stop'

# ---------- 用途二：孤儿清扫 ----------
if ($CleanupOrphans) {
    $targets = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -ieq 'rigic.exe' -or $_.Name -ieq 'rigic' -or
        ($_.Name -ieq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine -match '(?i)rigic\.dll')
    })
    if ($targets.Count -eq 0) {
        Write-Host "watch-Command: no leftover rigic/dotnet orphans"
        exit 0
    }
    $failed = 0
    foreach ($t in $targets) {
        Write-Host ("watch-command: killing orphan pid={0} name={1}" -f $t.ProcessId, $t.Name)
        # PS 5.1 下原生命令 stderr 在 EAP=Stop 时抛 NativeCommandError；清扫失败（如铁律 4
        # 提权边界 Access denied）属可报告事件而非脚本崩溃
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & taskkill /PID $t.ProcessId /T /F 2>&1 | Out-Null
        $rc = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($rc -ne 0) {
            $failed++
            Write-Host ("watch-command: taskkill pid={0} failed (exit {1})——可能需提权（铁律 4）" -f $t.ProcessId, $rc)
        }
    }
    exit $(if ($failed -gt 0) { 1 } else { 0 })
}

if (-not $Command) {
    Write-Error "watch-command: -Command is required (or use -CleanupOrphans)"
    exit 2
}
if ($TimeoutSeconds -le 0) {
    Write-Error "watch-command: -TimeoutSeconds must be positive"
    exit 2
}

# ---------- Job Object P/Invoke（全部封在 C# 内，避开 PS 嵌套 struct 赋值坑） ----------
if (-not ('RigiJob' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class RigiJob
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr hJob, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    public static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
            {
                int err = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new System.ComponentModel.Win32Exception(err, "SetInformationJobObject failed");
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }

    public static void Assign(IntPtr job, Process p)
    {
        if (!AssignProcessToJobObject(job, p.Handle))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    public static void KillTree(IntPtr job, uint exitCode)
    {
        TerminateJobObject(job, exitCode);
    }

    public static void Close(IntPtr job)
    {
        if (job != IntPtr.Zero) CloseHandle(job);
    }
}
'@
}

# ---------- 看门狗主流程 ----------
$job = [RigiJob]::CreateKillOnCloseJob()
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Command
    $psi.Arguments = $ArgumentList
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true   # 铁律 1：stdin 断开（启动后立即关流 → EOF）
    $psi.WorkingDirectory = $WorkingDirectory

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    try {
        [void]$p.Start()
    }
    catch {
        Write-Error "watch-command: failed to start '$Command': $($_.Exception.Message)"
        exit 127
    }

    [RigiJob]::Assign($job, $p)           # 铁律 3：后代出生即入 job
    $p.StandardInput.Close()              # 立即 EOF

    $exited = $p.WaitForExit($TimeoutSeconds * 1000)
    if ($exited) {
        $p.WaitForExit()                  # 让 ExitCode 定型
        $code = $p.ExitCode               # PS 关键字参数模式下 exit $p.ExitCode 会被拆成 $p + ".ExitCode"
        exit $code
    }

    [Console]::Error.WriteLine("watch-command: timeout after ${TimeoutSeconds}s, killing process tree (pid=$($p.Id))")
    [RigiJob]::KillTree($job, 137)
    # 铁律 2：逃逸者兜底（父进程多半已被 job 灭掉，taskkill 找不到属正常；PS 5.1 下原生命令
    # stderr 在 EAP=Stop 时会抛 NativeCommandError，这里必须降级 Continue 吞掉）
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & taskkill /PID $p.Id /T /F 2>$null | Out-Null } catch { }
    $ErrorActionPreference = $prevEap
    exit 124
}
finally {
    [RigiJob]::Close($job)                # KILL_ON_JOB_CLOSE：残余后代由内核收尾
}
