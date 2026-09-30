#Requires -Version 5.1
# Dot-sourced by the dev tools that start headless Edge (Test-Ui, Save-Screenshots, Test-All): puts
# this PowerShell in a job Windows closes when it ends, however it ends (Ctrl+C, a tool's time limit,
# a caller that stops reading): every process it started, Edge and Edge's own children, ends with it.
# Their own clean-up only ran when they finished normally: 41 headless Edge processes outlived killed
# runs on 30 Sept 2026. Children join the job as they start, so none can slip out before it is set.
Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class HtpcKillOnExit {
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long A, B; public uint LimitFlags; public UIntPtr C, D; public uint E; public UIntPtr F; public uint G, H; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public UIntPtr P, J, PP, PJ; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref Extended info, int size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    static IntPtr job; // never closed: Windows closes it when this process ends, and ends the rest
    public static bool Enable() {
        if (job != IntPtr.Zero) return true;
        var j = CreateJobObject(IntPtr.Zero, null);
        var info = new Extended(); info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (j == IntPtr.Zero || !SetInformationJobObject(j, 9, ref info, Marshal.SizeOf(typeof(Extended)))) return false;
        if (!AssignProcessToJobObject(j, GetCurrentProcess())) return false;
        job = j; return true;
    }
}
'@
if (-not [HtpcKillOnExit]::Enable()) {
    Write-Warning 'Processes this script starts are not tied to it: a run ended from outside may leave headless Edge behind'
}
