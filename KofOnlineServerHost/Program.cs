using System.Diagnostics;
using System.Runtime.InteropServices;

Console.Title = "KOFF Community Server";

var serverPath = Path.Combine(AppContext.BaseDirectory, "server", "KOF Online Server.exe");
if (!File.Exists(serverPath))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Servidor nao encontrado:");
    Console.WriteLine(serverPath);
    Console.ResetColor();
    Console.ReadKey();
    return;
}

KillOnCloseJob? job = null;
try { job = KillOnCloseJob.Create(); }
catch { }

var startInfo = new ProcessStartInfo(serverPath)
{
    WorkingDirectory = Path.GetDirectoryName(serverPath)!,
    UseShellExecute = false,
    CreateNoWindow = true
};
startInfo.ArgumentList.Add("--parent-pid");
startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

using var server = Process.Start(startInfo);

if (server is null)
{
    Console.WriteLine("Nao foi possivel iniciar o servidor.");
    return;
}

if (job is not null)
{
    try { job.Add(server); }
    catch
    {
        job.Dispose();
        job = null;
    }
}
server.WaitForExit();
job?.Dispose();

internal sealed class KillOnCloseJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly IntPtr _handle;

    private KillOnCloseJob(IntPtr handle) => _handle = handle;

    public static KillOnCloseJob Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(handle, 9, pointer, (uint)length))
                throw new System.ComponentModel.Win32Exception();
        }
        finally { Marshal.FreeHGlobal(pointer); }

        return new KillOnCloseJob(handle);
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new System.ComponentModel.Win32Exception();
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
