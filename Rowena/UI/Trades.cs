using Rowena.Core.Conversions;

namespace Rowena.UI;

/// <summary>
/// The catalogue, sliced the four ways the window reads it.
/// </summary>
/// <remarks>
/// Computed at load rather than per rebuild, and in one place rather than in each view that
/// wants a slice of it. Two views deriving "which currencies can be spent" separately is how
/// they end up disagreeing about what you are holding.
///
/// Reloading the catalogue swaps every slice in one <see cref="Replace"/>, so no view can
/// see the old trades next to the new currencies. The views read these per rebuild and pick
/// the change up on their own clock.
/// </remarks>
internal sealed class Trades
{
    public Trades(ConversionCatalog catalog) => Replace(catalog);

    /// <summary>Every trade there is, for the questions that want all of them.</summary>
    public IReadOnlyList<Conversion> All { get; private set; } = [];

    /// <summary>
    /// Items on the input side: the ones you would be buying.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Sold"/> because they are priced on different boards. An item appearing on
    /// both sides is fetched twice, once for each, which is correct rather than wasteful: they are two
    /// different numbers.
    /// </remarks>
    public uint[] Bought { get; private set; } = [];

    /// <summary>Items on the output side: the ones you would be selling.</summary>
    public uint[] Sold { get; private set; } = [];

    /// <summary>Bound currencies that something in the catalogue will take.</summary>
    public Resource[] Currencies { get; private set; } = [];

    /// <summary>Trades with no bound currency in them: pure gil in, more gil out, no gameplay.</summary>
    public Conversion[] Flips { get; private set; } = [];

    /// <summary>Swaps in a freshly loaded catalogue, all slices at once.</summary>
    public void Replace(ConversionCatalog catalog)
    {
        All = catalog.Conversions;
        Bought = ItemsOn(catalog, conversion => conversion.Inputs);
        Sold = ItemsOn(catalog, conversion => conversion.Outputs);

        Currencies =
        [
            .. catalog.Conversions
                .SelectMany(conversion => conversion.Inputs)
                .Select(amount => amount.Resource)
                .Where(resource => resource.Kind == ResourceKind.Currency)
                .Distinct(),
        ];

        Flips =
        [
            .. catalog.Conversions
                .Where(conversion => conversion.Inputs.All(input => input.Resource.Kind == ResourceKind.Item)),
        ];
    }

    private static uint[] ItemsOn(
        ConversionCatalog catalog,
        Func<Conversion, IReadOnlyList<ResourceAmount>> side) =>
    [
        .. catalog.Conversions
            .SelectMany(side)
            .Select(amount => amount.Resource)
            .Where(resource => resource.Kind == ResourceKind.Item)
            .Select(resource => resource.Id)
            .Distinct(),
    ];
}
