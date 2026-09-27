using System.Diagnostics;
using System.Text.Json;

namespace Htpc.Launcher;

/// <summary>
/// The hardware video decoding check from Settings › Display (SPEC N5): runs
/// setup\tools\Test-HwDecode.ps1 -Json in the background and keeps its report in
/// C:\ProgramData\HTPC\logs\hwdecode-last.json (About shows the last one).
///
/// Driver only (-NoPlayback): it asks the GPU driver which codecs it decodes in 4K. The
/// playback half needs mpv or ffmpeg, which setup does not install; on a dev box that has one,
/// it would decode 4K clips next to whatever the user is watching.
/// </summary>
sealed class DecodeCheck
{
    // In logs\: the one ProgramData\HTPC folder the launcher (standard rights) may write to once
    // setup's Library step has locked the rest; Save logs to USB copies it with the logs.
    static readonly string ResultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "logs", "hwdecode-last.json");
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public bool Running { get; private set; }

    /// <summary>The script: with the installed setup folder, else the one shipped with this build.</summary>
    static string? FindScript()
    {
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HTPC", "setup"),
            SetupRunner.FindSetupDir(),
        };
        return dirs.OfType<string>().Select(d => Path.Combine(d, "tools", "Test-HwDecode.ps1")).FirstOrDefault(File.Exists);
    }

    /// <summary>The last report, or null if there is none (or it is unreadable).</summary>
    public static JsonElement? Last()
    {
        try { return File.Exists(ResultPath) ? Parse(File.ReadAllText(ResultPath)) : null; }
        catch (Exception e) { Log.Warn($"Last decode check unreadable: {e.Message}"); return null; }
    }

    /// <summary>The script's JSON (anything PowerShell printed before it is skipped); null if there is none.</summary>
    public static JsonElement? Parse(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0) return null;
        using var doc = JsonDocument.Parse(output[start..]);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("codecs", out _) ? root.Clone() : null;
    }

    /// <summary>Runs the check; the report, or an object with "error" when it could not run.</summary>
    public async Task<JsonElement> RunAsync()
    {
        if (Running) throw new InvalidOperationException("already running");
        Running = true;
        try
        {
            var script = FindScript();
            if (script is null) return Error("The check is missing (setup\\tools\\Test-HwDecode.ps1)");
            Log.Info($"Decode check: {script}");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Json -NoPlayback")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(Timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return Error($"The check took longer than {Timeout.TotalSeconds:0} s");
            }
            var text = await output;
            var result = Parse(text);
            if (result is null)
            {
                Log.Warn($"Decode check gave no report (exit {process.ExitCode}): {(await errors).Trim()}");
                return Error("The check gave no answer");
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
                File.WriteAllText(ResultPath, text[text.IndexOf('{')..]);
            }
            catch (Exception e) { Log.Warn($"Saving the decode check: {e.Message}"); }
            Log.Info($"Decode check done: {(result.Value.TryGetProperty("pass", out var pass) && pass.ValueKind == JsonValueKind.True ? "all pass" : "not all")}");
            return result.Value;
        }
        catch (Exception e)
        {
            Log.Error("Decode check", e);
            return Error("The check could not run");
        }
        finally { Running = false; }
    }

    static JsonElement Error(string text) => JsonSerializer.SerializeToElement(new { error = text, checkedAt = DateTime.Now.ToString("s") });
}
