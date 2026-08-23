using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// The facts worth saying unprompted, computed once for everyone who says them.
/// </summary>
/// <remarks>
/// The server bar, the login briefing and the alerts all want the same two things: which
/// currency is running into its cap, and what the flips would pay. One place computes them
/// so that three places cannot disagree, and so that none of them fetches anything: these
/// are read off the books the cache already holds and inherit their age.
/// </remarks>
internal sealed class Headlines(Trades trades, Boards boards, Balances balances, Configuration config)
{
    public readonly record struct Capped(Resource Currency, long Held, long Cap);

    /// <summary>What the flips pay, and the single best one.</summary>
    public readonly record struct Flips(long Total, Allocation Best);

    /// <summary>Every currency into the last tenth of its cap, worst first.</summary>
    public IReadOnlyList<Capped> NearCap()
    {
        var near = new List<Capped>();

        foreach (var currency in trades.Currencies)
        {
            if (balances.CapOf(currency) is not { } cap)
                continue;

            var held = balances.Held(currency);
            if (WalletStrip.IsNearCap(held, cap))
                near.Add(new Capped(currency, held, cap));
        }

        return [.. near.OrderByDescending(capped => (double)capped.Held / capped.Cap)];
    }

    /// <summary>
    /// What the best split of your gil across the flips pays, off the cached books, sized
    /// to the selling horizon like the Flips tab. Null when nothing pays.
    /// </summary>
    public Flips? BestFlips()
    {
        var tax = boards.Tax;

        var candidates = trades.Flips
            .Where(conversion => ConversionEvaluator
                .Evaluate(conversion, 1, boards.Buying, boards.Selling, tax, boards.Vendor)
                is { IsExecutable: true, Profit: > 0 })
            .ToArray();

        if (candidates.Length == 0)
            return null;

        var allocated = ConversionAllocation
            .Allocate(
                candidates, boards.Buying, boards.Selling, tax, balances.Gil, config.SizingCap,
                config.SellingHorizon(), boards.Vendor)
            .Where(allocation => allocation.Runs > 0)
            .ToArray();

        if (allocated.Length == 0)
            return null;

        return new Flips(
            allocated.Sum(allocation => allocation.Profit),
            allocated.MaxBy(allocation => allocation.Profit)!);
    }
}
