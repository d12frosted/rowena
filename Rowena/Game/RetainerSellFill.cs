using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Rowena.Core.Market;
using Rowena.Market;
using Rowena.UI;

namespace Rowena.Game;

/// <summary>
/// Reprices a retainer's listings through the game's own windows.
/// </summary>
/// <remarks>
/// A listing's price can only be changed from the retainer, through the dialog the game opens
/// for it, and the way there is three windows long: the sell list's context menu for the
/// slot, "Adjust Price" on that menu, then the dialog with the price field and its confirm.
/// This walks that path the way a hand would, one window at a time, waiting for each to be
/// on screen and ready before sending it the event a click would have. Nothing is sent on a
/// guess: a step whose window does not appear within a moment stops the whole run and says
/// so, rather than sending the event to whatever is open instead.
///
/// The one place Rowena writes into the game's UI, which is why it is one class. Which
/// listing a dialog is about is read off the dialog itself, by matching the price and
/// quantity it opened with against the open retainer's slots; the dialog does not say, and
/// guessing from the name alone mistakes two stacks of the same thing for each other.
///
/// A dialog the player opened by hand on an undercut listing gets the price filled in too,
/// and left for them to confirm; that is the setting, and it does not press confirm.
///
/// Putting something up for sale walks a different path to the same dialog: the item's own menu
/// in the inventory, "Put Up for Sale", then the dialog with a quantity as well as a price. Two
/// things make that safer than it sounds. The menu entry is found by the game's own words for
/// it rather than by counting entries, because "Discard" is on that menu and a mis-counted
/// click is not a harmless miss. And the price is read from the board again immediately before
/// the walk starts, since a number worked out a minute ago is a number somebody has had a
/// minute to undercut.
/// </remarks>
internal sealed class RetainerSellFill : IDisposable
{
    private const string Dialog = "RetainerSell";
    private const string List = "RetainerSellList";
    private const string Menu = "ContextMenu";

    /// <summary>The retainer's own pages, which are a window of their own.</summary>
    private static readonly string[] RetainerBags = ["InventoryRetainer", "InventoryRetainerLarge"];

    /// <summary>The containers something can be put up for sale from.</summary>
    /// <remarks>
    /// Bags and the open retainer's own pages. Not the saddlebag: its window cannot be opened
    /// while a retainer is, so an item in it is not to hand however close it feels.
    /// </remarks>
    private static readonly InventoryType[] Sellable =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>The most entries a context menu is believed to be able to have.</summary>
    /// <remarks>
    /// A sanity bound rather than a game rule. A count outside it means the values are not the
    /// shape this expects, and a menu this does not understand is one it does not click.
    /// </remarks>
    private const int MostEntries = 32;

    /// <summary>Where the two inputs sit in the dialog's node list, and how long that list is.</summary>
    /// <remarks>Checked before touching anything: a dialog with a different shape is not this dialog.</remarks>
    private const int Nodes = 23;
    private const int PriceNode = 15;
    private const int QuantityNode = 11;

    /// <summary>How long a window gets to appear before the run is called off.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>A breath between a window closing and the next event, so the list has caught up.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long the board gets to answer before a listing is called off.
    /// </summary>
    /// <remarks>
    /// Longer than a window takes to open, because this is a request to the game's own server
    /// and it answers about one item a second. Called off rather than fallen back on: a price
    /// nobody has just checked is the thing this step exists to refuse.
    /// </remarks>
    private static readonly TimeSpan Answering = TimeSpan.FromSeconds(15);

    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly Undercutting undercutting;
    private readonly MenuLabels labels;
    private readonly BoardRequests requests;
    private readonly MarketCache market;
    private readonly PricingScope scope;
    private readonly Items names;
    private readonly Notices notices;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Queue<Job> queue = new();
    private Job? current;
    private Step step = Step.Idle;
    private DateTimeOffset askedAt;
    private DateTime deadline;
    private int done;
    private int listed;

    public RetainerSellFill(
        IAddonLifecycle lifecycle,
        IFramework framework,
        Configuration config,
        Undercutting undercutting,
        MenuLabels labels,
        BoardRequests requests,
        MarketCache market,
        PricingScope scope,
        Items names,
        Notices notices,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.config = config;
        this.undercutting = undercutting;
        this.labels = labels;
        this.requests = requests;
        this.market = market;
        this.scope = scope;
        this.names = names;
        this.notices = notices;
        this.diagnostics = diagnostics;
        this.log = log;

        lifecycle.RegisterListener(AddonEvent.PostSetup, Dialog, OnDialog);
        framework.Update += Tick;
    }

    public void Dispose()
    {
        lifecycle.UnregisterListener(AddonEvent.PostSetup, Dialog, OnDialog);
        framework.Update -= Tick;
    }

    /// <summary>Whether a run is in progress, how far along, and whether it is putting things out.</summary>
    public (int Done, int Left, bool Listing)? Running =>
        step == Step.Idle
            ? null
            : (done, queue.Count + (current is null ? 0 : 1), current?.Kind == JobKind.Sell);

    /// <summary>
    /// Whether the retainer's own inventory is the window being looked at.
    /// </summary>
    /// <remarks>
    /// Which of two piles a suggestion should be about. Standing at the sell list, it is what I
    /// am carrying; with the retainer's pages open, it is those, because that is the pile in
    /// front of me and both list in one step from where they are.
    /// </remarks>
    public unsafe bool LookingAtRetainerBags() => RetainerBags.Any(name => Visible(name) is not null);

    /// <summary>The open retainer as last read, if one is open and has been read.</summary>
    public unsafe StoredRetainer? ActiveRetainer()
    {
        var retainers = RetainerManager.Instance();
        var active = retainers is null ? null : retainers->GetActiveRetainer();

        if (active is null || active->RetainerId == 0)
            return null;

        return config.Retainers.FirstOrDefault(retainer => retainer.RetainerId == active->RetainerId);
    }

    /// <summary>
    /// The sell list's order: which row each container slot is shown on.
    /// </summary>
    /// <remarks>
    /// The list is not in slot order and the menu callback wants the row, so this is read off
    /// the list itself: its values carry, per row, the slot that row shows. Empty when the list
    /// is not open.
    /// </remarks>
    public unsafe IReadOnlyDictionary<int, int> ListOrder()
    {
        var list = Visible(List);

        if (list is null || list->AtkValues is null)
            return new Dictionary<int, int>();

        var order = new Dictionary<int, int>();
        var at = FirstRowValue;

        for (var row = 0; row < 20 && at < list->AtkValuesCount; row++, at += RowValueStride)
        {
            var value = list->AtkValues[at];

            if (value.Type == AtkValueType.Int || value.Type == AtkValueType.UInt)
                order.TryAdd(value.Int, row);
        }

        return order;
    }

    /// <summary>Where the sell list's per-row values start, and how many values a row takes.</summary>
    private const int FirstRowValue = 15;
    private const int RowValueStride = 13;

    /// <summary>Whether the retainer's sell list is on screen, and where its right edge is.</summary>
    public unsafe (float X, float Y, float Height)? ListEdge()
    {
        var list = Visible(List);

        if (list is null)
            return null;

        return (list->X + list->GetScaledWidth(true), list->Y, list->GetScaledHeight(true));
    }

    /// <summary>What the open dialog is about, if it is one of my listings and can be told apart.</summary>
    public unsafe MarketSlot? Open()
    {
        var dialog = Visible(Dialog);
        return dialog is null ? null : Identify(dialog);
    }

    /// <summary>Sets the price field of the open dialog, if it is open on this item.</summary>
    public unsafe bool Fill(uint itemId, long price)
    {
        var dialog = Visible(Dialog);

        if (dialog is null || Identify(dialog) is not { } slot || slot.ItemId != itemId)
            return false;

        return Set(dialog, price);
    }

    /// <summary>Queues one listing for repricing. The run starts on the next frame if idle.</summary>
    public unsafe void Reprice(int slot, uint itemId, long price)
    {
        if (slot is < 0 or >= 20 || Visible(List) is null)
            return;

        queue.Enqueue(new Job(slot, itemId, price));
    }

    /// <summary>
    /// Queues one stack to be put up for sale. The price is read from the board when its turn comes.
    /// </summary>
    /// <remarks>
    /// No price is taken here on purpose. Whatever a window was showing when the button was
    /// pressed is what the board said the last time anybody asked, and the whole point of doing
    /// this from a bell is that the board can be asked again, exactly, a second before the
    /// number is typed in.
    /// </remarks>
    /// <param name="fromRetainer">
    /// Which pile the stack was offered from. A thing can sit in both, and listing the copy the
    /// panel was not talking about would put up a different number of them.
    /// </param>
    public unsafe void Sell(uint itemId, int quantity, bool fromRetainer)
    {
        if (quantity <= 0 || Visible(List) is null)
            return;

        queue.Enqueue(new Job(JobKind.Sell, -1, itemId, 0, quantity, fromRetainer));
    }

    /// <summary>
    /// Everything a context menu currently on screen is carrying.
    /// </summary>
    /// <remarks>
    /// For reading rather than for clicking. Where the entries live in a menu's values, and how
    /// they are numbered, is not written down anywhere I can consult: it is a layout, and the
    /// only honest way to learn it is to open a menu by hand and look at what is in it. Guessing
    /// it twice is how a click lands on the wrong entry.
    /// </remarks>
    public unsafe string DumpMenu()
    {
        var lines = new List<string>();

        foreach (var name in (string[])["ContextMenu", "ContextIconMenu", "AddonContextSub", "ContextMenuTitle"])
            lines.Add($"{name}: {(Visible(name) is null ? "not open" : "open")}");

        var menu = Visible(Menu);

        if (menu is null)
            return string.Join("\n", lines);

        lines.Add($"values: {menu->AtkValuesCount}");

        for (var index = 0; index < Math.Min((int)menu->AtkValuesCount, 48); index++)
        {
            var value = menu->AtkValues[index];

            var shown = value.Type switch
            {
                AtkValueType.String or AtkValueType.ManagedString => $"\"{value.String.ToString()}\"",
                AtkValueType.Int => $"{value.Int}",
                AtkValueType.UInt => $"{value.UInt}",
                AtkValueType.Bool => $"{value.Bool}",
                _ => value.Type.ToString(),
            };

            lines.Add($"  [{index}] {value.Type}: {shown}");
        }

        lines.Add($"looking for: {string.Join(" | ", labels.PutUpForSale)}");
        lines.Add($"matched: {(Entry(menu, labels.PutUpForSale) is { } found ? found.ToString() : "nothing")}");

        return string.Join("\n", lines);
    }

    /// <summary>Calls off whatever is queued. The window in front stays as it is.</summary>
    public void Stop()
    {
        queue.Clear();
        current = null;
        step = Step.Idle;
    }

    private void Tick(IFramework _)
    {
        try
        {
            Advance();
        }
        catch (Exception error)
        {
            log.Warning(error, "Repricing failed.");
            Fail("something went wrong, see the log");
        }
    }

    /// <summary>
    /// One step of the walk, if its window is ready.
    /// </summary>
    /// <remarks>
    /// Each step waits for the window it needs to be on screen and ready, then sends one
    /// event and moves on. The dialog step is the one with a decision in it: the dialog is
    /// checked against the listing that was asked for before anything is typed into it.
    /// </remarks>
    private unsafe void Advance()
    {
        if (step != Step.Idle && DateTime.UtcNow > deadline)
        {
            Fail(step switch
            {
                Step.WantMenu => "the listing's menu did not open",
                Step.WantDialog => "the price dialog did not open",
                Step.WantClose => "the price dialog did not close",
                Step.WantPrice => "the board did not answer, so nothing was listed at a stale price",
                Step.WantSaleMenu => "the item's menu did not open",
                Step.WantSaleDialog => "the sale dialog did not open",
                _ => "it stalled",
            });
            return;
        }

        switch (step)
        {
            case Step.Idle:
                if (!queue.TryDequeue(out var next))
                {
                    if (current is not null)
                        Finish();
                    return;
                }

                var list = Visible(List);

                if (list is null)
                {
                    Fail("the sell list is not open");
                    return;
                }

                if (next.Kind == JobKind.Sell)
                {
                    Begin(next);
                    return;
                }

                // Translated at the last moment: the list reorders itself after a change, and
                // the row this slot sat on when the run was queued may not be its row now.
                if (!ListOrder().TryGetValue(next.Slot, out var row))
                {
                    Fail($"{names.Name(next.ItemId)} is not on the list");
                    return;
                }

                current = next;
                AddonCallbacks.Fire(list, 0, row, 1);
                diagnostics.Note("undercut", $"{names.Name(next.ItemId)}: slot {next.Slot}, row {row}");
                Wait(Step.WantMenu);
                break;

            case Step.WantMenu:
                var menu = Visible(Menu);

                if (menu is null || !menu->IsReady)
                    return;

                // The first entry of a listing's menu, which is "Adjust Price". The numbers are
                // the menu's own event arguments, the same ones a click sends.
                AddonCallbacks.Fire(menu, 0, 0, 1020, 0, 0);
                Wait(Step.WantDialog);
                break;

            case Step.WantDialog:
                var dialog = Visible(Dialog);

                if (dialog is null || !dialog->IsReady || Identify(dialog, quiet: true) is not { } slot)
                    return;

                if (slot.ItemId != current!.Value.ItemId)
                {
                    Fail($"the dialog opened on {names.Name(slot.ItemId)}, not {names.Name(current.Value.ItemId)}");
                    return;
                }

                if (!Set(dialog, current.Value.Price))
                {
                    Fail("the price field could not be set");
                    return;
                }

                diagnostics.Note(
                    "undercut",
                    $"{names.Name(slot.ItemId)}: {slot.UnitPrice:N0} -> {current.Value.Price:N0}");

                if (!config.UndercutConfirms)
                {
                    // Left for the player to confirm. The run goes on once they have, or once
                    // they have closed it; either way the dialog going is the signal.
                    deadline = DateTime.MaxValue;
                    step = Step.WantClose;
                    return;
                }

                AddonCallbacks.Fire(dialog, 0);
                Wait(Step.WantClose);
                break;

            case Step.WantClose:
                if (Visible(Dialog) is not null)
                    return;

                done++;

                if (current?.Kind == JobKind.Sell)
                    listed++;
                current = null;
                step = Step.Idle;

                // The list redraws after a change, and the next menu asked for before it has
                // is asked of the old list.
                deadline = DateTime.UtcNow + Settle;
                step = Step.Settling;
                break;

            case Step.Settling:
                if (DateTime.UtcNow >= deadline)
                    step = Step.Idle;
                break;

            case Step.WantPrice:
                Price();
                break;

            case Step.WantSaleMenu:
                var saleMenu = Visible(Menu);

                if (saleMenu is null || !saleMenu->IsReady)
                    return;

                diagnostics.Note("sell", $"the item's menu:\n{DumpMenu()}");

                if (Entry(saleMenu, labels.PutUpForSale) is not { } entry)
                {
                    Fail("the item's menu has no \"put up for sale\" on it");
                    return;
                }

                AddonCallbacks.Fire(saleMenu, 0, entry, 1020, 0, 0);
                Wait(Step.WantSaleDialog);
                break;

            case Step.WantSaleDialog:
                var sale = Visible(Dialog);

                if (sale is null || !sale->IsReady)
                    return;

                if (!Fill(sale, current!.Value))
                {
                    Fail("the sale dialog could not be filled in");
                    return;
                }

                diagnostics.Note(
                    "undercut",
                    $"{names.Name(current.Value.ItemId)}: listing {current.Value.Quantity} at "
                    + $"{current.Value.Price:N0}");

                if (!config.UndercutConfirms)
                {
                    deadline = DateTime.MaxValue;
                    step = Step.WantClose;
                    return;
                }

                AddonCallbacks.Fire(sale, 0);
                Wait(Step.WantClose);
                break;
        }
    }

    /// <summary>
    /// Starts a sale by asking the board what the thing is going for.
    /// </summary>
    /// <remarks>
    /// The board itself, because at a bell the character is standing on the world these would
    /// sell on, so the game's answer is exact where Universalis's is whatever somebody last
    /// uploaded. The item has to be found first: a price for something that turns out not to be
    /// in any bag is a request nobody needed.
    /// </remarks>
    private unsafe void Begin(Job job)
    {
        if (Locate(job.ItemId, job.FromRetainer) is not { } found)
        {
            Fail($"{names.Name(job.ItemId)} is not in your bags or this retainer");
            return;
        }

        current = job with { Quantity = Math.Min(job.Quantity, found.Quantity) };

        askedAt = DateTimeOffset.UtcNow;

        if (!requests.Refresh(scope.Selling, [job.ItemId]))
            market.RefreshInBackground(scope.Selling, [job.ItemId], true, FetchPriority.Interactive);

        step = Step.WantPrice;
        deadline = DateTime.UtcNow + Answering;
    }

    /// <summary>
    /// Waits for the board's answer, then prices the listing off it.
    /// </summary>
    /// <remarks>
    /// Called off rather than fallen back on when the answer does not come. A price nobody has
    /// just checked is exactly what this step exists to refuse, so a quiet board stops the run
    /// instead of quietly listing at yesterday's number.
    /// </remarks>
    private unsafe void Price()
    {
        if (scope.Selling is not { Length: > 0 } selling)
        {
            Fail("there is no world to price against");
            return;
        }

        if (market.FetchedAt(selling, current!.Value.ItemId) is not { } at || at < askedAt)
            return;

        if (Locate(current.Value.ItemId, current.Value.FromRetainer) is not { } found)
        {
            Fail($"{names.Name(current.Value.ItemId)} is no longer where it was");
            return;
        }

        if (undercutting.Fresh(current.Value.ItemId, found.IsHq) is not { } price)
        {
            Fail($"nothing on the board to price {names.Name(current.Value.ItemId)} against");
            return;
        }

        current = current.Value with { Price = price, Quantity = Math.Min(current.Value.Quantity, found.Quantity) };

        var context = AgentInventoryContext.Instance();
        var owner = Owner();

        if (context is null || owner == 0)
        {
            Fail("your inventory is not open");
            return;
        }

        // A menu already on screen is somebody else's: mine has not been asked for yet. Clicking
        // an entry of it would be clicking whatever a person, or another plugin, was in the
        // middle of.
        if (Visible(Menu) is not null)
        {
            Fail("a menu was already open");
            return;
        }

        diagnostics.Note(
            "sell",
            $"{names.Name(current.Value.ItemId)}: {found.Container} slot {found.Slot}, "
            + $"{current.Value.Quantity} at {price:N0}, menu owner {owner}");

        context->OpenForItemSlot(found.Container, found.Slot, 0, owner);
        Wait(Step.WantSaleMenu);
    }

    /// <summary>
    /// Where this item is, preferring the biggest stack of it that could be listed.
    /// </summary>
    /// <remarks>
    /// Searched in the pile the offer was about rather than everywhere. The same thing often
    /// sits in a bag and in a retainer at once, and listing the copy the panel was not talking
    /// about puts up a different number of them than the row said it would.
    /// </remarks>
    private unsafe Found? Locate(uint itemId, bool fromRetainer)
    {
        var inventory = InventoryManager.Instance();

        if (inventory is null)
            return null;

        Found? best = null;

        foreach (var type in Sellable.Where(type => Retained(type) == fromRetainer))
        {
            var container = inventory->GetInventoryContainer(type);

            if (container is null || !container->IsLoaded)
                continue;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);

                if (item is null || item->ItemId != itemId || item->Quantity <= 0)
                    continue;

                var found = new Found(type, slot, item->Quantity, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));

                if (best is null || found.Quantity > best.Value.Quantity)
                    best = found;
            }
        }

        return best;
    }

    private static bool Retained(InventoryType type) => type.ToString().StartsWith("RetainerPage", StringComparison.Ordinal);

    /// <summary>
    /// The window the item's menu belongs to.
    /// </summary>
    /// <remarks>
    /// A context menu is opened on behalf of a window, and the entries it offers depend on which
    /// one: the same stack has "Put Up for Sale" on it from the inventory with a retainer open,
    /// and does not from anywhere else. Whichever inventory window is on screen is the owner,
    /// and none being open is a stop rather than a guess at an id.
    /// </remarks>
    private static unsafe uint Owner()
    {
        foreach (var name in (string[])["Inventory", "InventoryLarge", "InventoryExpansion", "InventoryRetainer", "InventoryRetainerLarge"])
        {
            var addon = Visible(name);

            if (addon is not null)
                return addon->Id;
        }

        return 0;
    }

    /// <summary>
    /// Which entry of a context menu says the given thing, if exactly one does.
    /// </summary>
    /// <remarks>
    /// By the game's own words rather than by counting, because the entries an item offers
    /// depend on where it sits and "Discard" is among them. Anything other than exactly one
    /// match is no answer: two entries reading the same is as unclickable as none.
    ///
    /// Where the names start is found rather than assumed, and that is not fussiness. Assuming
    /// it put the whole list one out, which read "Entrust to Retainer" as "Put Up for Sale" and
    /// moved a stack into the retainer instead of onto the board. A run of exactly as many
    /// strings as the menu says it has entries is a shape worth trusting; an offset somebody
    /// wrote down once is not.
    /// </remarks>
    private unsafe int? Entry(AtkUnitBase* menu, IReadOnlySet<string> wanted)
    {
        if (wanted.Count == 0 || menu->AtkValues is null || menu->AtkValuesCount < 2)
            return null;

        var count = menu->AtkValues[0].Int;

        if (count is <= 0 or > MostEntries || Names(menu, count) is not { } first)
            return null;

        int? found = null;

        for (var index = 0; index < count; index++)
        {
            var text = menu->AtkValues[first + index].String.ToString()?.Trim();

            if (string.IsNullOrEmpty(text) || !wanted.Contains(text))
                continue;

            // Never the one that throws things away, whatever a sheet says it is called.
            if (labels.Discard.Contains(text))
                return null;

            if (found is not null)
                return null;

            found = index;
        }

        return found;
    }

    /// <summary>Where the run of entry names starts, if the menu has one as long as it claims.</summary>
    private static unsafe int? Names(AtkUnitBase* menu, int count)
    {
        for (var start = 1; start + count <= menu->AtkValuesCount; start++)
        {
            var all = true;

            for (var index = 0; index < count && all; index++)
                all = Text(menu->AtkValues[start + index]);

            if (all)
                return start;
        }

        return null;
    }

    private static unsafe bool Text(AtkValue value) =>
        value.Type is AtkValueType.String or AtkValueType.ManagedString && value.String.Value is not null;

    /// <summary>
    /// Puts the quantity and the price into the sale dialog, and checks they went in.
    /// </summary>
    /// <remarks>
    /// Read back before anything is confirmed. Confirming a dialog is the one irreversible thing
    /// here, and the only safe reason to do it is that the fields say what this put in them.
    /// 
    /// The dialog has the last word on the quantity. How much of a thing one listing takes is
    /// the board's rule rather than the bag's, and it varies by item; asking for more than it
    /// allows makes the field settle on what it allows, and that answer is better than the one
    /// worked out from a sheet. The price is not treated that way: a price that did not go in as
    /// asked is a price nobody chose.
    /// </remarks>
    private unsafe bool Fill(AtkUnitBase* dialog, Job job)
    {
        if (dialog->UldManager.NodeListCount != Nodes || job.Price <= 0 || job.Quantity <= 0)
            return false;

        if (!Set(dialog, QuantityNode, job.Quantity) || !Set(dialog, PriceNode, job.Price))
            return false;

        if (Number(dialog, QuantityNode) is not { } quantity || quantity <= 0)
            return false;

        if (quantity != job.Quantity)
        {
            diagnostics.Note(
                "sell",
                $"{names.Name(job.ItemId)}: asked to list {job.Quantity}, the dialog allows {quantity}");

            current = job with { Quantity = (int)quantity };
        }

        return Number(dialog, PriceNode) == job.Price;
    }

    /// <summary>One stack of something, where it sits and what quality it is.</summary>
    private readonly record struct Found(InventoryType Container, int Slot, int Quantity, bool IsHq);

    private void Wait(Step next)
    {
        step = next;
        deadline = DateTime.UtcNow + Patience;
    }

    private void Finish()
    {
        var repriced = done - listed;
        List<string> said = [];

        if (repriced > 0)
            said.Add(repriced == 1 ? "Repriced 1 listing" : $"Repriced {repriced} listings");

        if (listed > 0)
            said.Add(listed == 1 ? "put 1 stack up for sale" : $"put {listed} stacks up for sale");

        if (said.Count > 0)
            notices.Add(NoticeKind.Reprice, $"{string.Join(", ", said)}.", count: done);

        diagnostics.Note("undercut", $"run finished, {repriced} repriced, {listed} listed");
        done = 0;
        listed = 0;
        current = null;
    }

    private void Fail(string why)
    {
        var left = queue.Count + (current is null ? 0 : 1);

        diagnostics.Note("undercut", $"stopped: {why}; {done} done, {left} left alone");
        notices.Add(NoticeKind.Reprice, $"Stopped: {why}. {done} done, {left} left alone.", count: done);

        queue.Clear();
        current = null;
        step = Step.Idle;
        done = 0;
        listed = 0;
    }

    /// <summary>A dialog opened by hand on an undercut listing gets the price filled in.</summary>
    private unsafe void OnDialog(AddonEvent type, AddonArgs args)
    {
        // A dialog the run opened is the run's to fill.
        if (step != Step.Idle || !config.UndercutFillsPrice)
            return;

        try
        {
            var dialog = (AtkUnitBase*)args.Addon.Address;

            if (Identify(dialog) is not { } slot)
                return;

            if (undercutting.Wanted(slot.ItemId, slot.UnitPrice, slot.IsHq) is not { } plan)
                return;

            if (Set(dialog, plan.Target))
            {
                diagnostics.Note(
                    "undercut",
                    $"{names.Name(slot.ItemId)}: {slot.UnitPrice:N0} -> {plan.Target:N0}, under {plan.Below:N0}");
            }
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not fill the price dialog.");
        }
    }

    private static unsafe AtkUnitBase* Visible(string name)
    {
        var stage = AtkStage.Instance();
        var unit = stage is null ? null : stage->RaptureAtkUnitManager->GetAddonByName(name);
        return unit is not null && unit->IsVisible ? unit : null;
    }

    /// <summary>
    /// Matches the dialog against the open retainer's slots.
    /// </summary>
    /// <remarks>
    /// The dialog opens with the listing's price and quantity in its fields. Two listings that
    /// agree on both are not told apart, and the answer then is no answer rather than a guess
    /// that fills the wrong price into the wrong stack.
    /// </remarks>
    private unsafe MarketSlot? Identify(AtkUnitBase* dialog, bool quiet = false)
    {
        if (dialog->UldManager.NodeListCount != Nodes)
        {
            if (!quiet)
                diagnostics.Note("undercut", $"the price dialog has {dialog->UldManager.NodeListCount} nodes, not {Nodes}; leaving it alone");
            return null;
        }

        var price = Number(dialog, PriceNode);
        var quantity = Number(dialog, QuantityNode);

        if (price is null || quantity is null)
            return null;

        if (ActiveRetainer() is not { } stored)
            return null;

        var matches = stored.Slots
            .Where(slot => slot.ItemId != 0 && slot.UnitPrice == price && slot.Quantity == quantity)
            .Select(slot => new MarketSlot(slot.ItemId, slot.Quantity, slot.UnitPrice, slot.IsHq))
            .Distinct()
            .ToArray();

        if (matches.Length == 1)
            return matches[0];

        if (!quiet)
        {
            diagnostics.Note(
                "undercut",
                matches.Length == 0
                    ? $"price dialog at {price:N0} x{quantity} matches none of this retainer's listings"
                    : $"price dialog at {price:N0} x{quantity} matches {matches.Length} listings; leaving it alone");
        }

        return null;
    }

    private static unsafe long? Number(AtkUnitBase* dialog, int index)
    {
        var node = dialog->UldManager.NodeList[index];

        if (node is null || node->GetComponent() is null)
            return null;

        var input = (AtkComponentInputBase*)node->GetComponent();

        if (input->AtkTextNode is null)
            return null;

        var digits = new string(input->AtkTextNode->NodeText.ToString().Where(char.IsDigit).ToArray());

        return long.TryParse(digits, out var value) ? value : null;
    }

    private static unsafe bool Set(AtkUnitBase* dialog, long price) => Set(dialog, PriceNode, price);

    private static unsafe bool Set(AtkUnitBase* dialog, int index, long value)
    {
        if (dialog->UldManager.NodeListCount != Nodes || value <= 0 || value > int.MaxValue)
            return false;

        var node = dialog->UldManager.NodeList[index];

        if (node is null || node->GetComponent() is null)
            return false;

        ((AtkComponentNumericInput*)node->GetComponent())->SetValue((int)value);
        return true;
    }

    /// <summary>What a queued job is: repricing something already out, or putting something out.</summary>
    private enum JobKind
    {
        Reprice,
        Sell,
    }

    /// <param name="Slot">The listing being repriced, or -1 when there is not one yet.</param>
    /// <param name="Price">Filled in for a reprice, worked out at the last moment for a sale.</param>
    /// <param name="Quantity">How many to put up, which a reprice does not touch.</param>
    private readonly record struct Job(
        JobKind Kind,
        int Slot,
        uint ItemId,
        long Price,
        int Quantity,
        bool FromRetainer = false)
    {
        public Job(int slot, uint itemId, long price)
            : this(JobKind.Reprice, slot, itemId, price, 0)
        {
        }
    }

    private enum Step
    {
        Idle,
        WantMenu,
        WantDialog,
        WantClose,
        Settling,
        WantPrice,
        WantSaleMenu,
        WantSaleDialog,
    }
}
