using Rowena.Core.Conversions;
using Rowena.Core.Market;

namespace Rowena.UI;

/// <summary>
/// Turning numbers into the words a column can hold.
/// </summary>
/// <remarks>
/// Shared rather than repeated, because two views wording the same measurement differently is how a
/// reader ends up believing they are looking at two measurements.
/// </remarks>
internal static class Phrases
{
    /// <summary>How long the board would take to take the whole order off you.</summary>
    /// <remarks>
    /// Null is "never", not "instantly". Nothing sells is the strongest thing this column has to say
    /// and printing a blank there would have hidden it.
    /// </remarks>
    public static string Absorb(double? days) => days switch
    {
        null => "never",
        < 1d => "<1 day",
        _ => $"{days.Value:F1} days",
    };

    /// <summary>
    /// Gil at a width the server bar can afford.
    /// </summary>
    /// <remarks>
    /// The bar gets a handful of characters, so this trades digits for magnitude. Everywhere
    /// with room for the real number shows the real number.
    /// </remarks>
    public static string CompactGil(long gil) => gil switch
    {
        >= 10_000_000 => $"{gil / 1_000_000d:F0}M",
        >= 1_000_000 => $"{gil / 1_000_000d:F1}M",
        >= 10_000 => $"{gil / 1_000d:F0}k",
        _ => $"{gil:N0}",
    };

    /// <summary>
    /// An age, at the precision the age itself deserves.
    /// </summary>
    /// <remarks>
    /// Days at the top end because a listing can sit for a week without anybody asking the board
    /// about it again, and "163 h" is a number you have to do arithmetic on before it means
    /// anything.
    /// </remarks>
    public static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "seconds",
        { TotalHours: < 1 } => $"{span.TotalMinutes:F0} min",
        { TotalDays: < 1 } => $"{span.TotalHours:F0} h",
        _ => $"{span.TotalDays:F0} d",
    };

    /// <summary>What to do about a floor that has fallen a long way, in the words a row can hold.</summary>
    public static string Chase(ChaseCall call) => call switch
    {
        ChaseCall.Wait => "sit tight",
        ChaseCall.BuyOut => "buy it out",
        ChaseCall.Withdraw => "delist",
        ChaseCall.Accept => "price moved",
        _ => "",
    };

    /// <summary>
    /// The argument behind that word, with the numbers it is standing on.
    /// </summary>
    /// <remarks>
    /// Shared between the tab and the overlay, because they are two views of one decision and
    /// a reader who found them disagreeing would be right to stop trusting either.
    /// </remarks>
    public static string ChaseWhy(ChaseVerdict chase) => chase.Call switch
    {
        ChaseCall.Wait =>
            $"Worth sitting out. The board gets through those {chase.UnitsUnder:N0} units in\n"
            + $"{Absorb(chase.DaysToEat)}, and giving up {chase.Share:P0} to jump a queue that short is a\n"
            + "haircut for nothing.",

        ChaseCall.BuyOut =>
            $"Worth buying rather than joining. Those {chase.UnitsUnder:N0} units cost {chase.BuyOutCost:N0} to take\n"
            + "off the board, the buyer's cut included, and at what this has been selling for\n"
            + $"({chase.Typical:N0}) they come back as {chase.BuyOutBack:N0} after the seller's cut. That clears\n"
            + $"{chase.BuyOutBack - chase.BuyOutCost:N0} and leaves your own listing first without moving it.",

        // Nothing sells here at any price, so the queue is beside the point: being first in it
        // costs the cut and buys nothing.
        ChaseCall.Withdraw when chase.DaysToEat is null =>
            "Worth taking off the board for now. The board reports no sales at all for this, so\n"
            + "there is no queue to wait out and no price that makes it move.",

        ChaseCall.Withdraw =>
            $"Worth taking off the board for now. {chase.UnitsUnder:N0} units sit under you, which is\n"
            + $"{Absorb(chase.DaysToEat)} of queue, and they are priced well under what this has been\n"
            + $"selling for ({chase.Typical:N0}). Matching them means selling at a fraction of the value,\n"
            + "and the retainer slot is worth more on something that is moving.",

        ChaseCall.Accept =>
            $"This one probably is the new price. {chase.UnitsUnder:N0} units sit under you, which is\n"
            + $"{Absorb(chase.DaysToEat)} of queue, and recent sales agree with them rather than with your\n"
            + "price. Steep, but not somebody clearing a slot.",

        _ => "",
    };

    /// <summary>
    /// The noun for one unit of a currency, for labelling a rate.
    /// </summary>
    /// <remarks>
    /// "gil each" never said each what, and 71.25 next to a column of millions invites reading it as
    /// millions too. The tables are already grouped per currency, so the header only needs the noun:
    /// gil/scrip.
    /// </remarks>
    public static string UnitOf(Resource currency)
    {
        var words = currency.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "unit" : words[^1].ToLowerInvariant();
    }
}
