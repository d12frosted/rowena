using Rowena.Core.Market;

namespace Rowena.Core.Conversions;

/// <summary>Prices a conversion against a market snapshot.</summary>
public static class ConversionEvaluator
{
    /// <summary>
    /// Values <paramref name="runs"/> repetitions of a conversion.
    /// </summary>
    /// <param name="books">
    /// Looks up an item's order book, or null when the item is not on the board at all.
    /// Null and empty mean different things and the quote reports them differently.
    /// </param>
    /// <remarks>
    /// Inputs are priced by walking the book, which is the entire reason this library
    /// exists. Buying a hundred of something is not a hundred times the floor, and the
    /// gap between those two numbers has been the difference between a trade worth doing
    /// and one that is not.
    ///
    /// Outputs are valued at the floor, which is mildly optimistic: to actually sell you
    /// have to match it or undercut it. It is left optimistic on purpose, so that the
    /// pessimism lives in one honest place, <see cref="ConversionQuote.DaysToAbsorb"/>,
    /// rather than being smeared into the price as a fudge factor.
    ///
    /// Quantities in the returned quote are absolute, not per run. The conversion carried
    /// on the quote is the original, unscaled one, so it still reads as the rate it is.
    /// </remarks>
    public static ConversionQuote Evaluate(
        Conversion conversion,
        int runs,
        Func<uint, OrderBook?> books,
        MarketTax tax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);

        var scaled = conversion.Scaled(runs);

        long outlay = 0;
        var currencySpent = new List<ResourceAmount>();
        var unsourced = new List<ResourceAmount>();

        foreach (var input in scaled.Inputs)
        {
            if (input.Resource.Kind == ResourceKind.Currency)
            {
                currencySpent.Add(input);
                continue;
            }

            var book = books(input.Resource.Id);
            if (book is null)
            {
                unsourced.Add(input);
                continue;
            }

            var quote = book.CostToBuy(input.Quantity);
            outlay += quote.Total;

            if (!quote.IsComplete)
                unsourced.Add(new ResourceAmount(input.Resource, quote.ShortBy));
        }

        long gross = 0;
        var unpriced = new List<ResourceAmount>();
        double? daysToAbsorb = null;

        foreach (var output in scaled.Outputs)
        {
            // Currency out of a conversion is real, but it has no gil price by definition,
            // so it cannot be counted as proceeds. It shows up as a better rate later, when
            // whatever sink consumes it is evaluated in turn.
            if (output.Resource.Kind == ResourceKind.Currency)
                continue;

            var book = books(output.Resource.Id);
            if (book?.Floor is not { } floor)
            {
                unpriced.Add(output);
                continue;
            }

            gross += floor * output.Quantity;

            // The slowest output governs: the trade is not closed until all of it is sold.
            if (book.DaysToAbsorb(output.Quantity) is { } days)
                daysToAbsorb = Math.Max(daysToAbsorb ?? 0d, days);
        }

        return new ConversionQuote(
            conversion,
            runs,
            outlay,
            gross,
            tax.NetProceeds(gross),
            Merge(currencySpent),
            Merge(unsourced),
            Merge(unpriced),
            daysToAbsorb);
    }

    /// <summary>
    /// The most runs that still turn a profit at current depth, up to <paramref name="cap"/>.
    /// </summary>
    /// <remarks>
    /// The answer to "how many can I actually do", which is a different and more useful
    /// question than "is this profitable". Each extra run buys further up the book, so the
    /// margin per run only shrinks, and past some size it goes negative while the floor
    /// still looks inviting.
    ///
    /// Scanned rather than bisected. Marginal profit is non-increasing, so a bisection
    /// would be sound, but the caps here are small and being obviously correct is worth
    /// more than the microseconds.
    /// </remarks>
    public static int LargestProfitableSize(
        Conversion conversion,
        Func<uint, OrderBook?> books,
        MarketTax tax,
        int cap)
    {
        var best = 0;

        for (var runs = 1; runs <= cap; runs++)
        {
            var quote = Evaluate(conversion, runs, books, tax);
            if (!quote.IsExecutable || quote.Profit <= 0)
                break;

            best = runs;
        }

        return best;
    }

    private static IReadOnlyList<ResourceAmount> Merge(IEnumerable<ResourceAmount> amounts) =>
    [
        .. amounts
            .GroupBy(amount => amount.Resource)
            .Select(group => new ResourceAmount(group.Key, group.Sum(amount => amount.Quantity))),
    ];
}
