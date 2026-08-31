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
///
/// Under the column, the other half of standing at a bell: what is not out yet. The slots are
/// the scarce thing, so what to put in the free ones, at what price, and which of the stacks
/// within reach are not worth a slot at all and want a vendor instead. Only what is actually to
/// hand counts, which is my bags and this retainer's own pages: something sitting with another
/// retainer is an errand rather than a recommendation.
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
    private readonly BoardRequests requests;
    private readonly Pile pile;
    private readonly Balances balances;
    private readonly RetainerStock stock;
    private readonly Boards boards;
    private readonly BoardWatcher board;
    private readonly Func<IReadOnlySet<uint>> wanted;

    private readonly Rebuilt<Fill> fill;

    /// <summary>Candidates whose book has been asked for, so a rebuild is not a request.</summary>
    private readonly HashSet<uint> booked = [];

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
        PricingScope scope,
        BoardRequests requests,
        Pile pile,
        Balances balances,
        RetainerStock stock,
        Boards boards,
        BoardWatcher board,
        Diagnostics diagnostics,
        Func<IReadOnlySet<uint>> wanted)
        : base("Rowena##retainer", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.sellFill = sellFill;
        this.undercutting = undercutting;
        this.config = config;
        this.cells = cells;
        this.market = market;
        this.scope = scope;
        this.requests = requests;
        this.pile = pile;
        this.balances = balances;
        this.stock = stock;
        this.boards = boards;
        this.board = board;
        this.wanted = wanted;

        fill = new Rebuilt<Fill>("retainer fill", Build, diagnostics);

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
        DrawFill();
    }

    /// <summary>The tally, the run-everything button, and the run's progress while it goes.</summary>
    private void DrawFooter((StoredSlot Slot, int Index)[] slots, int undercut, int raise, int think)
    {
        if (sellFill.Running is { } running)
        {
            ImGui.TextColored(
                Style.Plain,
                $"{(running.Listing ? "listing" : "repricing")}, {running.Done} done, {running.Left} to go");
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

        if (Style.Quiet(
            "refresh",
            "Re-reads the books these listings sit in, from the board itself when you are standing\n"
            + "on it, which is exact, and from Universalis otherwise. The floor from an hour ago is\n"
            + "not a floor."))
            Ask(slots);

        DrawRefreshState();
    }

    /// <summary>Asks the board about every listing on this retainer, and starts watching for it.</summary>
    /// <remarks>
    /// The board itself first: at a bell the character is standing on the very world these
    /// listings sell on, so the game's own answer is exact where Universalis's is whatever
    /// somebody last uploaded. Universalis remains the answer when the boards differ, or when
    /// asking the game has been turned off.
    /// </remarks>
    private void Ask((StoredSlot Slot, int Index)[] slots)
    {
        var ids = slots.Select(entry => entry.Slot.ItemId).Distinct().ToArray();

        settled = null;
        askedAt = DateTimeOffset.UtcNow;
        asked = ids;

        if (!requests.Refresh(scope.Selling, ids))
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

            if (back < asked.Length && (requests.Busy || market.Busy))
            {
                ImGui.SameLine();
                ImGui.TextColored(Style.Muted, $"fetching {back} of {asked.Length}");
                Style.Explain(requests.Busy
                    ? "Asking the board itself, one item a second, and the ages above fill in as it\n"
                      + "answers. Exact but slow; anything the board goes quiet on falls back to\n"
                      + "Universalis rather than landing nowhere."
                    : "Asking Universalis, a few listings a request, and the ages above fill in as the\n"
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

    /// <summary>A retainer's market slots. Twenty, always.</summary>
    private const int SlotsEach = 20;

    /// <summary>How many of the vendor stacks are named rather than counted.</summary>
    /// <remarks>
    /// Enough to be worth a detour on the way out, few enough that the window stays a window.
    /// The rest are a tab's business rather than a bell's.
    /// </remarks>
    private const int Named = 5;

    /// <summary>How many swaps are worth naming when every slot is taken.</summary>
    /// <remarks>
    /// A full retainer is the normal state, so this is the line that will actually be read most
    /// visits. Three, because swapping is a chore each time and a list of twelve is a list
    /// nobody works through.
    /// </remarks>
    private const int Swaps = 3;

    /// <summary>
    /// What is not out yet, and what of it is worth a slot here.
    /// </summary>
    /// <remarks>
    /// Only what is to hand: my bags and this retainer's own pages. The pile ranks everything I
    /// own and is right to, but a stack sitting with another retainer cannot be listed from
    /// where I am standing, and a panel at a bell that recommends errands is a panel that gets
    /// ignored.
    ///
    /// Netted at this retainer's own city rate, which is the one place that rate is known for
    /// certain rather than guessed at from the worst of them.
    /// </remarks>
    private Fill Build()
    {
        if (sellFill.ActiveRetainer() is not { } retainer)
            return new Fill([], 0, false, [], 0, 0, null);

        var horizon = config.SellingHorizon();
        var tax = board.TaxFor(retainer.CityId);

        var listed = retainer.Slots.Where(slot => slot.ItemId != 0).ToArray();
        var free = Math.Max(0, SlotsEach - listed.Length);

        // One pile, and it is whichever one is in use: the retainer's pages when those are the
        // window in front, my bags otherwise. Both list in one step from where they sit, so this
        // is not about what is possible. It is that a list headed "what you are carrying" with a
        // retainer's stock mixed into it is a list that has to be checked item by item, which is
        // the work this window exists to save.
        var retained = sellFill.LookingAtRetainerBags();
        var holdings = retained ? stock.Held(retainer.RetainerId) : balances.Carrying();

        var reading = pile.Read(holdings, wanted(), tax);
        var already = listed.Select(slot => slot.ItemId).ToHashSet();

        var picks = reading.Stacks
            .Where(stack => stack.Verdict.Call == HoardCall.List && !already.Contains(stack.ItemId))
            .OrderByDescending(stack => stack.Verdict.Realised)
            .Select(stack => new Pick(
                stack.ItemId,
                cells.Name(stack.ItemId),
                stack.Quantity,
                stack.Verdict.Listable,
                pile.Ask(stack.ItemId),
                boards.Selling(stack.ItemId) is not null,
                stack.Verdict.Realised))
            .ToArray();

        var junk = reading.Stacks
            .Where(stack => stack.Verdict.Call == HoardCall.Vendor)
            .Select(stack => new Junk(stack.ItemId, cells.Name(stack.ItemId), stack.Quantity, stack.Verdict.Worth))
            .OrderByDescending(stack => stack.Worth)
            .ToArray();
        var weakest = Weakest(listed, tax, horizon);

        // With nothing free, a pick is only worth saying if it beats something that is already
        // out: otherwise the panel is listing things there is nowhere to put.
        if (free == 0 && weakest is { } worst)
            picks = [.. picks.Where(pick => pick.Realised > worst.Realised)];

        var shown = picks.Take(free > 0 ? free : Swaps).ToArray();

        // A price to ask needs a book, and a bag is ranked off the cheap summaries, so the few
        // stacks actually being offered a slot get one asked for. Only the ones on screen: a bag
        // of two hundred stacks is not a reason to fetch two hundred books, and the ranking
        // never needed them.
        Ask(shown);

        return new Fill(
            shown,
            free,
            retained,
            [.. junk.Take(Named)],
            junk.Length,
            junk.Sum(stack => stack.Worth),
            weakest);
    }

    /// <summary>Asks the board about the candidates whose price is still a guess.</summary>
    private void Ask(IReadOnlyList<Pick> picks)
    {
        var unread = picks
            .Where(pick => !pick.Read)
            .Select(pick => pick.ItemId)
            .Except(booked)
            .ToArray();

        if (unread.Length == 0)
            return;

        booked.UnionWith(unread);
        market.RefreshInBackground(scope.Selling, unread, false, FetchPriority.Background);
    }

    /// <summary>
    /// The listing here earning its slot the least.
    /// </summary>
    /// <remarks>
    /// Judged the same way a candidate is, which is the only way the comparison means anything:
    /// what actually sells while the slot holds it, over the horizon, after this city's cut.
    ///
    /// Listings whose board has not been read are left out rather than counted as earning
    /// nothing. A listing nobody has looked up is an unknown, and calling a stack in my bags a
    /// better use of a slot than an unknown would be a guess wearing a number.
    /// </remarks>
    private Weak? Weakest(IReadOnlyList<StoredSlot> listed, MarketTax tax, int horizon)
    {
        Weak? worst = null;

        foreach (var slot in listed)
        {
            if (ListingDiagnosis.Of(
                    slot.UnitPrice,
                    slot.Quantity,
                    boards.Selling(slot.ItemId),
                    boards.Vendor(slot.ItemId),
                    tax,
                    horizon,
                    slot.IsHq) is not { } reading)
                continue;

            var earns = RetainerSlots.Realised(slot.Quantity * reading.NetHolding, reading.DaysToClear, horizon);

            if (worst is null || earns < worst.Value.Realised)
                worst = new Weak(slot.ItemId, earns);
        }

        return worst;
    }

    /// <summary>What is not out yet: what to put out, and what to take to a vendor instead.</summary>
    private void DrawFill()
    {
        var current = fill.Current;

        if (current.Picks.Count == 0 && current.JunkStacks == 0 && current.Free == 0)
            return;

        ImGui.Separator();

        DrawFillLead(current);

        if (current.Picks.Count > 0)
            DrawPicks(current);

        DrawJunk(current);
    }

    /// <summary>The one line saying what this half of the window is about this visit.</summary>
    private void DrawFillLead(Fill current)
    {
        // An empty half of the window reads as one that failed to run. A retainer with room in
        // it and nothing worth putting there is an answer, and a good one.
        if (current.Picks.Count == 0)
        {
            if (current.Free == 0)
                return;

            ImGui.TextColored(
                Style.Muted,
                $"{current.Free} free {(current.Free == 1 ? "slot" : "slots")} here, and nothing "
                + $"{(current.Retained ? "in this retainer" : "to hand")} worth one.");

            Style.Explain(
                (current.Retained
                    ? "This retainer's own pages, since those are the window you have open."
                    : "Your bags, since that is the pile you are selling from. Open the retainer's own\n"
                      + "pages and this ranks those instead.")
                + "\nAgainst what a slot would earn from each over your selling horizon. Anything\n"
                + "already listed here is not offered again.");

            return;
        }

        if (current.Free > 0)
        {
            ImGui.TextColored(
                Style.Accent,
                $"{current.Free} free {(current.Free == 1 ? "slot" : "slots")} here, best filled from "
                + $"{(current.Retained ? "this retainer's pages" : "what you are carrying")}:");

            Style.Explain(
                (current.Retained
                    ? "This retainer's own pages, because that is the window you have open. Close it and\n"
                      + "this ranks what you are carrying instead."
                    : "Your bags alone, because that is the pile you are selling from. Open the retainer's\n"
                      + "own pages and it ranks those instead: one heading, one pile, so no row has to be\n"
                      + "checked for where it actually is.")
                + "\nRanked by what a slot earns while it holds the stack, not by what the stack is worth:\n"
                + "a fortune that takes four months to clear earns a week of a slot a fraction of itself.");

            return;
        }

        ImGui.TextColored(Style.Accent, "every slot is taken, and these would earn more than what is in them:");

        Style.Explain(
            current.Weakest is { } worst
                ? $"The least a slot here is earning is {worst.Realised:N0} over {config.SellingHorizon()} days, on "
                  + $"{cells.Name(worst.ItemId)}.\nAnything below that is not listed here. Listings whose board has not "
                  + "been read yet are\nleft out of the comparison rather than counted as earning nothing."
                : "Nothing here has a board read against it yet, so there is nothing to compare with.");
    }

    private void DrawPicks(Fill current)
    {
        if (!ImGui.BeginTable("fill", 5, ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, Style.Px(220));
        ImGui.TableSetupColumn("to hand", ImGuiTableColumnFlags.WidthFixed, Style.Px(120));
        ImGui.TableSetupColumn("ask", ImGuiTableColumnFlags.WidthFixed, Style.Px(130));
        ImGui.TableSetupColumn("earns", ImGuiTableColumnFlags.WidthFixed, Style.Px(90));
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Style.Px(60));
        Cell.Headers(FillHelp);

        foreach (var pick in current.Picks)
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)pick.ItemId);

            ImGui.TableNextColumn();
            cells.Draw(pick.Name, pick.ItemId);

            ImGui.TableNextColumn();
            DrawWhere(pick, current.Retained);

            ImGui.TableNextColumn();
            DrawAsk(pick);

            ImGui.TableNextColumn();
            Cell.Right(Style.Good, $"{pick.Realised:N0}");

            ImGui.TableNextColumn();
            DrawList(pick, current.Free > 0, current.Retained);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// How many are there, and how many of them one listing would take.
    /// </summary>
    /// <remarks>
    /// A listing holds a stack. Fourteen hundred of something that stacks to nine hundred and
    /// ninety-nine is two listings and a bit, and a column saying only "1,425" beside a figure
    /// worked out on 999 of them is two numbers that do not describe the same thing.
    ///
    /// Where they are does not need saying on the row, because every row is from the same pile
    /// and the heading above has already named it.
    /// </remarks>
    private void DrawWhere(Pick pick, bool retained)
    {
        Cell.Right(
            Style.Muted,
            pick.Listable < pick.Units ? $"{pick.Listable:N0} of {pick.Units:N0}" : $"{pick.Units:N0}");

        if (!ImGui.IsItemHovered())
            return;

        ImGui.SetTooltip(
            (retained
                ? $"{pick.Units:N0} in this retainer's pages."
                : $"{pick.Units:N0} in your bags.")
            + (pick.Listable < pick.Units
                ? $"\n\nA listing takes {pick.Listable:N0} of these, so the rest is another listing and\n"
                  + "another slot."
                : ""));
    }

    /// <summary>
    /// What to ask for it, and the only way to get that number into the game from here.
    /// </summary>
    /// <remarks>
    /// Copied rather than filled in. The price dialog for a stack that is already listed is one
    /// this window opens itself and can therefore fill; the dialog for a fresh listing is opened
    /// by a walk through the game's own menus that nothing here is driving, so the honest offer
    /// is the number on the clipboard.
    /// </remarks>
    private void DrawAsk(Pick pick)
    {
        if (!pick.Read)
        {
            Cell.Right(Style.Muted, "reading");
            Style.Explain(
                "The bags are ranked off the cheap summaries, which are enough to say what a thing is\n"
                + "worth but not enough to price a listing. The board behind this one is being read.");

            return;
        }

        if (pick.Ask is not { } ask)
        {
            Cell.Right(Style.Muted, "-");
            Style.Explain("Nothing is listed and nothing has sold lately, so there is no price to suggest.");

            return;
        }

        if (Style.Quiet("copy", $"Puts {ask:N0} on the clipboard, to paste into the game's price box."))
            ImGui.SetClipboardText($"{ask}");

        ImGui.SameLine();
        Cell.Right(Style.Plain, $"{ask:N0}");
        Style.Explain(
            "The margin under the cheapest listing that would serve the same buyer, or under what\n"
            + "people have actually been paying where the board is asking far more than that. The\n"
            + "same price the column above would reprice it to once it is out.");
    }

    /// <summary>
    /// The button that actually puts it out.
    /// </summary>
    /// <remarks>
    /// It carries no price. What this window is showing was worked out from a book that is
    /// minutes old at best, and a minute is long enough for somebody to have gone under it, so
    /// the board is asked again as the run starts and the number that goes into the dialog is
    /// the one that came back. A board that does not answer stops the run rather than falling
    /// back on what was on screen.
    ///
    /// Disabled with nothing free, since the game would refuse it: a swap means taking something
    /// down first, which is a decision rather than a step in a run.
    /// </remarks>
    private void DrawList(Pick pick, bool room, bool retained)
    {
        var busy = sellFill.Running is not null;

        ImGui.BeginDisabled(busy || !room || pick.Ask is null);

        if (Style.Row(
                "list",
                !room
                    ? "Every slot here is taken. Take one of the listings above down first, and this\n"
                      + "becomes a listing rather than a suggestion."
                    : pick.Ask is null
                        ? "There is no price to put on it yet."
                        : $"Asks the board what this is going for, then walks the game's own windows to put\n"
                          + $"{pick.Listable:N0} of it up at whatever comes back. The number on screen is not the\n"
                          + "number used: it is minutes old, and a minute is enough for somebody to have gone\n"
                          + "under it. The dialog's confirm is pressed only if you have left that setting on.")
            && !busy && room)
            sellFill.Sell(pick.ItemId, pick.Listable, retained);

        ImGui.EndDisabled();
    }

    /// <summary>
    /// What is within reach and not worth a slot, which is the other half of the answer.
    /// </summary>
    /// <remarks>
    /// Named rather than counted, because a count is not something anybody can act on and the
    /// point of saying it here is the retrieving: these are the stacks to pull out of the
    /// retainer while standing at it. The selling itself happens at a vendor, which a bell is
    /// not, so there is no button and this does not pretend otherwise.
    /// </remarks>
    private void DrawJunk(Fill current)
    {
        if (current.JunkStacks == 0)
            return;

        Style.Gap(2f);

        ImGui.TextColored(
            Style.Muted,
            $"{current.JunkStacks} {(current.JunkStacks == 1 ? "stack" : "stacks")} "
            + $"{(current.Retained ? "in this retainer" : "in your bags")} "
            + $"{(current.JunkStacks == 1 ? "is" : "are")} worth more to a vendor: {current.JunkGil:N0} gil the lot.");

        Style.Explain(
            "Either a vendor pays more than the board would leave you, or the stack would not earn\n"
            + $"a market slot the {config.SlotFloor:N0} gil you set as the least one is worth. The selling\n"
            + "happens at a vendor rather than here, so this is what to have on you before you go.\n"
            + "The Bags tab has all of them, wherever they are sitting.");

        foreach (var stack in current.Worst)
        {
            ImGui.PushID((int)stack.ItemId);
            ImGui.Indent();

            cells.Icon(stack.ItemId, 16f);
            ImGui.SameLine();

            ImGui.TextColored(Style.Muted, $"{stack.Units:N0}x {stack.Name}");
            ImGui.SameLine();

            Cell.Right(Style.Muted, $"{stack.Worth:N0}");

            ImGui.Unindent();
            ImGui.PopID();
        }

        if (current.JunkStacks > current.Worst.Count)
        {
            ImGui.TextColored(
                Style.Muted,
                $"    and {current.JunkStacks - current.Worst.Count} more, on the Bags tab.");
        }
    }

    private static readonly string?[] FillHelp =
    [
        null,
        "How many are in the pile named above, and how many of them one listing would take. A\n"
        + "listing holds a stack, so a pile bigger than one is more than one listing.",
        "What to ask per unit, and a copy of it for the game's price box.",
        "What a slot earns for holding one stack of it over your selling horizon, which is what\n"
        + "the ranking is on. Not what the pile is worth: a stack that takes months to clear\n"
        + "earns a week of a slot very little of itself.",
        null,
    ];

    /// <summary>One stack worth a slot here, and what to do about it.</summary>
    /// <param name="Listable">How many one slot would take, which is a stack rather than the pile.</param>
    /// <param name="Read">Whether the board behind it has been read, which is what an ask needs.</param>
    private readonly record struct Pick(
        uint ItemId,
        string Name,
        int Units,
        int Listable,
        long? Ask,
        bool Read,
        long Realised);

    /// <summary>The listing here earning its slot the least, which is what a swap has to beat.</summary>
    private readonly record struct Weak(uint ItemId, long Realised);

    /// <summary>One stack in the pile in front that a vendor is the better answer for.</summary>
    private readonly record struct Junk(uint ItemId, string Name, int Units, long Worth);

    /// <param name="Retained">Whether the pile in front is the retainer's pages rather than my bags.</param>
    private sealed record Fill(
        IReadOnlyList<Pick> Picks,
        int Free,
        bool Retained,
        IReadOnlyList<Junk> Worst,
        int JunkStacks,
        long JunkGil,
        Weak? Weakest);
}
