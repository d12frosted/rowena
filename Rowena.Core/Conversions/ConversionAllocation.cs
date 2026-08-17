using Rowena.Core.Market;

namespace Rowena.Core.Conversions;

/// <summary>How many times to run one conversion, and what that costs and pays.</summary>
public sealed record Allocation(Conversion Conversion, int Runs, long GilOutlay, long Profit)
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
        int capPerConversion)
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

            if (books(itemId) is not { } original)
                return null;

            left[itemId] = original;
            return original;
        }

        var budget = gilBudget;

        while (true)
        {
            Conversion? best = null;
            ConversionQuote? bestQuote = null;

            foreach (var conversion in competing)
            {
                if (runs[conversion.Id] >= capPerConversion)
                    continue;

                // One more run, priced against what is left rather than the whole book.
                var quote = ConversionEvaluator.Evaluate(conversion, 1, Remaining, tax);

                if (!quote.IsExecutable || quote.Profit <= 0 || quote.GilOutlay > budget)
                    continue;

                if (bestQuote is null || quote.Profit > bestQuote.Profit)
                {
                    best = conversion;
                    bestQuote = quote;
                }
            }

            if (best is null || bestQuote is null)
                break;

            foreach (var input in best.Inputs.Where(input => input.Resource.Kind == ResourceKind.Item))
            {
                if (Remaining(input.Resource.Id) is { } book)
                    left[input.Resource.Id] = book.WithoutCheapest(input.Quantity);
            }

            runs[best.Id]++;
            outlay[best.Id] += bestQuote.GilOutlay;
            profit[best.Id] += bestQuote.Profit;
            budget -= bestQuote.GilOutlay;
        }

        return
        [
            .. competing.Select(conversion => new Allocation(
                conversion,
                runs[conversion.Id],
                outlay[conversion.Id],
                profit[conversion.Id])),
        ];
    }
}
