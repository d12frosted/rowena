using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Rowena.Game;

/// <summary>
/// What each retainer is holding, remembered from the last time I looked at it.
/// </summary>
/// <remarks>
/// Most of a materials pile is not in my bags. AllaganTools answers how many of one item I own
/// and cannot be asked what I am holding, which is the question a full retainer actually poses,
/// so the stock is read from the game instead: a retainer's pages are ordinary inventory
/// containers, readable while that retainer is open.
///
/// Only while it is open, which is why this remembers. A retainer seen an hour ago is the best
/// answer there is about that retainer until it is opened again, and it is a far better answer
/// than pretending it is empty. The age is kept so a view can say how old the answer is rather
/// than quietly presenting it as current.
/// </remarks>
internal sealed class RetainerStock : IDisposable
{
    /// <summary>A retainer's seven pages. Crystals live elsewhere and are not stock.</summary>
    private static readonly InventoryType[] Pages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly Action save;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private DateTime nextAt;

    public RetainerStock(
        IFramework framework,
        Configuration config,
        Action save,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.framework = framework;
        this.config = config;
        this.save = save;
        this.diagnostics = diagnostics;
        this.log = log;

        framework.Update += Tick;
    }

    public void Dispose() => framework.Update -= Tick;

    /// <summary>Everything every remembered retainer holds, folded together by item.</summary>
    public IReadOnlyDictionary<uint, int> Held()
    {
        var held = new Dictionary<uint, int>();

        foreach (var stack in config.RetainerStock.SelectMany(retainer => retainer.Items))
            held[stack.ItemId] = held.GetValueOrDefault(stack.ItemId) + stack.Quantity;

        return held;
    }

    /// <summary>Which retainers hold one item, and how many each has.</summary>
    public IReadOnlyList<(string Retainer, int Quantity)> Where(uint itemId) =>
    [
        .. config.RetainerStock
            .Select(retainer => (
                retainer.Name,
                Quantity: retainer.Items.Where(stack => stack.ItemId == itemId).Sum(stack => stack.Quantity)))
            .Where(entry => entry.Quantity > 0)
            .OrderByDescending(entry => entry.Quantity),
    ];

    /// <summary>How many retainers have been looked at, and how stale the oldest is.</summary>
    public (int Known, DateTimeOffset? Oldest) Seen =>
        config.RetainerStock.Count == 0
            ? (0, null)
            : (config.RetainerStock.Count,
                DateTimeOffset.FromUnixTimeSeconds(config.RetainerStock.Min(retainer => retainer.SeenAt)));

    private void Tick(IFramework _)
    {
        if (DateTime.UtcNow < nextAt)
            return;

        nextAt = DateTime.UtcNow + Every;

        try
        {
            Read();
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not read a retainer's stock.");
        }
    }

    /// <summary>
    /// Reads the open retainer's pages, if one is open and they have loaded.
    /// </summary>
    /// <remarks>
    /// A page that has not loaded is not an empty page. Writing one down as empty would delete
    /// a retainer's worth of stock from the record on the strength of having looked too early.
    /// </remarks>
    private unsafe void Read()
    {
        var retainers = RetainerManager.Instance();
        var inventory = InventoryManager.Instance();

        if (retainers is null || inventory is null)
            return;

        var active = retainers->GetActiveRetainer();

        if (active is null || active->RetainerId == 0)
            return;

        var held = new Dictionary<uint, int>();

        foreach (var page in Pages)
        {
            var container = inventory->GetInventoryContainer(page);

            if (container is null || !container->IsLoaded)
                return;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);

                if (item is null || item->ItemId == 0 || item->Quantity <= 0)
                    continue;

                held[item->ItemId] = held.GetValueOrDefault(item->ItemId) + item->Quantity;
            }
        }

        Remember(active->RetainerId, active->NameString, held);
    }

    private void Remember(ulong id, string name, Dictionary<uint, int> held)
    {
        var stored = new StoredRetainerStock
        {
            RetainerId = id,
            Name = string.IsNullOrWhiteSpace(name) ? "a retainer" : name,
            SeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Items = [.. held.Select(entry => new StoredStack { ItemId = entry.Key, Quantity = entry.Value })],
        };

        var was = config.RetainerStock.FirstOrDefault(retainer => retainer.RetainerId == id);

        // Written only when something moved. This runs twice a second while a retainer is open,
        // and saving the configuration on every tick of that would be a file write per frame for
        // as long as somebody stands there reading their own stock.
        if (was is not null && Same(was, stored))
            return;

        config.RetainerStock.RemoveAll(retainer => retainer.RetainerId == id);
        config.RetainerStock.Add(stored);
        save();

        diagnostics.Note("retainer", $"{stored.Name}: {held.Count} kinds of thing, {held.Values.Sum()} in all");
    }

    private static bool Same(StoredRetainerStock was, StoredRetainerStock now) =>
        was.Items.Count == now.Items.Count
        && was.Items.OrderBy(stack => stack.ItemId)
            .Zip(now.Items.OrderBy(stack => stack.ItemId))
            .All(pair => pair.First.ItemId == pair.Second.ItemId && pair.First.Quantity == pair.Second.Quantity);
}
