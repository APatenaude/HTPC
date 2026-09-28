namespace Htpc.Launcher;

/// <summary>A program found in the Start menu, for the "Add tile &gt; On this box" screen.</summary>
sealed record InstalledProgram(string Name, string? Target, string? Args, string? WorkingDir, bool Launchable, string? Note);

/// <summary>
/// Reads the Start-menu shortcuts (all users and this user) so the "On this box" screen can list
/// what is installed (SPEC W1, Add tile). The user chose to see everything, Windows tools included;
/// each entry's real target, arguments and working folder are resolved here so a tile launches the
/// program directly. Entries that are not a launchable .exe (an advertised MSI shortcut with no
/// target, a document, a UWP link) are listed but marked, with a short note, and cannot be added.
///
/// UWP/Store apps do not appear: they live in Windows' Apps folder, not as .lnk files. That is fine
/// for this box (the catalog and classic installers cover what it runs).
/// </summary>
static class StartMenuScanner
{
    /// <summary>
    /// Scan on a thread of its own, not the UI thread (it took 120-410 ms on the box). STA: the
    /// shortcuts are read through WScript.Shell, which lives in one.
    /// </summary>
    public static Task<List<InstalledProgram>> ScanAsync()
    {
        var done = new TaskCompletionSource<List<InstalledProgram>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(Scan()); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Name = "Start menu" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    public static List<InstalledProgram> Scan()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var found = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch (Exception e) { Log.Warn($"Start menu {root}: {e.Message}"); continue; }
            foreach (var link in links)
            {
                var name = Path.GetFileNameWithoutExtension(link);
                var program = Resolve(name, link);
                // Prefer a launchable entry when the same name shows up twice (all-users + per-user).
                if (!found.TryGetValue(name, out var existing) || (!existing.Launchable && program.Launchable))
                    found[name] = program;
            }
        }
        return found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static InstalledProgram Resolve(string name, string link)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var shortcut = shell.CreateShortcut(link);
            string target = shortcut.TargetPath ?? "";
            string args = shortcut.Arguments ?? "";
            string workingDir = shortcut.WorkingDirectory ?? "";
            if (string.IsNullOrEmpty(target))
                return new InstalledProgram(name, null, null, null, false, "Windows Installer shortcut");
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return new InstalledProgram(name, target, null, null, false, "Not a program");
            if (!File.Exists(target))
                return new InstalledProgram(name, target, null, null, false, "Missing");
            return new InstalledProgram(name, target,
                string.IsNullOrWhiteSpace(args) ? null : args,
                string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(target) : workingDir,
                true, null);
        }
        catch (Exception e)
        {
            Log.Warn($"Shortcut {link}: {e.Message}");
            return new InstalledProgram(name, null, null, null, false, "Could not read");
        }
    }
}
