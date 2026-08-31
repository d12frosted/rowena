namespace Rowena.Core.Market;

/// <summary>What to do with a stack sitting in a bag.</summary>
public enum HoardCall
{
    /// <summary>The board pays more than the vendor and will actually take it.</summary>
    List,

    /// <summary>The vendor pays more, or is the only buyer there is.</summary>
    Vendor,

    /// <summary>Not surplus at all: wanted for a craft, mine to use, or not yet learned.</summary>
    Keep,

    /// <summary>Nobody pays anything worth the bag slot.</summary>
    Worthless,
}

/// <summary>Why a stack is not for sale at any counter.</summary>
public enum KeepWhy
{
    /// <summary>Nothing is holding it back: it is surplus, and the counters decide.</summary>
    Surplus,

    /// <summary>Wanted for something worth making.</summary>
    Wanted,

    /// <summary>Mine, because I said so: a thing I use rather than a thing I hold.</summary>
    Mine,

    /// <summary>An unlock I have not learned yet, so selling it costs the thing it teaches.</summary>
    Unlearned,
}

/// <summary>What a stack is worth and what to do with it.</summary>
/// <param name="Worth">What the whole stack fetches, taken to the better counter.</param>
/// <param name="Realised">What a market slot would earn for holding it over the horizon, which is not what it is worth.</param>
/// <param name="Keep">Why it is being kept, which is the whole of the reason when the call is to keep it.</param>
/// <param name="Slow">True when the board would take longer than the horizon to absorb it.</param>
public readonly record struct HoardVerdict(
    HoardCall Call,
    long Worth,
    long EachOnBoard,
    long EachAtVendor,
    double? DaysToSell,
    bool Slow,
    long Realised = 0,
    KeepWhy Keep = KeepWhy.Surplus);

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
///
/// It is an opinion with more than one source, too. A craft list is one reason to hold
/// something; a thing I use rather than sell is another, and it is not derivable from any
/// sheet, because chocobo greens and copper ore look identical to arithmetic. The third is the
/// game's own: an unlock I have not learned yet is worth what it teaches, and that is the one
/// mistake here no amount of gil undoes.
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
        long slotFloor,
        KeepWhy keep)
    {
        var board = floor is { } price ? tax.NetProceeds(price) : 0;
        var vendor = Math.Max(0, vendorPrice);
        var days = salesPerDay > 0 ? quantity / salesPerDay : (double?)null;
        var slow = days is null or > 0 && (days is null || days > horizonDays);

        // What a slot would earn for holding this, rather than what is in it. The same measure
        // the slot plan ranks on, because the question the floor asks is the plan's question
        // asked once: is this worth one of the twenty.
        var realised = RetainerSlots.Realised(quantity * board, days, horizonDays);

        // Before anything about counters, because keeping is not a verdict about what a thing
        // fetches. A stack nobody pays for is still not clutter when it is the last copy of
        // something I cannot buy back.
        if (keep != KeepWhy.Surplus)
        {
            return new HoardVerdict(
                HoardCall.Keep, quantity * Math.Max(board, vendor), board, vendor, days, slow, realised, keep);
        }

        // A board that never sells is not a counter, whatever it is asking. The vendor is the
        // one buyer that never runs out of appetite, so it wins by default rather than by
        // paying more.
        var call = (board, vendor, moves: salesPerDay > 0) switch
        {
            (0, 0, _) => HoardCall.Worthless,
            (_, _, false) => vendor > 0 ? HoardCall.Vendor : HoardCall.Worthless,
            _ => vendor >= board ? HoardCall.Vendor : HoardCall.List,
        };

        // The slots are the scarce thing, not the gil, so a stack that would not earn its keep
        // in one is not a listing however kindly the board treats it. Only ever a redirection to
        // a counter that pays: where the board is the one buyer there is, below the floor is
        // still the answer, because the alternative is the bin.
        if (call == HoardCall.List && realised < slotFloor && vendor > 0)
            call = HoardCall.Vendor;

        var worth = call switch
        {
            HoardCall.List => quantity * board,
            HoardCall.Vendor => quantity * vendor,
            _ => 0L,
        };

        return new HoardVerdict(call, worth, board, vendor, days, slow, realised);
    }
}
