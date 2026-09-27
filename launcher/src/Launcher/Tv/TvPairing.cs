namespace Htpc.Launcher;

/// <summary>
/// A method whose TV must accept the box once (LG: "Allow" on the TV; Google TV: a code the TV
/// shows, typed on the box). Pairing only ever starts from the user's pick of a TV, never under
/// --no-tv, and its keys live in TvCredentials (protected, never logged).
/// </summary>
interface ITvPairing
{
    void UseCredentials(TvCredentials credentials);

    /// <summary>Characters of the code the TV shows (0: it asks to say yes on it instead).</summary>
    int CodeLength { get; }

    bool IsPaired(TvDevice tv);

    /// <summary>
    /// Pairs with this TV, reporting each step ("prompt": say yes on the TV; "code": type the code
    /// the TV shows, taken from <paramref name="nextCode"/>) until it succeeds (true) or not.
    /// </summary>
    Task<bool> Pair(TvDevice tv, Action<string, string> step, Func<CancellationToken, Task<string?>> nextCode, CancellationToken cancel);

    void Forget(TvDevice tv);

    /// <summary>The paired TV refused the stored key (pairing undone on it, or expired).</summary>
    event Action<TvDevice>? PairingLost;
}

/// <summary>What setup and Settings show while pairing: the TV, the step ("prompt", "code", "working", "done", "failed") and why.</summary>
sealed record TvPairState(string DeviceKey, string Name, string Stage, string Message, int CodeLength);
