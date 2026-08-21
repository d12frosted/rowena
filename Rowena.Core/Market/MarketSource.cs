namespace Rowena.Core.Market;

/// <summary>
/// Where a piece of market data came from, which decides how far to trust it.
/// </summary>
/// <remarks>
/// Not all answers about a board are equally good. What the client itself saw is exactly what
/// was there, at the moment it was there. What Universalis returns is whatever somebody else's
/// client last uploaded, which may be hours old and is capped at however many listings were
/// asked for. Keeping the difference means a stale crowdsourced book can be told apart from
/// one seen a minute ago at the counter, rather than both being a timestamp.
/// </remarks>
public enum MarketSource
{
    /// <summary>Crowdsourced, from the Universalis API.</summary>
    Universalis,

    /// <summary>Pushed by Universalis as it happened.</summary>
    UniversalisLive,

    /// <summary>Seen by this client, at the board itself. Exact.</summary>
    Game,
}
