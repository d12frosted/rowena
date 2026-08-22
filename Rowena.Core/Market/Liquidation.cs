namespace Rowena.Core.Market;

/// <summary>What to do with a stack sitting in a bag.</summary>
public enum HoardCall
{
    /// <summary>The board pays more than the vendor and will actually take it.</summary>
    List,

    /// <summary>The vendor pays more, or is the only buyer there is.</summary>
    Vendor,

    /// <summary>Wanted for something worth making, so not surplus at all.</summary>
    Keep,

    /// <summary>Nobody pays anything worth the bag slot.</summary>
    Worthless,
}

/// <summary>What a stack is worth and what to do with it.</summary>
/// <param name="Worth">What the whole stack fetches, taken to the better counter.</param>
/// <param name="Slow">True when the board would take longer than the horizon to absorb it.</param>
public readonly record struct HoardVerdict(
    HoardCall Call,
    long Worth,
    long EachOnBoard,
    long EachAtVendor,
    double? DaysToSell,
    bool Slow);

/// <summary>
/// What to do with the pile, which is the question a full retainer actually asks.
/// </summary>
/// <remarks>
/// Materials accumulate faster than anybody decides about them, and the decision is dull
/// enough that the pile wins. It is not a hard question, only a repetitive one: for each
/// stack, does the board pay more than the vendor, will the board take it in any reasonable
/// time, and is it wanted for something anyway.
///
/// That last one is why this takes an outside opinion rather than working it out. Telling
/// somebody to vendor the materials for the thing the craft table just told them to make
/// would be the worst advice in the plugin, and only the caller knows what is on the list.
/// </remarks>
public static class Liquidation
{
    public static HoardVerdict Of(
        int quantity,
        long? floor,
        double salesPerDay,
        long vendorPrice,
        MarketTax tax,
        int horizonDays,
        bool needed)
    {
        var board = floor is { } price ? tax.NetProceeds(price) : 0;
        var vendor = Math.Max(0, vendorPrice);
        var days = salesPerDay > 0 ? quantity / salesPerDay : (double?)null;
        var slow = days is null or > 0 && (days is null || days > horizonDays);

        if (needed)
            return new HoardVerdict(HoardCall.Keep, quantity * Math.Max(board, vendor), board, vendor, days, slow);

        // A board that never sells is not a counter, whatever it is asking. The vendor is the
        // one buyer that never runs out of appetite, so it wins by default rather than by
        // paying more.
        var call = (board, vendor, moves: salesPerDay > 0) switch
        {
            (0, 0, _) => HoardCall.Worthless,
            (_, _, false) => vendor > 0 ? HoardCall.Vendor : HoardCall.Worthless,
            _ => vendor >= board ? HoardCall.Vendor : HoardCall.List,
        };

        var worth = call switch
        {
            HoardCall.List => quantity * board,
            HoardCall.Vendor => quantity * vendor,
            _ => 0L,
        };

        return new HoardVerdict(call, worth, board, vendor, days, slow);
    }
}
