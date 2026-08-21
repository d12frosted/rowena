using Rowena.Core.Market;

namespace Rowena.Core.Conversions;

/// <summary>Prices a conversion against a market snapshot.</summary>
public static class ConversionEvaluator
{
    /// <summary>
    /// Values a conversion where buying and selling happen on the same board.
    /// </summary>
    public static ConversionQuote Evaluate(
        Conversion conversion,
        int runs,
        Func<uint, OrderBook?> books,
        MarketTax tax,
        Func<uint, long>? vendor = null) =>
        Evaluate(conversion, runs, books, books, tax, vendor);

    /// <summary>
    /// Values <paramref name="runs"/> repetitions of a conversion.
    /// </summary>
    /// <param name="buying">
    /// Where the inputs come from, or null when an item is not on that board at all. Null and empty
    /// mean different things and the quote reports them differently.
    /// </param>
    /// <param name="selling">
    /// Where the outputs go. Not the same board as <paramref name="buying"/> in general: the market
    /// is per world, so materials can be fetched from anywhere on the data centre by travelling,
    /// while a retainer sells only where it stands. Pricing both together combines the whole data
    /// centre's cheapest listing with the whole data centre's demand, and the second half of that is
    /// badly wrong: measured on Light, a Glade Bench sells for more at home and a tenth as often.
    /// </param>
    /// <param name="vendor">
    /// What a vendor pays for an item, or null to sell only on the board. With it, every
    /// output is worth whichever pays more, the board net of tax or the vendor, and one the
    /// vendor wins is sold the moment it is made. See <see cref="VendorFloor"/>.
    /// </param>
    /// <remarks>
    /// Inputs are priced by walking the book, which is the entire reason this library
    /// exists. Buying a hundred of something is not a hundred times the floor, and the
    /// gap between those two numbers has been the difference between a trade worth doing
    /// and one that is not. The walk charges the buyer's tax as it goes, so the outlay
    /// is gil out of pocket, not the sticker.
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
        Func<uint, OrderBook?> buying,
        Func<uint, OrderBook?> selling,
        MarketTax tax,
        Func<uint, long>? vendor = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);

        var scaled = conversion.Scaled(runs);

        long outlay = 0;
        var currencySpent = new List<ResourceAmount>();
        var unsourced = new List<ResourceAmount>();
        var unseen = new List<ResourceAmount>();

        foreach (var input in scaled.Inputs)
        {
            if (input.Resource.Kind == ResourceKind.Currency)
            {
                currencySpent.Add(input);
                continue;
            }

            var book = buying(input.Resource.Id);
            if (book is null)
            {
                unsourced.Add(input);
                continue;
            }

            var quote = book.CostToBuy(input.Quantity, tax);
            outlay += quote.Total;

            if (quote.IsComplete)
                continue;

            // Short because the board has no more, or short because we were only shown the
            // cheap end of it. The second is not a fact about the market and must not be
            // reported as one: it understates what could be done rather than overstating it,
            // but a wrong answer confidently given is the thing this library exists to avoid.
            if (quote.Uncertain)
                unseen.Add(new ResourceAmount(input.Resource, quote.ShortBy));
            else
                unsourced.Add(new ResourceAmount(input.Resource, quote.ShortBy));
        }

        long gross = 0;
        long net = 0;
        var unpriced = new List<ResourceAmount>();
        var vendored = new List<ResourceAmount>();
        double? daysToAbsorb = null;
        var never = false;

        foreach (var output in scaled.Outputs)
        {
            // Currency out of a conversion is real, but it has no gil price by definition,
            // so it cannot be counted as proceeds. It shows up as a better rate later, when
            // whatever sink consumes it is evaluated in turn.
            if (output.Resource.Kind == ResourceKind.Currency)
                continue;

            var book = selling(output.Resource.Id);
            var sale = VendorFloor.Value(book, vendor?.Invoke(output.Resource.Id) ?? 0, output.Quantity, tax);

            if (sale is not { } sold)
            {
                unpriced.Add(output);
                continue;
            }

            gross += sold.Gross;
            net += sold.Net;

            if (sold.ToVendor)
            {
                // A vendor takes it on the spot. Nothing to wait for, which is not the same as
                // nothing known: zero days, not null.
                vendored.Add(output);
                daysToAbsorb ??= 0d;
                continue;
            }

            // The slowest output governs: the trade is not closed until all of it is sold,
            // and one that never sells is never, whatever the others do.
            if (book!.DaysToAbsorb(output.Quantity) is { } days)
                daysToAbsorb = Math.Max(daysToAbsorb ?? 0d, days);
            else
                never = true;
        }

        if (never)
            daysToAbsorb = null;

        return new ConversionQuote(
            conversion,
            runs,
            outlay,
            gross,
            net,
            Merge(currencySpent),
            Merge(unsourced),
            Merge(unpriced),
            daysToAbsorb,
            Merge(vendored),
            Merge(unseen));
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
        int cap,
        Func<uint, long>? vendor = null) =>
        LargestProfitableSize(conversion, books, books, tax, cap, vendor);

    /// <inheritdoc cref="LargestProfitableSize(Conversion, Func{uint, OrderBook}, MarketTax, int)"/>
    public static int LargestProfitableSize(
        Conversion conversion,
        Func<uint, OrderBook?> buying,
        Func<uint, OrderBook?> selling,
        MarketTax tax,
        int cap,
        Func<uint, long>? vendor = null)
    {
        var best = 0;

        for (var runs = 1; runs <= cap; runs++)
        {
            var quote = Evaluate(conversion, runs, buying, selling, tax, vendor);
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
