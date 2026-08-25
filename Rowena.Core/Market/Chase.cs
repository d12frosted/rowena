namespace Rowena.Core.Market;

/// <summary>What to do about a floor that has fallen a long way under what I am asking.</summary>
public enum ChaseCall
{
    /// <summary>Nothing to argue about. Ordinary competition, or nothing to argue it with.</summary>
    Follow,

    /// <summary>Little enough cheap stock that the board eats it inside the horizon. Sit still.</summary>
    Wait,

    /// <summary>The cheap stock is under what the thing is worth, and taking it pays.</summary>
    BuyOut,

    /// <summary>More cheap stock than the board will get through, and the sales agree with it.</summary>
    Accept,

    /// <summary>Deep cheap stock well under what this is worth, or a board where nothing sells.</summary>
    Withdraw,
}

/// <summary>
/// Whether a steep cut is worth taking, and what the alternatives cost.
/// </summary>
/// <param name="Cut">Gil the move takes off my asking price, per unit.</param>
/// <param name="Share">That as a share of my asking price.</param>
/// <param name="UnitsUnder">Units listed below mine that a buyer of my quality would take first.</param>
/// <param name="DaysToEat">How long the board takes to get through them, or null when it never does.</param>
/// <param name="Typical">What this has actually been changing hands for, when enough of it has.</param>
/// <param name="BuyOutCost">Gil to take every one of those units off the board, the buyer's cut included.</param>
/// <param name="BuyOutBack">What they should fetch back at the going rate, after the seller's cut.</param>
public readonly record struct ChaseVerdict(
    ChaseCall Call,
    long Cut,
    double Share,
    int UnitsUnder,
    double? DaysToEat,
    long? Typical,
    long BuyOutCost,
    long BuyOutBack);

/// <summary>
/// Whether to follow the floor down.
/// </summary>
/// <remarks>
/// <see cref="Undercut"/> says what price would put me first and is deliberately dumb about
/// whether that is a good idea. Usually it is a non-question: five gil off a hundred is what
/// competing looks like. But the same arithmetic on a listing at 4,000 against a floor of
/// 2,000 gives up half the price, and shown as "2,000 -> 1,995" it reads like giving up five
/// gil. The size of the move is what makes it a decision, so the size of the move is what
/// this is about.
///
/// The floor being low is not by itself evidence of anything. A floor a long way under what
/// the item has actually been selling for is: that is somebody clearing a retainer slot, and
/// it is a fact about them rather than about the market. What follows depends entirely on how
/// much of it there is. A couple of units are gone by the afternoon and matching them is a
/// haircut for nothing; where they are cheap enough, buying them is better than either, since
/// somebody else's panic at half price is the best-priced stock on the board. Four hundred
/// units is not a panic, it is the new price, or a board worth leaving for a while.
///
/// Deliberately silent where there is no evidence. Without a sale rate there is no queue to
/// read and no argument to make, and inventing one would be worse than the button on its own.
/// </remarks>
public static class Chase
{
    /// <summary>
    /// How much of the asking price a move has to give up before it is a decision.
    /// </summary>
    /// <remarks>
    /// Ordinary undercutting moves a price by the margin, a handful of gil. A quarter off is
    /// not competition with the listing in front; it is a different price for the item.
    /// </remarks>
    public const double Steep = 0.25d;

    /// <summary>
    /// How far under what people pay a floor has to sit before it stops being the market.
    /// </summary>
    /// <remarks>
    /// Prices move, and a floor a tenth under recent sales is one of the ways they move. At
    /// two fifths under, what is on the board and what people have paid for it disagree by
    /// more than a market does with itself.
    /// </remarks>
    public const double Dumped = 0.6d;

    /// <summary>How much a buy-out has to clear over its cost to be worth the gil and the wait.</summary>
    public const double Worth = 1.25d;

    /// <param name="patienceDays">
    /// How long I am willing to be selling. The same horizon the rest of the plugin plans
    /// over: a queue that clears inside it is a wait, and one that does not is a wall.
    /// </param>
    /// <param name="hq">The quality of my listing, which decides who counts as in front of it.</param>
    public static ChaseVerdict Of(
        UndercutPlan plan,
        OrderBook book,
        MarketTax tax,
        int patienceDays,
        bool hq = false)
    {
        var cut = Math.Max(0, -plan.Move);
        var share = Math.Max(0d, -plan.Share);

        // A raise is the opposite move, and pricing down to what people pay is my own mistake
        // rather than somebody else's dump, however steep the correction looks. Without a sale
        // rate there is no queue to read and nothing honest to say about any of it.
        //
        // Answered first because it answers nearly every row, and everything below it walks the
        // book: this runs per listing per frame while a retainer is open.
        if (plan.Why != UndercutWhy.Queue || share < Steep || !book.RateKnown)
            return new ChaseVerdict(ChaseCall.Follow, cut, share, plan.UnitsAhead, null, null, 0, 0);

        // Only what a buyer of my quality would take before mine, which is the same reading of
        // the queue the plan itself made.
        var under = book.Listings
            .Where(listing => listing.UnitPrice < plan.Mine && listing.Serves(hq))
            .ToArray();

        var units = under.Sum(listing => listing.Quantity);
        var days = book.SaleVelocityPerDay > 0 ? units / book.SaleVelocityPerDay : (double?)null;

        var typical = book.RecentSales.Count >= Undercut.EnoughSales && Median(book.RecentSales) is > 0 and var paid
            ? paid
            : (long?)null;

        // The pile, not the sticker on the front of it. A floor of 2,000 with one unit behind it
        // and thirty more at 4,300 costs nearly full price to clear, and the whole of this
        // library rests on the cheapest listing being a bad summary of a market.
        var cost = OrderBook.Create(book.ItemId, under).CostToBuy(units, tax).Total;
        var back = typical is { } worth ? tax.NetProceeds(worth) * units : 0;

        return new ChaseVerdict(
            Verdict(plan, days, typical, units, cost, back, patienceDays),
            cut,
            share,
            units,
            days,
            typical,
            cost,
            back);
    }

    /// <summary>
    /// The call, ordered by what overrules what.
    /// </summary>
    /// <remarks>
    /// A board selling nothing cannot be chased down at any price, so the queue is beside the
    /// point there. After that, cheap stock worth buying is worth buying whether or not it would
    /// also have cleared on its own, and a queue that clears inside the horizon is a wait rather
    /// than a haircut. Only what is left over is the price having actually moved.
    /// </remarks>
    private static ChaseCall Verdict(
        UndercutPlan plan,
        double? days,
        long? typical,
        int units,
        long cost,
        long back,
        int patienceDays)
    {
        // Nothing sells here at any price, so being first in the queue buys nothing and the
        // retainer slot is the only thing left worth recovering.
        if (days is not { } clears)
            return ChaseCall.Withdraw;

        var thin = clears <= patienceDays;
        var dumped = typical is { } worth && plan.Below < worth * Dumped;

        if (dumped && thin && units > 0 && back >= cost * Worth)
            return ChaseCall.BuyOut;

        if (thin)
            return ChaseCall.Wait;

        return dumped ? ChaseCall.Withdraw : ChaseCall.Accept;
    }

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}
