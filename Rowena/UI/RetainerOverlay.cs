using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Rowena.Core.Market;
using Rowena.Game;
using Rowena.Market;

namespace Rowena.UI;

/// <summary>
/// The undercut column, pinned beside the retainer's own sell list.
/// </summary>
/// <remarks>
/// The Selling tab says what wants repricing; this is where repricing happens, so the two
/// should not be two windows apart. It follows the sell list around, one row per slot in the
/// list's own order, and each row that wants a new price, under somebody or with room to
/// raise, has the button that opens the game's price dialog on it with that price filled in.
/// The dialog's confirm is still the game's.
///
/// Rows are the slots as the retainer was last read, which is this visit: the reader runs
/// whenever a retainer is open, so by the time the list is on screen the slots are current.
///
/// Each row says what it would be cut to and what it is being cut under, because a number on
/// its own is a number to check elsewhere, and the point of the column is not having to.
/// </remarks>
internal sealed class RetainerOverlay : Window
{
    private static readonly string?[] Help =
    [
        null,
        "What you are asking per unit, as the game has it.",
        "How long ago the board behind this row was read. Everything to the right of it is only\n"
        + "as good as this number, and a listing nobody has looked up yet says so rather than\n"
        + "quietly reading as settled.",
        "What the row wants doing, and the move it would make: from what you are asking now to\n"
        + "what it would ask instead. What it is going under sits in the tooltip, because that\n"
        + "is a fact about somebody else and the move is the decision. A target marked ~ is\n"
        + "what recent sales went for rather than a listing, and one marked ^ raises rather\n"
        + "than cuts.",
        "Said only where following the floor down would give up a quarter of the asking price\n"
        + "or more. That is not competing with the listing in front, it is agreeing to a\n"
        + "different price for the item, so the row argues instead of just offering a button.\n"
        + "These are counted apart from the rest and left out of \"reprice all\".",
    ];

    /// <summary>How long the result of a refresh stays on screen once the refresh is over.</summary>
    /// <remarks>
    /// Long enough to be read on the way back to the retainer's list, short enough that it is
    /// gone before it becomes another number to check. The ages in the column are the lasting
    /// record; this is only the press closing its loop.
    /// </remarks>
    private static readonly TimeSpan Lingers = TimeSpan.FromSeconds(6);

    private readonly RetainerSellFill sellFill;
    private readonly Undercutting undercutting;
    private readonly Configuration config;
    private readonly ItemCells cells;
    private readonly MarketCache market;
    private readonly PricingScope scope;

    private IDisposable? shell;

    // The press being waited on: what was asked about, and when. Progress is counted from the
    // listings themselves rather than from the fetcher's queue, because the queue is shared.
    // "fetching 8 of 412" while a sweep runs says nothing about whether these twenty are done,
    // and whether these twenty are done is the entire question.
    private uint[] asked = [];
    private DateTimeOffset askedAt;
    private (int Back, int Of, DateTimeOffset At)? settled;
    private string? watching;

    public RetainerOverlay(
        RetainerSellFill sellFill,
        Undercutting undercutting,
        Configuration config,
        ItemCells cells,
        MarketCache market,
        PricingScope scope)
        : base("Rowena##retainer", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.sellFill = sellFill;
        this.undercutting = undercutting;
        this.config = config;
        this.cells = cells;
        this.market = market;
        this.scope = scope;

        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions() =>
        config.UndercutOverlay && sellFill.ListEdge() is not null && sellFill.ActiveRetainer() is not null;

    public override void PreDraw()
    {
        shell = Style.Shell();

        if (sellFill.ListEdge() is { } edge)
            ImGui.SetNextWindowPos(new Vector2(edge.X, edge.Y));
    }

    public override void PostDraw()
    {
        shell?.Dispose();
        shell = null;
    }

    public override void Draw()
    {
        if (sellFill.ActiveRetainer() is not { } retainer)
            return;

        // No title bar on an overlay, so the masthead is the only thing saying whose
        // column this is - and which retainer it is reading.
        Style.Masthead("Rowena", retainer.Name);

        // A different retainer is a different question, and the last one's refresh is not this
        // one's news. Walking between them otherwise left a count about twenty other listings.
        if (watching != retainer.Name)
        {
            watching = retainer.Name;
            asked = [];
            settled = null;
        }

        // In the list's own order, so the rows line up with the game's. Anything the list does
        // not show (it should show everything) goes last rather than missing.
        var order = sellFill.ListOrder();

        var slots = retainer.Slots
            .Select((slot, index) => (Slot: slot, Index: index))
            .Where(entry => entry.Slot.ItemId != 0)
            .OrderBy(entry => order.TryGetValue(entry.Index, out var row) ? row : int.MaxValue)
            .ThenBy(entry => entry.Index)
            .ToArray();

        var undercut = 0;
        var raise = 0;
        var think = 0;

        if (!ImGui.BeginTable("overlay", 5, ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, Style.Px(220));
        ImGui.TableSetupColumn("asking", ImGuiTableColumnFlags.WidthFixed, Style.Px(90));
        ImGui.TableSetupColumn("age", ImGuiTableColumnFlags.WidthFixed, Style.Px(56));
        ImGui.TableSetupColumn("move", ImGuiTableColumnFlags.WidthFixed, Style.Px(215));
        ImGui.TableSetupColumn("call", ImGuiTableColumnFlags.WidthFixed, Style.Px(80));
        Cell.Headers(Help);

        foreach (var (slot, index) in slots)
        {
            ImGui.TableNextRow();
            ImGui.PushID(index);

            // Read once and used twice: the column says it, and the verdict beside it is
            // downgraded from a green dash to a shrug when there is nothing behind it.
            var freshness = market.FreshnessOf(scope.Selling, slot.ItemId);

            ImGui.TableNextColumn();
            cells.Draw(cells.Name(slot.ItemId), slot.ItemId);

            ImGui.TableNextColumn();
            Cell.Right(Style.Muted, $"{slot.UnitPrice:N0}");

            ImGui.TableNextColumn();
            Cell.Age(freshness);

            ImGui.TableNextColumn();
            DrawUndercut(slot, index, freshness.Standing, ref undercut, ref raise, ref think);

            ImGui.PopID();
        }

        ImGui.EndTable();

        DrawFooter(slots, undercut, raise, think);
    }

    /// <summary>The tally, the run-everything button, and the run's progress while it goes.</summary>
    private void DrawFooter((StoredSlot Slot, int Index)[] slots, int undercut, int raise, int think)
    {
        if (sellFill.Running is { } running)
        {
            ImGui.TextColored(Style.Plain, $"repricing, {running.Done} done, {running.Left} to go");
            ImGui.SameLine();

            if (Style.Row("stop"))
                sellFill.Stop();

            return;
        }

        List<string> says = [];

        if (undercut > 0)
            says.Add($"{undercut} to reprice");

        if (raise > 0)
            says.Add($"{raise} to raise");

        if (think > 0)
            says.Add($"{think} to think about");

        ImGui.TextColored(
            says.Count == 0 ? Style.Good : Style.Accent,
            says.Count == 0 ? "nothing wants repricing" : string.Join(", ", says));

        if (think > 0)
        {
            Style.Explain(
                "The ones to think about are giving up a quarter of the asking price or more, which\n"
                + "is not competing with the listing in front but agreeing to a different price for\n"
                + "the item. Each says what it thinks the answer is. \"reprice all\" leaves them alone.");
        }

        if (undercut + raise > 0)
        {
            ImGui.SameLine();

            if (Style.Commit(
                "reprice all",
                "Reprices every marked row above, one after another, through the game's own windows: "
                + "the ones somebody is under, the ones nobody is paying, and the ones with room to "
                + "raise. Ignored items are skipped, and so is anything giving up a quarter of its "
                + "price or more: one person clearing a retainer slot should not take a whole "
                + "retainer down with it. Those still have their own button."))
            {
                foreach (var (slot, index) in slots)
                {
                    if (undercutting.Ignored(slot.ItemId))
                        continue;

                    // Only the routine ones. A steep move is a decision, and a decision made by
                    // a button that does twenty of them is not one.
                    if (undercutting.Wants(slot.ItemId, slot.UnitPrice, slot.IsHq) is { } wants
                        && wants.Chase.Call == ChaseCall.Follow)
                        sellFill.Reprice(index, slot.ItemId, wants.Plan.Target);
                }
            }
        }

        ImGui.SameLine();

        if (Style.Quiet("refresh", "Refetches the books these listings sit in. The floor from an hour ago is not a floor."))
            Ask(slots);

        DrawRefreshState();
    }

    /// <summary>Asks the board about every listing on this retainer, and starts watching for it.</summary>
    private void Ask((StoredSlot Slot, int Index)[] slots)
    {
        var ids = slots.Select(entry => entry.Slot.ItemId).Distinct().ToArray();

        settled = null;
        askedAt = DateTimeOffset.UtcNow;
        asked = ids;

        market.RefreshInBackground(scope.Selling, ids, true, FetchPriority.Interactive);
    }

    /// <summary>
    /// Whether the last press is still going, and what it came to.
    /// </summary>
    /// <remarks>
    /// Counted from the listings rather than from the fetcher, because the fetcher's queue is
    /// shared with the sweeps and a count off it answers a question nobody asked. A press is
    /// over when everything it asked about has been stored, or when the fetcher has gone quiet
    /// without that happening: a batch that fails every attempt is given up on, and its ids
    /// never land, so waiting on them is waiting forever.
    ///
    /// Which rows have landed is the age column's to say and is not said again here.
    /// </remarks>
    private void DrawRefreshState()
    {
        if (asked.Length > 0)
        {
            var back = Back();

            if (back < asked.Length && market.Busy)
            {
                ImGui.SameLine();
                ImGui.TextColored(Style.Muted, $"fetching {back} of {asked.Length}");
                Style.Explain(
                    "Asking Universalis, a few listings a request, and the ages above fill in as the\n"
                    + "answers land. A press made while a sweep is running waits for the sweep first.");

                return;
            }

            settled = (back, asked.Length, DateTimeOffset.UtcNow);
            asked = [];
        }

        if (settled is not { } run || DateTimeOffset.UtcNow - run.At >= Lingers)
            return;

        ImGui.SameLine();

        if (run.Back == run.Of)
        {
            ImGui.TextColored(Style.Good, "up to date");
            Style.Explain("Every listing here was read again just now.");

            return;
        }

        // The reason goes in the tooltip rather than on the line. This window is pinned over
        // the game while a retainer is open, and a red sentence living there is worse than the
        // count, which is the part that has to be read.
        ImGui.TextColored(Style.Warn, $"{run.Back} of {run.Of} came back");
        Style.Explain(
            (market.LastError is { } error ? $"{error}\n\n" : "")
            + "The rest were given up on, so their ages above have not moved and the verdicts\n"
            + "beside them are the ones they already had. Refresh to have another go.");
    }

    /// <summary>How many of the listings asked about have been stored since the press.</summary>
    private int Back()
    {
        if (scope.Selling is not { Length: > 0 } selling)
            return 0;

        return asked.Count(id => market.FetchedAt(selling, id) is { } at && at >= askedAt);
    }

    private void DrawUndercut(StoredSlot slot, int index, Standing standing, ref int undercut, ref int raise, ref int think)
    {
        var wants = undercutting.Wants(slot.ItemId, slot.UnitPrice, slot.IsHq);
        var ignored = undercutting.Ignored(slot.ItemId);

        if (wants is not { } wanted)
        {
            // Green is a verdict, and there is none to give on a listing whose board has never
            // been read: the same dash meant "correctly priced" and "no idea", which is how a
            // column of dashes ends up being checked by hand anyway.
            Cell.Right(standing == Standing.Unknown ? Style.Muted : Style.Good, "-");

            if (standing == Standing.Unknown)
                Style.Explain("Nothing has been read for this one, so there is nothing to say about it yet.");

            return;
        }

        var (plan, chase) = wanted;
        var settled = chase.Call != ChaseCall.Follow;

        if (!ignored)
        {
            if (settled)
                think++;
            else if (plan.Why == UndercutWhy.RoomAbove)
                raise++;
            else
                undercut++;
        }

        var reasoning = Reasoning(slot, plan, chase, ignored);

        ImGui.BeginDisabled(sellFill.Running is not null);

        var pressed = Style.Row(plan.Why switch
        {
            UndercutWhy.NobodyPays => "reprice",
            UndercutWhy.RoomAbove => "raise",
            _ => "undercut",
        });

        if (pressed)
            sellFill.Reprice(index, slot.ItemId, plan.Target);

        ImGui.EndDisabled();
        Style.Explain(reasoning);

        // The move, from where the price is now. The floor and the target side by side describe
        // a different move entirely: a listing at 4,000 going under a floor of 2,000 reads as
        // "2,000 -> 1,995", five gil, when what is given up is 2,005 a unit. What it is going
        // under is a fact about somebody else, and lives in the tooltip.
        ImGui.SameLine();
        Cell.Right(
            ignored || settled ? Style.Muted : Style.Accent,
            $"{plan.Mine:N0} -> "
            + plan.Why switch
            {
                UndercutWhy.NobodyPays => "~",
                UndercutWhy.RoomAbove => "^",
                _ => "",
            }
            + $"{plan.Target:N0}"
            + (slot.IsHq && plan.Why == UndercutWhy.NobodyPays ? " HQ?" : ""));

        Style.Explain(reasoning);

        // Exactly one thing on a row is the open want. Where following the floor is a decision
        // rather than a chore, the decision is it, and the price beside it goes quiet.
        ImGui.TableNextColumn();
        Cell.Chase(chase, ignored);
    }

    /// <summary>
    /// The whole argument for one row, on whichever part of it the mouse lands.
    /// </summary>
    /// <remarks>
    /// One text rather than three, because the row is one argument: the move, what it is
    /// measured against, and what to do about it. Split up, the price carried the reasoning and
    /// the verdict beside it carried none, which is the wrong way round.
    /// </remarks>
    private string Reasoning(StoredSlot slot, UndercutPlan plan, ChaseVerdict chase, bool ignored) =>
        (plan.Move < 0
            ? $"Asking {plan.Target:N0} instead of {plan.Mine:N0} gives up {-plan.Move:N0} a unit, "
              + $"{-plan.Share:P0} of the price.\n"
            : $"Asking {plan.Target:N0} instead of {plan.Mine:N0} is {plan.Move:N0} more a unit.\n")
        + plan.Why switch
        {
            UndercutWhy.NobodyPays =>
                $"Nobody is paying {plan.Mine:N0}"
                + (plan.UnitsAhead > 0
                    ? $", nor the {plan.UnitsAhead:N0} units listed under it"
                    : ", cheapest on the board or not")
                + $": what actually sells goes for about {plan.Below:N0}.",
            UndercutWhy.RoomAbove =>
                $"Nothing is under yours and the next listing sits at {plan.Below:N0}, so this keeps you\n"
                + "the cheapest, and recent sales say somebody pays up there.",
            _ => $"{plan.UnitsAhead:N0} units sit in front, the cheapest at {plan.Below:N0}.",
        }
        + (Phrases.ChaseWhy(chase) is { Length: > 0 } advice ? $"\n\n{advice}" : "")
        + (slot.IsHq && plan.Why == UndercutWhy.NobodyPays
            ? "\n\nHQ: recent sales are not split by quality, so what people pay may be for NQ."
            : "")
        + (ignored
            ? "\n\nYou said to leave this one alone; the button still works, it just does not count."
            : "")
        + (config.UndercutConfirms ? "" : "\n\nThe price dialog is filled in and left for you to confirm.");

}
