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

    public long For(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var known))
            return known;

        var price = data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.PriceLow ?? 0u;
        cache[itemId] = price;
        return price;
    }
}
