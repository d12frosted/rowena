using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
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
/// </remarks>
internal sealed class RetainerSellFill : IDisposable
{
    private const string Dialog = "RetainerSell";
    private const string List = "RetainerSellList";
    private const string Menu = "ContextMenu";

    /// <summary>Where the two inputs sit in the dialog's node list, and how long that list is.</summary>
    /// <remarks>Checked before touching anything: a dialog with a different shape is not this dialog.</remarks>
    private const int Nodes = 23;
    private const int PriceNode = 15;
    private const int QuantityNode = 11;

    /// <summary>How long a window gets to appear before the run is called off.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>A breath between a window closing and the next event, so the list has caught up.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly Configuration config;
    private readonly Undercutting undercutting;
    private readonly Items names;
    private readonly Notices notices;
    private readonly Diagnostics diagnostics;
    private readonly IPluginLog log;

    private readonly Queue<Job> queue = new();
    private Job? current;
    private Step step = Step.Idle;
    private DateTime deadline;
    private int done;

    public RetainerSellFill(
        IAddonLifecycle lifecycle,
        IFramework framework,
        Configuration config,
        Undercutting undercutting,
        Items names,
        Notices notices,
        Diagnostics diagnostics,
        IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.config = config;
        this.undercutting = undercutting;
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

    /// <summary>Whether a run is in progress, and how far along.</summary>
    public (int Done, int Left)? Running => step == Step.Idle ? null : (done, queue.Count + (current is null ? 0 : 1));

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
        }
    }

    private void Wait(Step next)
    {
        step = next;
        deadline = DateTime.UtcNow + Patience;
    }

    private void Finish()
    {
        if (done > 0)
            notices.Add(NoticeKind.Sale, done == 1 ? "Repriced 1 listing." : $"Repriced {done} listings.", count: done);

        diagnostics.Note("undercut", $"run finished, {done} repriced");
        done = 0;
        current = null;
    }

    private void Fail(string why)
    {
        var left = queue.Count + (current is null ? 0 : 1);

        diagnostics.Note("undercut", $"stopped: {why}; {done} repriced, {left} left alone");
        notices.Add(NoticeKind.Sale, $"Repricing stopped: {why}. {done} done, {left} left alone.", count: done);

        queue.Clear();
        current = null;
        step = Step.Idle;
        done = 0;
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

            // The board data does not tell qualities apart, so the listing in front of an HQ
            // stack may be NQ, and filling an NQ price into an HQ listing is money gone. The
            // number is still shown on the Selling tab; it is just not typed in unasked.
            if (slot.IsHq)
            {
                diagnostics.Note("undercut", $"{names.Name(slot.ItemId)} is HQ; not filling in unasked");
                return;
            }

            if (undercutting.Wanted(slot.ItemId, slot.UnitPrice) is not { } plan)
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

    private static unsafe bool Set(AtkUnitBase* dialog, long price)
    {
        if (dialog->UldManager.NodeListCount != Nodes || price <= 0 || price > int.MaxValue)
            return false;

        var node = dialog->UldManager.NodeList[PriceNode];

        if (node is null || node->GetComponent() is null)
            return false;

        ((AtkComponentNumericInput*)node->GetComponent())->SetValue((int)price);
        return true;
    }

    private readonly record struct Job(int Slot, uint ItemId, long Price);

    private enum Step
    {
        Idle,
        WantMenu,
        WantDialog,
        WantClose,
        Settling,
    }
}
