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
    private readonly RetainerSellFill sellFill;
    private readonly Undercutting undercutting;
    private readonly Configuration config;
    private readonly ItemCells cells;
    private readonly Action<uint, long> fetch;

    private IDisposable? shell;

    public RetainerOverlay(
        RetainerSellFill sellFill,
        Undercutting undercutting,
        Configuration config,
        ItemCells cells,
        Action<uint, long> fetch)
        : base("Rowena##retainer", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.sellFill = sellFill;
        this.undercutting = undercutting;
        this.config = config;
        this.cells = cells;
        this.fetch = fetch;

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

        if (!ImGui.BeginTable("overlay", 3, ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, Style.Px(220));
        ImGui.TableSetupColumn("asking", ImGuiTableColumnFlags.WidthFixed, Style.Px(90));
        ImGui.TableSetupColumn("under -> yours", ImGuiTableColumnFlags.WidthFixed, Style.Px(250));
        ImGui.TableHeadersRow();

        foreach (var (slot, index) in slots)
        {
            ImGui.TableNextRow();
            ImGui.PushID(index);

            ImGui.TableNextColumn();
            cells.Draw(cells.Name(slot.ItemId), slot.ItemId);

            ImGui.TableNextColumn();
            Cell.Right(Style.Muted, $"{slot.UnitPrice:N0}");

            ImGui.TableNextColumn();
            DrawUndercut(slot, index, ref undercut, ref raise);

            ImGui.PopID();
        }

        ImGui.EndTable();

        DrawFooter(slots, undercut, raise);
    }

    /// <summary>The tally, the run-everything button, and the run's progress while it goes.</summary>
    private void DrawFooter((StoredSlot Slot, int Index)[] slots, int undercut, int raise)
    {
        if (sellFill.Running is { } running)
        {
            ImGui.TextColored(Style.Plain, $"repricing, {running.Done} done, {running.Left} to go");
            ImGui.SameLine();

            if (Style.Row("stop"))
                sellFill.Stop();

            return;
        }

        ImGui.TextColored(
            undercut + raise == 0 ? Style.Good : Style.Accent,
            (undercut, raise) switch
            {
                (0, 0) => "nothing wants repricing",
                (_, 0) => $"{undercut} to reprice",
                (0, _) => $"{raise} to raise",
                _ => $"{undercut} to reprice, {raise} to raise",
            });

        if (undercut + raise > 0)
        {
            ImGui.SameLine();

            if (Style.Commit(
                "reprice all",
                "Reprices every marked row above, one after another, through the game's own windows: "
                + "the ones somebody is under, the ones nobody is paying, and the ones with room to "
                + "raise. Ignored items are skipped."))
            {
                foreach (var (slot, index) in slots)
                {
                    if (!undercutting.Ignored(slot.ItemId) && undercutting.Plan(slot.ItemId, slot.UnitPrice, slot.IsHq) is { } plan)
                        sellFill.Reprice(index, slot.ItemId, plan.Target);
                }
            }
        }

        ImGui.SameLine();

        if (Style.Quiet("refresh", "Refetches the books these listings sit in. The floor from an hour ago is not a floor."))
        {
            foreach (var (slot, _) in slots)
                fetch(slot.ItemId, slot.UnitPrice);
        }
    }

    private void DrawUndercut(StoredSlot slot, int index, ref int undercut, ref int raise)
    {
        var plan = undercutting.Plan(slot.ItemId, slot.UnitPrice, slot.IsHq);
        var ignored = undercutting.Ignored(slot.ItemId);

        if (plan is not { } wanted)
        {
            Cell.Right(Style.Good, "-");
            return;
        }

        if (!ignored)
        {
            if (wanted.Why == UndercutWhy.RoomAbove)
                raise++;
            else
                undercut++;
        }

        ImGui.BeginDisabled(sellFill.Running is not null);

        var pressed = Style.Row(wanted.Why switch
        {
            UndercutWhy.NobodyPays => "reprice",
            UndercutWhy.RoomAbove => "raise",
            _ => "undercut",
        });

        if (pressed)
            sellFill.Reprice(index, slot.ItemId, wanted.Target);

        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Reprices this listing to {wanted.Target:N0} through the game's own price dialog"
                + (config.UndercutConfirms ? ".\n" : ", and leaves it for you to confirm.\n")
                + wanted.Why switch
                {
                    UndercutWhy.NobodyPays =>
                        $"Nobody is paying {slot.UnitPrice:N0}"
                        + (wanted.UnitsAhead > 0 ? $", nor the {wanted.UnitsAhead:N0} units listed under it" : ", cheapest on the board or not")
                        + $": what actually sells goes for about {wanted.Below:N0}, so that is what this sits under.",
                    UndercutWhy.RoomAbove =>
                        $"Nothing is under yours and the next listing sits at {wanted.Below:N0}: asking {wanted.Target:N0}\n"
                        + $"keeps you the cheapest and is {wanted.Target - slot.UnitPrice:N0} more a unit, and recent sales say\n"
                        + "somebody pays up there.",
                    _ => $"{wanted.UnitsAhead:N0} units sit in front, the cheapest at {wanted.Below:N0}.",
                }
                + (slot.IsHq && wanted.Why == UndercutWhy.NobodyPays
                    ? "\n\nHQ: recent sales are not split by quality, so what people pay may be for NQ."
                    : "")
                + (ignored ? "\n\nYou said to leave this one alone; the button still works, it just does not count." : ""));
        }

        ImGui.SameLine();
        Cell.Right(
            ignored ? Style.Muted : Style.Accent,
            wanted.Why switch
            {
                UndercutWhy.NobodyPays => $"sells ~{wanted.Below:N0}",
                UndercutWhy.RoomAbove => $"next {wanted.Below:N0}",
                _ => $"{wanted.Below:N0}",
            }
            + $" -> {wanted.Target:N0}" + (slot.IsHq && wanted.Why == UndercutWhy.NobodyPays ? " HQ?" : ""));
    }
}
