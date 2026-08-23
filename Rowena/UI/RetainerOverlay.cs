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
/// list's own order, and each row that is undercut has the button that opens the game's price
/// dialog on it with the undercut price filled in. The dialog's confirm is still the game's.
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
        if (sellFill.ListEdge() is { } edge)
            ImGui.SetNextWindowPos(new Vector2(edge.X, edge.Y));
    }

    public override void Draw()
    {
        if (sellFill.ActiveRetainer() is not { } retainer)
            return;

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

        if (!ImGui.BeginTable("overlay", 3, ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableSetupColumn("asking", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("cheapest ahead -> yours", ImGuiTableColumnFlags.WidthFixed, 250);
        ImGui.TableHeadersRow();

        foreach (var (slot, index) in slots)
        {
            ImGui.TableNextRow();
            ImGui.PushID(index);

            ImGui.TableNextColumn();
            cells.Draw(cells.Name(slot.ItemId), slot.ItemId);

            ImGui.TableNextColumn();
            Cell.Right(Palette.Dim, $"{slot.UnitPrice:N0}");

            ImGui.TableNextColumn();
            DrawUndercut(slot, index, ref undercut);

            ImGui.PopID();
        }

        ImGui.EndTable();

        DrawFooter(slots, undercut);
    }

    /// <summary>The tally, the run-everything button, and the run's progress while it goes.</summary>
    private void DrawFooter((StoredSlot Slot, int Index)[] slots, int undercut)
    {
        if (sellFill.Running is { } running)
        {
            ImGui.TextColored(Palette.Plain, $"Repricing, {running.Done} done, {running.Left} to go.");
            ImGui.SameLine();

            if (ImGui.SmallButton("stop"))
                sellFill.Stop();

            return;
        }

        ImGui.TextColored(
            undercut == 0 ? Palette.Good : Palette.Bad,
            undercut == 0 ? "Nothing is undercut." : $"{undercut} undercut.");

        if (undercut > 0)
        {
            ImGui.SameLine();

            if (ImGui.SmallButton("undercut all"))
            {
                foreach (var (slot, index) in slots)
                {
                    if (!undercutting.Ignored(slot.ItemId) && undercutting.Plan(slot.ItemId, slot.UnitPrice) is { } plan)
                        sellFill.Reprice(index, slot.ItemId, plan.Target);
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Reprices every undercut listing above, one after another, through the game's own\n"
                    + "windows. Ignored items are skipped. HQ listings are included: the board data does\n"
                    + "not tell qualities apart, so check those rows first if that worries you.");
            }
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("refresh"))
        {
            foreach (var (slot, _) in slots)
                fetch(slot.ItemId, slot.UnitPrice);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refetches the books these listings sit in. The floor from an hour ago is not a floor.");
    }

    private void DrawUndercut(StoredSlot slot, int index, ref int undercut)
    {
        var plan = undercutting.Plan(slot.ItemId, slot.UnitPrice);
        var ignored = undercutting.Ignored(slot.ItemId);

        if (plan is not { } wanted)
        {
            Cell.Right(Palette.Good, "-");
            return;
        }

        if (!ignored)
            undercut++;

        ImGui.BeginDisabled(sellFill.Running is not null);

        if (ImGui.SmallButton("undercut"))
            sellFill.Reprice(index, slot.ItemId, wanted.Target);

        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Reprices this listing to {wanted.Target:N0} through the game's own price dialog"
                + (config.UndercutConfirms ? ".\n" : ", and leaves it for you to confirm.\n")
                + $"{wanted.UnitsAhead:N0} units sit in front, the cheapest at {wanted.Below:N0}."
                + (slot.IsHq
                    ? "\n\nHQ: the board data does not tell qualities apart, so the listing in front may be NQ."
                    : "")
                + (ignored ? "\n\nYou said to leave this one alone; the button still works, it just does not count." : ""));
        }

        ImGui.SameLine();
        Cell.Right(
            ignored ? Palette.Dim : Palette.Bad,
            $"{wanted.Below:N0} -> {wanted.Target:N0}" + (slot.IsHq ? " HQ?" : ""));
    }
}
