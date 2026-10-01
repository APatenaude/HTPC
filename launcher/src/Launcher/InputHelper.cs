using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Htpc.Launcher;

/// <summary>
/// The input helper (--input-helper): this program, elevated, with no window, started by the
/// \HTPC\Input task when the launcher finds a window in front that runs with administrator
/// rights (ElevatedInput). It listens on a pipe, takes keyboard and mouse records from the
/// installed launcher alone and sends them with SendInput, which an elevated process may do into
/// elevated windows. It runs only from the installed launcher's file (Program Files\HTPC\Launcher:
/// administrator-write, so nothing the user can replace), takes one connection, serves it and ends
/// with it, and ends by itself when nobody connects. It logs in Program Files\HTPC\Setup\logs, not
/// anywhere the user can write (Log.PickPath).
/// </summary>
static class InputHelper
{
    /// <summary>The installed launcher's file.</summary>
    public static string InstalledExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HTPC", "Launcher", "HtpcLauncher.exe");

    public static bool SameFile(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the server end of the pipe is the helper: the installed launcher's file, and the pipe
    /// made by an elevated process (its owner is Administrators, as an elevated token's default owner
    /// is; a standard process that took the name first owns it as itself).
    /// </summary>
    public static bool IsTheHelper(int processId, NamedPipeClientStream pipe)
    {
        if (!SameFile(Native.ProcessInfo(processId).Path, InstalledExe)) return false;
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        return owner is SecurityIdentifier sid && sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
    }

    public static int Run()
    {
        if (!Environment.IsPrivilegedProcess) { Log.Warn("Input helper: not elevated; ending"); return 1; }
        if (!SameFile(Environment.ProcessPath, InstalledExe)) { Log.Warn($"Input helper: not the installed launcher ({Environment.ProcessPath}); ending"); return 1; }
        // Windows' own environment for the rest of this process (the task's command line did it for
        // what .NET reads before Main): nothing of the user's.
        SetupElevation.ApplyCleanEnvironment();
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.Write | PipeAccessRights.Synchronize | PipeAccessRights.ReadPermissions | (PipeAccessRights)0x80 /* FILE_READ_ATTRIBUTES: opening it asks for it */, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        NamedPipeServerStream server;
        try { server = NamedPipeServerStreamAcl.Create(ElevatedInput.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous, 0, 0, security); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info("Input helper: another one runs; ending"); return 0; }
        using (server)
        {
            Log.Info("Input helper: listening");
            using var waiting = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try { server.WaitForConnectionAsync(waiting.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Log.Info("Input helper: nobody connected; ending"); return 0; }
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var client) || !SameFile(Native.ProcessInfo((int)client).Path, InstalledExe))
            {
                Log.Warn("Input helper: a connection from something other than the installed launcher; ending");
                return 1;
            }
            Log.Info($"Input helper: serving the launcher (pid {client})");
            var sent = 0;
            while (InputFrame.Read(server) is { } records)
            {
                Input.SendRecords(records);
                sent++;
            }
            Log.Info($"Input helper: the launcher is gone; ending ({sent} frames sent)");
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
}
