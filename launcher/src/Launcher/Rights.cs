using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Htpc.Launcher;

/// <summary>
/// Who this process is, decided once in Program.Main and read from here everywhere else (never
/// Environment.IsPrivilegedProcess on its own). Users run the launcher as administrator whenever
/// they can (Run as administrator, User Account Control off, Windows' built-in Administrator, an
/// elevated CI runner), so administrator rights never mean "this is TV Box Setup":
///   - SetupElevated, setup mode with administrator rights (TV Box Setup), alone picks setup's
///     admin-only places in Program Files\HTPC\Setup (its log, its settings copy, logos, captures,
///     the TV step's files, its WebView2 run profile) and setup's own behaviour;
///   - Elevation, how the rights came: Standard (none), Split (elevated beside a standard-rights
///     token that User Account Control keeps: Run as administrator, an elevated shell) or NoSplit
///     (elevated with no other token: User Account Control off, the built-in Administrator).
/// The everyday launcher with a split token starts again at standard rights (AsUser's one-shot
/// Limited task) and ends before any file work; the one line it logs goes to setup's admin-only
/// log (Log.PickPath), the copy says why in the user's. With no split token there are no lower
/// rights to go to, and nothing to cross: it runs as it always does, in the user's own folders
/// (handoff, logos, phone certificates and HTTPS), with a warning in its log and in Settings ›
/// About. Real rights questions still ask Windows: the device-wide location switch
/// (LocationConsent), the kept setup folder's trust check, whether AsUser needs its task.
/// </summary>
static class Rights
{
    /// <summary>Standard: no administrator rights. Split: elevated, User Account Control on. NoSplit: elevated, no standard-rights token at all.</summary>
    public enum Token { Standard, Split, NoSplit }

    /// <summary>
    /// Run; RunWithFullRights (the everyday launcher with no split token: as usual, with a
    /// warning); AgainAtStandard (the everyday launcher with a split token: start again at
    /// standard rights, end); Stop (the copy started for that still has one: never a loop).
    /// </summary>
    public enum Start { Run, RunWithFullRights, AgainAtStandard, Stop }

    public readonly record struct Plan(bool SetupElevated, Start Start);

    /// <summary>On the command line of the copy started again at standard rights.</summary>
    public const string AtStandardFlag = "--standard-rights";

    /// <summary>TV Box Setup with administrator rights (setup mode and elevated). False until Main says otherwise (tests).</summary>
    public static bool SetupElevated { get; private set; }

    public static Token Elevation { get; private set; } = Token.Standard;

    /// <summary>Main, once, before anything logs.</summary>
    public static void Set(bool setupElevated, Token elevation) => (SetupElevated, Elevation) = (setupElevated, elevation);

    /// <summary>
    /// What a start does. Setup mode: elevated, it is TV Box Setup (SetupElevated, whichever
    /// token); not elevated, SetupElevation.Decide asks for the rights. The everyday launcher:
    /// runs at standard rights; with no split token runs as it is, warned; with a split token
    /// starts again at standard rights, unless it is that copy already (then it stops).
    /// </summary>
    public static Plan Decide(bool setupMode, Token elevation, bool startedAtStandard) => (setupMode, elevation) switch
    {
        (true, Token.Standard) => new(false, Start.Run),
        (true, _) => new(true, Start.Run),
        (false, Token.Standard) => new(false, Start.Run),
        (false, Token.NoSplit) => new(false, Start.RunWithFullRights),
        _ => new(false, startedAtStandard ? Start.Stop : Start.AgainAtStandard),
    };

    /// <summary>
    /// This process's token (TokenElevation, then TokenElevationType): elevated with
    /// TokenElevationTypeFull is Split (a limited twin exists), with TokenElevationTypeDefault
    /// NoSplit. A type Windows does not give counts as Split, the side that gives rights up.
    /// </summary>
    public static Token Read()
    {
        if (!Environment.IsPrivilegedProcess) return Token.Standard;
        using var me = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return GetTokenInformation(me.AccessToken, 18 /* TokenElevationType */, out var type, 4, out _) && type == 1 /* TokenElevationTypeDefault */
            ? Token.NoSplit : Token.Split;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returned);

    /// <summary>
    /// Main, the everyday launcher with a split token: this exe with its arguments (and
    /// AtStandardFlag) started for the signed-in user at standard rights, through the one-shot
    /// Limited task that runs at once in this session (AsUser). False when it could not (logged).
    /// </summary>
    public static bool StartAgainAtStandard(IEnumerable<string> args)
    {
        try
        {
            AsUser.Start(new UserStart(Environment.ProcessPath!, SetupElevation.CommandLine(AgainArgs(args)), AsUser.LauncherTask));
            return true;
        }
        catch (Exception e) { Log.Error("Starting the launcher again at standard rights", e); return false; }
    }

    /// <summary>The standard-rights copy's arguments: these, with AtStandardFlag once, last.</summary>
    public static List<string> AgainArgs(IEnumerable<string> args) => [.. args.Where(a => a != AtStandardFlag), AtStandardFlag];
}
