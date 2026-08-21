using Rowena.Core.Conversions;

namespace Rowena.UI;

/// <summary>
/// The catalogue, hand-written and generated together, sliced the ways the window reads it.
/// </summary>
/// <remarks>
/// Computed at load rather than per rebuild, and in one place rather than in each view that
/// wants a slice of it. Two views deriving "which currencies can be spent" separately is how
/// they end up disagreeing about what you are holding.
///
/// The generated trades come from the shop sheets and do not change while the game runs; the
/// file's trades win wherever the two describe the same exchange, since the file is where
/// venues and handoffs get written by hand. Reloading the catalogue swaps every slice in one
/// <see cref="Replace"/>, so no view can see the old trades next to the new currencies. The
/// views read these per rebuild and pick the change up on their own clock.
/// </remarks>
internal sealed class Trades
{
    private readonly IReadOnlyList<Conversion> generated;

    private HashSet<string> handIds = [];
    private HashSet<Resource> watched = [];

    public Trades(ConversionCatalog catalog, IReadOnlyList<Conversion> generated)
    {
        this.generated = generated;
        Replace(catalog);
    }

    /// <summary>
    /// Whether a currency is one the file names, as opposed to one the sheets know about.
    /// </summary>
    /// <remarks>
    /// The file is a declaration of interest, so its currencies stay on screen even at a
    /// balance of zero: "is it worth going to earn scrips" is a question asked precisely
    /// when you have none. The generated catalogue declares nothing, it merely knows, and
    /// its hundred and fifty currencies earn a place only by being in your pockets.
    /// </remarks>
    public bool IsWatched(Resource currency) => watched.Contains(currency);

    /// <summary>Every trade there is, for the questions that want all of them.</summary>
    public IReadOnlyList<Conversion> All { get; private set; } = [];

    /// <summary>Bound currencies that something in the catalogue will take.</summary>
    public Resource[] Currencies { get; private set; } = [];

    /// <summary>Trades with no bound currency in them: pure gil in, more gil out, no gameplay.</summary>
    public Conversion[] Flips { get; private set; } = [];

    /// <summary>Swaps in a freshly loaded catalogue, all slices at once.</summary>
    public void Replace(ConversionCatalog catalog)
    {
        All = CatalogMerge.Merge(catalog.Conversions, generated);

        handIds = [.. catalog.Conversions.Select(conversion => conversion.Id)];

        watched =
        [
            .. catalog.Conversions
                .SelectMany(conversion => conversion.Inputs)
                .Select(amount => amount.Resource)
                .Where(resource => resource.Kind == ResourceKind.Currency),
        ];

        Currencies =
        [
            .. All
                .SelectMany(conversion => conversion.Inputs)
                .Select(amount => amount.Resource)
                .Where(resource => resource.Kind == ResourceKind.Currency)
                .Distinct(),
        ];

        Flips =
        [
            .. All.Where(conversion =>
                conversion.Inputs.All(input => input.Resource.Kind == ResourceKind.Item)),
        ];
    }

    /// <summary>
    /// The items worth fetching prices for, split by the board each belongs to.
    /// </summary>
    /// <remarks>
    /// Not every item in the catalogue: with the generated trades in, that is a thousand ids
    /// and a fetch measured in minutes. A trade is worth pricing when you could actually run
    /// it, which for a flip is always and for anything spending a bound currency means
    /// holding some of every currency it wants. Hand-written trades are priced regardless,
    /// since the file's currencies stay on screen at zero. The trades this skips are exactly
    /// the ones the window does not show.
    ///
    /// Inputs and outputs are reported separately because they are priced on different
    /// boards; an item on both sides is in both lists, fetched twice, which is correct
    /// rather than wasteful.
    /// </remarks>
    public (uint[] Bought, uint[] Sold) Relevant(Func<Resource, long> held)
    {
        var runnable = All
            .Where(conversion => handIds.Contains(conversion.Id)
                || conversion.Inputs
                    .Where(input => input.Resource.Kind == ResourceKind.Currency)
                    .All(input => held(input.Resource) > 0))
            .ToArray();

        return (ItemsOn(runnable, conversion => conversion.Inputs),
            ItemsOn(runnable, conversion => conversion.Outputs));
    }

    private static uint[] ItemsOn(
        IReadOnlyList<Conversion> conversions,
        Func<Conversion, IReadOnlyList<ResourceAmount>> side) =>
    [
        .. conversions
            .SelectMany(side)
            .Select(amount => amount.Resource)
            .Where(resource => resource.Kind == ResourceKind.Item)
            .Select(resource => resource.Id)
            .Distinct(),
    ];
}
