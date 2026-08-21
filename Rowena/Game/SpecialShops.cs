using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Rowena.Core.Conversions;

namespace Rowena.Game;

/// <summary>
/// Every fixed-rate exchange the game's shop sheets publish, as conversions.
/// </summary>
/// <remarks>
/// The hand-written catalogue proved the model; this is where the coverage comes from. A
/// SpecialShop entry is costs in and rewards out at a fixed rate, which is precisely a
/// <see cref="Conversion"/>, and the sheets carry every scrip exchange, token counter,
/// tomestone vendor and hunt shop in the game. Reading them beats maintaining a file that
/// goes stale every patch.
///
/// Only shops that are actually offered somewhere: the sheet keeps every shop that ever
/// existed, and a retired tomestone exchange would otherwise come back as a trade priced
/// in a currency the counter no longer takes. Offered means reachable from an NPC's data,
/// or listed in an InclusionShopSeries, which is how the scrip exchanges are wired to
/// their vendors.
///
/// Costs are encoded three ways, and the encoding was pinned against known counters rather
/// than guessed: a cost type of 0 names an item id directly, 2 is a slot in the live
/// tomestone table, and 3 is a scrip slot. The scrip slots are a fixed map because the
/// sheets do not publish one; the Orange Scrip Exchange charging slot 7 for a Mount Token
/// that costs orange gatherers' scrips at the counter is what identifies it. Cost type 1
/// is an item demanded in high quality, which cannot be priced off an NQ book, so those
/// entries are skipped and counted.
///
/// An untradable cost is a currency in this model's sense, whatever the game calls it: it
/// cannot be bought, only earned and spent, and the wallet knows how many you hold. Gil
/// costs are skipped; buying from a vendor for gil is a different question with its own
/// arithmetic. Grand company seal shops live in their own sheet with the same shape, and
/// the rank requirement is deliberately ignored: it gates once per character, not per
/// trade.
/// </remarks>
internal sealed class SpecialShops(IDataManager data, Vendors vendors, IPluginLog log)
{
    private readonly Dictionary<string, IReadOnlyList<Spot>> spots = new(StringComparer.Ordinal);

    /// <summary>Where a generated trade's counter stands; empty for one nobody offers at a known spot.</summary>
    public IReadOnlyList<Spot> Where(string conversionId) =>
        spots.TryGetValue(conversionId, out var found) ? found : [];

    /// <summary>Grand company row to its seal: Maelstrom, Twin Adder, Immortal Flames.</summary>
    private static readonly Dictionary<uint, uint> SealByCompany = new()
    {
        [1] = 20,
        [2] = 21,
        [3] = 22,
    };

    /// <summary>
    /// Cost type 3 slots to scrip items: purple crafters', purple gatherers', orange
    /// crafters', orange gatherers'.
    /// </summary>
    /// <remarks>
    /// The even-numbered gaps are the retired tiers; a new expansion's scrips will need
    /// rows 8 and 9, and an unknown slot is skipped and logged rather than misread.
    /// </remarks>
    private static readonly Dictionary<uint, uint> ScripBySlot = new()
    {
        [2] = 33913,
        [4] = 33914,
        [6] = 41784,
        [7] = 41785,
    };

    private IReadOnlyList<Conversion>? cached;

    /// <summary>Built once. The sheets do not change while the game is running.</summary>
    public IReadOnlyList<Conversion> Trades() => cached ??= Build();

    private IReadOnlyList<Conversion> Build()
    {
        var items = data.GetExcelSheet<Item>();
        var tomestoneBySlot = data.GetExcelSheet<TomestonesItem>()
            .Where(row => row.Tomestones.RowId != 0)
            .ToDictionary(row => row.Tomestones.RowId, row => row.Item.RowId);

        var conversions = new List<Conversion>();
        var skippedHq = 0;
        var skippedUnknownSlot = 0;

        var offered = Offered();

        foreach (var shop in data.GetExcelSheet<SpecialShop>())
        {
            if (!offered.TryGetValue(shop.RowId, out var vendor))
                continue;

            // The counter named for who stands at it and where, when the world says; the
            // shop's own name only when it does not, since "Allagan Tomestones of Mathematics
            // (Other)" is the one string here nobody can act on.
            var placed = vendors.ForShop(shop.RowId);
            var shopName = shop.Name.ExtractText();
            var venue = placed.Count > 0
                ? $"{placed[0].Npc}, {placed[0].Zone}"
                : string.IsNullOrWhiteSpace(shopName) ? vendor : shopName;
            var index = 0;

            foreach (var entry in shop.Item)
            {
                index++;

                var receives = entry.ReceiveItems
                    .Where(receive => receive.Item.RowId != 0 && receive.ReceiveCount > 0)
                    .ToArray();
                var costs = entry.ItemCosts
                    .Where(cost => cost.ItemCost.RowId != 0 && cost.CurrencyCost > 0)
                    .ToArray();

                if (receives.Length == 0 || costs.Length == 0)
                    continue;

                // A reward demanded back in high quality, or handed over as one, is not
                // this model's trade: NQ books price neither.
                if (receives.Any(receive => receive.ReceiveHq))
                    continue;

                if (costs.Any(cost => cost.CollectabilityCost > 0))
                    continue;

                if (costs.Any(cost => cost.CostType == 1))
                {
                    skippedHq++;
                    continue;
                }

                if (!receives.All(receive => Marketable(items, receive.Item.RowId)))
                    continue;

                var inputs = new List<ResourceAmount>(costs.Length);

                foreach (var cost in costs)
                {
                    var resolved = cost.CostType switch
                    {
                        0 => cost.ItemCost.RowId,
                        2 => tomestoneBySlot.GetValueOrDefault(cost.ItemCost.RowId),
                        3 => ScripBySlot.GetValueOrDefault(cost.ItemCost.RowId),
                        _ => 0u,
                    };

                    // 1 is gil: a vendor purchase, not an exchange.
                    if (resolved is 0 or 1)
                    {
                        inputs = null;
                        if (cost.CostType is 2 or 3)
                            skippedUnknownSlot++;
                        break;
                    }

                    var name = Name(items, resolved);
                    inputs.Add(new ResourceAmount(
                        Marketable(items, resolved)
                            ? Resource.Item(resolved, name)
                            : Resource.Currency(resolved, name),
                        (int)cost.CurrencyCost));
                }

                if (inputs is null)
                    continue;

                var outputs = receives
                    .Select(receive => new ResourceAmount(
                        Resource.Item(receive.Item.RowId, Name(items, receive.Item.RowId)),
                        (int)receive.ReceiveCount))
                    .ToArray();

                var id = $"shop-{shop.RowId}-{index}";
                spots[id] = placed;

                conversions.Add(new Conversion(
                    id,
                    string.Join(" + ", outputs.Select(output => output.Resource.Name)),
                    inputs,
                    outputs,
                    venue));
            }
        }

        conversions.AddRange(GrandCompanyTrades(items));

        log.Information(
            $"Read {conversions.Count} exchanges from the shop sheets"
            + (skippedHq > 0 ? $", {skippedHq} skipped for HQ costs" : "")
            + (skippedUnknownSlot > 0 ? $", {skippedUnknownSlot} skipped for unknown currency slots" : "")
            + ".");

        return conversions;
    }

    /// <summary>Seal shops, which have their own sheet keyed by category rather than shop.</summary>
    private IEnumerable<Conversion> GrandCompanyTrades(Lumina.Excel.ExcelSheet<Item> items)
    {
        var categories = data.GetExcelSheet<GCScripShopCategory>();

        foreach (var row in data.GetSubrowExcelSheet<GCScripShopItem>().Flatten())
        {
            if (row.Item.RowId == 0 || row.CostGCSeals == 0 || !Marketable(items, row.Item.RowId))
                continue;

            if (categories.GetRowOrDefault(row.RowId)?.GrandCompany is not { RowId: not 0 } company
                || !SealByCompany.TryGetValue(company.RowId, out var seal))
                continue;

            var name = Name(items, row.Item.RowId);
            var id = $"gcshop-{row.RowId}-{row.SubrowId}";
            var placed = vendors.ForGrandCompany(company.RowId);
            spots[id] = placed;

            yield return new Conversion(
                id,
                name,
                [new ResourceAmount(Resource.Currency(seal, Name(items, seal)), (int)row.CostGCSeals)],
                [new ResourceAmount(Resource.Item(row.Item.RowId, name), 1)],
                placed.Count > 0
                    ? $"{placed[0].Npc}, {placed[0].Zone}"
                    : $"{company.ValueNullable?.Name.ExtractText() ?? "grand company"} quartermaster");
        }
    }

    /// <summary>
    /// The shops somewhere in the world actually offers, with who offers them.
    /// </summary>
    /// <remarks>
    /// Two routes: named directly in an NPC's data, or listed in an InclusionShopSeries,
    /// the category-picker interface the scrip and tomestone exchanges sit behind. The
    /// series route has no single NPC to name, which is fine: those shops carry real
    /// names of their own.
    /// </remarks>
    private Dictionary<uint, string> Offered()
    {
        var shopIds = new HashSet<uint>();
        foreach (var shop in data.GetExcelSheet<SpecialShop>())
            shopIds.Add(shop.RowId);

        var offered = new Dictionary<uint, string>();
        var residents = data.GetExcelSheet<ENpcResident>();

        foreach (var npc in data.GetExcelSheet<ENpcBase>())
        {
            foreach (var dataId in npc.ENpcData)
            {
                if (!shopIds.Contains(dataId.RowId) || offered.ContainsKey(dataId.RowId))
                    continue;

                offered[dataId.RowId] =
                    residents.GetRowOrDefault(npc.RowId)?.Singular.ExtractText() ?? "special shop";
            }
        }

        foreach (var row in data.GetSubrowExcelSheet<InclusionShopSeries>().Flatten())
        {
            if (shopIds.Contains(row.SpecialShop.RowId))
                offered.TryAdd(row.SpecialShop.RowId, "special shop");
        }

        return offered;
    }

    private static bool Marketable(Lumina.Excel.ExcelSheet<Item> items, uint id) =>
        items.GetRowOrDefault(id) is { } item && item.ItemSearchCategory.RowId > 0;

    private static string Name(Lumina.Excel.ExcelSheet<Item> items, uint id)
    {
        var name = items.GetRowOrDefault(id)?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"item {id}" : name;
    }
}
