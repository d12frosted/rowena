using Dalamud.Plugin.Services;
using Rowena.Core.Conversions;
using Rowena.Core.Market;
using Rowena.Game;

namespace Rowena.Market;

/// <summary>
/// Prices the furnishing market in two passes so the second one only pays for candidates worth
/// costing.
/// </summary>
/// <remarks>
/// A naive sweep would price every furnishing and every ingredient of every furnishing, which at
/// twenty ids per request is a great many requests for a great many rows that were never going
/// to be worth crafting.
///
/// So: price the products first, then shortlist on revenue potential, the product's price times
/// how fast it sells, and cost only those. The filter is sound rather than merely cheap. You
/// cannot earn more from an item in a day than the whole board turns over in it, so anything
/// filtered out here could not have led the ranking however cheap its materials turned out to be.
///
/// The sheets are read on this thread rather than the framework one, following the same
/// reasoning as AutoKill's mob index: it is file-backed reference data, not client memory, and
/// walking every recipe row has no business blocking a frame.
/// </remarks>
internal sealed class FurnishingSweep(Furnishings furnishings, MarketCache market, IPluginLog log)
{
    public enum Phase
    {
        Idle,
        Products,
        Shortlisting,
        Ingredients,
        Ready,
        Failed,
    }

    public Phase State { get; private set; } = Phase.Idle;

    /// <summary>Where the sweep has got to, for showing rather than for logic.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>Every craftable tradable furnishing found in the sheets.</summary>
    public int Candidates { get; private set; }

    /// <summary>The ones worth costing, once the products were priced.</summary>
    public IReadOnlyList<Conversion> Shortlist { get; private set; } = [];

    public DateTimeOffset? ReadyAt { get; private set; }

    /// <summary>
    /// The materials standing between the shortlist and a price, commonest first.
    /// </summary>
    /// <remarks>
    /// This exists to settle one question with evidence instead of opinion: whether following
    /// recipes down to raw materials is worth building. Direct-ingredient pricing discards any
    /// furnishing whose materials are not on the board, and the tree would rescue precisely those
    /// whose blocker is itself craftable. If the blockers turn out to be restoration mats, map
    /// drops or untradables, no amount of tree walking helps and the work should not be done.
    /// </remarks>
    public IReadOnlyList<Blocker> Blockers { get; private set; } = [];

    public bool Running => State is Phase.Products or Phase.Shortlisting or Phase.Ingredients;

    /// <param name="Blocks">How many shortlisted furnishings this one material makes unpriceable.</param>
    public sealed record Blocker(string Material, int Blocks);

    public void Start(string? scope, int chunkSize, int shortlistSize)
    {
        if (Running || string.IsNullOrWhiteSpace(scope))
            return;

        State = Phase.Products;
        Detail = "reading the sheets";
        _ = Task.Run(() => Run(scope, chunkSize, shortlistSize));
    }

    private async Task Run(string scope, int chunkSize, int shortlistSize)
    {
        try
        {
            var candidates = furnishings.Craftable();
            Candidates = candidates.Count;

            if (candidates.Count == 0)
            {
                State = Phase.Failed;
                Detail = "no craftable furnishings found in the sheets";
                return;
            }

            var products = candidates
                .SelectMany(conversion => conversion.Outputs)
                .Select(output => output.Resource.Id)
                .Distinct()
                .ToArray();

            State = Phase.Products;
            await Price(scope, products, chunkSize, "products").ConfigureAwait(false);

            State = Phase.Shortlisting;
            Detail = "picking what is worth costing";
            var shortlist = Shortlisted(candidates, shortlistSize);

            if (shortlist.Length == 0)
            {
                Shortlist = [];
                State = Phase.Ready;
                ReadyAt = DateTimeOffset.UtcNow;
                Detail = $"none of {Candidates} furnishings are selling on this board";
                return;
            }

            var ingredients = shortlist
                .SelectMany(conversion => conversion.Inputs)
                .Where(input => input.Resource.Kind == ResourceKind.Item)
                .Select(input => input.Resource.Id)
                .Distinct()
                .Where(market.IsStale)
                .ToArray();

            State = Phase.Ingredients;
            await Price(scope, ingredients, chunkSize, "materials").ConfigureAwait(false);

            Shortlist = shortlist;
            Blockers = Blocking(shortlist);
            State = Phase.Ready;
            ReadyAt = DateTimeOffset.UtcNow;
            Detail = $"{shortlist.Length} of {Candidates} costed";

            log.Information($"Furnishing sweep done: {shortlist.Length} of {Candidates} costed.");

            foreach (var blocker in Blockers.Take(15))
                log.Information($"  blocked by {blocker.Material} ({blocker.Blocks})");
        }
        catch (Exception error)
        {
            State = Phase.Failed;
            Detail = error.Message;
            log.Error(error, "Furnishing sweep failed.");
        }
    }

    private async Task Price(string scope, uint[] ids, int chunkSize, string what)
    {
        if (ids.Length == 0)
            return;

        // MarketCache refuses overlapping work, so wait for whatever else is in flight rather
        // than silently skipping the pass.
        while (market.Busy)
            await Task.Delay(250).ConfigureAwait(false);

        Detail = $"pricing {ids.Length} {what}";

        await market
            .PriceAsync(scope, ids, chunkSize, (done, total) => Detail = $"pricing {what}: {done} of {total}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The candidates whose product actually turns over, best revenue first.
    /// </summary>
    /// <remarks>
    /// Revenue potential rather than price. A furnishing listed at four million that sells twice
    /// a year is not a business, and ranking on price alone would put it at the top of the queue
    /// for costing while something that quietly moves thirty a day never got looked at.
    /// </remarks>
    private Conversion[] Shortlisted(IReadOnlyList<Conversion> candidates, int shortlistSize) =>
    [
        .. candidates
            .Select(conversion => new
            {
                Conversion = conversion,
                Revenue = Revenue(conversion),
            })
            .Where(candidate => candidate.Revenue > 0)
            .OrderByDescending(candidate => candidate.Revenue)
            .Take(Math.Max(1, shortlistSize))
            .Select(candidate => candidate.Conversion),
    ];

    /// <summary>
    /// Which materials are doing the blocking, counted across the shortlist.
    /// </summary>
    private Blocker[] Blocking(IReadOnlyList<Conversion> shortlist) =>
    [
        .. shortlist
            .Select(conversion => ConversionEvaluator.Evaluate(conversion, 1, market.Lookup, MarketTax.Standard))
            .Where(quote => !quote.IsExecutable)
            // Unsourced is a material the board could not supply; Unpriced is one nobody lists at
            // all. Both stop a row being costed, and for this question they count the same.
            .SelectMany(quote => quote.Unsourced.Concat(quote.Unpriced))
            .GroupBy(amount => amount.Resource.Name)
            .Select(group => new Blocker(group.Key, group.Count()))
            .OrderByDescending(blocker => blocker.Blocks),
    ];

    private double Revenue(Conversion conversion)
    {
        double total = 0;

        foreach (var output in conversion.Outputs)
        {
            if (market.Book(output.Resource.Id) is not { } book || book.Floor is not { } floor)
                continue;

            total += floor * book.SaleVelocityPerDay * output.Quantity;
        }

        return total;
    }
}
