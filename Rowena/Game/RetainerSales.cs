using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Rowena.Core.Market;

namespace Rowena.Game;

/// <summary>
/// Sales nobody was there to hear announced.
/// </summary>
/// <remarks>
/// The game says a retainer sold something in chat, and only while you are logged in to hear
/// it. Everything that sells overnight is silent, which made the record of my own sales a
/// record of the hours I happened to be online.
///
/// So the slots are read instead. A retainer's market listings are an ordinary inventory
/// container of twenty, and the prices sit beside them in the same manager, so opening a
/// retainer is a complete statement of what it is holding. Compare that against how it was
/// left and the difference is what went.
///
/// What the difference cannot say is why it went, and the purse can: a listing that sold and
/// a listing that was taken off the market look identical from the slots. Gil arriving is what
/// separates them, which is the trick AllaganMarket uses and the reason this reads the purse
/// at all.
///
/// Only ever runs with a retainer open, which is the only moment any of it is readable.
/// </remarks>
internal sealed class RetainerSales : IDisposable
{
    /// <summary>A retainer's market list is twenty slots, always.</summary>
    private const int Slots = 20;

    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly SalesLog sales;
    private readonly Func<uint, MarketTax> taxFor;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private DateTime nextAt;

    public RetainerSales(
        IFramework framework,
        Configuration config,
        SalesLog sales,
        Func<uint, MarketTax> taxFor,
        Action save,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.framework = framework;
        this.config = config;
        this.sales = sales;
        this.taxFor = taxFor;
        this.save = save;
        this.diagnostics = diagnostics;
        this.log = log;

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;

        try
        {
            Reconcile();
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not reconcile a retainer's sales.");
        }
    }

    /// <summary>
    /// Reads the open retainer, and books whatever went since the last look.
    /// </summary>
    /// <remarks>
    /// A first look at a retainer books nothing. There is nothing to compare against, and
    /// treating an unseen retainer as one that has just sold everything in it would turn the
    /// first visit into a windfall.
    /// </remarks>
    private unsafe void Reconcile()
    {
        var retainers = RetainerManager.Instance();
        var inventory = InventoryManager.Instance();

        if (retainers is null || inventory is null)
            return;

        var active = retainers->GetActiveRetainer();

        if (active is null || active->RetainerId == 0)
            return;

        var container = inventory->GetInventoryContainer(InventoryType.RetainerMarket);

        if (container is null || !container->IsLoaded)
            return;

        var now = new List<MarketSlot>(Slots);

        for (var slot = 0; slot < Slots && slot < container->Size; slot++)
        {
            var item = container->GetInventorySlot(slot);

            now.Add(item is null || item->ItemId == 0
                ? default
                : new MarketSlot(
                    item->ItemId,
                    item->Quantity,
                    (long)inventory->RetainerMarketPrices[slot],
                    item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)));
        }

        var gil = (long)inventory->GetRetainerGil();
        var id = active->RetainerId;
        var seen = Stored(id);

        if (seen is { } last)
            Book(id, last, now, gil, (uint)active->Town);

        Remember(id, now, gil);
    }

    /// <summary>Turns what went into sales, and says so.</summary>
    private void Book(ulong id, StoredRetainer last, IReadOnlyList<MarketSlot> now, long gil, uint town)
    {
        // Everything chat already said since this retainer was last looked at. Without it the
        // slots book those sales a second time and the takings quietly double.
        var told = sales.AnnouncedSince(DateTimeOffset.FromUnixTimeSeconds(last.SeenAt));

        var found = SaleReconciliation.Between(
            [.. last.Slots.Select(slot => new MarketSlot(slot.ItemId, slot.Quantity, slot.UnitPrice, slot.IsHq))],
            now,
            gil - last.Gil,
            taxFor(town),
            told);

        foreach (var sale in found)
            sales.Record(sale.ItemId, sale.Quantity, sale.Net, announced: false);

        if (found.Count > 0)
        {
            diagnostics.Note(
                "sales",
                $"retainer {id % 10_000}: {found.Count} sales found from the slots, "
                + $"{found.Sum(sale => sale.Net):N0} gil");
        }
    }

    private StoredRetainer? Stored(ulong id) =>
        config.Retainers.FirstOrDefault(stored => stored.RetainerId == id);

    /// <summary>Leaves the slots and the purse as they stand, to compare against next time.</summary>
    private void Remember(ulong id, IReadOnlyList<MarketSlot> now, long gil)
    {
        config.Retainers.RemoveAll(stored => stored.RetainerId == id);

        config.Retainers.Add(new StoredRetainer
        {
            RetainerId = id,
            Gil = gil,
            SeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Slots =
            [
                .. now.Select(slot => new StoredSlot
                {
                    ItemId = slot.ItemId,
                    Quantity = slot.Quantity,
                    UnitPrice = slot.UnitPrice,
                    IsHq = slot.IsHq,
                }),
            ],
        });

        save();
    }
}
