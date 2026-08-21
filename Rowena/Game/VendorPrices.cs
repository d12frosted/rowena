using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>
/// What a vendor pays for an item, from the Item sheet.
/// </summary>
/// <remarks>
/// The sheet calls it PriceLow, as against the PriceMid a vendor charges. It is the floor
/// under every sale: the one buyer who never undercuts and never runs out of appetite.
/// Cached per id because the evaluator asks for it on every rebuild.
/// </remarks>
internal sealed class VendorPrices(IDataManager data)
{
    private readonly Dictionary<uint, long> cache = [];

    private IReadOnlyList<uint>? sellable;

    /// <summary>
    /// Every item the board trades and a vendor will buy, for the scan to survey.
    /// </summary>
    /// <remarks>
    /// Marketable is the same test the shop walk uses: a search category is what puts an item
    /// on the board at all. A price of zero is the sheet saying no vendor takes it, which is
    /// the only "unsellable" signal this Lumina exposes; a hundred and fifty marketable items
    /// are in that state and are simply not candidates.
    /// </remarks>
    public IReadOnlyList<uint> Sellable() => sellable ??=
    [
        .. data.GetExcelSheet<Item>()
            .Where(item => item.ItemSearchCategory.RowId > 0 && item.PriceLow > 0)
            .Select(item => item.RowId),
    ];

    public long For(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var known))
            return known;

        var price = data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.PriceLow ?? 0u;
        cache[itemId] = price;
        return price;
    }
}
