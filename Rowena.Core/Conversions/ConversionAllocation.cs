using Rowena.Core.Market;

namespace Rowena.Core.Conversions;

/// <summary>How many times to run one conversion, and what that costs and pays.</summary>
/// <param name="DaysToAbsorb">
/// How long the selling board takes to digest what the allocated runs produce, counting
/// every allocation that sells into the same book: two trades dumping into one market are
/// one queue, and a trade is not closed until its part of the queue has sold. Null when
/// nothing was allocated, or when nothing it sells has a sale rate.
/// </param>
public sealed record Allocation(
    Conversion Conversion,
    int Runs,
    long GilOutlay,
    long Profit,
    double? DaysToAbsorb)
{
    public double? ReturnOnOutlay => GilOutlay == 0 ? null : (double)Profit / GilOutlay;
}

/// <summary>
/// Splits a shared order book between conversions that compete for it.
/// </summary>
/// <remarks>
/// Sizing each conversion on its own overstates every one of them, and does it in a way that
/// reads as opportunity rather than as a warning. Two trades that both want a hundred Mount
/// Tokens will each report that the book can supply them, when between them the book supplies
/// one. Acting on both is a mistake the numbers invited.
///
/// Allocated greedily: repeatedly commit whichever single run pays the most against what is
/// left of the book. Buying deeper only ever costs more, so marginal profit never rises, and
/// for that shape greedy is not an approximation. In practice this means the better trade
/// takes the whole book rather than the two splitting it, which is the right answer and not an
/// obvious one.
///
/// Outputs stay valued at the current floor, as everywhere else here, so selling three of
/// something assumes three sales at today's price. That optimism is deliberate and lives
/// opposite <see cref="ConversionQuote.DaysToAbsorb"/> rather than being hidden in the price.
/// </remarks>
public static class ConversionAllocation
{
    /// <summary>
    /// Allocates <paramref name="gilBudget"/> across whichever conversions buy their inputs.
    /// </summary>
    /// <remarks>
    /// Conversions with no item inputs are left out. Allocation is about dividing a market up,
    /// and a trade that spends only bound currency is not competing for anything on it. It also
    /// costs no gil, so it would stay profitable forever and eat the cap for nothing.
    /// </remarks>
    public static IReadOnlyList<Allocation> Allocate(
        IReadOnlyList<Conversion> conversions,
        Func<uint, OrderBook?> books,
        MarketTax tax,
        long gilBudget,
        int capPerConversion,
        double? sellingHorizonDays = null,
        Func<uint, long>? vendor = null) =>
        Allocate(conversions, books, books, tax, gilBudget, capPerConversion, sellingHorizonDays, vendor);

    /// <summary>
    /// Allocates where buying and selling happen on different boards.
    /// </summary>
    /// <param name="sellingHorizonDays">
    /// How many days of selling a run is allowed to need. A run is only committed if the queue it
    /// joins on the selling board, counting every allocation into the same book, would still clear
    /// within this many days at the observed rate. Null means no limit, which is the sizing
    /// question on its own: how many before the book eats the margin. A horizon makes it the
    /// practical one: how many before you are sitting on stock. Nothing selling means nothing
    /// allocated, since no number of days clears a queue that never moves.
    /// </param>
    /// <remarks>
    /// The buying side is consumed as the allocation proceeds. The selling side is not repriced:
    /// outputs are valued at the floor throughout, the same deliberate optimism as everywhere
    /// else, with the pessimism kept in absorption, which the horizon turns from a warning into
    /// a limit on volume. A volume limit rather than a price discount, so the price stays honest
    /// and what gives is how much of it you get to sell.
    /// </remarks>
    public static IReadOnlyList<Allocation> Allocate(
        IReadOnlyList<Conversion> conversions,
        Func<uint, OrderBook?> buying,
        Func<uint, OrderBook?> selling,
        MarketTax tax,
        long gilBudget,
        int capPerConversion,
        double? sellingHorizonDays = null,
        Func<uint, long>? vendor = null)
    {
        var competing = conversions
            .Where(conversion => conversion.Inputs.Any(input => input.Resource.Kind == ResourceKind.Item))
            .ToArray();

        var runs = new Dictionary<string, int>(StringComparer.Ordinal);
        var outlay = new Dictionary<string, long>(StringComparer.Ordinal);
        var profit = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var conversion in competing)
        {
            runs[conversion.Id] = 0;
            outlay[conversion.Id] = 0;
            profit[conversion.Id] = 0;
        }

        // What is still for sale after everything committed so far.
        var left = new Dictionary<uint, OrderBook>();

        OrderBook? Remaining(uint itemId)
        {
            if (left.TryGetValue(itemId, out var book))
                return book;

            if (buying(itemId) is not { } original)
                return null;

            left[itemId] = original;
            return original;
        }

        var budget = gilBudget;

        // What the allocated runs will dump on the selling board, queued per item. Absorption
        // is read against the whole queue, not each row's own contribution: the second trade
        // into a book waits behind the first whichever order they sell in. Kept as the loop
        // goes, because the horizon has to see the queue a run would join before it is committed.
        var queued = new Dictionary<uint, int>();

        bool ClearsInTime(Conversion conversion)
        {
            if (sellingHorizonDays is not { } horizon)
                return true;

            foreach (var output in ItemOutputs(conversion))
            {
                var book = selling(output.Resource.Id);

                // A vendor takes any amount on the spot; only the board has a queue.
                if (VendorFloor.Beats(book, vendor?.Invoke(output.Resource.Id) ?? 0, tax))
                    continue;

                var after = queued.GetValueOrDefault(output.Resource.Id) + output.Quantity;

                if (book?.DaysToAbsorb(after) is not { } days || days > horizon)
                    return false;
            }

            return true;
        }

        // What one more run of each conversion would pay, against the book as it stands. Kept
        // between rounds because committing a run only changes the answer for conversions that
        // wanted some of the same item: with two and a half thousand exchanges in the catalogue,
        // re-pricing all of them every round was tens of thousands of book walks inside a frame,
        // and a redraw somebody could feel.
        var quotes = new Dictionary<string, ConversionQuote?>(StringComparer.Ordinal);

        ConversionQuote? QuoteFor(Conversion conversion)
        {
            if (quotes.TryGetValue(conversion.Id, out var cached))
                return cached;

            var quote = ConversionEvaluator.Evaluate(conversion, 1, Remaining, selling, tax, vendor);
            var usable = quote.IsExecutable && quote.Profit > 0 ? quote : null;

            quotes[conversion.Id] = usable;
            return usable;
        }

        while (true)
        {
            Conversion? best = null;
            ConversionQuote? bestQuote = null;

            foreach (var conversion in competing)
            {
                if (runs[conversion.Id] >= capPerConversion || !ClearsInTime(conversion))
                    continue;

                // Priced against what is left rather than the whole book, and only re-priced
                // when something it buys has been bought out from under it.
                if (QuoteFor(conversion) is not { } quote || quote.GilOutlay > budget)
                    continue;

                if (bestQuote is null || quote.Profit > bestQuote.Profit)
                {
                    best = conversion;
                    bestQuote = quote;
                }
            }

            if (best is null || bestQuote is null)
                break;

            var consumed = new HashSet<uint>();

            foreach (var input in best.Inputs.Where(input => input.Resource.Kind == ResourceKind.Item))
            {
                consumed.Add(input.Resource.Id);

                if (Remaining(input.Resource.Id) is { } book)
                    left[input.Resource.Id] = book.WithoutCheapest(input.Quantity);
            }

            // Only the conversions that wanted some of what was just taken are worth pricing
            // again. Everything else faces exactly the book it faced a moment ago.
            foreach (var conversion in competing)
            {
                if (conversion.Inputs.Any(input =>
                        input.Resource.Kind == ResourceKind.Item && consumed.Contains(input.Resource.Id)))
                {
                    quotes.Remove(conversion.Id);
                }
            }

            foreach (var output in ItemOutputs(best))
                queued[output.Resource.Id] = queued.GetValueOrDefault(output.Resource.Id) + output.Quantity;

            runs[best.Id]++;
            outlay[best.Id] += bestQuote.GilOutlay;
            profit[best.Id] += bestQuote.Profit;
            budget -= bestQuote.GilOutlay;
        }

        double? Absorb(Conversion conversion)
        {
            if (runs[conversion.Id] == 0)
                return null;

            double? worst = null;

            foreach (var output in ItemOutputs(conversion))
            {
                var book = selling(output.Resource.Id);

                if (VendorFloor.Beats(book, vendor?.Invoke(output.Resource.Id) ?? 0, tax))
                {
                    worst ??= 0d;
                    continue;
                }

                if (book?.DaysToAbsorb(queued[output.Resource.Id]) is { } days)
                    worst = Math.Max(worst ?? 0d, days);
            }

            return worst;
        }

        return
        [
            .. competing.Select(conversion => new Allocation(
                conversion,
                runs[conversion.Id],
                outlay[conversion.Id],
                profit[conversion.Id],
                Absorb(conversion))),
        ];
    }

    private static IEnumerable<ResourceAmount> ItemOutputs(Conversion conversion) =>
        conversion.Outputs.Where(output => output.Resource.Kind == ResourceKind.Item);
}
