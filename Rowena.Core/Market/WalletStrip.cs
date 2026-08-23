using Rowena.Core.Conversions;

namespace Rowena.Core.Market;

/// <summary>A currency in your pockets, how much of it, and the cap when the game enforces one.</summary>
public readonly record struct Holding(Resource Currency, long Held, long? Cap);

/// <summary>One currency the strip shows, and why it is there.</summary>
/// <param name="Pinned">Chosen to always be on screen, whatever the balance.</param>
/// <param name="NearCap">Into the last tenth of its cap, where anything more earned is lost.</param>
public readonly record struct WalletRow(Resource Currency, long Held, long? Cap, bool Pinned, bool NearCap);

/// <summary>
/// Which currencies earn a place in the strip across the top of the window.
/// </summary>
/// <remarks>
/// Two reasons to be there, kept apart because they mean different things. A pinned currency
/// is one you have said you read the window against, so it stays whatever its balance: "is it
/// worth going to earn scrips" is a question asked precisely when you have none. Anything else
/// appears only as a warning, once it is into its last tenth and about to waste what is earned
/// next. A currency merely half full is not a decision, and showing it because of what was
/// ground last week made the strip look like a random pick.
///
/// Every currency you hold still has its table in the Sinks tab; this is not that list.
/// </remarks>
public static class WalletStrip
{
    /// <summary>Pinned first, in the order given; then the warnings, fullest first.</summary>
    public static IReadOnlyList<WalletRow> Pick(IEnumerable<Holding> holdings, Func<uint, bool> pinned)
    {
        var rows = holdings
            .Select(holding => new WalletRow(
                holding.Currency,
                holding.Held,
                holding.Cap,
                pinned(holding.Currency.Id),
                holding.Cap is { } cap && IsNearCap(holding.Held, cap)))
            .Where(row => row.Pinned || row.NearCap)
            .ToArray();

        return
        [
            .. rows.Where(row => row.Pinned),
            .. rows.Where(row => !row.Pinned).OrderByDescending(row => (double)row.Held / row.Cap!.Value),
        ];
    }

    /// <summary>Into the last tenth. The same line the chat alert is drawn at.</summary>
    public static bool IsNearCap(long held, long cap) => held >= cap - cap / 10;
}
